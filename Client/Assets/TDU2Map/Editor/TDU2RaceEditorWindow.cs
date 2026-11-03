using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDU2.Formats;
using TDU2.Interior;
using TDU2.Racing;
using TDU2.Vehicles;
using TDU2.World;
using UnityEditor;
using UnityEngine;

namespace TDU2.EditorTools
{
    /// <summary>
    /// Window "TDU2/Races (ig2)": load race/test files (Bnk/ig2/*.ig2), show them on the map (Scene view) and
    /// display all details in the Inspector (selection = <see cref="TDU2RaceInfo"/>).
    /// In play mode the route lies exactly on the loaded island (MapRoot); in edit mode the island transform is
    /// reconstructed from the streamer settings (MirrorX/CenterWorld).
    /// </summary>
    public sealed class TDU2RaceEditorWindow : EditorWindow
    {
        const string PrefBnk = "TDU2.RaceEditor.BnkDir";

        string _bnkDir;
        string _filter = "";
        bool _onlySchool;
        Vector2 _scroll;
        string[] _files = Array.Empty<string>();
        RaceRepository _repo;
        CarDatabase _cars;
        string _status = "";

        // Selection
        string _selected;
        RaceDefinition _race;
        Ige2File _ig2;
        LicenseInfo _license;
        TDU2RaceInfo _info;

        // Display
        bool _showRoute = true, _showGates = true, _showDecor = true, _showOpp = true, _showLabels = true;
        static readonly Dictionary<string, Matrix4x4> _islandMatrix = new Dictionary<string, Matrix4x4>();

        [MenuItem("TDU2/Show Races (ig2)")]
        public static void Open() => GetWindow<TDU2RaceEditorWindow>("TDU2 Races");

        void OnEnable()
        {
            _bnkDir = EditorPrefs.GetString(PrefBnk, "");
            if (string.IsNullOrEmpty(_bnkDir) || !Directory.Exists(_bnkDir)) _bnkDir = GuessBnkDir();
            SceneView.duringSceneGui += OnScene;
            Refresh();
        }

        void OnDisable() => SceneView.duringSceneGui -= OnScene;

        static string GuessBnkDir()
        {
            var d = Path.Combine(Application.streamingAssetsPath, "Euro", "Bnk");
            if (Directory.Exists(d)) return d;
            var s = FindStreamer();
            if (s != null && s.Index != null && Directory.Exists(s.Index.BnkDir)) return s.Index.BnkDir;
            return d;
        }

        static TDU2MapStreamer FindStreamer()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<TDU2MapStreamer>();
#else
            return UnityEngine.Object.FindObjectOfType<TDU2MapStreamer>();
#endif
        }

        void Refresh()
        {
            _files = Array.Empty<string>();
            _repo = null;
            if (string.IsNullOrEmpty(_bnkDir) || !Directory.Exists(Path.Combine(_bnkDir, "ig2"))) { _status = "ig2 folder not found"; return; }
            try
            {
                _files = Directory.GetFiles(Path.Combine(_bnkDir, "ig2"), "*.ig2")
                    .Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                _repo = RaceRepository.Load(_bnkDir);
                RaceTexts.Ensure(_bnkDir, EditorPrefs.GetString("TDU2.RaceEditor.Lang", "ge"));
                _status = _files.Length + " files, " + (_repo.Database?.Licenses.Count ?? 0) + " licenses";
            }
            catch (Exception ex) { _status = "Error: " + ex.Message; }
        }

