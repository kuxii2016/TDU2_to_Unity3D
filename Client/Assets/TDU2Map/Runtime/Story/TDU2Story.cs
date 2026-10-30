using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TDU2.Avatars;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.World;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Story
{
    /// <summary>
    /// Fortschritt der Geschichte je Profil (wie profile+0x58 im Original, FUN_00b10da0). Schritte (Namen aus dem Spiel):
    ///  0 Avatar choice / First WalkMode, 1 First Garage, 2 First Freeride, 3 Meeting Tess, 4 Driving Tess,
    ///  5 Solar Crown exterior, 6 Solar Crown interior, 7 Used Car Dealer, 8 Tuto FROG, 9 Go Home,
    ///  10 Home sweet home : Caravan, 11 Hawai First Freeride, 12 Igloo First Intro.
    /// Gespeichert lokal (PlayerPrefs "TDU2.Story.&lt;Profil&gt;"), dazu der gewählte Intro-Avatar (DBPNJ-Konfiguration).
    /// </summary>
    public static class TDU2StoryProgress
    {
        public static readonly string[] StepNames =
        {
            "Avatar choice / First WalkMode", "First Garage", "First Freeride", "Meeting Tess", "Driving Tess",
            "Solar Crown exterior", "Solar Crown interior", "Used Car Dealer", "Tuto FROG", "Go Home",
            "Home sweet home : Caravan", "Hawai First Freeride", "Igloo First Intro",
        };

        /// <summary>Ab diesem Schritt ist die Geschichte (soweit umgesetzt) durch: normales Spiel.</summary>
        public const int Implemented = 11;

        static string Key(string nick) => "TDU2.Story." + (string.IsNullOrEmpty(nick) ? "_offline" : nick.ToLowerInvariant());

        /// <summary>Schritt des Profils (-1 = noch nie begonnen).</summary>
        public static int Get(string nick) { try { return PlayerPrefs.GetInt(Key(nick), -1); } catch { return -1; } }

        public static void Set(string nick, int step)
        {
            try { PlayerPrefs.SetInt(Key(nick), step); PlayerPrefs.Save(); } catch { }
            DebugManager.Info("[TDU2] Geschichte: Schritt " + step + " (" + StepName(step) + ")");
        }

        public static string IntroAvatar(string nick) { try { return PlayerPrefs.GetString(Key(nick) + ".Avatar", ""); } catch { return ""; } }
        public static void SetIntroAvatar(string nick, string cfg) { try { PlayerPrefs.SetString(Key(nick) + ".Avatar", cfg ?? ""); PlayerPrefs.Save(); } catch { } }

        /// <summary>Hat die Geschichte das Story-Auto (California) in die Garage gelegt? (Tess nimmt es an der Solar Crown zurück)</summary>
        public static bool GaveStoryCar(string nick) { try { return PlayerPrefs.GetInt(Key(nick) + ".Gave", 0) == 1; } catch { return false; } }
        public static void SetGaveStoryCar(string nick, bool on) { try { PlayerPrefs.SetInt(Key(nick) + ".Gave", on ? 1 : 0); PlayerPrefs.Save(); } catch { } }

        public static string StepName(int step) => step >= 0 && step < StepNames.Length ? StepNames[step] : "Not handled";
    }

    /// <summary>
    /// Regie der Geschichte (Unity-Port der Spielmodi AVATAR_CHOICE / FIRST_WM aus OpenTDU2 und der Story-Schritte 2–5 des Originals):
    /// startet für ein neues Profil (GamePC.cpr SCENARIOTUTORIAL = ON) statt des direkten Spielstarts und gibt das Auto
    /// über <see cref="Spawn"/> an das normale Spiel (TDU2Login) zurück.
    /// </summary>
    public sealed class TDU2Story : MonoBehaviour
    {
        public static TDU2Story Instance { get; private set; }
        /// <summary>Läuft gerade ein Kino/Laufmodus der Geschichte (Pausemenü, Karte und Fahrtasten gesperrt)?</summary>
        public static bool BlocksPauseMenu => Instance != null && Instance._mode != null && Instance._mode.BlocksInput;
        /// <summary>Große Karte gesperrt (zusätzlich zu den Kinos auch während Missionen wie "Driving Tess").</summary>
        public static bool BlocksMap => Instance != null && Instance._mode != null && (Instance._mode.BlocksInput || Instance._mode.BlocksMap);

        /// <summary>Laufender Story-Schritt (-1 = keine Geschichte), z.B. für Discord RPC (<see cref="TDU2GameMode.MissionStep"/>).</summary>
        public static int CurrentStep => Instance != null && Instance._mode != null ? Instance.Step : -1;
        /// <summary>Name des laufenden Story-Schritts wie im Spiel ("Driving Tess" …), null = keine Geschichte.</summary>
        public static string CurrentMission => CurrentStep >= 0 ? TDU2StoryProgress.StepName(CurrentStep) : null;
        /// <summary>Zuletzt angezeigtes Ziel/Tipp der Geschichte (lokalisiert, z.B. "Tess zum Drehort fahren."), null = keins.</summary>
        public static string CurrentObjective { get; internal set; }

        public TDU2VehicleManager Manager { get; private set; }
        public string Nick { get; private set; }
        public int Step { get; private set; }
        /// <summary>Rückruf ins Spiel: Auto an Weltposition/-drehung setzen und freie Fahrt beginnen.</summary>
        public Action<CarPhysics, Vector3, Quaternion> Spawn;
        /// <summary>Rückruf ins Spiel: Gebrauchtwagenhändler mit den Startautos (TDU2Login/TDU2Interiors), gewähltes Auto -> Aktion.</summary>
        public Action<Action<CarPhysics>> StarterDealer;

        StoryMode _mode;
        float _blackUntil;

        /// <summary>California (DBCarPhysicsData _id_car 387): das Geschenk von Tess, Auto der ersten Schritte.</summary>
        public const int StoryCarId = 387;
        public CarPhysics StoryCar => Manager?.Database?.Cars.Find(c => c.Id == StoryCarId) ?? Manager?.FindCar("California");

        /// <summary>Eigenes Auto des Spielers (zuletzt gekauftes aus dem Spielstand), sonst das Start- bzw. Story-Auto.</summary>
        public CarPhysics PlayerCar()
        {
            var owned = Online.TDU2Session.Save?.ownedCars;
            if (owned != null)
                for (int i = owned.Count - 1; i >= 0; i--)
                {
                    var c = Manager.FindCar(owned[i]);
                    if (c != null && c.Id != StoryCarId) return c;
                }
            return Manager.FindCar(Manager.StartCar) ?? StoryCar;
        }

        /// <summary>Geschichte statt des normalen Starts? (SCENARIOTUTORIAL = ON und Schritt noch nicht durch)</summary>
        public static bool ShouldRun(TDU2GamePC gp, string nick, bool starterChosen)
        {
            ApplyStepOverride(nick);
            if (gp == null || !string.Equals(gp.Get("SCENARIOTUTORIAL") ?? "", "ON", StringComparison.OrdinalIgnoreCase)) return false;
            // noch nicht gespielt (auch bei Spielständen mit Auto) oder mittendrin: Geschichte (ab dem gespeicherten Schritt)
            int step = TDU2StoryProgress.Get(nick);
            return step < TDU2StoryProgress.Implemented;
        }

        static bool _overrideDone;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _overrideDone = false; }

        /// <summary>
        /// Story neu beginnen: DebugManager.StoryStartStep bzw. Startparameter -STORYSTEP n setzt den gespeicherten Schritt einmalig
        /// (z.B. 0 = Intro von vorn, wenn ein Durchlauf abgebrochen wurde). Danach gilt wieder der gespeicherte Fortschritt.
        /// </summary>
        static void ApplyStepOverride(string nick)
        {
            if (_overrideDone) return;
            int step = -1;
            var dm = DebugManager.Instance;
            if (dm != null && dm.StoryStartStep >= 0) step = dm.StoryStartStep;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-STORYSTEP", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int n)) step = n;
            if (step < 0) return;
            _overrideDone = true;
            step = Mathf.Clamp(step, 0, TDU2StoryProgress.Implemented);
            DebugManager.Info("[TDU2] Geschichte: Neustart ab Schritt " + step + " (" + TDU2StoryProgress.StepName(step) + ")");
            TDU2StoryProgress.Set(nick, step);
            if (step <= 4) TDU2StoryProgress.SetGaveStoryCar(nick, false);
        }

        public static TDU2Story Begin(TDU2VehicleManager mgr, string nick, Action<CarPhysics, Vector3, Quaternion> spawn)
        {
            if (Instance != null) Destroy(Instance.gameObject);
            var go = new GameObject("TDU2 Story");
            var s = go.AddComponent<TDU2Story>();
            Instance = s;
            s.Manager = mgr; s.Nick = nick; s.Spawn = spawn;
            int step = Mathf.Max(0, TDU2StoryProgress.Get(nick));
            // Schritt 2 (erste freie Fahrt) setzt ein Auto voraus: ohne Spielstand-Auto mit dem Ausfahrt-Kino beginnen
            s.Go(step);
            return s;
        }

        bool _ticking;
        int _pendingStep = -1;
        bool _pendingAbort;

        /// <summary>Zum Schritt wechseln (speichert den Fortschritt). Aus einem Schritt heraus erst nach dessen Tick.</summary>
        public void Go(int step)
        {
            if (_ticking) { _pendingStep = step; return; }
            Switch(step);
        }

        void Switch(int step)
        {
            try { _mode?.Exit(); } catch (Exception ex) { DebugManager.Error("[TDU2] Geschichte Exit: " + ex.Message); }
            _mode = null;
            TDU2Diag.Log("STORY", $"Schritt {Step} -> {step}");
            Step = step;
            CurrentObjective = null;
            TDU2StoryProgress.Set(Nick, step);
            _mode = Create(step);
            if (_mode == null)
            {
                DebugManager.Info("[TDU2] Geschichte: Schritt " + step + " (" + TDU2StoryProgress.StepName(step) + ") nicht umgesetzt – freie Fahrt");
                TDU2GameMode.Pop();
                Destroy(gameObject);
                return;
            }
            _mode.Story = this;
            try { _mode.Enter(); }
            catch (Exception ex) { DebugManager.Error("[TDU2] Geschichte Schritt " + step + ": " + ex); Abort(); }
        }

        StoryMode Create(int step)
        {
            switch (step)
            {
                case 0: return new AvatarChoiceMode();
                case 1: return new FirstGarageMode();
                case 2: return new FirstFreerideMode();
                case 3: return new MeetingTessMode();
                case 4: return new DrivingTessMode();
                case 5: return new SolarCrownExteriorMode();
                case 6: return new SolarCrownInteriorMode();
                case 7: return new UsedCarDealerMode();
                case 8: return new TutoFrogMode();
                case 9: return new GoHomeMode();
                case 10: return new CaravanMode();
                default: return null;
            }
        }

        /// <summary>Fehler: Geschichte abbrechen und mit dem Story-Auto in die freie Fahrt (am Haus EPH_L3DI_5067I).</summary>
        public void Abort()
        {
            if (_ticking) { _pendingAbort = true; return; }
            DebugManager.Warning("[TDU2] Geschichte abgebrochen – freie Fahrt");
            try { _mode?.Exit(); } catch { }
            _mode = null;
            TDU2GameMode.Pop();
            if (NeedsCar) SpawnAtSpot(StoryCar, "EPH_L3DI_5067I");
            FadeOverlay.Clear();
            Destroy(gameObject);
        }

        void Update()
        {
            if (SpawnPending && Manager.Current != null) SpawnPending = false;
            // Tageszeit/Wetter der Geschichte fest (sonnig, trocken, Nachmittag) solange ein Schritt läuft
            TDU2.World.TDU2Weather.StoryLock = _mode != null;
            if (_mode == null) return;
            // Geschichte ist keine freie Fahrt: Mitspieler nicht anzeigen (jedes Bild, Interieurs setzen den Schalter beim Verlassen zurück)
            Online.TDU2Multiplayer.HideRemotes = true;
            UpdateRadio(_mode.MutesRadio);
            _ticking = true;
            try { _mode.Tick(Time.deltaTime); }
            catch (Exception ex) { DebugManager.Error("[TDU2] Geschichte Schritt " + Step + ": " + ex); _pendingAbort = true; }
            finally { _ticking = false; }
            if (_pendingAbort) { _pendingAbort = false; _pendingStep = -1; Abort(); return; }
            if (_pendingStep >= 0) { int st = _pendingStep; _pendingStep = -1; Switch(st); }
        }

        bool _radioMuted;

        /// <summary>Autoradio während Kinos/Laufmodus stumm (nur selbst gesetzte Stummschaltung wieder aufheben).</summary>
        void UpdateRadio(bool mute)
        {
            var r = TDU2Radio.Instance;
            if (r == null) { _radioMuted = false; return; }
            if (mute && !_radioMuted) { if (!r.Muted) { r.Muted = true; _radioMuted = true; } }
            else if (!mute && _radioMuted) { r.Muted = false; _radioMuted = false; }
        }

        /// <summary>Wurde ein Auto angefordert, das noch nicht steht?</summary>
        public bool SpawnPending { get; private set; }

        /// <summary>Spieler ist in einem Interieur (Haus, Händler …): dort gibt es kein Auto – kein Ersatz-Auto auf die Straße setzen.</summary>
        public bool InInterior { get { var i = Manager != null ? Manager.GetComponent<TDU2Interiors>() : null; return i != null && i.Inside; } }

        /// <summary>Kein Auto, keins angefordert und nicht in einem Interieur: Ersatz-Auto (Fortsetzen nach dem Anmelden) setzen.</summary>
        public bool NeedsCar => Manager != null && Manager.Current == null && !SpawnPending && !InInterior;

        /// <summary>Auto über das Spiel (TDU2Login) setzen und freie Fahrt beginnen.</summary>
        public void RequestSpawn(CarPhysics car, Vector3 worldPos, Quaternion worldRot)
        {
            if (car == null) car = StoryCar;
            if (car != null && car.Id == StoryCarId && Online.TDU2Session.Save != null && !Online.TDU2Session.Save.ownedCars.Exists(c => string.Equals(c, car.FileName, StringComparison.OrdinalIgnoreCase)))
                TDU2StoryProgress.SetGaveStoryCar(Nick, true);
            SpawnPending = true;
            Spawn?.Invoke(car, worldPos, worldRot);
        }

        void OnDestroy()
        {
            if (Instance == this) { Instance = null; Online.TDU2Multiplayer.HideRemotes = false; CurrentObjective = null; TDU2.World.TDU2Weather.StoryLock = false; }
            UpdateRadio(false);
            try { _mode?.Exit(); } catch { }
            _mode = null;
        }

        // ------------------------------------------------------------------ Hilfen für die Schritte
        public Spot FindSpot(string name) => TDU2GamePC.FindSpot(Manager.Streamer?.Spots?.Spots, name);

        /// <summary>Auto am Spot (Ausfahrt Position2/Heading2) in die freie Fahrt setzen.</summary>
        public void SpawnAtSpot(CarPhysics car, string spotName)
        {
            var s = FindSpot(spotName);
            var root = Manager.Streamer.MapRoot;
            if (s == null || car == null) { RequestSpawn(car ?? StoryCar, Camera.main != null ? Camera.main.transform.position : Vector3.zero, Quaternion.identity); return; }
            var at = s.Position2.sqrMagnitude > 1f ? s.Position2 : s.Position;
            var dir = root.TransformDirection(new Vector3(Mathf.Sin(s.Heading2), 0f, Mathf.Cos(s.Heading2)));
            dir.y = 0f;
            RequestSpawn(car, root.TransformPoint(at), Quaternion.LookRotation(dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward, Vector3.up));
        }

        /// <summary>Auto an den Start einer Mission (.ig2: erster Checkpoint, Lage und Fahrtrichtung). false = keine Datei/kein Checkpoint.</summary>
        public static bool SpawnAtMissionStart(TDU2Story story, string ig2)
        {
            if (story == null || story.Manager?.Streamer?.MapRoot == null) return false;
            try
            {
                var path = CarDatabase.FindFile(System.IO.Path.Combine(story.Manager.BnkDir, "ig2"), ig2);
                if (path == null) return false;
                var ig = TDU2.Formats.Ige2File.Parse(System.IO.File.ReadAllBytes(path));
                if (ig == null || ig.Checkpoints.Count == 0) return false;
                var cp = ig.Checkpoints[0];
                var root = story.Manager.Streamer.MapRoot;
                var dir = root.TransformDirection(cp.Forward); dir.y = 0f;
                story.RequestSpawn(story.StoryCar, root.TransformPoint(cp.Position), Quaternion.LookRotation(dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward, Vector3.up));
                DebugManager.Info("[TDU2] Geschichte: Auto am Start von " + ig2 + " " + cp.Position);
                TDU2Diag.Log("STORY", $"Auto an den Start von {ig2}: spiel={TDU2Diag.F(cp.Position)} welt={TDU2Diag.F(root.TransformPoint(cp.Position))} fwd={TDU2Diag.F(dir)}");
                return true;
            }
            catch (Exception ex) { DebugManager.Warning("[TDU2] " + ig2 + ": " + ex.Message); return false; }
        }

        /// <summary>Kamera an eine Spielposition (Streaming lädt dort), z.B. vor dem Laden einer Szene.</summary>
        public void HoldCameraAt(Vector3 world, Vector3 forward)
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            cam.transform.SetPositionAndRotation(world, Quaternion.LookRotation(forward.normalized, Vector3.up));
        }

        /// <summary>Taste einer Aktion gedrückt (auch wenn die Fahrtasten gesperrt sind).</summary>
        public static bool ActionHeld(string id)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            var a = TDU2Controls.Get(id);
            if (a == null) return false;
            return (a.Key1 != KeyCode.None && Input.GetKey(a.Key1)) || (a.Key2 != KeyCode.None && Input.GetKey(a.Key2));
