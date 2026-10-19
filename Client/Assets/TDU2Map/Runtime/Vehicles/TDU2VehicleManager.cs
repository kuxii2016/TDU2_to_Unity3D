using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Fahrzeug-Loader: liest die Autos direkt aus dem Spielordner (Euro/Bnk/vehicules, database, sound/vehicles),
    /// spawnt das gewählte Auto und steuert es. Dev-Tasten:
    ///   F2 einsteigen/aussteigen (Freiflug), F5/F6 nächstes/vorheriges Auto, F7 nächste Lackfarbe, F8 neu laden,
    ///   C Kamera wechseln, B Rückblick (halten), T Fahrhilfen, G Automatik/Manuell, E/Q hoch-/runterschalten,
    ///   R zurücksetzen, H Hupe, WASD/Pfeile fahren, Leertaste Handbremse.
    /// </summary>
    public sealed class TDU2VehicleManager : MonoBehaviour
    {
        public TDU2MapStreamer Streamer;
        [Tooltip("Startauto (Dateiname aus der Datenbank, z.B. delta_integrale_evo_2)")]
        public string StartCar = "delta_integrale_evo_2";
        public bool SpawnOnStart = false;
        public bool LoadSound = true;
        [Range(0, 2)] public float Volume = 0.7f;
        [Range(0.3f, 1.2f)] public float GripScale = 0.62f;
        public DrivingAids Aids = DrivingAids.Sport;
        /// <summary>Getriebe für jedes (auch neu geladene) Fahrzeug; Optionsmenü/Taste setzen diesen Wert.</summary>
        public bool AutomaticGearbox = true;
        public bool ShowHud = true;
        [Tooltip("Physik-Zeitschritt während der Fahrt (s). TDU2 rechnet die Räder zusätzlich in Unterschritten.")]
        public float FixedTimeStep = 0.01f;

        public CarDatabase Database { get; private set; }
        public CameraDatabase Cameras { get; private set; }
        public PhysicsConfig Physics { get; private set; }
        public readonly List<CarPhysics> Available = new List<CarPhysics>();
        public TDU2Vehicle Current { get; private set; }

        string _bnkDir;
        Task _dbTask;
        Task<CarAssets> _loadTask;
        int _carIndex = -1, _colorIndex;
        Vector3? _pendingPos; Quaternion _pendingRot; Vector3 _pendingVel;
        CarAssets _assets;
        CarModel _model;
        TDU2VehicleCamera _vcam;
        TDU2VehicleAudio _audio;
        TDU2VehicleLights _lights;
        [Tooltip("Echtzeit-Reflexionen für Spiegel und Lack (kostet etwas Leistung)")]
        public bool MirrorReflections = true;
        [Tooltip("Original-HUD des Spiels (Flash/VMF: Drehzahlmesser, Tacho, Gang)")]
        public bool ShowOriginalHud = true;
        TDU2VehicleHud _hud;
        [Tooltip("Minimap mit Straßennetz aus <Insel>_ROAD_DETAIL.IA (Taste J schaltet um)")]
        public bool ShowMinimap = true;
        TDU2Minimap _minimap;
        int _headlights;
        Behaviour _flyCam;
        float _defaultFixedDt;
        string _status = "";
        float _steerInput;
        [Tooltip("Häuser, Autohändler & Co. betreten (Enter an der Einfahrt), Laufen in der Ich-Perspektive")]
        public bool EnableInteriors = true;
        [Tooltip("Original-Pausemenü (ESC) aus main.bnk")]
        public bool EnablePauseMenu = true;
        [Tooltip("Große Karte (M) wie im Original: Kamera über der Insel + map.bnk")]
        public bool EnableBigMap = true;
        [Tooltip("Anmeldung wie im Spiel (log_in2.bnk + Profilmenü) mit Konto/Spielstand auf dem TDU2Server")]
        public bool EnableLogin = true;
        [Tooltip("Verkehr (Straßennetz .IA, Verkehrsautos der Insel) – Einstellungen am Objekt 'TDU2 Traffic Manager' bzw. an der TDU2Traffic-Komponente")]
        public bool EnableTraffic = true;

        /// <summary>true, solange ein Interieur aktiv ist (keine Fahr-/Dev-Tasten, kein Fahr-HUD).</summary>
        public bool Suspended { get; set; }
        /// <summary>true: Pedale/Lenkung des aktuellen Autos setzt ein anderes Skript (z.B. Autopilot nach dem Zieleinlauf), Tasten werden ignoriert.</summary>
        public bool ExternalControl { get; set; }
        public string BnkDir => _bnkDir;
        public int ColorIndex => _colorIndex;
        /// <summary>Modell des aktuellen Autos (null ohne Auto).</summary>
        public CarModel Model => _model;
        bool _liveryHooked;

        void OnLiveryChanged(string carFile)
        {
            if (_model == null || Current == null || Current.Car == null) return;
            if (string.Equals(Current.Car.FileName, carFile, StringComparison.OrdinalIgnoreCase)) TDU2LiveryView.ApplyOwn(_model, carFile);
        }
        public bool IsLoadingCar => _loadTask != null;
        public TDU2Minimap Minimap => _minimap;
        /// <summary>Lichter des aktuellen Autos (Zustand für die Online-Synchronisation).</summary>
        public TDU2VehicleLights Lights => _lights;
        /// <summary>Hupe gedrückt (für die Online-Synchronisation).</summary>
        public bool HornOn => !Suspended && Time.timeScale > 0f && Current != null && TDU2Controls.Held("horn");

        void Start()
        {
            StartCar = DebugManager.Instance?.StartVehicle ?? StartCar;
            if (Streamer == null) Streamer = FindAnyStreamer();
            _defaultFixedDt = Time.fixedDeltaTime;
            if (EnableInteriors && GetComponent<TDU2Interiors>() == null) gameObject.AddComponent<TDU2Interiors>().Manager = this;
            if (EnablePauseMenu && GetComponent<TDU2PauseMenu>() == null) gameObject.AddComponent<TDU2PauseMenu>().Manager = this;
            if (EnableBigMap && GetComponent<TDU2BigMap>() == null) gameObject.AddComponent<TDU2BigMap>().Manager = this;
            var online = Online.TDU2GameData.Ensure();   // Online Manager: Server-Adresse, Online-Status, Spielerdaten für alle Flash-Filme
            online.BindManager(this);
            bool login = EnableLogin && online.UseLogin;
            if (login && EnablePauseMenu && GetComponent<TDU2Login>() == null) gameObject.AddComponent<TDU2Login>().Manager = this;
            if (login && GetComponent<Online.TDU2Multiplayer>() == null) gameObject.AddComponent<Online.TDU2Multiplayer>().Manager = this;
            if (EnableTraffic)
            {
#if UNITY_2023_1_OR_NEWER
                var traffic = FindFirstObjectByType<Traffic.TDU2Traffic>();
#else
                var traffic = FindObjectOfType<Traffic.TDU2Traffic>();
#endif
                if (traffic == null) traffic = gameObject.AddComponent<Traffic.TDU2Traffic>();
                if (traffic.Manager == null) traffic.Manager = this;
            }
        }

        /// <summary>Bestimmtes Auto an Position/Ausrichtung (Welt) laden.</summary>
        public void SpawnCar(CarPhysics car, Vector3 pos, Quaternion rot)
        {
            if (car == null || Available.Count == 0) return;
            int i = Available.IndexOf(car);
            if (i < 0) i = Available.FindIndex(c => string.Equals(c.FileName, car.FileName, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return;
            Load(i, pos, rot, Vector3.zero);
        }

        /// <summary>
        /// Genau dieses Fahrzeug laden (auch Sondermodelle, die wegen gleicher Fahrzeugbank nicht in <see cref="Available"/> stehen,
        /// z.B. Rennschul-Autos #626 "CCXR Edition Racing School Asphalt" = Bank CCXR_Edition mit eigener Physik und Beklebung).
        /// false, wenn gerade ein anderes Auto lädt.
        /// </summary>
        public bool SpawnCarExact(CarPhysics car, Vector3 pos, Quaternion rot)
        {
            if (car == null || _loadTask != null || string.IsNullOrEmpty(_bnkDir) || Database == null) return false;
            int i = Available.FindIndex(c => string.Equals(c.FileName, car.FileName, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _carIndex = i;
            _pendingPos = pos; _pendingRot = rot; _pendingVel = Vector3.zero;
            string bnk = _bnkDir; var db = Database; int color = _colorIndex; bool snd = LoadSound;
            _status = "Lade " + car.ModelName + " ...";
            _loadTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, snd));
            return true;
        }

        /// <summary>Aktuelles Auto entfernen, ohne die Freiflug-Kamera zu aktivieren (Interieur übernimmt die Kamera).</summary>
        public void RemoveCar()
        {
            Despawn(false);
            if (_vcam != null) _vcam.enabled = false;
            var cam = Camera.main;
            if (cam != null)
            {
                var fly = cam.GetComponent<FlyCamera>();
                if (fly != null) { _flyCam = fly; fly.enabled = false; }
            }
        }

        public CarPhysics FindCar(string fileName) => Available.Find(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        public float GroundHeight(Vector3 p) => GroundY(p);

        static TDU2MapStreamer FindAnyStreamer()
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<TDU2MapStreamer>();
#else
            return FindObjectOfType<TDU2MapStreamer>();
#endif
        }

        void EnsureDatabase()
        {
            if (_dbTask != null || Streamer == null || Streamer.Index == null) return;
            _bnkDir = Streamer.Index.BnkDir;
            if (string.IsNullOrEmpty(_bnkDir)) { _status = "Bnk-Ordner nicht gefunden"; return; }
            string bnk = _bnkDir;
            _status = "Lade Fahrzeugdatenbank ...";
            _dbTask = Task.Run(() =>
            {
                var dbFile = CarDatabase.FindDatabase(bnk) ?? throw new System.IO.FileNotFoundException("database/db_data.cpr fehlt");
                var db = CarDatabase.Load(dbFile);
                var cams = CameraDatabase.Load(bnk);
                var phys = PhysicsConfig.Load(bnk);
                var avail = new List<CarPhysics>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in db.Cars)
                {
                    if (c.IsBike || !seen.Add(c.FileName)) continue;
                    if (CarDatabase.CarBank(bnk, c) != null) avail.Add(c);
                }
                avail.Sort((a, b) => string.Compare(a.ModelName, b.ModelName, StringComparison.OrdinalIgnoreCase));
                lock (Available) { Available.Clear(); Available.AddRange(avail); }
                Database = db; Cameras = cams; Physics = phys;
            });
        }

        void Update()
        {
            EnsureDatabase();
            if (_dbTask != null && _dbTask.IsFaulted)
            {
                _status = "Datenbank-Fehler: " + _dbTask.Exception?.GetBaseException().Message;
                Debug.LogError("[TDU2] " + _status);
                _dbTask = Task.CompletedTask; Database = null;
            }
            bool ready = Database != null && Available.Count > 0;
            if (ready && _carIndex < 0)
            {
                _carIndex = Mathf.Max(0, Available.FindIndex(c => string.Equals(c.FileName, StartCar, StringComparison.OrdinalIgnoreCase)));
                _status = $"{Available.Count} Fahrzeuge gefunden (Physik: {Physics?.Source})";
                if (SpawnOnStart) SpawnAtCamera();
            }

            PollLoad();
            if (Suspended) return;
            HandleKeys(ready);
            DriveInput();
        }

        // ------------------------------------------------------------------ Laden / Spawnen
        void SpawnAtCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.forward;
            var pos = cam.transform.position + fwd.normalized * 8f;
            pos.y = GroundY(pos) + 0.25f;
            Load(_carIndex, pos, Quaternion.LookRotation(fwd.normalized, Vector3.up), Vector3.zero);
        }

        float GroundY(Vector3 p)
        {
            if (UnityEngine.Physics.Raycast(p + Vector3.up * 200f, Vector3.down, out var hit, 2000f, ~(1 << 2), QueryTriggerInteraction.Ignore)) return hit.point.y;
            if (Streamer != null && Streamer.TryGetGroundHeight(p, out var y)) return y;
            return p.y;
        }

        void Load(int index, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (Available.Count == 0 || _loadTask != null) return;
            _carIndex = (index % Available.Count + Available.Count) % Available.Count;
            var car = Available[_carIndex];
            _pendingPos = pos; _pendingRot = rot; _pendingVel = vel;
            string bnk = _bnkDir; var db = Database; int color = _colorIndex; bool snd = LoadSound;
            _status = "Lade " + car.ModelName + " ...";
            _loadTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, snd));
        }

        void PollLoad()
        {
            if (_loadTask == null || !_loadTask.IsCompleted) return;
            var t = _loadTask; _loadTask = null;
            if (t.IsFaulted)
            {
                _status = "Fehler: " + t.Exception?.GetBaseException().Message;
                Debug.LogError("[TDU2] " + t.Exception?.GetBaseException());
                return;
            }
            var a = t.Result;
            foreach (var l in a.Log) Debug.Log("[TDU2] " + a.Car.FileName + ": " + l);
            Despawn(false);
            try { BuildVehicle(a); }
            catch (Exception ex) { _status = "Fehler beim Aufbau: " + ex.Message; Debug.LogError("[TDU2] " + ex); }
        }

        void BuildVehicle(CarAssets a)
        {
            var car = a.Car;
            var go = new GameObject(car.Id + ": " + car.Brand + " - " + car.ModelName);
            go.layer = 2; // Ignore Raycast: eigene Collider nicht von den Rad-Raycasts treffen
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            _model = CarModel.Build(a, go.transform);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            // eigene Lackierung/Beklebung aus dem Sticker-Shop (TDU2Liveries)
            if (!Racing.RacingSchoolMenu.IsSchoolCar(car)) { TDU2StickerLibrary.Ensure(_bnkDir); TDU2LiveryView.ApplyOwn(_model, car.FileName); }
            if (!_liveryHooked) { _liveryHooked = true; TDU2Liveries.Changed += OnLiveryChanged; }

            // Karosserie-Collider (Bounds der High-Poly-Karosserie, Unterkante um CollisionOffset angehoben)
            var b = _model.BodyBounds;
            float lift = Physics != null ? Physics.Get("CollisionOffset", 0.1f) : 0.1f;
            var box = go.AddComponent<BoxCollider>();
            float bottom = b.min.y + lift;
            box.center = new Vector3(b.center.x, (bottom + b.max.y) * 0.5f, b.center.z);
            box.size = new Vector3(b.size.x * 0.96f, Mathf.Max(0.3f, b.max.y - bottom), b.size.z * 0.98f);
#if UNITY_6000_0_OR_NEWER
            var pm = new PhysicsMaterial { dynamicFriction = 0.3f, staticFriction = 0.3f, bounciness = 0.05f, frictionCombine = PhysicsMaterialCombine.Minimum };
#else
            var pm = new PhysicMaterial { dynamicFriction = 0.3f, staticFriction = 0.3f, bounciness = 0.05f, frictionCombine = PhysicMaterialCombine.Minimum };
#endif
            box.sharedMaterial = pm;
            _model.Owned.Add(pm);

            var veh = go.AddComponent<TDU2Vehicle>();
            veh.Car = car; veh.Config = Physics; veh.TorqueMax = a.TorqueMax; veh.TorqueMin = a.TorqueMin;
            veh.GripScale = GripScale; veh.Aids = Aids; veh.AutomaticGearbox = AutomaticGearbox;
            veh.GroundFallback = p => Streamer != null && Streamer.TryGetGroundHeight(p, out var gy) ? (true, gy) : (false, 0f);

            var wheels = new List<TDU2Vehicle.Wheel>();
            foreach (var n in new[] { "FL", "FR", "RL", "RR" })
            {
                bool front = n[0] == 'F';
                var fork = _model.Find("FORK_" + n);
                var w = new TDU2Vehicle.Wheel { Name = n, Front = front };
                if (fork != null) w.Anchor = go.transform.InverseTransformPoint(fork.position);
                else
                {
                    float x = (front ? car.TrackFront : car.TrackRear) * 0.0005f;
                    float z = front ? car.Wheelbase * 0.0005f : -car.Wheelbase * 0.0005f;
                    w.Anchor = new Vector3(n[1] == 'L' ? -x : x, 0.2f, z);
                }
                w.Left = w.Anchor.x < 0f;
                w.Radius = a.Rim != null ? a.Rim.TireRadius(front) : 0.33f;
                w.Width = a.Rim != null ? (front ? a.Rim.WidthFront : a.Rim.WidthRear) * 0.001f : 0.24f;
                w.Fork = fork;
                w.Stirrup = _model.Find("STIRRUP_" + n);
                w.Disc = _model.Find("DISC_" + n);
                if (w.Stirrup != null && w.Stirrup.parent != null)
                {
                    var m = go.transform.worldToLocalMatrix * w.Stirrup.parent.localToWorldMatrix;
                    w.SteerSign = m.determinant < 0f ? -1f : 1f;
                }
                wheels.Add(w);
            }
            veh.Wheels = wheels.ToArray();
            veh.SteeringWheel = _model.SteeringWheel;
            if (veh.SteeringWheel != null && veh.SteeringWheel.parent != null)
                veh.SteeringWheelSign = (go.transform.worldToLocalMatrix * veh.SteeringWheel.parent.localToWorldMatrix).determinant < 0f ? -1f : 1f;
            veh.Setup();
            veh.CaptureVisualBase();

            // Position
            var pos = _pendingPos ?? Vector3.zero;
            rb.isKinematic = false;
            veh.ResetVehicle(pos, _pendingRot);
            if (_pendingVel.sqrMagnitude > 0.01f) veh.SetVelocity(_pendingVel);
            _pendingPos = null;

            // Sound
            if (LoadSound)
            {
                _audio = go.AddComponent<TDU2VehicleAudio>();
                _audio.Vehicle = veh; _audio.MasterVolume = Volume;
                _audio.Setup(a);
                if (_pendingVel.sqrMagnitude < 1f) _audio.Play("EngineStart");
            }

            // Lichter (Scheinwerfer, Bremse, Rückfahr, Blinker)
            _lights = go.AddComponent<TDU2VehicleLights>();
            _lights.Headlights = _headlights;
            _lights.Setup(veh, _model);

            // Instrumente (Tacho-/Drehzahlnadel), Bodenschatten, Reifenspuren
            go.AddComponent<TDU2VehicleGauges>().Setup(veh, _model, a.Gauges);
            go.AddComponent<TDU2VehicleEffects>().Setup(veh, _model, a);
            // Verschmutzung (dirt_scratch, DBCarDirtData)
            go.AddComponent<TDU2CarDirt>().Setup(veh, _model);

            // Spiegel/Lack: Echtzeit-Reflexionen der Umgebung (Rück- und Außenspiegel sind Chrommaterialien)
            if (MirrorReflections)
            {
                var probeGo = new GameObject("ReflectionProbe");
                probeGo.transform.SetParent(go.transform, false);
                probeGo.transform.localPosition = new Vector3(0f, 1.1f, 0.2f);
                var probe = probeGo.AddComponent<ReflectionProbe>();
                probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
                probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame;
                probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
                probe.resolution = 256;
                probe.size = new Vector3(60f, 30f, 60f);
                probe.boxProjection = false;
                probe.importance = 10;
                probe.farClipPlane = 600f;
                probe.cullingMask = ~(1 << 2);   // das eigene Auto (Layer 2) nicht mitspiegeln
            }

            // Kamera
            var cam = Camera.main;
            if (cam != null)
            {
                _flyCam = cam.GetComponent<FlyCamera>();
                if (_flyCam != null) _flyCam.enabled = false;
                _vcam = cam.GetComponent<TDU2VehicleCamera>();
                if (_vcam == null) _vcam = cam.gameObject.AddComponent<TDU2VehicleCamera>();
                _vcam.enabled = true;
                _vcam.Target = veh; _vcam.Cameras = Cameras; _vcam.CarId = car.CameraId != 0 ? car.CameraId : car.Id;
            }

            // Original-Instrumente (GAUGES, Eden-Flash)
            if (ShowOriginalHud)
            {
                if (_hud == null) _hud = gameObject.AddComponent<TDU2VehicleHud>();
                _hud.Bind(_bnkDir, veh, a, _vcam);
            }
            if (ShowMinimap)
            {
                if (_minimap == null) _minimap = gameObject.AddComponent<TDU2Minimap>();
                _minimap.Database = Database;
                _minimap.Bind(Streamer, veh);
            }
            Time.fixedDeltaTime = Mathf.Clamp(FixedTimeStep, 0.005f, 0.02f);

            _assets = a;
            Current = veh;
            _status = $"{car.ModelName}  ({a.Color?.Name ?? "Standardfarbe"})";
        }

        void Despawn(bool restoreFlyCam)
        {
            if (Current != null) { UnityUtil.SafeDestroy(Current.gameObject); Current = null; }
            _model?.Destroy(); _model = null;
            _assets?.DestroyTextures(); _assets = null;
            _audio = null;
            _lights = null;
            if (restoreFlyCam)
            {
                if (_vcam != null) _vcam.enabled = false;
                if (_flyCam != null) _flyCam.enabled = true;
                Time.fixedDeltaTime = _defaultFixedDt;
            }
        }

        void OnEnable() => FloatingOrigin.Shifted += OnShift;
        void OnDisable() => FloatingOrigin.Shifted -= OnShift;
        void OnShift(Vector3 d) { if (_pendingPos.HasValue) _pendingPos = _pendingPos.Value - d; }

        void OnDestroy() { if (_liveryHooked) TDU2Liveries.Changed -= OnLiveryChanged; Despawn(true); }

        // ------------------------------------------------------------------ Eingaben
        void HandleKeys(bool ready)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!ready) return;
            if (Input.GetKeyDown(KeyCode.F2))
            {
                if (Current != null) Despawn(true); else SpawnAtCamera();
            }
            if (Input.GetKeyDown(KeyCode.F5)) SwitchCar(+1);
            if (Input.GetKeyDown(KeyCode.F6)) SwitchCar(-1);
            if (Input.GetKeyDown(KeyCode.F7)) { _colorIndex++; SwitchCar(0); }
            if (Input.GetKeyDown(KeyCode.F8)) SwitchCar(0);
            if (Current == null) return;
            // Einstellungen (Optionsmenü) gelten immer für das aktuelle Fahrzeug
            if (Current.Aids != Aids) Current.Aids = Aids;
            if (Current.AutomaticGearbox != AutomaticGearbox) Current.AutomaticGearbox = AutomaticGearbox;
            if (Time.timeScale <= 0f) return;   // Pause/Menü: keine Fahrtasten
            if (TDU2Controls.Pressed("camera") && _vcam != null) _vcam.NextMode();
            if (TDU2Controls.Pressed("minimap") && _minimap != null) _minimap.ShowMinimap = !_minimap.ShowMinimap;
            if (_vcam != null) _vcam.LookBack = TDU2Controls.Held("lookback");
            if (TDU2Controls.Pressed("aids")) { Aids = (DrivingAids)(((int)Aids + 1) % 4); Current.Aids = Aids; }
            if (TDU2Controls.Pressed("gearbox")) { AutomaticGearbox = !AutomaticGearbox; Current.AutomaticGearbox = AutomaticGearbox; }
            if (TDU2Controls.Pressed("gearup")) Current.ShiftUp();
            if (TDU2Controls.Pressed("geardown")) Current.ShiftDown();
            if (TDU2Controls.Pressed("reset"))
            {
                var p = Current.transform.position; p.y = GroundY(p) + 0.3f;
                var f = Vector3.ProjectOnPlane(Current.transform.forward, Vector3.up);
                Current.ResetVehicle(p, Quaternion.LookRotation(f.sqrMagnitude > 1e-3f ? f.normalized : Vector3.forward, Vector3.up));
            }
            if (_audio != null) _audio.SetHorn(TDU2Controls.Held("horn"));
            if (_lights != null)
            {
                if (TDU2Controls.Pressed("lights")) { _headlights = (_lights.Headlights + 1) % 3; _lights.Headlights = _headlights; }
                if (TDU2Controls.Pressed("blinkl")) { _lights.IndicatorLeft = !_lights.IndicatorLeft; _lights.IndicatorRight = false; }
                if (TDU2Controls.Pressed("blinkr")) { _lights.IndicatorRight = !_lights.IndicatorRight; _lights.IndicatorLeft = false; }
                if (TDU2Controls.Pressed("hazard")) _lights.Hazard = !_lights.Hazard;
                _lights.Flash = TDU2Controls.Held("flash");   // Lichthupe (im Original: Herausforderung an Mitspieler)
            }
