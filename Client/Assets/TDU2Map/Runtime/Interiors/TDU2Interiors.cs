using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TDU2.Map.Flash;
using TDU2.Map.Formats;
using TDU2.Map.Online;
using TDU2.Map.Racing;
using TDU2.Map.World;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Spots betreten wie im Spiel: an der Einfahrt eines Autohändlers / Hauses / Shops [Enter] → Interieur
    /// (Euro/Bnk/interior) wird geladen, man läuft in der Ich-Perspektive (FirstPerson-Startkamera der Szene).
    ///   Autohändler (icd_*, ibd_*): Bestand aus DBCarShop auf den Stellplätzen D_CAR_xx, Hostess = Kaufmenü,
    ///                               Auto anvisieren + E = kaufen und losfahren.
    ///   Haus: Garage (gph_*/gch_*) mit den eigenen Autos (D_CAR_xx), Fahrzeugtafel = Auswahl, Tür "Living" = Wohnbereich;
    ///         Wohnbereich (lph_*/ich_*) mit Garderobe, MyTULive/Tagebuch, TV (TDU Media), Garage, Ausgang; Möbel aus interior/myhouse.
    ///   Verlassen: Auto (gewählt oder zuletzt gefahren) erscheint an Position2/Richtung2 des Spots (spots.bin).
    /// </summary>
    public sealed partial class TDU2Interiors : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        [Tooltip("Höhenversatz des Interieurs über dem Spot (0 = an Ort und Stelle wie im Spiel)")]
        public float InteriorAltitude = 0f;
        public float EnterMaxSpeedKmh = 40f;
        public float InteractDistance = 10f;
        public bool ShowPrompts = true;

        enum Kind { Dealer, Living, Garage, StickerShop, Other }

        sealed class SpotBanks { public bool Valid, IsClub, IsCasino; public Kind Kind; public string Living, Garage, Name; public string TravelTo; }

        sealed class DisplayCar
        {
            public CarPhysics Car; public GameObject Go; public CarModel Model; public CarAssets Assets; public Collider Col;
            public bool Lobby;          // Ausstellungsauto (Casino-Drehteller): nicht fahrbar
            public float DegPerSec;     // Drehung (NbTurnByMinute)
            public Transform Socle;     // Drehteller darunter (dreht mit)
            public Vector3 AxisLocal;   // Drehachse (Tellermitte) im Raum des Tellers
            public InteriorScene.Ent Slot;   // Stellplatz (Neuaufbau mit anderer Ausstattung beim Händler)
            public CarChoice Choice;         // Ausstattung, mit der das Auto aufgebaut ist
        }

        // ------------------------------------------------------------------ Zustand
        readonly Dictionary<string, SpotBanks> _banks = new Dictionary<string, SpotBanks>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<Spot, Matrix4x4?> _enterBox = new Dictionary<Spot, Matrix4x4?>();
        Spot _spot;
        SpotBanks _spotBanks;
        Kind _kind;
        Transform _holder, _carsRoot;
        TDU2MapStreamer.SceneHandle _scene;
        readonly List<TDU2MapStreamer.SceneHandle> _furniture = new List<TDU2MapStreamer.SceneHandle>();
        InteriorScene _data;
        TDU2FirstPerson _fp;
        Task<(BankModel model, InteriorScene data)> _sceneTask;
        Task<List<(InteriorScene.Ent ent, BankModel model)>> _furnTask;
        string _pendingBank, _pendingStartCam;
        Kind _pendingKind;
        int _gen;

        readonly List<DisplayCar> _cars = new List<DisplayCar>();
        readonly Queue<(CarPhysics car, InteriorScene.Ent slot)> _carQueue = new Queue<(CarPhysics, InteriorScene.Ent)>();
        Task<CarAssets> _carTask;
        (CarPhysics car, InteriorScene.Ent slot, int gen) _carTaskInfo;

        CarPhysics _lastCar;
        Spot _nearSpot;
        SpotBanks _nearBanks;
        float _nextScan;

        bool _menuOpen;
        string _menuTitle;
        List<CarPhysics> _menuCars = new List<CarPhysics>();
        bool _menuBuy, _menuAll;
        Vector2 _menuScroll;
        string _aimLabel;
        object _aimTarget;
        string _info;
        float _infoUntil;
        string _status;
        GUIStyle _box, _label, _small;

        const string OwnedKey = "TDU2.OwnedCars";
        public bool Inside => _spot != null;
        /// <summary>Spot des Interieurs, in dem man gerade ist (null = draußen).</summary>
        public Spot CurrentSpot => _spot;
        /// <summary>Interieur betreten (Spot) bzw. verlassen – z.B. für Spot-Musik/Radio (TDU2SpotAudio).</summary>
        public event Action<Spot> Entered;
        public event Action<Spot> Exited;
        [Tooltip("Im Interieur Autoradio stumm schalten und die Spot-Musik (sound/musics) abspielen")]
        public bool SpotAudio = true;
        TDU2SpotAudio _spotAudio;

        // ------------------------------------------------------------------ Update
        void Update()
        {
            if (Manager == null) Manager = GetComponent<TDU2VehicleManager>();
            if (Manager == null || Manager.Streamer == null) return;
            if (SpotAudio && _spotAudio == null) { _spotAudio = GetComponent<TDU2SpotAudio>(); if (_spotAudio == null) _spotAudio = gameObject.AddComponent<TDU2SpotAudio>(); _spotAudio.Interiors = this; }
            PollScene();
            PollFurniture();
            PollHouseObjects();
            PollDonors();
            PollCars();
            PollDealerPreview();
            UpdateTestDrive();
            if (Inside) TDU2Accessories.UpdateAll(Time.deltaTime);   // Türen/Fenster der Ausstellungs-/Garagenautos
            if (Inside) UpdateInside();
            else UpdateOutside();
        }

        // ------------------------------------------------------------------ Draußen: Einfahrt erkennen
        void UpdateOutside()
        {
            var st = Manager.Streamer;
            if (Manager.Suspended) { _nearSpot = null; return; }   // Login/Start läuft
            // Karte/Menüs offen: Enter gehört der Oberfläche (sonst betritt man z.B. den Flughafen, an dessen Einfahrt das Auto steht)
            if (TDU2BigMap.IsOpen || Racing.RacingSchoolMenu.BlocksPauseMenu || Time.timeScale <= 0f) { _nearSpot = null; return; }
            if (st.Spots == null || st.MapRoot == null || Manager.Database == null || string.IsNullOrEmpty(Manager.BnkDir) || Manager.IsLoadingCar) { _nearSpot = null; return; }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.25f;
                _nearSpot = null; _nearBanks = null;
                Vector3 wp;
                if (Manager.Current != null)
                {
                    if (Manager.Current.SpeedKmh > EnterMaxSpeedKmh) return;
                    wp = Manager.Current.transform.position;
                }
                else if (Camera.main != null) wp = Camera.main.transform.position;
                else return;
                var g = st.MapRoot.InverseTransformPoint(wp);
                float best = float.MaxValue;
                foreach (var s in st.Spots.Spots)
                {
                    // Abstand zum Spot ODER zu seiner Einfahrt: beim Casino (ECAS) liegt die Einfahrt ~160 m vom Spot-Anker
                    float dx = g.x - s.Position.x, dz = g.z - s.Position.z;
                    float d2 = dx * dx + dz * dz;
                    if (s.Position2.sqrMagnitude > 1f)
                    {
                        float ex = g.x - s.Position2.x, ez = g.z - s.Position2.z;
                        d2 = Mathf.Min(d2, ex * ex + ez * ez);
                    }
                    if (d2 > 150f * 150f) continue;
                    bool atGate = InEnterBox(s, g) || HorizDist(g, s.Position2) < 12f;
                    if (!atGate) continue;
                    var b = Resolve(s);
                    if (!b.Valid)
                    {
                        if (_loggedInvalid.Add(s.Code ?? ""))
                        {
                            DebugManager.Warning($"Spot ohne Interieur: '{s.Code}' Kategorie {s.TypeA & 0xFF} ({s.TypeA}/{s.TypeB}) bei {s.Position}");
                        }
                        continue;
                    }
                    if (d2 < best) { best = d2; _nearSpot = s; _nearBanks = b; }
                }
            }
            var mm = Manager.Minimap;
            if (mm != null && mm.HasOriginalHud) mm.ShowEnter(_nearSpot != null && Manager.Current != null ? _nearSpot.Code : null);
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_nearSpot != null && !TDU2GameData.ChatTyping && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                if (mm != null) mm.ShowEnter(null);
                if (!string.IsNullOrEmpty(_nearBanks.TravelTo)) Travel(_nearSpot, _nearBanks);
                else Enter(_nearSpot, _nearBanks);
            }