#else
            return false;
#endif
        }

        public static bool ValidatePressed()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            // einheitlich wie in den Interieurs/Läden (TDU2Interiors, TDU2MyHouse, Händler): Enter, Ziffernblock-Enter, E, Leertaste, Maus
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0);
#else
            return false;
#endif
        }

        public static bool SkipPressed()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Space);
#else
            return false;
#endif
        }

        /// <summary>Kino-Überspringen erlaubt (GamePC.cpr ALLOWSKIPCINE, Standard 1).</summary>
        public bool AllowSkip
        {
            get
            {
                if (_allowSkip < 0)
                {
                    var gp = TDU2GamePC.Load(Manager.BnkDir);
                    var v = gp?.Get("ALLOWSKIPCINE");
                    _allowSkip = v == null || v.Trim() != "0" ? 1 : 0;
                }
                return _allowSkip == 1;
            }
        }
        int _allowSkip = -1;

        /// <summary>Anzeige-Modell eines Autos (ohne Physik) laden; fertig -> <paramref name="done"/>.</summary>
        public void LoadDisplayCar(CarPhysics car, Action<GameObject, CarModel> done)
        {
            if (car == null) { done?.Invoke(null, null); return; }
            string bnk = Manager.BnkDir; var db = Manager.Database;
            var task = Task.Run(() => CarAssets.Load(bnk, db, car, 0, false));
            _pendingCars.Add((task, car, done));
        }

        readonly List<(Task<CarAssets> task, CarPhysics car, Action<GameObject, CarModel> done)> _pendingCars = new List<(Task<CarAssets>, CarPhysics, Action<GameObject, CarModel>)>();

        void LateUpdate()
        {
            for (int i = _pendingCars.Count - 1; i >= 0; i--)
            {
                var (task, car, done) = _pendingCars[i];
                if (!task.IsCompleted) continue;
                _pendingCars.RemoveAt(i);
                GameObject go = null; CarModel model = null;
                if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                {
                    try
                    {
                        go = new GameObject("Story " + car.ModelName);
                        model = CarModel.Build(task.Result, go.transform);
                        TDU2WheelSpin.Attach(go, model, task.Result);   // Räder drehen mit (Ausfahrt-Kinos)
                    }
                    catch (Exception ex) { DebugManager.Error("[TDU2] Story-Auto " + car.FileName + ": " + ex.Message); }
                }
                else DebugManager.Error("[TDU2] Story-Auto " + car.FileName + ": " + task.Exception?.GetBaseException().Message);
                try { done?.Invoke(go, model); } catch (Exception ex) { DebugManager.Error("[TDU2] Story-Auto: " + ex.Message); }
            }
        }
    }

    // =====================================================================================================================
    public abstract class StoryMode
    {
        public TDU2Story Story;
        protected TDU2VehicleManager Manager => Story.Manager;
        /// <summary>Pausemenü/Karte gesperrt (Kino, Laufmodus).</summary>
        public virtual bool BlocksInput => true;
        /// <summary>Große Karte gesperrt (Kinos/Laufmodus und Missionen mit festem Ziel, z.B. Tess zum Drehort).</summary>
        public virtual bool BlocksMap => BlocksInput;
        /// <summary>Autoradio stumm (Kinos, Laufmodus: dort laufen die Töne/Musik der Szene).</summary>
        public virtual bool MutesRadio => BlocksInput;
        public abstract void Enter();
        public abstract void Tick(float dt);
        public virtual void Exit() { }

        /// <summary>Spielerauto weg, Fahrzeugsteuerung angehalten (Kino-/Laufmodus).</summary>
        protected void Suspend()
        {
            if (Manager.Current != null) Manager.RemoveCar();
            Manager.Suspended = true;
        }

        protected string PlayerAvatarConfig()
        {
            var c = TDU2StoryProgress.IntroAvatar(Story.Nick);
            if (string.IsNullOrEmpty(c)) c = AvatarConfigs.Local != null && AvatarConfigs.Local.Sex == 1 ? "AVT_Female_Intro" : "AVT_Male_Intro";
            return c;
        }

        protected bool PlayerIsWoman => PlayerAvatarConfig().IndexOf("Female", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // =====================================================================================================================
    /// <summary>
    /// Laufmodus auf einer Story-Szene (OpenTDU2 WalkMode): Ich-Perspektive (TDU2FirstPerson) ab einer Entity__Camera,
    /// Auswahl der FpSelectables (vor dem Auge, näher als 10 m, Strahl trifft das Volumen, nicht verdeckt; 0,2 s Sperre) mit
    /// INTRO-Film ItemType('over'/'out') und /:SelItemType.
    /// </summary>
    public sealed class StoryWalk
    {
        const float SelectRadius = 10f, Cooldown = 0.2f;
        static readonly Dictionary<int, string> TypeNames = new Dictionary<int, string>
        {
            { 0, "exit" }, { 1, "carseller" }, { 2, "player" }, { 3, "carrenter" }, { 4, "stolencar" }, { 5, "garage" }, { 6, "living" },
            { 7, "realtor" }, { 8, "photographer" }, { 9, "diary" }, { 10, "dressing" }, { 17, "npc" }, { 20, "tutor" }, { 22, "goodies" },
            { 49, "game_progress" }, { 50, "bar" }, { 51, "office" }, { 52, "photos" }, { 54, "club" }, { 56, "jacuzzi" },
        };

        TDU2FirstPerson _fp;
        TDU2StoryScene _scene;
        float _cooldown;
        public int Selected { get; private set; } = -1;
        public Func<int, bool> Filter;

        public bool Active => _fp != null && _fp.enabled;

        public bool Start(TDU2StoryScene scene, string cameraName, float fovDeg)
        {
            _scene = scene;
            if (!scene.EntityCamera(cameraName, out var eye, out var fwd)) { DebugManager.Warning("[TDU2] Laufmodus: Kamera " + cameraName + " fehlt"); eye = scene.Holder.position + Vector3.up * 1.6f; fwd = scene.Holder.forward; }
            // Boden unter der Startkamera (10 m), Auge 1,6 m darüber
            if (UnityEngine.Physics.Raycast(eye + Vector3.up * 0.1f, Vector3.down, out var hit, 10f)) eye.y = hit.point.y + 1.6f;
            if (_fp == null)
            {
                var go = new GameObject("TDU2 Story Walk");
                _fp = go.AddComponent<TDU2FirstPerson>();
                _fp.Cam = Camera.main;
                _fp.EyeHeight = 1.6f;
            }
            _fp.FieldOfView = fovDeg;
            _fp.enabled = true;
            _fp.LookEnabled = true;
            _fp.Place(eye, fwd);
            scene.CameraOverride = true;
            Selected = -1;
            return true;
        }

        public void Resume() { if (_fp != null) { _fp.enabled = true; _fp.LookEnabled = true; } if (_scene != null) _scene.CameraOverride = true; }
        public void Pause() { if (_fp != null) _fp.enabled = false; if (_scene != null) _scene.CameraOverride = false; }

        public void Stop()
        {
            if (_fp != null) UnityEngine.Object.Destroy(_fp.gameObject);
            _fp = null;
            if (_scene != null) _scene.CameraOverride = false;
        }

        public Vector3 EyeWorld => _fp != null && _fp.Cam != null ? _fp.Cam.transform.position : Vector3.zero;
        public Vector3 ForwardWorld => _fp != null && _fp.Cam != null ? _fp.Cam.transform.forward : Vector3.forward;
        public Vector3 EyeLocal => _scene.ToLocal(EyeWorld);

        public string TypeOf(int i)
        {
            var sel = _scene.Selectables;
            if (i < 0 || i >= sel.Count) return "";
            var v = sel[i];
            if (v.SelectableType == 57) return v.SelectableString;
            return TypeNames.TryGetValue(v.SelectableType, out var n) ? n : (string.IsNullOrEmpty(v.SelectableString) ? "" : v.SelectableString);
        }

        public string SelectedType => TypeOf(Selected);

        public void Clear()
        {
            if (Selected >= 0) _scene.Ui?.Call("/", "ItemType", "out");
            Selected = -1;
        }

        public void Update(float dt)
        {
            if (!Active || _scene == null) return;
            _cooldown -= dt;
            var o = EyeLocal; var d = _scene.ToLocalDir(ForwardWorld).normalized;
            var sel = _scene.Selectables;
            int best = -1; float bestD2 = SelectRadius * SelectRadius;
            for (int i = 0; i < sel.Count; i++)
            {
                if (Filter != null && !Filter(i)) continue;
                var v = sel[i];
                var to = v.Center - o;
                float d2 = to.sqrMagnitude;
                if (Vector3.Dot(to, d) <= 0f || d2 >= bestD2) continue;
                if (!v.Raycast(o, d, SelectRadius * 2f, out _, out float tExit)) continue;
                // verdeckt? (Szenen-Collider bis zur Rückseite des Volumens)
                if (UnityEngine.Physics.Raycast(EyeWorld, ForwardWorld, out var hit, tExit) && !v.Contains(_scene.ToLocal(hit.point))) continue;
                best = i; bestD2 = d2;
            }
            if (best == Selected || (_cooldown > 0f && best != -1)) return;
            var ui = _scene.Ui;
            if (Selected >= 0) ui?.Call("/", "ItemType", "out");
            Selected = best;
            _cooldown = Cooldown;
            if (best >= 0 && ui != null)
            {
                ui.SetVariable("/:SelItemType", TypeOf(best));
                ui.Call("/", "ItemType", "over");
            }
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 0 "Avatar choice / First WalkMode" (GAME_MODE_AVATAR_CHOICE, OpenTDU2 GMAvatarChoice): Party im Haus
    /// EPH_L3DI_5067I (new_intro.bnk). C_INTRO ("The Dream", Credits), Auswahl "Select your character" (C_AV_xx über der
    /// Party C_AVATAR in Zeitlupe, Wechsel C_xTOy, Tasten links/rechts/Enter an intro.vmf), C_FIESTA_INTRO_xx, Laufmodus
    /// bis zu Tess (Volumen "Tess" -> C_TESS) und zum Garagentor (FSCommand "gain" -> Schritt 1).
    /// </summary>
    sealed class AvatarChoiceMode : StoryMode
    {
        enum St { Loading, Intro, Choice, Fiesta, Walk, Tess, Leaving }
        St _st;
        TDU2StoryScene _scene;
        readonly StoryWalk _walk = new StoryWalk();
        readonly Queue<string> _cmds = new Queue<string>();
        int _index = -1, _selected = -1;
        bool _tessStarted;
        float _skipTimer;
        float _loadT;

        static readonly string[] Configs = { "AVT_Male_Intro", "AVT_Female_Intro", "AVT_Male_Afrik_Intro", "AVT_Female_Afrik_Intro", "AVT_Male_Asia_Intro", "AVT_Female_Asia_Intro" };

        public override void Enter()
        {
            Suspend();
            TDU2GameMode.Push(Gamemode.GAME_MODE_AVATAR_CHOICE);
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = "scenarics/cinematics/intro/new_intro.bnk", Spot = "EPH_L3DI_5067I", HideSpotInterior = true });
            _scene.Command += (c, a) => _cmds.Enqueue(c);
            _st = St.Loading; _loadT = 0f;
            // Streaming beginnt am Haus
            var s = Story.FindSpot("EPH_L3DI_5067I");
            if (s != null) Story.HoldCameraAt(Manager.Streamer.MapRoot.TransformPoint(s.Position + Vector3.up * 2f), Vector3.forward);
        }

        public override void Tick(float dt)
        {
            if (_scene == null) return;
            if (_scene.Failed) { Story.Abort(); return; }
            if (_st == St.Loading)
            {
                _loadT += dt;
                _scene.SetFade(1f, Color.black);
                if (!_scene.Loaded) return;
                // Welt um das Haus fertig (sonst sieht man das Nachladen), spätestens nach 20 s
                if (!Manager.Streamer.ReadyAround(_scene.Holder.position, 300f) && _loadT < 20f) return;
                _scene.SetFade(0f, Color.black);
                _scene.PlayMain("C_INTRO");
                _st = St.Intro; _skipTimer = 0f;
                return;
            }
            var ui = _scene.Ui;
            if (_st == St.Choice && ui != null)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) ui.SendKey(1);
                if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) ui.SendKey(2);
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space)) ui.SendKey(13);
#endif
            }
            // C_INTRO überspringen: ab 25 s (FUN_00da7e70)
            if (_scene.MainName == "C_INTRO" && _skipTimer >= 0f)
            {
                _skipTimer = _scene.MainTime;
                if (_skipTimer >= 25f && Story.AllowSkip && TDU2Story.SkipPressed()) { _scene.SkipMain(); _skipTimer = -1f; }
            }
            foreach (var n in _scene.TakeEndEvents()) OnEnd(n);
            while (_cmds.Count > 0) OnCommand(_cmds.Dequeue());
            if (_index >= 0 && ui != null && _scene.MainName.Length > 0)
                ui.SetValue("/:IsTransition", _scene.MainName.IndexOf("C_AV_", StringComparison.Ordinal) >= 0 ? 0 : 1);
            if (_st == St.Walk) UpdateWalk(dt);
        }

        void OnEnd(string name)
        {
            var ui = _scene.Ui;
            if (name == "C_INTRO")
            {
                _scene.PlayMain("C_AV_01");
                _index = 0;
                _scene.PlayBackground("C_AVATAR");
                ui?.GotoLabel("/", "avatar", true);
                _st = St.Choice;
                return;
            }
            if (name.StartsWith("C_FIESTA_INTRO", StringComparison.Ordinal)) { StartWalk(); return; }
            if (name == "C_TESS")
            {
                ui?.SetVisible("/cursor", true);
                _scene.StopMain();
                _walk.Resume();
                _scene.ShowGoal(_scene.Loc(5108));   // "In die Garage gehen"
                _st = St.Walk;
                return;
            }
            if (_index >= 0 && name.Length > 5 && name.StartsWith("C_", StringComparison.Ordinal) && name.Contains("TO"))
            {
                // C_xTOy -> Schleife C_AV_0y
                var parts = name.Substring(2).Split(new[] { "TO" }, StringSplitOptions.None);
                if (parts.Length == 2 && int.TryParse(parts[0], out int from) && int.TryParse(parts[1], out int to) && from == _index + 1)
                {
                    _index = to - 1;
                    _scene.PlayMain("C_AV_" + to.ToString("00"));
                }
            }
        }

        void OnCommand(string c)
        {
            switch (c)
            {
                case "nxav":
                    if (_index < 0 || _index > 4) return;
                    _scene.PlayMain($"C_{_index + 1}TO{(_index + 1) % 6 + 1}");
                    break;
                case "prav":
                    if (_index < 1) return;
                    _scene.PlayMain($"C_{_index + 1}TO{_index}");
                    break;
                case "crav":
                    _scene.PlayEmitter("S_TimeStart");
                    _selected = _index;
                    Validate();
                    _index = -1;
                    break;
                case "skip": _skipTimer = -1f; break;
                case "gain":
                    // Garage gewählt -> Schritt 1 "First Garage"
                    _st = St.Leaving;
                    Story.Go(1);
                    break;
            }
        }

        void Validate()
        {
            _scene.StopBackground();
            _scene.PlayBackground("C_WALKMODE");
            var ui = _scene.Ui;
            string sex = ui?.GetVariable("/:WhatSex") ?? "male";
            string eth = ui?.GetVariable("/:WhatEthnicGrp") ?? "caucasian";
            bool woman = sex.StartsWith("f", StringComparison.OrdinalIgnoreCase);
            string suffix = eth.Length > 1 && eth[1] == 'a' ? "" : eth.Length > 1 && eth[1] == 's' ? "_Asia" : "_Afrik";
            string cfg = "AVT_" + (woman ? "Female" : "Male") + suffix + "_Intro";
            if (Array.IndexOf(Configs, cfg) < 0) cfg = Configs[Mathf.Clamp(_selected, 0, 5)];
            DebugManager.Info($"[TDU2] Avatar {_selected + 1} gewählt ({sex}, {eth}) -> {cfg}");
            _scene.SetPlayerAvatar(cfg);
            TDU2StoryProgress.SetIntroAvatar(Story.Nick, cfg);
            var pc = AvatarLibrary.PnjConfig(cfg);
            if (pc != null) AvatarConfigs.SetLocal(pc);
            ui?.SetVariable("/:PlayerName", Online.TDU2Session.Nick ?? "");
            _scene.PlayMain($"C_FIESTA_INTRO_{_selected + 1:00}");
            _st = St.Fiesta;
        }

        void StartWalk()
        {
            _scene.StopMain();
            _walk.Start(_scene, $"cam_entree_{Mathf.Max(_selected, 0) + 1:00}", 60f);
            _scene.Ui?.GotoLabel("/", "wm_home", true);
            _scene.ShowGoal(_scene.Loc(5033));   // "Mit Tess reden"
            _st = St.Walk;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        void UpdateWalk(float dt)
        {
            _walk.Update(dt);
            if (!_tessStarted)
            {
                var tess = _scene.FindVolume("Tess");
                if (tess != null && tess.Contains(_walk.EyeLocal - Vector3.up * 0.8f))
                {
                    _scene.CloseGoal();
                    _walk.Clear();
                    _walk.Pause();
                    _scene.PlayMain("C_TESS");
                    _tessStarted = true;
                    _scene.Ui?.SetVisible("/cursor", false);
                    _st = St.Tess;
                    return;
                }
            }
            if (TDU2Story.ValidatePressed() && _walk.Selected >= 0)
            {
                var ui = _scene.Ui;
                if (ui == null || !ui.Call("/cursor", "A")) ui?.Call("/", "ItemType", "valid");
            }
        }

        public override void Exit()
        {
            _walk.Stop();
            _scene?.Dispose(); _scene = null;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 1 "First Garage" (GAME_MODE_FIRST_WM, OpenTDU2 GMFirstWalkMode): Garage (garage.bnk, geschlossener Raum)
    /// mit Tess (C_LIFE_TESS) und dem California (Auto 387) an D_CAR. C_INTRO, Laufmodus, Fahrertür mit der Zubehör-Taste
    /// öffnen (FSCommand opdo / Taste), Auto wählen -> C_ENTER_CAR, Cockpit, Gas geben -> C_OUT, C_OUTRO -> Schritt 2.
    /// </summary>
    sealed class FirstGarageMode : StoryMode
    {
        enum St { Loading, Intro, Walk, EnterCar, Seated, Out, Outro, Done }
        St _st;
        TDU2StoryScene _scene;
        readonly StoryWalk _walk = new StoryWalk();
        readonly Queue<string> _cmds = new Queue<string>();
        GameObject _carGo; CarModel _model; TDU2Accessories _acc;
        bool _carRequested;
        int _carSelectable = -1;
        float _doorT = -1f;      // seit dem Öffnen der Tür (s), -1 = zu
        const float DoorDuration = 2f;
        bool _entered;
        float _revTimer;
        float _tutoT; int _tuto = -1;
        float _loadT;
        // Kommentare von Tess, solange kein Kino läuft (FUN_009a9990 -> FUN_009a9920): 3 im Auto, 1/2 mit Ziel (2 = Tür offen), sonst 0
        static readonly string[] Voices = { "C_VOICE_03", "C_VOICE_04", "C_VOICE_05", "C_VOICE_06" };
        readonly bool[] _voiceDone = new bool[4];
        bool _voicePlaying;

        bool DoorOpen => _doorT >= DoorDuration;

        public override void Enter()
        {
            Suspend();
            TDU2GameMode.Push(Gamemode.GAME_MODE_FIRST_WM);
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = "scenarics/cinematics/intro/garage.bnk", Spot = "EPH_L3DI_5067I", Closed = true });
            _scene.Command += (c, a) => _cmds.Enqueue(c);
            _st = St.Loading;
        }

        public override void Tick(float dt)
        {
            if (_scene == null) return;
            if (_scene.Failed) { Story.Abort(); return; }
            if (_st == St.Loading)
            {
                _loadT += dt;
                _scene.SetFade(1f, Color.black);
                if (!_scene.Loaded) return;
                if (!_carRequested)
                {
                    _carRequested = true;
                    _scene.SetPlayerAvatar(PlayerAvatarConfig());
                    for (int i = 0; i < _scene.Selectables.Count; i++) if (_scene.Selectables[i].SelectableString == "car") _carSelectable = i;
                    _walk.Filter = i => i != _carSelectable || DoorOpen;
                    Story.LoadDisplayCar(Story.StoryCar, (go, model) =>
                    {
                        _carGo = go; _model = model;
                        if (go != null) { _scene.SetCar(go.transform, "D_CAR", "CIN_CAR"); _acc = TDU2Accessories.For(model, null); }
                    });
                }
                if (_carGo == null && _loadT < 15f) return;
                _scene.SetFade(0f, Color.black);
                _scene.PlayBackground("C_LIFE_SOUND", true);
                _scene.PlayMain("C_INTRO");
                _st = St.Intro;
                return;
            }
            if (_st == St.Intro && Story.AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
            foreach (var n in _scene.TakeEndEvents()) OnEnd(n);
            while (_cmds.Count > 0) OnCommand(_cmds.Dequeue());
            if (_doorT >= 0f) _doorT += dt;
            _acc?.Update(dt);
            if (_st == St.Walk) UpdateWalk(dt);
            else if (_st == St.Seated) UpdateSeated(dt);
            UpdateTutorials(dt);
        }

        void OnEnd(string name)
        {
            switch (name)
            {
                case "C_INTRO":
                    _scene.StopMain();
                    _scene.PlayBackground("C_LIFE_TESS", true);
                    _walk.Start(_scene, "cam_entree", 50f);
                    _scene.Ui?.GotoLabel("/", "wm_garage", true);
                    _scene.ShowGoal(_scene.Loc(5032));   // "Steig in das Auto" (TUTO_ENTER_IN_THE _CAR)
                    _st = St.Walk;
                    Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
                    break;
                case "C_ENTER_CAR":
                    _scene.StopMain();
                    _acc?.SetDoor(true, false);
                    _doorT = -1f;
                    _entered = true;
                    _st = St.Seated;
                    break;
                case "C_OUT":
                    _scene.PlayMain("C_OUTRO");
                    _st = St.Outro;
                    break;
                case "C_OUTRO":
                    _st = St.Done;
                    Story.Go(2);
                    break;
                default:
                    for (int i = 0; i < Voices.Length; i++)
                        if (name == Voices[i]) { _voiceDone[i] = true; _voicePlaying = false; }
                    break;
            }
        }

        void UpdateVoices()
        {
            int index = 0;
            if (_entered) index = 3;
            else if (_walk.Selected >= 0 || AimingAtCar()) index = _doorT >= 0f ? 2 : 1;
            if (!_voiceDone[index] && !_voicePlaying && _scene.PlayBackground(Voices[index], false)) _voicePlaying = true;
        }

        void OnCommand(string c)
        {
            if (c == "opdo" && !_entered) OpenDoor();
        }

        void OpenDoor()
        {
            if (_doorT >= 0f) return;
            _acc?.SetDoor(true, true);
            _doorT = 0f;
        }

        bool AimingAtCar()
        {
            if (_carGo == null) return false;
            var ray = new Ray(_walk.EyeWorld, _walk.ForwardWorld);
            foreach (var r in _carGo.GetComponentsInChildren<Renderer>())
                if (r.enabled && r.bounds.size.y < 4f && r.bounds.IntersectRay(ray, out float d) && d < 50f) return true;
            return false;
        }

        static bool AccessoryLeftPressed()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Keypad4) || Input.GetKeyDown(KeyCode.Alpha4)) return true;
            var a = TDU2Controls.Get("doors");
            return a != null && ((a.Key1 != KeyCode.None && Input.GetKeyDown(a.Key1)) || (a.Key2 != KeyCode.None && Input.GetKeyDown(a.Key2)));