#endif
        }

        float _padCheckAt = -10f; bool _hasPad;
        bool HasGamepad()
        {
            if (Time.unscaledTime - _padCheckAt > 2f)
            {
                _padCheckAt = Time.unscaledTime; _hasPad = false;
#if ENABLE_LEGACY_INPUT_MANAGER
                foreach (var n in Input.GetJoystickNames()) if (!string.IsNullOrEmpty(n)) { _hasPad = true; break; }
#endif
            }
            return _hasPad;
        }

        void SwitchCar(int delta)
        {
            if (Available.Count == 0) return;
            if (Current != null)
            {
                var tr = Current.transform;
#if UNITY_6000_0_OR_NEWER
                var vel = Current.GetComponent<Rigidbody>().linearVelocity;
#else
                var vel = Current.GetComponent<Rigidbody>().velocity;
#endif
                var f = Vector3.ProjectOnPlane(tr.forward, Vector3.up);
                Load(_carIndex + delta, tr.position + Vector3.up * 0.2f, Quaternion.LookRotation(f.sqrMagnitude > 1e-3f ? f.normalized : Vector3.forward, Vector3.up), vel);
            }
            else
            {
                _carIndex = (_carIndex + delta + Available.Count) % Available.Count;
                SpawnAtCamera();
            }
        }

        void DriveInput()
        {
            if (Current == null) return;
            if (ExternalControl) { if (_audio != null && _vcam != null) _audio.InteriorView = _vcam.IsInterior; return; }
#if ENABLE_LEGACY_INPUT_MANAGER
            bool paused = Time.timeScale <= 0f;
            float accel = !paused && TDU2Controls.Held("accel") ? 1f : 0f;
            float brake = !paused && TDU2Controls.Held("brake") ? 1f : 0f;
            float steer = 0f;
            if (!paused && TDU2Controls.Held("left")) steer -= 1f;
            if (!paused && TDU2Controls.Held("right")) steer += 1f;
            // Gamepad (Input Manager Standardachsen), falls benutzt
            float padH = 0f, padV = 0f;
            // Standardachsen enthalten auch WASD/Pfeile -> nur mit angeschlossenem Gamepad nutzen, sonst gilt die eigene Belegung
            if (!paused && HasGamepad())
                try { padH = Input.GetAxisRaw("Horizontal"); padV = Input.GetAxisRaw("Vertical"); } catch (Exception) { }
            if (Mathf.Abs(steer) < 0.01f && Mathf.Abs(padH) > 0.15f) steer = padH;
            if (accel < 0.01f && padV > 0.15f) accel = padV;
            if (brake < 0.01f && padV < -0.15f) brake = -padV;
            // Tastatur-Lenkung glätten
            float rate = Mathf.Abs(steer) > Mathf.Abs(_steerInput) ? 4f : 6f;
            _steerInput = Mathf.MoveTowards(_steerInput, steer, rate * Time.deltaTime);
            Current.AccelPedal = accel;
            Current.BrakePedal = brake;
            Current.Steer = _steerInput;
            Current.HandBrake = !paused && TDU2Controls.Held("handbrake") ? 1f : 0f;
#endif
            if (_audio != null && _vcam != null) _audio.InteriorView = _vcam.IsInterior;
        }

        // ------------------------------------------------------------------ HUD
        GUIStyle _style, _big;
        void OnGUI()
        {
            if (DebugManager.Instance != null && !DebugManager.Instance.isVehicleDebug) return;
            if (!ShowHud || Suspended) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };
                _big = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            }
            float x = 10, y = Screen.height - 150;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(x - 5, y - 5, 470, 145), Texture2D.whiteTexture);
            GUI.color = Color.white;
