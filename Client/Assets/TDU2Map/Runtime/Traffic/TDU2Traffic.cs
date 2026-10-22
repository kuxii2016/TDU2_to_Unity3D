using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using TDU2.Map.Online;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Traffic
{
    /// <summary>
    /// Verkehr wie im Spiel (tdu2-specs §8.2, §12.5):
    ///   * Straßennetz &lt;Insel&gt;_ROAD_MAP/_ROAD_DETAIL.IA (eigene Spuren, Rechtsverkehr, Tempoklassen),
    ///   * Autos = DBCarPhysicsData Marke 40, Modelle islands/&lt;insel&gt;/vehicules/traffic/*.bnk, Mischung und Dichte aus
    ///     level/aiconfig.cpr (AICONFIG VEHICLE_REPARTITION), Lackfarben TR_01..TR_11 aus vehicules/colors.bnk,
    ///   * Fahren: Spur folgen, Abstand halten (IDM), Kurventempo, Kreuzungen nacheinander befahren, Blinker/Bremslicht,
    ///   * Physik: nahe Autos sind echte Körper (anstoßen, wegschieben), bei einem Unfall fallen sie aus der KI (Warnblinker),
    ///     danach fädeln sie wieder ein.
    /// Mehrspieler: jedes Auto gehört genau einem Spieler (der es simuliert); die anderen sehen es interpoliert.
    ///   * Spawnen nur in der eigenen Zone (näher an mir als an jedem Mitspieler), Dichte zählt alle Autos.
    ///   * Entfernt sich der Besitzer, wird das Auto an den nächsten Spieler übergeben (Epoche + 1), sonst entfernt.
    ///   * Wer ein fremdes Auto rammt, übernimmt es (Unfallphysik beim Verursacher). Verschwindet ein Besitzer, übernimmt der Nächste.
    /// Paket (über TDU2Multiplayer/TDU2Server): [ver 1][Insel][u32 Zeit ms][u8 Anzahl] + je Auto 54 Byte (siehe WriteCar).
    /// </summary>
    public sealed class TDU2Traffic : MonoBehaviour
    {
        [Header("Verkehr")]
        public bool EnableTraffic = true;
        [Tooltip("Autos im Umkreis (GamePC.cpr: TrafficMaxCount 8, VehicleMaxCount 16)")]
        [Range(0, 40)] public int MaxCars = 12;
        [Tooltip("Dichte nach Uhrzeit (aiconfig.cpr DENSITY, Systemuhr)")]
        public bool DensityByTime = false;
        [Tooltip("Spawnabstand min/max (m) – GamePC.cpr SpawnDistances 200 400")]
        public float SpawnMin = 180f, SpawnMax = 400f;
        [Tooltip("Ab dieser Entfernung zu allen Spielern wird ein Auto entfernt (CullingConfig 500)")]
        public float DespawnDistance = 520f;
        [Tooltip("Tempo relativ zur Tempoklasse der Straße")]
        [Range(0.5f, 1.3f)] public float SpeedFactor = 0.92f;

        [Header("Physik / Darstellung")]
        [Tooltip("Autos in diesem Umkreis sind echte Physikkörper (CullingConfig 100)")]
        public float PhysicsRadius = 100f;
        [Tooltip("Höchstzahl Physik-Autos (PhysicsMaxCount 4)")]
        public int PhysicsMax = 6;
        [Tooltip("Aufprall (m/s Relativgeschwindigkeit), ab dem ein Auto aus der KI fällt")]
        public float CrashSpeed = 3.5f;
        public bool Sounds = true;
        [Tooltip("So viele der nächsten Autos haben Motorsound")]
        public int SoundMax = 3;
        public float SoundRadius = 70f;

        [Header("Mehrspieler")]
        public bool Sync = true;
        [Tooltip("Pakete pro Sekunde")]
        public float SendRate = 8f;
        [Tooltip("Eigene Autos nur senden, wenn ein Mitspieler so nah ist (m)")]
        public float RelevantDistance = 900f;

        [Header("Debug")]
        public bool ShowDebug;

        public TDU2VehicleManager Manager;

        // ------------------------------------------------------------------ Daten
        sealed class TypeInfo
        {
            public int DbId; public CarPhysics Car; public string Name, Bank, Sound; public float Weight;
            public Task<CarAssets> Load; public CarAssets Assets; public bool Failed;
        }

        struct Snap { public double T; public Vector3 P, F, U, V; public float Steer; public byte Flags; }

        sealed class Car
        {
            public uint Id; public int Owner; public byte Epoch; public bool Mine;
            public TypeInfo Type; public int Color;
            // Simulation (nur Besitzer)
            public LaneTrack Lane, Next; public JunctionTrack Junction; public int Phase;   // 0 Spur, 1 Kreuzung
            public float S, V, Accel, SpeedFactor = 1f, WaitT, StuckT;
            public int Mode;            // 0 fährt, 1 Unfall (Physik), 2 steht (nach Unfall / Sackgasse)
            public float ModeT, RestT;
            public bool DeadEnd;
            // Lage (Inselkoordinaten, Ursprung des Autos)
            public Vector3 Pos, Fwd = Vector3.forward, Up = Vector3.up, Vel;
            public float Steer, GroundDy; public Vector3 GroundUp = Vector3.up; public int GroundFrame;
            public Vector3 VisOffset;   // weiche Korrektur nach Übernahme/Wiedereinfädeln
            public bool Brake, IndL, IndR, Hazard;
            // Darstellung
            public GameObject Go; public CarModel Model; public Rigidbody Rb; public BoxCollider Box;
            public TDU2VehicleLights Lights; public TDU2VehicleAudio Audio;
            public (Transform fork, Quaternion fBase, float steerSign, Transform disc, Quaternion dBase, float spinSign, bool front)[] Wheels;
            public float Radius = 0.33f, RideOffset = 0.3f, HalfLen = 2.2f, HalfWidth = 0.9f, Spin;
            public bool Physical;
            // Fernsteuerung
            public readonly List<Snap> Snaps = new List<Snap>();
            public float LastPacket; public int Sender;
            public bool HasVis; public Vector3 VisPos, VisVel; public Quaternion VisRot = Quaternion.identity;
            public int HandoffSends, RemoveSends; public bool Removed;
            // Pfad-Info des letzten Pakets (für die Übernahme)
            public int NetRoad, NetLane, NetNextRoad, NetNextLane, NetMode; public bool NetFwd, NetNextFwd; public float NetS;

            public Vector3 WorldPosVisual;
        }

        sealed class SenderClock { public readonly Queue<double> Lag = new Queue<double>(); public double Offset, Jitter; }

        readonly Dictionary<uint, Car> _cars = new Dictionary<uint, Car>();
        readonly Dictionary<int, TypeInfo> _types = new Dictionary<int, TypeInfo>();
        readonly List<TypeInfo> _spawnable = new List<TypeInfo>();
        readonly List<Color> _paints = new List<Color>();
        readonly Dictionary<int, SenderClock> _clocks = new Dictionary<int, SenderClock>();
        float[] _density = new float[13];
        TrafficLanes _lanes;
        Task<LoadResult> _load;
        string _loadedIsland;
        byte _islandCode;
        Transform _container;
        readonly System.Random _rng = new System.Random();
        ushort _idSalt;
        uint _idCounter;
        float _nextPopulation, _nextSend, _fillUntil;
        int _myPid = -1;
        TDU2Multiplayer _mp;
        string _status = "";
        static TDU2GameData GD => TDU2GameData.Instance;

        sealed class LoadResult { public TrafficLanes Lanes; public List<TypeInfo> Types; public float[] Density; public List<Color> Paints; public string Log; }

        public string Status => _status;
        public int CarCount => _cars.Count;
        public int OwnedCount { get { int n = 0; foreach (var c in _cars.Values) if (c.Mine) n++; return n; } }
        public TrafficLanes Lanes => _lanes;

        void Awake()
        {
            _idSalt = (ushort)UnityEngine.Random.Range(1, 65535);
        }

        // ------------------------------------------------------------------ Laden
        void EnsureLoaded()
        {
            var st = Manager != null ? Manager.Streamer : null;
            if (st == null || st.Index == null || Manager.Database == null) { _status = "warte auf Karte/Datenbank"; return; }
            string island = st.Index.IslandName;
            if (_loadedIsland == island && (_load != null || _lanes != null)) return;
            ClearAll();
            _loadedIsland = island;
            _islandCode = (byte)(island.ToLowerInvariant().StartsWith("haw") ? 2 : island.ToLowerInvariant().StartsWith("cas") ? 3 : 1);
            var idx = st.Index; var db = Manager.Database; string bnk = Manager.BnkDir;
            _status = "loading trafficnet …";
            _load = Task.Run(() => LoadData(idx, db, bnk));
        }

        static LoadResult LoadData(World.IslandIndex idx, CarDatabase db, string bnkDir)
        {
            var res = new LoadResult { Types = new List<TypeInfo>(), Paints = new List<Color>(), Density = new float[13] };
            var net = IaNetwork.LoadFromBank(idx.IslandBank);
            res.Lanes = new TrafficLanes(net);

            var log = net.ToString();

            // Mischung aus aiconfig.cpr
            var weights = new Dictionary<int, float>();
            int police = -1;
            try
            {
                var cfg = CarDatabase.FindFile(idx.LevelDir, "aiconfig.cpr");
                if (cfg != null)
                {
                    var plain = CprCrypto.Decrypt(File.ReadAllBytes(cfg));
                    string xml = Bin.Latin1(plain, 0, plain.Length);
                    var pm = Regex.Match(xml, @"<POLICE_CAR_ID>\s*(\d+)\s*</POLICE_CAR_ID>");
                    if (pm.Success) police = int.Parse(pm.Groups[1].Value);
                    int zones = 0;
                    foreach (Match z in Regex.Matches(xml, @"<ZONE>(.*?)</ZONE>", RegexOptions.Singleline))
                    {
                        var dens = Regex.Matches(z.Groups[1].Value, @"<NB_TRAFFIC\s+hour=""([\d.]+)""\s+frequency=""([\d.]+)""");
                        float sum = 0f;
                        var d = new float[13];
                        for (int i = 0; i < dens.Count && i < 13; i++) { d[i] = ParseF(dens[i].Groups[2].Value); sum += d[i]; }
                        if (sum <= 0f) continue;
                        zones++;
                        for (int i = 0; i < 13; i++) res.Density[i] += d[i];
                        foreach (Match v in Regex.Matches(z.Groups[1].Value, @"<VEHICLE\s+database_id=""(\d+)""\s+frequency_day=""([\d.]+)""\s+frequency_night=""([\d.]+)"""))
                        {
                            int id = int.Parse(v.Groups[1].Value);
                            weights.TryGetValue(id, out var w);
                            weights[id] = w + ParseF(v.Groups[2].Value);
                        }
                    }
                    if (zones > 0) for (int i = 0; i < 13; i++) res.Density[i] /= zones;
                    log += $", aiconfig: {zones} Zonen, {weights.Count} Autos";
                }
            }
            catch (Exception ex) { log += ", aiconfig: " + ex.Message; }

            // Modelle
            string trafficDir = Path.Combine(idx.IslandDir, "vehicules", "traffic");
            string soundDir = Path.Combine(bnkDir, "sound", "vehicles");
            string generic = CarDatabase.FindFile(Path.Combine(bnkDir, "vehicules", "med"), "generic_traffic_low.bnk")
                             ?? CarDatabase.FindFile(soundDir, "generic_high_high.bnk");
            foreach (var c in db.Traffic)
            {
                string model = c.Raw != null ? c.Raw.S("_car_model", "") : "";
                string version = c.Raw != null ? c.Raw.S("_car_version", "") : "";
                string name = (model + version).ToLowerInvariant();
                if (name.Length == 0) continue;
                var bank = CarDatabase.FindFile(trafficDir, name + ".bnk");
                if (bank == null) continue;
                float w = weights.Count == 0 ? 1f : (weights.TryGetValue(c.Id, out var ww) ? ww : 0f);
                if (c.Id == police) w *= 0.15f;
                var snd = CarDatabase.FindFile(soundDir, name + "_high.bnk") ?? generic;
                res.Types.Add(new TypeInfo { DbId = c.Id, Car = c, Name = name, Bank = bank, Sound = snd, Weight = w });
            }
            log += $", {res.Types.Count} Modelle";

            // Lackfarben TR_01..TR_11
            try
            {
                var cb = CarDatabase.FindFile(Path.Combine(bnkDir, "vehicules"), "colors.bnk");
                if (cb != null)
                {
                    var bank = BnkArchive.Open(cb);
                    var e = bank.FindByExt(".2dm");
                    if (e != null)
                    {
                        var lib = Material2DM.Parse(bank.Read(e));
                        for (int i = 1; i <= 11; i++)
                            if (lib.Materials.TryGetValue(MaterialDef.KeyFromName($"TR_{i:00}"), out var m) && m.TryGetParam("RAMPCLR1", out var c1))
                                res.Paints.Add(new Color(c1.x, c1.y, c1.z, 1f));
                    }
                }
            }
            catch (Exception ex) { log += ", colors: " + ex.Message; }
            res.Log = log;
            return res;
        }

        static float ParseF(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;

        void PollLoad()
        {
            if (_load == null || !_load.IsCompleted) return;
            var t = _load; _load = null;
            if (t.IsFaulted)
            {
                _status = "Fehler: " + t.Exception?.GetBaseException().Message;
                Debug.LogWarning("[TDU2] Verkehr: " + t.Exception?.GetBaseException());
                return;
            }
            var r = t.Result;
            _lanes = r.Lanes;
            _density = r.Density;
            _paints.Clear(); _paints.AddRange(r.Paints);
            _types.Clear(); _spawnable.Clear();
            foreach (var ti in r.Types) { _types[ti.DbId] = ti; if (ti.Weight > 0f) _spawnable.Add(ti); }
            if (_spawnable.Count == 0) _spawnable.AddRange(r.Types);
            // alle Modelle im Hintergrund vorladen (je ~0,5 MB)
            foreach (var ti in _spawnable) StartTypeLoad(ti);
            _fillUntil = Time.unscaledTime + 4f;
            _status = r.Log;
            Debug.Log("[TDU2] Verkehr: " + r.Log);
        }

        void StartTypeLoad(TypeInfo ti)
        {
            if (ti.Load != null || ti.Assets != null || ti.Failed) return;
            bool snd = Sounds;
            ti.Load = Task.Run(() => CarAssets.LoadTraffic(ti.Bank, snd ? ti.Sound : null, ti.Car));
        }

        void PollTypes()
        {
            foreach (var ti in _types.Values)
            {
                if (ti.Load == null || !ti.Load.IsCompleted) continue;
                var t = ti.Load; ti.Load = null;
                if (t.IsFaulted) { ti.Failed = true; Debug.LogWarning("[TDU2] Verkehrsauto " + ti.Name + ": " + t.Exception?.GetBaseException().Message); continue; }
                ti.Assets = t.Result;
            }
        }

        void ClearAll()
        {
            foreach (var c in _cars.Values) DestroyVisual(c);
            _cars.Clear();
            foreach (var ti in _types.Values) ti.Assets?.DestroyTextures();
            _types.Clear(); _spawnable.Clear();
            _lanes = null;
        }

        void OnDestroy() { ClearAll(); if (_container != null) UnityUtil.SafeDestroy(_container.gameObject); }

        // ------------------------------------------------------------------ Hauptschleife
        void Update()
        {
            if (Manager == null) { var m = GetComponent<TDU2VehicleManager>(); if (m == null) m = FindManager(); Manager = m; }
            if (Manager == null) return;
            if (!EnableTraffic)
            {
                if (_cars.Count > 0) { foreach (var c in _cars.Values) DestroyVisual(c); _cars.Clear(); }
                return;
            }
            EnsureLoaded();
            PollLoad();
            PollTypes();
            var root = Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
            if (_lanes == null || root == null) return;
            if (_container == null) _container = new GameObject("TDU2 Verkehr").transform;
            if (_mp == null) _mp = Manager.GetComponent<TDU2Multiplayer>();

            // Spielernummer gewechselt (An-/Abmelden): eigene Autos umschreiben
            int pid = TDU2Session.LoggedIn ? TDU2Session.PlayerId : 0;
            if (pid != _myPid)
            {
                _myPid = pid;
                foreach (var c in _cars.Values) if (c.Mine) { c.Owner = pid; c.Epoch++; }
            }

            ReceiveNet();
            if (Time.unscaledTime >= _nextPopulation)
            {
                _nextPopulation = Time.unscaledTime + 0.25f;
                Population(root);
            }
            BuildVisuals();
            UpdateRemoteCars(root);
            UpdateVisuals(root);
            SendNet();
        }

        static TDU2VehicleManager FindManager()
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<TDU2VehicleManager>();
#else
            return FindObjectOfType<TDU2VehicleManager>();
#endif
        }

        // ------------------------------------------------------------------ Positionen der Spieler (Inselkoordinaten)
        readonly List<(int pid, Vector3 pos)> _players = new List<(int, Vector3)>();
        Vector3 _me; bool _hasMe;

        void CollectPlayers(Transform root)
        {
            _hasMe = false;
            var cur = Manager.Current;
            if (cur != null) { _me = root.InverseTransformPoint(cur.transform.position); _hasMe = true; }
            else if (Camera.main != null && !TDU2BigMap.IsOpen) { _me = root.InverseTransformPoint(Camera.main.transform.position); _hasMe = true; }
            _players.Clear();
            var gd = GD;
            if (gd == null || !TDU2Session.LoggedIn) return;
            float timeout = gd.Timeout + 1f;
            foreach (var p in gd.Players.Values)
            {
                if (p.Id == _myPid || !p.HasPos || !p.InCar) continue;
                if (Time.unscaledTime - p.LastUpdate > timeout) continue;
                _players.Add((p.Id, p.GamePos));
            }
        }

        bool PlayerActive(int pid)
        {
            if (pid == _myPid) return true;
            foreach (var p in _players) if (p.pid == pid) return true;
            return false;
        }

        static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        // ------------------------------------------------------------------ Bevölkerung: Spawnen, Entfernen, Übergeben, Übernehmen
        readonly List<(int, int)> _cells = new List<(int, int)>();
        readonly List<uint> _remove = new List<uint>();

        void Population(Transform root)
        {
            CollectPlayers(root);
            _remove.Clear();
            float now = Time.unscaledTime;

            foreach (var c in _cars.Values)
            {
                if (c.Mine)
                {
                    if (c.Removed) { if (c.RemoveSends <= 0) _remove.Add(c.Id); continue; }
                    float dMe = _hasMe ? Flat(c.Pos, _me) : 0f;
                    bool parkedLong = c.Mode == 2 && c.ModeT > 60f && dMe > 120f && !Visible(root, c.Pos);
                    bool deadEnd = c.DeadEnd && c.V < 0.2f && !Visible(root, c.Pos) && dMe > 60f;
                    bool stuck = c.StuckT > 45f && !Visible(root, c.Pos) && dMe > 60f;
                    if (dMe > DespawnDistance || parkedLong || deadEnd || stuck)
                    {
                        // nächster Mitspieler in Reichweite übernimmt
                        int best = -1; float bd = DespawnDistance;
                        if (!parkedLong && !deadEnd && !stuck)
                            foreach (var p in _players) { float d = Flat(c.Pos, p.pos); if (d < bd) { bd = d; best = p.pid; } }
                        if (best > 0) Handoff(c, best);
                        else Remove(c);
                    }
                }
                else
                {
                    if (c.HandoffSends > 0) continue;
                    // Besitzer weg oder keine Pakete mehr
                    if (now - c.LastPacket > 2.5f)
                    {
                        if (!PlayerActive(c.Owner) && IAmNearest(c.Pos)) Claim(c);
                        else _remove.Add(c.Id);
                    }
                }
            }
            foreach (var id in _remove) { if (_cars.TryGetValue(id, out var c)) { DestroyVisual(c); _cars.Remove(id); } }

            // Spawnen
            // neue Autos nur, wenn ich fahre (nicht im Login, Interieur oder Freiflug)
            if (!_hasMe || Manager.Suspended || Manager.Current == null || _spawnable.Count == 0) return;
            int target = MaxCars;
            if (DensityByTime && _density != null)
            {
                float h = DateTime.Now.Hour + DateTime.Now.Minute / 60f;
                int i = Mathf.Clamp(Mathf.FloorToInt(h / 2f), 0, 11);
                float d = Mathf.Lerp(_density[i], _density[i + 1], (h - i * 2f) / 2f);
                float max = 0f; foreach (var x in _density) max = Mathf.Max(max, x);
                if (max > 0f) target = Mathf.RoundToInt(MaxCars * d / max);
            }
            int count = 0;
            foreach (var c in _cars.Values) if (!c.Removed && Flat(c.Pos, _me) < SpawnMax + 30f) count++;
            bool fill = now < _fillUntil;
            int attempts = fill ? 24 : 6;
            float rMin = fill ? 60f : SpawnMin;
            _lanes.CellsInRing(_me, rMin, SpawnMax, _cells);
            if (_cells.Count == 0) return;
            for (int a = 0; a < attempts && count < target; a++)
                if (TrySpawn(root, rMin, fill)) count++;
        }

        bool IAmNearest(Vector3 p)
        {
            if (!_hasMe) return false;
            float d = Flat(p, _me);
            foreach (var o in _players) if (o.pid != _myPid && Flat(p, o.pos) < d) return false;
            return true;
        }

        bool Visible(Transform root, Vector3 island)
        {
            var cam = Camera.main;
            if (cam == null) return false;
            var vp = cam.WorldToViewportPoint(root.TransformPoint(island) + Vector3.up);
            return vp.z > 0f && vp.z < 600f && vp.x > -0.15f && vp.x < 1.15f && vp.y > -0.2f && vp.y < 1.2f;
        }

        bool TrySpawn(Transform root, float rMin, bool fill)
        {
            var cell = _cells[_rng.Next(_cells.Count)];
            var roads = _lanes.RoadsInCell(cell);
            if (roads == null || roads.Count == 0) return false;
            int rid = roads[_rng.Next(roads.Count)];
            var road = _lanes.Net.Roads[rid];
            bool fwd = _rng.NextDouble() < 0.5;
            if (_lanes.LaneCount(road, fwd) == 0) fwd = !fwd;
            int cnt = _lanes.LaneCount(road, fwd);
            if (cnt == 0) return false;
            var lane = _lanes.Lane(rid, fwd, _rng.Next(cnt));
            if (lane == null || lane.Length < 20f) return false;
            float s = (float)(_rng.NextDouble() * (lane.Length - 16f) + 8f);
            lane.Sample(s, out var p, out var t);
            float d = Flat(p, _me);
            if (d < rMin || d > SpawnMax) return false;
            // eigene Zone: näher an mir als an jedem Mitspieler, und nicht vor deren Augen
            foreach (var o in _players) { float od = Flat(p, o.pos); if (od < d || od < 160f) return false; }
            if (!fill || d < 120f) { if (d < 320f && Visible(root, p)) return false; }
            // Abstand zu anderen Autos
            foreach (var c in _cars.Values) if (!c.Removed && (c.Pos - p).sqrMagnitude < 28f * 28f) return false;
            var ti = PickType();
            if (ti == null) return false;
            var car = new Car
            {
                Id = ((uint)_idSalt << 16) | (++_idCounter & 0xFFFF),
                Owner = _myPid,
                Epoch = 0,
                Mine = true,
                Type = ti,
                Color = _paints.Count > 0 ? _rng.Next(_paints.Count) : 0,
                Lane = lane,
                S = s,
                SpeedFactor = SpeedFactor * (0.85f + 0.2f * (float)_rng.NextDouble()),
            };
            car.V = lane.LimitAt(s) * car.SpeedFactor * 0.85f;
            car.Pos = p + Vector3.up * car.RideOffset; car.Fwd = t;
            ChooseNext(car);
            _cars[car.Id] = car;
            return true;
        }

        TypeInfo PickType()
        {
            float sum = 0f;
            foreach (var t in _spawnable) if (!t.Failed) sum += Mathf.Max(0.001f, t.Weight);
            if (sum <= 0f) return null;
            float r = (float)_rng.NextDouble() * sum;
            foreach (var t in _spawnable)
            {
                if (t.Failed) continue;
                r -= Mathf.Max(0.001f, t.Weight);
                if (r <= 0f) return t;
            }
            return _spawnable[_spawnable.Count - 1];
        }

        void Remove(Car c)
        {
            c.Removed = true;
            c.RemoveSends = Sync && _mp != null && _mp.CanSend && _players.Count > 0 ? 3 : 0;
            if (c.Go != null) { DestroyVisual(c); }
        }

        void Handoff(Car c, int pid)
        {
            c.Owner = pid; c.Epoch++; c.Mine = false; c.HandoffSends = 4;
            c.Snaps.Clear();
            c.LastPacket = Time.unscaledTime;
            SeedSnap(c);
            SetPhysical(c, false);
        }

        /// <summary>Übernehmen (Besitzer weg oder ich habe es gerammt): Simulation aus dem letzten Zustand starten.</summary>
        void Claim(Car c)
        {
            c.Owner = _myPid; c.Epoch++; c.Mine = true; c.HandoffSends = 0;
            InitSimFromNet(c);
        }

        void InitSimFromNet(Car c)
        {
            var oldVis = c.HasVis ? c.VisPos : c.Pos;
            if (c.Snaps.Count > 0) { var s = c.Snaps[c.Snaps.Count - 1]; c.Pos = s.P; c.Fwd = s.F; c.Up = s.U; c.Vel = s.V; }
            c.Mine = true;
            c.Phase = 0; c.Junction = null; c.Next = null; c.Lane = null;
            if (c.NetMode == 0 || c.NetMode == 1)
            {
                var lane = _lanes.Lane(c.NetRoad, c.NetFwd, c.NetLane);
                if (lane != null)
                {
                    c.Lane = lane; c.S = c.NetS; c.Mode = 0;
                    if (c.NetMode == 1)
                    {
                        var next = _lanes.Lane(c.NetNextRoad, c.NetNextFwd, c.NetNextLane);
                        var j = _lanes.Junction(lane, next);
                        if (j != null) { c.Next = next; c.Junction = j; c.Phase = 1; }
                        else c.S = lane.Length;
                    }
                    if (c.Phase == 0) ChooseNext(c);
                    c.V = new Vector3(c.Vel.x, 0f, c.Vel.z).magnitude;
                    // ein paar Zehntel weiterrechnen (Paketalter), Rest gleicht VisOffset aus
                }
                else c.Mode = 2;
            }
            else c.Mode = 2;
            if (c.Mode == 2) { c.ModeT = 0f; c.V = 0f; }
            c.Snaps.Clear();
            ComputePose(c, 0f);
            c.VisOffset = oldVis - c.Pos;
            if (c.VisOffset.sqrMagnitude > 15f * 15f) c.VisOffset = Vector3.zero;
        }

        // ------------------------------------------------------------------ KI
        readonly List<(int road, bool forward)> _succ = new List<(int, bool)>();

        void ChooseNext(Car c)
        {
            c.Next = null; c.Junction = null; c.DeadEnd = false;
            if (c.Lane == null) return;
            _lanes.Successors(c.Lane, _succ);
            if (_succ.Count == 0) { c.DeadEnd = true; return; }
            // geradeaus bevorzugt
            float sum = 0f;
            var w = new float[_succ.Count];
            for (int i = 0; i < _succ.Count; i++)
            {
                var nl = _lanes.NextLane(c.Lane, _succ[i].road, _succ[i].forward);
                if (nl == null) continue;
                float dot = Vector3.Dot(new Vector3(c.Lane.EndDir.x, 0, c.Lane.EndDir.z).normalized, new Vector3(nl.StartDir.x, 0, nl.StartDir.z).normalized);
                w[i] = dot > 0.85f ? 3f : dot > -0.2f ? 1f : 0.25f;
                if (_succ[i].road == c.Lane.RoadId) w[i] = 0.05f;   // wenden nur notfalls
                sum += w[i];
            }
            if (sum <= 0f) { c.DeadEnd = true; return; }
            float r = (float)_rng.NextDouble() * sum;
            int pick = 0;
            for (int i = 0; i < w.Length; i++) { r -= w[i]; if (r <= 0f) { pick = i; break; } }
            var next = _lanes.NextLane(c.Lane, _succ[pick].road, _succ[pick].forward);
            c.Next = next;
            c.Junction = _lanes.Junction(c.Lane, next);
        }

        TrafficTrack Cur(Car c) => c.Phase == 1 && c.Junction != null ? (TrafficTrack)c.Junction : c.Lane;

        /// <summary>Punkt auf dem geplanten Weg <paramref name="ahead"/> Meter voraus.</summary>
        Vector3 PointAhead(Car c, float ahead)
        {
            float s = c.S + ahead;
            if (c.Phase == 0)
            {
                if (s <= c.Lane.Length || c.Junction == null) { c.Lane.Sample(s, out var p, out _); return p; }
                s -= c.Lane.Length;
                if (s <= c.Junction.Length || c.Next == null) { c.Junction.Sample(s, out var p, out _); return p; }
                s -= c.Junction.Length;
                c.Next.Sample(s, out var q, out _); return q;
            }
            if (s <= c.Junction.Length || c.Next == null) { c.Junction.Sample(s, out var p, out _); return p; }
            c.Next.Sample(s - c.Junction.Length, out var r, out _); return r;
        }

        struct Obstacle { public Vector3 P, V, F; public float HalfLen; public Car Car; public bool Player; }
        readonly List<Obstacle> _obs = new List<Obstacle>();
        readonly List<(int id, Vector3 pos, Vector3 vel, Vector3 fwd, float halfLength)> _remoteVeh = new List<(int, Vector3, Vector3, Vector3, float)>();

        void CollectObstacles(Transform root)
        {
            _obs.Clear();
            foreach (var c in _cars.Values)
            {
                if (c.Removed) continue;
                if (c.Mine) _obs.Add(new Obstacle { P = c.Pos, V = c.Vel, F = c.Fwd, HalfLen = c.HalfLen, Car = c });
                else if (c.HasVis) _obs.Add(new Obstacle { P = c.VisPos, V = c.VisVel, F = c.Fwd, HalfLen = c.HalfLen, Car = c });
            }
            var cur = Manager.Current;
            if (cur != null)
            {
                var rb = cur.GetComponent<Rigidbody>();
                _obs.Add(new Obstacle
                {
                    P = root.InverseTransformPoint(cur.transform.position),
                    V = rb != null ? root.InverseTransformVector(GetVel(rb)) : Vector3.zero,
                    F = root.InverseTransformVector(cur.transform.forward).normalized,
                    HalfLen = 2.3f,
                    Player = true,
                });
            }
            if (_mp != null)
            {
                _mp.RemoteVehicles(_remoteVeh);
                foreach (var r in _remoteVeh) _obs.Add(new Obstacle { P = r.pos, V = r.vel, F = r.fwd, HalfLen = r.halfLength, Player = true });
            }
        }

        bool NeedYield(Car c, float dEnd)
        {
            int nodeId = c.Lane.ToNode;
            if (!_lanes.Net.Nodes.TryGetValue(nodeId, out var node) || node.Roads.Length < 3) return false;
            _lanes.NodeRadius.TryGetValue(nodeId, out var rad);
            float R = Mathf.Max(8f, rad + 3f);
            var center = node.Pos;
            foreach (var o in _obs)
            {
                if (o.Car == c) continue;
                float dc = Flat(o.P, center);
                if (dc > R + 8f) continue;
                var oc = o.Car;
                if (oc != null && oc.Mine)
                {
                    if (oc.Phase == 1 && oc.Junction != null && oc.Junction.Node == nodeId && oc.Junction.From != c.Lane) return true;
                    if (oc.Phase == 0 && oc.Lane != null && oc.Lane.ToNode == nodeId && oc.Lane != c.Lane && oc.Junction != null)
                    {
                        float od = oc.Lane.Length - oc.S;
                        if (od < 12f && (od < dEnd - 0.01f || (Mathf.Abs(od - dEnd) <= 0.01f && oc.Id < c.Id))) return true;
                    }
                    continue;
                }
                // fremde Verkehrsautos und Spieler: wer sich in der Kreuzung bewegt (quer zu mir) oder darin steht
                float sp = new Vector3(o.V.x, 0f, o.V.z).magnitude;
                if (dc < R && sp > 0.5f && Vector3.Dot(o.F, c.Fwd) < 0.8f) return true;
                if (o.Player && dc < R * 0.8f && sp <= 0.5f) return true;
            }
            return false;
        }

        void Step(Car c, float dt)
        {
            var cur = Cur(c);
            if (cur == null) { c.Mode = 2; return; }
            float v = c.V;
            var fwdH = new Vector3(c.Fwd.x, 0f, c.Fwd.z).normalized;

            // Hindernis voraus
            float look = Mathf.Max(30f, v * 3.5f + 12f);
            float gap = float.MaxValue, leadV = 0f;
            foreach (var o in _obs)
            {
                if (o.Car == c) continue;
                var d = o.P - c.Pos; d.y = 0f;
                if (d.sqrMagnitude > (look + 10f) * (look + 10f)) continue;
                float f = Vector3.Dot(d, fwdH);
                if (f < 0.5f || f > look) continue;
                var p = PointAhead(c, f);
                float lat = Flat(o.P, p);
                float ov = Vector3.Dot(o.V, fwdH);
                if (lat > 2.0f || (ov < -2f && lat > 1.2f)) continue;
                float g = f - c.HalfLen - o.HalfLen;
                if (g < gap) { gap = g; leadV = ov; }
            }

            // Ampel voraus (Rot / Gelb mit genug Bremsweg): vor der Ampel anhalten
            if (TDU2.Map.World.TDU2TrafficLights.MustStop(c.Pos, fwdH, look, v, out float dLight))
            {
                float g = dLight - c.HalfLen - 2.5f;
                if (g < gap) { gap = g; leadV = 0f; }
            }

            float dEnd = c.Phase == 0 ? c.Lane.Length - c.S : 0f;
            bool waiting = false;
            if (c.Phase == 0)
            {
                if (c.Junction == null)
                {
                    float g = dEnd - 2f;   // Sackgasse
                    if (g < gap) { gap = g; leadV = 0f; }
                }
                else if (dEnd < 35f && c.WaitT < 7f && NeedYield(c, dEnd))
                {
                    float g = dEnd - 1.5f;
                    if (g < gap) { gap = g; leadV = 0f; }
                    waiting = dEnd < 6f;
                }
            }
            if (waiting) c.WaitT += dt; else if (c.Phase == 1 || dEnd > 12f) c.WaitT = 0f;

            // Wunschtempo: Tempoprofil + rechtzeitig für die Kreuzung bremsen
            float vDes = cur.LimitAt(c.S);
            if (c.Phase == 0 && c.Junction != null)
            {
                float vj = c.Junction.MinLimit(0f, c.Junction.Length);
                vDes = Mathf.Min(vDes, Mathf.Sqrt(vj * vj + 2f * TrafficTrack.Brake * Mathf.Max(0f, dEnd)));
            }
            else if (c.Phase == 1 && c.Next != null)
            {
                float vn = c.Next.LimitAt(0f);
                vDes = Mathf.Min(vDes, Mathf.Sqrt(vn * vn + 2f * TrafficTrack.Brake * Mathf.Max(0f, c.Junction.Length - c.S)));
            }
            vDes *= c.SpeedFactor;

            // IDM
            const float aMax = 1.6f, b = 2.4f, s0 = 3.5f, T = 1.4f;
            float acc = aMax * (1f - Mathf.Pow(v / Mathf.Max(0.5f, vDes), 4f));
            if (gap < float.MaxValue)
            {
                float sStar = s0 + Mathf.Max(0f, v * T + v * (v - leadV) / (2f * Mathf.Sqrt(aMax * b)));
                acc -= aMax * (sStar / Mathf.Max(0.2f, gap)) * (sStar / Mathf.Max(0.2f, gap));
            }
            acc = Mathf.Clamp(acc, -9f, aMax);
            v = Mathf.Max(0f, v + acc * dt);
            if (gap < 0.5f) v = Mathf.Min(v, Mathf.Max(0f, leadV));
            c.Accel = acc;
            c.V = v;
            c.Brake = acc < -1.2f || (v < 0.3f && vDes > 1f);
            c.StuckT = v < 0.1f ? c.StuckT + dt : 0f;

            // weiterfahren
            c.S += v * dt;
            for (int guard = 0; guard < 4; guard++)
            {
                if (c.Phase == 0)
                {
                    if (c.S <= c.Lane.Length) break;
                    if (c.Junction == null) { c.S = c.Lane.Length; c.V = 0f; break; }
                    c.S -= c.Lane.Length; c.Phase = 1;
                }
                else
                {
                    if (c.S <= c.Junction.Length) break;
                    c.S -= c.Junction.Length;
                    c.Lane = c.Next; c.Phase = 0;
                    if (c.Lane == null) { c.Mode = 2; break; }
                    ChooseNext(c);
                }
            }

            // Blinker
            int turn = 0;
            if (c.Phase == 1 && c.Junction != null) turn = c.Junction.Turn;
            else if (c.Phase == 0 && c.Junction != null && c.Lane.Length - c.S < 45f) turn = c.Junction.Turn;
            c.IndL = turn < 0; c.IndR = turn > 0;
            c.Hazard = false;
        }

        void ComputePose(Car c, float dt)
        {
            var cur = Cur(c);
            if (cur == null) return;
            cur.Sample(c.S, out var p, out var t);
            if (t.sqrMagnitude < 1e-6f) t = c.Fwd;
            c.Fwd = t.normalized;
            c.Up = Vector3.Slerp(c.Up, c.GroundUp, dt > 0f ? 1f - Mathf.Exp(-dt * 6f) : 1f);
            // Nur Steigung neigen, nicht seitlich kippen (Straßenquerneigung): Roll-Anteil entfernen.
            var rightH = Vector3.Cross(Vector3.up, c.Fwd);
            if (rightH.sqrMagnitude > 1e-6f)
            {
                rightH.Normalize();
                var up = c.Up - rightH * Vector3.Dot(c.Up, rightH);
                if (up.sqrMagnitude > 1e-6f) c.Up = up.normalized;
                else c.Up = Vector3.up;
            }
            var newPos = p + Vector3.up * (c.RideOffset + c.GroundDy);
            c.Vel = dt > 0f ? c.Fwd * c.V : c.Fwd * c.V;
            c.Pos = newPos;
            // Lenkwinkel aus der Krümmung (Radstand ~2,7 m)
            var ahead = PointAhead(c, 2.7f);
            var dir = ahead - p; dir.y = 0f;
            // Inselkoordinaten (x Ost, z Süd): + = Rechtskurve; Lenkwinkel ≈ Richtungsänderung über den Radstand
            float ang = Vector2.SignedAngle(new Vector2(c.Fwd.x, c.Fwd.z), new Vector2(dir.x, dir.z));
            c.Steer = Mathf.Lerp(c.Steer, Mathf.Clamp(ang, -35f, 35f), dt > 0f ? 1f - Mathf.Exp(-dt * 8f) : 1f);
        }

        // ------------------------------------------------------------------ Physikschritt
        void FixedUpdate()
        {
            if (_lanes == null || Manager == null || !EnableTraffic) return;
            var root = Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
            if (root == null) return;
            float dt = Time.fixedDeltaTime;
            CollectObstacles(root);
            bool groundFrame = (Time.frameCount & 3) == 0;
            foreach (var c in _cars.Values)
            {
                if (!c.Mine || c.Removed) continue;
                c.ModeT += dt;
                if (c.Mode == 0)
                {
                    Step(c, dt);
                    if (c.Mode != 0) continue;
                    if (groundFrame) GroundProbe(root, c);
                    ComputePose(c, dt);
                    c.VisOffset *= Mathf.Exp(-dt * 3f);
                    if (c.Go != null) DriveBody(root, c, dt);
                }
                else if (c.Mode == 1)
                {
                    // Unfall: Physik bestimmt die Lage
                    if (c.Rb != null)
                    {
                        c.Pos = root.InverseTransformPoint(c.Rb.position);
                        c.Fwd = root.InverseTransformVector(c.Rb.rotation * Vector3.forward).normalized;
                        c.Up = root.InverseTransformVector(c.Rb.rotation * Vector3.up).normalized;
                        c.Vel = root.InverseTransformVector(GetVel(c.Rb));
                        bool rest = c.Vel.magnitude < 0.4f && c.Rb.angularVelocity.magnitude < 0.3f;
                        c.RestT = rest ? c.RestT + dt : 0f;
                        if (c.RestT > 1.5f || c.ModeT > 25f) { c.Mode = 2; c.ModeT = 0f; SetPhysical(c, false); }
                    }
                    else { c.Mode = 2; c.ModeT = 0f; }
                    c.V = 0f; c.Hazard = c.ModeT > 0.8f; c.Brake = false; c.IndL = c.IndR = false;
                }
                else
                {
                    // steht: nach einer Weile wieder einfädeln, wenn die Spur frei ist
                    c.Vel = Vector3.zero; c.V = 0f; c.Hazard = true; c.Brake = false; c.IndL = c.IndR = false;
                    if (c.ModeT > 6f) { c.RestT += dt; if (c.RestT > 2f) { c.RestT = 0f; TryRejoin(c); } }
                }
            }
        }

        void GroundProbe(Transform root, Car c)
        {
            if (c.Go == null || !_hasMe || Flat(c.Pos, _me) > 160f) { c.GroundDy = Mathf.Lerp(c.GroundDy, 0f, 0.1f); c.GroundUp = Vector3.up; return; }
            var cur = Cur(c);
            cur.Sample(c.S, out var surf, out _);
            var w = root.TransformPoint(surf);
            if (UnityEngine.Physics.Raycast(w + Vector3.up * 3f, Vector3.down, out var hit, 7f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                float dy = Mathf.Clamp(hit.point.y - w.y, -1.5f, 1.5f);
                c.GroundDy = Mathf.Lerp(c.GroundDy, dy, 0.35f);
                var n = root.InverseTransformDirection(hit.normal);
                if (n.y > 0.7f) c.GroundUp = n.normalized;
            }
        }

        void TryRejoin(Car c)
        {
            foreach (var o in _obs) if (o.Car != c && Flat(o.P, c.Pos) < 7f) return;
            if (!_lanes.Nearest(c.Pos, c.Fwd, 4.5f, out var lane, out var s)) return;
            var old = c.Pos;
            c.Lane = lane; c.S = s; c.Phase = 0; c.V = 0f; c.Mode = 0; c.ModeT = 0f; c.WaitT = 0f; c.StuckT = 0f;
            ChooseNext(c);
            ComputePose(c, 0f);
            c.VisOffset = old - c.Pos;
            if (c.VisOffset.sqrMagnitude > 25f) c.VisOffset = Vector3.zero;
            SetPhysical(c, false);
        }

        void DriveBody(Transform root, Car c, float dt)
        {
            var rb = c.Rb;
            if (rb == null) return;
            if (rb.interpolation != RigidbodyInterpolation.Interpolate) rb.interpolation = RigidbodyInterpolation.Interpolate;
            var pos = root.TransformPoint(c.Pos + c.VisOffset);
            var f = root.TransformVector(c.Fwd); var u = root.TransformVector(c.Up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            var rot = Quaternion.LookRotation(f.normalized, u.sqrMagnitude > 1e-6f ? u.normalized : Vector3.up);
            if (c.Physical && !rb.isKinematic)
            {
                // Körper folgt der KI über die Geschwindigkeit: Stöße wirken echt
                var err = pos - rb.position;
                if (err.magnitude > 3f) { Crash(c, GetVel(rb)); return; }   // weggeschoben
                SetVel(rb, Vector3.ClampMagnitude(err / dt, 70f));
                var dq = rot * Quaternion.Inverse(rb.rotation);
                dq.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 1e-3f) rb.angularVelocity = Vector3.zero;
                else rb.angularVelocity = Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad / dt), 20f);
            }
            else
            {
                rb.MovePosition(pos);
                rb.MoveRotation(rot);
            }
        }

        /// <summary>Unfall: KI aus, echter Körper mit Schwerkraft.</summary>
        void Crash(Car c, Vector3 worldVel)
        {
            if (c.Rb == null) { c.Mode = 2; c.ModeT = 0f; return; }
            c.Mode = 1; c.ModeT = 0f; c.RestT = 0f; c.V = 0f;
            c.Physical = true;
            c.Rb.isKinematic = false;
            c.Rb.useGravity = true;
            SetVel(c.Rb, worldVel);
        }

        void SetPhysical(Car c, bool on)
        {
            c.Physical = on;
            if (c.Rb == null) return;
            if (!on) { c.Rb.isKinematic = true; c.Rb.useGravity = false; }
            else { c.Rb.isKinematic = false; c.Rb.useGravity = false; }
        }

        /// <summary>Kollision eines Verkehrsautos (TrafficCarBody).</summary>
        internal void OnCarCollision(uint id, Collision col)
        {
            if (!_cars.TryGetValue(id, out var c) || c.Removed) return;
            var other = col.rigidbody;
            if (other == null) return;
            float rel = col.relativeVelocity.magnitude;
            var otherTraffic = other.GetComponent<TrafficCarBody>();
            var myCar = Manager.Current != null ? Manager.Current.GetComponent<Rigidbody>() : null;
            bool byMe = myCar != null && other == myCar;
            if (otherTraffic != null)
            {
                // Kettenreaktion nur, wenn das andere Auto schon verunfallt ist
                if (!_cars.TryGetValue(otherTraffic.Id, out var oc) || oc.Mode != 1 || rel < CrashSpeed || !c.Mine) return;
                if (c.Mode == 0) Crash(c, c.Rb != null ? GetVel(c.Rb) : Vector3.zero);
                return;
            }
            if (rel < CrashSpeed) return;
            if (c.Mine)
            {
                if (c.Mode == 1) return;
                var v = c.Rb != null && !c.Rb.isKinematic ? GetVel(c.Rb) : EstimateHitVelocity(c, other);
                Crash(c, v);
            }
            else if (byMe)
            {
                // fremdes Auto gerammt: übernehmen, Unfall bei mir berechnen
                var root = Manager.Streamer.MapRoot;
                var est = EstimateHitVelocity(c, other);
                Claim(c);
                if (c.Go != null && root != null)
                {
                    c.Rb.position = c.Go.transform.position;
                    c.Rb.rotation = c.Go.transform.rotation;
                    c.Pos = root.InverseTransformPoint(c.Go.transform.position);
                }
                c.VisOffset = Vector3.zero;
                Crash(c, est);
            }
        }

        Vector3 EstimateHitVelocity(Car c, Rigidbody other)
        {
            var root = Manager.Streamer.MapRoot;
            var mine = root != null ? root.TransformVector(c.Mine ? c.Vel : c.VisVel) : Vector3.zero;
            float m1 = Mathf.Max(100f, other.mass), m2 = Mathf.Max(300f, c.Type.Car.Mass);
            return mine + (GetVel(other) - mine) * (m1 / (m1 + m2)) * 1.3f;
        }

        // ------------------------------------------------------------------ Darstellung
        void BuildVisuals()
        {
            int built = 0;
            foreach (var c in _cars.Values)
            {
                if (c.Go != null || c.Removed || built >= 2) continue;
                var ti = c.Type;
                if (ti.Assets == null) { StartTypeLoad(ti); continue; }
                try { BuildCar(c); built++; }
                catch (Exception ex) { Debug.LogWarning("[TDU2] Verkehrsauto aufbauen: " + ex.Message); ti.Failed = true; }
            }
        }

        void BuildCar(Car c)
        {
            var a = c.Type.Assets;
            var go = new GameObject($"Verkehr #{c.Id & 0xFFFF} ({c.Type.Name})");
            go.layer = 2;   // Ignore Raycast: Radstrahlen des Spielers treffen es nicht, Kollision bleibt
            go.transform.SetParent(_container, false);
            var model = CarModel.Build(a, go.transform);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            c.Go = go; c.Model = model;

            // Lack (TR_xx: RampClr1)
            if (_paints.Count > 0)
            {
                var col = _paints[Mathf.Clamp(c.Color, 0, _paints.Count - 1)];
                foreach (var o in model.Owned)
                    if (o is Material m && m.name.StartsWith("PAINT", StringComparison.OrdinalIgnoreCase))
                    {
                        m.color = col;
                        if (m.HasProperty("_Color")) m.SetColor("_Color", col);
                    }
            }

            // Räder: FORK_xx lenkt, DISC_xx dreht; Bodenabstand aus den Rädern
            var wheels = new List<(Transform, Quaternion, float, Transform, Quaternion, float, bool)>();
            float minY = float.MaxValue, radius = 0f;
            foreach (var n in new[] { "FL", "FR", "RL", "RR" })
            {
                var fork = model.Find("FORK_" + n); var disc = model.Find("DISC_" + n);
                if (disc != null)
                    foreach (var r in disc.GetComponentsInChildren<MeshRenderer>())
                    {
                        minY = Mathf.Min(minY, r.bounds.min.y - go.transform.position.y);
                        radius = Mathf.Max(radius, r.bounds.extents.y);
                    }
                float steerSign = 1f, spinSign = 1f;
                if (fork != null && n[0] == 'F')
                {
                    var b0 = fork.localRotation; var q0 = fork.rotation;
                    fork.localRotation = b0 * Quaternion.AngleAxis(10f, Vector3.up);
                    var v = (fork.rotation * Quaternion.Inverse(q0)) * go.transform.forward;
                    fork.localRotation = b0;
                    steerSign = Vector3.SignedAngle(go.transform.forward, Vector3.ProjectOnPlane(v, go.transform.up), go.transform.up) > 0f ? 1f : -1f;
                }
                if (disc != null)
                {
                    var b0 = disc.localRotation;
                    var top = disc.position + go.transform.up * 0.3f;
                    var local = disc.InverseTransformPoint(top);
                    disc.localRotation = b0 * Quaternion.AngleAxis(10f, Vector3.right);
                    var moved = disc.TransformPoint(local);
                    disc.localRotation = b0;
                    spinSign = Vector3.Dot(moved - top, go.transform.forward) > 0f ? 1f : -1f;
                }
                wheels.Add((fork, fork != null ? fork.localRotation : Quaternion.identity, steerSign, disc, disc != null ? disc.localRotation : Quaternion.identity, spinSign, n[0] == 'F'));
            }
            c.Wheels = wheels.ToArray();
            var bb = model.BodyBounds;
            c.RideOffset = minY < float.MaxValue ? -minY : Mathf.Max(0.2f, -bb.min.y);
            if (radius > 0.15f) c.Radius = radius;
            c.HalfLen = Mathf.Max(1.5f, bb.extents.z);
            c.HalfWidth = Mathf.Max(0.7f, bb.extents.x);

            // Körper: Box ab etwas über dem Boden (fährt ohne Bodenkontakt), echte Masse
            var box = go.AddComponent<BoxCollider>();
            float bottom = bb.min.y + 0.25f;
            box.center = new Vector3(bb.center.x, (bottom + bb.max.y) * 0.5f, bb.center.z);
            box.size = new Vector3(bb.size.x * 0.95f, Mathf.Max(0.4f, bb.max.y - bottom), bb.size.z * 0.97f);
#if UNITY_6000_0_OR_NEWER
            var pm = new PhysicsMaterial { dynamicFriction = 0.7f, staticFriction = 0.8f, bounciness = 0.05f };
#else
            var pm = new PhysicMaterial { dynamicFriction = 0.7f, staticFriction = 0.8f, bounciness = 0.05f };
#endif
            box.sharedMaterial = pm;
            model.Owned.Add(pm);
            c.Box = box;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(600f, c.Type.Car.Mass);
            rb.isKinematic = true; rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
#if UNITY_6000_0_OR_NEWER
            rb.linearDamping = 0.05f; rb.angularDamping = 0.8f;
#else
            rb.drag = 0.05f; rb.angularDrag = 0.8f;
#endif
            rb.centerOfMass = new Vector3(bb.center.x, bb.min.y + bb.size.y * 0.35f, bb.center.z);
            c.Rb = rb;
            var body = go.AddComponent<TrafficCarBody>();
            body.Owner = this; body.Id = c.Id;

            var lights = go.AddComponent<TDU2VehicleLights>();
            lights.External = true;
            lights.UnityLights = false;
            lights.Setup(null, model);
            c.Lights = lights;

            // Startlage
            var root = Manager.Streamer.MapRoot;
            var p = c.Mine ? c.Pos : (c.HasVis ? c.VisPos : c.Pos);
            var f = root.TransformVector(c.Fwd);
            go.transform.SetPositionAndRotation(root.TransformPoint(p), Quaternion.LookRotation(f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward, Vector3.up));
            rb.position = go.transform.position; rb.rotation = go.transform.rotation;
            if (c.Mine && c.Mode == 0) ComputePose(c, 0f);
            if (c.Mine && c.Mode == 1) Crash(c, Vector3.zero);
        }

        void DestroyVisual(Car c)
        {
            if (c.Model != null) c.Model.Destroy();
            if (c.Go != null) UnityUtil.SafeDestroy(c.Go);
            c.Go = null; c.Model = null; c.Rb = null; c.Box = null; c.Lights = null; c.Audio = null; c.Wheels = null; c.Physical = false;
        }

        readonly List<(Car c, float d)> _near = new List<(Car, float)>();

        void UpdateVisuals(Transform root)
        {
            var cam = Camera.main;
            var camPos = cam != null ? cam.transform.position : Vector3.zero;
            _near.Clear();
            foreach (var c in _cars.Values)
            {
                if (c.Go == null) continue;
                float d = (c.Go.transform.position - camPos).magnitude;
                _near.Add((c, d));
            }
            _near.Sort((x, y) => x.d.CompareTo(y.d));
            int phys = 0, snd = 0;
            var mine = Manager.Lights;
            float dt = Time.deltaTime;
            foreach (var (c, d) in _near)
            {
                // Physik nur für die nächsten eigenen Autos im Umkreis
                if (c.Mine && c.Mode == 0)
                {
                    bool want = d < PhysicsRadius && phys < PhysicsMax && Time.timeScale > 0f;
                    if (want) phys++;
                    if (want != c.Physical) SetPhysical(c, want);
                }
                bool far = d > 900f;
                if (c.Go.activeSelf == far) c.Go.SetActive(!far);
                if (far) continue;

                float speed = c.Mine ? c.V : Vector3.Dot(c.VisVel, c.Fwd);
                c.Spin += speed * dt / Mathf.Max(0.2f, c.Radius);
                if (c.Wheels != null && d < 250f)
                    foreach (var w in c.Wheels)
                    {
                        if (w.fork != null && w.front) w.fork.localRotation = w.fBase * Quaternion.AngleAxis(c.Steer * w.steerSign, Vector3.up);
                        if (w.disc != null) w.disc.localRotation = w.dBase * Quaternion.AngleAxis(c.Spin * Mathf.Rad2Deg * w.spinSign, Vector3.right);
                    }
                if (c.Lights != null)
                {
                    // Nachts/bei Regen immer Licht an (TDU2Weather, car_headlights_intensity), sonst wie das eigene Auto
                    c.Lights.Headlights = TDU2.Map.World.TDU2Weather.HeadlightsWanted || (mine != null && mine.Headlights > 0) ? 1 : 0;
                    c.Lights.ExtBrake = c.Brake;
                    c.Lights.IndicatorLeft = c.IndL; c.Lights.IndicatorRight = c.IndR; c.Lights.Hazard = c.Hazard;
                    if (mine != null) { c.Lights.Brightness = mine.Brightness; c.Lights.LensBrightness = mine.LensBrightness; }
                    if (TDU2.Map.World.TDU2Weather.Instance != null) c.Lights.Brightness = Mathf.Max(c.Lights.Brightness, Mathf.Lerp(0.3f, 1.1f, TDU2.Map.World.TDU2Weather.Night));
                }
                // Sound für die nächsten Autos
                bool wantSnd = Sounds && d < SoundRadius && snd < SoundMax && Time.timeScale > 0f && c.Type.Assets?.Sound != null;
                if (wantSnd) snd++;
                if (wantSnd && c.Audio == null)
                {
                    c.Audio = c.Go.AddComponent<TDU2VehicleAudio>();
                    c.Audio.External = true;
                    c.Audio.MinDistance = 4f; c.Audio.MaxDistance = 90f;
                    c.Audio.MasterVolume = Manager.Volume * 0.7f;
                    c.Audio.Setup(c.Type.Assets);
                }
                else if (!wantSnd && c.Audio != null && d > SoundRadius * 1.3f)
                {
                    UnityUtil.SafeDestroy(c.Audio);
                    foreach (var src in c.Go.GetComponents<AudioSource>()) UnityUtil.SafeDestroy(src);
                    c.Audio = null;
                }
                if (c.Audio != null)
                {
                    float v = Mathf.Abs(speed);
                    int gear = Mathf.Clamp(Mathf.FloorToInt(v / 7.5f), 0, 4);
                    float inGear = (v - gear * 7.5f) / 7.5f;
                    c.Audio.ExtKmh = v * 3.6f;
                    c.Audio.ExtRpm = 850f + gear * 350f + Mathf.Clamp01(inGear) * 2300f;
                    c.Audio.ExtTorque = c.Mine ? Mathf.Clamp(c.Accel * 120f + 60f, 0f, 250f) : 80f;
                    c.Audio.MasterVolume = Manager.Volume * 0.7f;
                }
            }
        }

        // ------------------------------------------------------------------ fremde Autos (Interpolation)
        void UpdateRemoteCars(Transform root)
        {
            double now = _mp != null ? _mp.Clock : Time.unscaledTimeAsDouble;
            float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
            foreach (var c in _cars.Values)
            {
                if (c.Mine || c.Removed) continue;
                if (!SampleRemote(c, now, out var s)) continue;
                if (!c.HasVis || (s.P - c.VisPos).sqrMagnitude > 20f * 20f) { c.VisPos = s.P; c.VisVel = s.V; c.HasVis = true; }
                else
                {
                    var predicted = c.VisPos + c.VisVel * dt;
                    var err = predicted - s.P;
                    var np = s.P + err * Mathf.Exp(-dt * 10f);
                    c.VisVel = (np - c.VisPos) / dt;
                    c.VisPos = np;
                }
                c.Pos = c.VisPos; c.Fwd = s.F; c.Up = s.U; c.Steer = s.Steer;
                c.Brake = (s.Flags & 2) != 0; c.Hazard = (s.Flags & 4) != 0; c.IndL = (s.Flags & 8) != 0; c.IndR = (s.Flags & 16) != 0;
                if (c.Go == null) continue;
                var f = root.TransformVector(s.F); var u = root.TransformVector(s.U);
                var rot = Quaternion.LookRotation(f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward, u.sqrMagnitude > 1e-6f ? u.normalized : Vector3.up);
                c.VisRot = Quaternion.Angle(c.VisRot, rot) > 60f ? rot : Quaternion.Slerp(c.VisRot, rot, 1f - Mathf.Exp(-dt * 15f));
                if (c.Rb != null && !c.Rb.isKinematic) SetPhysical(c, false);
                if (c.Rb != null && c.Rb.interpolation != RigidbodyInterpolation.None) c.Rb.interpolation = RigidbodyInterpolation.None;
                c.Go.transform.SetPositionAndRotation(root.TransformPoint(c.VisPos), c.VisRot);
                if (c.Rb != null) { c.Rb.position = c.Go.transform.position; c.Rb.rotation = c.VisRot; }
            }
        }

        bool SampleRemote(Car c, double now, out Snap s)
        {
            s = default;
            if (c.Snaps.Count == 0) return false;
            double off = 0, jit = 0;
            if (c.Sender != 0 && _clocks.TryGetValue(c.Sender, out var clk)) { off = clk.Offset; jit = clk.Jitter; }
            else if (c.Sender == 0) off = 0;
            double delay = Mathf.Clamp((float)(0.08 + jit * 2.5 + 1.0 / Mathf.Max(2f, SendRate)), 0.1f, 0.6f);
            double t = now - off - delay;
            var snaps = c.Snaps;
            if (t <= snaps[0].T) { s = snaps[0]; return true; }
            for (int i = snaps.Count - 1; i > 0; i--)
            {
                var a = snaps[i - 1]; var b = snaps[i];
                if (t < a.T || t > b.T) continue;
                double span = Math.Max(1e-4, b.T - a.T);
                float k = (float)((t - a.T) / span), h = (float)span;
                s = b;
                if ((b.P - a.P).sqrMagnitude > 25f * 25f) return true;
                float k2 = k * k, k3 = k2 * k;
                s.P = (2 * k3 - 3 * k2 + 1) * a.P + (k3 - 2 * k2 + k) * h * a.V + (-2 * k3 + 3 * k2) * b.P + (k3 - k2) * h * b.V;
                s.V = Vector3.Lerp(a.V, b.V, k);
                s.F = Vector3.Slerp(a.F, b.F, k);
                s.U = Vector3.Slerp(a.U, b.U, k);
                s.Steer = Mathf.Lerp(a.Steer, b.Steer, k);
                return true;
            }
            s = snaps[snaps.Count - 1];
            s.P += s.V * Mathf.Clamp((float)(t - s.T), 0f, 0.6f);
            return true;
        }

        /// <summary>Nach einer Übergabe: aktuellen Zustand als ersten Stützpunkt (bis die Pakete des neuen Besitzers kommen).</summary>
        void SeedSnap(Car c)
        {
            double now = _mp != null ? _mp.Clock : Time.unscaledTimeAsDouble;
            c.Sender = 0;
            c.Snaps.Add(new Snap { T = now, P = c.Pos, F = c.Fwd, U = c.Up, V = c.Vel, Steer = c.Steer });
            c.VisPos = c.Pos; c.VisVel = c.Vel; c.HasVis = true;
        }

        // ------------------------------------------------------------------ Netz
        const int CarSize = 54, MaxPerPacket = 20;
        readonly byte[] _pkt = new byte[8 + CarSize * MaxPerPacket];
        readonly List<Car> _send = new List<Car>();

        void SendNet()
        {
            if (!Sync || _mp == null || !_mp.CanSend || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 1f / Mathf.Max(1f, SendRate);
            if (_players.Count == 0)
            {
                // niemand da: übergebene/entfernte Autos trotzdem abschließen
                foreach (var c in _cars.Values) { if (c.HandoffSends > 0) c.HandoffSends = 0; if (c.RemoveSends > 0) c.RemoveSends = 0; }
                return;
            }
            _send.Clear();
            foreach (var c in _cars.Values)
            {
                bool relevant = false;
                if (c.Mine || c.HandoffSends > 0 || c.RemoveSends > 0)
                    foreach (var p in _players) if (Flat(c.Pos, p.pos) < RelevantDistance) { relevant = true; break; }
                if (c.HandoffSends > 0 || c.RemoveSends > 0) relevant = true;
                if (relevant) _send.Add(c);
            }
            uint time = (uint)(_mp.Clock * 1000.0);
            for (int i = 0; i < _send.Count; i += MaxPerPacket)
            {
                int n = Math.Min(MaxPerPacket, _send.Count - i);
                var b = _pkt;
                b[0] = 1; b[1] = _islandCode; WriteU32(b, 2, time); b[6] = (byte)n; b[7] = 0;
                int o = 8;
                for (int k = 0; k < n; k++, o += CarSize) WriteCar(b, o, _send[i + k]);
                _mp.SendTraffic(b, o);
            }
            foreach (var c in _send)
            {
                if (c.HandoffSends > 0) c.HandoffSends--;
                if (c.RemoveSends > 0) c.RemoveSends--;
            }
        }

        /// <summary>
        /// Auto (54 Byte): +0 u32 Id, +4 u16 Besitzer, +6 u8 Epoche, +7 u8 Flags (1 entfernt, 2 Bremse, 4 Warnblinker, 8 Blinker l, 16 Blinker r),
        /// +8 u16 DB-Id, +10 u8 Lack, +11 i8 Lenkwinkel, +12 f32×3 Position, +24 i16×3 Richtung, +30 i16×3 Oben, +36 i16×3 Geschw. (cm/s),
        /// +42 u16 Straße, +44 u8 Spur|Richtung(0x80), +45 u8 Modus (0 Spur, 1 Kreuzung, 2 Unfall/steht), +46 u16 nächste Straße,
        /// +48 u8 nächste Spur|Richtung, +49 u8 Tempofaktor×100, +50 f32 Weg auf Spur/Kreuzung.
        /// </summary>
        void WriteCar(byte[] b, int o, Car c)
        {
            WriteU32(b, o, c.Id);
            b[o + 4] = (byte)c.Owner; b[o + 5] = (byte)(c.Owner >> 8);
            b[o + 6] = c.Epoch;
            byte fl = 0;
            if (c.Removed) fl |= 1;
            if (c.Brake) fl |= 2;
            if (c.Hazard) fl |= 4;
            if (c.IndL) fl |= 8;
            if (c.IndR) fl |= 16;
            b[o + 7] = fl;
            b[o + 8] = (byte)c.Type.DbId; b[o + 9] = (byte)(c.Type.DbId >> 8);
            b[o + 10] = (byte)c.Color;
            b[o + 11] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(c.Steer), -127, 127);
            var p = c.Pos + (c.Mine ? c.VisOffset : Vector3.zero);
            WriteF(b, o + 12, p.x); WriteF(b, o + 16, p.y); WriteF(b, o + 20, p.z);
            WriteDir(b, o + 24, c.Fwd); WriteDir(b, o + 30, c.Up);
            WriteI16(b, o + 36, c.Vel.x * 100f); WriteI16(b, o + 38, c.Vel.y * 100f); WriteI16(b, o + 40, c.Vel.z * 100f);
            int mode = c.Mode != 0 ? 2 : c.Phase;
            var lane = c.Lane; var next = c.Next;
            b[o + 42] = (byte)(lane != null ? lane.RoadId : 0); b[o + 43] = (byte)((lane != null ? lane.RoadId : 0) >> 8);
            b[o + 44] = (byte)((lane != null ? lane.Lane & 0x7F : 0) | (lane != null && lane.Forward ? 0x80 : 0));
            b[o + 45] = (byte)mode;
            b[o + 46] = (byte)(next != null ? next.RoadId : 0); b[o + 47] = (byte)((next != null ? next.RoadId : 0) >> 8);
            b[o + 48] = (byte)((next != null ? next.Lane & 0x7F : 0) | (next != null && next.Forward ? 0x80 : 0));
            b[o + 49] = (byte)Mathf.Clamp(Mathf.RoundToInt(c.SpeedFactor * 100f), 0, 255);
            WriteF(b, o + 50, c.S);
        }

        void ReceiveNet()
        {
            if (_mp == null) return;
            var gd = GD;
            while (_mp.TrafficInbox.TryDequeue(out var pk))
            {
                var d = pk.data;
                if (d.Length < 11) continue;
                int sender = d[1] | (d[2] << 8);
                if (sender == _myPid || d[3] != 1 || d[4] != _islandCode) continue;
                double sendT = BitConverter.ToUInt32(d, 5) / 1000.0;
                int n = d[9];
                if (d.Length < 11 + n * CarSize) continue;
                // Uhrversatz je Absender (kleinster Laufzeitunterschied der letzten Pakete)
                if (!_clocks.TryGetValue(sender, out var clk)) _clocks[sender] = clk = new SenderClock();
                double lag = pk.t - sendT;
                clk.Lag.Enqueue(lag);
                while (clk.Lag.Count > 40) clk.Lag.Dequeue();
                double min = double.MaxValue; foreach (var l in clk.Lag) if (l < min) min = l;
                clk.Offset = min; clk.Jitter = clk.Jitter * 0.9 + (lag - min) * 0.1;
                for (int k = 0; k < n; k++) ReadCar(d, 11 + k * CarSize, sender, sendT);
            }
        }

        static bool Newer(byte a, byte b) { int diff = (byte)(a - b); return diff > 0 && diff < 128; }

        void ReadCar(byte[] d, int o, int sender, double sendT)
        {
            uint id = BitConverter.ToUInt32(d, o);
            int owner = d[o + 4] | (d[o + 5] << 8);
            byte epoch = d[o + 6], fl = d[o + 7];
            int dbId = d[o + 8] | (d[o + 9] << 8);
            var snap = new Snap
            {
                T = sendT,
                Flags = fl,
                Steer = (sbyte)d[o + 11],
                P = new Vector3(BitConverter.ToSingle(d, o + 12), BitConverter.ToSingle(d, o + 16), BitConverter.ToSingle(d, o + 20)),
                F = ReadDir(d, o + 24),
                U = ReadDir(d, o + 30),
                V = new Vector3(BitConverter.ToInt16(d, o + 36), BitConverter.ToInt16(d, o + 38), BitConverter.ToInt16(d, o + 40)) * 0.01f,
            };
            _cars.TryGetValue(id, out var c);
            if ((fl & 1) != 0)
            {
                if (c != null && !c.Mine && !Newer(c.Epoch, epoch)) { DestroyVisual(c); _cars.Remove(id); }
                return;
            }
            if (c == null)
            {
                if (!_types.TryGetValue(dbId, out var ti) || ti.Failed) return;
                if (_hasMe && Flat(snap.P, _me) > RelevantDistance + 300f) return;
                c = new Car { Id = id, Owner = owner, Epoch = epoch, Mine = false, Type = ti, Color = d[o + 10], Pos = snap.P, Fwd = snap.F };
                _cars[id] = c;
                StartTypeLoad(ti);
            }
            if (c.Removed) return;
            if (c.Mine)
            {
                // Konflikt: jemand anderes beansprucht mein Auto (gerammt) – neuere Epoche gewinnt, bei Gleichstand die kleinere Spielernummer
                if (owner == _myPid) return;
                bool lose = Newer(epoch, c.Epoch) || (epoch == c.Epoch && owner < _myPid);
                if (!lose) return;
                c.Mine = false; c.Owner = owner; c.Epoch = epoch;
                c.Snaps.Clear(); c.VisPos = c.Pos; c.VisVel = c.Vel; c.HasVis = true;
                SetPhysical(c, false);
            }
            else
            {
                if (Newer(c.Epoch, epoch)) return;   // veraltet (vom vorigen Besitzer)
                c.Owner = owner; c.Epoch = epoch;
            }
            if (c.Sender != sender) c.Snaps.Clear();   // andere Uhr
            c.Sender = sender;
            c.LastPacket = Time.unscaledTime;
            c.HandoffSends = 0;
            if (c.Snaps.Count > 0 && snap.T <= c.Snaps[c.Snaps.Count - 1].T)
            {
                if (c.Snaps[c.Snaps.Count - 1].T - snap.T > 5.0) c.Snaps.Clear();   // Neustart des Absenders
                else return;
            }
            c.Snaps.Add(snap);
            if (c.Snaps.Count > 30) c.Snaps.RemoveRange(0, c.Snaps.Count - 30);
            int lane = d[o + 44], nlane = d[o + 48];
            c.NetRoad = d[o + 42] | (d[o + 43] << 8); c.NetLane = lane & 0x7F; c.NetFwd = (lane & 0x80) != 0;
            c.NetMode = d[o + 45];
            c.NetNextRoad = d[o + 46] | (d[o + 47] << 8); c.NetNextLane = nlane & 0x7F; c.NetNextFwd = (nlane & 0x80) != 0;
            c.SpeedFactor = d[o + 49] / 100f;
            c.NetS = BitConverter.ToSingle(d, o + 50);
            // an mich übergeben
            if (owner == _myPid && _myPid != 0) InitSimFromNet(c);
        }

        static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static void WriteF(byte[] b, int o, float f) => WriteU32(b, o, BitConverter.ToUInt32(BitConverter.GetBytes(f), 0));
        static void WriteI16(byte[] b, int o, float v) { short s = (short)Mathf.Clamp(Mathf.RoundToInt(v), -32767, 32767); b[o] = (byte)s; b[o + 1] = (byte)(s >> 8); }
        static void WriteDir(byte[] b, int o, Vector3 v) { v = v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; WriteI16(b, o, v.x * 32767f); WriteI16(b, o + 2, v.y * 32767f); WriteI16(b, o + 4, v.z * 32767f); }
        static Vector3 ReadDir(byte[] d, int o) => new Vector3(BitConverter.ToInt16(d, o), BitConverter.ToInt16(d, o + 2), BitConverter.ToInt16(d, o + 4)) / 32767f;

        static Vector3 GetVel(Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        static void SetVel(Rigidbody rb, Vector3 v)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = v;
#else
            rb.velocity = v;
#endif
        }

        // ------------------------------------------------------------------ Inspector/Debug
        /// <summary>Für den Inspector: Id, eigenes Auto, Modell, Modus, Tempo (km/h), Besitzer.</summary>
        public IEnumerable<(uint id, bool mine, string model, string mode, float kmh, int owner, float dist)> Info()
        {
            foreach (var c in _cars.Values)
            {
                string mode = c.Removed ? "entfernt" : !c.Mine ? "fremd" : c.Mode == 1 ? "Unfall" : c.Mode == 2 ? "steht" : c.Phase == 1 ? "Kreuzung" : (c.WaitT > 0f ? "wartet" : "fährt");
                float kmh = (c.Mine ? c.V : c.VisVel.magnitude) * 3.6f;
                yield return (c.Id, c.Mine, c.Type?.Name ?? "-", mode, kmh, c.Owner, _hasMe ? Flat(c.Pos, _me) : 0f);
            }
        }

        void OnDrawGizmos()
        {
            if (!ShowDebug || _lanes == null || Manager == null || Manager.Streamer == null || Manager.Streamer.MapRoot == null) return;
            var root = Manager.Streamer.MapRoot;
            foreach (var c in _cars.Values)
            {
                if (!c.Mine || c.Lane == null) continue;
                Gizmos.color = c.Mode != 0 ? Color.red : c.WaitT > 0f ? Color.yellow : Color.green;
                Vector3 prev = root.TransformPoint(c.Pos);
                for (float s = 2f; s < 40f; s += 2f)
                {
                    var p = root.TransformPoint(PointAhead(c, s));
                    Gizmos.DrawLine(prev, p); prev = p;
                }
            }
        }

        void OnGUI()
        {
            if (!ShowDebug) return;
            GUI.Label(new Rect(10, Screen.height - 60, 900, 22), $"Verkehr: {_cars.Count} Autos ({OwnedCount} eigene) – {_status}");
        }
    }

    /// <summary>Meldet Kollisionen eines Verkehrsautos an TDU2Traffic.</summary>
    public sealed class TrafficCarBody : MonoBehaviour
    {
        public TDU2Traffic Owner;
        public uint Id;
        void OnCollisionEnter(Collision c) { if (Owner != null) Owner.OnCarCollision(Id, c); }
    }
}
