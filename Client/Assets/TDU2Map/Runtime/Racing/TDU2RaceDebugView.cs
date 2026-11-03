using System;
using System.Collections.Generic;
using System.IO;
using TDU2.Interior;
using TDU2.UI;
using TDU2.Vehicles;
using UnityEngine;


#if UNITY_EDITOR
#endif

namespace TDU2.Racing
{
    public enum RaceGroup { RacingSchool, Championship, LicensedEvent, FreeRace }

    /// <summary>
    /// Race debug view for the developer menu: catalog of all .ig2 races, live race details, 3D route/gate/decor/
    /// opponent visualisation in the Scene view and in the running game, plus start and teleport helpers.
    /// The runtime overlay uses <see cref="TDU2DebugLines"/> (GL lines) so it also works in a built player, where
    /// Gizmos are not rendered.
    /// </summary>
    public sealed class TDU2RaceDebugView : MonoBehaviour
    {
        static TDU2RaceDebugView _instance;
        public static TDU2RaceDebugView Instance => _instance;

        public static TDU2RaceDebugView Ensure()
        {
            if (_instance != null) return _instance;
#if UNITY_2023_1_OR_NEWER
            _instance = FindFirstObjectByType<TDU2RaceDebugView>();
#else
            _instance = FindObjectOfType<TDU2RaceDebugView>();
#endif
            if (_instance == null)
            {
                var go = new GameObject("TDU2 Race Debug");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<TDU2RaceDebugView>();
            }
            return _instance;
        }

        [Header("Show in the 3D world")]
        public bool ShowInWorld = true;
        [Tooltip("Also show the race info panel and 3D labels while playing.")]
        public bool ShowOverlay = true;
        public bool ShowRoute = true;
        [Tooltip("Use the road-following route (RaceRoadPath) instead of the straight checkpoint line.")]
        public bool ShowRoadRoute = true;
        public bool ShowGates = true;
        public bool ShowDecor = true;
        public bool ShowOpponents = true;
        public bool ShowLabels = true;
        [Tooltip("Objects farther away than this from the camera are not drawn (m).")]
        public float ViewDistance = 900f;
        [Tooltip("World labels are only drawn up to this distance (m).")]
        public float LabelDistance = 250f;

        TDU2VehicleManager _mgr;
        TDU2MapStreamer _streamer;
        RaceRepository _repo;
        string _bnkDir;
        string _selectedName;
        RaceDefinition _selected;
        string _status = "not loaded";

        readonly List<string> _files = new List<string>();
        readonly Dictionary<string, LicenseInfo> _license = new Dictionary<string, LicenseInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, RaceGroup> _group = new Dictionary<string, RaceGroup>(StringComparer.OrdinalIgnoreCase);

        GUIStyle _panelStyle, _labelStyle, _titleStyle;
        Texture2D _bg;

        public bool Loaded => _repo != null && _files.Count > 0;
        public string Status => _status;
        public string SelectedName => _selectedName;
        public RaceDefinition Selected => _selected;
        public IReadOnlyList<string> Files => _files;

        public RaceGroup SelectedGroup =>
            _selectedName != null && _group.TryGetValue(_selectedName, out var g) ? g : RaceGroup.FreeRace;

        public string SelectedGroupName => GroupName(SelectedGroup);

        public static string GroupName(RaceGroup g)
        {
            switch (g)
            {
                case RaceGroup.RacingSchool: return "RACING SCHOOL";
                case RaceGroup.Championship: return "CHAMPIONSHIP";
                case RaceGroup.LicensedEvent: return "LICENSED EVENT";
                default: return "FREE RACE / SPECIAL";
            }
        }

        TDU2VehicleManager Manager
        {
            get
            {
                if (_mgr == null)
                {
#if UNITY_2023_1_OR_NEWER
                    _mgr = FindFirstObjectByType<TDU2VehicleManager>();
#else
                    _mgr = FindObjectOfType<TDU2VehicleManager>();
#endif
                }
                return _mgr;
            }
        }

        TDU2MapStreamer Streamer
        {
            get
            {
                var m = Manager;
                if (m != null && m.Streamer != null) return m.Streamer;
                if (_streamer == null)
                {
#if UNITY_2023_1_OR_NEWER
                    _streamer = FindFirstObjectByType<TDU2MapStreamer>();
#else
                    _streamer = FindObjectOfType<TDU2MapStreamer>();
#endif
                }
                return _streamer;
            }
        }