#if !ENABLE_LEGACY_INPUT_MANAGER
            GUI.Label(new Rect(x, y, 460, 20), "Player Settings > Active Input Handling auf 'Both' stellen (Tastatur).", _style); y += 18;
#endif
            GUI.Label(new Rect(x, y, 460, 20), _status, _style); y += 18;
            if (Current != null)
            {
                var v = Current;
                string gear = v.Gear < 0 ? "R" : v.Gear == 0 ? "N" : v.Gear.ToString();
                GUI.Label(new Rect(x, y, 300, 40), $"{v.SpeedKmh:0} km/h   {gear}", _big); y += 40;
                float redFrac = Mathf.Clamp01(v.Rpm / Mathf.Max(1f, v.Car.RpmMax));
                GUI.color = new Color(0.2f, 0.2f, 0.2f, 0.9f); GUI.DrawTexture(new Rect(x, y, 300, 10), Texture2D.whiteTexture);
                GUI.color = v.Rpm > v.Car.RedLine * 0.95f ? Color.red : new Color(1f, 0.7f, 0.1f); GUI.DrawTexture(new Rect(x, y, 300 * redFrac, 10), Texture2D.whiteTexture);
                GUI.color = Color.white; y += 14;
                GUI.Label(new Rect(x, y, 460, 20), $"{v.Rpm:0} U/min   Hilfen: {v.Aids}   {(v.AutomaticGearbox ? "Automatik" : "Manuell")}   Kamera: {_vcam?.CurrentDef?.Mode}", _style); y += 18;
            }
            GUI.Label(new Rect(x, y, 470, 40), "F2 Ein/Aus  F5/F6 Auto  F7 Farbe  C Kamera  B Rückblick  T Hilfen  G Getriebe  E/Q Schalten  R Reset  H Hupe  L Licht  N/M Blinker  K Warnblinker  J Karte", _style);
        }
    }
}
