using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDU2.Map.Flash;
using TDU2.Map.Vehicles;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>
    /// "TDU2/Flash Debugger": live-Ansicht aller laufenden Flash-Filme (VmfPlayer) im Play-Modus.
    ///   Baum      – Clips/Shapes/Texte mit Frame, Label, Sichtbarkeit, Position; Auswahl wird im Spielbild rot umrahmt,
    ///               Details: Labels anspringen, Frame-Regler, Play/Stop, sichtbar, Position, Textinhalt
    ///   Variablen – alle _root- und Clip-Variablen ("/pfad:name"), filterbar und direkt editierbar
    ///   Funktionen– alle ActionScript-Funktionen, mit Argumenten aufrufbar
    ///   FSCommands– Protokoll der Befehle des Films an das Spiel
    ///   Eingabe   – Move/A/B/X/Y wie das Spiel an den Film schicken
    /// "Film laden…" öffnet zusätzlich eine beliebige Frontend-Bank (hud.bnk, main.bnk …) im Spielbild.
    /// </summary>
    public sealed class TDU2FlashDebugger : EditorWindow
    {
        [MenuItem("TDU2/Flash Debugger")]
        public static void Open() => GetWindow<TDU2FlashDebugger>("Flash Debugger");

        VmfPlayer _player;
        int _tab;
        static readonly string[] Tabs = { "Baum", "Variablen", "Funktionen", "FSCommands", "Eingabe" };
        string _filter = "";
        bool _onlyVisible = true, _onlyClips;
        Vector2 _scrollTree, _scrollDetail, _scrollList;
        readonly HashSet<string> _open = new HashSet<string> { "/" };
        VmfPlayer.DisplayObject _sel;
        string _selPath;
        readonly Dictionary<string, string> _edits = new Dictionary<string, string>();
        readonly Dictionary<string, string> _callArgs = new Dictionary<string, string>();
        readonly List<string> _cmdLog = new List<string>();
        VmfPlayer _logPlayer;
        string _customFn = "", _customArgs = "", _status = "";
        double _nextRepaint;
        bool _autoRefresh = true;

        void OnEnable() { EditorApplication.update += Tick; }
        void OnDisable()
        {
            EditorApplication.update -= Tick;
            Unhook();
            if (_player != null) _player.Highlight = null;
        }

        void Tick()
        {
            if (!_autoRefresh || !EditorApplication.isPlaying) return;
            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.25;
            Repaint();
        }

        void Hook(VmfPlayer p)
        {
            if (_logPlayer == p) return;
            Unhook();
            _logPlayer = p;
            if (p != null) p.FSCommand += OnCmd;
        }

        void Unhook() { if (_logPlayer != null) _logPlayer.FSCommand -= OnCmd; _logPlayer = null; }

        void OnCmd(string cmd, string args)
        {
            _cmdLog.Add($"{Time.unscaledTime,8:0.00}  {cmd}{(string.IsNullOrEmpty(args) ? "" : "  " + args)}");
            if (_cmdLog.Count > 2000) _cmdLog.RemoveRange(0, 500);
        }

        // ------------------------------------------------------------------ Oberfläche
        void OnGUI()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Der Flash Debugger arbeitet im Play-Modus: er zeigt alle laufenden Flash-Filme (Pausemenü, HUD, Händler …).\n" +
                                        "Im Play-Modus kannst du mit \"Film laden…\" auch jede andere Frontend-Bank öffnen.", MessageType.Info);
                return;
            }

            var players = FindObjectsByType<VmfPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Where(p => p.Movie != null).ToArray();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var names = players.Select(Label).ToArray();
                int idx = Array.IndexOf(players, _player);
                int nidx = EditorGUILayout.Popup(idx, names, EditorStyles.toolbarPopup, GUILayout.Width(300));
                if (nidx != idx && nidx >= 0) Select(players[nidx]);
                if (GUILayout.Button("Film laden…", EditorStyles.toolbarButton, GUILayout.Width(90))) LoadMovie();
                GUILayout.FlexibleSpace();
                _autoRefresh = GUILayout.Toggle(_autoRefresh, "Live", EditorStyles.toolbarButton, GUILayout.Width(40));
            }
            if (_player == null && players.Length > 0) Select(players[0]);
            if (_player == null) { EditorGUILayout.HelpBox("Kein Flash-Film aktiv. Öffne z.B. das Pausemenü (ESC) oder lade einen Film.", MessageType.None); return; }
            Hook(_player);

            using (new EditorGUILayout.HorizontalScope())
            {
                _player.Visible = EditorGUILayout.ToggleLeft("Sichtbar", _player.Visible, GUILayout.Width(80));
                _player.MouseEnabled = EditorGUILayout.ToggleLeft("Maus", _player.MouseEnabled, GUILayout.Width(60));
                EditorGUILayout.LabelField("GUI-Tiefe", GUILayout.Width(60));
                _player.GuiDepth = EditorGUILayout.IntField(_player.GuiDepth, GUILayout.Width(40));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Baum in Datei", GUILayout.Width(100))) SaveTree();
            }

            _tab = GUILayout.Toolbar(_tab, Tabs);
            switch (_tab)
            {
                case 0: TreeTab(); break;
                case 1: VariablesTab(); break;
                case 2: FunctionsTab(); break;
                case 3: CommandsTab(); break;
                case 4: InputTab(); break;
            }
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
        }

        static string Label(VmfPlayer p)
        {
            string file = p.Movie != null ? Path.GetFileName(p.BankPath ?? "") : "";
            return $"{p.gameObject.name} – {(string.IsNullOrEmpty(file) ? "Film" : file)}{(p.Visible ? "" : " (unsichtbar)")}";
        }

        void Select(VmfPlayer p)
        {
            if (_player != null) _player.Highlight = null;
            _player = p; _sel = null; _selPath = null; _edits.Clear(); _cmdLog.Clear();
            Hook(p);
        }

        // ------------------------------------------------------------------ Baum
        void TreeTab()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("Filter (Name)", _filter);
                _onlyVisible = EditorGUILayout.ToggleLeft("nur sichtbare", _onlyVisible, GUILayout.Width(100));
                _onlyClips = EditorGUILayout.ToggleLeft("nur Clips", _onlyClips, GUILayout.Width(80));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (var sv = new EditorGUILayout.ScrollViewScope(_scrollTree, GUILayout.Width(position.width * 0.55f)))
                {
                    _scrollTree = sv.scrollPosition;
                    if (_player.Root != null) DrawNode(_player.Root, "/", 0);
                }
                using (var sv = new EditorGUILayout.ScrollViewScope(_scrollDetail))
                {
                    _scrollDetail = sv.scrollPosition;
                    DetailPanel();
                }
            }
        }

        bool Matches(VmfPlayer.DisplayObject d)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            return (d.Name ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool SubtreeMatches(VmfPlayer.Instance inst, int depth)
        {
            if (depth > 24) return false;
            foreach (var d in inst.List.Values)
            {
                if (_onlyVisible && !d.Visible) continue;
                if (Matches(d)) return true;
                if (d.Sprite != null && SubtreeMatches(d.Sprite, depth + 1)) return true;
            }
            return false;
        }

        void DrawNode(VmfPlayer.Instance inst, string path, int depth)
        {
            if (depth > 24) return;
            foreach (var d in inst.List.Values)
            {
                if (_onlyVisible && (!d.Visible || (d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f))) continue;
                if (_onlyClips && d.Sprite == null) continue;
                string name = d.Name ?? ("#" + d.Depth);
                string p = (path == "/" ? "/" : path + "/") + name;
                bool filtered = !string.IsNullOrEmpty(_filter);
                bool selfMatch = Matches(d);
                bool childMatch = d.Sprite != null && filtered && SubtreeMatches(d.Sprite, depth + 1);
                if (filtered && !selfMatch && !childMatch) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(depth * 14);
                    if (d.Sprite != null && d.Sprite.List.Count > 0)
                    {
                        bool open = _open.Contains(p) || (filtered && childMatch);
                        if (GUILayout.Button(open ? "▾" : "▸", EditorStyles.label, GUILayout.Width(14))) { if (open) _open.Remove(p); else _open.Add(p); }
                    }
                    else GUILayout.Space(14);
                    var style = ReferenceEquals(d, _sel) ? EditorStyles.boldLabel : EditorStyles.label;
                    var c = GUI.color;
                    if (!d.Visible) GUI.color = new Color(1, 1, 1, 0.45f);
                    if (GUILayout.Button(NodeText(d, name), style)) { _sel = d; _selPath = p; _player.Highlight = d; }
                    GUI.color = c;
                }
                if (d.Sprite != null && (_open.Contains(p) || (filtered && childMatch))) DrawNode(d.Sprite, p, depth + 1);
            }
        }

        string NodeText(VmfPlayer.DisplayObject d, string name)
        {
            string kind;
            if (d.Sprite != null)
            {
                var lab = CurrentLabel(d.Sprite);
                kind = $"clip f={d.Sprite.Frame + 1}/{d.Sprite.Timeline.FrameCount}{(lab != null ? " [" + lab + "]" : "")}{(d.Sprite.Playing ? " ▶" : "")}";
            }
            else if (_player.Movie.Chars.TryGetValue(d.CharId, out var ch))
            {
                kind = ch.GetType().Name;
                if (ch is VmfMovie.EditText)
                {
                    var t = _player.DebugText(d);
                    if (!string.IsNullOrEmpty(t)) kind += " \"" + Short(t, 30) + "\"";
                }
            }
            else kind = "?";
            string a = d.Cx.Mul.w < 0.999f ? $" α{d.Cx.Mul.w:0.00}" : "";
            return $"{name}  ·  {kind}{a}";
        }

        static string Short(string s, int n) { s = s.Replace("\n", "⏎"); return s.Length > n ? s.Substring(0, n) + "…" : s; }

        static string CurrentLabel(VmfPlayer.Instance inst)
        {
            string lab = null; int best = -1;
            foreach (var kv in inst.Timeline.Labels) if (kv.Value <= inst.Frame && kv.Value > best) { best = kv.Value; lab = kv.Key; }
            return lab;
        }

        void DetailPanel()
        {
            var d = _sel;
            if (d == null) { EditorGUILayout.HelpBox("Objekt im Baum anklicken – es wird im Spielbild rot umrahmt.", MessageType.None); return; }
            EditorGUILayout.SelectableLabel(_selPath, EditorStyles.boldLabel, GUILayout.Height(18));
            EditorGUILayout.LabelField("Tiefe / Charakter", $"{d.Depth}  /  #{d.CharId}{(d.ClipDepth > 0 ? "  (Maske bis " + d.ClipDepth + ")" : "")}");
            d.Visible = EditorGUILayout.Toggle("_visible", d.Visible);
            var pos = EditorGUILayout.Vector2Field("_x / _y", new Vector2(d.Matrix.Tx, d.Matrix.Ty));
            if (pos.x != d.Matrix.Tx || pos.y != d.Matrix.Ty) { d.Matrix.Tx = pos.x; d.Matrix.Ty = pos.y; d.Position = null; d.ScriptMatrix = true; }
            float alpha = EditorGUILayout.Slider("_alpha", d.Cx.Mul.w * 100f, 0f, 100f);
            if (Mathf.Abs(alpha - d.Cx.Mul.w * 100f) > 0.01f) { d.Cx.Mul.w = alpha / 100f; d.ScriptCx = true; }
            EditorGUILayout.LabelField("Skalierung", $"{new Vector2(d.Matrix.A, d.Matrix.B).magnitude:0.###} x {new Vector2(d.Matrix.C, d.Matrix.D).magnitude:0.###}{(d.ScriptMatrix ? "   (per Skript gesetzt)" : "")}");
            var sb = _player.ScreenBounds(d);
            if (sb.HasValue) EditorGUILayout.LabelField("Bildschirm", $"{sb.Value.x:0},{sb.Value.y:0}  {sb.Value.width:0}x{sb.Value.height:0} px");

            if (_player.Movie.Chars.TryGetValue(d.CharId, out var ch) && ch is VmfMovie.EditText et)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Textfeld", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Variable", string.IsNullOrEmpty(et.Variable) ? "-" : et.Variable);
                EditorGUILayout.LabelField("Starttext", et.Text ?? "");
                string cur = _player.DebugText(d) ?? "";
                if (!string.IsNullOrEmpty(et.Variable))
                {
                    string key = VmfPlayer.ResolveVar(d.Parent, et.Variable);
                    EditorGUILayout.LabelField("Schlüssel", key);
                    string edit = EditorGUILayout.TextField("Wert", cur);
                    if (edit != cur) _player.DebugSetVariable(key, edit);
                }
                else EditorGUILayout.LabelField("Text", cur);
            }

            if (d.Sprite != null)
            {
                var inst = d.Sprite;
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Zeitleiste", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(inst.Playing ? "Stop" : "Play", GUILayout.Width(60))) inst.Playing = !inst.Playing;
                    int f = EditorGUILayout.IntSlider(inst.Frame + 1, 1, Mathf.Max(1, inst.Timeline.FrameCount));
                    if (f != inst.Frame + 1) _player.GotoFrame(inst, f - 1, true);
                }
                EditorGUILayout.LabelField("Aktuelles Label", CurrentLabel(inst) ?? "-");
                if (inst.Timeline.Labels.Count > 0)
                {
                    EditorGUILayout.LabelField("Labels (Klick = gotoAndPlay, Shift+Klick = gotoAndStop)", EditorStyles.miniLabel);
                    foreach (var kv in inst.Timeline.Labels.OrderBy(k => k.Value))
                        if (GUILayout.Button($"{kv.Key}  (Frame {kv.Value + 1})", EditorStyles.miniButton))
                            _player.GotoLabel(_selPath, kv.Key, !Event.current.shift);
                }
                // Variablen und Funktionen dieses Clips
                string prefix = _selPath + ":";
                var vars = _player.DebugVariables().Where(v => v.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (vars.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Variablen dieses Clips", EditorStyles.boldLabel);
                    foreach (var v in vars) VarRow(v.Key, v.Value, v.Key.Substring(prefix.Length));
                }
                var fns = _player.DebugFunctions().Where(v => v.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (fns.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Funktionen dieses Clips", EditorStyles.boldLabel);
                    foreach (var fn in fns) FnRow(fn.Key, fn.Value, fn.Key.Substring(prefix.Length));
                }
            }
        }

        // ------------------------------------------------------------------ Variablen / Funktionen
        string _usageVar; List<string> _usage; bool _autoCall;

        /// <summary>Unten im Fenster: Funktionen, die die zuletzt geänderte/gefragte Variable lesen – zum erneuten Ausführen.</summary>
        void UsagePanel()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(_usageVar == null ? "Variable ändern oder \"?\" klicken: zeigt, welche Funktionen sie lesen" : $"Gelesen von ({_usage?.Count ?? 0}):  {_usageVar}", EditorStyles.boldLabel);
                _autoCall = EditorGUILayout.ToggleLeft(new GUIContent("nach Änderung alle aufrufen", "Ruft nach jeder Änderung alle Funktionen auf, die die Variable lesen (kann Menüs umschalten!)"), _autoCall, GUILayout.Width(190));
            }
            EditorGUILayout.LabelField("Textfelder mit dieser Variable aktualisieren sich sofort. Symbole, Frames und Balken setzt der Film nur, wenn eine dieser Funktionen läuft (im Spiel z.B. setInfos beim Öffnen).", EditorStyles.wordWrappedMiniLabel);
            if (_usage == null) return;
            foreach (var f in _usage.Take(12))
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(f, GUILayout.Height(16));
                    if (GUILayout.Button("Aufrufen", GUILayout.Width(70))) _status = _player.DebugCall(f, "") ? f + "() aufgerufen" : f + ": nicht gefunden";
                }
            if (_usage.Count == 0) EditorGUILayout.LabelField("Keine Funktion liest diesen Namen – nur Textfelder (oder das Spiel selbst setzt die Anzeige).", EditorStyles.miniLabel);
        }

        void VariablesTab()
        {
            _filter = EditorGUILayout.TextField("Filter (Pfad/Name/Wert)", _filter);
            var all = _player.DebugVariables();
            var list = string.IsNullOrEmpty(_filter) ? all
                     : all.Where(v => v.Key.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 || (v.Value ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            EditorGUILayout.LabelField($"{list.Count} von {all.Count} Variablen" + (list.Count > 400 ? " – nur die ersten 400, Filter benutzen" : ""), EditorStyles.miniLabel);
            using (var sv = new EditorGUILayout.ScrollViewScope(_scrollList))
            {
                _scrollList = sv.scrollPosition;
                foreach (var v in list.Take(400)) VarRow(v.Key, v.Value, v.Key);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Neu setzen", GUILayout.Width(70));
                _customFn = EditorGUILayout.TextField(_customFn, GUILayout.MinWidth(150));
                _customArgs = EditorGUILayout.TextField(_customArgs, GUILayout.MinWidth(100));
                if (GUILayout.Button("Setzen", GUILayout.Width(60)) && !string.IsNullOrEmpty(_customFn))
                {
                    string k = _customFn.Contains(":") ? _customFn : "/:" + _customFn;
                    _player.DebugSetVariable(k, _customArgs);
                    _status = $"{_customFn} = {_customArgs}";
                    _usageVar = k; _usage = _player.DebugFunctionsUsing(k);
                    if (_autoCall) foreach (var f in _usage) _player.DebugCall(f, "");
                }
            }
            EditorGUILayout.LabelField("Name ohne Pfad = _root (\"/:name\"); Zahlen und true/false werden typisiert gesetzt.", EditorStyles.miniLabel);
            UsagePanel();
        }

        void VarRow(string key, string value, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(label, GUILayout.Height(16), GUILayout.MinWidth(120), GUILayout.MaxWidth(position.width * 0.45f));
                string shown = value ?? "";
                int sp = shown.IndexOf("  (", StringComparison.Ordinal);
                string raw = sp >= 0 && (shown.EndsWith("(Zahl)") || shown.EndsWith("(Bool)")) ? shown.Substring(0, sp) : shown;
                bool editable = !raw.StartsWith("<") && !raw.StartsWith("[");
                if (!_edits.TryGetValue(key, out var ed)) ed = raw;
                GUI.enabled = editable;
                string n = EditorGUILayout.DelayedTextField(ed);
                GUI.enabled = true;
                if (n != ed)
                {
                    _player.DebugSetVariable(key, n); _status = $"{key} = {n}"; _edits.Remove(key);
                    _usageVar = key; _usage = _player.DebugFunctionsUsing(key);
                    if (_autoCall) foreach (var f in _usage) _player.DebugCall(f, "");
                }
                if (sp >= 0) GUILayout.Label(shown.Substring(sp + 2), EditorStyles.miniLabel, GUILayout.Width(44));
                if (GUILayout.Button(new GUIContent("?", "Welche Funktionen lesen diese Variable?"), EditorStyles.miniButton, GUILayout.Width(20)))
                { _usageVar = key; _usage = _player.DebugFunctionsUsing(key); }
            }
        }

        void FunctionsTab()
        {
            _filter = EditorGUILayout.TextField("Filter (Pfad/Name)", _filter);
            var all = _player.DebugFunctions();
            var list = string.IsNullOrEmpty(_filter) ? all : all.Where(v => v.Key.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            EditorGUILayout.LabelField($"{list.Count} von {all.Count} Funktionen – Argumente mit Komma trennen (Zahlen, true/false, Text)", EditorStyles.miniLabel);
            using (var sv = new EditorGUILayout.ScrollViewScope(_scrollList))
            {
                _scrollList = sv.scrollPosition;
                foreach (var f in list.Take(400)) FnRow(f.Key, f.Value, f.Key);
            }
        }

        void FnRow(string key, string pars, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel($"{label}({pars})", GUILayout.Height(16), GUILayout.MinWidth(160));
                _callArgs.TryGetValue(key, out var a);
                a = EditorGUILayout.TextField(a ?? "", GUILayout.Width(120));
                _callArgs[key] = a;
                if (GUILayout.Button("Aufrufen", GUILayout.Width(70)))
                    _status = _player.DebugCall(key, a) ? $"{key}({a}) aufgerufen" : $"{key}: nicht gefunden";
            }
        }

        // ------------------------------------------------------------------ FSCommands / Eingabe
        void CommandsTab()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("Filter", _filter);
                if (GUILayout.Button("Leeren", GUILayout.Width(70))) _cmdLog.Clear();
            }
            EditorGUILayout.LabelField("Befehle, die der Film ans Spiel schickt (FSCommand / getURL)", EditorStyles.miniLabel);
            using (var sv = new EditorGUILayout.ScrollViewScope(_scrollList))
            {
                _scrollList = sv.scrollPosition;
                for (int i = _cmdLog.Count - 1; i >= 0; i--)
                {
                    var l = _cmdLog[i];
                    if (!string.IsNullOrEmpty(_filter) && l.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    EditorGUILayout.SelectableLabel(l, GUILayout.Height(16));
                }
            }
        }

        void InputTab()
        {
            EditorGUILayout.LabelField("Wie das Spiel: ruft die Funktion beim Menü mit Fokus (_root.Clip) auf", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("▲", GUILayout.Width(40))) Send("Move", "up");
                GUILayout.FlexibleSpace();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("◀", GUILayout.Width(40))) Send("Move", "left");
                if (GUILayout.Button("▼", GUILayout.Width(40))) Send("Move", "down");
                if (GUILayout.Button("▶", GUILayout.Width(40))) Send("Move", "right");
                GUILayout.FlexibleSpace();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                foreach (var b in new[] { "A", "B", "X", "Y", "LB", "RB" })
                    if (GUILayout.Button(b, GUILayout.Width(44))) Send(b);
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space();
            var focus = _player.DebugVariables().FirstOrDefault(v => string.Equals(v.Key, "/:Clip", StringComparison.OrdinalIgnoreCase)).Value;
            EditorGUILayout.LabelField("Fokus (_root.Clip)", string.IsNullOrEmpty(focus) ? "-" : focus);
            var lst = _player.ActiveList;
            if (lst != null) EditorGUILayout.LabelField("Aktive Liste", $"{lst.Name}  Zeilen {lst.Rows.Count}, Auswahl {lst.Selected + 1}, oben {lst.Top + 1}, aktiv {lst.Active}");
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Eigene Eingabe", GUILayout.Width(90));
                _customFn = EditorGUILayout.TextField(_customFn);
                _customArgs = EditorGUILayout.TextField(_customArgs);
                if (GUILayout.Button("Senden", GUILayout.Width(60)) && !string.IsNullOrEmpty(_customFn))
                {
                    var args = string.IsNullOrEmpty(_customArgs) ? Array.Empty<object>() : _customArgs.Split(',').Select(s => (object)s.Trim()).ToArray();
                    Send(_customFn, args);
                }
            }
        }

        void Send(string fn, params object[] args)
        {
            bool ok = _player.Input(fn, args);
            _status = ok ? $"{fn}({string.Join(", ", args)}) verarbeitet" : $"{fn}: keine passende Funktion im Film";
        }

        // ------------------------------------------------------------------ Laden / Speichern
        void LoadMovie()
        {
            string start = EditorPrefs.GetString("TDU2.FlashDebugger.Dir", "");
            var mgr = FindAnyObjectByType<TDU2VehicleManager>();
            if (string.IsNullOrEmpty(start) && mgr != null && !string.IsNullOrEmpty(mgr.BnkDir)) start = Path.Combine(mgr.BnkDir, "frontend", "hires");
            string path = EditorUtility.OpenFilePanel("Flash-Film (Frontend-Bank) laden", start, "bnk");
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString("TDU2.FlashDebugger.Dir", Path.GetDirectoryName(path));
            var go = new GameObject("FlashViewer " + Path.GetFileNameWithoutExtension(path));
            var p = go.AddComponent<VmfPlayer>();
            p.GuiDepth = -20; p.MouseEnabled = true;
            try
            {
                p.Load(path);
                // Texte wie im Spiel (Bnk-Ordner = Ordner über "frontend")
                string bnk = mgr != null && !string.IsNullOrEmpty(mgr.BnkDir) ? mgr.BnkDir : FindBnkDir(path);
                if (bnk != null) FlashLocalization.Apply(p, FlashLocalization.Load(bnk, FlashLocalization.LangCode(Application.systemLanguage)));
                Select(p);
                _status = "Geladen: " + path + " – Labels der Wurzel im Baum (\"/\" auswählen) anspringen, z.B. pause_in";
                _sel = null; _selPath = "/";
            }
            catch (Exception ex)
            {
                DestroyImmediate(go);
                _status = "Fehler: " + ex.Message;
            }
        }

        static string FindBnkDir(string file)
        {
            var dir = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "database"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        void SaveTree()
        {
            string path = EditorUtility.SaveFilePanel("Baum speichern", "", "flash_tree.txt", "txt");
            if (string.IsNullOrEmpty(path)) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_player.DumpTree(12));
            sb.AppendLine().AppendLine("== Variablen");
            foreach (var v in _player.DebugVariables()) sb.AppendLine(v.Key + " = " + v.Value);
            sb.AppendLine().AppendLine("== Funktionen");
            foreach (var f in _player.DebugFunctions()) sb.AppendLine(f.Key + "(" + f.Value + ")");
            File.WriteAllText(path, sb.ToString());
            _status = "Gespeichert: " + path;
        }
    }
}