        string BnkDir
        {
            get
            {
                var m = Manager;
                if (m != null && !string.IsNullOrEmpty(m.BnkDir)) return m.BnkDir;
                var s = Streamer;
                if (s != null && s.Index != null && !string.IsNullOrEmpty(s.Index.BnkDir)) return s.Index.BnkDir;
                string guess = Path.Combine(Application.streamingAssetsPath, "Euro", "Bnk");
                return Directory.Exists(guess) ? guess : null;
            }
        }

        void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_bg != null) Destroy(_bg);
        }

        // ------------------------------------------------------------------ catalog

        public void Refresh()
        {
            _selected = null;
            _selectedName = null;
            _files.Clear();
            _license.Clear();
            _group.Clear();

            string dir = BnkDir;
            if (string.IsNullOrEmpty(dir)) { _repo = null; _status = "no vehicle manager / bnk folder yet"; return; }

            if (_repo == null || !string.Equals(dir, _bnkDir, StringComparison.OrdinalIgnoreCase))
            {
                _bnkDir = dir;
                try { _repo = RaceRepository.Load(dir); }
                catch (Exception ex) { _repo = null; _status = "loading failed: " + ex.Message; return; }
            }

            try { RaceTexts.Ensure(dir, TDU2PauseMenu.SavedLanguage()); } catch (Exception) { }

            string ig2 = Path.Combine(dir, "ig2");
            if (!Directory.Exists(ig2)) { _status = "ig2 folder not found: " + ig2; return; }

            var arr = Directory.GetFiles(ig2, "*.ig2");
            Array.Sort(arr, StringComparer.OrdinalIgnoreCase);
            foreach (var p in arr) _files.Add(Path.GetFileNameWithoutExtension(p));

            if (_repo.Database != null)
            {
                foreach (var lic in _repo.Database.Licenses)
                {
                    if (lic == null) continue;
                    foreach (var cf in lic.ChallengeFiles)
                    {
                        string key = Path.GetFileNameWithoutExtension(cf ?? "");
                        if (key.Length > 0 && !_license.ContainsKey(key)) _license[key] = lic;
                    }
                }
            }

            foreach (var name in _files)
            {
                _license.TryGetValue(name, out var lic);
                _group[name] = lic == null ? RaceGroup.FreeRace
                    : lic.IsRacingSchool ? RaceGroup.RacingSchool
                    : lic.IsChampionship ? RaceGroup.Championship
                    : RaceGroup.LicensedEvent;
            }

            _status = _files.Count + " .ig2 files · " + (_repo.Database?.Licenses.Count ?? 0) + " licenses";
        }

        public List<string> FilesInGroup(RaceGroup g)
        {
            var list = new List<string>();
            foreach (var n in _files) if (_group.TryGetValue(n, out var f) && f == g) list.Add(n);
            return list;
        }

        public string HintFor(string file)
        {
            if (_license.TryGetValue(file, out var lic) && lic != null) return lic.DisplayName;
            return "";
        }

        public bool Select(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (_repo == null) Refresh();
            if (_repo == null) { _status = "race data not available"; return false; }
            _license.TryGetValue(name, out var lic);
            try { _selected = _repo.Build(name, lic); }
            catch (Exception ex) { _selected = null; _status = "error in " + name + ": " + ex.Message; return false; }
            if (_selected == null) { _selected = null; _status = name + ".ig2 could not be parsed"; return false; }
            _selectedName = name;
            ShowInWorld = true;
            _status = "selected " + name;
            return true;
        }

        /// <summary>Select the next (+1) or previous (-1) .ig2 file in the catalog.</summary>
        public bool Cycle(int direction)
        {
            if (_files.Count == 0) { Refresh(); if (_files.Count == 0) return false; }
            int i = _selectedName != null ? _files.IndexOf(_selectedName) : -1;
            i = ((i + direction) % _files.Count + _files.Count) % _files.Count;
            return Select(_files[i]);
        }

        /// <summary>Select the next/previous file inside the current group.</summary>
        public bool CycleGroup(int direction)
        {
            var list = FilesInGroup(SelectedGroup);
            if (list.Count == 0) return false;
            int i = _selectedName != null ? list.IndexOf(_selectedName) : -1;
            i = ((i + direction) % list.Count + list.Count) % list.Count;
            return Select(list[i]);
        }

        // ------------------------------------------------------------------ start pose / actions

        public bool TryGetStartPose(out Vector3 worldPos, out Quaternion worldRot)
        {
            worldPos = Vector3.zero;
            worldRot = Quaternion.identity;
            var def = _selected;
            if (def == null) return false;
            if (def.Checkpoints.Count == 0 && def.StartPosition.sqrMagnitude < 1f) return false;

            var root = Streamer != null ? Streamer.MapRoot : null;
            Vector3 start = def.StartPosition;
            Vector3 f = def.Start != null ? def.Start.Forward : new Vector3(Mathf.Sin(def.StartHeading), 0f, Mathf.Cos(def.StartHeading));

            Vector3 aim = Vector3.zero;
            foreach (int k in def.TargetSequence())
            {
                if (k < 0 || k >= def.Checkpoints.Count) continue;
                var d = def.Checkpoints[k].Position - start; d.y = 0f;
                if (d.sqrMagnitude > 225f) { aim = d; break; }
            }
            float ang = aim != Vector3.zero ? Vector3.Angle(new Vector3(f.x, 0f, f.z), aim) : 0f;
            if (ang > 100f) f = -f;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;

            worldPos = root != null ? root.TransformPoint(start) : start;
            var wf = root != null ? root.TransformVector(f.normalized) : f.normalized;
            wf.y = 0f;
            if (wf.sqrMagnitude < 1e-6f) wf = Vector3.forward;
            worldRot = Quaternion.LookRotation(wf.normalized, Vector3.up);
            return true;
        }

        public string StartWorldText()
        {
            if (!TryGetStartPose(out var p, out _)) return "no start position";
            return p.x.ToString("0") + " / " + p.y.ToString("0") + " / " + p.z.ToString("0");
        }

        public string StartRace()
        {
            var m = Manager;
            if (m == null) return "no vehicle manager in the scene";
            if (_selected == null) return "no race selected";
            if (_selected.Checkpoints.Count == 0) return "race has no checkpoints";

            var menu = FindMenu(m);
            if (menu == null) return "could not create the racing school menu";
            if (!menu.StartTestDirect(_selected)) return "race could not be started (data not loaded yet?)";
            return "starting " + _selected.Title;
        }

        static RacingSchoolMenu FindMenu(TDU2VehicleManager m)
        {
#if UNITY_2023_1_OR_NEWER
            var menu = FindFirstObjectByType<RacingSchoolMenu>();
#else
            var menu = FindObjectOfType<RacingSchoolMenu>();
#endif
            if (menu == null)
            {
                var go = new GameObject("TDU2 Racing School");
                go.transform.SetParent(m.transform, false);
                menu = go.AddComponent<RacingSchoolMenu>();
                menu.Manager = m;
                menu.Interiors = m.GetComponent<TDU2Interiors>();
            }
            else
            {
                if (menu.Manager == null) menu.Manager = m;
                if (menu.Interiors == null) menu.Interiors = m.GetComponent<TDU2Interiors>();
            }
            return menu;
        }

        public string TeleportCarToStart()
        {
            var m = Manager;
            if (m == null) return "no vehicle manager in the scene";
            if (_selected == null) return "no race selected";
            if (!TryGetStartPose(out var pos, out var rot)) return "race has no start position";

            pos.y = m.GroundHeight(pos) + 0.6f;
            if (m.Current != null)
            {
                m.TeleportSafe(pos, rot);
            }
            else
            {
                var car = m.FindCar(m.StartCar);
                if (car == null) return "no car available";
                m.SpawnCar(car, pos, rot);
            }
            MoveCamera(pos, rot, 8f, 3f);
            return "car teleported to " + _selected.Title;
        }

        public string MoveCameraToStart(bool freeCamera)
        {
            if (_selected == null) return "no race selected";
            if (!TryGetStartPose(out var pos, out var rot)) return "race has no start position";
            MoveCamera(pos, rot, 10f, 4f);
            if (freeCamera && TDU2FlyCameraTool.Ensure().Begin()) return "free camera at " + _selected.Title + " (F4 to exit)";
            return "camera moved to " + _selected.Title;
        }

        static void MoveCamera(Vector3 pos, Quaternion rot, float back, float up)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 forward = rot * Vector3.forward;
            Vector3 eye = pos - forward * back + Vector3.up * up;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation((pos + Vector3.up * 1.5f - eye).normalized, Vector3.up));
        }

        public string ComputeRoute()
        {
            if (_selected == null) return "no race selected";
            try
            {
                RaceRoadPath.Compute(_selected);
                int n = _selected.RoadPath != null ? _selected.RoadPath.Count : 0;
                return n > 1 ? "road route: " + n + " points" : "no road route found (using checkpoint line)";
            }
            catch (Exception ex) { return "route failed: " + ex.Message; }
        }

        // ------------------------------------------------------------------ Gizmos (editor Scene view)

        void OnDrawGizmos()
        {
            if (!ShowInWorld || _selected == null) return;
            var root = Streamer != null ? Streamer.MapRoot : null;
            var cam = Camera.current;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            bool cull = cam != null;

            Vector3 W(Vector3 p) => root != null ? root.TransformPoint(p) : p;
            bool Near(Vector3 p) => !cull || Vector3.Distance(camPos, p) <= ViewDistance;

            var def = _selected;

            if (ShowRoute && def.Checkpoints.Count > 1)
            {
                if (ShowRoadRoute && def.RoadPath != null && def.RoadPath.Count > 1)
                {
                    int stride = Mathf.Max(1, def.RoadPath.Count / 400);
                    for (int i = 0; i + stride < def.RoadPath.Count; i += stride)
                    {
                        var a = W(def.RoadPath[i]); var b = W(def.RoadPath[Mathf.Min(i + stride, def.RoadPath.Count - 1)]);
                        if (!Near(a) && !Near(b)) continue;
                        float t = i / (float)Mathf.Max(1, def.RoadPath.Count - 1);
                        Gizmos.color = Color.Lerp(new Color(0.2f, 1f, 1f, 0.9f), new Color(1f, 0.85f, 0.2f, 0.9f), t);
                        Gizmos.DrawLine(a, b);
                    }
                }
                else
                {
                    Gizmos.color = new Color(0.2f, 1f, 1f, 0.9f);
                    for (int i = 1; i < def.Checkpoints.Count; i++)
                    {
                        var a = W(def.Checkpoints[i - 1].Position);
                        var b = W(def.Checkpoints[i].Position);
                        if (Near(a) || Near(b)) Gizmos.DrawLine(a, b);
                    }
                }
            }

            if (ShowGates)
            {
                foreach (var cp in def.Checkpoints)
                {
                    var c = W(cp.Position);
                    if (!Near(c)) continue;
                    float half = Mathf.Max(2.5f, cp.Width * 0.5f);
                    float height = cp.IsBrakingZone ? 2f : 5f;
                    Gizmos.color = cp.IsStart ? Color.yellow : cp.IsFinish ? new Color(0.3f, 1f, 0.3f) : cp.IsBrakingZone ? new Color(1f, 0.35f, 0.2f) : Color.white;
                    for (int s = 0; s <= 4; s++)
                    {
                        float a0 = (s - 1) / 2f;   // -1, -0.5, 0, 0.5, 1
                        var p0 = root != null ? root.TransformPoint(cp.Position + cp.Right * half * a0) : cp.Position + cp.Right * half * a0;
                        Gizmos.DrawLine(p0, p0 + Vector3.up * height);
                        if (s < 4)
                        {
                            float a1 = (s) / 2f;
                            var p1 = root != null ? root.TransformPoint(cp.Position + cp.Right * half * a1) : cp.Position + cp.Right * half * a1;
                            Gizmos.DrawLine(p0 + Vector3.up * height, p1 + Vector3.up * height);
                            Gizmos.DrawLine(p0, p1);
                        }
                    }
                    Gizmos.color = new Color(1f, 1f, 1f, 0.45f);
                    Gizmos.DrawRay(c, root != null ? root.TransformVector(cp.Forward).normalized * 6f : cp.Forward * 6f);
                }
            }

            if (ShowDecor)
            {
                int n = 0;
                foreach (var d in def.Decors)
                {
                    if (n++ > 600) break;
                    var a = W(d.From);
                    if (!Near(a)) continue;
                    Gizmos.color = d.IsSeparator ? new Color(1f, 0.5f, 0.1f) : new Color(1f, 0.85f, 0.2f);
                    if (d.IsLine)
                    {
                        var b = W(d.To);
                        Gizmos.DrawLine(a, b);
                        Gizmos.DrawLine(a + Vector3.up * 0.8f, b + Vector3.up * 0.8f);
                    }
                    else Gizmos.DrawWireSphere(a + Vector3.up * 0.4f, 0.4f);
                }
            }

            if (ShowOpponents && def.Opponents.Count > 0)
            {
                foreach (var o in def.Opponents)
                {
                    var p = W(o.Position);
                    if (!Near(p)) continue;
                    Gizmos.color = new Color(1f, 0.3f, 0.3f);
                    Gizmos.DrawWireSphere(p + Vector3.up * 0.9f, 1.1f);
                    var f = root != null ? root.TransformVector(o.Forward).normalized : o.Forward;
                    Gizmos.DrawRay(p + Vector3.up * 0.9f, f * 4f);
                }
            }

            // Start arrow (thick enough to spot from the air).
            if (TryGetStartPose(out var start, out var rot))
            {
                var fwd = rot * Vector3.forward;
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(start, start + fwd * 12f);
                Gizmos.DrawLine(start + fwd * 12f, start + fwd * 9f + rot * Vector3.right * 2.5f);
                Gizmos.DrawLine(start + fwd * 12f, start + fwd * 9f - rot * Vector3.right * 2.5f);
            }

#if UNITY_EDITOR
            if (ShowLabels)
            {
                UnityEditor.Handles.color = Color.white;
                if (TryGetStartPose(out var sp, out _)) UnityEditor.Handles.Label(sp + Vector3.up * 6f, def.Title + "  (" + GroupName(SelectedGroup) + ")");
                int shown = 0;
                foreach (var cp in def.Checkpoints)
                {
                    if (shown++ > 40) break;
                    var p = W(cp.Position);
                    if (cull && Vector3.Distance(camPos, p) > LabelDistance) continue;
                    UnityEditor.Handles.Label(p + Vector3.up * 5.5f, (cp.IsStart ? "START" : cp.IsFinish ? "FINISH" : "CP " + cp.Index) + "\n" + cp.Width.ToString("0") + " m");
                }
            }
#endif
        }

        // ------------------------------------------------------------------ runtime overlay (builds)

        void OnGUI()
        {
            if (!ShowInWorld || !ShowOverlay || _selected == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            EnsureStyles();
            float k = Screen.height / 1080f;
            GUI.depth = -800;

            if (Event.current.type == EventType.Repaint)
            {
                BuildWorldOverlay(cam);
                TDU2DebugLines.Draw();
                if (ShowLabels) DrawWorldLabels(cam, k);
            }

            DrawPanel(k);
        }

        void EnsureStyles()
        {
            if (_panelStyle != null) return;
            _panelStyle = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Clip };
            _panelStyle.font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 16);
            _labelStyle = new GUIStyle(_panelStyle) { alignment = TextAnchor.MiddleCenter };
            _titleStyle = new GUIStyle(_panelStyle) { fontStyle = FontStyle.Bold };
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            _bg.Apply();
        }

        void BuildWorldOverlay(Camera cam)
        {
            TDU2DebugLines.Clear();
            var def = _selected;
            var root = Streamer != null ? Streamer.MapRoot : null;
            Vector3 camPos = cam.transform.position;

            Vector3 W(Vector3 p) => root != null ? root.TransformPoint(p) : p;
            bool Near(Vector3 p) => Vector3.Distance(camPos, p) <= ViewDistance;

            if (ShowRoute && def.Checkpoints.Count > 1)
            {
                if (ShowRoadRoute && def.RoadPath != null && def.RoadPath.Count > 1)
                {
                    int stride = Mathf.Max(1, def.RoadPath.Count / 300);
                    for (int i = 0; i + stride < def.RoadPath.Count; i += stride)
                    {
                        var a = W(def.RoadPath[i]);
                        var b = W(def.RoadPath[Mathf.Min(i + stride, def.RoadPath.Count - 1)]);
                        if (!Near(a) && !Near(b)) continue;
                        float t = i / (float)Mathf.Max(1, def.RoadPath.Count - 1);
                        TDU2DebugLines.Line(a, b, Color.Lerp(new Color(0.2f, 1f, 1f, 0.8f), new Color(1f, 0.85f, 0.2f, 0.8f), t));
                    }
                }
                else
                {
                    for (int i = 1; i < def.Checkpoints.Count; i++)
                    {
                        var a = W(def.Checkpoints[i - 1].Position);
                        var b = W(def.Checkpoints[i].Position);
                        if (Near(a) || Near(b)) TDU2DebugLines.Line(a, b, new Color(0.2f, 1f, 1f, 0.8f));
                    }
                }
            }

            if (ShowGates)
            {
                foreach (var cp in def.Checkpoints)
                {
                    var c = W(cp.Position);
                    if (!Near(c)) continue;
                    var rot = Quaternion.LookRotation(root != null ? root.TransformVector(cp.Forward).normalized : cp.Forward, Vector3.up);
                    Color col = cp.IsStart ? Color.yellow : cp.IsFinish ? new Color(0.3f, 1f, 0.3f) : cp.IsBrakingZone ? new Color(1f, 0.35f, 0.2f) : Color.white;
                    TDU2DebugLines.Box(c + Vector3.up * (cp.IsBrakingZone ? 1f : 2.5f), rot,
                        new Vector3(Mathf.Max(5f, cp.Width), cp.IsBrakingZone ? 2f : 5f, 0.3f), col);
                    TDU2DebugLines.Arrow(c + Vector3.up * 0.4f, rot * Vector3.forward, 6f, col, 1.2f);
                }
            }

            if (ShowDecor)
            {
                int n = 0;
                foreach (var d in def.Decors)
                {
                    if (n++ > 500) break;
                    var a = W(d.From);
                    if (!Near(a)) continue;
                    var col = d.IsSeparator ? new Color(1f, 0.5f, 0.1f, 0.9f) : new Color(1f, 0.85f, 0.2f, 0.9f);
                    if (d.IsLine) TDU2DebugLines.Line(a, W(d.To), col);
                    else TDU2DebugLines.Circle(a + Vector3.up * 0.2f, 0.5f, col, 8);
                }
            }

            if (ShowOpponents)
            {
                foreach (var o in def.Opponents)
                {
                    var p = W(o.Position);
                    if (!Near(p)) continue;
                    TDU2DebugLines.Circle(p, 1.2f, new Color(1f, 0.3f, 0.3f, 0.9f), 12);
                    TDU2DebugLines.Ray(p + Vector3.up * 0.2f, root != null ? root.TransformVector(o.Forward) : o.Forward, 4f, new Color(1f, 0.3f, 0.3f, 0.9f));
                }
            }

            if (TryGetStartPose(out var start, out var rot2))
            {
                var fwd = rot2 * Vector3.forward;
                TDU2DebugLines.Arrow(start + Vector3.up * 0.3f, fwd, 12f, Color.yellow, 2.5f);
                TDU2DebugLines.Circle(start + Vector3.up * 0.3f, 3f, Color.yellow, 24);
            }
        }

        void DrawWorldLabels(Camera cam, float k)
        {
            _labelStyle.fontSize = Mathf.Max(9, Mathf.RoundToInt(13f * k));
            float lh = _labelStyle.fontSize * 1.2f;
            var def = _selected;
            var root = Streamer != null ? Streamer.MapRoot : null;
            Vector3 ToWorld(Vector3 p) => root != null ? root.TransformPoint(p) : p;

            bool Project(Vector3 world, out Vector2 screen)
            {
                var sp = cam.WorldToScreenPoint(world);
                screen = new Vector2(sp.x, Screen.height - sp.y);
                return sp.z > 0f && sp.x > -50f && sp.x < Screen.width + 50f && sp.y > -50f && sp.y < Screen.height + 50f;
            }

            if (TryGetStartPose(out var sp0, out _) && Project(sp0 + Vector3.up * 5f, out var s0))
            {
                _labelStyle.normal.textColor = Color.yellow;
                string startTxt = def.Title + "  ·  START";
                float sw = _labelStyle.CalcSize(new GUIContent(startTxt)).x + 8f * k;
                GUI.Label(new Rect(s0.x - sw * 0.5f, s0.y - lh, sw, lh), startTxt, _labelStyle);
            }

            int shown = 0;
            foreach (var cp in def.Checkpoints)
            {
                if (shown >= 30) break;
                var wp = ToWorld(cp.Position);
                if (Vector3.Distance(cam.transform.position, wp) > LabelDistance) continue;
                if (!Project(wp + Vector3.up * 5f, out var s)) continue;
                shown++;
                _labelStyle.normal.textColor = cp.IsStart ? Color.yellow : cp.IsFinish ? new Color(0.4f, 1f, 0.4f) : Color.white;
                string txt = (cp.IsStart ? "START" : cp.IsFinish ? "FINISH" : "CP " + cp.Index) + "  " + cp.Width.ToString("0") + " m";
                float tw = _labelStyle.CalcSize(new GUIContent(txt)).x + 8f * k;
                GUI.Label(new Rect(s.x - tw * 0.5f, s.y - lh * 0.5f, tw, lh), txt, _labelStyle);
            }

            shown = 0;
            foreach (var o in def.Opponents)
            {
                if (shown++ >= 12) break;
                var wp = ToWorld(o.Position);
                if (Vector3.Distance(cam.transform.position, wp) > LabelDistance) continue;
                if (!Project(wp + Vector3.up * 2.5f, out var s)) continue;
                _labelStyle.normal.textColor = new Color(1f, 0.5f, 0.5f);
                string txt = "CAR #" + o.CarId;
                float tw = _labelStyle.CalcSize(new GUIContent(txt)).x + 8f * k;
                GUI.Label(new Rect(s.x - tw * 0.5f, s.y - lh * 0.5f, tw, lh), txt, _labelStyle);
            }
        }

        void DrawPanel(float k)
        {
            var def = _selected;
            _panelStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(14f * k));
            float lh = _panelStyle.fontSize * 1.35f;

            var lines = new List<string>
            {
                def.Title + "   ·   " + SelectedGroupName,
                "Island " + def.Island + "   Mode " + def.Kind + (def.IsCircuit ? "   Circuit" : "   Point-to-point") + "   Laps " + def.Laps,
                "Length " + def.Length.ToString("0") + " m   CP " + def.Checkpoints.Count + "   Decor " + def.Decors.Count + "   Cars " + def.Opponents.Count,
                "Target " + (def.TargetTimeMs > 0 ? RaceHud.FormatRaceTimeMs(def.TargetTimeMs) : "-") +
                    (def.HasTimeLimit ? "   Limit " + RaceHud.FormatRaceTimeMs(def.TimeLimitMs) : "") +
                    "   Car #" + def.CarId,
                "Start " + StartWorldText(),
                "Route " + (def.RoadPath != null ? def.RoadPath.Count + " pts" : "checkpoint line") + "   View " + ViewDistance.ToString("0") + " m",
            };

            // Size the panel to the longest line so nothing is cut off on the right.
            float natW = _panelStyle.CalcSize(new GUIContent("RACE DEBUG")).x;
            foreach (var s in lines) natW = Mathf.Max(natW, _panelStyle.CalcSize(new GUIContent(s)).x);
            float w = Mathf.Min(Screen.width - 32f * k, natW + 24f * k);
            float h = lh * (lines.Count + 1) + 12f * k;
            var r = new Rect(Screen.width - w - 16f * k, 16f * k, w, h);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            GUI.DrawTexture(r, _bg);
            GUI.color = old;

            float x = r.x + 10f * k, y = r.y + 6f * k;
            _titleStyle.fontSize = _panelStyle.fontSize + Mathf.RoundToInt(1f * k);
            _titleStyle.normal.textColor = new Color(0.85f, 0.6f, 1f);
            GUI.Label(new Rect(x, y, r.width - 20f * k, lh), "RACE DEBUG", _titleStyle);
            y += lh;
            _panelStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            foreach (var s in lines)
            {
                GUI.Label(new Rect(x, y, r.width - 20f * k, lh), s, _panelStyle);
                y += lh;
            }
        }
    }
}
