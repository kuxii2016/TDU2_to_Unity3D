using System;
using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Map.Flash
{
    /// <summary>
    /// Einfache Helfer für die Arbeit mit den Flash-Filmen (Pfade wie im Spiel: "/menus/profile/profile2", Variablen "/:Name"
    /// oder "/pfad:name"):
    ///   ui.Show(path) / ui.Hide(path)            sichtbar/unsichtbar (wie _visible; Skripte des Films können es wieder ändern)
    ///   ui.ForceVisible(path, false)             bleibt so, auch wenn der Film selbst _visible setzt; ForceVisible(path, null) gibt frei
    ///   ui.SetFrame(path, 10)                    gotoAndStop(10) – Bildnummer wie in Flash (1 = erstes Bild)
    ///   ui.PlayFrom(path, 10) / ui.PlayLabel(path, "open")
    ///   ui.SetLabel(path, "on")                  gotoAndStop("on")
    ///   ui.SetText("/:AvatarName", "Michael")    Text-/Wertvariable (ein Textfeld zeigt sie, sobald sie gesetzt ist)
    ///   ui.SetNumber("/:Nationality", 24)        typisierter Wert (für Vergleiche/gotoAndStop(_root.Nationality) im Skript)
    ///   ui.SetBool("/:state_online", true)
    ///   ui.Frame(path) / ui.Label(path)          aktuelles Bild (1-basiert) / Label
    ///   ui.Exists(path), ui.LabelsOf(path)
    /// Festhalten ("Pins", damit Zeitleiste/Skripte des Films eigene Werte nicht überschreiben):
    ///   ui.PinText("/car_datas/datas_base:cur_power", "300 bhp")   Variable bleibt so (nach jedem Bild erneut gesetzt)
    ///   ui.PinValue("/:IsCardLoaded", true)                        typisiert (Zahl/bool)
    ///   ui.PinLabel("/car_datas/ca_name/group", "A2")              gotoAndStop(label), erneut wenn der Clip neu angelegt wird
    ///   ui.PinFrame(path, 3)                                       dito mit Bildnummer (1-basiert)
    ///   using (ui.PinScope()) { ui.SetVariable(...); ui.SetLabel(...); }   alles darin wird automatisch festgehalten
    ///   ui.Unpin("/car_datas") / ui.UnpinAll()                      freigeben (Präfix von Variable bzw. Pfad)
    /// Maus: ui.ConsumeClick() – den laufenden Klick verwerfen (eigene Klickbehandlung; der Film sieht das Loslassen nicht).
    /// Maus: HintClicked ("A"/"B"/"X"/"Y" – Klick auf die Tasten-Symbole a_/b_/x_/y_ der Menüs, ohne Abonnent = Input(...)),
    ///       TextFieldClicked (Variable des angeklickten Textfelds, z.B. "/:email").
    /// </summary>
    public sealed partial class VmfPlayer
    {
        // ------------------------------------------------------------------ Sichtbarkeit
        readonly Dictionary<string, bool> _forcedVisible = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public bool Show(string path) { var d = Find(path); if (d == null) return false; d.Visible = true; return true; }
        public bool Hide(string path) { var d = Find(path); if (d == null) return false; d.Visible = false; return true; }

        /// <summary>Sichtbarkeit festhalten (true/false) – wird nach jedem Bild erneut angewendet; null = wieder dem Film überlassen.</summary>
        public void ForceVisible(string path, bool? visible)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (visible.HasValue) { _forcedVisible[path] = visible.Value; var d = Find(path); if (d != null) d.Visible = visible.Value; }
            else _forcedVisible.Remove(path);
        }

        /// <summary>Alle festgehaltenen Sichtbarkeiten unter einem Pfad freigeben.</summary>
        public void ReleaseForced(string pathPrefix = null)
        {
            if (string.IsNullOrEmpty(pathPrefix)) { _forcedVisible.Clear(); return; }
            var keys = new List<string>(_forcedVisible.Keys);
            foreach (var k in keys) if (k.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)) _forcedVisible.Remove(k);
        }

        void ApplyForcedVisibility()
        {
            if (_forcedVisible.Count == 0) return;
            foreach (var kv in _forcedVisible) { var d = Find(kv.Key); if (d != null) d.Visible = kv.Value; }
        }

        // ------------------------------------------------------------------ Festhalten (Pins)
        sealed class PinVar { public object Value; public bool Typed; }
        sealed class PinPos { public string Label; public int Frame; public Instance Applied; }
        readonly Dictionary<string, PinVar> _pinVars = new Dictionary<string, PinVar>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PinPos> _pinPos = new Dictionary<string, PinPos>(StringComparer.OrdinalIgnoreCase);
        int _pinScope;
        bool _applyingPins;

        /// <summary>Solange der Bereich offen ist, werden SetVariable/SetValue/SetLabel/SetFrame/GotoLabel(…, false) festgehalten.</summary>
        public IDisposable PinScope() { _pinScope++; return new PinScopeEnd(this); }
        sealed class PinScopeEnd : IDisposable
        {
            VmfPlayer _p; public PinScopeEnd(VmfPlayer p) { _p = p; }
            public void Dispose() { if (_p != null) { _p._pinScope = Math.Max(0, _p._pinScope - 1); _p = null; } }
        }
        bool AutoPin => _pinScope > 0 && !_applyingPins;

        public void PinText(string variable, string text) { _pinVars[variable] = new PinVar { Value = text ?? "", Typed = false }; _applyingPins = true; SetVariable(variable, text ?? ""); _applyingPins = false; }
        public void PinValue(string variable, object value) { _pinVars[variable] = new PinVar { Value = value, Typed = true }; _applyingPins = true; SetValue(variable, value); _applyingPins = false; }
        public bool PinLabel(string path, string label)
        {
            var pp = new PinPos { Label = label, Frame = -1 }; _pinPos[path] = pp;
            _applyingPins = true; bool ok = GotoLabel(path, label, false); _applyingPins = false;
            pp.Applied = InstanceOf(path); return ok;
        }
        public bool PinFrame(string path, int flashFrame)
        {
            var pp = new PinPos { Frame = flashFrame }; _pinPos[path] = pp;
            _applyingPins = true; bool ok = SetFrame(path, flashFrame); _applyingPins = false;
            pp.Applied = InstanceOf(path); return ok;
        }

        /// <summary>Festgehaltene Variablen/Pfade freigeben, die mit <paramref name="prefix"/> beginnen.</summary>
        public void Unpin(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) { UnpinAll(); return; }
            foreach (var k in new List<string>(_pinVars.Keys)) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _pinVars.Remove(k);
            foreach (var k in new List<string>(_pinPos.Keys)) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _pinPos.Remove(k);
        }
        public void UnpinAll() { _pinVars.Clear(); _pinPos.Clear(); }
        public int PinCount => _pinVars.Count + _pinPos.Count;

        /// <summary>Nach jedem Bild: festgehaltene Werte wiederherstellen (nur wenn der Film sie geändert hat).</summary>
        void ApplyPins()
        {
            if (_pinVars.Count == 0 && _pinPos.Count == 0) return;
            _applyingPins = true;
            try
            {
                foreach (var kv in _pinVars)
                {
                    var pv = kv.Value;
                    string want = pv.Value is bool b ? (b ? "true" : "false") : pv.Value is double dd ? dd.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : pv.Value is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : pv.Value?.ToString() ?? "";
                    if (string.Equals(GetVariable(kv.Key), want, StringComparison.Ordinal)) continue;
                    if (pv.Typed) SetValue(kv.Key, pv.Value); else SetVariable(kv.Key, want);
                }
                foreach (var kv in _pinPos)
                {
                    var inst = InstanceOf(kv.Key);
                    if (inst == null || ReferenceEquals(inst, kv.Value.Applied)) continue;   // nur neu angelegte Clips (Zeitleiste hat sie ersetzt)
                    if (kv.Value.Label != null) GotoLabel(kv.Key, kv.Value.Label, false); else SetFrame(kv.Key, kv.Value.Frame);
                    kv.Value.Applied = inst;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Flash-Pins: " + ex.Message); }
            finally { _applyingPins = false; }
        }

        /// <summary>Laufenden Mausklick verwerfen: das Loslassen löst im Film nichts mehr aus (eigene Klickbehandlung).</summary>
        public void ConsumeClick() { _mouseDown = false; _pressBtn = null; }

        // ------------------------------------------------------------------ Bilder / Labels
        Instance InstanceOf(string path) => string.IsNullOrEmpty(path) || path == "/" ? Root : Find(path)?.Sprite;

        /// <summary>gotoAndStop(Bild) mit Flash-Nummerierung (1 = erstes Bild).</summary>
        public bool SetFrame(string path, int flashFrame)
        {
            if (AutoPin) return PinFrame(path, flashFrame);
            var inst = InstanceOf(path);
            if (inst == null) return false;
            inst.Playing = false;
            GotoFrame(inst, Mathf.Clamp(flashFrame - 1, 0, Math.Max(0, inst.Timeline.FrameCount - 1)), true);
            return true;
        }

        /// <summary>gotoAndPlay(Bild), Flash-Nummerierung.</summary>
        public bool PlayFrom(string path, int flashFrame)
        {
            var inst = InstanceOf(path);
            if (inst == null) return false;
            inst.Playing = true;
            GotoFrame(inst, Mathf.Clamp(flashFrame - 1, 0, Math.Max(0, inst.Timeline.FrameCount - 1)), true);
            return true;
        }

        public bool SetLabel(string path, string label) => AutoPin ? PinLabel(path, label) : GotoLabel(path, label, false);
        public bool PlayLabel(string path, string label) => GotoLabel(path, label, true);

        /// <summary>Aktuelles Bild (1-basiert), 0 wenn der Clip fehlt.</summary>
        public int Frame(string path) { var i = InstanceOf(path); return i != null ? i.Frame + 1 : 0; }

        /// <summary>Label des aktuellen Bilds bzw. das letzte davor (wie _currentlabel), null wenn keins.</summary>
        public string Label(string path)
        {
            var i = InstanceOf(path);
            if (i?.Timeline == null) return null;
            string best = null; int bf = -1;
            foreach (var kv in i.Timeline.Labels) if (kv.Value <= i.Frame && kv.Value > bf) { bf = kv.Value; best = kv.Key; }
            return best;
        }

        public bool Exists(string path) => InstanceOf(path) != null || Find(path) != null;

        /// <summary>Labels eines Clips mit Bildnummer (1-basiert), z.B. "on" = 10.</summary>
        public Dictionary<string, int> LabelsOf(string path)
        {
            var r = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var i = InstanceOf(path);
            if (i?.Timeline != null) foreach (var kv in i.Timeline.Labels) r[kv.Key] = kv.Value + 1;
            return r;
        }

        // ------------------------------------------------------------------ Variablen
        public void SetText(string variable, string text) => SetVariable(variable, text ?? "");
        public string GetText(string variable) => GetVariable(variable);
        public void SetNumber(string variable, double value) => SetValue(variable, value);
        public void SetBool(string variable, bool value) => SetValue(variable, value);

        // ------------------------------------------------------------------ Mausklicks außerhalb von Flash-Buttons
        /// <summary>Klick auf ein Tasten-Symbol (a_, b_, x_, y_): "A", "B", "X", "Y". Ohne Abonnent wird Input(Taste) aufgerufen.</summary>
        public event Action<string> HintClicked;
        /// <summary>Klick auf ein sichtbares Textfeld: dessen Variable (z.B. "/:email").</summary>
        public event Action<string> TextFieldClicked;

        static readonly string[] HintNames = { "a_", "b_", "x_", "y_" };

        /// <summary>Linksklick ohne getroffenen Flash-Button: Tasten-Symbol oder Textfeld suchen.</summary>
        void HandleFreeClick(Vector2 gui)
        {
            // Tasten-Symbole (oberstes zuerst)
            string hint = null; int hintOrder = -1, order = 0;
            string field = null; int fieldOrder = -1;
            void Walk(Instance inst, bool visible)
            {
                foreach (var d in inst.List.Values)
                {
                    order++;
                    bool vis = visible && d.Visible && d.ClipDepth == 0 && !(d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f);
                    if (!vis) continue;
                    if (d.Sprite != null)
                    {
                        int hi = Array.IndexOf(HintNames, d.Name ?? "");
                        if (hi >= 0)
                        {
                            var r = ScreenBounds(d);
                            if (r.HasValue && Grow(r.Value, 6f).Contains(gui) && order > hintOrder) { hintOrder = order; hint = "ABXY".Substring(hi, 1); }
                        }
                        Walk(d.Sprite, true);
                    }
                    else if (Movie.Chars.TryGetValue(d.CharId, out var ch) && ch is VmfMovie.EditText et && !string.IsNullOrEmpty(et.Variable))
                    {
                        var m = WorldOf(d.Parent) * LocalMatrix(d);
                        var a = m.Apply(new Vector2(et.Bounds.xMin, et.Bounds.yMin));
                        var b = m.Apply(new Vector2(et.Bounds.xMax, et.Bounds.yMax));
                        var rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                        if (Grow(rect, 4f).Contains(gui) && order > fieldOrder) { fieldOrder = order; field = et.Variable; }
                    }
                }
            }
            Walk(Root, true);
            if (hint != null)
            {
                if (HintClicked != null) HintClicked(hint);
                else Input(hint);
                return;
            }
            if (field != null) TextFieldClicked?.Invoke(field);
        }

        static Rect Grow(Rect r, float px) => new Rect(r.x - px, r.y - px, r.width + 2 * px, r.height + 2 * px);
    }
}