#endif
        }

        static float HorizDist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        bool InEnterBox(Spot s, Vector3 g)
        {
            if (!_enterBox.TryGetValue(s, out var inv))
            {
                inv = Mathf.Abs(s.Box2.determinant) > 1e-3f ? s.Box2.inverse : (Matrix4x4?)null;
                _enterBox[s] = inv;
            }
            if (!inv.HasValue) return false;
            var l = inv.Value.MultiplyPoint3x4(g);
            return Mathf.Abs(l.x) <= 0.6f && Mathf.Abs(l.z) <= 0.6f && Mathf.Abs(l.y) <= 1.5f;
        }

        string InteriorDir => Path.Combine(Manager.BnkDir, "interior");
        readonly HashSet<string> _loggedInvalid = new HashSet<string>();

        SpotBanks Resolve(Spot s)
        {
            string code = (s.Code ?? "").ToLowerInvariant();
            if (_banks.TryGetValue(code, out var b)) return b;
            b = new SpotBanks();
            try
            {
                if (code.StartsWith("eland"))
                {
                    // Landeplatz (Kategorie 14): Hawaii ELANDCAA <-> Casino-Insel ELANDCA (Flug, Ankunft am Landeplatz der Gegenseite)
                    string here = Manager.Streamer?.Index?.IslandName ?? Manager.Streamer?.Island ?? "";
                    bool onCasino = here.StartsWith("cas", StringComparison.OrdinalIgnoreCase);
                    string target = onCasino ? "hawai" : "casino";
                    if (TDU2IslandTravel.CanTravelTo(target, Manager.BnkDir)) { b.TravelTo = target; b.Kind = Kind.Other; b.Name = onCasino ? "Hawaii" : "Casino"; }
                    b.Valid = b.TravelTo != null;
                    _banks[code] = b;
                    return b;
                }
                if (code.Length > 4 && code.StartsWith("eph_"))
                {
                    string rest = code.Substring(4);
                    b.Living = Bank("lph_" + rest); b.Garage = Bank("gph_" + rest); b.Kind = Kind.Living; b.Name = "Haus";
                }
                else if (code.Length > 4 && code.StartsWith("ech_"))
                {
                    string rest = code.Substring(4);
                    // Clubhaus (ECH_*): das Spiel lädt für ECH_IA "Interior/ich_ib.bnk" (TestDrive2Dev.exe)
                    b.Living = (rest == "ia" ? Bank("ich_ib") : null) ?? Bank("ich_" + rest);
                    b.Garage = Bank("gch_" + rest) ?? BankPrefix("gch_" + rest.Substring(0, 1)); b.Kind = Kind.Living; b.Name = "Club";
                    b.IsClub = true;
                }
                else if (code.StartsWith("ecas") || (s.TypeA & 0xFF) == 21)   // Kategorie 21 = Casino (spots.bin +256)
                {
                    // Casino (DLC, exe: Spot "ECAS", Interior/ICASDR.bnk): Halle icashal, Räume icaspok1 (Poker), icasrou1 (Roulette),
                    // icasslo1 (Spielautomaten), icasvip (VIP), icasdr (Garderobe)
                    b.Living = Bank("i" + code.Substring(1)) ?? Bank("icashal"); b.Kind = Kind.Other; b.Name = "Casino";
                    b.IsCasino = true;
                }
                else if (code.StartsWith("estol"))
                {
                    // Gebrauchtwagenhändler (Ibiza ESTOL_B: Start des Spiels, DBCarShop ESTOL_B_2265)
                    b.Living = Bank("i" + code.Substring(1)); b.Kind = Kind.Dealer; b.Name = "Gebrauchtwagenhändler";
                }
                else if (code.StartsWith("estick"))
                {
                    // Sticker-Shop (ESTICK_B -> interior/istick_b.bnk): eigenes Auto auf D_CAR, Verkäuferin = FpSelectable Typ 28
                    b.Living = Bank("i" + code.Substring(1)); b.Kind = Kind.StickerShop; b.Name = "Sticker-Shop";
                }
                else if (code.StartsWith("ecd") || code.StartsWith("ebd"))
                {
                    b.Living = Bank("i" + code.Substring(1)); b.Kind = Kind.Dealer; b.Name = code.StartsWith("ebd") ? "Motorradhändler" : "Autohändler";
                }
                else if (code.Length > 1 && code[0] == 'e')
                {
                    b.Living = Bank("i" + code.Substring(1)); b.Kind = Kind.Other; b.Name = SpotsFile.CategoryFromCode(s.Code);
                }
                b.Valid = b.Living != null || b.Garage != null;
            }
            catch (Exception) { b.Valid = false; }
            _banks[code] = b;
            return b;
        }

        string Bank(string name) => CarDatabase.FindFile(InteriorDir, name + ".bnk");

        string BankPrefix(string prefix)
        {
            try
            {
                foreach (var f in Directory.GetFiles(InteriorDir, prefix + "*.bnk"))
                    if (!Path.GetFileName(f).Contains("__")) return f;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Landeplatz: Flug zur Casino-Insel bzw. zurück nach Hawaii (Szene mit der Zielinsel neu laden).</summary>
        void Travel(Spot s, SpotBanks b)
        {
            string car = Manager.Current != null && Manager.Current.Car != null ? Manager.Current.Car.FileName : null;
            DebugManager.Info("Landeplatz " + s.Code + " -> " + b.TravelTo);
            _nearSpot = null; _nearBanks = null;
            TDU2IslandTravel.Go(b.TravelTo, true, car, "ELAND");
        }

        // ------------------------------------------------------------------ Betreten / Verlassen
        List<CarPhysics> _starterCars;
        Action<CarPhysics> _starterChosen;
        /// <summary>true, solange beim Start ein Startauto gewählt werden muss (Ausgang gesperrt).</summary>
        public bool ChoosingStarter => _starterCars != null;

        /// <summary>
        /// Spielstart eines neuen Kontos: Händler-Interieur betreten, auf den Stellplätzen stehen nur die Startautos;
        /// das gewählte Auto (anvisieren + E) wird an <paramref name="chosen"/> gemeldet, danach fährt man los.
        /// </summary>
        public void EnterStarterDealer(Spot s, List<CarPhysics> cars, Action<CarPhysics> chosen)
        {
            if (s == null || Manager == null) return;
            if (Inside) ExitInterior(null);
            var b = Resolve(s);
            if (!b.Valid)
            {
                DebugManager.Error("Kein Interieur für Startauto: " + s.Code);
                chosen?.Invoke(cars != null && cars.Count > 0 ? cars[0] : null);
                return;
            }
            _starterCars = cars != null ? new List<CarPhysics>(cars) : new List<CarPhysics>();
            _starterChosen = chosen;
            _lastCar = null;
            Enter(s, b);
        }

        /// <summary>
        /// Interieur eines Spots direkt betreten (z.B. Rückkehr in die Rennschule nach einer Prüfung).
        /// <paramref name="lastCar"/> = Auto, das beim Verlassen wieder erscheint (statt des gerade gefahrenen).
        /// </summary>
        public bool EnterSpot(Spot s, CarPhysics lastCar)
        {
            if (s == null || Manager == null) return false;
            var b = Resolve(s);
            if (!b.Valid) return false;
            if (Inside) ExitInterior(null, false);
            // gerade gefahrenes Auto (z.B. Prüfungsauto) NICHT als eigenes/aktuelles Auto speichern: vorher entfernen
            Manager.RemoveCar();
            Enter(s, b);
            if (lastCar != null)
            {
                _lastCar = lastCar; RememberOwned(lastCar);
            }
            TDU2DebugLog.Event("Interieur betreten (EnterSpot) " + s.Code + ", Auto beim Verlassen: " + (_lastCar != null ? _lastCar.FileName + "#" + _lastCar.Id : "-"));
            DebugManager.Info("Interieur betreten (EnterSpot) " + s.Code + ", Auto beim Verlassen: " + (_lastCar != null ? _lastCar.FileName + "#" + _lastCar.Id : "-"));
            return true;
        }

        /// <summary>true, sobald die Szene des Interieurs aufgebaut ist.</summary>
        public bool SceneReady => Inside && _holder != null && _sceneTask == null;

        void Enter(Spot s, SpotBanks b)
        {
            AbortTestDriveOnEnter();   // Testfahrt läuft noch: eigenes Auto statt des Leihwagens merken
            _casinoReturnCam = null;
            if (Manager.Current != null) { _lastCar = Manager.Current.Car; RememberOwned(_lastCar); }
            if (_lastCar == null && _starterCars == null) _lastCar = Manager.FindCar(Manager.StartCar) ?? (Manager.Available.Count > 0 ? Manager.Available[0] : null);
            Manager.RemoveCar();
            Manager.Suspended = true;
            _spot = s; _spotBanks = b;
            SetInteriorAmbient(true);
            // Sticker-Shop: nur das eigene Auto – Mitspieler werden solange ausgeblendet
            Online.TDU2Multiplayer.HideRemotes = b.Kind == Kind.StickerShop && Online.TDU2OnlineSettings.HideCarsInStickerShop;
            try
            {
                Entered?.Invoke(s);
            }
            catch (Exception ex)
            {
                DebugManager.Error("Interieur Entered-Event: " + ex.Message);
            }

            var go = new GameObject("TDU2 FirstPerson");
            _fp = go.AddComponent<TDU2FirstPerson>();
            _fp.Cam = Camera.main;
            _fp.enabled = false;   // erst nach dem Laden der Szene
            LockCursor(true);

            // Wie im Spiel: Haus und Club betritt man im Wohnbereich bzw. im Clubraum; die Garage erreicht man von dort.
            // (Ohne Wohnbereich-Bank direkt in die Garage.)
            if (b.Kind == Kind.Living && b.Living != null)
                LoadScene(b.Living, Kind.Living, null);
            else if (b.Kind == Kind.Living && b.Garage != null)
                LoadScene(b.Garage, Kind.Garage, null);
            else
                LoadScene(b.Living ?? b.Garage, b.Kind, null);
        }

        /// <summary>Interieur fertig aufgebaut (Szene, Ausstellungs-/Garagenautos, Möbel) – Ende des Ladebildschirms.</summary>
        bool InteriorLoaded => SceneReady && _carTask == null && _carQueue.Count == 0 && _furnTask == null;

        void LoadScene(string bank, Kind kind, string startCam)
        {
            // Ladebildschirm (Tipp-Bild, Speichern) bis Szene und Autos stehen – der Aufbau soll nicht zu sehen sein
            TDU2LoadingScreen.Show("Interieur " + Path.GetFileNameWithoutExtension(bank), () => !Inside || InteriorLoaded, 1.0f, true, Manager.BnkDir);
            ClearScene();
            int gen = ++_gen;
            _pendingBank = bank; _pendingKind = kind; _pendingStartCam = startCam;
            _status = "Lade " + Path.GetFileNameWithoutExtension(bank) + " ...";
            var st = Manager.Streamer;
            _sceneTask = Task.Run(() =>
            {
                var model = st.LoadSceneModel(bank);
                var data = InteriorScene.Parse(model.Bank);
                InteriorFx.Prepare(model.Bank);   // Textur-Animationen (.uva) der Szene
                return (model, data);
            });
            if (_fp != null) _fp.enabled = false;
        }

        void PollScene()
        {
            if (_sceneTask == null || !_sceneTask.IsCompleted) return;
            var t = _sceneTask; _sceneTask = null;
            if (t.IsFaulted)
            {
                _status = null;
                DebugManager.Error("Interieur: " + t.Exception?.GetBaseException());
                if (_holder == null)
                {
                    if (_starterCars != null && _starterCars.Count > 0)
                    {
                        // Interieur fehlt: erstes Startauto nehmen
                        var first = _starterCars[0]; var cb = _starterChosen;
                        _starterCars = null; _starterChosen = null;
                        cb?.Invoke(first);
                        _lastCar = first;
                    }
                    ExitInterior(_lastCar);
                }
                return;
            }
            if (!Inside) { t.Result.model?.Release(); return; }
            try { BuildInterior(t.Result.model, t.Result.data); }
            catch (Exception ex)
            {
                DebugManager.Error("Interieur aufbauen: " + ex.Message);
            }
            _status = null;
        }

        void BuildInterior(BankModel model, InteriorScene data)
        {
            var st = Manager.Streamer;
            _kind = _pendingKind;
            _data = data;
            _holder = new GameObject("TDU2 Interior " + Path.GetFileNameWithoutExtension(_pendingBank)).transform;
            _holder.SetParent(st.MapRoot, false);
            // wie im Spiel an der Stelle des Spots (gleiche Platzierung wie das Exterior: Position, Drehung um Y = Winkel)
            _holder.localPosition = _spot.Position + Vector3.up * InteriorAltitude;
            _holder.localRotation = Quaternion.Euler(0f, _spot.Value1 * Mathf.Rad2Deg, 0f);
            // Innenräume sind immer beleuchtet: leuchtende (additive) Materialien wie Neon/Lichtbänder mit aufbauen
            bool glow = World.MaterialFactory.ShowGlowMaterials;
            World.MaterialFactory.ShowGlowMaterials = true;
            World.MaterialFactory.Interior = true;
            try { _scene = st.BuildScene(model, _holder, "Scene"); }
            finally { World.MaterialFactory.ShowGlowMaterials = glow; World.MaterialFactory.Interior = false; }
            // Garage wie im Spiel heller und mit deutlich spiegelndem Boden
            InteriorFx.MinFloorReflection = _kind == Kind.Garage ? GarageFloorReflection : 0f;
            if (_ambientSet) RenderSettings.ambientLight = _kind == Kind.Garage ? GarageAmbient : InteriorAmbient;
            InteriorFx.Apply(_pendingBank, _holder);   // .uva-Animationen, Glas transparent
            if (_spotBanks != null && _spotBanks.IsCasino) LogSceneDiag(model, data);
            _liftOffset = 0f;
            SetupRankingLift();
            SetExteriorVisible(false, model);
            PrepareStatic(_holder, _spotBanks != null && _spotBanks.IsCasino);
            ComputeClipBox();
            DisableWorldColliders();   // vor dem Sicherheitsboden: dessen Raycasts sollen nur das Interieur treffen
            HideWorld();
            AddSafetyFloor(data);
            // Figuren (Verkäuferin, Hostess, Friseur …) mit ihrem DBPNJ-Aussehen, Cinematic-Szenen der Bank
            try
            {
                Npcs.Build(_holder, data, _pendingBank);
            }
            catch (Exception ex)
            {
                DebugManager.Error("Interieur-Figuren: " + ex.Message);
            }
            _carsRoot = new GameObject("TDU2 Interior Cars").transform;

            // Möbel (Haus): interior/myhouse/<Name ohne _NN>.bnk
            if (data.HouseObjects.Count > 0)
            {
                var list = new List<(InteriorScene.Ent, string)>();
                foreach (var e in data.HouseObjects)
                {
                    string n = Regex.Replace(e.Name ?? "", @"_\d+$", "").ToLowerInvariant();
                    string b = HouseObjectBank(e, n);   // My House: eingerichtete Bank (TDU2MyHouse.cs)
                    var f = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), b + ".bnk");
                    if (f == null && b != n) { b = n; f = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), n + ".bnk"); }
                    _houseBankOf[e] = b;
                    if (f != null) list.Add((e, f));
                }
                if (list.Count > 0)
                    _furnTask = Task.Run(() =>
                    {
                        var r = new List<(InteriorScene.Ent, BankModel)>();
                        foreach (var (e, f) in list)
                        {
                            try { r.Add((e, st.LoadSceneModel(f))); } catch (Exception) { }
                        }
                        return r;
                    });
            }

            LoadUi(_kind);
            ApplyHouseMaterials();   // My House: eingerichtete Böden/Wände

            // Ich-Perspektive an der Startkamera
            var cam = (_pendingStartCam != null ? data.Camera(_pendingStartCam) : null) ?? data.StartCam;
            Vector3 eye, look;
            if (cam != null)
            {
                eye = _holder.TransformPoint(cam.Position);
                look = _holder.TransformDirection(cam.Forward);
            }
            else { eye = _holder.position + Vector3.up * 1.7f; look = _holder.forward; }
            if (_fp != null)
            {
                _fp.enabled = true;
                _fp.Place(eye, look);
                _fp.LookEnabled = !_menuOpen;
            }

            // z.B. Garderobe: nach dem Aufbau öffnen
            if (_afterBuild != null)
            {
                var ab = _afterBuild; _afterBuild = null;
                try
                {
                    ab();
                }
                catch (Exception ex)
                {
                    DebugManager.Error("Interieur AfterBuild: " + ex.Message);
                }
            }

            // Autos auf die Stellplätze
            var cars = new List<CarPhysics>();
            if (_kind == Kind.Dealer && _starterCars != null) cars.AddRange(_starterCars);
            else if (_kind == Kind.Dealer)
            {
                var shop = Manager.Database.ShopFor(_spot.Code);
                if (shop != null)
                    foreach (int id in shop.CarIds)
                        if (id != 0 && Manager.Database.ById.TryGetValue(id, out var c)) { var av = Manager.FindCar(c.FileName); if (av != null && !cars.Contains(av)) cars.Add(av); }
            }
            else if (_kind == Kind.StickerShop)
            {
                if (_lastCar != null) cars.Add(_lastCar);
            }
            else if (_kind == Kind.Garage && _spotBanks != null && _spotBanks.IsClub)
            {
                // Club-Garage: keine eigenen Autos (im Spiel stehen dort die Autos der Clubmitglieder)
            }
            else if (_kind == Kind.Garage)
            {
                if (GuestGarageCars != null)
                {
                    // Gast im Haus eines Mitspielers: dessen Autos (aktuelles zuerst) statt der eigenen
                    foreach (var n in GuestGarageCars)
                    {
                        var c = Manager.FindCar(n);
                        if (c != null && !cars.Contains(c))
                            cars.Add(c);
                    }
                }
                else
                {
                    if (_lastCar != null) cars.Add(_lastCar);
                    foreach (var c in OwnedCars())
                        if (!cars.Contains(c)) cars.Add(c);
                }
                var house = Manager.Database.HouseFor(_spot.Code);
                int max = house != null && house.CarSlots > 0 ? house.CarSlots : data.CarSlots.Count;
                if (cars.Count > max) cars.RemoveRange(max, cars.Count - max);
            }
            for (int i = 0; i < cars.Count && i < data.CarSlots.Count; i++) _carQueue.Enqueue((cars[i], data.CarSlots[i]));
            // Ausstellungsautos auf Drehtellern (Casino-Lobby: VirtualVehiculeActor, VehicleId aus der Datenbank)
            foreach (var sc in data.ShowCars)
            {
                if (Manager.Database == null || !Manager.Database.ById.TryGetValue(sc.VehicleId, out var cp) || cp == null)
                {
                    DebugManager.Warning("Ausstellungsauto: VehicleId " + sc.VehicleId + " nicht in der Datenbank");
                    continue;
                }
                var av = Manager.FindCar(cp.FileName) ?? cp;
                _carQueue.Enqueue((av, sc));
            }
        }

        Vector3 _floorPoint;      // Welt: Boden an der Startkamera
        float _nextIntruderScan;

        /// <summary>
        /// Unsichtbarer Sicherheitsboden in Höhe des Startpunkts (manche Böden haben keine nutzbare Kollision – Glas/Decals, offene
        /// Meshes): man fällt nicht mehr durch. Echte Böden darüber/darunter (Treppen, zweite Etage) bleiben maßgeblich.
        /// </summary>
        void AddSafetyFloor(InteriorScene data)
        {
            var cam = data.StartCam;
            Vector3 eye = cam != null ? _holder.TransformPoint(cam.Position) : _holder.position + Vector3.up * 1.7f;
            // Boden unter der Startkamera und unter den Stellplätzen (D_CAR_xx, oft tiefer: Galerie/Treppe); der tiefste zählt.
            // Welt-Kollider sind zu diesem Zeitpunkt schon abgeschaltet -> die Strahlen treffen nur das Interieur.
            // Ohne Treffer (Böden ohne Kollision, z.B. Yacht): Augenhöhe - 1,65 m bzw. Höhe des Stellplatz-Punkts.
            UnityEngine.Physics.SyncTransforms();
            float floor = eye.y - 1.65f;
            if (UnityEngine.Physics.Raycast(eye, Vector3.down, out var hit, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore)) floor = hit.point.y;
            foreach (var slot in data.CarSlots)
            {
                var sp = _holder.TransformPoint(slot.Position);
                float sf = sp.y;
                if (UnityEngine.Physics.Raycast(sp + Vector3.up * 1.0f, Vector3.down, out var sh, 3f, ~(1 << 2), QueryTriggerInteraction.Ignore)) sf = sh.point.y;
                floor = Mathf.Min(floor, sf);
            }
            _floorPoint = new Vector3(eye.x, floor, eye.z);
            var go = new GameObject("TDU2 Interior Safety Floor");
            go.layer = 2;   // Ignore Raycast: Autos/Hotspots/Stellplätze treffen ihn nicht, man selbst steht trotzdem darauf
            go.transform.SetParent(_holder, true);
            go.transform.position = new Vector3(eye.x, floor - 0.3f, eye.z);   // Oberkante 5 cm unter dem Boden
            go.transform.rotation = _holder.rotation;
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(120f, 0.5f, 120f);
        }

        /// <summary>Fällt man trotzdem (z.B. Rampe/Loch), zurück an den Startpunkt.</summary>
        void CatchFall()
        {
            if (_fp == null || !_fp.enabled || _holder == null) return;
            if (_fp.transform.position.y < _floorPoint.y - 6f)
            {
                var cam = _data != null ? _data.StartCam : null;
                var eye = cam != null ? _holder.TransformPoint(cam.Position) : _floorPoint + Vector3.up * 1.7f;
                var look = cam != null ? _holder.TransformDirection(cam.Forward) : _holder.forward;
                _fp.Place(eye, look);
                DebugManager.Warning("Interieur: durch den Boden gefallen – zurück zum Startpunkt");
            }
        }

        readonly List<Renderer> _hiddenIntruders = new List<Renderer>();
        Bounds _interiorBounds; bool _haveInteriorBounds;

        /// <summary>
        /// Welt-Objekte (LOD-Gebäude, Nachbar-Objekte, Spot-Exterior aus anderen Kacheln), die in den Innenraum ragen, ausblenden.
        /// Nur Objekte, die klein genug sind (kein Gelände) und deren Mitte im Innenraum liegt bzw. die ihn überwiegend durchdringen.
        /// Läuft beim Betreten und danach jede Sekunde (der Streamer lädt beim Laufen Kacheln nach).
        /// </summary>
        void HideIntruders()
        {
            if (!_haveInteriorBounds || Manager?.Streamer?.MapRoot == null) return;
            var inner = _interiorBounds; inner.Expand(-0.6f);
            if (inner.size.x <= 0f || inner.size.z <= 0f) return;
            float maxVol = _interiorBounds.size.x * _interiorBounds.size.y * _interiorBounds.size.z * 3f;
            foreach (var r in Manager.Streamer.MapRoot.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r.transform.IsChildOf(_holder)) continue;
                if (_carsRoot != null && r.transform.IsChildOf(_carsRoot)) continue;
                var b = r.bounds;
                if (!b.Intersects(inner)) continue;
                var sz = b.size;
                if (sz.x * sz.y * sz.z > maxVol || sz.x > _interiorBounds.size.x * 2.5f || sz.z > _interiorBounds.size.z * 2.5f) continue;   // Gelände/große Objekte
                bool centerIn = inner.Contains(b.center);
                var ov = Overlap(b, inner);
                if (!centerIn && ov < 0.5f) continue;
                r.enabled = false;
                _hiddenIntruders.Add(r);
            }
        }

        static float Overlap(Bounds a, Bounds b)
        {
            var mn = Vector3.Max(a.min, b.min); var mx = Vector3.Min(a.max, b.max);
            var d = mx - mn;
            if (d.x <= 0f || d.y <= 0f || d.z <= 0f) return 0f;
            float va = Mathf.Max(1e-4f, a.size.x * a.size.y * a.size.z);
            return d.x * d.y * d.z / va;
        }

        // ---- Interieur-Ausschnitt (Shader TDU2InteriorClip.cginc): Gelände, Meer, LOD-/Nachbarobjekte im Interieur-Quader verwerfen
        static readonly int ClipMatrixId = Shader.PropertyToID("_TDU2ClipMatrix"), ClipExtId = Shader.PropertyToID("_TDU2ClipExt");
        Vector3 _clipCenter, _clipExt; bool _clipValid;
        [Tooltip("Welt (Gelände, Meer, LOD-Objekte) innerhalb des Interieur-Quaders ausblenden")]
        public bool ClipWorldInside = false;
        [Tooltip("Rand des Ausschnitts nach innen (m), damit Gelände direkt vor den Fenstern sichtbar bleibt")]
        public float ClipInset = 0.3f;
        [Tooltip("Auch Welt-Gebäude im Interieur-Quader ausschneiden (aus: nur Gelände, Wasser, Gras, Vegetation – Außengebäude bleiben vollständig)")]
        public bool ClipWorldObjects = false;
        static readonly int ClipObjectsId = Shader.PropertyToID("_TDU2ClipObjects");

        /// <summary>Quader des Interieurs im Raum des Holders (aus den Bounds der Szene).</summary>
        void ComputeClipBox()
        {
            _clipValid = false;
            if (_holder == null || _scene == null || _scene.Root == null) return;
            bool any = false; Vector3 mn = Vector3.zero, mx = Vector3.zero;
            foreach (var r in _scene.Root.GetComponentsInChildren<Renderer>(false))
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? b.max.x : b.min.x, (i & 2) != 0 ? b.max.y : b.min.y, (i & 4) != 0 ? b.max.z : b.min.z);
                    var l = _holder.InverseTransformPoint(c);
                    if (!any) { mn = mx = l; any = true; } else { mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l); }
                }
            }
            if (!any) return;
            _clipCenter = (mn + mx) * 0.5f;
            // Rand nur seitlich (Fenster); nach oben/unten großzügig, sonst schaut Gelände knapp über dem Boden durch
            _clipExt = (mx - mn) * 0.5f - new Vector3(ClipInset, 0f, ClipInset) + new Vector3(0f, 1.0f, 0f);
            _clipValid = _clipExt.x > 0.5f && _clipExt.z > 0.5f;
            UpdateClipBox();
        }

        void UpdateClipBox()
        {
            // Wohnbereich (Garten/Terrasse, offene Räume): die Welt gehört dazu (Haus-Exterior, Nachbarhäuser, Pool) -> kein Ausschnitt
            if (!ClipWorldInside || !_clipValid || _holder == null || _kind == Kind.Living) { Shader.SetGlobalVector(ClipExtId, Vector4.zero); return; }
            var m = Matrix4x4.Translate(-_clipCenter) * _holder.worldToLocalMatrix;   // Floating Origin: jedes Bild neu
            Shader.SetGlobalMatrix(ClipMatrixId, m);
            Shader.SetGlobalFloat(ClipObjectsId, ClipWorldObjects ? 1f : 0f);
            Shader.SetGlobalVector(ClipExtId, new Vector4(_clipExt.x, _clipExt.y, _clipExt.z, 1f));
        }

        // ---- Welt-Kollisionen im Interieur (Gelände, Welt-Gebäude, Vegetation laufen durch unterirdische Garagen/Yachten)
        readonly List<Collider> _hiddenWorldColliders = new List<Collider>();
        float _nextColliderScan;

        /// <summary>Welt-Kollider im Interieur-Quader abschalten (optisch blendet sie der Shader-Ausschnitt schon aus). Nicht im Wohnbereich.</summary>
        void DisableWorldColliders()
        {
            if (!_clipValid || _holder == null || _kind == Kind.Living) return;
            var center = _holder.TransformPoint(_clipCenter);
            var half = new Vector3(_clipExt.x + ClipInset, _clipExt.y, _clipExt.z + ClipInset);
            var hits = UnityEngine.Physics.OverlapBox(center, half, _holder.rotation, ~0, QueryTriggerInteraction.Ignore);
            int n = 0;
            foreach (var c in hits)
            {
                if (c == null || !c.enabled) continue;
                var t = c.transform;
                if (t.IsChildOf(_holder)) continue;
                if (_carsRoot != null && t.IsChildOf(_carsRoot)) continue;
                if (_fp != null && t.IsChildOf(_fp.transform)) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;   // bewegliche Objekte (eigenes Auto)
                c.enabled = false; _hiddenWorldColliders.Add(c); n++;
            }
            if (n > 0) Debug.Log("[TDU2] Interieur: " + n + " Welt-Kollider im Raum abgeschaltet (gesamt " + _hiddenWorldColliders.Count + ")");
        }

        void RestoreIntruders()
        {
            foreach (var c in _hiddenWorldColliders) if (c != null) c.enabled = true;
            _hiddenWorldColliders.Clear();
            _clipValid = false;
            Shader.SetGlobalVector(ClipExtId, Vector4.zero);
            foreach (var r in _hiddenIntruders) if (r != null) r.enabled = true;
            _hiddenIntruders.Clear();
            _haveInteriorBounds = false;
        }

        [Tooltip("Umgebungslicht im Interieur (Möbel/Autos ohne Lightmap; Wände/Böden nutzen die eingebackene Lightmap)")]
        public Color InteriorAmbient = new Color(0.42f, 0.4f, 0.37f);
        [Tooltip("Umgebungslicht in Garagen (im Spiel deutlich heller als Wohnräume/Händler)")]
        public Color GarageAmbient = new Color(0.44f, 0.44f, 0.46f);
        [Tooltip("Mindest-Spiegelanteil des Garagenbodens (0..1)")]
        [Range(0, 1)] public float GarageFloorReflection = 0.45f;
        bool _ambientSet; UnityEngine.Rendering.AmbientMode _savedAmbientMode; Color _savedAmbient; float _savedAmbientIntensity;

        // ------------------------------------------------------------------ Mitspieler im Interieur (TDU2Multiplayer)
        /// <summary>
        /// Kennung des aktuellen Raums (Spot + geladene Bank, z.B. Casino-Halle/Pokerraum getrennt); Mitspieler mit gleicher
        /// Kennung sieht man als Spielfigur. 0 = nicht im Interieur. Häuser (EPH) sind privat: dort nur Mitspieler aus der
        /// eigenen Gruppe (<see cref="IsPrivateRoom"/>).
        /// </summary>
        public uint PresenceKey
        {
            get
            {
                if (!Inside || _holder == null || string.IsNullOrEmpty(_pendingBank)) return 0;
                if (!Online.TDU2OnlineSettings.InteriorShared(_spot.Code, _spotBanks != null && _spotBanks.IsCasino)) return 0;   // TDU2OnlineSettings
                string s = (_spot.Code ?? "").ToUpperInvariant() + "|" + _spot.Key.ToString("X16") + "|" + Path.GetFileNameWithoutExtension(_pendingBank).ToLowerInvariant();
                uint h = 2166136261u;
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return h == 0 ? 1u : h;
            }
        }

        /// <summary>Gast im Haus eines Mitspielers (Einladung): dessen Autos und Beklebungen in der Garage; null = eigene.</summary>
        public List<string> GuestGarageCars;
        public Dictionary<string, string> GuestLiveries;
        /// <summary>Spielernummer des Gastgebers (0 = kein Gast).</summary>
        public int GuestHost;

        /// <summary>Name der geladenen Szene (Bank ohne Endung, z.B. "lph_l1a", "gph_l1a").</summary>
        public string CurrentBankStem => string.IsNullOrEmpty(_pendingBank) ? "" : Path.GetFileNameWithoutExtension(_pendingBank).ToLowerInvariant();

        /// <summary>Lesbarer Ort (Einladung): Garage, Wohnbereich, Casino, Autohändler …</summary>
        public string CurrentPlaceName => Inside ? TitleFor(_kind) : "";

        /// <summary>
        /// Interieur eines Spots betreten und dort die Szene <paramref name="bankStem"/> öffnen (Einladung: wie der Gastgeber
        /// Wohnbereich oder Garage). <paramref name="lastCar"/> = Auto beim Verlassen.
        /// </summary>
        public bool EnterSpotRoom(Spot s, string bankStem, CarPhysics lastCar)
        {
            if (!EnterSpot(s, lastCar)) return false;
            if (string.IsNullOrEmpty(bankStem) || _spotBanks == null) return true;
            string Stem(string p) => string.IsNullOrEmpty(p) ? "" : Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
            string target = null; Kind k = _spotBanks.Kind; string cam = null;
            if (Stem(_spotBanks.Garage) == bankStem) { target = _spotBanks.Garage; k = Kind.Garage; }
            else if (Stem(_spotBanks.Living) == bankStem) { target = _spotBanks.Living; k = _spotBanks.Kind; }
            if (target != null && !string.Equals(target, _pendingBank, StringComparison.OrdinalIgnoreCase)) LoadScene(target, k, cam);
            return true;
        }

        /// <summary>Haus (EPH): Mitspieler nur auf Einladung (Gruppe).</summary>
        public bool IsPrivateRoom => Inside && Online.TDU2OnlineSettings.HousesInviteOnly
                                     && (_spot.Code ?? "").StartsWith("EPH", StringComparison.OrdinalIgnoreCase)
                                     && !(_spot.Code ?? "").StartsWith("EPHOT", StringComparison.OrdinalIgnoreCase);

        /// <summary>Eigene Position (Füße) und Blickrichtung (Grad um Y) im Raum der Interieur-Szene.</summary>
        public bool LocalPresence(out Vector3 local, out float yaw)
        {
            local = default; yaw = 0f;
            if (_holder == null || _fp == null) return false;
            local = _holder.InverseTransformPoint(_fp.transform.position);
            var cam = _fp.Cam != null ? _fp.Cam.transform : _fp.transform;
            var f = _holder.InverseTransformDirection(cam.forward); f.y = 0f;
            yaw = f.sqrMagnitude > 1e-6f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : 0f;
            return true;
        }

        [Tooltip("Avatare im Interieur: weiches Deckenlicht statt Sonne (Farbe, Richtung) und Umgebung oben/unten")]
        public Color AvatarIndoorLight = new Color(0.5f, 0.47f, 0.43f);
        public Vector3 AvatarIndoorDir = new Vector3(0.25f, 1f, 0.35f);
        public Color AvatarIndoorAmbTop = new Color(0.46f, 0.44f, 0.41f), AvatarIndoorAmbBottom = new Color(0.2f, 0.19f, 0.18f);

        /// <summary>Avatar-Shader (TDU2/Avatar): drinnen kein Sonnenlicht/-schatten, sondern Deckenlicht + Verlauf + Kontaktschatten.</summary>
        void SetAvatarIndoor(bool on)
        {
            Shader.SetGlobalFloat("_TDU2AvatarIndoor", on ? 1f : 0f);
            if (!on) return;
            var d = AvatarIndoorDir.sqrMagnitude > 1e-4f ? AvatarIndoorDir.normalized : Vector3.up;
            Shader.SetGlobalVector("_TDU2IndoorDir", new Vector4(d.x, d.y, d.z, 0f));
            Shader.SetGlobalColor("_TDU2IndoorLight", AvatarIndoorLight);
            Shader.SetGlobalColor("_TDU2IndoorAmbTop", AvatarIndoorAmbTop);
            Shader.SetGlobalColor("_TDU2IndoorAmbBottom", AvatarIndoorAmbBottom);
        }

        void SetInteriorAmbient(bool on)
        {
            SetAvatarIndoor(on);
            if (on && !_ambientSet)
            {
                _savedAmbientMode = RenderSettings.ambientMode; _savedAmbient = RenderSettings.ambientLight; _savedAmbientIntensity = RenderSettings.ambientIntensity;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = InteriorAmbient;
                _ambientSet = true;
            }
            else if (!on && _ambientSet)
            {
                RenderSettings.ambientMode = _savedAmbientMode; RenderSettings.ambientLight = _savedAmbient; RenderSettings.ambientIntensity = _savedAmbientIntensity;
                _ambientSet = false;
            }
        }

        /// <summary>ESC gehört gerade dem Interieur (eigenes Menü offen/gerade geschlossen, Sticker-Shop).</summary>
        public bool EscOwned => _menuOpen || Time.frameCount - _menuClosedFrame <= 1 || TDU2StickerShop.Busy || TDU2ClothesShop.Busy || TDU2Hairdresser.Busy
                                || TopMenuLayer() != MenuLayer.None;   // Film-Menüs: ESC gehört dem zentralen Menüablauf (TDU2MenuFlow.cs)
        int _menuClosedFrame = -10, _relockFrame = -10;

        /// <summary>Kollision für das Laufen, keine Schatten (Dach würde den Raum verdunkeln).</summary>
        /// <summary>Flache, waagrechte Fläche (Boden) – bekommt auch mit transparentem Material eine Kollision.</summary>
        static bool IsFlatFloor(MeshFilter mf)
        {
            var b = mf.GetComponent<Renderer>() != null ? mf.GetComponent<Renderer>().bounds : new Bounds();
            if (b.size == Vector3.zero) return false;
            float h = b.size.y, w = Mathf.Min(b.size.x, b.size.z);
            return w > 1.5f && h < 0.35f && h < w * 0.1f;
        }

        static void PrepareStatic(Transform root, bool collideTransparent = false)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!UnityUtil.IsCollidable(mf.sharedMesh)) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                // Glas/Hologramme ohne Kollision – außer flachen Böden (Casino: spiegelnder/transparenter Hallenboden, Pool-Abdeckung)
                if (mr != null && mr.sharedMaterial != null && mr.sharedMaterial.renderQueue >= 2900 && !collideTransparent && !IsFlatFloor(mf)) continue;
                if (mr != null && !mr.enabled) continue;   // ausgeblendete Hilfskörper (z.B. CubeCar) – keine unsichtbaren Wände/Platten
                if (mf.GetComponent<Collider>() == null)
                {
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }

        void PollFurniture()
        {
            if (_furnTask == null || !_furnTask.IsCompleted) return;
            var t = _furnTask; _furnTask = null;
            if (t.IsFaulted) return;
            if (_holder == null) { foreach (var (_, m) in t.Result) m?.Release(); return; }
            foreach (var (e, m) in t.Result)
            {
                var anchor = new GameObject("Furniture " + e.Name).transform;
                anchor.SetParent(_holder, false);
                anchor.localPosition = e.Position;
                anchor.localRotation = e.M.rotation;
                anchor.localScale = e.M.lossyScale;
                bool glow = World.MaterialFactory.ShowGlowMaterials;
                World.MaterialFactory.ShowGlowMaterials = true;
                TDU2MapStreamer.SceneHandle h;
                try { h = Manager.Streamer.BuildScene(m, anchor, e.Name); }
                finally { World.MaterialFactory.ShowGlowMaterials = glow; }
                InteriorFx.Apply(null, anchor);
                _furniture.Add(h);
                PrepareStatic(anchor);
                RegisterHouseObject(e, anchor, h);
            }
        }

        // ------------------------------------------------------------------ Ausstellungsautos
        void PollCars()
        {
            if (_carTask != null && _carTask.IsCompleted)
            {
                var t = _carTask; var info = _carTaskInfo; _carTask = null;
                if (t.IsFaulted)
                {
                    DebugManager.Error("Ausstellungsauto " + info.car?.FileName + ": " + t.Exception?.GetBaseException().Message);
                }
                else if (info.gen != _gen || _holder == null) t.Result?.DestroyTextures();
                else
                {
                    try { BuildDisplayCar(t.Result, info.car, info.slot); }
                    catch (Exception ex)
                    {
                        DebugManager.Error("Ausstellungsauto " + info.car?.FileName + ": " + ex.Message);
                    }
                }
            }
            if (_carTask == null && _carQueue.Count > 0 && _holder != null && _sceneTask == null)
            {
                var (car, slot) = _carQueue.Dequeue();
                string bnk = Manager.BnkDir; var db = Manager.Database;
                _carTaskInfo = (car, slot, _gen);
                // eigene Autos (Garage, Sticker-Shop): gekaufte Ausstattung; Händler und Gast-Garage: Werkszustand
                int col = 0, rim = 0, intr = 0;
                if (_kind != Kind.Dealer && GuestGarageCars == null && TDU2CarChoices.TryGet(car.FileName, out var ch)) { col = ch.Color; rim = ch.Rim; intr = ch.Interior; }
                _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, col, false, rim, intr));
            }
        }

        void BuildDisplayCar(CarAssets a, CarPhysics car, InteriorScene.Ent slot)
        {
            var pos = _holder.TransformPoint(slot.Position);
            // Casino-Lobby: der Drehteller (Socle_Car) steht in den Daten unten im Schacht (Hubbühne, im Spiel per Animation
            // hochgefahren) -> Oberkante auf die Höhe des Auto-Platzes heben
            Transform socle = slot is InteriorScene.ShowCar ? FindSocle(slot.Position) : null;
            if (socle != null)
            {
                bool any = false; var sb = new Bounds();
                foreach (var r in socle.GetComponentsInChildren<Renderer>()) { if (!any) { sb = r.bounds; any = true; } else sb.Encapsulate(r.bounds); }
                if (any)
                {
                    // Teller mittig in den Schacht (Auto-Platz), Oberkante bündig mit dem Hallenboden = Höhe des Actors Socle_Car
                    // (hochgefahrene Hubbühne; das Auto steht dann auf dem Teller)
                    float top = _holder.TransformPoint(socle.localPosition).y;
                    socle.position += new Vector3(pos.x - sb.center.x, top - sb.max.y, pos.z - sb.center.z);
                    UnityEngine.Physics.SyncTransforms();
                }
            }
            var fwd = _holder.TransformDirection(slot.Forward); fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var go = new GameObject("Display " + car.ModelName);
            go.transform.SetParent(_carsRoot, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd.normalized, Vector3.up));
            var model = CarModel.Build(a, go.transform);
            // Teile mit unbrauchbarer Geometrie ausblenden (z.B. R8 Spyder: gehäutete Verdeck-Teile ohne Skinning -> riesige Spitzen,
            // die das Auto beim Aufstellen in die Luft heben)
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var s = r.bounds.size;
                if (s.x > 7f || s.y > 4f || s.z > 9f)
                {
                    r.enabled = false;
                    DebugManager.Warning("Auto " + car.ModelName + ": Teil '" + r.name + "' ausgeblendet (Größe " + s + ") not Implemented");
                }
            }
            // eigene Autos (Garage, Sticker-Shop): gespeicherte Lackierung/Beklebung zeigen
            if (GuestGarageCars != null && _kind == Kind.Garage)
            {
                // Gast: Lackierung/Beklebung des Gastgebers (aus seinem Spielstand)
                if (GuestLiveries != null && GuestLiveries.TryGetValue(car.FileName, out var lv) && !string.IsNullOrEmpty(lv))
                {
                    try
                    {
                        var l = CarLivery.Decode(Convert.FromBase64String(lv));
                        if (l != null) { TDU2StickerLibrary.Ensure(Manager.BnkDir); TDU2LiveryView.For(model)?.Show(l); }
                    }
                    catch (Exception ex)
                    {
                        DebugManager.Warning("Gast-Garage Beklebung " + car.FileName + ": " + ex.Message);
                    }
                }
            }
            else if (_kind == Kind.Garage || _kind == Kind.StickerShop) TDU2LiveryView.ApplyOwn(model, car.FileName);

            // auf den Boden stellen: Unterkante der Räder/Karosserie auf den Hallenboden
            var rs = Array.FindAll(go.GetComponentsInChildren<Renderer>(), r => r.enabled);
            if (rs.Length > 0)
            {
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                float floor = FloorBelow(pos);
                // Aufstandspunkt = Unterkante der Reifen (Schattenflächen, Spoiler, Auspuff o.ä. unterhalb hoben das Auto sonst an)
                bool tires = false; var tb = new Bounds();
                foreach (var r in rs)
                {
                    if (!IsTirePart(r.transform, go.transform)) continue;
                    if (!tires) { tb = r.bounds; tires = true; } else tb.Encapsulate(r.bounds);
                }
                float bottom = tires ? tb.min.y : b.min.y;
                go.transform.position += Vector3.up * (floor - bottom);
                // Bodenschatten wie draußen (Schattentextur des Autos), sonst "schwebt" das Auto optisch
                TDU2VehicleEffects.AddStaticShadow(go.transform, model, a, floor, (tires ? tb.center : b.center) + Vector3.up * (floor - bottom));
            }
            // Kollision: Karosserie ohne ausgeblendete Teile, im Fahrzeugraum (dreht mit dem Auto); Unterkante etwas angehoben,
            // damit man nicht an Schweller/Spoiler hängen bleibt
            var box = go.AddComponent<BoxCollider>();
            var bb = model.RecomputeBodyBounds();
            float lift = Mathf.Min(0.15f, bb.size.y * 0.15f);
            box.center = bb.center + Vector3.up * (lift * 0.5f);
            box.size = new Vector3(bb.size.x * 0.96f, Mathf.Max(0.3f, bb.size.y - lift), bb.size.z * 0.98f);
            var dc = new DisplayCar
            {
                Car = car,
                Go = go,
                Model = model,
                Assets = a,
                Col = box,
                Slot = slot,
                Choice = new CarChoice(ColorIndexOf(a), a.Rim?.Id ?? 0, a.Interior?.Id ?? 0)
            };
            if (slot is InteriorScene.ShowCar show)
            {
                dc.Lobby = true;
                dc.DegPerSec = show.TurnsPerMinute * 6f;
                dc.Socle = socle;
                if (socle != null) dc.AxisLocal = socle.InverseTransformPoint(go.transform.position);
                var cb = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.zero);
                foreach (var r in rs) cb.Encapsulate(r.bounds);
            }
            _cars.Add(dc);
        }

        /// <summary>
        /// Hallenboden unter einem Stellplatz: nächster Treffer nach unten, Ausstellungsautos (Kollisionsboxen unter _carsRoot)
        /// zählen nicht – sonst stellte der Neuaufbau beim Felgen-/Lackwechsel das Auto auf das Dach des alten.
        /// </summary>
        float FloorBelow(Vector3 pos)
        {
            var hits = UnityEngine.Physics.RaycastAll(pos + Vector3.up * 1.5f, Vector3.down, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, floor = pos.y;
            foreach (var h in hits)
            {
                if (_carsRoot != null && h.collider.transform.IsChildOf(_carsRoot)) continue;
                if (h.distance < best) { best = h.distance; floor = h.point.y; }
            }
            return floor;
        }

        /// <summary>Index der Lackfarbe des Autos in DBCarsAndColors (bzw. des Basis-Autos bei Sondermodellen).</summary>
        int ColorIndexOf(CarAssets a)
        {
            var db = Manager?.Database;
            if (db == null || a?.Color == null || a.Car == null) return 0;
            int dup = a.Car.Raw != null ? a.Car.Raw.I("_duplicate", 0) : 0;
            if ((db.Colors.TryGetValue(a.Car.Id, out var cols) && cols.Count > 0) || (dup != 0 && db.Colors.TryGetValue(dup, out cols)))
                return Mathf.Max(0, cols.IndexOf(a.Color));
            return 0;
        }

        /// <summary>Gehört der Renderer zu einem Rad (Knoten Tire / D_TIRE_xx / D_WHEEL_xx / D_RIM_xx)?</summary>
        static bool IsTirePart(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
            {
                string n = t.name ?? "";
                if (n == "Tire" || n.StartsWith("D_TIRE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("D_WHEEL", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("D_RIM", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Diagnose (Casino): geladene Szenen-Teile, Renderer/Kollider, Ladefehler der Bank.</summary>
        void LogSceneDiag(BankModel model, InteriorScene data)
        {
            try
            {
                int sets = 0, inst = 0;
                foreach (var set in model.AllObjectSets()) { sets++; inst += set.Placements != null ? set.Placements.Count : 1; }
                int rs = _holder.GetComponentsInChildren<Renderer>(true).Length, cs = _holder.GetComponentsInChildren<Collider>(true).Length;
                var sb = new System.Text.StringBuilder();
                sb.Append("[TDU2] Casino-Diagnose ").Append(Path.GetFileNameWithoutExtension(_pendingBank))
                  .Append(": ").Append(sets).Append(" Szenen, ").Append(inst).Append(" Platzierungen, ").Append(rs).Append(" Renderer, ")
                  .Append(cs).Append(" Kollider, ").Append(data.Selectables.Count).Append(" Hotspots, ").Append(data.ShowCars.Count).Append(" Ausstellungsautos");
                if (model.Log.Count > 0) { sb.Append("\nLadefehler:"); for (int i = 0; i < model.Log.Count && i < 15; i++) sb.Append("\n  ").Append(model.Log[i]); }
                Debug.Log(sb.ToString());
            }
            catch (Exception ex)
            {
                DebugManager.Error("Casino-Diagnose: " + ex.Message);
            }
        }

        /// <summary>Drehteller unter einem Ausstellungsauto: Szenen-Teil (Actor) mit gleicher Lage in X/Z.</summary>
        Transform FindSocle(Vector3 slotLocal)
        {
            if (_scene == null || _scene.Root == null) return null;
            Transform best = null; float bd = 0.6f * 0.6f;
            foreach (Transform t in _scene.Root.transform)
            {
                var p = t.localPosition;
                float dx = p.x - slotLocal.x, dz = p.z - slotLocal.z, d = dx * dx + dz * dz;
                if (d < bd && Mathf.Abs(p.y - slotLocal.y) < 2f && t.GetComponentInChildren<Renderer>() != null) { bd = d; best = t; }
            }
            return best;
        }

        // ------------------------------------------------------------------ Casino-Halle: Ranglisten-Monitor auf Hubsäule
        // icashal: FlashScreen_CRNK_TimeAttack (Monitor, Actor bei 0.54/0.58/-30.39) auf TubeEcransRankings (Säule, steht in den
        // Daten im Schacht). Im Spiel fährt der Monitor hoch, wenn man ihn ansieht (CAM_RANKINGS), sonst ist er im Boden versenkt.
        Transform _liftTv, _liftTube; float _liftDown, _liftK, _liftLookUntil;
        static readonly Vector3 LiftTvPos = new Vector3(0.54f, 0.58f, -30.39f), LiftTubePos = new Vector3(0.54f, -3.62f, -30.47f);

        Transform SceneHolderAt(Vector3 local, float tol)
        {
            if (_scene == null || _scene.Root == null) return null;
            Transform best = null; float bd = tol * tol;
            foreach (Transform t in _scene.Root.transform)
            {
                float d = (t.localPosition - local).sqrMagnitude;
                if (d < bd && t.GetComponentInChildren<Renderer>() != null) { bd = d; best = t; }
            }
            return best;
        }

        static bool WorldBounds(Transform t, out Bounds b)
        {
            b = new Bounds(); bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>()) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            return any;
        }

        void SetupRankingLift()
        {
            _liftTv = _liftTube = null; _liftK = 0f;
            if (_spotBanks == null || !_spotBanks.IsCasino || _pendingBank == null
                || !Path.GetFileNameWithoutExtension(_pendingBank).Equals("icashal", StringComparison.OrdinalIgnoreCase)) return;
            _liftTv = SceneHolderAt(LiftTvPos, 0.3f);
            _liftTube = SceneHolderAt(LiftTubePos, 0.3f);
            if (_liftTv == null || !WorldBounds(_liftTv, out var tb)) { _liftTv = null; return; }
            float floor = _holder.TransformPoint(new Vector3(0f, -0.99f, 0f)).y;   // Hallenboden (Oberkante Socle_Car)
            if (_liftTube != null && WorldBounds(_liftTube, out var ub))
                _liftTube.position += Vector3.up * (tb.min.y - ub.max.y);       // Säule unter den Monitor
            _liftDown = tb.max.y - floor + 0.05f;                               // so weit versenken, dass die Oberkante im Boden liegt
            MoveLift(-_liftDown);
        }

        float _liftOffset;
        void MoveLift(float offset)
        {
            float d = offset - _liftOffset; _liftOffset = offset;
            if (Mathf.Abs(d) < 1e-5f) return;
            if (_liftTv != null) _liftTv.position += Vector3.up * d;
            if (_liftTube != null) _liftTube.position += Vector3.up * d;
        }

        void UpdateRankingLift()
        {
            if (_liftTv == null || _fp == null) return;
            var ray = _fp.AimRay;
            var target = _holder.TransformPoint(LiftTvPos);
            var to = target - ray.origin;
            bool look = to.magnitude < 14f && Vector3.Angle(ray.direction, to) < 25f;
            if (look) _liftLookUntil = Time.time + 2.5f;
            float want = Time.time < _liftLookUntil ? 1f : 0f;
            _liftK = Mathf.MoveTowards(_liftK, want, Time.deltaTime / 1.5f);
            float s = _liftK * _liftK * (3f - 2f * _liftK);
            MoveLift(-_liftDown * (1f - s));
        }

        void SpinShowCars()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            foreach (var c in _cars)
            {
                if (c.DegPerSec == 0f || c.Go == null) continue;
                var axis = c.Socle != null ? c.Socle.TransformPoint(c.AxisLocal) : c.Go.transform.position;
                c.Go.transform.RotateAround(axis, Vector3.up, c.DegPerSec * dt);
                if (c.Socle != null) c.Socle.RotateAround(axis, Vector3.up, c.DegPerSec * dt);
            }
        }

        // ------------------------------------------------------------------ Drinnen
        void UpdateInside()
        {
            SpinShowCars();
            UpdateRankingLift();
            _aimLabel = null; _aimTarget = null;
            CatchFall();
            UpdateClipBox();
            if (Time.unscaledTime >= _nextColliderScan) { _nextColliderScan = Time.unscaledTime + 0.5f; DisableWorldColliders(); }
            if (Time.unscaledTime >= _nextWorldScan) { _nextWorldScan = Time.unscaledTime + 1.5f; HideWorld(); }   // nachgeladene Sektoren
            if (TDU2GameData.ChatTyping) return;   // Chat hat die Tastatur
            UpdateMenuFlow();                      // ESC/Rücktaste aller Film-Menüs an einer Stelle (TDU2MenuFlow.cs)
            if (_casinoMenu) { UpdateCasinoMenu(); return; }
            if ((_myHouse != HouseMode.None || _houseRestoreAt > 0f) && UpdateMyHouse()) return;
            if (_diaryOpen) { UpdateDiary(); return; }
            if (_dealerMenu)
            {
                // Autohändler/Garage: Mini-Menü, Kauffenster, Daten, Einsteigen (TDU2CarDealer.cs). Ein Fehler darf das Spiel nicht
                // im Menü festhalten -> Menü schließen, Laufmodus.
                try { UpdateDealerMenu(); }
                catch (Exception ex)
                {
                    DebugManager.Error("UpdateInside: " + ex.Message);
                    try
                    {
                        DealerForceClose();
                    }
                    catch (Exception)
                    {
                        _dealerMenu = false;
                        CasinoMenuOpen = false;
                        RestoreWalkState();
                    }
                }
                return;
            }
            // Fremdes Menü offen (Rennschule, Ergebnis, Flughafen …): ESC gehört dem Menü, Maus frei, keine Umsicht/Auswahl
            if (Racing.RacingSchoolMenu.BlocksPauseMenu)
            {
                if (_fp != null) _fp.LookEnabled = false;
                if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                return;
            }
            if (TDU2StickerShop.Busy) { if (_fp != null && TDU2StickerShop.IsOpen) _fp.LookEnabled = false; return; }
            if (TDU2ClothesShop.Busy) { if (_fp != null && TDU2ClothesShop.IsOpen) _fp.LookEnabled = false; return; }
            if (TDU2Hairdresser.Busy) { if (_fp != null && TDU2Hairdresser.IsOpen) _fp.LookEnabled = false; return; }
            if (Time.timeScale <= 0f) { if (_fp != null) _fp.LookEnabled = false; return; }   // Pausemenü offen
#if ENABLE_LEGACY_INPUT_MANAGER
            // ESC: eigenes Auswahlmenü schließen, sonst öffnet das Pausemenü (TDU2PauseMenu) wie draußen
            if (Input.GetKeyDown(KeyCode.Escape) && _menuOpen) { CloseMenu(); _menuClosedFrame = Time.frameCount; }
            // Maus nach dem Pausemenü wieder fangen (Klick ins Bild)
            if (!_menuOpen && Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0) && Time.timeScale > 0f) { LockCursor(true); _relockFrame = Time.frameCount; }
#endif
            // Laufmodus ohne Menü: Maus immer wieder fangen (ESC gibt sie im Editor frei – danach hing man ohne Umsehen fest)
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!_menuOpen && Cursor.lockState != CursorLockMode.Locked && Time.timeScale > 0f && !Input.GetKey(KeyCode.Escape)) LockCursor(true);
#endif
            if (_fp != null) _fp.LookEnabled = !_menuOpen && Cursor.lockState == CursorLockMode.Locked;
            if (_menuOpen || _holder == null || _data == null || _fp == null || _sceneTask != null) return;

            var ray = _fp.AimRay;
            float bestT = InteractDistance;
            // Autos: wie im Spiel über die Hülle des Autos (nicht nur der erste Treffer – Türen, Scheiben, Räder oder der
            // Boden davor fingen den Strahl ab). Das schon anvisierte Auto bleibt mit etwas größerer Hülle gewählt, damit der
            // Cursor beim leichten Umsehen nicht ständig wechselt (jeder Wechsel spielt die Film-Animation neu).
            DisplayCar aimCar = null; float carT = CarAimDistance;
            foreach (var c in _cars)
            {
                if (c == null || c.Col == null || c.Go == null) continue;
                var b = c.Col.bounds;
                b.Expand(ReferenceEquals(c, _uiAim) ? 0.5f : 0.15f);
                if (b.IntersectRay(ray, out float ct) && ct < carT) { carT = ct; aimCar = c; }
            }
            if (aimCar != null && carT > 0.5f && UnityEngine.Physics.Raycast(ray, out var hit, carT, ~(1 << 2), QueryTriggerInteraction.Ignore)
                && !IsCarCollider(hit.collider) && _holder != null && hit.transform.IsChildOf(_holder) && hit.distance < carT - 0.5f)
                aimCar = null;   // Wand/Säule davor
            if (aimCar != null)
            {
                var c = aimCar;
                _aimTarget = c; bestT = Mathf.Max(carT, 0.5f); _aimCarDist = carT;
                _aimLabel = _kind == Kind.Dealer ? (_starterCars != null ? $"{c.Car.ModelName} wählen und losfahren" : $"{c.Car.ModelName}: Kaufen / Testfahrt") : _kind == Kind.StickerShop ? "Lackieren & bekleben" : $"Mit {c.Car.ModelName} losfahren";
            }
            // Hotspots (FpSelectable-Volumen, Spielraum der Szene)
            var o = _holder.InverseTransformPoint(ray.origin);
            var d = _holder.InverseTransformDirection(ray.direction);
            foreach (var s in _data.Selectables)
            {
                if (!s.Raycast(o, d, out float t) || t > bestT) continue;
                // verdeckt durch Wände?
                if (t > 0.3f && UnityEngine.Physics.Raycast(ray, out var wall, t, ~(1 << 2), QueryTriggerInteraction.Ignore) && wall.distance < t - 0.6f && _aimTarget == null) continue;
                if (!IsUsable(s)) continue;
                bestT = t; _aimTarget = s; _aimLabel = InteriorScene.Label(s);
            }
            UpdateUiAim(_aimTarget);
            // Fahrzeugdaten rechts (und Preis): öffnet der Film über Cursor "info"; ist danach noch nichts offen, selbst öffnen
            if (_infoHiddenFor != null && !ReferenceEquals(_infoHiddenFor, _uiAim)) _infoHiddenFor = null;
            if (_aimInfoAt > 0f && Time.unscaledTime >= _aimInfoAt && _uiAim is DisplayCar && _ui != null)
            {
                _aimInfoAt = -1f;
                string cl = _ui.Label("/car_datas") ?? "";
                if (cl == "" || cl == "default" || cl == "close")
                {
                    _ui.SetValue("/:ItemInfo", true);
                    _ui.GotoLabel("/car_datas", "open", true);
                    if (_kind == Kind.Dealer) _ui.GotoLabel("/price", "open", true);
                }
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            // Händler/Garage: anvisierte Tür bzw. Scheibe mit Rechtsklick oder der Türen-Taste öffnen/schließen, Fenster-Taste = alle Fenster
            if (_aimTarget is DisplayCar ac && ac.Model != null && !ac.Lobby && (_kind == Kind.Dealer || _kind == Kind.Garage))
            {
                var acc = TDU2Accessories.For(ac.Model, null);
                // wie im Spiel nur über das Fahrzeugmenü der Hilfeleiste (kein Rechtsklick auf Tür/Scheibe)
                // Fahrzeugmenü des Films (Steuerkreuz unten links): Num4/Num6 Tür links/rechts, Num8/Num2 Fenster hoch/runter
                if (CarPad(acc)) _netCar = ac;
            }
            bool use = Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || (Cursor.lockState == CursorLockMode.Locked && Input.GetMouseButtonDown(0) && Time.frameCount != _relockFrame);
            //if (use) Debug.Log("[TDU2] Auswahl-Taste: Ziel " + AimName(_aimTarget));
            // Rücktaste im Laufmodus: Fahrzeugdaten/Vergleich ausblenden, bis ein anderes Ziel anvisiert wird
            if (Input.GetKeyDown(KeyCode.Backspace) && _uiAim is DisplayCar && _ui != null)
            {
                _ui.GotoLabel("/car_datas", "close", true);
                if (_kind == Kind.Dealer) _ui.GotoLabel("/price", "close", true);
                _ui.SetValue("/:ItemInfo", false);
                _aimInfoAt = -1f; _infoHiddenFor = _uiAim;
            }
            if (use && _aimTarget != null) Activate(_aimTarget);
#endif
        }

        // ------------------------------------------------------------------ Original-Flash-Oberfläche (frontend/<res>/shopscar.bnk, home.bnk)
        VmfPlayer _ui;
        GameObject _uiGo;
        string _uiBank;
        object _uiAim;
        /// <summary>Autos nur aus der Nähe anvisieren (m vom Auge bis zur Hülle) – wie im Spiel: man steht vor dem Auto.</summary>
        const float CarAimDistance = 3.2f;
        float _aimCarDist;
        float _aimInfoAt = -1f;
        object _infoHiddenFor;
        // "help": Hilfeleiste unten – beim Anvisieren eines Autos zeigt der Film dort das Fahrzeugmenü (SetHelp("options_menu"):
        // Steuerkreuz links = Tür links, rechts = Tür rechts, oben/unten = Fenster hoch/runter)
        static readonly HashSet<string> UiVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cursor", "title_bar", "car_datas", "price", "help" };

        string FrontendBank(string name)
        {
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                var f = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), name);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>
        /// Lädt den Original-Screen: Händler = shopscar.vmf (Label car_dealer/cd_stop), Haus/Garage = Home.vmf (living_stop/garage_stop).
        /// Sichtbar bleiben nur cursor (Fadenkreuz + Hotspot-Leiste mit Icon), title_bar, car_datas (Fahrzeugdaten) und price.
        /// </summary>
        void LoadUi(Kind kind)
        {
            bool casino = _spotBanks != null && _spotBanks.IsCasino;
            string bankName = casino ? "casino.bnk" : kind == Kind.Dealer || kind == Kind.StickerShop ? "shopscar.bnk" : (kind == Kind.Living || kind == Kind.Garage) ? "home.bnk" : null;
            string bank = bankName != null ? FrontendBank(bankName) : null;
            if (bank == null) { DestroyUi(); return; }
            try
            {
                if (_ui == null || _uiBank != bank)
                {
                    DestroyUi();
                    _uiGo = new GameObject("TDU2 Interior UI");
                    _ui = _uiGo.AddComponent<VmfPlayer>();
                    _ui.GuiDepth = -1;
                    _ui.Load(bank);
                    _uiBank = bank;
                    _ui.FSCommand += OnUiCommand;
                }
                if (kind == Kind.StickerShop) { LoadShopUi(); return; }
                if (casino) PrepareCasinoUi();
                string stop = casino ? "casino_hall" : kind == Kind.Dealer ? "cd_stop" : kind == Kind.Garage ? "garage_stop" : "living_stop";
                _ui.GotoLabel("/", stop, false);
                foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
                _ui.SetVisible("/car_datas/datas_base/limits", false);
                _ui.SetVariable("/:Title", TitleFor(kind));
                _ui.SetVariable("/:MyMoneyStr", " ");
                _ui.SetVariable("/:ClubMoney", " ");
                _ui.GotoLabel("/title_bar", "open", true);
                //Debug.Log("[TDU2] Interieur-Oberfläche: " + bankName + " (" + kind + ")");
                //_ui.GotoLabel("/title_bar/ic", kind == Kind.Garage ? "garage" : kind == Kind.Living ? "living" : "home", true);
                _ui.GotoLabel("/title_bar/ic", "garage", false);
                _ui.GotoLabel("/cursor", "valid", true);
                //_ui.SetPosition("/cursor/low_bar/ic", new Vector2(-39.4f, -539.3f));
                _ui.SetVisible("/cursor/low_bar/top_bar", false);
                _ui.GotoLabel("/car_datas", "default", false);
                _ui.GotoLabel("/price", "default", false);
                _uiAim = null;
            }
            catch (Exception ex)
            {
                DebugManager.Error("Interieur-Oberfläche (" + bankName + "): " + ex.Message);
                DestroyUi();
            }
        }

        /// <summary>
        /// Sticker-Shop: shopscar.vmf ab Label "st_open" (WhatPage "stickers", Titel m_StickersShop_), im Laufmodus nur Fadenkreuz +
        /// Titel; Menü/Lack/Sticker/Rechnung öffnet <see cref="TDU2StickerShop"/> (Verkäuferin Typ 28 oder das Auto).
        /// </summary>
        void LoadShopUi()
        {
            var texts = FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage());
            FlashLocalization.Apply(_ui, texts);
            _ui.MouseEnabled = true;
            _ui.SetValue("/:fake", false);
            _ui.SetValue("/:IsTutorial", 0);
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.GotoLabel("/", "st_open", true);
            foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && (d.Name == "cursor" || d.Name == "title_bar");
            string title = texts != null && texts.TryGetValue("m_StickersShop_", out var t) && !string.IsNullOrEmpty(t) ? t : "Sticker-Shop";
            _ui.SetVariable("/:Title", title);
            _ui.SetVariable("/:MyMoneyStr", Online.TDU2Session.Save != null ? "$ " + Online.TDU2Session.Save.money.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) : " ");
            _ui.SetVariable("/:ClubMoney", " ");
            _ui.GotoLabel("/cursor", "valid", true);
            _ui.SetVisible("/cursor/low_bar/top_bar", false);
            _uiAim = null;
        }

        /// <summary>Film der Interieur-Oberfläche (Sticker-Shop).</summary>
        internal VmfPlayer Ui => _ui;

        /// <summary>Ich-Perspektive an/aus (Sticker-Shop übernimmt die Kamera).</summary>
        public void SetFirstPerson(bool on)
        {
            if (_fp != null) { _fp.enabled = on; _fp.LookEnabled = on && Cursor.lockState == CursorLockMode.Locked; }
            if (on && Inside) LockCursor(true);
        }

        TDU2StickerShop _shop;

        /// <summary>Sticker-Shop-Menü öffnen (Verkäuferin Typ 28 oder Klick aufs Auto).</summary>
        void OpenStickerShop()
        {
            if (_ui == null) { ShowInfo("Sticker-Shop: shopscar.bnk fehlt"); return; }
            DisplayCar dc = _cars.Count > 0 ? _cars[0] : null;
            if (dc == null || dc.Model == null) { ShowInfo("Sticker-Shop: Auto wird noch geladen …"); return; }
            if (_shop == null)
            {
                _shop = GetComponentInChildren<TDU2StickerShop>();
                if (_shop == null)
                {
                    var go = new GameObject("TDU2 Sticker Shop");
                    go.transform.SetParent(transform, false);
                    _shop = go.AddComponent<TDU2StickerShop>();
                }
            }
            _shop.Interiors = this;
            if (_uiAim != null) { UpdateUiAim(null); }
            _shop.Open(_ui, dc.Car, dc.Model, dc.Go.transform, dc.Col);
        }

        TDU2ClothesShop _clothes;
        TDU2InteriorNpcs _npcs;
        Action _afterBuild;
        bool _dressing;

        /// <summary>Figuren und Cinematic-Szenen des geladenen Interieurs.</summary>
        public TDU2InteriorNpcs Npcs
        {
            get
            {
                if (_npcs == null) { _npcs = GetComponent<TDU2InteriorNpcs>(); if (_npcs == null) _npcs = gameObject.AddComponent<TDU2InteriorNpcs>(); }
                return _npcs;
            }
        }

        /// <summary>Wurzel der Interieur-Szene (Spielraum der Szene, Elternteil der Figuren).</summary>
        public Transform SceneRoot => _holder;

        TDU2ClothesShop ClothesShop
        {
            get
            {
                if (_clothes == null)
                {
                    _clothes = GetComponentInChildren<TDU2ClothesShop>();
                    if (_clothes == null)
                    {
                        var go = new GameObject("TDU2 Clothes Shop");
                        go.transform.SetParent(transform, false);
                        _clothes = go.AddComponent<TDU2ClothesShop>();
                    }
                    _clothes.Closed += OnClothesClosed;
                }
                _clothes.Interiors = this;
                return _clothes;
            }
        }

        /// <summary>
        /// Kleidungsladen (EAS_*, Verkäuferin Typ 22): eigener Film frontend/&lt;res&gt;/shopsavatar.bnk, Kollektion aus der
        /// Verkäuferin (DBPNJ), sonst aus der Spot-Kategorie. false = kein Kleidungsladen (andere Typ-22-Punkte).
        /// </summary>
        bool OpenClothesShop()
        {
            string code = (_spot?.Code ?? "").ToLowerInvariant();
            if (!code.StartsWith("eas")) return false;
            string bank = FrontendBank("shopsavatar.bnk");
            if (bank == null) { ShowInfo("Kleidungsladen: frontend/shopsavatar.bnk fehlt"); return true; }
            if (!Avatars.AvatarLibrary.Ready) { ShowInfo("Kleidungsladen: Avatar-Daten werden noch geladen …"); return true; }
            _aimLabel = null; _aimTarget = null;
            if (!ClothesShop.Open(TDU2ClothesShop.ShopMode.Shop, bank, Npcs, (_spot.TypeA >> 8) & 0xFF)) ShowInfo("Kleidungsladen: Oberfläche konnte nicht geladen werden");
            return true;
        }

        /// <summary>
        /// Garderobe (Haus, Typ 10): Ankleidezimmer Interior/IDR_&lt;x&gt;.bnk (exe: IDR_0, IDR_A … IDR_F; hier nach der Hausstufe
        /// L0..L5 -> 0, A, B, C, D, F) mit home.vmf ab "dressing_in"; beim Verlassen zurück ins Wohnzimmer.
        /// </summary>
        void OpenDressing()
        {
            string film = FrontendBank("home.bnk");
            if (film == null) { ShowInfo("Garderobe: frontend/home.bnk fehlt"); return; }
            if (!Avatars.AvatarLibrary.Ready) { ShowInfo("Garderobe: Avatar-Daten werden noch geladen …"); return; }
            string code = (_spot?.Code ?? "").ToLowerInvariant();
            char lvl = code.Length > 5 && code.StartsWith("eph_") ? code[5] : '0';
            string letter = lvl == '1' ? "a" : lvl == '2' ? "b" : lvl == '3' ? "c" : lvl == '4' ? "d" : lvl == '5' ? "f" : "0";
            string bank = Bank("idr_" + letter) ?? Bank("idr_0") ?? BankPrefix("idr_");
            if (bank == null) { ShowInfo("Garderobe: interior/idr_*.bnk fehlt"); return; }
            _aimLabel = null; _aimTarget = null;
            _dressing = true;
            _afterBuild = () =>
            {
                if (!ClothesShop.Open(TDU2ClothesShop.ShopMode.Dressing, film, Npcs)) { _dressing = false; ShowInfo("Garderobe: Oberfläche konnte nicht geladen werden"); BackFromDressing(); }
            };
            LoadScene(bank, Kind.Other, null);
        }

        TDU2Hairdresser _hair;

        /// <summary>Friseur (Typ 29): shopsavatar.vmf ab "hairdresser", Avatar im Stuhl (M_HEAD), Kamera C_HEAD.</summary>
        void OpenHairdresser()
        {
            string bank = FrontendBank("shopsavatar.bnk");
            if (bank == null) { ShowInfo("Friseur: frontend/shopsavatar.bnk fehlt"); return; }
            if (!Avatars.AvatarLibrary.Ready) { ShowInfo("Friseur: Avatar-Daten werden noch geladen …"); return; }
            if (_hair == null)
            {
                _hair = GetComponentInChildren<TDU2Hairdresser>();
                if (_hair == null) { var go = new GameObject("TDU2 Hairdresser"); go.transform.SetParent(transform, false); _hair = go.AddComponent<TDU2Hairdresser>(); }
            }
            _hair.Interiors = this;
            _aimLabel = null; _aimTarget = null;
            if (!_hair.Open(bank, Npcs)) ShowInfo("Friseur: Oberfläche konnte nicht geladen werden");
        }

        void OnClothesClosed()
        {
            if (!_dressing) return;
            _dressing = false;
            BackFromDressing();
        }

        void BackFromDressing()
        {
            if (!Inside || _spotBanks == null || _spotBanks.Living == null) return;
            LoadScene(_spotBanks.Living, Kind.Living, null);
        }

        string TitleFor(Kind kind)
        {
            if (_spotBanks != null && _spotBanks.IsCasino) return "Casino";
            if (kind == Kind.Garage) return "Garage";
            if (kind == Kind.Living) return "Wohnbereich";
            if (_spotBanks != null && _spotBanks.Name == "Gebrauchtwagenhändler") return _spotBanks.Name;
            var shop = Manager.Database?.ShopFor(_spot?.Code);
            string brand = null;
            if (shop != null)
                foreach (int id in shop.CarIds)
                    if (id != 0 && Manager.Database.ById.TryGetValue(id, out var c) && !string.IsNullOrEmpty(c.Brand)) { brand = c.Brand; break; }
            return brand != null ? "Autohändler " + brand : "Autohändler";
        }

        void DestroyUi()
        {
            if (_uiGo != null) UnityUtil.SafeDestroy(_uiGo);
            _uiGo = null; _ui = null; _uiBank = null; _uiAim = null;
        }

        static string IconLabel(InteriorScene.Selectable s)
        {
            string n = s.Name ?? "";
            if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return "exit";
            switch (s.Type)
            {
                case 5: return "garage";
                case 6: return "living";
                case 9: return "diary";
                case 10: return "dressing";
                case 12: case 14: return "billboard";
                case 54: return "club";
                case 57: return "game_plug";
            }
            return "default";
        }

        /// <summary>
        /// Fahrzeugmenü (Hilfeleiste "options_menu", Steuerkreuz): Num4 = Tür links, Num6 = Tür rechts, Num8 = Fenster hoch,
        /// Num2 = Fenster runter (beide Seiten). true = etwas geschaltet.
        /// </summary>
        static string AimName(object t) => t == null ? "-" : t is DisplayCar dc ? "Auto " + (dc.Car != null ? dc.Car.FileName : "?") : t is InteriorScene.Selectable s ? "Hotspot " + InteriorScene.Label(s) : t.ToString();

        bool IsCarCollider(Collider col)
        {
            if (col == null) return false;
            foreach (var c in _cars)
                if (c != null && c.Go != null && (col == c.Col || col.transform.IsChildOf(c.Go.transform))) return true;
            return false;
        }

        static bool CarPad(TDU2Accessories acc)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (acc == null) return false;
            if (Input.GetKeyDown(KeyCode.Keypad4)) { acc.ToggleDoor(true); return true; }
            if (Input.GetKeyDown(KeyCode.Keypad6)) { acc.ToggleDoor(false); return true; }
            if (Input.GetKeyDown(KeyCode.Keypad8)) { acc.SetWindow(true, false); acc.SetWindow(false, false); return true; }
            if (Input.GetKeyDown(KeyCode.Keypad2)) { acc.SetWindow(true, true); acc.SetWindow(false, true); return true; }