#else
            return false;
#endif
        }

        void UpdateWalk(float dt)
        {
            _walk.Update(dt);
            UpdateVoices();
            bool aiming = AimingAtCar();
            if (aiming && _doorT < 0f && AccessoryLeftPressed()) OpenDoor();
            if (!_entered && _walk.SelectedType == "car" && DoorOpen && TDU2Story.ValidatePressed())
            {
                _scene.Ui?.SetVisible("/cursor", false);
                _walk.Clear();
                _scene.CloseGoal();
                _walk.Stop();
                _scene.PlayMain("C_ENTER_CAR");
                _st = St.EnterCar;
            }
        }

        void UpdateSeated(float dt)
        {
            // Cockpit-Kamera: über dem Fahrersitz, Blick nach vorne (camerasingame.xmb Modus 23 ~ (0.37, 1.01, -0.36) -> (0.37, 0.9, 1.35))
            var cam = Camera.main;
            if (cam != null && _model != null && _carGo != null)
            {
                var seat = _model.Find("DVRSEAT_HR") ?? _model.Find("DVRSEAT_LR");
                var car = _carGo.transform;
                Vector3 eye = seat != null ? seat.position + car.up * 0.75f : car.TransformPoint(new Vector3(-0.37f, 1.01f, -0.36f));
                var fwd = (car.forward * 1.71f - car.up * 0.11f).normalized;
                _scene.CameraOverride = true;
                cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(fwd, car.up));
                cam.fieldOfView = 55f;
            }
            UpdateVoices();
            // Gas > 0.7 -> 3 s, Entity__Fx C_OUTRO (Motor) wird gestartet
            if (_revTimer == 0f && TDU2Story.ActionHeld("accel")) { _revTimer = 3f; _scene.PlayEmitter("C_OUTRO"); }
            if (_revTimer > 0f)
            {
                _revTimer -= dt;
                if (_revTimer <= 0f)
                {
                    _revTimer = 0f;
                    _scene.CameraOverride = false;
                    _scene.PlayMain("C_OUT");
                    _st = St.Out;
                }
            }
        }

        // DBTutoMess "opdo" (5036, nach 2 s, bis die Tür aufgeht) und "stca" (5035, im Auto bis Gas gegeben wird)
        void UpdateTutorials(float dt)
        {
            int want = -1;
            if (_st == St.Walk && !_entered && _doorT < 0f && _scene.MainName.Length == 0) want = 0;
            else if (_st == St.Seated && _revTimer == 0f) want = 1;
            if (_tuto >= 0 && want != _tuto) { _scene.CloseTutorial(); _tuto = -1; _tutoT = 0f; }
            if (want >= 0 && _tuto < 0)
            {
                _tutoT += dt;
                if (_tutoT > 2f) { _scene.ShowTutorial(_scene.Loc(want == 0 ? 5036 : 5035)); _tuto = want; }
            }
            else if (want < 0) _tutoT = 0f;
        }

        public override void Exit()
        {
            _walk.Stop();
            if (_carGo != null) UnityEngine.Object.Destroy(_carGo);
            _model?.Destroy();
            _scene?.Dispose(); _scene = null;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 2 "First Freeride": Ausfahrt-Kino C_FIRST_OUTRO am Haus EPH_L3DI_5067I (islands/ibiza/exterior/eph_l3di.bnk,
    /// der California PLAYER_CAR fährt aus der Garage), dann freie Fahrt; nach 90 s (Story-Timer) C_FADE_FREERIDE
    /// (Weißblende, fade_freeride.bnk) -> Schritt 3 "Meeting Tess".
    /// </summary>
    sealed class FirstFreerideMode : StoryMode
    {
        enum St { Loading, Outro, Driving, Fade }
        St _st;
        TDU2StoryScene _scene;
        GameObject _carGo; CarModel _model;
        float _timer = 90f, _fadeT, _loadT;
        bool _carRequested, _spawned;

        public override bool BlocksInput => _st != St.Driving;

        public override void Enter()
        {
            TDU2GameMode.Push(Gamemode.GAME_MODE_CINE);
            if (Manager.Current != null)
            {
                // Spielstand mit Auto (Fortsetzen): ohne Ausfahrt-Kino direkt fahren
                _spawned = true; _st = St.Driving;
                TDU2GameMode.Push(Gamemode.GAME_MODE_MISSION);   // Story-Schritt, keine freie Fahrt (keine Mitspieler)
                return;
            }
            Suspend();
            string island = Manager.Streamer?.Index?.IslandName ?? "ibiza";
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = "islands/" + island + "/exterior/eph_l3di.bnk", Spot = "EPH_L3DI_5067I", LoadSets = false, IntroUi = false });
            _st = St.Loading;
            var s = Story.FindSpot("EPH_L3DI_5067I");
            if (s != null) Story.HoldCameraAt(Manager.Streamer.MapRoot.TransformPoint(s.Position + new Vector3(0f, 6f, 0f)), Vector3.forward);
        }

        public override void Tick(float dt)
        {
            switch (_st)
            {
                case St.Loading:
                    _loadT += dt;
                    if (_scene == null || _scene.Failed) { SpawnFallback(); return; }
                    _scene.SetFade(1f, Color.black);
                    if (!_scene.Loaded) return;
                    if (!_carRequested)
                    {
                        _carRequested = true;
                        Story.LoadDisplayCar(Story.StoryCar, (go, m) => { _carGo = go; _model = m; if (go != null) _scene.SetCar(go.transform, null, "PLAYER_CAR"); });
                    }
                    if ((_carGo == null || !Manager.Streamer.ReadyAround(_scene.Holder.position, 300f)) && _loadT < 20f) return;
                    if (!_scene.PlayMain("C_FIRST_OUTRO")) { SpawnFallback(); return; }
                    _scene.SetFade(0f, Color.black);
                    _st = St.Outro;
                    break;
                case St.Outro:
                    if (Story.AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
                    foreach (var n in _scene.TakeEndEvents())
                        if (n == "C_FIRST_OUTRO") SpawnFromCinematic();
                    break;
                case St.Driving:
                    // Story-Timer (DAT_00ede78c: 90 s), nur außerhalb von Interieurs/Menüs
                    if (Manager.Current != null && !TDU2BigMap.IsOpen) _timer -= dt;
                    if (_timer <= 0f) { _st = St.Fade; _fadeT = 0f; TDU2GameMode.Push(Gamemode.GAME_MODE_CINE); }
                    break;
                case St.Fade:
                    // C_FADE_FREERIDE: Weißblende (fade_blanc), dann Meeting Tess
                    _fadeT += dt;
                    FadeOverlay.Set(Mathf.Clamp01(_fadeT / 2f), Color.white);
                    if (_fadeT > 2.5f) { Story.Go(3); }
                    break;
            }
        }

        void SpawnFromCinematic()
        {
            var car = Story.StoryCar;
            Vector3 p; Quaternion q;
            if (_carGo != null) { p = _carGo.transform.position; var f = _carGo.transform.forward; f.y = 0f; q = Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward, Vector3.up); }
            else { SpawnFallback(); return; }
            CleanupScene();
            Story.RequestSpawn(car, p + Vector3.up * 0.3f, q);
            AfterSpawn();
        }

        void SpawnFallback()
        {
            CleanupScene();
            Story.SpawnAtSpot(Story.StoryCar, "EPH_L3DI_5067I");
            AfterSpawn();
        }

        void AfterSpawn()
        {
            _spawned = true;
            _st = St.Driving; _timer = 90f;
            TDU2GameMode.Push(Gamemode.GAME_MODE_MISSION);   // Story-Schritt, keine freie Fahrt (keine Mitspieler)
            DebugManager.Info("[TDU2] Geschichte: erste freie Fahrt (" + _timer + " s bis Meeting Tess)");
        }

        void CleanupScene()
        {
            if (_carGo != null) UnityEngine.Object.Destroy(_carGo);
            _model?.Destroy(); _model = null; _carGo = null;
            _scene?.Dispose(); _scene = null;
        }

        public override void Exit() { CleanupScene(); }
    }

    /// <summary>Vollbild-Blende für Übergänge zwischen Story-Schritten (Quad vor der Kamera, keine IMGUI).</summary>
    public static class FadeOverlay
    {
        static GameObject _q; static Material _m;
        public static void Set(float a, Color c)
        {
            var cam = Camera.main;
            if (a <= 0.001f || cam == null) { if (_q != null) _q.SetActive(false); return; }
            if (_q == null)
            {
                _q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _q.name = "TDU2 Story Blende";
                var col = _q.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                _m = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent")) { renderQueue = 5000 };
                _q.GetComponent<Renderer>().sharedMaterial = _m;
                UnityEngine.Object.DontDestroyOnLoad(_q);
            }
            _q.SetActive(true);
            var t = cam.transform; float d = cam.nearClipPlane + 0.01f;
            _q.transform.SetPositionAndRotation(t.position + t.forward * d, t.rotation);
            float h = 2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
            _q.transform.localScale = new Vector3(h * cam.aspect, h, 1f);
            c.a = a; _m.color = c;
        }
        public static void Clear() => Set(0f, Color.black);
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 3 "Meeting Tess" (Original: Spielmodus CINE, Kino-Bank scenarics/cinematics/intro/meeting_tess am Spot
    /// E_HOTELA_1266I): C_MEETING_TESS (Mann) bzw. C_MEETING_TESS_W (Frau) mit Tess, Avatar und dem California (PLAYER_CAR).
    /// Danach steht das Auto an D_CAR -> Schritt 4 "Driving Tess".
    /// </summary>
    sealed class MeetingTessMode : StoryMode
    {
        TDU2StoryScene _scene;
        GameObject _carGo; CarModel _model;
        bool _carRequested, _started, _grounded;
        float _loadT;
        string _cine;

        public override void Enter()
        {
            Suspend();
            TDU2GameMode.Push(Gamemode.GAME_MODE_CINE);
            _cine = PlayerIsWoman ? "C_MEETING_TESS_W" : "C_MEETING_TESS";
            // Rahmen = Exterior des Hotels (Spot-Position/Richtung); den Ursprung der .sce gleicht TDU2StoryScene aus
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = "scenarics/cinematics/intro/meeting_tess.bnk", Spot = "E_HOTELA_1266I" });
            var s = Story.FindSpot("E_HOTELA_1266I");
            if (s != null) Story.HoldCameraAt(Manager.Streamer.MapRoot.TransformPoint(s.Position2 + Vector3.up * 3f), Vector3.forward);
        }

        public override void Tick(float dt)
        {
            if (_scene == null || _scene.Failed) { Next(); return; }
            if (!_started)
            {
                _loadT += dt;
                FadeOverlay.Set(1f, Color.white);
                if (!_scene.Loaded) return;
                if (!_carRequested)
                {
                    _carRequested = true;
                    _scene.SetPlayerAvatar(PlayerAvatarConfig());
                    Story.LoadDisplayCar(Story.StoryCar, (go, m) => { _carGo = go; _model = m; if (go != null) _scene.SetCar(go.transform, "D_CAR", "PLAYER_CAR"); });
                }
                if ((_carGo == null || !Manager.Streamer.ReadyAround(_scene.Holder.position, 300f)) && _loadT < 20f) return;
                _grounded = true;   // Rahmen = Hotel-Exterior: Vorplatz liegt auf dessen Boden (0,07 m), keine Höhenkorrektur nötig
                FadeOverlay.Clear();
                if (!_scene.PlayMain(_cine)) { Next(); return; }
                _started = true;
                return;
            }
            if (Story.AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
            foreach (var n in _scene.TakeEndEvents()) if (n == _cine) { Next(); return; }
        }

        void Next()
        {
            // Auto an den Start der Mission (tesseven.ig2, erster Checkpoint: Straße vor dem Hotel, Fahrtrichtung), sonst an den
            // Auto-Punkt des Spots. (D_CAR ist ein Entity__PosCam – ein Kamerapunkt, keine Auto-Platzierung.)
            Cleanup();
            if (!TDU2Story.SpawnAtMissionStart(Story, "tesseven.ig2")) Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I");
            FadeOverlay.Clear();
            TDU2GameMode.Pop();
            Story.Go(4);
        }

        void Cleanup()
        {
            if (_carGo != null) UnityEngine.Object.Destroy(_carGo);
            _model?.Destroy(); _model = null; _carGo = null;
            _scene?.Dispose(); _scene = null;
        }

        public override void Exit() { Cleanup(); FadeOverlay.Clear(); }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 4 "Driving Tess" (Original: Spielmodus MISSION, DBEvent TESSEVEN, ig2/tesseven.ig2 "Der Weg zur Solar Crown",
    /// scenarics/packs/pack_events13.bnk): Tess sitzt auf dem Beifahrersitz, Tipp 4032 "Tess zum Drehort fahren.", Ziel =
    /// zweiter Checkpoint der ig2 (Solar Crown, Club ECH_IA_1243I). Am Ziel stehen die LKW/der Teppich des Drehorts
    /// (C_TRUCK = M_SC_LIFE); Ankunft (langsam im Zielradius) -> Schritt 5.
    /// </summary>
    sealed class DrivingTessMode : StoryMode
    {
        public override bool BlocksInput => false;
        public override bool BlocksMap => true;
        public override bool MutesRadio => false;
        // Zeitlimit der Mission (tesseven.ig2: "ChCD" = 1 Countdown, "timG" = 160000 ms), Anzeige über das Original-HUD
        // (hud.vmf /HUD/time_cd mit /:MinCd /:SecCd /:CentCd, letzte 5 s /HUD/time_out "on" + /:time_out)
        float _timeLimit, _timeLeft = -1f;
        int _lastTimeOut = -1;
        bool _hudShown;
        TDU2CarAvatar _tess;
        TDU2Vehicle _veh;
        Vector3 _targetGame;
        bool _hasTarget, _told;
        TDU2StoryScene _decor;
        float _waitCar = 30f, _arriveT;
        // Ziel-Tor wie im Spiel: Checkpoint-Actor "Finish_line" aus pack_cp.bnk (dieselben Modelle wie bei Rennen) am letzten
        // Checkpoint der tesseven.ig2 – das Original zeigt das Ziel der Mission so in der Welt (dazu GPS-Route und Minikarte)
        TDU2.Formats.Ige2Checkpoint _finishCp;
        int _finishIndex;
        TDU2.Racing.RaceProps _gate;
        bool _gateBuilt;
        float _gateWait;

        public override void Enter()
        {
            TDU2GameMode.Push(Gamemode.GAME_MODE_MISSION);
            if (Manager.Current == null && !Story.SpawnPending) Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I");
            // Ziel: letzter Checkpoint der Mission (tesseven.ig2), sonst der Club-Spot der Solar Crown
            try
            {
                var path = CarDatabase.FindFile(System.IO.Path.Combine(Manager.BnkDir, "ig2"), "tesseven.ig2");
                if (path != null)
                {
                    var ig = TDU2.Formats.Ige2File.Parse(System.IO.File.ReadAllBytes(path));
                    if (ig != null && ig.Checkpoints.Count > 0)
                    {
                        _finishIndex = ig.Checkpoints.Count - 1;
                        _finishCp = ig.Checkpoints[_finishIndex];
                        _targetGame = _finishCp.Position; _hasTarget = true;
                    }
                    if (ig != null && ig.Int("ChCD") == 1 && ig.Int("timG") > 0) _timeLimit = ig.Int("timG") / 1000f;
                }
            }
            catch (Exception ex) { DebugManager.Warning("[TDU2] tesseven.ig2: " + ex.Message); }
            if (!_hasTarget) { var s = Story.FindSpot("ECH_IA_1243I"); if (s != null) { _targetGame = s.Position2.sqrMagnitude > 1f ? s.Position2 : s.Position; _hasTarget = true; } }
            TrySetGps();
            // Drehort-Dekor (LKW, Teppich) am Club
            _decor = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = "scenarics/packs/pack_events13.bnk", Spot = "ECH_IA_1243I", IntroUi = false });
        }

        public override void Tick(float dt)
        {
            if (_decor != null && _decor.Loaded && !_decor.IsBackgroundPlaying("C_TRUCK")) _decor.PlayBackground("C_TRUCK", true);
            var veh = Manager.Current;
            if (veh == null)
            {
                _waitCar -= dt;
                if (_waitCar < 0f && !Story.SpawnPending) { Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I"); _waitCar = 30f; }
                return;
            }
            if (veh != _veh)
            {
                _veh = veh;
                if (_tess != null) UnityEngine.Object.Destroy(_tess);
                var cfg = AvatarLibrary.PnjConfig("STO_Tess_Wintory");
                if (cfg != null && Manager.Model != null) _tess = TDU2CarAvatar.Attach(veh.gameObject, Manager.Model, cfg, true, () => veh.Steer);
            }
            if (!_told && _decor != null && _decor.Loaded && !Story.SpawnPending)
            {
                _told = true;
                _decor.ShowTutorial(_decor.Loc(4032));   // "Tess zum Drehort fahren."
                _decor.PlayFx("EVTE_to_player_01");       // Tess im Auto (pack_events13__<sprache>.bnk)
                if (_timeLimit > 0f) StartTimer();
            }
            if (_timeLeft >= 0f && UpdateTimer(dt)) return;
            if (!_hasTarget) return;
            TrySetGps();
            var root = Manager.Streamer.MapRoot;
            var pos = root.InverseTransformPoint(veh.transform.position);
            float dist = new Vector2(pos.x - _targetGame.x, pos.z - _targetGame.z).magnitude;
            TickGate(dt, dist);
            // Durch das Ziel-Tor gefahren (wie bei Rennen: Durchfahrt zählt, egal wie schnell) -> Zwischensequenz
            if (_finishCp != null && dist < Mathf.Max(12f, _finishCp.Width * 0.75f))
            {
                TDU2Diag.Log("STORY", $"Driving Tess: Ziel-Tor durchfahren ({dist:0.0} m, {veh.SpeedKmh:0} km/h, Restzeit {_timeLeft:0.0} s)");
                Story.Go(5);
                return;
            }
            if (dist < 30f && veh.SpeedKmh < 15f)
            {
                _arriveT += dt;
                if (_arriveT > 1f) { DebugManager.Info("[TDU2] Geschichte: Tess am Drehort"); Story.Go(5); }
            }
            else _arriveT = 0f;
        }

        bool _gpsSet; float _gpsCheck;

        /// <summary>
        /// Ziel-Tor bauen, sobald das Auto näher als 600 m ist (dann ist der Boden am Ziel geladen und das Tor steht auf der
        /// Straße). Modelle aus worldactors/pack_cp (RaceProps.Preload lädt im Hintergrund).
        /// </summary>
        void TickGate(float dt, float dist)
        {
            if (_gateBuilt || _finishCp == null || dist > 600f) return;
            var st = Manager.Streamer;
            if (st == null || st.MapRoot == null) return;
            string bank = TDU2.Racing.RaceProps.WorldActorsBank(Manager.BnkDir, st.Index != null ? st.Index.IslandName : null);
            _gateWait += dt;
            if (!TDU2.Racing.RaceProps.Preload(bank) && _gateWait < 20f) return;
            _gateBuilt = true;
            try
            {
                var race = new TDU2.Racing.RaceDefinition { Name = "tesseven" };
                var cp = TDU2.Racing.RaceCheckpoint.From(_finishCp, Mathf.Max(1, _finishIndex));
                cp.IsFinish = true;
                race.Checkpoints.Add(cp);
                race.StartPosition = cp.Position + cp.Forward * -50f;   // kein Startplatz in der Nähe (ClearStart)
                _gate = new TDU2.Racing.RaceProps();
                var visuals = new List<TDU2.Racing.RaceGateVisual>();
                _gate.Build(st, race, TDU2.Racing.RaceGateVisual.SharedMaterial(), visuals);
                foreach (var v in visuals) if (v != null) v.SetState(TDU2.Racing.RaceGateState.Next);
                // nur das Original-Modell zeigen – kein Ersatz-Tor (keine eigenen Zusätze)
                bool model = visuals.Count > 0 && visuals[0] != null && visuals[0].IsModel;
                if (!model) { _gate.Clear(); _gate = null; }
                TDU2Diag.Log("STORY", $"Ziel-Tor Driving Tess: {visuals.Count} Tor(e) bei {TDU2Diag.F(cp.Position)} Breite {cp.Width:0.0} m, Modell={(visuals.Count > 0 && visuals[0] != null && visuals[0].IsModel)} Bank={bank ?? "-"}");
            }
            catch (Exception ex) { DebugManager.Warning("[TDU2] Ziel-Tor Driving Tess: " + ex.Message); }
        }

        static VmfPlayer Hud => UnityEngine.Object.FindFirstObjectByType<TDU2Minimap>()?.HudPlayer;

        void StartTimer()
        {
            _timeLeft = _timeLimit; _lastTimeOut = -1;
            TDU2Diag.Log("STORY", $"Driving Tess: Zeitlimit {_timeLimit:0} s gestartet, Ziel {TDU2Diag.F(_targetGame)}");
            var h = Hud;
            if (h == null) return;
            SetTime(h, _timeLeft);
            h.SetVisible("/HUD/time_cd", true);
            h.GotoLabel("/HUD/time_cd", "open", true);
            _hudShown = true;
        }

        void HideTimer()
        {
            var h = Hud;
            // Countdown und die roten Schluss-Sekunden ganz weg (sonst stand z.B. "4" noch in der nächsten Mission)
            if (h != null)
            {
                h.GotoLabel("/HUD/time_cd", "default", false); h.GotoLabel("/HUD/time_out", "default", false);
                h.SetVariable("/:time_out", "");
                h.SetVisible("/HUD/time_cd", false); h.SetVisible("/HUD/time_out", false);
            }
            _hudShown = false; _lastTimeOut = -1;
        }

        static void SetTime(VmfPlayer h, float seconds)
        {
            int ms = Mathf.Max(0, (int)(seconds * 1000f + 0.5f));
            h.SetVariable("/:MinCd", (ms / 60000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            h.SetVariable("/:SecCd", (ms / 1000 % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            h.SetVariable("/:CentCd", (ms % 1000 / 10).ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Restzeit herunterzählen; abgelaufen -> Tess beschwert sich, zurück zum Hotel, neuer Versuch. true = diesen Tick beendet.</summary>
        bool UpdateTimer(float dt)
        {
            if (Time.timeScale <= 0f) return false;
            _timeLeft -= dt;
            var h = Hud;
            if (h != null)
            {
                if (!_hudShown) { h.SetVisible("/HUD/time_cd", true); h.GotoLabel("/HUD/time_cd", "open", true); _hudShown = true; }
                SetTime(h, _timeLeft);
                int sec = Mathf.CeilToInt(_timeLeft);
                if (sec <= 5 && sec >= 1 && sec != _lastTimeOut)
                {
                    if (_lastTimeOut < 0) { h.SetVisible("/HUD/time_out", true); h.GotoLabel("/HUD/time_out", "on", true); }
                    _lastTimeOut = sec;
                    h.SetVariable("/:time_out", sec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            if (_timeLeft > 0f) return false;
            DebugManager.Info("[TDU2] Geschichte: Zeit abgelaufen – Tess zum Drehort, neuer Versuch");
            HideTimer();
            _decor?.PlayFx("EVTE_loose_01");
            _timeLeft = -1f; _told = false; _veh = null;
            Story.SpawnAtSpot(Story.StoryCar, "E_HOTELA_1266I");
            return true;
        }

        /// <summary>GPS-Ziel (Drehort) setzen, sobald das GPS da ist (entsteht erst mit der Minikarte); bleibt gesetzt, bis Tess ankommt.</summary>
        void TrySetGps()
        {
            if (!_hasTarget) return;
            var gps = TDU2Gps.Instance;
            if (gps == null) return;
            if (_gpsSet && Time.unscaledTime < _gpsCheck) return;
            _gpsCheck = Time.unscaledTime + 2f;
            if (!gps.HasTarget || !gps.IsTargetNear(_targetGame, 450f)) { gps.SetTarget(_targetGame, !_gpsSet); _gpsSet = true; }
        }

        public override void Exit()
        {
            if (_tess != null) UnityEngine.Object.Destroy(_tess);
            _tess = null;
            HideTimer();
            _gate?.Clear(); _gate = null; _gateBuilt = false;
            _decor?.CloseTutorial();
            _decor?.Dispose(); _decor = null;
            if (TDU2Gps.Instance != null && _hasTarget) TDU2Gps.Instance.ClearTarget(true);
            TDU2GameMode.Pop();
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 5 "Solar Crown exterior" (Spielmodus CINE, scenarics/cinematics/intro/solar_crown_ext am Club ECH_IA_1243I):
    /// C_SOLAR_CROWN_E (Ankunft am Drehort, Tess, Sicherheitsleute, Kamerateam, PLAYER_CAR). Danach freie Fahrt
    /// (Schritt 6 "Solar Crown interior" ist noch nicht umgesetzt).
    /// </summary>
    sealed class SolarCrownExteriorMode : StoryMode
    {
        TDU2StoryScene _scene;
        GameObject _carGo; CarModel _model;
        CarPhysics _car;
        bool _carRequested, _started;
        float _loadT;

        public override void Enter()
        {
            _car = Manager.Current?.Car ?? Story.StoryCar;
            Suspend();
            TDU2GameMode.Push(Gamemode.GAME_MODE_CINE);
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = "scenarics/cinematics/intro/solar_crown_ext.bnk", Spot = "ECH_IA_1243I" });
        }

        public override void Tick(float dt)
        {
            if (_scene == null || _scene.Failed) { Finish(); return; }
            if (!_started)
            {
                _loadT += dt;
                _scene.SetFade(1f, Color.black);
                if (!_scene.Loaded) return;
                if (!_carRequested)
                {
                    _carRequested = true;
                    _scene.SetPlayerAvatar(PlayerAvatarConfig(), AvatarConfigs.Local);
                    Story.LoadDisplayCar(_car, (go, m) => { _carGo = go; _model = m; if (go != null) _scene.SetCar(go.transform, null, "PLAYER_CAR"); });
                }
                if ((_carGo == null || !Manager.Streamer.ReadyAround(_scene.Holder.position, 300f)) && _loadT < 20f) return;
                _scene.SetFade(0f, Color.black);
                if (!_scene.PlayMain("C_SOLAR_CROWN_E")) { Finish(); return; }
                _started = true;
                return;
            }
            if (Story.AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
            foreach (var n in _scene.TakeEndEvents()) if (n == "C_SOLAR_CROWN_E") { Finish(); return; }
        }

        void Finish()
        {
                if (_carGo != null) UnityEngine.Object.Destroy(_carGo);
            _model?.Destroy(); _model = null; _carGo = null;
            _scene?.Dispose(); _scene = null;
            TDU2GameMode.Pop();
            // Tess nimmt den California zurück (Original: Auto 387 mit -1 entfernt) – nur, wenn die Geschichte ihn gegeben hat
            ReturnStoryCar();
            Story.Go(6);
        }

        void ReturnStoryCar()
        {
            var save = Online.TDU2Session.Save;
            var car = Story.StoryCar;
            if (save == null || car == null || !TDU2StoryProgress.GaveStoryCar(Story.Nick)) return;
            save.ownedCars.RemoveAll(c => string.Equals(c, car.FileName, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(save.currentCar, car.FileName, StringComparison.OrdinalIgnoreCase)) save.currentCar = save.ownedCars.Count > 0 ? save.ownedCars[0] : "";
            if (save.ownedCars.Count == 0) save.starterChosen = false;   // Startauto beim Gebrauchtwagenhändler (Schritt 7)
            TDU2StoryProgress.SetGaveStoryCar(Story.Nick, false);
            if (Online.TDU2Session.LoggedIn) Online.TDU2Session.SaveAsync();
            DebugManager.Info("[TDU2] Geschichte: Tess nimmt den California zurück");
        }

        public override void Exit()
        {
            if (_carGo != null) UnityEngine.Object.Destroy(_carGo);
            _model?.Destroy();
            _scene?.Dispose(); _scene = null;
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 6 "Solar Crown interior" (Spielmodus CINE, scenarics/cinematics/intro/solar_crown_int + interior/ich_ib.bnk am
    /// Club ECH_IA_1243I): C_SOLAR_CROWN_I (Drehort im Club, Tess, Todd, Kamerateam) -> Schritt 7.
    /// </summary>
    sealed class SolarCrownInteriorMode : StoryMode
    {
        TDU2StoryScene _scene;
        bool _started, _avatar;
        float _loadT;

        public override void Enter()
        {
            Suspend();
            TDU2GameMode.Push(Gamemode.GAME_MODE_CINE);
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup
            {
                Bank = "scenarics/cinematics/intro/solar_crown_int.bnk", Spot = "ECH_IA_1243I", Closed = true, ClosedAltitude = 0f,
                ExtraBanks = new[] { "interior/ich_ib.bnk" }, IntroUi = false,
            });
        }

        public override void Tick(float dt)
        {
            if (_scene == null || _scene.Failed) { Story.Go(7); return; }
            if (!_started)
            {
                _loadT += dt;
                _scene.SetFade(1f, Color.black);
                if (!_scene.Loaded) return;
                if (!_avatar) { _avatar = true; _scene.SetPlayerAvatar(PlayerAvatarConfig(), AvatarConfigs.Local); }
                if (_loadT < 1.5f) return;   // Figuren laden
                _scene.SetFade(0f, Color.black);
                if (!_scene.PlayMain("C_SOLAR_CROWN_I")) { Story.Go(7); return; }
                _started = true;
                return;
            }
            if (Story.AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
            foreach (var n in _scene.TakeEndEvents()) if (n == "C_SOLAR_CROWN_I") { Story.Go(7); return; }
        }

        public override void Exit()
        {
            _scene?.Dispose(); _scene = null;
            TDU2GameMode.Pop();
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 7 "Used Car Dealer" (Original: Spielmodus SHOP am Gebrauchtwagenhändler ESTOL_B, Tipp 4033 "Ein Auto kaufen!"
    /// TUTO_BUY_A_CAR): Auto-Auswahl beim Händler (Startautos, wie bei neuen Konten), danach zurück in die freie Fahrt ->
    /// Schritt 8 (Tuto FROG, noch nicht umgesetzt: Ende der Geschichte).
    /// </summary>
    sealed class UsedCarDealerMode : StoryMode
    {
        public override bool BlocksInput => false;
        TDU2StoryScene _ui;
        bool _chosen, _told;
        float _t;

        public override void Enter()
        {
            Suspend();
            TDU2GameMode.Push(Gamemode.GAME_MODE_SHOP);
            _ui = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
            if (Story.StarterDealer != null) Story.StarterDealer(car => { _chosen = true; DebugManager.Info("[TDU2] Geschichte: Auto gekauft – " + car?.FileName); });
            else { _chosen = true; Story.SpawnAtSpot(Manager.FindCar(Manager.StartCar) ?? Story.StoryCar, "ESTOL_B"); }
        }

        public override void Tick(float dt)
        {
            _t += dt;
            if (!_told && _ui != null && _ui.Loaded && _t > 2f) { _told = true; _ui.ShowTutorial(_ui.Loc(4033)); }   // "Ein Auto kaufen!"
            var inter = Manager.GetComponent<TDU2Interiors>();
            if (_chosen && Manager.Current != null && (inter == null || !inter.Inside)) Story.Go(8);
        }

        public override void Exit()
        {
            _ui?.CloseTutorial();
            _ui?.Dispose(); _ui = null;
            TDU2GameMode.Pop();
        }
    }
    // =====================================================================================================================
    /// <summary>
    /// Schritt 8 "Tuto FROG" (pack_freerideFROG, DBTutoMess "frog", Ziel 4034 "Mit dem F.R.I.M. in das 1. Level Geld holen."):
    /// zuerst wie im Original die Wahl der Fahrhilfen (general.vmf /driving_assistance: Open(), Auswahlbox "tuto:assi" mit
    /// FSCommands tuto:assi:scne/scpr = nächste/vorige, tuto:assi:vali = bestätigt; 0 Sicher, 1 Sport, 2 Hypersport).
    /// Das F.R.I.M.-Minispiel selbst ist noch nicht umgesetzt -> danach Schritt 9 "Go Home".
    /// </summary>
    sealed class TutoFrogMode : StoryMode
    {
        public override bool BlocksInput => true;
        public override bool MutesRadio => false;
        TDU2StoryScene _ui;
        VmfPlayer _g;
        bool _opened, _initSeen, _done, _wasSuspended;
        int _idx = 1;   // Original: DrivingAssistance = txt1 (Sport)
        float _t, _openT;

        public override void Enter()
        {
            _wasSuspended = Manager.Suspended;
            _ui = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
        }

        public override void Tick(float dt)
        {
            _t += dt;
            if (_done) return;
            if (_ui == null || _ui.Failed) { Finish("keine Oberfläche"); return; }
            if (!_ui.Loaded) { if (_t > 15f) Finish("Oberfläche lädt nicht"); return; }
            if (_g == null)
            {
                _g = _ui.General;
                if (_g == null || _g.Find("/driving_assistance") == null) { Finish("general.vmf ohne /driving_assistance"); return; }
                _g.FSCommand += OnCommand;
            }
            if (!_opened)
            {
                if (Manager.Current == null || _t < 1f) return;   // erst, wenn das gekaufte Auto da ist
                _opened = true; _openT = _t;
                Manager.Suspended = true;
                _g.SetVisible("/driving_assistance", true);
                _g.SetValue("/:Input", true);
                if (!_g.Call("/driving_assistance", "Open")) { Finish("Open() fehlt"); return; }
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                TDU2Diag.Log("STORY", "Fahrhilfen-Auswahl geöffnet (general.vmf /driving_assistance)");
                return;
            }
            // Rückfall: Bild 9 ruft initParameter() – kam nach 1 s noch kein tuto:assi:open, selbst aufrufen
            if (!_initSeen && _t - _openT > 1f) { _initSeen = true; _g.Call("/driving_assistance", "initParameter"); }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) Step(-1);
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) Step(1);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E))
            {
                if (!_g.Input("A")) Choose();
            }
#endif
        }

        /// <summary>Auswahl um eins weiter (wie die Pfeile der Auswahlbox, die das Spiel mit scne/scpr beantwortet).</summary>
        void Step(int dir)
        {
            _idx = (_idx + dir + 3) % 3;
            Apply();
        }

        void Apply()
        {
            string txt = _g.GetVariable("/driving_assistance:txt" + _idx);
            if (txt != null) _g.SetVariable("/:DrivingAssistance", txt);
            _g.Call("/driving_assistance", "updateText", _idx);
        }

        void OnCommand(string c, string a)
        {
            string cmd = string.IsNullOrEmpty(a) ? c : c + ":" + a;
            TDU2Diag.Log("STORY", "Fahrhilfen FSCommand " + cmd);
            if (cmd.StartsWith("tuto:assi:open", StringComparison.Ordinal)) { _initSeen = true; Apply(); }
            else if (cmd.EndsWith(":scne", StringComparison.Ordinal)) Step(1);
            else if (cmd.EndsWith(":scpr", StringComparison.Ordinal)) Step(-1);
            else if (cmd.StartsWith("tuto:assi:vali", StringComparison.Ordinal)) Choose();
        }

        void Choose()
        {
            if (_done) return;
            var aids = _idx == 0 ? DrivingAids.Secure : _idx == 2 ? DrivingAids.HyperSport : DrivingAids.Sport;
            Manager.Aids = aids;
            if (Manager.Current != null) Manager.Current.Aids = aids;
            try { PlayerPrefs.SetInt("TDU2.Aids", (int)aids); PlayerPrefs.Save(); } catch { }
            _g?.Call("/driving_assistance", "Close");
            Finish("gewählt: " + aids);
        }

        void Finish(string why)
        {
            if (_done) return;
            _done = true;
            TDU2Diag.Log("STORY", "Fahrhilfen-Auswahl: " + why + " – weiter mit Go Home (F.R.I.M. noch nicht umgesetzt)");
            Story.Go(9);
        }

        public override void Exit()
        {
            if (_g != null) _g.FSCommand -= OnCommand;
            _g = null;
            _ui?.Dispose(); _ui = null;
            if (_opened) Manager.Suspended = _wasSuspended;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 9 "Go Home" (Spielmodus MISSION): zum ersten Haus fahren – der Wohnwagen EPH_L0C_1254I (DBHouse Stufe 0, "Wohnwagen",
    /// GamePC DefaultHouse des Originals). Ziel 4035 "Zum ersten Haus Fahren.", GPS zum Spot; angekommen (langsam am Spot oder drinnen)
    /// -> Schritt 10.
    /// </summary>
    sealed class GoHomeMode : StoryMode
    {
        public const string HouseSpot = "EPH_L0C_1254I";
        public override bool BlocksInput => false;
        TDU2StoryScene _ui;
        Vector3 _targetGame;
        bool _hasTarget, _told, _gpsSet;
        float _t, _arriveT, _gpsCheck;

        public override void Enter()
        {
            TDU2GameMode.Push(Gamemode.GAME_MODE_MISSION);
            _ui = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
            // Fortsetzen nach dem Anmelden: noch kein Auto -> eigenes Auto am Gebrauchtwagenhändler (Ende von Schritt 7)
            if (Story.NeedsCar) Story.SpawnAtSpot(Story.PlayerCar(), "ESTOL_B");
            var s = Story.FindSpot(HouseSpot);
            if (s != null) { _targetGame = s.Position2.sqrMagnitude > 1f ? s.Position2 : s.Position; _hasTarget = true; }
            else DebugManager.Warning("[TDU2] Geschichte: Spot " + HouseSpot + " nicht gefunden");
        }

        public override void Tick(float dt)
        {
            _t += dt;
            if (!_told && _ui != null && _ui.Loaded && _t > 1.5f) { _told = true; _ui.ShowGoal(_ui.Loc(4035)); }   // "Zum ersten Haus Fahren."
            var inter = Manager.GetComponent<TDU2Interiors>();
            if (inter != null && inter.Inside && IsCaravan(inter.CurrentSpot)) { Story.Go(10); return; }
            var veh = Manager.Current;
            if (!_hasTarget || veh == null) return;
            var gps = TDU2Gps.Instance;
            if (gps != null && (!_gpsSet || Time.unscaledTime >= _gpsCheck))
            {
                _gpsCheck = Time.unscaledTime + 2f;
                if (!gps.HasTarget || !gps.IsTargetNear(_targetGame, 450f)) { gps.SetTarget(_targetGame, !_gpsSet); _gpsSet = true; }
            }
            var pos = Manager.Streamer.MapRoot.InverseTransformPoint(veh.transform.position);
            float dist = new Vector2(pos.x - _targetGame.x, pos.z - _targetGame.z).magnitude;
            if (dist < 25f && veh.SpeedKmh < 15f) { _arriveT += dt; if (_arriveT > 1f) Story.Go(10); }
            else _arriveT = 0f;
        }

        public static bool IsCaravan(Spot s) => s != null && TDU2GamePC.FindSpot(new List<Spot> { s }, HouseSpot) != null;

        public override void Exit()
        {
            _ui?.CloseGoal();
            _ui?.Dispose(); _ui = null;
            if (_gpsSet && TDU2Gps.Instance != null) TDU2Gps.Instance.ClearTarget(true);
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Schritt 10 "Home sweet home : Caravan": am Wohnwagen; Tipp "spva" (2225: Ort mit Bestätigen betreten), bis man drinnen ist.
    /// Danach ist die Geschichte auf Ibiza durch (Schritt 11 "Hawai First Freeride" ist noch nicht umgesetzt) -> freie Fahrt.
    /// </summary>
    sealed class CaravanMode : StoryMode
    {
        public override bool BlocksInput => false;
        TDU2StoryScene _ui;
        bool _told, _inside;
        float _t, _insideT;

        public override void Enter()
        {
            TDU2GameMode.Push(Gamemode.GAME_MODE_MISSION);
            // Nur ohne Auto UND draußen (Fortsetzen nach dem Anmelden). Wer per Enter schon im Wohnwagen ist, hat kein Auto –
            // ein Ersatz-Auto würde ihn mitten im Betreten auf die Straße setzen (Interieur halb geladen, Spieler hängt draußen).
            if (Story.NeedsCar) Story.SpawnAtSpot(Story.PlayerCar(), GoHomeMode.HouseSpot);
            TDU2Diag.Log("STORY", $"Wohnwagen: Auto={(Manager.Current != null ? Manager.Current.Car.FileName : "-")} drinnen={Story.InInterior} Ersatzauto={(Story.SpawnPending ? "ja" : "nein")}");
            _ui = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
        }

        public override void Tick(float dt)
        {
            _t += dt;
            var inter = Manager.GetComponent<TDU2Interiors>();
            bool inside = inter != null && inter.Inside && GoHomeMode.IsCaravan(inter.CurrentSpot);
            if (!_told && !inside && _ui != null && _ui.Loaded && _t > 1f) { _told = true; _ui.ShowTutorial(_ui.Loc(2225)); }
            if (inside && !_inside)
            {
                _inside = true;
                _ui?.CloseTutorial();
                TDU2GameMode.Pop();   // drinnen: Wohnbereich (LIVING) wie sonst
                DebugManager.Info("[TDU2] Geschichte: Home sweet home – Wohnwagen erreicht");
            }
            if (_inside) { _insideT += dt; if (_insideT > 2f) Story.Go(11); }
        }

        public override void Exit()
        {
            _ui?.CloseTutorial();
            _ui?.Dispose(); _ui = null;
            if (!_inside) TDU2GameMode.Pop();
        }
    }

}
