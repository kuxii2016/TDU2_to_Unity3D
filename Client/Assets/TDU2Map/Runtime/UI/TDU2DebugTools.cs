using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Avatars;
using TDU2.Formats;
using TDU2.IO;
using TDU2.Online;
using TDU2.Story;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Shared helper for the developer tools (cutscene tester, animation viewer): parks the player's car (removed, controls
    /// suspended, game mode CINE) and restores it afterwards at the same place. Also builds a floor plane for the "sky stage".
    /// </summary>
    public static class TDU2DebugStage
    {
        static int _users;
        static CarPhysics _car;
        static Vector3 _pos; static Quaternion _rot;
        static TDU2VehicleManager _mgr;

        public static bool Active => _users > 0;

        public static void Enter(TDU2VehicleManager mgr)
        {
            if (mgr == null) return;
            if (_users++ > 0) return;
            _mgr = mgr;
            var cur = mgr.Current;
            _car = cur != null ? cur.Car : null;
            if (cur != null) { _pos = cur.transform.position; _rot = cur.transform.rotation; }
            else if (Camera.main != null) { _pos = Camera.main.transform.position; _rot = Quaternion.LookRotation(Flat(Camera.main.transform.forward)); }
            mgr.RemoveCar();
            mgr.Suspended = true;
            TDU2GameMode.Push(Gamemode.GAME_MODE_CINE);
        }

        public static void Exit()
        {
            if (_users <= 0) return;
            if (--_users > 0) return;
            var m = _mgr; _mgr = null;
            if (m == null) return;
            TDU2GameMode.Pop();
            m.Suspended = false;
            if (_car != null)
            {
                var p = _pos; p.y = m.GroundHeight(p) + 0.6f;
                var cam = Camera.main;
                if (cam != null) cam.transform.SetPositionAndRotation(p - _rot * Vector3.forward * 7f + Vector3.up * 2.5f, _rot);
                m.SpawnCar(_car, p, _rot);
            }
            _car = null;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized; }

        /// <summary>Grey floor plane (with collider) of the given size under a transform (local y = 0).</summary>
        public static GameObject Floor(Transform parent, float size = 400f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = "Debug Floor";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, -0.01f, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(size / 10f, 1f, size / 10f);
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            if (r != null && sh != null) r.sharedMaterial = new Material(sh) { color = new Color(0.42f, 0.43f, 0.45f) };
            return go;
        }

        // ------------------------------------------------------------------ bank lists (cached, scanned in the background)
        static List<string> _cineBanks, _animBanks;
        static Task _scan;
        static string _scanIsland;
        public static string ScanState { get; private set; } = "";

        /// <summary>Banks with cinematics (.cin): scenarics/** and the exterior banks of the current island. null = still scanning.</summary>
        public static List<string> CinematicBanks(TDU2VehicleManager mgr) { StartScan(mgr); return _scan != null && _scan.IsCompleted ? _cineBanks : null; }

        /// <summary>Banks with avatar animations (.anm). null = still scanning.</summary>
        public static List<string> AnimationBanks(TDU2VehicleManager mgr) { StartScan(mgr); return _scan != null && _scan.IsCompleted ? _animBanks : null; }

        public static void Rescan() { _scan = null; }

        static void StartScan(TDU2VehicleManager mgr)
        {
            string island = mgr?.Streamer?.Index?.IslandName ?? "";
            if (_scan != null && _scanIsland == island) return;
            if (mgr == null || string.IsNullOrEmpty(mgr.BnkDir)) return;
            _scanIsland = island;
            string bnk = mgr.BnkDir, ext = mgr.Streamer?.Index?.ExteriorDir;
            ScanState = "scanning …";
            _scan = Task.Run(() =>
            {
                var cine = new List<string>(); var anim = new List<string>();
                var files = new List<string>();
                try { var sc = Path.Combine(bnk, "scenarics"); if (Directory.Exists(sc)) files.AddRange(Directory.GetFiles(sc, "*.bnk", SearchOption.AllDirectories)); } catch (Exception) { }
                try { if (ext != null && Directory.Exists(ext)) files.AddRange(Directory.GetFiles(ext, "*.bnk")); } catch (Exception) { }
                try { var it = Path.Combine(bnk, "interior"); if (Directory.Exists(it)) files.AddRange(Directory.GetFiles(it, "*.bnk")); } catch (Exception) { }
                int n = 0;
                foreach (var f in files)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name.Contains("__") || name.EndsWith("_low", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var b = BnkArchive.Open(f);
                        bool hasCin = false, hasAnm = false;
                        foreach (var e in b.Entries)
                        {
                            if (e.Extension == ".cin") hasCin = true;
                            else if (e.Extension == ".anm") hasAnm = true;
                            if (hasCin && hasAnm) break;
                        }
                        if (hasCin) cine.Add(f);
                        if (hasAnm) anim.Add(f);
                    }
                    catch (Exception) { }
                    n++;
                }
                Comparison<string> byName = (a, b2) => string.Compare(Rel(bnk, a), Rel(bnk, b2), StringComparison.OrdinalIgnoreCase);
                cine.Sort(byName); anim.Sort(byName);
                _cineBanks = cine; _animBanks = anim;
                ScanState = $"{cine.Count} cinematic banks, {anim.Count} animation banks ({n} scanned)";
            });
        }

        public static string Rel(string bnkDir, string path)
        {
            if (string.IsNullOrEmpty(bnkDir) || path == null) return path;
            string p = path.Replace('\\', '/'), b = bnkDir.Replace('\\', '/').TrimEnd('/') + "/";
            return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? p.Substring(b.Length) : p;
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Cutscene tester (developer menu): loads any bank with cinematics into a <see cref="TDU2StoryScene"/> and plays its
    /// Entity__Cinematic entries – on a sky stage (floor plane 1500 m above the island), right here in front of the camera, or
    /// at the spot the bank belongs to (exterior banks: nearest spot with that code; story banks: their known story spot).
    /// Player avatar (local look) and the player's current car are cast as "Avatar" / PLAYER_CAR.
    /// </summary>
    [DefaultExecutionOrder(1100)]   // after TDU2StoryScene (1000): free view camera when no cinematic camera runs
    public sealed class TDU2CutsceneTester : MonoBehaviour
    {
        /// <summary>Play the first cinematic with a camera as soon as the bank is loaded.</summary>
        public bool AutoPlay = true;
        bool _showFloor = true;
        /// <summary>Show / hide the debug floor plane of the sky stage.</summary>
        public bool ShowFloor { get => _showFloor; set { _showFloor = value; if (_floor != null) _floor.SetActive(value); } }
        /// <summary>Free view (no cinematic camera running): orbit around the stage.</summary>
        public float ViewAngle = 35f, ViewDistance = 14f, ViewHeight = 5f, ViewSpeed = 8f;
        public enum Stage { Sky, Here, Spot }
        static readonly string[] StageNames = { "Sky stage (1500 m up)", "Here (in front of the camera)", "At the bank's spot" };
        public static string StageName(Stage s) => StageNames[(int)s];

        public static TDU2CutsceneTester Instance { get; private set; }
        public TDU2VehicleManager Manager;
        public Stage Where = Stage.Sky;
        public bool CastAvatar = true, CastCar = true;

        public TDU2StoryScene Scene { get; private set; }
        public string BankPath { get; private set; }
        public string Status { get; private set; } = "idle";
        GameObject _floor, _carGo; CarModel _carModel; Task<CarAssets> _carTask; CarPhysics _carFor;
        bool _cast, _staged;
        string _pending; bool _pendingBg;

        public static TDU2CutsceneTester Get(TDU2VehicleManager mgr)
        {
            if (Instance == null)
            {
                var go = new GameObject("TDU2 Cutscene Tester");
                Instance = go.AddComponent<TDU2CutsceneTester>();
            }
            Instance.Manager = mgr;
            return Instance;
        }

        public bool Active => Scene != null;

        /// <summary>Story banks and the spot the story plays them at (Ibiza).</summary>
        static readonly Dictionary<string, (string spot, bool closed, float alt, bool hide)> KnownSpots = new Dictionary<string, (string, bool, float, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            { "new_intro", ("EPH_L3DI_5067I", false, 0f, true) },
            { "garage", ("EPH_L3DI_5067I", true, 1500f, false) },
            { "meeting_tess", ("E_HOTELA_1266I", false, 0f, false) },
            { "solar_crown_ext", ("ECH_IA_1243I", false, 0f, false) },
            { "solar_crown_int", ("ECH_IA_1243I", true, 0f, false) },
            { "pack_events13", ("ECH_IA_1243I", false, 0f, false) },
        };

        public void Load(string bankPath)
        {
            Unload(false);
            if (Manager == null || string.IsNullOrEmpty(bankPath)) return;
            if (!_staged) { TDU2DebugStage.Enter(Manager); _staged = true; }
            BankPath = bankPath;
            string stem = Path.GetFileNameWithoutExtension(bankPath);
            var setup = new TDU2StoryScene.Setup { Bank = bankPath, LoadUi = false, IntroUi = false };
            var st = Manager.Streamer;
            var where = Where; bool fellBack = false;
            if (where == Stage.Spot)
            {
                Spot spot = null; bool exterior = st?.Index?.ExteriorDir != null && bankPath.Replace('\\', '/').IndexOf("/exterior/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (exterior) spot = NearestSpot(stem);
                if (spot != null) { setup.SpotRef = spot; setup.Spot = spot.Code; setup.HideSpotInterior = true; }
                else if (KnownSpots.TryGetValue(stem, out var k) && TDU2GamePC.FindSpot(st?.Spots?.Spots, k.spot) != null)
                {
                    setup.Spot = k.spot; setup.Closed = k.closed; setup.ClosedAltitude = k.alt; setup.HideSpotInterior = k.hide;
                    if (stem.Equals("solar_crown_int", StringComparison.OrdinalIgnoreCase)) setup.ExtraBanks = new[] { "interior/ich_ib.bnk" };
                }
                else { where = Stage.Sky; fellBack = true; }
            }
            if (where == Stage.Here && st?.MapRoot != null && Camera.main != null)
            {
                var ct = Camera.main.transform;
                var f = ct.forward; f.y = 0f; if (f.sqrMagnitude < 1e-4f) f = Vector3.forward; f.Normalize();
                var p = ct.position + f * 8f; p.y = Manager.GroundHeight(p);
                setup.UseFrame = true;
                setup.FramePos = st.MapRoot.InverseTransformPoint(p);
                var d = st.MapRoot.InverseTransformDirection(f);
                setup.FrameHeading = Mathf.Atan2(d.x, d.z);
            }
            if (where == Stage.Sky) { setup.Closed = true; setup.ClosedAltitude = 1500f; }
            Scene = TDU2StoryScene.Create(Manager, setup);
            _cast = false;
            Status = "loading " + stem + " (" + StageName(where) + (fellBack ? " – no spot for this bank on this island" : "") + ")";
            _skyFloor = where == Stage.Sky;
            if (CastCar) LoadCar();
        }

        bool _skyFloor;

        Spot NearestSpot(string code)
        {
            var st = Manager.Streamer;
            if (st?.Spots == null || st.MapRoot == null) return null;
            var g = st.MapRoot.InverseTransformPoint(Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            Spot best = null; float bd = float.MaxValue;
            foreach (var s in st.Spots.Spots)
            {
                if (!string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)) continue;
                float d = (s.Position - g).sqrMagnitude;
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        void LoadCar()
        {
            CarPhysics car = null;
            var save = TDU2Session.Save;
            if (save != null && !string.IsNullOrEmpty(save.currentCar)) car = Manager.FindCar(save.currentCar);
            if (car == null) car = Manager.FindCar(Manager.StartCar) ?? (Manager.Available.Count > 0 ? Manager.Available[0] : null);
            if (car == null) return;
            _carFor = car;
            int color = 0, rim = 0, interior = 0;
            if (TDU2CarChoices.TryGet(car.FileName, out var ch)) { color = ch.Color; rim = ch.Rim; interior = ch.Interior; }
            string bnk = Manager.BnkDir; var db = Manager.Database;
            _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, false, rim, interior));
        }

        public void Play(string name, bool background)
        {
            if (Scene == null) return;
            if (!Scene.Loaded) { _pending = name; _pendingBg = background; Status = "queued " + name; return; }
            bool ok = background ? Scene.PlayBackground(name, false) : Scene.PlayMain(name);
            Status = ok ? (background ? "background " : "playing ") + name : name + " failed";
        }

        public void StopAll() { if (Scene == null) return; Scene.StopMain(); Scene.StopBackground(); Scene.StopMusic(); Status = "stopped"; }
        public void Skip() { Scene?.SkipMain(); }

        void Update()
        {
            if (Scene == null) return;
            if (Scene.Failed) { Status = "failed: " + Scene.Error; Unload(false); return; }
            if (_carTask != null && _carTask.IsCompleted)
            {
                var t = _carTask; _carTask = null;
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                {
                    try
                    {
                        _carGo = new GameObject("Tester Car " + _carFor.ModelName);
                        _carModel = CarModel.Build(t.Result, _carGo.transform);
                        TDU2WheelSpin.Attach(_carGo, _carModel, t.Result);
                        try { TDU2StickerLibrary.Ensure(Manager.BnkDir); TDU2LiveryView.ApplyOwn(_carModel, _carFor.FileName); } catch (Exception) { }
                        _cast = false;
                    }
                    catch (Exception ex) { DebugManager.Warning("[TDU2] Cutscene tester: car: " + ex.Message); }
                }
            }
            if (!Scene.Loaded) return;
            if (!_cast)
            {
                _cast = true;
                if (_skyFloor && _floor == null && Scene.Holder != null) { _floor = TDU2DebugStage.Floor(Scene.Holder, 600f); _floor.SetActive(_showFloor); }
                if (CastAvatar)
                {
                    string cfg = AvatarConfigs.Local != null && AvatarConfigs.Local.Sex == 1 ? "AVT_Female_Intro" : "AVT_Male_Intro";
                    Scene.SetPlayerAvatar(cfg, AvatarConfigs.Local);
                }
                if (_carGo != null) Scene.SetCar(_carGo.transform, null, "PLAYER_CAR");
                if (Status.StartsWith("loading")) Status = "ready – " + Scene.Definitions.Count + " cinematics (Enter on a name = play)";
                if (AutoPlay && _pending == null)
                    foreach (var d in Scene.Definitions)
                        if (!d.NoCamera) { _pending = d.Name; _pendingBg = false; break; }
            }
            if (_pending != null) { var n = _pending; _pending = null; Play(n, _pendingBg); }
            if (!string.IsNullOrEmpty(Scene.MainName) && Status.StartsWith("playing")) Status = $"playing {Scene.MainName}  {Scene.MainTime:0.0} / {Scene.MainDuration:0.0} s";
            foreach (var e in Scene.TakeEndEvents()) Status = "ended " + e;
        }

        /// <summary>
        /// Without a running cinematic camera the game camera would stay where the car was – look at the stage instead
        /// (slow orbit around the scene frame).
        /// </summary>
        void LateUpdate()
        {
            if (Scene == null || Scene.Holder == null) return;
            if (Scene.Loaded && Scene.CinematicCamera(out _, out _, out _)) return;   // the cinematic owns the camera
            var cam = Camera.main;
            if (cam == null) return;
            ViewAngle += ViewSpeed * Time.unscaledDeltaTime;
            var c = Scene.Holder.position + Vector3.up * 1.2f;
            var dir = Quaternion.Euler(0f, ViewAngle, 0f) * Scene.Holder.forward;
            var p = c + dir * ViewDistance + Vector3.up * ViewHeight;
            cam.transform.SetPositionAndRotation(p, Quaternion.LookRotation(c - p, Vector3.up));
        }

        /// <summary>Close the scene (keep = stay staged for the next bank).</summary>
        public void Unload(bool restore = true)
        {
            if (Scene != null) Scene.Dispose();
            Scene = null; BankPath = null; _pending = null;
            if (_floor != null) Destroy(_floor); _floor = null;
            if (_carGo != null) Destroy(_carGo); _carGo = null;
            _carModel?.Destroy(); _carModel = null; _carTask = null;
            if (restore && _staged) { _staged = false; TDU2DebugStage.Exit(); Status = "idle"; }
        }

        void OnDestroy() { Unload(true); if (Instance == this) Instance = null; }
    }

    // =====================================================================================================================
    /// <summary>
    /// Animation viewer (developer menu): an avatar (player look or any DBPNJ figure) on a floor plane high above the island,
    /// camera orbiting around it, playing any .anm clip of avatarsdriver.bnk or of a chosen bank (loop, speed, pause, scrub).
    /// </summary>
    [DefaultExecutionOrder(1100)]
    public sealed class TDU2AnimViewer : MonoBehaviour
    {
        public static TDU2AnimViewer Instance { get; private set; }
        public TDU2VehicleManager Manager;

        public string Model { get; private set; } = "";      // "" = player avatar, else DBPNJ config id
        public string ClipBank { get; private set; } = "";   // "" = avatarsdriver.bnk
        public string ClipName { get; private set; } = "";
        public AnmClip Clip { get; private set; }
        public float Time_, Speed = 1f, Orbit = 30f, Distance = 3.2f, Height = 1.1f, OrbitSpeed = 0f;
        public bool Paused, Loop = true, AutoPlay = true;
        bool _showFloor = true;
        /// <summary>Show / hide the debug floor plane under the model.</summary>
        public bool ShowFloor { get => _showFloor; set { _showFloor = value; if (_floor != null) _floor.SetActive(value); } }
        public bool Active => _root != null;

        Transform _root; GameObject _floor; TDU2Avatar _avatar;
        BnkArchive _bank;
        List<string> _bankClips;

        public static TDU2AnimViewer Get(TDU2VehicleManager mgr)
        {
            if (Instance == null)
            {
                var go = new GameObject("TDU2 Animation Viewer");
                Instance = go.AddComponent<TDU2AnimViewer>();
            }
            Instance.Manager = mgr;
            return Instance;
        }

        public void Open()
        {
            if (_root != null || Manager?.Streamer?.MapRoot == null) return;
            TDU2DebugStage.Enter(Manager);
            _root = new GameObject("Anim Viewer Stage").transform;
            _root.SetParent(Manager.Streamer.MapRoot, false);
            _root.localPosition = new Vector3(0f, 1600f, 0f);
            _floor = TDU2DebugStage.Floor(_root, 60f);
            _floor.SetActive(_showFloor);
            SetModel(Model);
        }

        public void Close()
        {
            if (_root == null) return;
            Destroy(_root.gameObject); _root = null; _avatar = null; _floor = null;
            TDU2DebugStage.Exit();
        }

        public void SetModel(string pnjId)
        {
            Model = pnjId ?? "";
            if (_root == null) return;
            if (_avatar != null) Destroy(_avatar.gameObject);
            var cfg = string.IsNullOrEmpty(Model) ? AvatarConfigs.Local : AvatarLibrary.PnjConfig(Model);
            _avatar = TDU2Avatar.Create(_root, cfg ?? AvatarConfig.Default(), "Viewer " + (string.IsNullOrEmpty(Model) ? "Player" : Model));
        }

        /// <summary>Clip source: "" = avatarsdriver.bnk, else a bank path.</summary>
        public void SetBank(string bankPath)
        {
            ClipBank = bankPath ?? "";
            _bank = null; _bankClips = null;
            if (string.IsNullOrEmpty(ClipBank)) return;
            try { _bank = BnkArchive.Open(ClipBank); } catch (Exception ex) { DebugManager.Warning("[TDU2] Animation viewer: " + ex.Message); }
        }

        public List<string> Clips()
        {
            if (string.IsNullOrEmpty(ClipBank)) return AvatarLibrary.ClipNames();
            if (_bankClips != null) return _bankClips;
            _bankClips = new List<string>();
            if (_bank != null) foreach (var e in _bank.All(".anm")) _bankClips.Add(Path.GetFileNameWithoutExtension(e.Name));
            _bankClips.Sort(StringComparer.OrdinalIgnoreCase);
            return _bankClips;
        }

        public void SetClip(string name)
        {
            ClipName = name ?? "";
            Clip = string.IsNullOrEmpty(ClipName) ? null : string.IsNullOrEmpty(ClipBank) ? AvatarLibrary.Clip(ClipName) : CineScene.LoadClip(_bank, ClipName);
            Time_ = 0f; Paused = !AutoPlay;
        }

        public void Step(int dir)
        {
            var l = Clips();
            if (l.Count == 0) return;
            int i = l.IndexOf(ClipName);
            i = ((i + dir) % l.Count + l.Count) % l.Count;
            SetClip(l[i]);
        }

        void LateUpdate()
        {
            if (_root == null) return;
            float dt = Time.unscaledDeltaTime;
            if (Clip != null && !Paused)
            {
                Time_ += dt * Speed;
                float len = Mathf.Max(0.01f, Clip.Length);
                if (Time_ > len) Time_ = Loop ? Time_ % len : len;
                if (Time_ < 0f) Time_ = Loop ? len + Time_ % len : 0f;
            }
            if (_avatar != null && _avatar.Ready)
            {
                _avatar.SetVisible(true);
                _avatar.BeginPose();
                if (Clip != null) _avatar.ApplyClip(Clip, Time_);
                _avatar.CommitPose();
            }
            Orbit += OrbitSpeed * dt;
            var cam = Camera.main;
            if (cam != null)
            {
                var c = _root.position + Vector3.up * Height;
                var dir = Quaternion.Euler(0f, Orbit, 0f) * Vector3.forward;
                cam.transform.SetPositionAndRotation(c + dir * Distance + Vector3.up * 0.25f, Quaternion.LookRotation(-dir - Vector3.up * 0.08f, Vector3.up));
                cam.fieldOfView = 50f;
            }
        }

        void OnDestroy() { Close(); if (Instance == this) Instance = null; }
    }
}
