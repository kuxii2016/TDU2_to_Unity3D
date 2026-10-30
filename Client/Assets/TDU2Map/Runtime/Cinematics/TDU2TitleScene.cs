using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Cinematics;
using TDU2.Formats;
using TDU2.IO;
using TDU2;
using TDU2.World;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// 3D-Hintergrund des "Press Start"-Bildschirms wie im Original (GMLogin, FUN_009c7ea0 / FUN_009c6b20; nach OpenTDU2
    /// game/bootmenu/title_scene.cpp): eines der Press-Start-Kinos der Insel (islands/&lt;insel&gt;/scenarics/press_start.bnk).
    ///   - entities.xmb: Entity__Cinematic ohne "C_LIFE" (Zufall, FORCELOGINCINEINDEX n überschreibt), die letzten 4 Ziffern
    ///     des Namens wählen die .cin ("C_01_3332" -> *_3332.cin); Entity__CinCar CIN_CAR_xx -> id_car -> DBCarPhysicsData
    ///   - .cin: 3 Shots à 20 s über eine .sce (Kameras c_01..03, Auto CIN_CAR_xx), Nachrichten "weat:" / "wmod:" je Schnitt
    ///   - Wetter: fx/cinematic_weathers.xmb (benannte Schlüssel, z.B. SS_Death_Valley) über <see cref="TDU2Weather.Override"/>
    /// Die Insel wird um die Kinokamera gestreamt (die Kamera ist der Betrachter des Streamers). Schleife über die Kinodauer.
    /// </summary>
    // nach allen anderen Kamera-Skripten (TDU2VehicleCamera u.a. schreiben ebenfalls in LateUpdate -> sonst Kamera-Zittern)
    [DefaultExecutionOrder(1000)]
    public sealed class TDU2TitleScene : MonoBehaviour
    {
        /// <summary>Läuft gerade ein Titel-Kino?</summary>
        public static bool Playing => _inst != null && _inst._loaded;
        static TDU2TitleScene _inst;

        TDU2VehicleManager _mgr;
        CinData _cin;
        readonly List<CinScene> _scenes = new List<CinScene>();
        CinObject _carObj;
        string _cinName = "";
        int _index = -1, _count;
        float _time;
        bool _loaded, _started;
        float _waitSince;
        Camera _cam;
        float _camFov = 60f;
        bool _camSaved;
        GameObject _carGo;
        Task<CarAssets> _carTask;
        CarPhysics _car;
        bool _carGrounded;
        int _carId = -1;            // id_car der Szene (Auflösung, sobald die Datenbank geladen ist)
        bool _carResolved, _carFallbackTried;
        string _weather = "", _modifier = "";
        TDU2WeatherData _weathers;

        /// <summary>Titel-Kino starten (cinematicIndex &lt; 0: Zufall wie im Original).</summary>
        public static TDU2TitleScene Begin(TDU2VehicleManager mgr, int cinematicIndex = -1)
        {
            if (mgr == null) return null;
            if (_inst == null)
            {
                var go = new GameObject("TDU2 TitleScene");
                _inst = go.AddComponent<TDU2TitleScene>();
            }
            _inst._mgr = mgr;
            if (!_inst.Load(cinematicIndex)) { _inst.Unload(); return null; }
            return _inst;
        }

        /// <summary>Titel-Kino beenden (Kamera und Wetter zurück).</summary>
        public static void End()
        {
            if (_inst == null) return;
            _inst.Unload();
            UnityUtil.SafeDestroy(_inst.gameObject);
            _inst = null;
        }

        /// <summary>Nächstes Press-Start-Kino (OpenTDU2-Taste R).</summary>
        public static void Next()
        {
            if (_inst == null || _inst._count <= 0) return;
            int i = (_inst._index + 1) % _inst._count;
            var mgr = _inst._mgr;
            _inst.Unload();
            _inst._mgr = mgr;
            if (!_inst.Load(i)) _inst.Unload();
        }

        string IslandDir => _mgr?.Streamer?.Index?.IslandDir;

        bool Load(int cinematicIndex)
        {
            Unload();
            string islandDir = IslandDir;
            if (string.IsNullOrEmpty(islandDir)) return false;
            string bankPath = Path.Combine(islandDir, "scenarics", "press_start.bnk");
            if (!File.Exists(bankPath)) { DebugManager.Warning("[TDU2] Titel-Kino: " + bankPath + " fehlt"); return false; }
            try
            {
                var bank = BnkArchive.Open(bankPath);
                var ent = bank.Find("entities.xmb");
                if (ent == null) return false;
                var root = XmbFile.Parse(bank.Read(ent));
                var et = root.Get("ExportType_Entity");
                var names = new List<string>();
                var cinCars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (et != null)
                {
                    var arr = et.Get("ArrayOf_Entity__Cinematic");
                    if (arr != null) foreach (var it in arr.Items) { var n = it.S("__Name", ""); if (n.IndexOf("C_LIFE", StringComparison.OrdinalIgnoreCase) < 0) names.Add(n); }
                    var cars = et.Get("ArrayOf_Entity__CinCar");
                    if (cars != null) foreach (var it in cars.Items) cinCars[it.S("__Name", "")] = it.I("id_car", -1);
                }
                if (names.Count == 0) { DebugManager.Warning("[TDU2] Titel-Kino: keine Press-Start-Kinos"); return false; }
                int idx = cinematicIndex >= 0 && cinematicIndex < names.Count ? cinematicIndex : UnityEngine.Random.Range(0, names.Count);
                int forced = ForcedIndex();
                if (cinematicIndex < 0 && forced >= 0 && forced < names.Count) idx = forced;
                _index = idx; _count = names.Count; _cinName = names[idx];
                string digits = _cinName.Length >= 4 ? _cinName.Substring(_cinName.Length - 4) : "";
                BnkEntry cinEntry = null;
                foreach (var e in bank.All(".cin"))
                {
                    var stem = Path.GetFileNameWithoutExtension(e.Name);
                    if (stem.EndsWith("_" + digits, StringComparison.OrdinalIgnoreCase)) { cinEntry = e; break; }
                }
                _cin = cinEntry != null ? CinData.Parse(bank.Read(cinEntry), cinEntry.Name) : null;
                if (_cin == null) { DebugManager.Warning("[TDU2] Titel-Kino: .cin zu " + _cinName + " fehlt"); return false; }
                for (int s = 0; s < _cin.SceneReferences.Count; s++)
                {
                    string file = _cin.SceneFile(s);
                    var se = file != null ? bank.Find(file + ".sce") : null;
                    _scenes.Add(se != null ? CinScene.Parse(bank.Read(se), se.Name) : null);
                    if (se == null) DebugManager.Warning("[TDU2] Titel-Kino: Szene " + file + " fehlt");
                }
                if (_scenes.Count == 0 || _scenes[0] == null || _scenes[0].Objects.Count == 0) return false;
                // Auto der Szene: Darsteller "CIN_CAR_xx" -> Entity__CinCar id_car -> DBCarPhysicsData
                foreach (var sc in _scenes)
                {
                    if (sc == null) continue;
                    foreach (var o in sc.Objects)
                        if (o.ClassHash == CinIds.ClassActor && o.Name.StartsWith("CIN_CAR", StringComparison.OrdinalIgnoreCase)) { _carObj = o; break; }
                    if (_carObj != null) break;
                }
                _carId = _carObj != null && cinCars.TryGetValue(_carObj.Name, out int idCar) ? idCar : -1;
                _carResolved = false; _carFallbackTried = false;
                LoadWeathers(islandDir);
                DebugManager.Info($"[TDU2] Titel-Kino {_cinName} ({idx + 1}/{names.Count}): {_cin.Shots.Count} Shots, {_cin.Duration:0} s, Auto {(_car != null ? _car.ModelName : "-")}");
            }
            catch (Exception ex)
            {
                DebugManager.Error("[TDU2] Titel-Kino: " + ex.Message);
                return false;
            }
            _time = 0f; _started = false; _waitSince = Time.unscaledTime; _carGrounded = false;
            _loaded = true;
            if (!_probe) { _probe = true; TDU2SceneReflection.Acquire(); }   // Glanz/Spiegelung ohne Spieler-Auto
            ResolveCar();
            return true;
        }

        /// <summary>
        /// Auto der Szene laden, sobald die Fahrzeugdatenbank da ist. Fehlt das Auto (DLC-/Zusatzauto nicht installiert, Bank fehlt
        /// oder Laden schlägt fehl), ein vorhandenes Auto nehmen – im Original steht immer ein Auto in der Szene.
        /// </summary>
        void ResolveCar()
        {
            if (_carResolved || _carObj == null || _mgr?.Database == null) return;
            if (_mgr.Available.Count == 0 && _mgr.Database.Cars.Count > 0) return;   // Liste der installierten Autos noch nicht fertig
            _carResolved = true;
            _car = _carId >= 0 ? _mgr.Database.Cars.Find(c => c.Id == _carId) : null;
            if (_car != null && _mgr.Available.Count > 0 && !_mgr.Available.Exists(c => c.Id == _car.Id))
            {
                DebugManager.Info($"[TDU2] Titel-Kino: Auto {_car.ModelName} ({_carId}) nicht installiert – Ersatz");
                _car = null;
            }
            if (_car == null) { _car = FallbackCar(); _carFallbackTried = true; }
            StartCarLoad();
        }

        CarPhysics FallbackCar()
        {
            var pool = _mgr.Available.FindAll(c => !c.IsBike && (_car == null || c.Id != _car.Id));
            if (pool.Count == 0) pool = _mgr.Available;
            return pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;
        }

        void StartCarLoad()
        {
            if (_car == null) return;
            string bnk = _mgr.BnkDir; var db = _mgr.Database; var car = _car;
            _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, 0, false));
            DebugManager.Debug("[TDU2] Titel-Kino Auto: " + car.ModelName);
        }

        static int ForcedIndex()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-FORCELOGINCINEINDEX", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int n)) return n;
            return -1;
        }

        void LoadWeathers(string islandDir)
        {
            _weathers = null;
            string path = Path.Combine(islandDir, "fx", "cinematic_weathers.xmb");
            try { if (File.Exists(path)) _weathers = TDU2WeatherData.Load(path); }
            catch (Exception ex) { DebugManager.Warning("[TDU2] cinematic_weathers.xmb: " + ex.Message); }
            try { if (_weathers != null) _weathers.LoadModifiers(Path.Combine(islandDir, "fx", "cinematic_modifiers.xmb")); }
            catch (Exception ex) { DebugManager.Warning("[TDU2] cinematic_modifiers.xmb: " + ex.Message); }
        }

        bool _probe;

        void Unload()
        {
            _loaded = false;
            if (_probe) { _probe = false; TDU2SceneReflection.Release(); }
            _cin = null; _scenes.Clear(); _carObj = null; _car = null; _carId = -1; _carResolved = false;
            if (_carGo != null) { UnityUtil.SafeDestroy(_carGo); _carGo = null; }
            _carTask = null; _carModel = null; _carAssets = null; _hubs.Clear(); _carGrounded = false;
            TDU2Weather.Override = null;
            _weather = ""; _modifier = "";
            if (_cam != null && _camSaved) _cam.fieldOfView = _camFov;
            _camSaved = false;
        }

        void OnDestroy() { if (_inst == this) { Unload(); _inst = null; } }

        void LateUpdate()
        {
            if (!_loaded || _cin == null) return;
            var root = _mgr?.Streamer?.MapRoot;
            if (root == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            if (!_camSaved) { _camFov = _cam.fieldOfView; _camSaved = true; }

            // erst laufen lassen, wenn die Welt um die Kamera steht (sonst sieht man das Nachladen); spätestens nach 10 s
            if (!_started)
            {
                Apply(root, 0f);
                var at = _cam.transform.position;
                if (_mgr.Streamer.ReadyAround(at, 400f) || Time.unscaledTime - _waitSince > 10f) { _started = true; TDU2Diag.TraceCamera(240, "Titel-Kino"); }
            }
            else
            {
                _time += Time.unscaledDeltaTime;
                if (_cin.Duration > 0f && _time >= _cin.Duration) _time %= _cin.Duration;
                Apply(root, _time);
                TDU2Diag.CameraSet(_cam, "Titel", _time);
            }
            UpdateCar(root);
        }

        void Apply(Transform root, float t)
        {
            if (_cin.Camera(_scenes, t, out var gp, out var gr, out float fov))
            {
                _cam.transform.SetPositionAndRotation(root.TransformPoint(gp), CinIds.FrameRotation(root, gr));
                if (fov > 1f && fov < 170f) _cam.fieldOfView = fov;
            }
            // Wetter: "weat:<name>" und danach "wmod:<modifier>" je Schnitt
            string w = _cin.LastMessage("weat:", t, out float wt) ?? "";
            string m = _cin.LastMessage("wmod:", t, out float mt) ?? "";
            if (mt < wt) m = "";
            if (w != _weather || m != _modifier)
            {
                _weather = w; _modifier = m;
                var key = FindWeather(w, m);
                TDU2Weather.Override = key;
                DebugManager.Debug($"[TDU2] Titel-Kino Wetter {w}{(m.Length > 0 ? " / " + m : "")} -> {(key != null ? key.Name : "nicht gefunden")}");
            }
        }

        TDU2WeatherData.Key FindWeather(string name, string modifier)
        {
            if (_weathers == null || string.IsNullOrEmpty(name)) return null;
            return _weathers.Find(name, modifier);
        }

        void UpdateCar(Transform root)
        {
            if (!_carResolved) ResolveCar();
            if (_carTask != null && _carTask.IsCompleted)
            {
                var task = _carTask; _carTask = null;
                if (task.Status == TaskStatus.RanToCompletion && task.Result != null && _loaded)
                {
                    try
                    {
                        // Auto-Modelle sind selbst gespiegelt (CarModel.Visual) -> nicht unter MapRoot, Weltlage setzen
                        _carGo = new GameObject("TitleScene " + _car.ModelName);
                        var model = CarModel.Build(task.Result, _carGo.transform);
                        TDU2WheelSpin.Attach(_carGo, model, task.Result);
                        _carModel = model; _carAssets = task.Result; _hubs.Clear(); _carGrounded = false;
                    }
                    catch (Exception ex)
                    {
                        DebugManager.Error("[TDU2] Titel-Kino Auto: " + ex.Message);
                        if (_carGo != null) { UnityUtil.SafeDestroy(_carGo); _carGo = null; }
                    }
                }
                else if (task.Exception != null) DebugManager.Error("[TDU2] Titel-Kino Auto: " + task.Exception.GetBaseException().Message);
                // Laden fehlgeschlagen: einmal mit einem vorhandenen Auto
                if (_carGo == null && _loaded && !_carFallbackTried)
                {
                    _carFallbackTried = true;
                    _car = FallbackCar();
                    StartCarLoad();
                }
            }
            if (_carGo == null || _carObj == null || _scenes.Count == 0) return;
            // wie OpenTDU2 TitleScene::computeCarWorld: Ursprung der ersten Szene, Darsteller zur Kinozeit
            var sc = _scenes[0];
            float st = _started ? _time : 0f;
            var gp = sc.ToGame(_carObj.SamplePosition(st));
            var gr = sc.OriginRotation * _carObj.SampleRotation(st);
            _carGo.transform.SetPositionAndRotation(root.TransformPoint(gp), CinIds.FrameRotation(root, gr));
            if (!_carGo.activeSelf) _carGo.SetActive(true);
            FitOnGround();
        }

        /// <summary>
        /// Das Kino gibt nur die Lage über der Straße vor (im Spiel lässt die Physik das Auto auf die Räder fallen): wie OpenTDU2
        /// CinematicCar::fitOnGround jede Radnabe auf Straße/Gelände + Reifenradius setzen (Mittelwert, jedes Bild – das Auto fährt
        /// über Hügel; ein fester Versatz vom ersten Bild ließ es im Berg verschwinden).
        /// </summary>
        void FitOnGround()
        {
            if (_hubs.Count == 0 && _carModel != null)
            {
                foreach (var n in new[] { "FL", "FR", "RL", "RR" })
                {
                    var hub = _carModel.Find("DISC_" + n) ?? _carModel.Find("D_RIM_" + n) ?? _carModel.Find("FORK_" + n);
                    if (hub != null) _hubs.Add((hub, _carAssets?.Rim != null ? _carAssets.Rim.TireRadius(n[0] == 'F') : 0.33f));
                }
                if (_hubs.Count == 0) _carModel = null;   // ohne Naben: Unterkante der Karosserie (unten)
            }
            float sum = 0f; int count = 0;
            if (_hubs.Count > 0)
            {
                foreach (var (hub, r) in _hubs)
                {
                    if (hub == null) continue;
                    var hp = hub.position;
                    if (GroundAt(hp, out float surf)) { sum += surf + r - hp.y; count++; }
                }
            }
            else
            {
                float minY = float.MaxValue;
                foreach (var r in _carGo.GetComponentsInChildren<Renderer>()) if (r.enabled && r.bounds.size.y < 4f) minY = Mathf.Min(minY, r.bounds.min.y);
                if (minY < float.MaxValue && GroundAt(_carGo.transform.position, out float surf)) { sum = surf - minY; count = 1; }
            }
            if (count > 0)
            {
                float target = sum / count;
                // weich nachführen (Höhenraster/Straßen-Kanten), bei Schnitten springen
                if (!_carGrounded || Mathf.Abs(target - _groundOffset) > 1.5f) _groundOffset = target;
                else _groundOffset = Mathf.Lerp(_groundOffset, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f));
                _carGrounded = true;
            }
            if (_carGrounded) _carGo.transform.position += Vector3.up * _groundOffset;
        }

        /// <summary>Oberfläche unter einem Punkt: Straßen-/Objekt-Collider (höchster Treffer bis knapp über dem Gelände), sonst Höhenraster.</summary>
        bool GroundAt(Vector3 p, out float y)
        {
            bool terrain = _mgr.Streamer.TryGetGroundHeight(p, out float th);
            float top = Mathf.Max(p.y, terrain ? th : p.y) + 3f;
            bool hitAny = false; float best = float.MinValue;
            foreach (var hit in UnityEngine.Physics.RaycastAll(new Vector3(p.x, top, p.z), Vector3.down, top - p.y + 20f))
            {
                if (hit.transform.IsChildOf(_carGo.transform)) continue;
                if (hit.point.y > best) { best = hit.point.y; hitAny = true; }
            }
            if (hitAny && (!terrain || best >= th - 0.5f)) { y = best; return true; }
            y = th;
            return terrain;
        }

        readonly List<(Transform hub, float radius)> _hubs = new List<(Transform, float)>();
        CarModel _carModel;
        CarAssets _carAssets;
        float _groundOffset;
    }
}