#endif
            return false;
        }

        void UpdateUiAim(object target)
        {
            if (_ui == null || ReferenceEquals(target, _uiAim)) return;
            Debug.Log("[TDU2] Anvisiert: " + AimName(target) + (target is DisplayCar ? " in " + _aimCarDist.ToString("0.0") + " m" : "") + " (vorher " + AimName(_uiAim) + ")");
            // altes Ziel schließen
            if (_uiAim is DisplayCar && _kind == Kind.StickerShop)
                _ui.GotoLabel("/cursor/low_bar", "close", true);
            else if (_uiAim is DisplayCar)
            {
                _ui.GotoLabel("/car_datas", "close", true);
                if (_kind == Kind.Dealer) _ui.GotoLabel("/price", "close", true);
                _ui.GotoLabel("/title_bar", "open", true);   // Titel wieder einblenden (Preis lag an seiner Stelle)
            }
            else if (_uiAim is InteriorScene.Selectable)
                _ui.GotoLabel("/cursor/low_bar", "close", true);
            _uiAim = target;
            if (!(target is DisplayCar)) _aimInfoAt = -1f;
            if (target is DisplayCar sc && _kind == Kind.StickerShop)
            {
                _ui.SetVariable("/:t_wm_destination", "Lackieren & bekleben");
                _ui.GotoLabel("/cursor", "on", true);
                _ui.GotoLabel("/cursor/low_bar", "open", true);
                _ui.GotoLabel("/cursor/low_bar/ic", "default", false);
            }
            else if (target is DisplayCar dc && (_kind == Kind.Dealer || _kind == Kind.Garage))
            {
                // wie im Spiel: Cursor "on" -> ItemAction("over") (Hilfeleiste "options_menu" = Fahrzeugmenü mit Steuerkreuz),
                // läuft selbst weiter nach "info" -> ItemType("info") öffnet Fahrzeugdaten (und beim Händler den Preis).
                // ItemType("info") schaltet um (ItemInfo) – darum hier nichts selbst öffnen, nur Daten und Zustand setzen.
                FillCarData(dc.Car);
                _ui.SetVariable("/:SelItemType", "vehicle");
                if (_kind == Kind.Dealer) _ui.SetVariable("/:WhatPage", "car_dealer");   // ItemType("info") verzweigt danach (sonst keine Daten)
                _ui.SetValue("/:ItemInfo", false);
                _aimInfoAt = Time.unscaledTime + 0.45f;
                _ui.SetValue("/:Input", true);
                _ui.GotoLabel("/car_datas", "default", false);
                if (_kind == Kind.Dealer) { _ui.GotoLabel("/price", "default", false); _ui.GotoLabel("/title_bar", "close", true); }
                _ui.GotoLabel("/cursor", "on", true);
            }
            else if (target is DisplayCar dc2)
            {
                FillCarData(dc2.Car);
                _ui.GotoLabel("/car_datas", "open", true);
                _ui.GotoLabel("/cursor", "info", true);
            }
            else if (target is InteriorScene.Selectable cs && _spotBanks != null && _spotBanks.IsCasino && !string.IsNullOrEmpty(cs.Str))
            {
                // Casino-Film: SelItemType setzen, "on" ruft ItemAction("over") -> ItemType -> t_wm_destination/Symbol wie im Spiel
                _ui.SetVariable("/:SelItemType", cs.Str);
                _ui.SetVariable("/:t_wm_destination", CasinoLabel(cs.Str));
                _ui.GotoLabel("/cursor", "on", true);
                _ui.GotoLabel("/cursor/low_bar", "open", true);
            }
            else if (target is InteriorScene.Selectable s)
            {
                _ui.SetVariable("/:t_wm_destination", s.Type == 28 ? "Sticker-Shop" : InteriorScene.Label(s));
                _ui.GotoLabel("/cursor", "on", true);
                _ui.GotoLabel("/cursor/low_bar", "open", true);
                _ui.GotoLabel("/cursor/low_bar/ic", IconLabel(s), false);
            }
            else _ui.GotoLabel("/cursor", "off", true);
        }

        /// <summary>
        /// Fahrzeugdaten wie GMShop.cpp. Der Film füllt car_datas selbst: datas_base ruft in Bild 0 (bei jedem "open", weil die
        /// Zeitleiste den Clip neu anlegt) SetDatas() auf und liest dabei nur _root-Variablen:
        ///   cur_speed/cur_accel/cur_braking (Balken, Bild 1..101), cur_difficulty (Bild 1..5), cur_group (Label A1…C4, mA1…),
        ///   cur_upgrade ("level0".."level5"), state_metric (1 = km/h) mit cur_max_speed_m/_i, cur_0_100_m/_i, cur_power_m/_i,
        ///   Km_m/_i, Centaine_m/_i, isCarPaintable/isCarStickable/isCarTunable, IsBike, m_Speed/m_Acceleration/m_Braking,
        ///   Current_car (Name, initVehicleName). Darum hier nur diese Variablen setzen (festgehalten) und SetDatas() aufrufen –
        ///   direkt gesetzte Bilder/Texte der Kinder würden beim nächsten "open" wieder überschrieben.
        /// </summary>
        /// <param name="asCompare">Vergleichsauto: dieselben Werte als comp_* / Comp_car (SetDatasCp zeigt sie neben dem
        /// angesehenen Auto, solange _root.IsCompare gilt).</param>
        void FillCarData(CarPhysics c, bool asCompare = false)
        {
            if (c == null || _ui == null) return;
            try
            {
                var r = c.Raw;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                _ui.ReleaseForced("/car_datas/datas_base/limits");   // forb_* setzt SetDatas selbst (sichtbar = gesperrt)

                string version = r?.S("_version_name", "") ?? "";
                if (version == "##") version = "";
                string name = (string.IsNullOrEmpty(c.Brand) ? "" : c.Brand + " ") + c.ModelName + (version.Length > 0 ? " " + version : "");
                int cat = r != null ? r.I("_vehiclecategory") : 0;
                float bhp = r != null ? r.F("_power_bhp") : 0f;
                float acc = c.Accel0To100, vmax = c.MaxTheoricalSpeed;
                float brake = r != null ? r.F("_brakingdist") : 0f;
                int diff = r != null ? r.I("_difficulty") : 0;
                int price = r != null ? r.I("_price") : 0;

                // Kilometerzähler wie MngHud ("%08d" + Zehntel); Händlerwagen sind neu, eigene Autos aus dem gespeicherten Zähler
                double odo = 0;
                if (_kind != Kind.Dealer)
                    double.TryParse(PlayerPrefs.GetString("TDU2.Odo." + c.Id, "0"), System.Globalization.NumberStyles.Float, ci, out odo);
                double km = odo / 1000.0, mi = odo / 1609.344;
                long kmW = (long)Math.Floor(km), miW = (long)Math.Floor(mi);

                int aAcc = acc > 0 ? Mathf.RoundToInt(Mathf.InverseLerp(12f, 2.5f, acc) * 100f) : 0;
                int aSpd = Mathf.RoundToInt(Mathf.InverseLerp(140f, 420f, vmax) * 100f);
                int aBrk = brake > 0 ? Mathf.RoundToInt(Mathf.InverseLerp(75f, 30f, brake) * 100f) : 50;
                bool isPaintable = c.Raw != null && c.Raw.I("_paintable") != 0;
                bool isStickerable = c.Raw != null && c.Raw.I("_stickerable") != 0;
                bool isTuningable = c.Raw != null && c.Raw.I("_tuningable") != 0;

                if (asCompare)
                {
                    using (_ui.PinScope())
                    {
                        _ui.ForceVisible("/car_datas/datas_base/limits/forb_tun", isTuningable);
                        _ui.ForceVisible("/car_datas/datas_base/limits/forb_stick", isStickerable);
                        _ui.ForceVisible("/car_datas/datas_base/limits/forb_paint", isPaintable);
                        _ui.SetVariable("/:Comp_car", name);
                        _ui.SetVariable("/:comp_group", GroupLabel(cat));
                        _ui.SetVariable("/:comp_upgrade", "level0");
                        _ui.SetValue("/:comp_speed", Mathf.Clamp(aSpd, 1, 101));
                        _ui.SetValue("/:comp_accel", Mathf.Clamp(aAcc, 1, 101));
                        _ui.SetValue("/:comp_braking", Mathf.Clamp(aBrk, 1, 101));
                        _ui.SetValue("/:comp_difficulty", Mathf.Clamp(diff > 0 ? diff : 1, 1, 5));
                        _ui.SetVariable("/:comp_power_m", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
                        _ui.SetVariable("/:comp_power_i", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
                        _ui.SetVariable("/:comp_max_speed_m", vmax > 0 ? vmax.ToString("0", ci) + " Km/h" : "-");
                        _ui.SetVariable("/:comp_max_speed_i", vmax > 0 ? (vmax / 1.609344f).ToString("0", ci) + " mph" : "-");
                        _ui.SetVariable("/:comp_0_100_m", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                        _ui.SetVariable("/:comp_0_100_i", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                    }
                    return;
                }
                using (_ui.PinScope())   // festhalten: der Film setzt einige davon beim Auf-/Zuklappen zurück
                {
                    _ui.ForceVisible("/car_datas/datas_base/limits/forb_tun", isTuningable);
                    _ui.ForceVisible("/car_datas/datas_base/limits/forb_stick", isStickerable);
                    _ui.ForceVisible("/car_datas/datas_base/limits/forb_paint", isPaintable);
                    _ui.SetValue("/:fake", false);
                    _ui.SetValue("/:WhatPlatform", 1);
                    _ui.SetVariable("/:MenuType", "pc");
                    _ui.SetValue("/:IsPC_X360", true);
                    _ui.SetValue("/:state_metric", c.Kmh ? 1 : 0);
                    _ui.SetVariable("/:Current_car", name);
                    _ui.SetVariable("/:cur_group", GroupLabel(cat));
                    _ui.SetVariable("/:cur_upgrade", "level0");
                    _ui.SetValue("/:cur_speed", Mathf.Clamp(aSpd, 1, 101));
                    _ui.SetValue("/:cur_accel", Mathf.Clamp(aAcc, 1, 101));
                    _ui.SetValue("/:cur_braking", Mathf.Clamp(aBrk, 1, 101));
                    _ui.SetValue("/:cur_difficulty", Mathf.Clamp(diff > 0 ? diff : 1, 1, 5));
                    _ui.SetVariable("/:cur_power_m", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
                    _ui.SetVariable("/:cur_power_i", bhp > 0 ? bhp.ToString("0", ci) + " bhp" : "-");
                    _ui.SetVariable("/:cur_max_speed_m", vmax > 0 ? vmax.ToString("0", ci) + " Km/h" : "-");
                    _ui.SetVariable("/:cur_max_speed_i", vmax > 0 ? (vmax / 1.609344f).ToString("0", ci) + " mph" : "-");
                    _ui.SetVariable("/:cur_0_100_m", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                    _ui.SetVariable("/:cur_0_100_i", acc > 0 ? acc.ToString("0.0", ci) + " sec" : "-");
                    _ui.SetVariable("/:Km_m", (kmW % 100000000).ToString("D8"));
                    _ui.SetVariable("/:Centaine_m", ((int)Math.Floor((km - kmW) * 10.0)).ToString());
                    _ui.SetVariable("/:Km_i", (miW % 100000000).ToString("D8"));
                    _ui.SetVariable("/:Centaine_i", ((int)Math.Floor((mi - miW) * 10.0)).ToString());
                    _ui.SetValue("/:isCarPaintable", (r?.I("_paintable") ?? 1) != 0);
                    _ui.SetValue("/:isCarStickable", (r?.I("_stickerable") ?? 1) != 0);
                    _ui.SetValue("/:isCarTunable", (r?.I("_tuningable") ?? 1) != 0);
                    _ui.SetValue("/:IsBike", cat >= 18 && cat <= 24);
                    if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Speed"))) _ui.SetVariable("/:m_Speed", TDU2UIManager.GetLangString(3511, "tdu2interiour.3511"));
                    if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Acceleration"))) _ui.SetVariable("/:m_Acceleration", TDU2UIManager.GetLangString(2814, "tdu2interiour.2814"));
                    if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Braking"))) _ui.SetVariable("/:m_Braking", TDU2UIManager.GetLangString(2813, "tdu2interiour.2813"));
                    if (string.IsNullOrEmpty(_ui.GetVariable("/:m_Difficulty"))) _ui.SetVariable("/:m_Difficulty", TDU2UIManager.GetLangString(6664, "tdu2interiour.6664"));
                    _ui.SetVariable("/:current_price", price > 0 ? price.ToString("N0", de) + " $" : "");
                }
                // jetzt füllen (und bei jedem erneuten "open" füllt der Film aus denselben Variablen)
                // (gibt es datas_base noch nicht – car_datas steht noch vor "open" –, ruft der Film SetDatas beim Anlegen selbst auf)
                // Vergleich aktiv (Mini-Menü "Vergleichen"): SetDatasCp zeigt das Vergleichsauto daneben und ruft SetDatas selbst auf
                _ui.SetValue("/:IsCompare", _compareCar != null);
                if (_compareCar == null || !_ui.CallClip("/car_datas/datas_base", "SetDatasCp")) _ui.CallClip("/car_datas/datas_base", "SetDatas");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TDU2] FillCarData: " + ex.Message);
            }
        }

        /// <summary>
        /// DBCarPhysicsData._vehiclecategory -> Label von /car_datas/ca_name/group:
        /// 1–7 = A1–A7 (Asphalt), 9–13 = B1–B5 (Gelände), 14–17 = C1–C4 (Klassiker), 18–19 = mA1–mA2, 20–22 = mB1–mB3, 23–24 = mC1–mC2 (Motorräder).
        /// </summary>
        static string GroupLabel(int cat)
        {
            if (cat >= 1 && cat <= 7) return "A" + cat;
            if (cat >= 9 && cat <= 13) return "B" + (cat - 8);
            if (cat >= 14 && cat <= 17) return "C" + (cat - 13);
            if (cat >= 18 && cat <= 19) return "mA" + (cat - 17);
            if (cat >= 20 && cat <= 22) return "mB" + (cat - 19);
            if (cat >= 23 && cat <= 24) return "mC" + (cat - 22);
            return "empty";
        }

        void SetGauge(string clip, string label, int value)
        {
            string p = "/car_datas/datas_base/" + clip;
            value = Mathf.Clamp(value, 0, 100);
            _ui.SetVariable(p + ":txt", label);
            _ui.SetVariable(p + ":val", value.ToString());
            _ui.SetVariable(p + ":value", value.ToString());
            _ui.GotoFrameNumber(p + "/gauge", value);
        }

        bool IsUsable(InteriorScene.Selectable s)
        {
            string n = s.Name ?? "";
            if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Type == 5) return _spotBanks?.Garage != null && _kind != Kind.Garage;
            if (s.Type == 6) return _spotBanks?.Living != null && _kind != Kind.Living;
            if (s.Type == 9) return GuestHost == 0;   // Tagebuch / My House nur für den Besitzer, nicht für eingeladene Gäste
            return true;
        }

        void Activate(object target)
        {
            if (target is DisplayCar && _kind == Kind.StickerShop) 
            { 
                OpenStickerShop(); 
                return; 
            }
            if (target is DisplayCar lobby && lobby.Lobby) return;   // Ausstellungsauto (Casino) – need recreate the UI
            if (target is DisplayCar dc)
            {
                // Autohändler: Original-Menü (Kaufen mit Felgen/Lack/Innenraum, Testfahrt) – Startauto bleibt Direktwahl
                if (_kind == Kind.Dealer && _starterCars == null && OpenDealerMenu(dc)) 
                    return;
                // Garage: Original-Menü des Films (Losfahren, Einsteigen, Daten, Vergleichen – Gäste ohne Losfahren)
                if (_kind == Kind.Garage && _starterCars == null && OpenDealerMenu(dc)) 
                    return;
                if (_starterCars != null)
                {
                    var cb = _starterChosen;
                    _starterCars = null; _starterChosen = null;
                    cb?.Invoke(dc.Car);
                }
                if (_kind == Kind.Dealer) 
                    RememberOwned(dc.Car);
                if (GuestGarageCars != null && _kind == Kind.Garage) 
                { 
                    ShowInfo("Das Auto gehört deinem Gastgeber."); 
                    return; 
                }
                ExitInterior(dc.Car);
                return;
            }
            if (!(target is InteriorScene.Selectable s)) return;
            string n = s.Name ?? "";
            // Casino: Hotspots sind Typ 57 mit SelectableString = SelItemType des Casino-Films (outdoor, crou, cslo, cpok, chal …)
            if (_spotBanks != null && _spotBanks.IsCasino && !string.IsNullOrEmpty(s.Str)) { CasinoSelect(s); return; }
            if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase))
            {
                if (_starterCars != null) 
                { 
                    ShowInfo("Wähle zuerst eines der Autos."); 
                    return; 
                }   // Start: erst ein Auto aussuchen
                if (_spotBanks != null && _spotBanks.IsCasino && OpenCasinoExit()) return;   // Casino: Ziel wählen (Ibiza/Hawaii)
                ExitInterior(_lastCar);
                return;
            }
            Debug.Log("GUI Id: " + s.Type + " kind: " + _kind);
            switch (s.Type)
            {
                case 5: LoadScene(_spotBanks.Garage, Kind.Garage, null); break;
                case 6: LoadScene(_spotBanks.Living, Kind.Living, "cam_entree_garage"); break;
                case 54 when _spotBanks != null && _spotBanks.Living != null:   // Club: in den Clubraum (ich_*)
                    LoadScene(_spotBanks.Living, Kind.Living, null); break;
                case 1 when _ui != null:
                case 12 when _ui != null:
                case 9 when _ui != null && GuestHost == 0:   // Tagebuch (home.vmf diary_in) – nur der Besitzer
                    OpenDiary(); break;
                case 9 when _ui != null: break;            // Gast: kein Tagebuch
                case 57 when _ui != null:
                    break;   // im Original eigene Flash-Menüs (noch nicht angebunden)
                case 1:
                    {
                        var list = new List<CarPhysics>();
                        foreach (var c in _cars) list.Add(c.Car);
                        var shop = Manager.Database.ShopFor(_spot.Code);
                        Debug.Log("Shop: " + _spot.Code + " -> " + (shop != null ? shop.SpotName : "(kein Shop)"));
                        if (shop != null)
                            foreach (int id in shop.CarIds)
                                if (id != 0 && Manager.Database.ById.TryGetValue(id, out var c)) { var av = Manager.FindCar(c.FileName); if (av != null && !list.Contains(av)) list.Add(av); }
                        OpenMenu("Autohändler – Bestand", list, true);
                        break;
                    }
                case 12:
                case 14:
                    {
                        var list = new List<CarPhysics>();
                        if (_lastCar != null) 
                            list.Add(_lastCar);

                        _ui.ForceVisible("/garage", true);
                        _ui.ForceVisible("/garage/money_cost", false);
                        _ui.ForceVisible("/garage/vehicule_list", true);

                        foreach (var c in OwnedCars()) 
                            if (!list.Contains(c)) list.Add(c);

                        for (int i = 1; i < list.Count; i++)
                        {
                            _ui.ForceVisible($"/garage/vehicule_list/line{i}", true);
                            _ui.SetLabel("/garage/vehicule_list/line1/content/ic", "A1");

                            string name = (string.IsNullOrEmpty(list[i].Brand) ? "" : list[i].Brand + " ") + list[i].ModelName;
                            _ui.SetText("/garage/vehicule_list/line1:name", name);
                            int cat = list[i] != null ? list[i].Raw.I ("_vehiclecategory") : 0;
                            _ui.SetLabel("/garage/vehicule_list/line1/content/ic", GroupLabel(cat));
                            _ui.SetText("/garage/vehicule_list/line1:power", list[i].PowerBhp + " Bhp");
                        }

                        OpenMenu("Meine Autos", list, false);
                        break;
                    }
                case 10: OpenDressing(); break;      // Garderobe -> Interieur idr_* + home.vmf dressing_in
                case 9: ShowInfo($"GUI {s.Type} - {_kind} not Implemented"); break;
                case 57: ShowInfo($"GUI {s.Type} - {_kind} not Implemented"); break;
                case 22: if (!OpenClothesShop()) goto default; break;
                case 29: OpenHairdresser(); break;  // Friseur (Hairdresser_area) -> shopsavatar.vmf "hairdresser"
                case 28: OpenStickerShop(); break;    // Sticker-Shop-Verkäuferin -> shopscar.vmf StickersMenu()
                case 20: OpenRacingSchool(); break;   // Rennschul-NPC -> r_school.vmf + Prüfungsauswahl
                case 32: OpenAirport(); break;        // Flughafen-Hostess -> airport.vmf + Flug auf die andere Insel
                default:
                    if (_spotBanks != null && _spotBanks.IsCasino && TryCasinoRoom(s)) break;
                    ShowInfo($"GUI {s.Type} - {_kind} not Implemented"); break;
            }
        }

        /// <summary>
        /// Casino: Durchgänge der Halle in die Spielräume. Zuordnung über den Namen des Auswahlpunkts
        /// (POK = Poker, ROU = Roulette, SLO/SLOT = Automaten, VIP, DR/DRESS = Garderobe, HAL = Halle).
        /// </summary>
        bool TryCasinoRoom(InteriorScene.Selectable s)
        {
            string n = (s.Name ?? "").ToUpperInvariant();
            Debug.Log("[TDU2] Casino-Auswahlpunkt: '" + s.Name + "' Typ " + s.Type);
            string bank = n.Contains("POK") ? "icaspok1" : n.Contains("ROU") ? "icasrou1" : n.Contains("SLO") ? "icasslo1"
                        : n.Contains("VIP") ? "icasvip" : n.Contains("DRESS") || n.Contains("_DR") ? "icasdr" : n.Contains("HAL") ? "icashal" : null;
            string path = bank != null ? Bank(bank) : null;
            if (path == null) return false;
            LoadScene(path, Kind.Other, null);
            return true;
        }

        // ------------------------------------------------------------------ Casino (frontend/<res>/casino.bnk, Casino.vmf)
        // Halle = Label "casino_hall". Ausgangstür = SelItemType "outdoor": SetMenu() füllt /cursor/mini_menu mit
        // "ibiza"/"hawai" (m_gotoibiza/m_gotohawai, _root.hawaiIsLocked), Cursor-Label "mini_menu"; mini_menu.A() setzt
        // _root.BtValid, Bild 45 ruft ValidMenu() -> SendMsg_WhereToGo -> FSCommand "gtib"/"gthw". B() -> "wmon" (zurück).
        bool _casinoMenu;
        float _casinoFlyAt = -1f, _casinoOpenedAt;
        /// <summary>Ziel-Menü der Casino-Ausgangstür ist offen (Pausemenü gesperrt).</summary>
        public static bool CasinoMenuOpen { get; private set; }

        void PrepareCasinoUi()
        {
            try { FlashLocalization.Apply(_ui, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); } catch (Exception) { }
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.SetVariable("/:MenuType", "pc");
            _ui.SetValue("/:IsPC_X360", true);
            _ui.SetValue("/:fake", false);
            _ui.SetValue("/:IsTutorial", 0);
            _ui.SetValue("/:Input", true);
            _ui.SetValue("/:hawaiIsLocked", false);
            _ui.SetValue("/:ExitValid", true);
        }

        string _casinoReturnCam;

        static string CasinoLabel(string sel)
        {
            switch ((sel ?? "").ToLowerInvariant())
            {
                case "outdoor": return "Ausgang";
                case "chal": return "Haupthalle";
                case "crou": return "Roulette";
                case "cslo": return "Spielautomaten";
                case "cpok": return "Poker";
                case "sitgo": return "Poker – Sit & Go";
                case "cvip": return "VIP-Terrasse";
                case "shop": return "Casino-Shop";
                case "avat": return "Garderobe";
                case "iclo": return "Kleidung";
                case "ifur": return "Möbel";
                case "isti": return "Sticker";
            }
            return sel;
        }

        /// <summary>
        /// Casino-Hotspot (icashal: GoToRoulette1 "crou", GoToSlotMachine1 "cslo", GoToPoker1 "cpok", GoToPokerArcade1 "sitgo",
        /// GoToVIP "cvip", GoToShop "shop", GoToDressing "avat", GoToExit "outdoor"; in den Räumen GoToHall/Exit "chal").
        /// Räume: icasrou1, icasslo1, icaspok1, icasvip, ishop_ca; zurück in die Halle an cam_from_&lt;Raum&gt;.
        /// </summary>
        void CasinoSelect(InteriorScene.Selectable s)
        {
            string sel = s.Str.ToLowerInvariant();
            Debug.Log("[TDU2] Casino: " + s.Name + " (" + sel + ")");
            switch (sel)
            {
                case "outdoor": OpenCasinoFlight(); return;
                case "chal": GoCasinoRoom("icashal", _casinoReturnCam ?? "cam_from_Ext", null); return;
                case "crou": GoCasinoRoom("icasrou1", null, "cam_from_rou1"); return;
                case "cslo": GoCasinoRoom("icasslo1", null, "cam_from_slo1"); return;
                case "cpok": GoCasinoRoom("icaspok1", null, "cam_from_poker1"); return;
                case "sitgo": GoCasinoRoom("icaspok1", null, "cam_from_pokerArcade1"); return;
                case "cvip": GoCasinoRoom("icasvip", null, "cam_from_VIP"); return;
                case "shop": GoCasinoRoom("ishop_ca", null, "cam_from_shop"); return;
            }
            ShowInfo(CasinoLabel(sel) + ": im Spiel ein eigenes Flash-Menü – noch nicht nachgebaut.");
        }

        void GoCasinoRoom(string bank, string startCam, string returnCam)
        {
            string path = Bank(bank);
            if (path == null) { ShowInfo("Casino: interior/" + bank + ".bnk fehlt."); return; }
            if (returnCam != null) _casinoReturnCam = returnCam;
            UpdateUiAim(null);
            LoadScene(path, Kind.Other, startCam);
        }

        bool OpenCasinoExit()
        {
            if (_ui == null || _ui.Find("/cursor/mini_menu") == null) return false;
            _casinoMenu = true; CasinoMenuOpen = true;
            _casinoFlyAt = -1f; _casinoOpenedAt = Time.unscaledTime;
            UpdateUiAim(null);
            if (_fp != null) _fp.LookEnabled = false;
            _ui.SetValue("/:Input", true);
            _ui.SetValue("/:hawaiIsLocked", !TDU2IslandTravel.CanTravelTo("hawai", Manager.BnkDir));
            _ui.SetVariable("/:SelItemType", "outdoor");
            _ui.SetVariable("/:BtValid", "");
            _ui.SetVisible("/cursor", true);
            _ui.CallClip("/", "SetMenu");                    // Knöpfe ibiza/hawai, mini_menu "open"
            _ui.GotoLabel("/cursor", "mini_menu", true);     // _root.Clip = cursor (Eingabe), Lage des Menüs
            Debug.Log("[TDU2] Casino: Ausgang – Ziel wählen (Ibiza/Hawaii)");
            return true;
        }

        void CloseCasinoMenu()
        {
            if (!_casinoMenu) return;
            _casinoMenu = false; CasinoMenuOpen = false; _casinoFlyAt = -1f;
            Racing.RacingSchoolMenu.NoteUiClosed();
            if (_ui != null)
            {
                _ui.GotoLabel("/cursor/mini_menu", "close", true);
                _ui.GotoLabel("/cursor", "valid", true);
                _ui.SetValue("/:Input", true);
            }
            _uiAim = null;
        }

        void UpdateCasinoMenu()
        {
            if (_fp != null) _fp.LookEnabled = false;
            if (_casinoFlyAt > 0f && Time.unscaledTime >= _casinoFlyAt)
            {
                // Rückfall: der Film hat nach der Auswahl kein gtib/gthw geschickt -> Auswahl selbst auswerten
                _casinoFlyAt = -1f;
                string bt = _ui != null ? (_ui.GetVariable("/:BtValid") ?? "") : "";
                if (bt.StartsWith("hawai", StringComparison.OrdinalIgnoreCase)) CasinoTravel("hawai");
                else if (bt.StartsWith("ibiza", StringComparison.OrdinalIgnoreCase)) CasinoTravel("ibiza");
                else CloseCasinoMenu();
                return;
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_ui == null) { CloseCasinoMenu(); return; }
            if (Time.unscaledTime - _casinoOpenedAt < 0.15f) return;   // Taste, mit der geöffnet wurde
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _ui.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _ui.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E))
            {
                if (_ui.Input("A") && _casinoFlyAt < 0f) _casinoFlyAt = Time.unscaledTime + 1.5f;
            }
            // ESC/Rücktaste: zentraler Menüablauf (TDU2MenuFlow.cs)
#endif
        }

        void OnUiCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (TDU2FrontendSounds.Command(c)) return;
            Debug.Log("[TDU2] Interieur-Film -> " + c + (_dealerMenu ? " (Menü " + _dPhase + ", Mini " + (_ui?.Label("/cursor/mini_menu") ?? "-") + ", BtValid " + (_ui?.GetVariable("/:BtValid") ?? "") + ")" : ""));
            if (_dealerMenu)
            {
                bool handled;
                try { handled = OnDealerCommand(c); }
                catch (Exception ex) { Debug.LogException(ex); handled = true; }
                if (handled) return;   // Autohändler/Garage (TDU2CarDealer.cs)
            }
            if ((c.StartsWith("myhs:") || _myHouse != HouseMode.None) && OnMyHouseCommand(c)) return;
            if (_myHouse != HouseMode.None && c == "dbxo") { AnswerUiDialog(); return; }
            if ((c.StartsWith("mylf:") || c == "tfoc" || c == "vtke") && _ui != null && OnMyLifeCommand(c)) return;   // MyTDULife (TDU2MyLife.cs)
            if (_diaryOpen) { OnDiaryCommand(c); return; }
            if (!_casinoMenu) return;
            switch (c)
            {
                case "gthw": CasinoTravel("hawai"); break;   // SendMsg_WhereToGo("gotohawai")
                case "gtib": CasinoTravel("ibiza"); break;   // SendMsg_WhereToGo("gotoibiza")
                case "wmon": CloseCasinoMenu(); break;       // B(): zurück in den Laufmodus
            }
        }

        // ------------------------------------------------------------------ Tagebuch / My House (home.vmf)
        // Wohnbereich, Auswahlpunkt Typ 9: Film-Label "diary_in" (SetBDD) -> "diary_stop" öffnet /diary. Zurück: diary_out schickt
        // camf + wmon und springt nach living_in. "myhs:open" (Eintrag My House) -> Label myhs_open (Laufmodus mit Editor-Cursor).
        bool _diaryOpen, _diaryChecked; float _diaryOpenedAt;
        static readonly string[] DiaryClips = { "diary", "mylf", "mylf_book", "myhs_editor", "help", "help_system", "title_bar", "cmon_loader", "cmon_loading", "club_money" };

        void OpenDiary()
        {
            if (_ui == null || _ui.Find("/diary") == null) { ShowInfo("Tagebuch: home.vmf ohne /diary."); return; }
            _diaryOpen = true; _diaryChecked = false; CasinoMenuOpen = true;   // sperrt das Pausemenü wie die anderen Film-Menüs
            _diaryOpenedAt = Time.unscaledTime;
            UpdateUiAim(null);
            if (_fp != null) _fp.LookEnabled = false;
            LockCursor(false);
            try { FlashLocalization.Apply(_ui, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); } catch (Exception) { }
            _ui.MouseEnabled = true;
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.SetVariable("/:MenuType", "pc");
            _ui.SetValue("/:IsPC_X360", true);
            _ui.SetValue("/:fake", false);
            _ui.SetValue("/:Input", true);
            _ui.SetValue("/:IsMyHouse", true);
            // _root.ValidMenu() verzweigt nach SelItemType: nur bei "diary" führen die Einträge (my_house, my_life …) weiter.
            // Im Spiel setzt das Anvisieren des Tagebuchs den Wert – hier nie gesetzt, darum hing das Menü nach der Auswahl.
            _ui.SetVariable("/:SelItemType", "diary");
            _ui.SetVariable("/:WhatDiaryMenu", "diary");
            _ui.SetValue("/:LockMyHouse", false);
            _ui.SetValue("/:LockMyPhotos", true);      // Fotoalbum noch nicht umgesetzt -> gesperrt (Film spielt "wrong")
            _ui.SetVariable("/:BtValid", "");
            _diaryStuckAt = -1f;
            _ui.SetVariable("/:MyMoneyStr", Online.TDU2Session.Save != null ? Online.TDU2Session.Save.money.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $" : " ");
            foreach (var n in DiaryClips) _ui.SetVisible("/" + n, true);
            _ui.GotoLabel("/", "diary_in", true);
            Racing.RaceTexts.Ensure(Manager.BnkDir, TDU2PauseMenu.SavedLanguage());
            EnsureHouseCatalog();
            Debug.Log("[TDU2] Tagebuch geöffnet");
        }

        void CloseDiary()
        {
            if (!_diaryOpen) return;
            _diaryOpen = false; CasinoMenuOpen = false;
            EndLifeModes();
            SaveLifeIfDirty();
            Racing.RacingSchoolMenu.NoteUiClosed();
            if (_ui != null)
            {
                _ui.GotoLabel("/", _kind == Kind.Garage ? "garage_stop" : "living_stop", false);
                foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
                _ui.GotoLabel("/cursor", "valid", true);
                _ui.SetValue("/:Input", true);
            }
            _uiAim = null;
            LockCursor(true);
            Debug.Log("[TDU2] Tagebuch geschlossen");
        }

        void UpdateDiary()
        {
            if (_fp != null) _fp.LookEnabled = false;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_ui == null) { CloseDiary(); return; }
            // Rückfall: steht das Tagebuch nach 1,5 s noch nicht im Menü (Bild "stop"), selbst öffnen
            if (!_diaryChecked && Time.unscaledTime - _diaryOpenedAt > 1.5f)
            {
                _diaryChecked = true;
                var di = _ui.Find("/diary")?.Sprite;
                Debug.Log("[TDU2] Tagebuch: Wurzel " + _ui.Label("/") + ", diary " + _ui.Label("/diary") + " (Bild " + (di != null ? di.Frame + 1 : 0) + "), Input " + _ui.GetVariable("/:Input"));
                if (di == null || di.Frame < 19)
                {
                    _ui.SetVisible("/diary", true);
                    _ui.GotoLabel("/", "diary_stop", true);
                    _ui.GotoLabel("/diary", "open", true);
                    _ui.SetValue("/:Input", true);
                }
            }
            DiaryWatchdog();
            if (Time.unscaledTime - _diaryOpenedAt < 0.2f) return;   // Taste, mit der geöffnet wurde
            if (UpdateMyLife()) return;   // MyTDULife: Foto-Sucher bzw. Texteingabe haben die Tastatur
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _ui.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _ui.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _ui.Input("Move", "left");
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _ui.Input("Move", "right");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) _ui.Input("A");
            if (Input.GetKeyDown(KeyCode.Y)) _ui.Input("Y");
            if (Input.GetKeyDown(KeyCode.X)) _ui.Input("X");
            // ESC/Rücktaste: zentraler Menüablauf (TDU2MenuFlow.cs)