        CarDatabase Cars()
        {
            if (_cars != null) return _cars;
            try
            {
                var f = CarDatabase.FindDatabase(_bnkDir);
                if (f != null) _cars = CarDatabase.Load(f);
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Car database: " + ex.Message); }
            return _cars;
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _bnkDir = EditorGUILayout.TextField("Bnk folder", _bnkDir);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    var p = EditorUtility.OpenFolderPanel("Bnk folder (contains ig2/)", _bnkDir, "");
                    if (!string.IsNullOrEmpty(p)) _bnkDir = p;
                }
                if (EditorGUI.EndChangeCheck()) { EditorPrefs.SetString(PrefBnk, _bnkDir); Refresh(); }
                if (GUILayout.Button("Reload", GUILayout.Width(80))) { _cars = null; Refresh(); }
            }
            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("Filter", _filter);
                _onlySchool = GUILayout.Toggle(_onlySchool, "racing school only", GUILayout.Width(110));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _showRoute = GUILayout.Toggle(_showRoute, "Route");
                _showGates = GUILayout.Toggle(_showGates, "Gates");
                _showDecor = GUILayout.Toggle(_showDecor, "Cones/barriers");
                _showOpp = GUILayout.Toggle(_showOpp, "Vehicles");
                _showLabels = GUILayout.Toggle(_showLabels, "Labels");
                if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
            }

            if (_race != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Show in scene")) FrameRace();
                    if (GUILayout.Button("Inspector")) Selection.activeObject = _info;
                    using (new EditorGUI.DisabledScope(!Application.isPlaying))
                        if (GUILayout.Button(new GUIContent("Start in game", "Play mode: start the test directly (RacingSchoolMenu)"))) StartInGame();
                }
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var f in _files)
            {
                if (_onlySchool && !f.StartsWith("rs", StringComparison.OrdinalIgnoreCase)) continue;
                if (_filter.Length > 0 && f.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool sel = string.Equals(f, _selected, StringComparison.OrdinalIgnoreCase);
                var style = sel ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(f.ToUpperInvariant() + "   " + Hint(f), style)) Select(f);
            }
            EditorGUILayout.EndScrollView();
        }

        string Hint(string file)
        {
            var lic = FindLicense(file);
            return lic != null ? "(" + lic.DisplayName + ")" : "";
        }

        LicenseInfo FindLicense(string file)
        {
            if (_repo?.Database == null) return null;
            foreach (var l in _repo.Database.Licenses)
                foreach (var c in l.ChallengeFiles)
                    if (string.Equals(c, file, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        void Select(string file)
        {
            if (_repo == null) return;
            _selected = file;
            _license = FindLicense(file);
            _ig2 = _repo.LoadIge2(file);
            _race = _repo.Build(file, _license);
            if (_info == null) { _info = CreateInstance<TDU2RaceInfo>(); _info.hideFlags = HideFlags.DontSave; }
            _info.Set(file, _race, _ig2, _license, Cars());
            _info.SelectedCheckpoint = -1;
            Selection.activeObject = _info;
            SceneView.RepaintAll();
            Repaint();
        }

        void StartInGame()
        {
            if (_race == null) return;
#if UNITY_2023_1_OR_NEWER
            var menu = UnityEngine.Object.FindFirstObjectByType<RacingSchoolMenu>();
            var man = UnityEngine.Object.FindFirstObjectByType<TDU2VehicleManager>();
#else
            var menu = UnityEngine.Object.FindObjectOfType<RacingSchoolMenu>();
            var man = UnityEngine.Object.FindObjectOfType<TDU2VehicleManager>();
#endif
            if (man == null) { ShowNotification(new GUIContent("No TDU2VehicleManager in the scene")); return; }
            if (menu == null)
            {
                var go = new GameObject("TDU2 Racing School");
                go.transform.SetParent(man.transform, false);
                menu = go.AddComponent<RacingSchoolMenu>();
                menu.Manager = man;
                menu.Interiors = man.GetComponent<TDU2Interiors>();
            }
            if (!menu.StartTestDirect(_race)) ShowNotification(new GUIContent("Could not start (data not loaded yet?)"));
        }

        // ------------------------------------------------------------------ Scene view

        /// <summary>Island coordinates → world: MapRoot in play mode, reconstructed from the streamer settings otherwise.</summary>
        Matrix4x4 IslandToWorld(out string note)
        {
            note = null;
            var s = FindStreamer();
            if (s != null && s.MapRoot != null) return s.MapRoot.localToWorldMatrix;
            if (s == null) { note = "no TDU2MapStreamer in the scene – island coordinates"; return Matrix4x4.identity; }
            string key = _bnkDir + "|" + s.Island;
            if (!_islandMatrix.TryGetValue(key, out var local))
            {
                local = Matrix4x4.identity;
                try
                {
                    var idx = IslandIndex.Scan(_bnkDir, s.Island);
                    var center = new Vector3(idx.AreasX * WorldLayout.AreaSize * 0.5f, 0, idx.AreasZ * WorldLayout.AreaSize * 0.5f);
                    var pos = s.CenterWorld ? new Vector3(s.MirrorX ? center.x : -center.x, 0f, -center.z) : Vector3.zero;
                    local = Matrix4x4.TRS(pos, Quaternion.identity, s.MirrorX ? new Vector3(-1f, 1f, 1f) : Vector3.one);
                }
                catch (Exception) { }
                _islandMatrix[key] = local;
            }
            note = "Edit mode: position from streamer settings (island " + s.Island + ")";
            return s.transform.localToWorldMatrix * local;
        }

        void FrameRace()
        {
            if (_race == null) return;
            var m = IslandToWorld(out _);
            var b = new Bounds(m.MultiplyPoint3x4(_race.StartPosition), Vector3.one * 10f);
            foreach (var cp in _race.Checkpoints) b.Encapsulate(m.MultiplyPoint3x4(cp.Position));
            b.Expand(30f);
            var sv = SceneView.lastActiveSceneView;
            if (sv != null) { sv.Frame(b, false); sv.Repaint(); }
        }

        static readonly Color ColRoute = new Color(0.2f, 0.9f, 1f, 0.9f);
        static Color GateColor(Ige2GateKind k)
        {
            switch (k)
            {
                case Ige2GateKind.Cones: return new Color(1f, 0.55f, 0.1f);
                case Ige2GateKind.Gate: return new Color(0.3f, 1f, 0.35f);
                case Ige2GateKind.BrakingZone: return new Color(1f, 0.2f, 0.2f);
                default: return new Color(1f, 0.92f, 0.2f);
            }
        }

        void OnScene(SceneView sv)
        {
            if (_race == null) return;
            var m = IslandToWorld(out var note);
            Vector3 W(Vector3 p) => m.MultiplyPoint3x4(p);
            Vector3 D(Vector3 d) => m.MultiplyVector(d);
            var race = _race;
            var oldZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

            // Start
            var sp = W(race.StartPosition);
            var sf = race.Start != null ? D(race.Start.Forward) : D(new Vector3(Mathf.Sin(race.StartHeading), 0f, Mathf.Cos(race.StartHeading)));
            Handles.color = Color.white;
            Handles.DrawWireDisc(sp, Vector3.up, 3f);
            Handles.ArrowHandleCap(0, sp, Quaternion.LookRotation(sf.sqrMagnitude > 1e-6f ? sf : Vector3.forward), 8f, EventType.Repaint);
            if (_showLabels) Handles.Label(sp + Vector3.up * 4f, race.Title + " – " + RaceTexts.Get(race.NameHc, "") + (note != null ? "\n(" + note + ")" : ""), EditorStyles.whiteBoldLabel);

            // Route
            if (_showRoute && race.Checkpoints.Count > 1)
            {
                var seq = race.TargetSequence();
                var pts = new List<Vector3> { sp };
                foreach (int i in seq) pts.Add(W(race.Checkpoints[i].Position) + Vector3.up * 0.3f);
                Handles.color = ColRoute;
                Handles.DrawAAPolyLine(4f, pts.ToArray());
            }

            // Gates
            if (_showGates)
            {
                foreach (var cp in race.Checkpoints)
                {
                    var p = W(cp.Position); var r = D(cp.Right).normalized; var f = D(cp.Forward).normalized;
                    float hw = Mathf.Max(1f, cp.Width * 0.5f);
                    bool sel = _info != null && _info.SelectedCheckpoint == cp.Index;
                    Handles.color = sel ? Color.magenta : GateColor(cp.GateKind);
                    if (cp.IsBrakingZone)
                    {
                        float hd = cp.ZoneDepth * 0.5f;
                        var c = p - f * hd;
                        Handles.DrawSolidRectangleWithOutline(new[] { c - r * hw - f * hd, c + r * hw - f * hd, c + r * hw + f * hd, c - r * hw + f * hd },
                            new Color(1f, 0.2f, 0.2f, 0.15f), Handles.color);
                    }
                    Handles.DrawAAPolyLine(sel ? 8f : 5f, p - r * hw, p + r * hw);
                    Handles.DrawLine(p - r * hw, p - r * hw + Vector3.up * 3f);
                    Handles.DrawLine(p + r * hw, p + r * hw + Vector3.up * 3f);
                    Handles.ArrowHandleCap(0, p, Quaternion.LookRotation(f.sqrMagnitude > 1e-6f ? f : Vector3.forward), 3f, EventType.Repaint);
                    float size = HandleUtility.GetHandleSize(p) * 0.08f;
                    if (Handles.Button(p + Vector3.up * 0.5f, Quaternion.identity, size, size * 1.5f, Handles.SphereHandleCap))
                    {
                        if (_info != null) { _info.SelectedCheckpoint = cp.Index; Selection.activeObject = _info; EditorUtility.SetDirty(_info); }
                        sv.Repaint();
                    }
                    if (_showLabels) Handles.Label(p + Vector3.up * 3.5f, "CP " + cp.Index + " " + cp.GateKind + (cp.IsFinish ? " (finish)" : "") + "  " + cp.Width.ToString("0.#") + " m", EditorStyles.miniBoldLabel);
                }
            }

            // Decor
            if (_showDecor)
            {
                foreach (var d in race.Decors)
                {
                    Handles.color = d.IsSeparator ? new Color(0.9f, 0.15f, 0.1f) : new Color(1f, 0.5f, 0.05f);
                    var a = W(d.From);
                    if (d.IsLine) Handles.DrawAAPolyLine(d.IsSeparator ? 6f : 3f, a, W(d.To));
                    else Handles.DrawSolidDisc(a, Vector3.up, d.IsSeparator ? 0.6f : 0.35f);
                }
            }

            // Vehicles
            if (_showOpp)
            {
                foreach (var o in race.Opponents)
                {
                    var p = W(o.Position); var f = D(o.Forward);
                    Handles.color = new Color(0.3f, 0.5f, 1f);
                    Handles.DrawWireCube(p + Vector3.up * 0.7f, new Vector3(2f, 1.4f, 2f));
                    Handles.ArrowHandleCap(0, p, Quaternion.LookRotation(f.sqrMagnitude > 1e-6f ? f : Vector3.forward), 5f, EventType.Repaint);
                    if (_showLabels) Handles.Label(p + Vector3.up * 2.5f, "#" + o.CarId + "  " + (o.MaxSpeed * 3.6f).ToString("0") + " km/h  from CP " + o.StartCheckpoint, EditorStyles.miniLabel);
                }
            }
            Handles.zTest = oldZ;
        }
    }

    /// <summary>Inspector object of a loaded race (display only).</summary>
    [CustomEditor(typeof(TDU2RaceInfo))]
    public sealed class TDU2RaceInfoEditor : Editor
    {
        bool _cps = true, _decor, _opp = true, _tags;

        public override void OnInspectorGUI()
        {
            var i = (TDU2RaceInfo)target;
            var r = i.Race;
            if (r == null) { EditorGUILayout.HelpBox("No race loaded (window TDU2/Show Races (ig2)).", MessageType.Info); return; }

            EditorGUILayout.LabelField(r.Title + "  " + RaceTexts.Get(r.NameHc, ""), EditorStyles.boldLabel);
            var desc = RaceTexts.Get(r.DescHc, "");
            if (desc.Length > 0) EditorGUILayout.HelpBox(desc, MessageType.None);

            Section("General");
            Row("File", i.File + ".ig2");
            Row("License", i.License != null ? i.License.DisplayName + "  (hash " + i.License.Hash + ", island " + i.License.Island + ", order " + i.License.Order + ")" : "—");
            Row("School", r.School == '\0' ? "—" : r.School == 'A' ? "A Asphalt" : r.School == 'B' ? "B Dirt" : r.School == 'C' ? "C Classic" : r.School.ToString());
            Row("Island (isla)", r.Island == 0 ? "0 Ibiza" : r.Island == 1 ? "1 Hawaii" : r.Island.ToString());
            Row("Mode (gmth)", r.Mode.ToString());
            Row("Test type / place / weather", r.TestType + " / " + r.Place + " / " + r.Weather);
            Row("Graded (TEST)", r.IsGradedTest ? "yes" : "no");

            Section("Times");
            Row("Target (timG)", Ms(r.TargetTimeMs));
            Row("Silver / Bronze", Ms(r.SilverTimeMs) + " / " + Ms(r.BronzeTimeMs));
            Row("Time limit (ChCD/CtDw)", r.HasTimeLimit ? "countdown " + Ms(r.TimeLimitMs) : (r.TimeLimitMs > 0 ? "abort after " + Ms(r.TimeLimitMs) : "—"));
            Row("Penalties (PeLv / offR)", r.PenaltyLevel + " / " + (r.OffroadPenalty ? "offroad" : "—"));
            Row("Reward (rewm)", r.RewardMoney.ToString());

            Section("Vehicle");
            Row("VMod", r.CarId + "  " + i.CarName);
            if (i.CarSticker != null) Row("Sticker", i.CarSticker);

            Section("Route");
            Row("Checkpoints", r.Checkpoints.Count + "  (targets " + r.TargetCount + ")");
            Row("Circuit / laps", (r.IsCircuit ? "yes" : "no") + " / " + r.Laps + " (nblp " + r.LapsRaw + ")");
            Row("Length (leme)", r.Length.ToString("0") + " m");
            Row("Start", r.StartPosition.ToString("F1") + (r.HasSpotStart ? "  (spotworld.bin)" : ""));
            Row("Ends in braking zone", r.EndsInBrakingZone ? "yes" : "no");

            _cps = EditorGUILayout.Foldout(_cps, "Checkpoints (" + r.Checkpoints.Count + ")", true);
            if (_cps)
                foreach (var cp in r.Checkpoints)
                {
                    var st = cp.Index == i.SelectedCheckpoint ? EditorStyles.boldLabel : EditorStyles.label;
                    EditorGUILayout.LabelField($"  {cp.Index,2}  {cp.GateKind,-11} {cp.Width,5:0.0} m  {(cp.OnRoad ? "road" : "open")}{(cp.IsFinish ? "  finish" : "")}", cp.Position.ToString("F1"), st);
                }

            _decor = EditorGUILayout.Foldout(_decor, "Cones/barriers (" + r.Decors.Count + ")", true);
            if (_decor)
                foreach (var d in r.Decors)
                    EditorGUILayout.LabelField("  " + (d.IsSeparator ? "barrier" : "cone") + (d.IsLine ? " (line)" : ""), d.From.ToString("F1") + (d.IsLine ? " → " + d.To.ToString("F1") : ""));

            _opp = EditorGUILayout.Foldout(_opp, "Vehicles (" + r.Opponents.Count + ")", true);
            if (_opp)
                foreach (var o in r.Opponents)
                    EditorGUILayout.LabelField("  #" + o.CarId + " " + i.CarLabel(o.CarId), (o.MaxSpeed * 3.6f).ToString("0") + " km/h, from CP " + o.StartCheckpoint);

            if (i.Ig2 != null)
            {
                _tags = EditorGUILayout.Foldout(_tags, "All ig2 settings (" + i.Ig2.Settings.Count + ")", true);
                if (_tags)
                    foreach (var kv in i.Ig2.Settings.OrderBy(k => k.Key, StringComparer.Ordinal))
                        EditorGUILayout.LabelField("  " + kv.Key, TagText(kv.Value));
            }
        }

        static string TagText(Ige2Tag t)
        {
            if (t == null || t.Data == null) return "—";
            if (t.Data.Length == 4) return t.AsInt + "  (0x" + t.AsUInt.ToString("X8") + ", " + t.AsFloat.ToString("0.###") + ")";
            if (t.Data.Length == 8) return t.AsLong.ToString();
            var s = t.AsText;
            return string.IsNullOrEmpty(s) ? BitConverter.ToString(t.Data, 0, Math.Min(16, t.Data.Length)) : s;
        }

        static void Section(string s) { EditorGUILayout.Space(4); EditorGUILayout.LabelField(s, EditorStyles.miniBoldLabel); }
        static void Row(string k, string v) => EditorGUILayout.LabelField(k, v ?? "");
        static string Ms(int ms) => ms > 0 ? RaceHud.FormatRaceTimeMs(ms) : "—";
    }
}
