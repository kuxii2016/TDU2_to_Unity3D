using System;
using System.Collections.Generic;
using System.Globalization;
using TDU2.Map.Flash;
using TDU2.Map.Formats;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>
    /// Einzelspieler-Meisterschaften (DBLicense mit _isachampionship):
    ///  - Freischaltung: Normal (Order 1) mit der Rennschul-Lizenz der Fahrzeugklasse (z.B. C4), Elite (Order 2) sobald die
    ///    Normal-Meisterschaft derselben Klasse abgeschlossen ist.
    ///  - Wie im Spiel EIN Spot je Meisterschaft (Treffpunkt = ECIL-Position der TNOC-Gruppe in scenarics/spotworld.bin):
    ///    Kartensymbol asphalt / dirt / classic / cup (Elite: *_h), Lichtsäule in der Welt. Dort anhalten + Enter öffnet die
    ///    große Karte im Wettbewerbsmodus (map.vmf "co_direct_enter"): erst dort erscheinen die Rennen der Meisterschaft,
    ///    Auswahl -> Teleport an den Start und Rennen. Auf der großen Karte führt A auf den Meisterschafts-Spot ebenso dorthin.
    ///  - Startpunkte der Rennen (2EGI-Einträge der Gruppe); direkt am Startpunkt anhalten + Enter startet auch.
    ///  - Rennen (gmth 3): 7 Gegner aus der Lizenz (_bot_n = DBPNJ, Auto der Klasse aus DBPNJCar, Stärke _difficulty_bot_n) in
    ///    Startaufstellung, Platzierung nach Weg/Zieleinlauf. Zeitfahren (0): Gegnerzeiten aus der Zielzeit timG.
    ///    Radar (1): Summe der Tempi an den Toren. Tempo (10): Durchschnittstempo über die Zeit CtDw. [Wertung vermutet]
    ///  - Punkte 10-8-6-5-4-3-2-1 je Rennen, Gesamtwertung nach allen Rennen (RaceProgress, PlayerPrefs).
    /// </summary>
    public sealed class TDU2Championships : MonoBehaviour
    {
        public static TDU2Championships Instance { get; private set; }
        /// <summary>Ergebnisfenster offen (ESC gehört dann dem Fenster, nicht dem Pausemenü).</summary>
        public static bool ResultsOpen => Instance != null && Instance._phase == Phase.Results;
        public static bool AutoCreate = true;

        [Tooltip("Alle Meisterschaften freischalten (Test).")]
        public bool UnlockAll = false;
        [Tooltip("Nur mit einem Auto der Meisterschaftsklasse starten (wie im Spiel).")]
        public bool RequireCarCategory = true;
        [Tooltip("Abstand zum Startpunkt (m), ab dem gestartet werden kann.")]
        public float TriggerRadius = 16f;
        [Tooltip("Höchsttempo (km/h), um am Startpunkt starten zu können.")]
        public float TriggerMaxKmh = 35f;
        [Tooltip("Lichtsäulen bis zu dieser Entfernung (m) anzeigen.")]
        public float BeaconDistance = 900f;
        [Tooltip("Punkte je Platz.")]
        public int[] PointsTable = { 10, 8, 6, 5, 4, 3, 2, 1 };
        [Tooltip("Tempo-Faktor der Gegner (1 = Standard).")]
        public float BotSpeedScale = 1f;
        [Tooltip("Abstand der Startreihen (m).")]
        public float GridRowSpacing = 8f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!AutoCreate || Instance != null) return;
            var go = new GameObject("TDU2 Championships");
            DontDestroyOnLoad(go);
            go.AddComponent<TDU2Championships>();
        }

        // ------------------------------------------------------------------ Daten
        sealed class Cup
        {
            public LicenseInfo Lic;
            public string Name;
            public readonly List<RaceDefinition> Races = new List<RaceDefinition>();
            public string ClassName => RaceDatabase.CategoryName(Lic.VehicleCategory1);
            public bool Elite => Lic.Order >= 2;
        }

        TDU2VehicleManager _man;
        RaceRepository _repo;
        string _bnk;
        int _island = -1;
        readonly List<Cup> _cups = new List<Cup>();
        readonly Dictionary<string, (Cup cup, RaceDefinition race, Spot spot)> _spots = new Dictionary<string, (Cup, RaceDefinition, Spot)>();
        readonly Dictionary<string, GameObject> _beacons = new Dictionary<string, GameObject>();
        readonly Dictionary<string, (Cup cup, Spot spot)> _hubs = new Dictionary<string, (Cup, Spot)>();
        Cup _nearHub;
        float _nextRefresh;
        int _lastSpotCount = -1;
        Material _beaconMat;
        Mesh _beaconMesh;

        // ------------------------------------------------------------------ Rennen
        enum Phase { None, Racing, Speed, Results }
        Phase _phase;
        GameObject _raceGo;
        TDU2RaceManager _race;
        RaceHud _hud;
        RaceAutopilot _autopilot;
        RaceResults _results;
        bool _filmTried, _filmShown;
        Cup _cup;
        RaceDefinition _def;
        readonly Dictionary<string, (int target, int limit, bool hasLimit)> _orig = new Dictionary<string, (int, int, bool)>();
        float[] _cum;          // Weglänge entlang RoadPath
        int _pIdx;
        float _pProgress;
        int _lastPos = -1, _lastTotal = -1;
        float _finishLen;
        readonly List<float> _cpDist = new List<float>();       // Weg der Tore (Radar)
        readonly List<float> _playerRadar = new List<float>();
        float[][] _botRadar;
        int[] _botRadarNext;
        float _resultAt = -1f;
        List<Row> _rows;
        string _resultTitle, _resultInfo, _cupInfo;
        bool _privateOn, _trafficWas;
        // Tempo-Modus
        float _speedT, _speedDist, _speedCountdown, _gaugeBase = -1f;
        // Meldungen
        string _toast; float _toastUntil;
        (Cup cup, RaceDefinition race) _near;
        CursorLockMode _savedLock; bool _savedCursor;

        sealed class Row
        {
            public string Name, Car;
            public bool Player, Finished;
            public float Value;      // Zeit (s) bzw. Wert (km/h)
            public int Points, Place, BotIndex = -1;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            // ESC-Menü im Rennen (main.vmf WhatGameMode 2): "Neu starten" / "Beenden" = Rennen abbrechen
            TDU2PauseMenu.RaceActive = () => Instance != null && (Instance._phase == Phase.Racing || Instance._phase == Phase.Speed);
            TDU2PauseMenu.RaceQuit = () => { if (Instance != null) { Instance.Toast("Rennen abgebrochen"); Instance.EndRace(); } };
            TDU2PauseMenu.RaceRestart = () =>
            {
                if (Instance == null) return;
                var c = Instance._cup; var d = Instance._def;
                Instance.EndRace();
                if (c != null && d != null) Instance.BeginRace(c, d);
            };
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ClearSpots();
            foreach (var b in _beacons.Values) if (b != null) Destroy(b);
            _beacons.Clear();
            SetPrivate(false);
        }

        float _nextFind;

        TDU2VehicleManager Man
        {
            get
            {
                if (_man == null && Time.unscaledTime >= _nextFind)
                {
                    _nextFind = Time.unscaledTime + 1f;
#if UNITY_2023_1_OR_NEWER
                    _man = FindFirstObjectByType<TDU2VehicleManager>();
#else
                    _man = FindObjectOfType<TDU2VehicleManager>();
#endif
                }
                return _man;
            }
        }

        Transform Root => Man != null && Man.Streamer != null ? Man.Streamer.MapRoot : null;

        int CurrentIsland()
        {
            string n = Man != null && Man.Streamer != null && Man.Streamer.Index != null ? Man.Streamer.Index.IslandName : "";
            if (string.IsNullOrEmpty(n)) return -1;
            return n.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        bool EnsureData()
        {
            var man = Man;
            if (man == null || string.IsNullOrEmpty(man.BnkDir) || man.Streamer == null || man.Streamer.MapRoot == null) return false;
            int isl = CurrentIsland();
            if (isl < 0) return false;
            // nach einem Inselwechsel (Szene neu geladen): Rennobjekte gehören noch zum alten Streamer
            if (_race != null && _race.Streamer != man.Streamer)
            {
                if (_raceGo != null) Destroy(_raceGo);
                _raceGo = null; _race = null; _hud = null; _autopilot = null; _results = null;
                _phase = Phase.None; _privateOn = false;
            }
            if (_repo != null && _bnk == man.BnkDir && _island == isl) return true;
            try
            {
                _bnk = man.BnkDir;
                _island = isl;
                _repo = RaceRepository.Load(_bnk);
                RaceTexts.Ensure(_bnk, TDU2PauseMenu.SavedLanguage());
                _cups.Clear();
                foreach (var l in _repo.Database.Championships(isl))
                {
                    var c = new Cup { Lic = l };
                    c.Name = RaceTexts.Get(l.NameHc, (l.Order >= 2 ? "Elite-Meisterschaft " : "Meisterschaft ") + RaceDatabase.CategoryName(l.VehicleCategory1));
                    foreach (var f in l.ChallengeFiles)
                    {
                        var d = _repo.Build(f, l);
                        if (d != null && d.Checkpoints.Count > 0) c.Races.Add(d);
                    }
                    if (c.Races.Count > 0) _cups.Add(c);
                }
                Debug.Log("[TDU2] Meisterschaften: " + _cups.Count + " auf Insel " + isl + " (" + string.Join(", ", _cups.ConvertAll(c => c.Lic.ChallengeFiles[0].Substring(0, 5) + (IsAvailable(c) ? "*" : ""))) + ")");
                ClearSpots();
                _nextRefresh = 0f;
                return true;
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Meisterschaften: " + ex.Message); _repo = null; return false; }
        }

        // ------------------------------------------------------------------ Freischaltung

        bool SchoolLicenceWon(int cat)
        {
            var school = _repo.Database.SchoolLicenseWithCategory(cat);
            if (school == null) return false;
            if (RaceProgress.IsLicenceWon(school.Hash)) return true;
            foreach (var t in school.ChallengeFiles) if (!RaceProgress.IsTestDone(t)) return false;
            return school.ChallengeFiles.Count > 0;
        }

        bool IsAvailable(Cup c)
        {
            if (UnlockAll) return true;
            int cat = c.Lic.VehicleCategory1;
            if (cat <= 0) return false;   // Insel-Meisterschaften (alle Klassen) – noch nicht freigeschaltet
            if (!SchoolLicenceWon(cat)) return false;
            if (c.Lic.Order <= 1) return true;
            foreach (var o in _cups)
                if (o.Lic.VehicleCategory1 == cat && o.Lic.Order == c.Lic.Order - 1) return RaceProgress.IsCupDone(o.Lic.Hash);
            return true;
        }

        static string ModeLabel(RaceDefinition d)
        {
            switch (d.Kind)
            {
                case RaceMode.Race: return "race";
                case RaceMode.SpeedTrap: return "speedtrap";
                case RaceMode.Speed: return "speed";
                default: return "timeAttack";
            }
        }

        static string ModeName(RaceDefinition d)
        {
            switch (d.Kind)
            {
                case RaceMode.Race: return "Rennen";
                case RaceMode.SpeedTrap: return "Radar";
                case RaceMode.Speed: return "Tempo";
                default: return "Zeitfahren";
            }
        }

        string RaceName(RaceDefinition d) => RaceTexts.Get(d.NameHc, d.Title);

        // ------------------------------------------------------------------ Spots / Lichtsäulen

        void RefreshSpots()
        {
            var spots = Man?.Streamer?.Spots;
            if (spots == null) return;
            var want = new HashSet<string>();
            if (_phase == Phase.None)
                foreach (var c in _cups)
                {
                    if (!IsAvailable(c) || RaceProgress.IsCupDone(c.Lic.Hash)) continue;
                    for (int i = 0; i < c.Races.Count; i++)
                    {
                        var r = c.Races[i];
                        if (RaceProgress.IsCupRaceDone(c.Lic.Hash, r.Name)) continue;
                        string code = "#CUP_" + r.Name;
                        want.Add(code);
                        if (!_spots.TryGetValue(code, out var e))
                        {
                            // Rennstart: nicht als Kartensymbol (die Rennen zeigt erst der Wettbewerbsmodus der Karte)
                            StartFrame(r, out var sp, out var f);
                            var s = new Spot { Code = code, Position = sp, Position2 = sp - f * 14f, Index = -1 };
                            e = (c, r, s);
                            _spots[code] = e;
                        }
                    }
                }
            // Meisterschafts-Spots (Treffpunkt) – alle freigeschalteten, auch abgeschlossene (Wiederholen/Ergebnis)
            var wantHub = new HashSet<string>();
            if (_phase == Phase.None)
                foreach (var c in _cups)
                {
                    if (!IsAvailable(c)) continue;
                    string code = HubCode(c);
                    wantHub.Add(code);
                    if (!_hubs.TryGetValue(code, out var h))
                    {
                        var pos = HubPosition(c, out var f);
                        h = (c, new Spot { Code = code, Position = pos, Position2 = pos - f * 14f, Index = -1 });
                        _hubs[code] = h;
                        TDU2Minimap.ExtraSpotLabels[code] = HubLabel(c);
                        TDU2Minimap.ExtraSpotNames[code] = c.Name;
                        //TDU2Minimap.ExtraSpotNumbers[code] = ClassNumber(c);
                        TDU2Minimap.ExtraSpotPictures[code] = ContestPicture(c);
                    }
                    if (!spots.Spots.Contains(h.spot)) spots.Spots.Add(h.spot);
                }
            foreach (var code in new List<string>(_hubs.Keys))
            {
                if (wantHub.Contains(code)) continue;
                spots.Spots.Remove(_hubs[code].spot);
                _hubs.Remove(code);
                TDU2Minimap.ExtraSpotLabels.Remove(code);
                TDU2Minimap.ExtraSpotNames.Remove(code);
                TDU2Minimap.ExtraSpotPictures.Remove(code);
                //TDU2Minimap.ExtraSpotNumbers.Remove(code);
            }
            if (want.Count != _lastSpotCount)
            {
                _lastSpotCount = want.Count;
                string first = null;
                foreach (var kv in _spots) { first = kv.Value.race.Name + " @ " + kv.Value.spot.Position.ToString("0"); break; }
                TDU2DebugLog.Event("Meisterschaften: " + _hubs.Count + " Treffpunkte auf der Karte, " + want.Count + " offene Rennen (" + (first ?? "-") + "), Spots gesamt " + spots.Spots.Count);
            }
            foreach (var code in new List<string>(_spots.Keys))
            {
                if (want.Contains(code)) continue;
                _spots.Remove(code);
            }
        }

        /// <summary>Kartenbild der Meisterschaft: frontend/AllRes/MapScreens/contest/&lt;as|of|cl&gt;-&lt;Klasse&gt;-&lt;1 Normal|2 Elite&gt;.bnk,
        /// Insel-Meisterschaften cu-&lt;i|h&gt;-&lt;n&gt;.</summary>
        string ContestPicture(Cup c)
        {
            int cat = c.Lic.VehicleCategory1, ord = Mathf.Clamp(c.Lic.Order, 1, 2);
            string kind; int num;
            if (cat >= 1 && cat <= 7) { kind = "as"; num = cat; }
            else if (cat >= 9 && cat <= 12) { kind = "of"; num = cat - 8; }
            else if (cat >= 14 && cat <= 17) { kind = "cl"; num = cat - 13; }
            else return "contest/cu-" + (_island == 0 ? "i" : "h") + "-" + ord;
            return "contest/" + kind + "-" + num + "-" + ord;
        }

        void ClearSpots()
        {
            var spots = _man != null && _man.Streamer != null ? _man.Streamer.Spots : null;
            foreach (var kv in _hubs)
            {
                if (spots != null) spots.Spots.Remove(kv.Value.spot);
                TDU2Minimap.ExtraSpotLabels.Remove(kv.Key);
                TDU2Minimap.ExtraSpotNames.Remove(kv.Key);
                TDU2Minimap.ExtraSpotPictures.Remove(kv.Key);
                //TDU2Minimap.ExtraSpotNumbers.Remove(kv.Key);
            }
            _hubs.Clear();
            _spots.Clear();
        }

        static string HubCode(Cup c) => "#CUPHUB_" + c.Lic.Hash.ToString("X");

        /// <summary>Kartensymbol der Meisterschaft (map.vmf point/spot): asphalt (A), dirt (B), classic (C), cup (Insel); Elite "_h".</summary>
        static string HubKind(Cup c)
        {
            int cat = c.Lic.VehicleCategory1;
            if (cat >= 1 && cat <= 7) return "asphalt";
            if (cat >= 9 && cat <= 12) return "dirt";
            if (cat >= 14 && cat <= 17) return "classic";
            return "cup";
        }
        static string HubLabel(Cup c) => HubKind(c) + (c.Elite ? "_h" : "");

        /// <summary>Treffpunkt: ECIL-Position der TNOC-Gruppe der Lizenz, sonst Start des ersten Rennens.</summary>
        Vector3 HubPosition(Cup c, out Vector3 fwd)
        {
            var sw = _repo != null ? _repo.SpotWorld : null;
            if (sw != null)
                foreach (var g in sw.Groups)
                    if (g.License == c.Lic.Hash && g.Position.sqrMagnitude > 1f)
                    {
                        fwd = new Vector3(Mathf.Sin(g.Heading), 0f, Mathf.Cos(g.Heading));
                        return g.Position;
                    }
            StartFrame(c.Races[0], out var p, out fwd);
            return p;
        }

        [Tooltip("Lichtsäulen an den Meisterschafts-Treffpunkten (nicht im Original).")]
        public bool Beacons = false;

        void UpdateBeacons(Vector3 carIsland)
        {
            var root = Root;
            if (root == null) return;
            if (!Beacons)
            {
                foreach (var b in _beacons.Values) if (b != null) Destroy(b);
                _beacons.Clear();
                return;
            }
            var keep = new HashSet<string>();
            if (_phase == Phase.None)
                foreach (var kv in _hubs)
                {
                    var d = kv.Value.spot.Position - carIsland; d.y = 0f;
                    if (d.sqrMagnitude > BeaconDistance * BeaconDistance) continue;
                    keep.Add(kv.Key);
                    if (!_beacons.TryGetValue(kv.Key, out var go) || go == null)
                    {
                        go = MakeBeacon(kv.Key);
                        _beacons[kv.Key] = go;
                    }
                    if (go.transform.parent != root) go.transform.SetParent(root, false);
                    go.transform.localPosition = kv.Value.spot.Position;
                }
            foreach (var k in new List<string>(_beacons.Keys))
            {
                if (keep.Contains(k)) continue;
                if (_beacons[k] != null) Destroy(_beacons[k]);
                _beacons.Remove(k);
            }
        }

        GameObject MakeBeacon(string code)
        {
            if (_beaconMesh == null) _beaconMesh = BeamMesh(5f, 70f);
            if (_beaconMat == null)
            {
                var src = RaceGateVisual.SharedMaterial();
                _beaconMat = src != null ? new Material(src) : new Material(Shader.Find("Sprites/Default"));
                if (_beaconMat.HasProperty("_Tint")) _beaconMat.SetColor("_Tint", new Color(1f, 0.55f, 0.12f, 1f));
                else _beaconMat.color = new Color(1f, 0.55f, 0.12f, 0.8f);
            }
            var go = new GameObject("Meisterschaft " + code);
            go.AddComponent<MeshFilter>().sharedMesh = _beaconMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _beaconMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.layer = 2;
            return go;
        }

        /// <summary>Lichtsäule: zwei gekreuzte Flächen, unten kräftig, nach oben ausblendend (Vertexfarbe).</summary>
        static Mesh BeamMesh(float w, float h)
        {
            var m = new Mesh { name = "CupBeacon" };
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<Color>(); var tri = new List<int>();
            for (int q = 0; q < 2; q++)
            {
                var axis = q == 0 ? Vector3.right : Vector3.forward;
                int b = v.Count;
                v.Add(-axis * w * 0.5f); v.Add(axis * w * 0.5f); v.Add(axis * w * 0.5f + Vector3.up * h); v.Add(-axis * w * 0.5f + Vector3.up * h);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                col.Add(new Color(1, 1, 1, 0.9f)); col.Add(new Color(1, 1, 1, 0.9f)); col.Add(new Color(1, 1, 1, 0f)); col.Add(new Color(1, 1, 1, 0f));
                tri.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2, b, b + 1, b + 2, b, b + 2, b + 3 });   // beidseitig
            }
            m.SetVertices(v); m.SetUVs(0, uv); m.SetColors(col); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ Update

        void Update()
        {
            if (!EnsureData()) return;
            if (Time.unscaledTime >= _nextRefresh) { _nextRefresh = Time.unscaledTime + 2f; RefreshSpots(); }
            var veh = Man.Current;
            var root = Root;
            Vector3 carIsland = veh != null && root != null ? root.InverseTransformPoint(veh.transform.position) : Vector3.zero;
            UpdateBeacons(carIsland);

            switch (_phase)
            {
                case Phase.None: UpdateNear(veh, carIsland); break;
                case Phase.Racing: UpdateRacing(carIsland); break;
                case Phase.Speed: UpdateSpeed(veh); break;
                case Phase.Results: UpdateResults(); break;
            }
        }

        void UpdateNear(TDU2Vehicle veh, Vector3 carIsland)
        {
            _near = default;
            if (_phoneUntil > 0f && Time.unscaledTime >= _phoneUntil) { _phoneUntil = -1f; HidePhone(); }
            if (veh == null || Time.timeScale <= 0f || RacingSchoolMenu.BlocksPauseMenu) { _nearHub = null; HubPrompt(null); return; }
            var inter = Man.GetComponent<TDU2Interiors>();
            if (inter != null && inter.Inside) { _nearHub = null; HubPrompt(null); return; }
            float best = TriggerRadius * TriggerRadius;
            _nearHub = null;
            foreach (var kv in _hubs)
            {
                var d = kv.Value.spot.Position - carIsland; d.y *= 0.3f;
                if (d.sqrMagnitude < best * 1.6f) { best = d.sqrMagnitude; _nearHub = kv.Value.cup; }
            }
            HubPrompt(veh);
            if (_nearHub == null) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Mathf.Abs(veh.SpeedKmh) <= TriggerMaxKmh && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                TDU2DebugLog.Event("Meisterschaft: Enter am Treffpunkt " + _nearHub.Name + (TDU2Minimap.ExtraEnterLocked ? " – falsche Fahrzeugklasse (" + RaceDatabase.CategoryName(CarCategory(veh.Car)) + ")" : " – öffne Wettbewerb"));
                if (!TDU2Minimap.ExtraEnterLocked)   // falsche Klasse: Einfahrt zeigt "Gesperrt"
                {
                    var cup = _nearHub;
                    _nearHub = null; HubPrompt(null);
                    // wie im Spiel: Treffpunkt -> große Karte im Wettbewerbsmodus mit den Rennen der Meisterschaft
                    var map = Man.GetComponent<TDU2BigMap>();
                    if (map != null && map.OpenContest(_cups.IndexOf(cup), true)) return;
                    var first = cup.Races.Find(r => !RaceProgress.IsCupRaceDone(cup.Lic.Hash, r.Name)) ?? cup.Races[0];
                    BeginRace(cup, first);
                }
            }
#endif
        }

        // ------------------------------------------------------------------ Daten für die große Karte (Wettbewerbsmodus)

        /// <summary>Meisterschaft für die Karte (map.vmf: Spot-Symbol, Tooltip setContests, Titel).</summary>
        public sealed class MapCup
        {
            public int Index;
            public string Code, Name, Label, ContestType, Picture, ClassName, Group;
            public bool Elite, Done;
            public Vector3 Hub;                 // Inselkoordinaten
            public int RaceCount, RacesDone, Points, Place;
            public long RaceReward, ChampReward;
        }

        /// <summary>Rennen einer Meisterschaft: State 0 gesperrt, 1 offen, 2 gefahren (map.vmf chall&lt;i&gt;_state).</summary>
        public sealed class MapRace
        {
            public int Index;
            public string Name, Title, Label, ModeName;
            public int State, Laps, Points, Place;
            public float LengthM;
            public Vector3 Start, Forward;
        }

        public int CupCount => EnsureData() ? _cups.Count : 0;

        /// <summary>Freigeschaltete Meisterschaften der aktuellen Insel.</summary>
        public List<MapCup> MapCups()
        {
            var list = new List<MapCup>();
            if (!EnsureData()) return list;
            for (int i = 0; i < _cups.Count; i++)
            {
                var c = _cups[i];
                if (!IsAvailable(c)) continue;
                list.Add(MapCupAt(i));
            }
            return list;
        }

        public MapCup MapCupAt(int index)
        {
            if (!EnsureData() || index < 0 || index >= _cups.Count) return null;
            var c = _cups[index];
            var m = new MapCup
            {
                Index = index,
                Code = HubCode(c),
                Name = c.Name,
                Label = HubLabel(c),
                ContestType = HubKind(c),
                Picture = ContestPicture(c),
                ClassName = c.ClassName,
                Group = c.ClassName,
                Elite = c.Elite,
                Done = RaceProgress.IsCupDone(c.Lic.Hash),
                Hub = HubPosition(c, out _),
                RaceCount = c.Races.Count,
                RaceReward = c.Lic.RaceMoneyReward,
                ChampReward = c.Lic.ChampMoneyReward,
            };
            var total = CupTotals(c, out int raced);
            m.RacesDone = raced; m.Points = total[0];
            m.Place = 1; for (int k = 1; k < total.Length; k++) if (total[k] > total[0]) m.Place++;
            return m;
        }

        public List<MapRace> MapRaces(int index)
        {
            var list = new List<MapRace>();
            if (!EnsureData() || index < 0 || index >= _cups.Count) return list;
            var c = _cups[index];
            for (int i = 0; i < c.Races.Count; i++)
            {
                var r = c.Races[i];
                StartFrame(r, out var sp, out var f);
                var pts = RaceProgress.CupRacePoints(c.Lic.Hash, r.Name);
                list.Add(new MapRace
                {
                    Index = i,
                    Name = r.Name,
                    Title = RaceName(r),
                    Label = ModeLabel(r),
                    ModeName = ModeName(r),
                    State = RaceProgress.IsCupRaceDone(c.Lic.Hash, r.Name) ? 2 : 1,
                    Laps = Mathf.Max(1, r.Laps),
                    Points = pts != null && pts.Length > 0 ? pts[0] : 0,
                    Place = PlaceOf(pts),
                    LengthM = r.Length > 1f ? r.Length : 0f,
                    Start = sp,
                    Forward = f,
                });
            }
            return list;
        }

        /// <summary>Gesamtwertung (Name, Punkte, Spieler?) absteigend.</summary>
        public List<(string name, int points, bool player)> MapStandings(int index)
        {
            var list = new List<(string, int, bool)>();
            if (!EnsureData() || index < 0 || index >= _cups.Count) return list;
            var c = _cups[index];
            var total = CupTotals(c, out _);
            list.Add((TDU2.Map.Online.TDU2Session.Nick ?? "Du", total[0], true));
            for (int i = 0; i < c.Lic.Bots.Count && i + 1 < total.Length; i++)
            {
                int pnj = c.Lic.Bots[i].pnj;
                string nm = _repo.Database.PnjNames.TryGetValue(pnj, out var n) && !string.IsNullOrEmpty(n) ? n : "Fahrer " + (i + 1);
                list.Add((nm, total[i + 1], false));
            }
            list.Sort((a, b) => b.Item2 != a.Item2 ? b.Item2.CompareTo(a.Item2) : (a.Item3 ? -1 : b.Item3 ? 1 : 0));
            return list;
        }

        static int PlaceOf(int[] pts)
        {
            if (pts == null || pts.Length == 0) return 0;
            int p = 1; for (int i = 1; i < pts.Length; i++) if (pts[i] > pts[0]) p++;
            return p;
        }

        int[] CupTotals(Cup c, out int raced)
        {
            var total = new int[c.Lic.Bots.Count + 1];
            raced = 0;
            foreach (var r in c.Races)
            {
                var p = RaceProgress.CupRacePoints(c.Lic.Hash, r.Name);
                if (p == null) continue;
                raced++;
                for (int i = 0; i < p.Length && i < total.Length; i++) total[i] += p[i];
            }
            return total;
        }

        /// <summary>Name des Rivalen (DBLicense._rival).</summary>
        public string RivalName(int index)
        {
            if (!EnsureData() || index < 0 || index >= _cups.Count) return null;
            int r = _cups[index].Lic.Rival;
            return r != 0 && _repo.Database.PnjNames.TryGetValue(r, out var n) ? n : null;
        }

        /// <summary>Rennen aus der Karte starten (Teleport an den Start, Startaufstellung).</summary>
        public void StartFromMap(int cupIndex, int raceIndex)
        {
            if (_phase != Phase.None) { Toast("Erst das laufende Rennen beenden"); return; }
            BeginRace(cupIndex, raceIndex);
        }

        /// <summary>Meisterschaft aufgeben: Punkte/Ergebnisse zurücksetzen (map.vmf "giup").</summary>
        public void GiveUp(int cupIndex)
        {
            if (!EnsureData() || cupIndex < 0 || cupIndex >= _cups.Count) return;
            RaceProgress.ResetCup(_cups[cupIndex].Lic);
            _nextRefresh = 0f;
            Toast(_cups[cupIndex].Name + ": aufgegeben – Wertung zurückgesetzt");
        }

        // ------------------------------------------------------------------ Treffpunkt-Hinweis: Einfahrt (/HUD/enter) + Handy-Nachricht
        Cup _promptCup;

        void HubPrompt(TDU2Vehicle veh)
        {
            var mm = Man != null ? Man.Minimap : null;
            var c = _nearHub;
            if (c == null)
            {
                if (_promptCup != null)
                {
                    _promptCup = null;
                    TDU2Minimap.ExtraEnterCode = null;
                    if (mm != null) mm.ShowEnter(null);
                    if (_phoneUntil < 0f) HidePhone();
                }
                return;
            }
            int cat = CarCategory(veh != null ? veh.Car : null);
            bool wrongCar = RequireCarCategory && c.Lic.VehicleCategory1 > 0 && cat != c.Lic.VehicleCategory1 && cat != c.Lic.VehicleCategory2;
            TDU2Minimap.ExtraEnterCode = HubCode(c);
            TDU2Minimap.ExtraEnterLocked = wrongCar;
            if (mm != null) mm.ShowEnter(null);   // nimmt ExtraEnterCode
            int doneN = 0; foreach (var rr in c.Races) if (RaceProgress.IsCupRaceDone(c.Lic.Hash, rr.Name)) doneN++;
            string text = c.Name + "\r" + "Klasse " + c.ClassName + (c.Elite ? " (Elite)" : "") + " – " + doneN + "/" + c.Races.Count + " Rennen\r" +
                          (wrongCar ? "Nur mit einem Auto der Klasse " + c.ClassName : "[Enter] Meisterschaft öffnen");
            _ = text;
            _promptCup = c;
        }

        static string ClassNumber(Cup c)
        {
            string n = c.ClassName ?? "";
            int i = n.Length; while (i > 0 && char.IsDigit(n[i - 1])) i--;
            return i < n.Length ? n.Substring(i) : "";
        }

        // ------------------------------------------------------------------ Start

        /// <summary>Startpunkt und -richtung (Inselkoordinaten) wie in der Rennschule: Startlinie, ggf. zum ersten Ziel gedreht.</summary>
        static void StartFrame(RaceDefinition def, out Vector3 start, out Vector3 fwd)
        {
            start = def.StartPosition;
            Vector3 f = def.Start != null ? def.Start.Forward : new Vector3(Mathf.Sin(def.StartHeading), 0f, Mathf.Cos(def.StartHeading));
            Vector3 aim = Vector3.zero;
            foreach (int k in def.TargetSequence()) { var d = def.Checkpoints[k].Position - start; d.y = 0f; if (d.sqrMagnitude > 225f) { aim = d; break; } }
            if (aim != Vector3.zero && Vector3.Angle(new Vector3(f.x, 0f, f.z), aim) > 100f) f = -f;
            f.y = 0f;
            fwd = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        void Toast(string msg, float seconds = 4f)
        {
            _toast = msg; _toastUntil = Time.unscaledTime + seconds; Debug.Log("[TDU2] " + msg);
        }

        // ------------------------------------------------------------------ Handy-Nachricht (Original-HUD /HUD/phone_call/phone_message)
        bool _phoneOpen; string _phoneText; float _phoneUntil = -1f;

        void ShowPhone(string title, string text)
        {
            var h = Man != null && Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            if (h == null) return;
            if (_phoneOpen && _phoneText == text) return;
            h.SetVariable("/:dialog_pnj", title ?? "");
            h.SetVariable("/:dialog_ph", text ?? "");
            if (!_phoneOpen)
            {
                h.SetVisible("/HUD/phone_call", true);
                h.GotoLabel("/HUD/phone_call", "open", true);
                h.GotoLabel("/HUD/phone_call/phone_message", "open", true);
            }
            h.GotoLabel("/HUD/phone_call/phone_message/help", "A", false);
            _phoneOpen = true; _phoneText = text; _phoneUntil = -1f;
        }

        void HidePhone()
        {
            if (!_phoneOpen) return;
            var h = Man != null && Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            if (h != null) { h.GotoLabel("/HUD/phone_call/phone_message", "close", true); h.GotoLabel("/HUD/phone_call", "default", false); }
            _phoneOpen = false; _phoneText = null;
        }

        int CarCategory(CarPhysics c) => c != null && c.Raw != null ? c.Raw.I("_vehiclecategory") : 0;

        public void BeginRace(int cupIndex, int raceIndex)
        {
            if (!EnsureData() || cupIndex < 0 || cupIndex >= _cups.Count) return;
            var c = _cups[cupIndex];
            if (raceIndex < 0 || raceIndex >= c.Races.Count) return;
            BeginRace(c, c.Races[raceIndex]);
        }

        void BeginRace(Cup cup, RaceDefinition def)
        {
            var veh = Man.Current;
            if (veh == null) return;
            int cat = CarCategory(veh.Car);
            if (RequireCarCategory && cup.Lic.VehicleCategory1 > 0 && cat != cup.Lic.VehicleCategory1 && cat != cup.Lic.VehicleCategory2)
            {
                Toast(cup.Name + ": nur mit einem Auto der Klasse " + cup.ClassName + " (dein Auto: " + RaceDatabase.CategoryName(cat) + ")", 5f);
                return;
            }
            _cup = cup; _def = def;
            EnsureRaceObjects();
            _autopilot.End();
            _race.Cleanup();
            SetPrivate(true);

            // Original-Werte merken (werden je Spielart angepasst)
            if (!_orig.ContainsKey(def.Name)) _orig[def.Name] = (def.TargetTimeMs, def.TimeLimitMs, def.HasTimeLimit);
            var o = _orig[def.Name];
            def.TargetTimeMs = o.target; def.TimeLimitMs = o.limit; def.HasTimeLimit = o.hasLimit;
            if (def.Kind == RaceMode.Race || def.Kind == RaceMode.SpeedTrap) { def.TargetTimeMs = 0; def.TimeLimitMs = 0; def.HasTimeLimit = false; }

            if (def.RoadPath == null) { try { RaceRoadPath.Compute(def); } catch (Exception ex) { Debug.LogWarning("[TDU2] Straßenroute: " + ex.Message); } }
            if (def.RoadPath == null || def.RoadPath.Count < 2) def.RoadPath = new List<Vector3> { def.StartPosition, def.Checkpoints[def.Checkpoints.Count - 1].Position };
            RaceRoute.RoadPath = def.Kind == RaceMode.Speed ? null : def.RoadPath;
            RaceRoute.RoadPathSeg = def.Kind == RaceMode.Speed ? null : def.RoadPathSeg;
            BuildCum(def.RoadPath);

            StartFrame(def, out var start, out var fwd);
            var right = new Vector3(fwd.z, 0f, -fwd.x);
            def.Opponents.Clear();
            int playerSlot = 0;
            if (def.Kind == RaceMode.Race || def.Kind == RaceMode.SpeedTrap) playerSlot = BuildGrid(cup, def, start, fwd, right);
            // Spielerauto in die Startaufstellung (hinterste Reihe) bzw. an die Startlinie
            int row = playerSlot / 2; float side = playerSlot == 0 ? 0f : (playerSlot % 2 == 0 ? -2.2f : 2.2f);
            var pIsl = start - fwd * (row * GridRowSpacing) + right * side;
            var root = Root;
            var pw = root.TransformPoint(pIsl);
            pw.y = Man.GroundHeight(pw) + 0.4f;
            var wf = root.TransformVector(fwd); wf.y = 0f;
            veh.ResetVehicle(pw, Quaternion.LookRotation(wf.sqrMagnitude > 1e-6f ? wf.normalized : Vector3.forward, Vector3.up));
            _pIdx = 0; _pProgress = -row * GridRowSpacing; _lastPos = -1;
            _playerRadar.Clear(); _cpDist.Clear();
            foreach (int k in def.TargetSequence()) _cpDist.Add(DistanceAlong(def.Checkpoints[k].Position));
            _rows = null; _resultAt = -1f;

            Debug.Log("[TDU2] Meisterschaft " + cup.Lic.ChallengeFiles[0].Substring(0, 5) + " – " + ModeName(def) + " " + def.Name + " (" + RaceName(def) + "): " +
                      def.Opponents.Count + " Gegner, Strecke " + (_finishLen / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km, " + (def.IsCircuit ? def.Laps + " Runden" : "Punkt-zu-Punkt"));

            if (def.Kind == RaceMode.Speed)
            {
                _phase = Phase.Speed;
                _speedT = 0f; _speedDist = 0f; _speedCountdown = 3.7f;
                Man.ExternalControl = true;   // im Countdown halten (Pedale setzt dieses Skript)
                var h = Man.Minimap != null ? Man.Minimap.HudPlayer : null;
                if (h != null) { TDU2FrontendSounds.Attach(h); h.SetVisible("/_3_2_1_go", true); h.GotoLabel("/_3_2_1_go", "3_go", true); }
                return;
            }
            if (!_race.StartRace(def)) { Toast("Rennen " + def.Name + " konnte nicht gestartet werden"); EndRace(); return; }
            _phase = Phase.Racing;
        }

        void EnsureRaceObjects()
        {
            if (_race != null) return;
            _raceGo = new GameObject("TDU2 Championship Race");
            _raceGo.transform.SetParent(transform, false);
            _raceGo.SetActive(false);
            _race = _raceGo.AddComponent<TDU2RaceManager>();
            _race.Streamer = Man.Streamer;
            _race.Vehicles = Man;
            _hud = _raceGo.AddComponent<RaceHud>();
            _hud.Race = _race;
            _hud.Minimap = Man.Minimap;
            _autopilot = _raceGo.AddComponent<RaceAutopilot>();
            // Ergebnis über den Original-Film results.vmf (wie die Rennschule)
            _results = _raceGo.AddComponent<RaceResults>();
            _results.Manager = Man;
            _results.Retry += () => AfterResults(1);
            _results.Next += () => AfterResults(2);
            _results.Quit += () => AfterResults(0);
            _race.Prepared += r =>
            {
                var opp = r.Opponents;
                if (opp != null) { opp.FinishDistance = _finishLen; opp.Clock = () => _race.ElapsedSeconds; opp.KeepDrivingAfterFinish = true; }
                _botRadar = null;
            };
            _race.Started += r => ShowPosition(true);
            _race.CheckpointPassed += (r, i) => { if (_def != null && _def.Kind == RaceMode.SpeedTrap && Man.Current != null) _playerRadar.Add(Mathf.Abs(Man.Current.SpeedKmh)); };
            _race.Completed += r => OnFinished(false);
            _race.Failed += r => OnFinished(true);
            _race.Aborted += r => { Toast("Rennen abgebrochen"); EndRace(); };
            _raceGo.SetActive(true);
        }

        /// <summary>Gegner der Lizenz in Zweierreihen vor dem Spieler aufstellen. Liefert den Startplatz des Spielers.</summary>
        int BuildGrid(Cup cup, RaceDefinition def, Vector3 start, Vector3 fwd, Vector3 right)
        {
            var db = Man.Database;
            int n = Mathf.Min(def.BotCount > 0 ? def.BotCount : 7, cup.Lic.Bots.Count);
            var extended = new List<Vector3>(def.RoadPath);
            var a = extended[extended.Count - 1]; var b = extended[Mathf.Max(0, extended.Count - 3)];
            var dir = a - b; dir.y = 0f;
            if (dir.sqrMagnitude > 1e-3f) extended.Add(a + dir.normalized * 250f);
            for (int i = 0; i < n; i++)
            {
                var (pnj, diff, _) = cup.Lic.Bots[i];
                var car = BotCar(pnj, cup.Lic, db);
                if (car == null) continue;
                int row = i / 2; float side = i % 2 == 0 ? -2.2f : 2.2f;
                var pos = start - fwd * (row * GridRowSpacing) + right * side;
                var path = new List<Vector3> { pos };
                // auf die Ideallinie: ab dem Punkt der Route, der vor dem Startplatz liegt
                int k = 0; while (k < extended.Count - 1 && Vector3.Dot(extended[k] - pos, fwd) < 2f) k++;
                for (; k < extended.Count; k++) path.Add(extended[k]);
                float top = Mathf.Max(30f, car.MaxTheoricalSpeed) / 3.6f;
                float grip = Mathf.Clamp(Mathf.Min(car.MaxLatFrictionFront, car.MaxLatFrictionRear), 1.1f, 2f);
                def.Opponents.Add(new RaceOpponentDef
                {
                    CarId = car.Id,
                    Name = db != null && _repo.Database.PnjNames.TryGetValue(pnj, out var nm) && !string.IsNullOrEmpty(nm) ? nm : "Fahrer " + (i + 1),
                    Position = pos,
                    Forward = fwd,
                    MaxSpeed = top * Mathf.Lerp(0.74f, 0.94f, diff) * BotSpeedScale,
                    LatAccel = 9.81f * grip * Mathf.Lerp(0.40f, 0.58f, diff),
                    Accel = (100f / 3.6f) / Mathf.Max(3f, car.Accel0To100) * Mathf.Lerp(0.8f, 1.05f, diff),
                    LateralOffset = side * 0.6f,
                    PathOverride = path,
                    StartDistance = -row * GridRowSpacing,
                    StartCheckpoint = 1,
                });
            }
            return def.Opponents.Count;
        }

        CarPhysics BotCar(int pnj, LicenseInfo lic, CarDatabase db)
        {
            if (db == null) return null;
            if (_repo.Database.PnjCars.TryGetValue(pnj, out var cars))
            {
                CarPhysics any = null;
                foreach (var id in cars)
                {
                    if (!db.ById.TryGetValue(id, out var c) || CarDatabase.CarBank(Man.BnkDir, c) == null) continue;
                    int cat = CarCategory(c);
                    if (cat == lic.VehicleCategory1 || (lic.VehicleCategory2 > 0 && cat == lic.VehicleCategory2)) return c;
                    if (any == null) any = c;
                }
                if (lic.VehicleCategory1 <= 0 && any != null) return any;
            }
            // Fallback: irgendein Auto der Klasse
            foreach (var c in db.Cars) if (CarCategory(c) == lic.VehicleCategory1 && CarDatabase.CarBank(Man.BnkDir, c) != null) return c;
            return null;
        }

        void BuildCum(List<Vector3> path)
        {
            _cum = new float[path.Count];
            for (int i = 1; i < path.Count; i++) { var d = path[i] - path[i - 1]; d.y = 0f; _cum[i] = _cum[i - 1] + d.magnitude; }
            _finishLen = _cum.Length > 0 ? _cum[_cum.Length - 1] : 0f;
        }

        float DistanceAlong(Vector3 p)
        {
            var path = _def.RoadPath; float best = float.MaxValue; int bi = 0;
            for (int i = 0; i < path.Count; i++) { var d = path[i] - p; d.y = 0f; float m = d.sqrMagnitude; if (m < best) { best = m; bi = i; } }
            return _cum[bi];
        }

        // ------------------------------------------------------------------ Laufendes Rennen

        void UpdateRacing(Vector3 carIsland)
        {
            if (_race == null || _def == null) return;
            if (!_race.Running) return;
            // Fortschritt des Spielers entlang der Route (Fenster um den letzten Punkt, Rundkurse wiederholen die Strecke)
            var path = _def.RoadPath;
            int lo = Mathf.Max(0, _pIdx - 15), hi = Mathf.Min(path.Count - 1, _pIdx + 80);
            float best = float.MaxValue; int bi = _pIdx;
            for (int i = lo; i <= hi; i++) { var d = path[i] - carIsland; d.y = 0f; float m = d.sqrMagnitude; if (m < best) { best = m; bi = i; } }
            _pIdx = bi;
            if (_pIdx == 0)
            {
                var d0 = carIsland - path[0]; d0.y = 0f;
                StartFrame(_def, out _, out var f0);
                _pProgress = Vector3.Dot(d0, f0);
            }
            else _pProgress = _cum[_pIdx];

            var opp = _race.Opponents;
            if (opp == null) return;
            var st = opp.Standings();
            if (_def.Kind == RaceMode.SpeedTrap) TrackBotRadar(st);
            if (_def.Kind != RaceMode.Race) return;
            int pos = 1;
            foreach (var s in st) if (s.Finished || s.Traveled > _pProgress) pos++;
            if (pos != _lastPos || st.Count + 1 != _lastTotal) { _lastPos = pos; _lastTotal = st.Count + 1; SetPosition(pos, st.Count + 1); }
            TDU2Minimap.RaceCount = st.Count + 1;
            TDU2Minimap.RaceBlips.Clear(); opp.WorldPositions(TDU2Minimap.RaceBlips);
            UpdateRaceHud(st, pos);
        }

        // ------------------------------------------------------------------ Renn-HUD: Namen vor/hinter mir, Abstand zum Führenden
        const float GapBin = 25f;                      // Zeitmessung alle 25 m Strecke
        readonly List<float> _binBot = new List<float>();     // früheste Gegnerzeit je Abschnitt
        readonly List<float> _binMe = new List<float>();      // meine Zeit je Abschnitt
        int[] _botNextBin;
        float[] _botLastBinT;
        List<float>[] _botBins;
        float _sideOnAt;
        bool _versusOpen;
        float _hudNext;
        string _hudBefore = "-", _hudAfter = "-";
        int _gapSign;

        void ResetRaceHud()
        {
            _binBot.Clear(); _binMe.Clear(); _botNextBin = null; _botLastBinT = null; _botBins = null; _versusOpen = false; _hudBefore = "-"; _hudAfter = "-"; _gapSign = 0; _hudNext = 0f;
        }

        void UpdateRaceHud(List<RaceOpponents.Standing> st, int pos)
        {
            var h = Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            if (h == null) return;
            float t = _race.ElapsedSeconds;
            // Abschnittszeiten
            if (_botNextBin == null || _botNextBin.Length != st.Count)
            {
                _botNextBin = new int[st.Count]; _botLastBinT = new float[st.Count];
                _botBins = new List<float>[st.Count]; for (int i = 0; i < st.Count; i++) _botBins[i] = new List<float>();
            }
            float? gap = null;
            for (int i = 0; i < st.Count; i++)
                while ((_botNextBin[i] + 1) * GapBin <= st[i].Traveled)
                {
                    int k = _botNextBin[i]++;
                    _botLastBinT[i] = t; _botBins[i].Add(t);
                    while (_binBot.Count <= k) _binBot.Add(-1f);
                    if (_binBot[k] < 0f)
                    {
                        _binBot[k] = t;
                        // ich war vorne: Vorsprung auf den Zweiten, sobald der erste Gegner den Abschnitt erreicht
                        if (k < _binMe.Count && k == _binMe.Count - 1) gap = _binMe[k] - t;
                    }
                }
            while ((_binMe.Count + 1) * GapBin <= _pProgress)
            {
                int k = _binMe.Count;
                _binMe.Add(t);
                if (k < _binBot.Count && _binBot[k] >= 0f) gap = t - _binBot[k];   // Rückstand auf den Führenden
            }
            if (gap.HasValue) ShowGap(h, gap.Value);
            if (Time.unscaledTime < _hudNext) return;
            _hudNext = Time.unscaledTime + 0.2f;
            // Reihenfolge (Ziel zuerst, dann nach Weg) für die Namen vor/hinter mir (-1 = ich)
            var order = new List<(string name, float key, int bot)>();
            for (int i = 0; i < st.Count; i++) order.Add((st[i].Name ?? "?", st[i].Finished ? 1e9f - st[i].FinishTime : st[i].Traveled, i));
            order.Add((TDU2.Map.Online.TDU2Session.Nick ?? "Du", _pProgress, -1));
            order.Sort((a, b) => b.key.CompareTo(a.key));
            int me = order.FindIndex(o => o.bot < 0);
            // Abstand zu Vorder-/Hintermann (Sekunden) über die 25-m-Abschnitte
            string GapText(int bot, bool ahead)
            {
                if (bot < 0 || _botBins == null || bot >= _botBins.Length) return "";
                var bb = _botBins[bot];
                int k = ahead ? _binMe.Count - 1 : bb.Count - 1;
                if (k < 0 || k >= bb.Count || k >= _binMe.Count) return "";
                float g = ahead ? _binMe[k] - bb[k] : bb[k] - _binMe[k];
                if (g < 0.05f) return "";
                return "  " + (ahead ? "-" : "+") + g.ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + "s";
            }
            h.SetVariable("/HUD/position/player:PlayerName", TDU2.Map.Online.TDU2Session.Nick ?? "Du");
            h.SetVariable("/:PosPlayer", pos.ToString(CultureInfo.InvariantCulture)); h.SetVariable("/:ExpPlayer", ".");
            string before = me > 0 ? order[me - 1].name + GapText(order[me - 1].bot, true) : null;
            string after = me >= 0 && me + 1 < order.Count ? order[me + 1].name + GapText(order[me + 1].bot, false) : null;
            h.SetVariable("/HUD/position/player_before:PlyrNameBefore", before ?? "");
            h.SetVariable("/:PosBefore", (pos - 1).ToString(CultureInfo.InvariantCulture)); h.SetVariable("/:ExpBefore", ".");
            h.SetVariable("/HUD/position/player_after:PlyrNameAfter", after ?? "");
            h.SetVariable("/:PosAfter", (pos + 1).ToString(CultureInfo.InvariantCulture)); h.SetVariable("/:ExpAfter", ".");
            // Platzliste rechts unter der Zeit (Original-Liste /HUD/list_versus, neutrale Zeilen, eigene Zeile hervorgehoben)
            UpdateVersusList(h, st);
        }

        void KeepSide(VmfPlayer h, string path, bool show)
        {
            var d = h.Find(path);
            var inst = d?.Sprite;
            if (inst == null) return;
            int f = inst.Frame;
            if (show)
            {
                if (f <= 0 || f >= 120) h.GotoLabel(path, "on", true);          // einblenden
                else if (f >= 90) h.GotoFrameNumber(path, 60, false);           // sichtbar halten
            }
            else if (f > 0 && f < 120) h.GotoLabel(path, "off", true);
        }

        /// <summary>Liste der Fahrer rechts (Original /HUD/list_versus: line0..7, Name + Rückstand auf den Führenden).</summary>
        void UpdateVersusList(VmfPlayer h, List<RaceOpponents.Standing> st)
        {
            float LeaderAt(int bin)
            {
                float lt = float.MaxValue;
                if (bin >= 0 && bin < _binBot.Count && _binBot[bin] >= 0f) lt = _binBot[bin];
                if (bin >= 0 && bin < _binMe.Count) lt = Mathf.Min(lt, _binMe[bin]);
                return lt;
            }
            var rows = new List<(string name, float key, float gap, bool me)>();
            for (int i = 0; i < st.Count; i++)
            {
                int bin = _botNextBin != null && i < _botNextBin.Length ? _botNextBin[i] - 1 : -1;
                float lt = LeaderAt(bin);
                float gap = bin >= 0 && lt < float.MaxValue ? _botLastBinT[i] - lt : 0f;
                rows.Add((st[i].Name ?? "?", st[i].Finished ? 1e9f - st[i].FinishTime : st[i].Traveled, gap, false));
            }
            {
                int bin = _binMe.Count - 1;
                float lt = LeaderAt(bin);
                float gap = bin >= 0 && lt < float.MaxValue ? _binMe[bin] - lt : 0f;
                rows.Add((TDU2.Map.Online.TDU2Session.Nick ?? "Du", _pProgress, gap, true));
            }
            rows.Sort((x, y) => y.key.CompareTo(x.key));
            if (!_versusOpen)
            {
                _versusOpen = true;
                h.SetVisible("/HUD/list_versus", true);
                h.GotoLabel("/HUD/list_versus", "open", true);
            }
            for (int i = 0; i < 8; i++)
            {
                string line = "/HUD/list_versus/line" + i;
                if (i >= rows.Count) { h.GotoLabel(line + "/content", "empty", false); continue; }
                var r = rows[i];
                string txt = r.name + (i > 0 && r.gap > 0.05f ? "  +" + r.gap.ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) : "");
                bool changed = h.GetVariable(line + ":player_name") != txt;
                h.SetVariable(line + ":player_name", txt);
                h.SetVariable(line + ":pos", (i + 1).ToString(CultureInfo.InvariantCulture));
                string lab = r.me ? "blue_on" : "default";
                if (h.Label(line + "/content") != lab) h.GotoLabel(line + "/content", lab, false);
                if (h.Label(line) == "default" || h.Label(line) == null) h.GotoLabel(line, "open", true);
                if (changed) h.CallClip(line + "/content/p", "SetTxtScroll");
            }
        }

        /// <summary>Abstand (s): positiv = Rückstand auf den Führenden, negativ = Vorsprung auf den Zweiten (ref_score, Karte "time").</summary>
        void ShowGap(VmfPlayer h, float gap)
        {
            int ms = Mathf.RoundToInt(Mathf.Abs(gap) * 1000f);
            h.SetVariable("/:MinRef", (gap < 0f ? "-" : "+") + (ms / 60000).ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:SecRef", ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture));
            h.SetVariable("/:CentRef", ((ms / 10) % 100).ToString("00", CultureInfo.InvariantCulture));
            int sign = gap < 0f ? -1 : 1;
            if (sign != _gapSign)
            {
                if (_gapSign == 0) { h.SetVisible("/HUD/ref_score", true); h.GotoLabel("/HUD/ref_score", "open", true); }
                else h.GotoLabel("/HUD/ref_score", sign < 0 ? "up" : "down", true);
                h.GotoLabel("/HUD/ref_score/cart", "time", false);
                _gapSign = sign;
            }
        }

        void TrackBotRadar(List<RaceOpponents.Standing> st)
        {
            if (_botRadar == null || _botRadar.Length != st.Count)
            {
                _botRadar = new float[st.Count][]; _botRadarNext = new int[st.Count];
                for (int i = 0; i < st.Count; i++) _botRadar[i] = new float[_cpDist.Count];
            }
            for (int i = 0; i < st.Count; i++)
                while (_botRadarNext[i] < _cpDist.Count && st[i].Traveled >= _cpDist[_botRadarNext[i]])
                    _botRadar[i][_botRadarNext[i]++] = st[i].Speed * 3.6f;
        }

        void ShowPosition(bool on)
        {
            var h = Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            if (h == null || _def == null) return;
            if (on && _def.Kind == RaceMode.Race)
            {
                h.SetVariable("/:cur_gm", "race");
                h.SetVisible("/HUD/position", true);
                h.GotoLabel("/HUD/position", "open", true);
                h.GotoLabel("/HUD/position/player", "default", false);
                h.GotoLabel("/HUD/position/player_before", "default", false);
                h.GotoLabel("/HUD/position/player_after", "default", false);
                _lastPos = -1;
                ResetRaceHud();
            }
            else
            {
                h.GotoLabel("/HUD/position", "default", false);
                h.GotoLabel("/HUD/ref_score", "default", false);
                h.GotoLabel("/HUD/list_versus", "default", false);
                TDU2Minimap.RaceCount = 0; TDU2Minimap.RaceBlips.Clear();
                ResetRaceHud();
            }
        }

        void SetPosition(int pos, int total)
        {
            var h = Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            if (h == null) return;
            h.SetVariable("/:Pos", pos.ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:Exp", ".");
            h.SetVariable("/:NbPlayers", total.ToString(CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ Tempo (Durchschnitt über die Zeit)

        void UpdateSpeed(TDU2Vehicle veh)
        {
            if (veh == null) { EndRace(); return; }
            float dt = Time.deltaTime;
            var h = Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            float limit = Mathf.Max(10f, _orig[_def.Name].limit / 1000f);
            if (_speedCountdown > 0f)
            {
                _speedCountdown -= Time.unscaledDeltaTime;
                veh.BrakePedal = 1f; veh.HandBrake = 1f;
                if (_speedCountdown <= 0f)
                {
                    Man.ExternalControl = false;
                    veh.BrakePedal = 0f; veh.HandBrake = 0f;
                    if (h != null)
                    {
                        h.SetVisible("/HUD/time_cd", true); h.GotoLabel("/HUD/time_cd", "open", true);
                        // Tempo-Prüfung: Original-Anzeige speed_gauge (Wert + Mindest-/aktuelles Tempo, Balken = Zeit)
                        h.SetVisible("/HUD/speed_gauge", true); h.GotoLabel("/HUD/speed_gauge", "open", true);
                        h.SetVariable("/:m_Pts", "km/h Ø"); h.SetVariable("/:m_SpeedUnit", "km/h");
                        _gaugeBase = -1f;
                    }
                }
                return;
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Backspace)) { Toast("Rennen abgebrochen"); EndRace(); return; }
#endif
            _speedT += dt;
            _speedDist += Mathf.Abs(veh.SpeedKmh) / 3.6f * dt;
            if (h != null)
            {
                float avgK = _speedT > 0.1f ? _speedDist / _speedT * 3.6f : 0f;
                h.SetVariable("/:SpeedScore", avgK.ToString("0", CultureInfo.InvariantCulture));
                h.SetVariable("/:speed_min", Mathf.Abs(veh.SpeedKmh).ToString("0", CultureInfo.InvariantCulture));
                var comp = h.Find("/HUD/speed_gauge/gauge/completion");
                if (comp != null)
                {
                    if (_gaugeBase < 0f) _gaugeBase = Mathf.Abs(comp.Matrix.A) > 1e-4f ? comp.Matrix.A : 1f;
                    comp.Matrix.A = _gaugeBase * Mathf.Clamp01(_speedT / limit); comp.ScriptMatrix = true;
                }
                float left = Mathf.Max(0f, limit - _speedT);
                int ms = (int)(left * 1000f);
                h.SetVariable("/:MinCd", (ms / 60000).ToString(CultureInfo.InvariantCulture));
                h.SetVariable("/:SecCd", ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture));
                h.SetVariable("/:CentCd", ((ms / 10) % 100).ToString("00", CultureInfo.InvariantCulture));
            }
            if (_speedT >= limit)
            {
                if (h != null) { h.GotoLabel("/HUD/time_cd", "default", false); h.GotoLabel("/HUD/speed_gauge", "default", false); h.GotoLabel("/_3_2_1_go", "default", false); h.SetVisible("/_3_2_1_go", false); }
                OnFinished(false);
            }
        }

        // ------------------------------------------------------------------ Ergebnis

        void OnFinished(bool failed)
        {
            ShowPosition(false);
            var veh = Man.Current;
            if (veh != null) _autopilot.Begin(Man);
            _rows = BuildRows(failed);
            _filmTried = false; _filmShown = false;
            int[] pts = new int[1 + _cup.Lic.Bots.Count];
            foreach (var r in _rows)
            {
                r.Points = r.Place - 1 < PointsTable.Length ? PointsTable[r.Place - 1] : 0;
                if (r.Player) pts[0] = r.Points; else if (r.BotIndex >= 0 && r.BotIndex + 1 < pts.Length) pts[r.BotIndex + 1] = r.Points;
            }
            RaceProgress.SetCupRacePoints(_cup.Lic.Hash, _def.Name, pts);
            var me = _rows.Find(r => r.Player);
            _resultTitle = ModeName(_def) + ": " + RaceName(_def);
            _resultInfo = failed ? "Zeit abgelaufen" : "Platz " + me.Place + " von " + _rows.Count + " – " + me.Points + " Punkte";
            // Preisgeld des Rennens (DBLicense._racemoneyreward) nach Platz; bei Wiederholung nur die Verbesserung
            if (!failed)
            {
                long prize = PrizeFor(me.Place, _cup.Lic.RaceMoneyReward);
                long paid = RaceProgress.CupPaid(_cup.Lic.Hash, _def.Name);
                if (prize > paid && AddMoney(prize - paid)) { RaceProgress.SetCupPaid(_cup.Lic.Hash, _def.Name, prize); _resultInfo += "   +" + Money(prize - paid); }
            }
            _cupInfo = CupStanding(out bool done, out int place);
            if (done && !RaceProgress.IsCupDone(_cup.Lic.Hash))
            {
                RaceProgress.MarkCupDone(_cup.Lic.Hash, place);
                bool hasElite = _cups.Exists(c => c.Lic.VehicleCategory1 == _cup.Lic.VehicleCategory1 && c.Lic.Order == _cup.Lic.Order + 1);
                _cupInfo += "\nMeisterschaft abgeschlossen – Gesamtplatz " + place + (hasElite ? ". Die Elite-Meisterschaft ist freigeschaltet!" : ".");
                // Meisterschafts-Preisgeld (DBLicense._champmoneyreward) für den Gesamtsieg
                if (place == 1 && _cup.Lic.ChampMoneyReward > 0 && AddMoney(_cup.Lic.ChampMoneyReward))
                    _cupInfo += " Preisgeld Meisterschaft: +" + Money(_cup.Lic.ChampMoneyReward);
            }
            Debug.Log("[TDU2] Meisterschaftsrennen " + _def.Name + ": " + _resultInfo + " | " + _cupInfo.Replace("\n", " | "));
            _phase = Phase.Results;
            _resultAt = Time.unscaledTime + 1.2f;
            _nextRefresh = 0f;
        }

        [Tooltip("Anteil des Renn-Preisgelds je Platz (1., 2., 3. ...). [vermutet]")]
        public float[] PrizeShare = { 1f, 0.5f, 0.25f };

        long PrizeFor(int place, int reward) => place >= 1 && place <= PrizeShare.Length ? (long)Mathf.Round(reward * PrizeShare[place - 1]) : 0;

        static string Money(long v) => v.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " $";

        /// <summary>Geld auf den Spielstand (Server) buchen. Ohne Anmeldung kein Geld.</summary>
        static bool AddMoney(long amount)
        {
            if (amount <= 0 || !TDU2.Map.Online.TDU2Session.LoggedIn || TDU2.Map.Online.TDU2Session.Save == null) return false;
            TDU2.Map.Online.TDU2Session.Save.money += amount;
            TDU2.Map.Online.TDU2Session.SaveAsync();
            TDU2DebugLog.Event("Preisgeld +" + amount + " $ -> " + TDU2.Map.Online.TDU2Session.Save.money + " $");
            return true;
        }

        List<Row> BuildRows(bool failed)
        {
            var rows = new List<Row>();
            var opp = _race != null ? _race.Opponents : null;
            var st = opp != null ? opp.Standings() : new List<RaceOpponents.Standing>();
            var me = new Row { Name = TDU2.Map.Online.TDU2Session.Nick ?? "Du", Car = Man.Current?.Car?.ModelName ?? "", Player = true, Finished = !failed };
            rows.Add(me);
            switch (_def.Kind)
            {
                case RaceMode.Race:
                    {
                        float now = _race.ElapsedSeconds;
                        me.Value = failed ? float.MaxValue : _race.Result != null ? _race.Result.FinalTimeMs / 1000f : now;
                        for (int i = 0; i < st.Count; i++)
                        {
                            var s = st[i];
                            float t = s.Finished ? s.FinishTime : now + Mathf.Max(0f, _finishLen - s.Traveled) / Mathf.Max(8f, s.Def.MaxSpeed * 0.7f);
                            rows.Add(new Row { Name = s.Name, Car = s.Car, Value = t, Finished = s.Finished, BotIndex = i });
                        }
                        rows.Sort((a, b) => a.Value.CompareTo(b.Value));
                        break;
                    }
                case RaceMode.SpeedTrap:
                    {
                        float sum = 0f; foreach (var v in _playerRadar) sum += v;
                        me.Value = failed ? 0f : sum;
                        if (st.Count > 0)
                        {
                            if (_botRadar == null) TrackBotRadar(st);
                            for (int i = 0; i < st.Count; i++)
                            {
                                float s2 = 0f;
                                for (int k = 0; k < _cpDist.Count; k++) s2 += _botRadar[i][k] > 0f ? _botRadar[i][k] : st[i].Def.MaxSpeed * 3.6f * 0.75f;
                                rows.Add(new Row { Name = st[i].Name, Car = st[i].Car, Value = s2, Finished = true, BotIndex = i });
                            }
                        }
                        else AddGeneratedBots(rows, sum > 0f ? sum : 600f, true);
                        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                        break;
                    }
                case RaceMode.Speed:
                    {
                        me.Value = _speedT > 0.1f ? _speedDist / _speedT * 3.6f : 0f;
                        AddGeneratedBots(rows, 105f, true);
                        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                        break;
                    }
                default:
                    {
                        me.Value = failed || _race.Result == null ? float.MaxValue : _race.Result.FinalTimeMs / 1000f;
                        float refT = _orig[_def.Name].target > 0 ? _orig[_def.Name].target / 1000f : (me.Value < float.MaxValue ? me.Value : 120f);
                        AddGeneratedBots(rows, refT, false);
                        rows.Sort((a, b) => a.Value.CompareTo(b.Value));
                        break;
                    }
            }
            for (int i = 0; i < rows.Count; i++) rows[i].Place = i + 1;
            if (failed) { rows.Remove(me); rows.Add(me); for (int i = 0; i < rows.Count; i++) rows[i].Place = i + 1; }
            return rows;
        }

        /// <summary>Gegnerwerte ohne gefahrene Gegner: um den Referenzwert gestreut nach Stärke (Zeit: kleiner = besser).</summary>
        void AddGeneratedBots(List<Row> rows, float reference, bool higherIsBetter)
        {
            var bots = _cup.Lic.Bots;
            for (int i = 0; i < bots.Count; i++)
            {
                var (pnj, diff, _) = bots[i];
                float k = 1f + (0.55f - diff) * 0.12f + i * 0.012f;
                float v = higherIsBetter ? reference / k : reference * k;
                var car = BotCar(pnj, _cup.Lic, Man.Database);
                string name = _repo.Database.PnjNames.TryGetValue(pnj, out var nm) && !string.IsNullOrEmpty(nm) ? nm : "Fahrer " + (i + 1);
                rows.Add(new Row { Name = name, Car = car != null ? car.ModelName : "", Value = v, Finished = true, BotIndex = i });
            }
        }

        string CupStanding(out bool done, out int place)
        {
            int nb = _cup.Lic.Bots.Count;
            var total = new int[nb + 1];
            int raced = 0;
            foreach (var r in _cup.Races)
            {
                var p = RaceProgress.CupRacePoints(_cup.Lic.Hash, r.Name);
                if (p == null) continue;
                raced++;
                for (int i = 0; i < p.Length && i < total.Length; i++) total[i] += p[i];
            }
            done = raced >= _cup.Races.Count;
            place = 1;
            for (int i = 1; i < total.Length; i++) if (total[i] > total[0]) place++;
            return _cup.Name + ": " + raced + "/" + _cup.Races.Count + " Rennen, " + total[0] + " Punkte (Gesamtplatz " + place + ")";
        }

        void UpdateResults()
        {
            if (_rows == null) return;
            if (!_filmTried && Time.unscaledTime >= _resultAt)
            {
                _filmTried = true;
                _filmShown = ShowResultsFilm();
                if (_filmShown) _resultAt = -1f;
            }
            if (_filmShown) return;   // Eingaben gehören dem Film (RaceResults)
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_resultAt > 0f) return;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape)) CloseResults(false);
            else if (Input.GetKeyDown(KeyCode.R)) CloseResults(true);
#endif
        }

        void CloseResults(bool retry)
        {
            Cursor.lockState = _savedLock; Cursor.visible = _savedCursor;
            AfterResults(retry ? 1 : 2);
        }

        /// <summary>Nach dem Ergebnis: 0 freie Fahrt, 1 wiederholen, 2 weiter = zurück in den Wettbewerb der Meisterschaft (Karte).</summary>
        void AfterResults(int mode)
        {
            RacingSchoolMenu.NoteUiClosed();
            var cup = _cup; var def = _def;
            _filmShown = false;
            EndRace();
            if (mode == 1 && cup != null && def != null) BeginRace(cup, def);
            else if (mode == 2 && cup != null) StartCoroutine(BackToHub(cup));
        }

        /// <summary>
        /// "Weiter": zurück zum Treffpunkt der Meisterschaft (das Auto steht nach dem Rennen irgendwo auf der Strecke – die Karte
        /// zeigt sonst Gegenden, die nicht geladen sind) und dort die Wettbewerbsansicht öffnen, sobald die Umgebung geladen ist.
        /// </summary>
        System.Collections.IEnumerator BackToHub(Cup cup)
        {
            var veh = Man != null ? Man.Current : null;
            var root = Root;
            if (veh != null && root != null)
            {
                var hub = HubPosition(cup, out var f);
                var w = root.TransformPoint(hub);
                w.y = Man.GroundHeight(w) + 0.5f;
                var wf = root.TransformVector(f); wf.y = 0f;
                veh.ResetVehicle(w, Quaternion.LookRotation(wf.sqrMagnitude > 1e-4f ? wf.normalized : Vector3.forward, Vector3.up));
                TDU2DebugLog.Event("Meisterschaft: zurück zum Treffpunkt " + cup.Name);
            }
            // Umgebung nachladen lassen (Streamer folgt dem Auto), danach noch einmal sauber auf den Boden setzen
            float until = Time.unscaledTime + 2f;
            while (Time.unscaledTime < until) yield return null;
            veh = Man != null ? Man.Current : null;
            if (veh != null && root != null)
            {
                var p = veh.transform.position; p.y = Man.GroundHeight(p) + 0.5f;
                veh.ResetVehicle(p, veh.transform.rotation);
            }
            until = Time.unscaledTime + 0.3f;
            while (Time.unscaledTime < until) yield return null;
            var map = Man != null ? Man.GetComponent<TDU2BigMap>() : null;
            if (map != null && _phase == Phase.None) map.OpenContest(_cups.IndexOf(cup), true);
        }

        /// <summary>Ergebnis im Original-Film (results.vmf): Ergebniskarte, Rennliste, Meisterschaftswertung, Menü.</summary>
        bool ShowResultsFilm()
        {
            if (_results == null || _rows == null || _cup == null || _def == null) return false;
            bool time = _def.Kind == RaceMode.Race || _def.Kind == RaceMode.TimeAttack;
            string Val(Row r) => time ? (r.Value >= float.MaxValue * 0.5f ? "--'--\"--" : RaceHud.FormatRaceTime(r.Value))
                                      : r.Value.ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " km/h";
            var race = new List<RaceResults.Row>();
            foreach (var r in _rows)
                race.Add(new RaceResults.Row { Place = r.Place, Name = r.Name, Car = r.Car, Player = r.Player, Score = Val(r) + " (" + r.Points + " Pkt.)" });
            var cupRows = new List<RaceResults.Row>();
            int idx = _cups.IndexOf(_cup);
            var st = MapStandings(idx);
            string myCar = Man.Current?.Car?.ModelName ?? "";
            for (int i = 0; i < st.Count; i++)
                cupRows.Add(new RaceResults.Row { Place = i + 1, Name = st[i].name, Car = st[i].player ? myCar : "", Player = st[i].player, Score = st[i].points + " Pkt." });
            var me = _rows.Find(r => r.Player);
            long prize = me != null ? PrizeFor(me.Place, _cup.Lic.RaceMoneyReward) : 0;
            return _results.ShowChampionship(RaceName(_def), _cup.Name, ModeLabel(_def), HubLabel(_cup), me != null ? me.Place : _rows.Count,
                                             me != null ? Val(me) : "", prize, race, cupRows);
        }

        void EndRace()
        {
            ShowPosition(false);
            var hh = Man != null && Man.Minimap != null ? Man.Minimap.HudPlayer : null;
            if (hh != null) { hh.GotoLabel("/HUD/speed_gauge", "default", false); hh.GotoLabel("/HUD/time_cd", "default", false); }
            if (_autopilot != null) _autopilot.End();
            if (Man != null) Man.ExternalControl = false;
            if (_race != null) { if (_race.Running || _race.State == RaceState.Countdown || _race.State == RaceState.Preparing) _race.Abort(); _race.Cleanup(); }
            RaceRoute.RoadPath = null; RaceRoute.RoadPathSeg = null;
            SetPrivate(false);
            _phase = Phase.None;
            _rows = null;
            _nextRefresh = 0f;
        }

        void SetPrivate(bool on)
        {
            if (on == _privateOn) return;
            _privateOn = on;
#if UNITY_2023_1_OR_NEWER
            var tr = FindFirstObjectByType<TDU2.Map.Traffic.TDU2Traffic>();
#else
            var tr = FindObjectOfType<TDU2.Map.Traffic.TDU2Traffic>();
#endif
            if (tr != null)
            {
                if (on) { _trafficWas = tr.EnableTraffic; tr.EnableTraffic = false; }
                else tr.EnableTraffic = _trafficWas;
            }
            TDU2.Map.Online.TDU2Multiplayer.Hidden = on;
        }

        // ------------------------------------------------------------------ Oberfläche

        GUIStyle _box, _title, _text, _small, _cell, _cellR;

        void Styles()
        {
            if (_box != null) return;
            _box = new GUIStyle(GUI.skin.box);
            _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.white } };
            _small = new GUIStyle(_text) { normal = { textColor = new Color(1f, 0.8f, 0.4f) } };
            _cell = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white }, clipping = TextClipping.Clip };
            _cellR = new GUIStyle(_cell) { alignment = TextAnchor.MiddleRight };
        }

        void OnGUI()
        {
            Styles();
            float sh = Screen.height, sw = Screen.width, u = sh / 720f;
            _title.fontSize = Mathf.RoundToInt(20 * u); _text.fontSize = Mathf.RoundToInt(15 * u); _small.fontSize = Mathf.RoundToInt(13 * u);
            _cell.fontSize = Mathf.RoundToInt(14 * u); _cellR.fontSize = _cell.fontSize;
            GUI.depth = -60;

            // Hinweise laufen über das Original-HUD (Einfahrt /HUD/enter, Handy /HUD/phone_call) – kein eigenes Fenster

            if (_phase == Phase.Speed && _speedCountdown <= 0f && (Man == null || Man.Minimap == null || Man.Minimap.HudPlayer == null))
            {
                float avg = _speedT > 0.1f ? _speedDist / _speedT * 3.6f : 0f;
                var r = new Rect(sw * 0.5f - 140 * u, sh * 0.08f, 280 * u, 40 * u);
                Panel(r);
                GUI.Label(r, "Ø " + avg.ToString("0.0", CultureInfo.InvariantCulture) + " km/h", _title);
            }

            if (_phase == Phase.Results && _rows != null && !_filmShown && _filmTried)
            {
                if (_resultAt > 0f)
                {
                    if (Time.unscaledTime < _resultAt) return;
                    _resultAt = -1f;
                    _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
                    Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                }
                DrawResults(sw, sh, u);
            }
        }

        void DrawResults(float sw, float sh, float u)
        {
            float rowH = 26 * u;
            float h = 150 * u + rowH * (_rows.Count + 1) + 40 * u;
            var r = new Rect(sw * 0.5f - 330 * u, sh * 0.5f - h * 0.5f, 660 * u, h);
            Panel(r);
            GUI.Label(new Rect(r.x, r.y + 8 * u, r.width, 28 * u), _resultTitle, _title);
            GUI.Label(new Rect(r.x, r.y + 38 * u, r.width, 22 * u), _resultInfo, _text);
            float y = r.y + 70 * u, x = r.x + 20 * u, w = r.width - 40 * u;
            string valHead = _def.Kind == RaceMode.Race || _def.Kind == RaceMode.TimeAttack ? "Zeit" : "km/h";
            Cells(x, y, w, rowH, "Pl.", "Fahrer", "Auto", valHead, "Pkt.", new Color(1f, 0.8f, 0.4f));
            y += rowH;
            foreach (var row in _rows)
            {
                string val;
                if (_def.Kind == RaceMode.Race || _def.Kind == RaceMode.TimeAttack)
                    val = row.Value >= float.MaxValue * 0.5f ? "--" : RaceHud.FormatRaceTime(row.Value) + (row.Finished ? "" : " *");
                else val = row.Value.ToString("0.0", CultureInfo.InvariantCulture);
                if (row.Player) { GUI.color = new Color(1f, 0.55f, 0.1f, 0.35f); GUI.DrawTexture(new Rect(x - 6 * u, y, w + 12 * u, rowH), Texture2D.whiteTexture); GUI.color = Color.white; }
                Cells(x, y, w, rowH, row.Place + ".", row.Name, row.Car, val, row.Points.ToString(CultureInfo.InvariantCulture), Color.white);
                y += rowH;
            }
            GUI.Label(new Rect(r.x + 10 * u, y + 6 * u, r.width - 20 * u, 44 * u), _cupInfo, _small);
            float by = r.yMax - 42 * u;
            if (GUI.Button(new Rect(r.x + r.width * 0.5f - 210 * u, by, 200 * u, 32 * u), "Weiter [Enter]")) CloseResults(false);
            if (GUI.Button(new Rect(r.x + r.width * 0.5f + 10 * u, by, 200 * u, 32 * u), "Wiederholen [R]")) CloseResults(true);
        }

        void Cells(float x, float y, float w, float h, string a, string b, string c, string d, string e, Color col)
        {
            _cell.normal.textColor = col; _cellR.normal.textColor = col;
            GUI.Label(new Rect(x, y, w * 0.08f, h), a, _cell);
            GUI.Label(new Rect(x + w * 0.08f, y, w * 0.34f, h), b, _cell);
            GUI.Label(new Rect(x + w * 0.42f, y, w * 0.30f, h), c, _cell);
            GUI.Label(new Rect(x + w * 0.72f, y, w * 0.18f, h), d, _cellR);
            GUI.Label(new Rect(x + w * 0.90f, y, w * 0.10f, h), e, _cellR);
            _cell.normal.textColor = Color.white; _cellR.normal.textColor = Color.white;
        }

        static void Panel(Rect r)
        {
            GUI.color = new Color(0.03f, 0.05f, 0.08f, 0.82f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.55f, 0.1f, 0.9f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 3f), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