#endif
        }

        float _diaryStuckAt = -1f;

        /// <summary>
        /// Sicherung: nach der Auswahl (A -> Knopf "valid" -> Menü "close" -> Bild 45 ruft _root.ValidMenu()) steht das Menü wieder
        /// auf "default", Input ist aus und das Tagebuch noch auf "stop". Hat ValidMenu nicht weitergeschaltet (z.B. weil der Film
        /// ValidMenu in der Umkleide-Zeitleiste überschreibt), wird die Tagebuch-Verzweigung von ValidMenu hier nachgespielt.
        /// </summary>
        void DiaryWatchdog()
        {
            // Menü "default" = ValidMenu lief ohne Wirkung; Menü "stop" mit Input aus = Knopf-Animation kam nie bei "close" an
            string ml = _ui.Label("/diary/menu");
            bool stuck = _ui.Label("/") == "diary_stop" && _ui.Label("/diary") == "stop" && (ml == "default" || ml == "stop") && _ui.GetVariable("/:Input") != "true"
                         && !string.IsNullOrEmpty(_ui.GetVariable("/:BtValid"));
            if (!stuck) { _diaryStuckAt = -1f; return; }
            if (_diaryStuckAt < 0f) { _diaryStuckAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - _diaryStuckAt < (ml == "default" ? 0.5f : 2f)) return;
            _diaryStuckAt = -1f;
            string bt = _ui.GetVariable("/:BtValid") ?? "";
            Debug.Log("[TDU2] Tagebuch: ValidMenu ohne Wirkung (BtValid '" + bt + "', SelItemType '" + _ui.GetVariable("/:SelItemType") + "') – Verzweigung nachgespielt");
            switch (bt)
            {
                case "my_house":
                    _ui.GotoLabel("/diary", "default", false);
                    _ui.GotoLabel("/title_bar", "close", true);
                    _ui.CallClip("/", "SetHelp", "close", false);
                    _ui.GotoLabel("/", "myhs_open", true);     // Bild 90: myhs:open, wmon, camf -> StartMyHouse()
                    break;
                case "rankings":
                    _ui.CallClip("/", "SetHelp", "close", false);
                    _ui.GotoLabel("/diary", "menus", true);
                    break;
                case "my_life":                                // wie ValidMenu: mylf:self, Tagebuch "my_life", /mylf "load"
                    _ui.GotoLabel("/title_bar", "close", true);
                    OnMyLifeCommand("mylf:self");
                    _ui.GotoLabel("/diary", "my_life", true);
                    _ui.CallClip("/", "SetHelp", "back", true);
                    _ui.GotoLabel("/mylf", "load", true);
                    break;
                default:                                       // nicht umgesetzt: Menü wieder freigeben
                    _ui.GotoLabel("/diary", "stop", true);
                    _ui.SetValue("/:Input", true);
                    break;
            }
        }

        void OnDiaryCommand(string c)
        {
            switch (c)
            {
                case "wmon":                 // diary_out (Bild 89): zurück in den Laufmodus
                case "gofr":
                    CloseDiary(); return;
                case "camf": case "camn": return;   // Kamera (frei/normal) – hier ohne Bedeutung
                case "dbxo": AnswerUiDialog(); return;
            }
            Debug.Log("[TDU2] Tagebuch FSCommand: " + c);
        }

        /// <summary>Bestätigungsdialog des Films (DBXO -> "dbxo") mit Ja beantworten (Spiel zeigt sonst eine Ja/Nein-Box).</summary>
        void AnswerUiDialog()
        {
            Debug.Log("[TDU2] Interieur-Dialog '" + (_ui.GetVariable("/:DgbxCase") ?? "") + "' -> Ja");
            _ui.SetVariable("/:PlayerAnswer", "a");
            // home.vmf: die echte DialogBox steht in _root (Unter-Clips haben nur leere Platzhalter gleichen Namens)
            var owners = _ui.FunctionOwners("DialogBox");
            string owner = owners.Contains("/") && _ui.Find("/myhs_editor") != null ? "/" : (owners.Count > 0 ? owners[owners.Count - 1] : null);
            if (owner == null || !_ui.CallClip(owner, "DialogBox")) _ui.SetValue("/:Input", true);
        }

        void CasinoTravel(string island)
        {
            if (!_casinoMenu) return;
            _casinoMenu = false; CasinoMenuOpen = false; _casinoFlyAt = -1f;
            Racing.RacingSchoolMenu.NoteUiClosed();
            if (!TDU2IslandTravel.CanTravelTo(island, Manager.BnkDir)) { ShowInfo("Insel " + island + " fehlt in den Spieldaten."); return; }
            var car = _lastCar;
            Debug.Log("[TDU2] Casino: Flug nach " + TDU2IslandTravel.DisplayName(island));
            // Ankunft: Hawaii am Landeplatz (ELANDCAA), Ibiza ohne Landeplatz -> Flughafen (EAIR*)
            TDU2IslandTravel.Go(island, true, car != null && !Racing.RacingSchoolMenu.IsSchoolCar(car) ? car.FileName : null, "ELAND");
        }

        /// <summary>Auto, mit dem man ins Interieur gekommen ist (wird beim Verlassen wieder gespawnt).</summary>
        public CarPhysics LastCar => _lastCar;

        /// <summary>
        /// Casino-Ausgang: wie im Spiel der Flughafen-Film (airport.bnk) mit Zielwahl Hawaii/Ibiza (links/rechts), Ankunft am
        /// Landeplatz (Hawaii ELANDCAA; Ibiza hat keinen -> Flughafen).
        /// </summary>
        void OpenCasinoFlight()
        {
            var air = GetComponentInChildren<TDU2Airport>();
            if (air == null)
            {
                var go = new GameObject("TDU2 Airport");
                go.transform.SetParent(transform, false);
                air = go.AddComponent<TDU2Airport>();
            }
            air.Manager = Manager;
            air.Interiors = this;
            var list = new List<string>();
            foreach (var isl in new[] { "hawai", "ibiza" }) if (TDU2IslandTravel.CanTravelTo(isl, Manager.BnkDir)) list.Add(isl);
            if (list.Count == 0) { ShowInfo("Keine Insel zum Zurückfliegen in den Spieldaten."); return; }
            UpdateUiAim(null);
            air.Open(list.ToArray(), "ELAND");
        }

        /// <summary>Öffnet den Flughafen (Typ 32, Hostess): Original-Film airport.bnk, Flug Ibiza ↔ Hawaii.</summary>
        void OpenAirport()
        {
            var air = GetComponentInChildren<TDU2Airport>();
            if (air == null)
            {
                var go = new GameObject("TDU2 Airport");
                go.transform.SetParent(transform, false);
                air = go.AddComponent<TDU2Airport>();
            }
            air.Manager = Manager;
            air.Interiors = this;
            air.Open();
        }

        /// <summary>Öffnet das Rennschul-Menü (Typ 20). Komponente wird bei Bedarf angelegt.</summary>
        void OpenRacingSchool()
        {
#if UNITY_2023_1_OR_NEWER
            var menu = FindFirstObjectByType<RacingSchoolMenu>();
#else
            var menu = FindObjectOfType<RacingSchoolMenu>();
#endif
            if (menu == null)
            {
                var go = new GameObject("TDU2 Racing School");
                go.transform.SetParent(transform, false);
                menu = go.AddComponent<RacingSchoolMenu>();
                menu.Manager = Manager;
            }
            menu.Interiors = this;
            menu.Open();
        }

        /// <summary>
        /// Interieur verlassen; das Fahrzeug erscheint an Position2/Richtung2 des Spots.
        /// Mit <paramref name="spawnCar"/>=false wird nur die Szene verlassen (z.B. um das Auto anschließend
        /// selbst z.B. an die Startlinie einer Prüfung zu setzen – ein doppeltes SpawnCar würde sonst verworfen).
        /// Liefert das (ggf. neu bestimmte) Fahrzeug zurück.
        /// </summary>
        public CarPhysics ExitInterior(CarPhysics car, bool spawnCar = true)
        {
            _casinoMenu = false; CasinoMenuOpen = false; _casinoFlyAt = -1f;
            var s = _spot;
            if (_shop != null && TDU2StickerShop.IsOpen) _shop.Close(false);
            _dressing = false;
            if (_clothes != null && TDU2ClothesShop.IsOpen) _clothes.Close();
            if (_hair != null && TDU2Hairdresser.IsOpen) _hair.Close();
            Online.TDU2Multiplayer.HideRemotes = false;
            GuestGarageCars = null; GuestLiveries = null; GuestHost = 0; GuestHouses = null;
            SetInteriorAmbient(false);
            ClearScene();
            CloseMenu();
            if (_fp != null) { UnityUtil.SafeDestroy(_fp.gameObject); _fp = null; }
            DestroyUi();
            SetExteriorVisible(true);
            LockCursor(false);
            _spot = null; _spotBanks = null; _data = null;
            Manager.Suspended = false;
            if (s == null) return car;
            try { Exited?.Invoke(s); } catch (Exception ex) { Debug.LogException(ex); }
            // Ladebildschirm (Tipp-Bild, Speichern) bis die Welt um die Ausfahrt gebaut und das Auto geladen ist
            TDU2LoadingScreen.Show("Welt (" + s.Code + ")", () => Inside || TDU2LoadingScreen.WorldReady(), 1.0f, true, Manager.BnkDir);
            TDU2DebugLog.Event("Interieur verlassen " + s.Code + ", Auto: " + (car != null ? car.FileName + "#" + car.Id : "-") + ", spawn=" + spawnCar);

            var root = Manager.Streamer.MapRoot;
            var gp = s.Position2.sqrMagnitude > 1f ? s.Position2 : s.Position;
            var p = root.TransformPoint(gp);
            // Spots.bin Richtung2: vorne = (sin h, 0, cos h) im Spielraum (an den Straßentangenten geprüft)
            var fwd = root.TransformDirection(new Vector3(Mathf.Sin(s.Heading2), 0f, Mathf.Cos(s.Heading2)));
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            p.y = Manager.GroundHeight(p) + 0.4f;
            var cam = Camera.main;
            if (cam != null) cam.transform.SetPositionAndRotation(p - fwd * 7f + Vector3.up * 2.5f, Quaternion.LookRotation(fwd, Vector3.up));
            car = car ?? _lastCar ?? Manager.FindCar(Manager.StartCar) ?? (Manager.Available.Count > 0 ? Manager.Available[0] : null);
            if (car != null)
            {
                _lastCar = car;
                RememberOwned(car);
                if (spawnCar) Manager.SpawnCar(car, p, Quaternion.LookRotation(fwd, Vector3.up));
                TDU2DebugLog.Event("Verlassen: SpawnCar " + car.FileName + "#" + car.Id + " spawn=" + spawnCar + " inAvailable=" + Manager.Available.Contains(car) + " loading=" + Manager.IsLoadingCar);
            }
            _nextScan = Time.unscaledTime + 2f;
            return car;
        }

        readonly List<GameObject> _hiddenExteriors = new List<GameObject>();

        [Tooltip("Beim Betreten das ganze Gebäude (Spot-Exterior) ausblenden. Aus = wie im Spiel: Gebäude bleibt stehen (kein Void " +
                 "vor den Fenstern), nur der im Exterior enthaltene Interieur-Platzhalter wird ausgeblendet.")]
        public bool HideWholeExterior = false;

        /// <summary>
        /// Solange man drinnen ist: im Exterior des Spots (Spot_&lt;Code&gt;, Spot_&lt;Code&gt;_low) nur das ausblenden, was mit dem
        /// geladenen Interieur kollidiert – den Interieur-Platzhalter (Entity__Actor__Interior / Knoten "INTERIOR") und Teile, die
        /// vollständig im Interieur liegen. Fassade, Dach und Umgebung bleiben sichtbar (Blick aus dem Fenster).
        /// </summary>
        void SetExteriorVisible(bool visible, BankModel interiorModel = null)
        {
            if (visible)
            {
                foreach (var go in _hiddenExteriors) if (go != null) go.SetActive(true);
                _hiddenExteriors.Clear();
                RestoreIntruders();
                return;
            }
            if (_spot == null || Manager.Streamer.MapRoot == null) return;
            string n1 = "Spot_" + _spot.Code, n2 = n1 + "_low";
            var center = Manager.Streamer.MapRoot.TransformPoint(_spot.Position);

            // Szenennamen des Interieurs (gleiche Szene steckt oft als Platzhalter im Exterior)
            var names = new List<string>();
            if (interiorModel != null)
                foreach (var set in interiorModel.AllObjectSets())
                    if (!string.IsNullOrEmpty(set.Name)) names.Add(Path.GetFileNameWithoutExtension(set.Name));
            // Ausdehnung des Interieurs (Welt)
            bool haveBounds = false; var ib = new Bounds();
            if (!HideWholeExterior && _holder != null)
                foreach (var r in _holder.GetComponentsInChildren<Renderer>(false))
                {
                    if (!haveBounds) { ib = r.bounds; haveBounds = true; } else ib.Encapsulate(r.bounds);
                }
            if (haveBounds) { _interiorBounds = ib; _haveInteriorBounds = true; ib.Expand(1.0f); }

            foreach (var t in Manager.Streamer.MapRoot.GetComponentsInChildren<Transform>(false))
            {
                if (t.name != n1 && t.name != n2) continue;
                if ((t.position - center).sqrMagnitude > 50f * 50f) continue;
                if (HideWholeExterior || (!haveBounds && names.Count == 0))
                {
                    t.gameObject.SetActive(false);
                    _hiddenExteriors.Add(t.gameObject);
                    continue;
                }
                HideInteriorParts(t, names, haveBounds, ib);
            }
        }

        void HideInteriorParts(Transform spotRoot, List<string> names, bool haveBounds, Bounds interior)
        {
            for (int i = 0; i < spotRoot.childCount; i++)
            {
                var c = spotRoot.GetChild(i);
                if (!c.gameObject.activeSelf) continue;
                bool hide = string.Equals(c.name, "INTERIOR", StringComparison.OrdinalIgnoreCase);
                if (!hide)
                    foreach (var n in names)
                        if (c.name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) { hide = true; break; }
                if (!hide && haveBounds)
                {
                    // Teil liegt komplett im Interieur (z.B. vereinfachte Innenräume des Exteriors)?
                    bool any = false; var b = new Bounds();
                    foreach (var r in c.GetComponentsInChildren<Renderer>(false)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                    hide = any && interior.Contains(b.min) && interior.Contains(b.max);
                }
                if (hide) { c.gameObject.SetActive(false); _hiddenExteriors.Add(c.gameObject); continue; }
                // Knoten "INTERIOR" kann tiefer liegen
                foreach (var d in c.GetComponentsInChildren<Transform>(false))
                    if (d != c && string.Equals(d.name, "INTERIOR", StringComparison.OrdinalIgnoreCase))
                    { d.gameObject.SetActive(false); _hiddenExteriors.Add(d.gameObject); }
            }
        }

        // ------------------------------------------------------------------ Außenwelt ausblenden
        [Tooltip("Wie im Spiel: Interieurs sind eigene Szenen – solange man drinnen ist, wird die Außenwelt (Insel, Gebäude, " +
                 "Bäume, Gras, Verkehr) nicht gezeichnet. Sonst ragen Dach, Palmen und Nachbarhäuser in Garage/Händler/Club.")]
        public bool HideOutsideWorld = true;
        [Tooltip("Auch im Wohnbereich (Haus) ausblenden – dort sieht man im Spiel durch die Fenster nach draußen")]
        public bool HideOutsideInLiving = false;
        readonly List<Renderer> _hiddenWorld = new List<Renderer>();
        readonly List<Behaviour> _hiddenWorldBehaviours = new List<Behaviour>();
        float _nextWorldScan;

        [Tooltip("Außenwelt auch in Händlern, Shops, Casino usw. ausblenden. Aus = wie im Spiel: durch Fenster und Glasfronten sieht man " +
                 "die Insel; nur Garagen (fensterlos, Häuser/Hügel ragen sonst hinein) blenden die Außenwelt aus.")]
        public bool HideOutsideEverywhere = false;

        // Wie im Spiel: Interieurs stehen am Spot in der Welt, durch Fenster sieht man die Insel (Händler, Shops, Casino, Wohnbereich).
        // Nur in Garagen (geschlossene Räume, oft im Hang/unter dem Haus) wird die Außenwelt ausgeblendet; was sonst in den Raum
        // ragt, schneidet der Interieur-Clip (TDU2InteriorClip) bzw. SetExteriorVisible weg.
        bool WorldHiddenWanted => HideOutsideWorld && _holder != null
            && (_kind == Kind.Garage || HideOutsideEverywhere || (_kind == Kind.Living && HideOutsideInLiving));

        void HideWorld()
        {
            if (!WorldHiddenWanted) { if (_hiddenWorld.Count > 0 || _hiddenWorldBehaviours.Count > 0) ShowWorld(); return; }
            var root = Manager != null && Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
            if (root != null)
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    var t = r.transform;
                    if (t.IsChildOf(_holder) || (_carsRoot != null && t.IsChildOf(_carsRoot))) continue;
                    r.enabled = false; _hiddenWorld.Add(r);
                }
                // Bäume/Büsche/Felsen werden per Instancing gezeichnet (keine Renderer) -> Komponente anhalten
                foreach (var v in root.GetComponentsInChildren<VegetationRenderer>(false))
                    if (v != null && v.enabled) { v.enabled = false; _hiddenWorldBehaviours.Add(v); }
            }
            // Verkehr
            var traffic = GameObject.Find("TDU2 Verkehr");
            if (traffic != null)
                foreach (var r in traffic.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { r.enabled = false; _hiddenWorld.Add(r); }
        }

        void ShowWorld()
        {
            foreach (var r in _hiddenWorld) if (r != null) r.enabled = true;
            foreach (var b in _hiddenWorldBehaviours) if (b != null) b.enabled = true;
            _hiddenWorld.Clear(); _hiddenWorldBehaviours.Clear();
            TDU2TrafficLights.ForceRefresh();   // Ampeln: nur die gerade aktive Leuchte an
        }

        void ClearScene()
        {
            EndDealerModes();
            ShowWorld();
            RestoreIntruders();
            _gen++;
            _carQueue.Clear();
            foreach (var c in _cars)
            {
                c.Model?.Destroy();
                c.Assets?.DestroyTextures();
                if (c.Go != null) UnityUtil.SafeDestroy(c.Go);
            }
            _cars.Clear();
            foreach (var f in _furniture) f.Destroy();
            _furniture.Clear();
            ClearHouse();
            EndLifeModes();
            if (_npcs != null) _npcs.Clear();
            _scene?.Destroy(); _scene = null;
            if (_holder != null) UnityUtil.SafeDestroy(_holder.gameObject);
            if (_carsRoot != null) UnityUtil.SafeDestroy(_carsRoot.gameObject);
            _holder = null; _carsRoot = null; _data = null;
        }

        void OnDestroy()
        {
            ClearScene();
            if (_fp != null) UnityUtil.SafeDestroy(_fp.gameObject);
        }

        // ------------------------------------------------------------------ Eigene Autos (Garage)
        List<CarPhysics> OwnedCars()
        {
            var r = new List<CarPhysics>();
            if (TDU2Session.LoggedIn)
            {
                // angemeldet: eigene Autos aus dem Spielstand (Server)
                foreach (var n in TDU2Session.OwnedCars)
                {
                    var c = Manager.FindCar(n);
                    if (c != null && !r.Contains(c)) r.Add(c);
                }
                return r;
            }
            string s = "";
            try { s = PlayerPrefs.GetString(OwnedKey, ""); } catch (Exception) { }
            foreach (var n in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var c = Manager.FindCar(n);
                if (c != null && !r.Contains(c)) r.Add(c);
            }
            return r;
        }

        void RememberOwned(CarPhysics car)
        {
            if (TestDriveActive) return;   // Leihwagen der Testfahrt nie als eigenes Auto
            if (car == null || Racing.RacingSchoolMenu.IsSchoolCar(car)) return;   // Prüfungsautos der Rennschule nie als eigenes Auto
            if (TDU2Session.LoggedIn) { TDU2Session.AddOwned(car.FileName); TDU2Session.SetCurrentCar(car.FileName); return; }
            var names = new List<string> { car.FileName };
            foreach (var c in OwnedCars()) if (!string.Equals(c.FileName, car.FileName, StringComparison.OrdinalIgnoreCase)) names.Add(c.FileName);
            if (names.Count > 24) names.RemoveRange(24, names.Count - 24);
            try { PlayerPrefs.SetString(OwnedKey, string.Join(",", names)); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ UI
        void OpenMenu(string title, List<CarPhysics> cars, bool buy)
        {
            _menuOpen = true; _menuTitle = title; _menuCars = cars; _menuBuy = buy; _menuAll = false; _menuScroll = Vector2.zero;
            LockCursor(false);
        }

        void CloseMenu()
        {
            if (!_menuOpen) return;
            _menuOpen = false;
            if (Inside) LockCursor(true);
        }

        void ShowInfo(string text) { _info = text; _infoUntil = Time.unscaledTime + 5f; }

        static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void EnsureStyles()
        {
            if (_box != null) return;
            _box = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _box.normal.textColor = Color.white;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _label.normal.textColor = Color.white;
            _small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            _small.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
        }

        void OnGUI()
        {
            //if (Manager == null) return;
            //EnsureStyles();
            //float w = Screen.width, h = Screen.height;
            //if (!Inside)
            //{
            //    var mm = Manager.Minimap;
            //    if (mm != null && mm.HasOriginalHud) return;   // Original-Einfahrtsanzeige (hud.vmf /HUD/enter)
            //    if (ShowPrompts && _nearSpot != null && _nearBanks != null)
            //    {
            //        var r = new Rect(w * 0.5f - 230f, h * 0.72f, 460f, 34f);
            //        GUI.Box(r, $"[Enter]  {_nearBanks.Name} betreten  ({_nearSpot.Code})", _box);
            //    }
            //    return;
            //}

            //if (_status != null) GUI.Box(new Rect(w * 0.5f - 200f, h * 0.45f, 400f, 40f), _status, _box);
            //if (TDU2ClothesShop.IsOpen || TDU2Hairdresser.IsOpen) return;   // Original-Oberfläche (Kleidungsladen, Friseur)
            //if (_ui != null) return;   // Original-Oberfläche aktiv – keine eigenen Einblendungen
            //if (!_menuOpen && _status == null)
            //{
            //    // Fadenkreuz
            //    var c = new Rect(w * 0.5f - 3f, h * 0.5f - 3f, 6f, 6f);
            //    GUI.color = new Color(1f, 1f, 1f, _aimLabel != null ? 1f : 0.5f);
            //    GUI.DrawTexture(c, Texture2D.whiteTexture);
            //    GUI.color = Color.white;
            //    if (_aimLabel != null)
            //    {
            //        GUI.color = new Color(0f, 0f, 0f, 0.6f);
            //        GUI.Label(new Rect(w * 0.5f - 299f, h * 0.5f + 19f, 600f, 30f), "[E]  " + _aimLabel, _label);
            //        GUI.color = Color.white;
            //        GUI.Label(new Rect(w * 0.5f - 300f, h * 0.5f + 18f, 600f, 30f), "[E]  " + _aimLabel, _label);
            //    }
            //}
            //if (!_menuOpen && _aimTarget is DisplayCar dcar) DrawCarInfo(dcar.Car, w, h);
            //if (_info != null && Time.unscaledTime < _infoUntil)
            //    GUI.Box(new Rect(w * 0.5f - 300f, h * 0.18f, 600f, 54f), _info, _box);

            //string where = _kind == Kind.Dealer ? "Autohändler" : _kind == Kind.Garage ? "Garage" : _kind == Kind.Living ? "Wohnbereich" : (_spotBanks?.Name ?? "Interieur");
            //GUI.Label(new Rect(0f, h - 30f, w, 24f), $"{where} ({_spot?.Code})   ·   WASD laufen · Shift rennen · Maus umsehen · E / Klick benutzen · Esc Maus freigeben", _small);

            //if (_menuOpen) DrawMenu(w, h);
        }

        GUIStyle _infoTitle, _infoLine;

        /// <summary>Fahrzeugdaten links/rechts neben dem anvisierten Auto (wie die Händler-Infotafeln im Spiel).</summary>
        void DrawCarInfo(CarPhysics c, float w, float h)
        {
            if (c == null) return;
            if (_infoTitle == null)
            {
                _infoTitle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
                _infoTitle.normal.textColor = Color.white;
                _infoLine = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, richText = true };
                _infoLine.normal.textColor = new Color(0.92f, 0.95f, 1f);
            }
            var r = c.Raw;
            var ci = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            string version = r?.S("_version_name", "") ?? "";
            if (version == "##") version = "";
            int price = r != null ? r.I("_price") : 0;
            float bhp = r != null ? r.F("_power_bhp") : 0f, prpm = r != null ? r.F("_power_rpm") : 0f;
            float nm = r != null ? r.F("_torque_nm") : 0f, trpm = r != null ? r.F("_torque_rpm") : 0f;
            float disp = r != null ? r.F("_displacement") : 0f;
            int gears = r != null ? r.I("_nb_gears") : 0;

            // links: Name, Version, Preis
            float pw = Mathf.Min(360f, w * 0.24f), ph = 190f;
            var left = new Rect(w * 0.04f, h * 0.30f, pw, ph);
            GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(left, Texture2D.whiteTexture); GUI.color = Color.white;
            GUI.DrawTexture(new Rect(left.x, left.y, 4f, left.height), Texture2D.whiteTexture);
            float y = left.y + 12f;
            if (!string.IsNullOrEmpty(c.Brand)) { GUI.Label(new Rect(left.x + 16f, y, pw - 24f, 24f), c.Brand.ToUpperInvariant(), _infoLine); y += 24f; }
            GUI.Label(new Rect(left.x + 16f, y, pw - 24f, 60f), c.ModelName + (version.Length > 0 ? " " + version : ""), _infoTitle); y += 64f;
            if (price > 0) GUI.Label(new Rect(left.x + 16f, y, pw - 24f, 24f), "<b>Preis</b>   " + price.ToString("N0", ci) + " $", _infoLine);
            y += 26f;
            if (_kind == Kind.Dealer) GUI.Label(new Rect(left.x + 16f, y, pw - 24f, 40f), "[E] kaufen und losfahren", _infoLine);
            else GUI.Label(new Rect(left.x + 16f, y, pw - 24f, 40f), "[E] mit diesem Auto losfahren", _infoLine);

            // rechts: technische Daten
            var right = new Rect(w * 0.96f - pw, h * 0.30f, pw, 230f);
            GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(right, Texture2D.whiteTexture); GUI.color = Color.white;
            GUI.DrawTexture(new Rect(right.xMax - 4f, right.y, 4f, right.height), Texture2D.whiteTexture);
            var lines = new List<string>();
            if (bhp > 0) lines.Add($"<b>Leistung</b>   {bhp:0} bhp" + (prpm > 0 ? $" bei {prpm:0} U/min" : ""));
            if (nm > 0) lines.Add($"<b>Drehmoment</b>   {nm:0} Nm" + (trpm > 0 ? $" bei {trpm:0} U/min" : ""));
            if (disp > 0) lines.Add($"<b>Hubraum</b>   {disp:0} cm³");
            if (c.MaxTheoricalSpeed > 0) lines.Add($"<b>Höchstgeschw.</b>   {c.MaxTheoricalSpeed:0} km/h");
            if (c.Accel0To100 > 0) lines.Add("<b>0–100 km/h</b>   " + c.Accel0To100.ToString("0.0", ci) + " s");
            if (c.Mass > 0) lines.Add($"<b>Gewicht</b>   {c.Mass:0} kg");
            if (gears > 0) lines.Add($"<b>Getriebe</b>   {gears} Gänge");
            y = right.y + 14f;
            foreach (var l in lines) { GUI.Label(new Rect(right.x + 14f, y, pw - 26f, 26f), l, _infoLine); y += 28f; }
        }

        void DrawMenu(float w, float h)
        {
            var r = new Rect(w * 0.5f - 260f, h * 0.15f, 520f, h * 0.7f);
            GUI.Box(r, "");
            GUI.Label(new Rect(r.x, r.y + 8f, r.width, 28f), _menuTitle, _label);
            var list = _menuAll ? Manager.Available : _menuCars;
            var view = new Rect(r.x + 12f, r.y + 44f, r.width - 24f, r.height - 100f);
            var content = new Rect(0f, 0f, view.width - 20f, Mathf.Max(view.height, list.Count * 30f));
            _menuScroll = GUI.BeginScrollView(view, _menuScroll, content);
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                string txt = string.IsNullOrEmpty(c.Brand) ? c.ModelName : c.Brand + "  " + c.ModelName;
                if (GUI.Button(new Rect(0f, i * 30f, content.width, 26f), txt))
                {
                    if (_menuBuy) RememberOwned(c);
                    ExitInterior(c);
                    return;
                }
            }
            if (list.Count == 0) GUI.Label(new Rect(0f, 0f, content.width, 26f), "keine Autos", _small);
            GUI.EndScrollView();
            if (!_menuBuy && GUI.Button(new Rect(r.x + 12f, r.yMax - 46f, 200f, 32f), _menuAll ? "Nur meine Autos" : "Alle Autos")) { _menuAll = !_menuAll; _menuScroll = Vector2.zero; }
            if (GUI.Button(new Rect(r.xMax - 152f, r.yMax - 46f, 140f, 32f), "Schließen")) CloseMenu();
        }
    }
}
