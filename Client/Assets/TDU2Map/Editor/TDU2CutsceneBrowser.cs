using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TDU2.Cinematics;
using TDU2.Vehicles;
using TDU2.World;
using UnityEditor;
using UnityEngine;

namespace TDU2.EditorTools
{
    /// <summary>
    /// Fenster "TDU2/Cutscene Browser": alle Banken mit Kinos (scenarics/**, interior/*, Exteriors von Ibiza/Hawaii) durchsuchen,
    /// je Kino Dauer, Eigenschaften (Loop, ohne Kamera), Szenen, Kameras, Darsteller, Nachrichten und Effekt-/Musik-Ereignisse
    /// anzeigen, den passenden <c>TDU2Cutscenes.Play(...)</c>-Aufruf kopieren und im Play-Modus direkt abspielen.
    /// Liest nur die Bank (entities.xmb, .cin, .sce) – nichts wird gebaut.
    /// </summary>
    public sealed class TDU2CutsceneBrowser : EditorWindow
    {
        const string PrefBnk = "TDU2.CutsceneBrowser.BnkDir";

        string _bnkDir, _filter = "", _status = "";
        List<string> _banks = new List<string>();
        Task<List<string>> _scan;
        Vector2 _scrollL, _scrollR;
        string _selBank;
        TDU2CutsceneInfo _info;
        Task<TDU2CutsceneInfo> _read;
        readonly HashSet<string> _open = new HashSet<string>();
        // Abspiel-Optionen
        bool _car = true, _avatar = true, _others, _traffic, _park = true, _skip = true, _loop, _sets = true;
        CutscenePlace _place = CutscenePlace.Auto;
        string _spot = "";

        [MenuItem("TDU2/Cutscene Browser")]
        public static void Open() => GetWindow<TDU2CutsceneBrowser>("TDU2 Cutscenes");

        void OnEnable()
        {
            _bnkDir = EditorPrefs.GetString(PrefBnk, "");
            if (string.IsNullOrEmpty(_bnkDir) || !Directory.Exists(_bnkDir)) _bnkDir = GuessBnkDir();
            StartScan();
        }

        static string GuessBnkDir()
        {
            var d = Path.Combine(Application.streamingAssetsPath, "Euro", "Bnk");
            if (Directory.Exists(d)) return d;
#if UNITY_2023_1_OR_NEWER
            var s = UnityEngine.Object.FindFirstObjectByType<TDU2MapStreamer>();
#else
            var s = UnityEngine.Object.FindObjectOfType<TDU2MapStreamer>();
#endif
            return s != null && s.Index != null && Directory.Exists(s.Index.BnkDir) ? s.Index.BnkDir : d;
        }

        void StartScan()
        {
            if (string.IsNullOrEmpty(_bnkDir) || !Directory.Exists(_bnkDir)) { _status = "Bnk-Ordner nicht gefunden"; return; }
            string bnk = _bnkDir;
            _status = "suche Kino-Banken …";
            _scan = Task.Run(() =>
            {
                var all = new List<string>();
                all.AddRange(TDU2Cutscenes.CinematicBankCandidates(bnk, null));
                try
                {
                    var isl = Path.Combine(bnk, "islands");
                    if (Directory.Exists(isl))
                        foreach (var d in Directory.GetDirectories(isl))
                        {
                            var ext = Path.Combine(d, "exterior");
                            if (!Directory.Exists(ext)) continue;
                            foreach (var f in Directory.GetFiles(ext, "*.bnk"))
                            {
                                var n = Path.GetFileNameWithoutExtension(f);
                                if (!n.Contains("__") && !n.EndsWith("_low", StringComparison.OrdinalIgnoreCase)) all.Add(f);
                            }
                        }
                }
                catch (Exception) { }
                var res = new List<string>();
                foreach (var f in all.Distinct())
                {
                    try
                    {
                        var b = TDU2.IO.BnkArchive.Open(f);
                        foreach (var e in b.Entries) if (e.Extension == ".cin") { res.Add(f); break; }
                    }
                    catch (Exception) { }
                }
                res.Sort((a, c) => string.Compare(Rel(bnk, a), Rel(bnk, c), StringComparison.OrdinalIgnoreCase));
                return res;
            });
        }

        static string Rel(string bnk, string p) => TDU2DebugStage.Rel(bnk, p);

        void Update()
        {
            if (_scan != null && _scan.IsCompleted)
            {
                _banks = _scan.Status == TaskStatus.RanToCompletion ? _scan.Result : new List<string>();
                _scan = null; _status = _banks.Count + " Banken mit Kinos"; Repaint();
            }
            if (_read != null && _read.IsCompleted)
            {
                _info = _read.Status == TaskStatus.RanToCompletion ? _read.Result : null;
                _read = null; Repaint();
            }
            if (Application.isPlaying && TDU2Cutscenes.Current != null) Repaint();
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _bnkDir = EditorGUILayout.TextField("Bnk-Ordner", _bnkDir);
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefBnk, _bnkDir);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    var p = EditorUtility.OpenFolderPanel("TDU2 Bnk-Ordner", _bnkDir, "");
                    if (!string.IsNullOrEmpty(p)) { _bnkDir = p; EditorPrefs.SetString(PrefBnk, p); StartScan(); }
                }
                if (GUILayout.Button("Neu suchen", GUILayout.Width(90))) StartScan();
            }
            _filter = EditorGUILayout.TextField("Filter", _filter);
            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                // links: Banken
                float leftW = Mathf.Max(220f, position.width * 0.34f);
                _rightW = Mathf.Max(200f, position.width - leftW - 30f);
                using (var sv = new EditorGUILayout.ScrollViewScope(_scrollL, GUILayout.Width(leftW)))
                {
                    _scrollL = sv.scrollPosition;
                    foreach (var b in _banks)
                    {
                        string rel = Rel(_bnkDir, b);
                        if (_filter.Length > 0 && rel.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var style = b == _selBank ? EditorStyles.boldLabel : EditorStyles.label;
                        if (GUILayout.Button(rel, style)) Select(b);
                    }
                }
                // rechts: Details
                using (var sv = new EditorGUILayout.ScrollViewScope(_scrollR))
                {
                    _scrollR = sv.scrollPosition;
                    DrawDetails();
                }
            }
        }

        void Select(string bank)
        {
            _selBank = bank; _info = null;
            _read = Task.Run(() => TDU2CutsceneInfo.Read(bank));
        }

        void DrawDetails()
        {
            DrawPlayOptions();
            if (_selBank == null) { EditorGUILayout.HelpBox("Links eine Bank wählen.", MessageType.Info); return; }
            if (_read != null) { EditorGUILayout.LabelField("lese …"); return; }
            if (_info == null) return;
            string stem = Path.GetFileNameWithoutExtension(_info.Bank);
            EditorGUILayout.LabelField(Rel(_bnkDir, _info.Bank), EditorStyles.boldLabel);
            if (_info.Error != null) EditorGUILayout.HelpBox(_info.Error, MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bericht kopieren")) EditorGUIUtility.systemCopyBuffer = _info.Report(_bnkDir);
                if (GUILayout.Button("Bericht speichern …"))
                {
                    var p = EditorUtility.SaveFilePanel("Kino-Bericht", "", stem + "_cutscenes.txt", "txt");
                    if (!string.IsNullOrEmpty(p)) File.WriteAllText(p, _info.Report(_bnkDir));
                }
            }
            EditorGUILayout.Space();
            foreach (var c in _info.Cinematics)
            {
                bool open = _open.Contains(c.Name);
                string head = $"{c.Name}   {c.Duration:0.00} s   {c.Shots} Shots" + (c.Loop ? "   Loop" : "") + (c.NoCamera ? "   ohne Kamera" : "")
                              + (c.UsesPlayerCar ? "   PLAYER_CAR" : "") + (c.UsesAvatar ? "   Avatar" : "");
                bool now = EditorGUILayout.Foldout(open, head, true);
                if (now != open) { if (now) _open.Add(c.Name); else _open.Remove(c.Name); }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(16);
                    string id = stem + ":" + c.Name;
                    if (GUILayout.Button("Aufruf kopieren", GUILayout.Width(110))) EditorGUIUtility.systemCopyBuffer = CallText(id, c);
                    GUI.enabled = Application.isPlaying;
                    if (GUILayout.Button("▶ Abspielen", GUILayout.Width(100))) PlayNow(id);
                    GUI.enabled = true;
                    EditorGUILayout.SelectableLabel(id, GUILayout.Height(18));
                }
                if (!now) continue;
                EditorGUI.indentLevel++;
                Line("Datei", c.File);
                Line("CinProps", "0x" + c.Props.ToString("X") + (c.ShadowConfig.Length > 0 ? "   Schatten " + c.ShadowConfig : ""));
                if (c.Scenes.Count > 0) Line("Szenen", string.Join(", ", c.Scenes));
                if (c.Cameras.Count > 0) Line("Kameras", string.Join(", ", c.Cameras));
                if (c.Actors.Count > 0) Line("Darsteller", string.Join(", ", c.Actors));
                foreach (var m in c.Messages) Line($"Nachricht {m.time:0.00} s", m.text);
                foreach (var m in c.Fx) Line($"Fx {m.time:0.00} s", m.text);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.Space();
            if (_info.Pnjs.Count > 0) Line("Figuren", string.Join(", ", _info.Pnjs.Select(p => p.name + " (" + p.config + ")")));
            if (_info.CinCars.Count > 0) Line("Kino-Autos", string.Join(", ", _info.CinCars));
            if (_info.CameraEntities.Count > 0) Line("Kamera-Entitäten", string.Join(", ", _info.CameraEntities));
            if (_info.Volumes.Count > 0) Line("Volumen", string.Join(", ", _info.Volumes));
        }

        float _rightW = 400f;
        GUIStyle _wrap;

        /// <summary>Beschriftung + umbrechender, markierbarer Text; die Höhe wächst mit dem Inhalt (lange Darsteller-/Kameralisten).</summary>
        void Line(string label, string text)
        {
            if (_wrap == null) _wrap = new GUIStyle(EditorStyles.textArea) { wordWrap = true, richText = false };
            text = string.IsNullOrEmpty(text) ? "-" : text;
            const float labelW = 150f;
            float indent = EditorGUI.indentLevel * 15f;
            float w = Mathf.Max(80f, _rightW - labelW - indent - 24f);   // Scrollbalken + Ränder
            float h = Mathf.Max(18f, _wrap.CalcHeight(new GUIContent(text), w));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelW));
                EditorGUILayout.SelectableLabel(text, _wrap, GUILayout.Width(w), GUILayout.Height(h));
            }
        }

        void DrawPlayOptions()
        {
            EditorGUILayout.LabelField("Abspiel-Optionen", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _car = EditorGUILayout.ToggleLeft("LoadPlayerCar", _car, GUILayout.Width(120));
                _avatar = EditorGUILayout.ToggleLeft("LoadPlayerAvatar", _avatar, GUILayout.Width(130));
                _others = EditorGUILayout.ToggleLeft("ShowOtherPlayers", _others, GUILayout.Width(130));
                _traffic = EditorGUILayout.ToggleLeft("ShowTraffic", _traffic, GUILayout.Width(100));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _park = EditorGUILayout.ToggleLeft("ParkPlayer", _park, GUILayout.Width(120));
                _skip = EditorGUILayout.ToggleLeft("Skippable", _skip, GUILayout.Width(130));
                _loop = EditorGUILayout.ToggleLeft("Loop", _loop, GUILayout.Width(130));
                _sets = EditorGUILayout.ToggleLeft("LoadSets", _sets, GUILayout.Width(100));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _place = (CutscenePlace)EditorGUILayout.EnumPopup("Place", _place);
                _spot = EditorGUILayout.TextField("Spot", _spot);
            }
            if (Application.isPlaying)
            {
                var cur = TDU2Cutscenes.Current;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(cur != null ? cur.Status : "kein Kino aktiv", EditorStyles.miniLabel);
                    GUI.enabled = cur != null;
                    if (GUILayout.Button("■ Stop", GUILayout.Width(70))) TDU2Cutscenes.Stop();
                    GUI.enabled = true;
                }
            }
            else EditorGUILayout.LabelField("Abspielen im Play-Modus (Insel geladen).", EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        CutsceneOptions Options() => new CutsceneOptions
        {
            LoadPlayerCar = _car, LoadPlayerAvatar = _avatar, ShowOtherPlayers = _others, ShowTraffic = _traffic,
            ParkPlayer = _park, Skippable = _skip, Loop = _loop, LoadSets = _sets, Place = _place, Spot = string.IsNullOrEmpty(_spot) ? null : _spot,
            OnFinished = ok => Debug.Log("[TDU2] Cutscene beendet: " + (ok ? "zu Ende gespielt" : "abgebrochen")),
        };

        void PlayNow(string id) => TDU2Cutscenes.Play(id, Options());

        string CallText(string id, TDU2CutsceneInfo.Cin c)
        {
            var o = new List<string>();
            o.Add("LoadPlayerCar = " + (_car && c.UsesPlayerCar ? "true" : "false"));
            o.Add("LoadPlayerAvatar = " + (_avatar && c.UsesAvatar ? "true" : "false"));
            if (_others) o.Add("ShowOtherPlayers = true");
            if (_traffic) o.Add("ShowTraffic = true");
            if (!_park) o.Add("ParkPlayer = false");
            if (!_skip) o.Add("Skippable = false");
            if (_loop) o.Add("Loop = true");
            if (!_sets) o.Add("LoadSets = false");
            if (_place != CutscenePlace.Auto) o.Add("Place = CutscenePlace." + _place);
            if (!string.IsNullOrEmpty(_spot)) o.Add("Spot = \"" + _spot + "\"");
            return $"TDU2Cutscenes.Play(\"{id}\", new CutsceneOptions {{ {string.Join(", ", o)} }});";
        }
    }
}
