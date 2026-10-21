using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TDU2.Map.Flash
{
    /// <summary>
    /// ActionScript-1/2-Interpreter (Stackmaschine, SWF-DoAction-Bytecode) für die Eden-Flash-Screens.
    /// Unterstützt: Push (alle Typen, Konstantenpool, Register), Variablen mit Pfaden ("/a/b:x", "_root.a.x", "../:x"),
    /// GetMember/SetMember auf Clips/Objekten/Strings, If/Jump, Arithmetik/Vergleiche/Logik, Funktionen
    /// (DefineFunction, CallFunction, CallMethod, Return), Clip-Methoden (gotoAndStop/Play, play, stop ...),
    /// Get/SetProperty (_x, _y, _alpha, _visible, _currentframe ...), GotoFrame/GotoFrame2/GoToLabel, SetTarget, FSCommand.
    /// Unbekanntes liefert undefined statt abzubrechen.
    /// </summary>
    public sealed partial class VmfPlayer
    {
        /// <summary>FSCommand aus dem Flash (getURL "FSCommand:cmd", args) – z.B. Menüauswahl.</summary>
        public event Action<string, string> FSCommand;

        sealed class Undef { public override string ToString() => "undefined"; }
        static readonly object Undefined = new Undef();

        sealed class AsFunction
        {
            public byte[] Code; public int Start, Length; public string[] Params; public string[] Pool; public Instance Scope; public string Name;
        }
        sealed class AsObject { public readonly Dictionary<string, object> Members = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
        sealed class AsArray { public readonly List<object> Items = new List<object>(); }
        sealed class Builtin { public string Name; public Builtin(string n) { Name = n; } }
        static readonly Builtin MathObj = new Builtin("Math"), KeyObj = new Builtin("Key"), StageObj = new Builtin("Stage");

        /// <summary>Benannte Button-Instanz als Skriptobjekt (AS2: btn.onPress = function(){...}, btn._visible ...).</summary>
        sealed class ButtonObj { public DisplayObject D; public readonly Dictionary<string, object> Members = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
        readonly Dictionary<DisplayObject, ButtonObj> _btnObjs = new Dictionary<DisplayObject, ButtonObj>();
        ButtonObj BtnObj(DisplayObject d) { if (!_btnObjs.TryGetValue(d, out var b)) _btnObjs[d] = b = new ButtonObj { D = d }; return b; }

        void CallButtonHandler(DisplayObject d, string ev)
        {
            if (d == null || !_btnObjs.TryGetValue(d, out var bo) || !bo.Members.TryGetValue(ev, out var h) || !(h is AsFunction fn)) return;
            try { InvokeTop(fn, bo, Array.Empty<object>()); }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Flash-Button " + ev + ": " + ex.Message); }
        }

        // onEnterFrame-Handler (je Bild nach dem Zeitleisten-Schritt)
        readonly List<Instance> _enterFrame = new List<Instance>();

        void RunEnterFrames()
        {
            if (_enterFrame.Count == 0) return;
            foreach (var inst in _enterFrame.ToArray())
            {
                if (!Attached(inst)) { _enterFrame.Remove(inst); continue; }
                if (!_objVars.TryGetValue(VarKey(inst, "onEnterFrame"), out var f) || !(f is AsFunction fn)) { _enterFrame.Remove(inst); continue; }
                try { InvokeTop(fn, inst, Array.Empty<object>()); }
                catch (Exception ex) { Debug.LogWarning("[TDU2] onEnterFrame (" + PathOf(inst) + "): " + ex.Message); }
            }
        }

        bool Attached(Instance inst)
        {
            for (var i = inst; i != null; i = i.Owner?.Parent)
            {
                if (i == Root) return true;
                var o = i.Owner;
                if (o == null || o.Parent == null || !o.Parent.List.TryGetValue(o.Depth, out var x) || x != o) return false;
            }
            return false;
        }

        /// <summary>Clip hängt im Anzeigebaum und ist samt Eltern sichtbar.</summary>
        public bool IsShown(Instance inst) => Shown(inst);

        bool Shown(Instance inst)
        {
            if (!Attached(inst)) return false;
            for (var i = inst; i != null && i.Owner != null; i = i.Owner.Parent) if (!i.Owner.Visible) return false;
            return true;
        }

        // ------------------------------------------------------------------ Native Liste (im Spiel: C++-Klasse ASFlashList)
        /// <summary>
        /// Ersatz für die native Liste des Spiels (ASFlashList(owner, name, sichtbareZeilen, spalten)). Das Spiel füllt die Zeilen
        /// selbst: hier über das Ereignis <see cref="ListCreated"/> (Rows füllen, dann <see cref="ListRefresh"/>).
        /// Zeilen-Clips heißen "line1".."lineN" im Besitzer; Spaltentexte landen in den Variablen aus <see cref="FlashList.ColumnVars"/>.
        /// </summary>
        public sealed class FlashList
        {
            public Instance Owner; public string Name; public int ViewSize, Columns;
            public readonly List<string[]> Rows = new List<string[]>();
            public string[] ColumnVars = { "control_name", "control_bind", "control_device" };
            public int Selected, Top; public bool Active;
            public int ViewCount => Rows.Count == 0 ? ViewSize : Mathf.Min(ViewSize, Rows.Count);
            public int SelectedLine => Selected - Top + 1;   // 1-basiert wie getLinePath(id)
        }

        public event Action<FlashList> ListCreated;
        public event Action<FlashList> ListValidated;
        FlashList _activeList;
        public FlashList ActiveList => _activeList;

        string LinePath(FlashList l, int line) { var op = PathOf(l.Owner); return (op == "/" ? "" : op) + "/line" + line; }

        /// <summary>Zeilentexte (ab Top) in die Zeilen-Clips schreiben; leere Zeilen bekommen empty = 1.</summary>
        public void ListRefresh(FlashList l)
        {
            if (l == null || l.Owner == null) return;
            for (int i = 1; i <= l.ViewSize; i++)
            {
                string lp = LinePath(l, i);
                int r = l.Top + i - 1;
                var row = r >= 0 && r < l.Rows.Count ? l.Rows[r] : null;
                for (int c = 0; c < l.ColumnVars.Length; c++)
                {
                    string v = row != null && c < row.Length ? row[c] ?? "" : "";
                    _vars[lp + ":" + l.ColumnVars[c]] = v; _objVars.Remove(lp + ":" + l.ColumnVars[c]);
                }
                _vars[lp + ":empty"] = row == null ? "1" : "0"; _objVars[lp + ":empty"] = row == null ? 1.0 : 0.0;
            }
        }

        /// <summary>Liste als geladen melden (beendet die Ladeanimation des Menüs).</summary>
        public void ListLoaded(FlashList l)
        {
            if (l?.Owner != null) StoreClipVar(l.Owner, "isListLoaded", true);
        }

        void CallOn(Instance inst, string fn, params object[] args)
        {
            if (inst != null && GetMember(inst, fn) is AsFunction f) InvokeTop(f, inst, args);
        }

        /// <summary>Auswahl verschieben (wie die native Liste: Animationen der Zeilen im Besitzer-Clip auslösen).</summary>
        public void ListMove(FlashList l, int delta)
        {
            if (l == null || l.Rows.Count == 0) return;
            int oldLine = l.SelectedLine;
            int sel = Mathf.Clamp(l.Selected + delta, 0, l.Rows.Count - 1);
            if (sel == l.Selected) return;
            CallOn(l.Owner, "PlayAnimSelectionOut", (double)oldLine);
            l.Selected = sel;
            int top = l.Top;
            if (sel < top) top = sel;
            if (sel >= top + l.ViewSize) top = sel - l.ViewSize + 1;
            if (top != l.Top) { l.Top = top; ListRefresh(l); }
            StoreClipVar(l.Owner, "line_view", (double)l.SelectedLine);   // LineValidate liest die gewählte Zeile aus line_view
            CallOn(l.Owner, "PlayAnimSelectionIn", (double)l.SelectedLine);
            FSCommand?.Invoke("sndp:move", "");
        }

        object ListMethod(FlashList l, string name)
        {
            switch (name)
            {
                case "GetViewSize": return (double)l.ViewCount;
                case "GetViewLineSelected": return (double)(l.Selected - l.Top);
                case "Activate":
                    l.Active = true; _activeList = l;
                    StoreClipVar(l.Owner, "line_view", (double)l.SelectedLine);
                    if (l.Rows.Count > 0) CallOn(l.Owner, "PlayAnimSelectionIn", (double)l.SelectedLine);
                    // Scrollbalken wie im Spiel einschalten (Pfeile/Lift melden dann "mylf:scrl:btnu|btnd|lift:prss")
                    CallOn(Child(l.Owner, "scrollBar"), "myLifeScroll");
                    return Undefined;
                case "Deactivate": l.Active = false; return Undefined;
                case "OnValidate": ListValidated?.Invoke(l); return Undefined;
            }
            return Undefined;   // OnSortInvert, OnHeaderClic …
        }

        readonly Dictionary<string, object> _globals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, object> _objVars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);   // Nicht-String-Werte (Funktionen/Objekte) pro "/pfad:name"
        readonly object[] _registers = new object[256];
        int _depth;
        float _startTime = -1f;

        // ------------------------------------------------------------------ Einstieg
        void ExecFrameActions(Instance self, byte[] code)
        {
            if (code == null || code.Length == 0) return;
            if (_depth > 24) return;
            _depth++; _inActions++;
            try { Exec(code, 0, code.Length, self, null, null, out _); }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Flash-Skript (" + PathOf(self) + "): " + ex.Message); }
            finally { _depth--; _inActions--; if (_inActions == 0) DrainActionQueue(); }
        }

        // Wie im Flash Player: Bildaktionen, die ein gotoAndStop/Play innerhalb eines Skripts auslöst, laufen erst NACH dem
        // aktuellen Aktionsblock (sonst überschreibt z.B. ein folgendes play() das stop() des Zielbilds -> Endlosschleifen).
        int _inActions;
        bool _draining;
        readonly Queue<(Instance inst, byte[] code)> _actionQueue = new Queue<(Instance, byte[])>();

        void QueueOrRun(Instance inst, byte[] code)
        {
            if (_inActions > 0) _actionQueue.Enqueue((inst, code));
            else ExecFrameActions(inst, code);
        }

        void DrainActionQueue()
        {
            if (_draining) return;
            _draining = true;
            try
            {
                int guard = 0;
                while (_actionQueue.Count > 0 && guard++ < 10000)
                {
                    var (inst, code) = _actionQueue.Dequeue();
                    ExecFrameActions(inst, code);
                }
            }
            finally { _draining = false; }
        }

        /// <summary>Aufruf von außen (Eingabe/Maus) wie ein Aktionsblock behandeln: ausgelöste Bildaktionen danach ausführen.</summary>
        object InvokeTop(AsFunction fn, object thisObj, object[] args)
        {
            _inActions++;
            try { return Invoke(fn, thisObj, args); }
            finally { _inActions--; if (_inActions == 0) DrainActionQueue(); }
        }

        // ------------------------------------------------------------------ Interpreter
        bool Exec(byte[] a, int start, int end, Instance self, Dictionary<string, object> locals, object thisObj, out object ret)
        {
            ret = Undefined;
            var stack = new List<object>(32);
            string[] pool = Array.Empty<string>();
            var target = self;
            int p = start, guard = 0;
            object Pop() { if (stack.Count == 0) return Undefined; var v = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); return v; }
            void Push(object v) => stack.Add(v);
            while (p < end && guard++ < 200000)
            {
                int op = a[p++];
                if (op == 0) break;
                int len = 0, q = p;
                if (op >= 0x80) { if (p + 2 > end) break; len = a[p] | (a[p + 1] << 8); q = p + 2; p = q + len; }
                switch (op)
                {
                    // --- Zeitleiste
                    case 0x04: if (target != null) GotoFrame(target, target.Frame + 1, true); break;
                    case 0x05: if (target != null) GotoFrame(target, target.Frame - 1, true); break;
                    case 0x06: if (target != null) target.Playing = true; break;
                    case 0x07: if (target != null) target.Playing = false; break;
                    case 0x81: if (target != null && len >= 2) { target.Playing = false; GotoFrame(target, a[q] | (a[q + 1] << 8), true); } break;
                    case 0x8C:
                        if (target != null && target.Timeline.Labels.TryGetValue(CStr(a, q, q + len), out var lf)) { target.Playing = false; GotoFrame(target, lf, true); }
                        break;
                    case 0x9F:   // GotoFrame2
                        {
                            int flags = a[q];
                            var fv = Pop();
                            GotoValue(target, fv, (flags & 1) != 0);
                            break;
                        }
                    case 0x8B: { string t = CStr(a, q, q + len); target = string.IsNullOrEmpty(t) ? self : ResolveClip(self, t); break; }
                    case 0x20: { var t = Pop(); target = t is Instance ti ? ti : (ToStr(t).Length == 0 ? self : ResolveClip(self, ToStr(t))); break; }

                    // --- Stack/Konstanten
                    case 0x88:
                        {
                            int n = a[q] | (a[q + 1] << 8); var list = new string[n]; int r = q + 2;
                            for (int i = 0; i < n; i++) { int s0 = r; while (r < q + len && a[r] != 0) r++; list[i] = Encoding.UTF8.GetString(a, s0, r - s0); r++; }
                            pool = list; break;
                        }
                    case 0x96:
                        {
                            int r = q;
                            while (r < q + len)
                            {
                                int type = a[r++];
                                switch (type)
                                {
                                    case 0: { int s0 = r; while (r < q + len && a[r] != 0) r++; Push(Encoding.UTF8.GetString(a, s0, r - s0)); r++; break; }
                                    case 1: Push((double)BitConverter.ToSingle(a, r)); r += 4; break;
                                    case 2: Push(null); break;
                                    case 3: Push(Undefined); break;
                                    case 4: Push(_registers[a[r++]] ?? Undefined); break;
                                    case 5: Push(a[r++] != 0); break;
                                    case 6:
                                        {
                                            var b = new byte[8];
                                            Buffer.BlockCopy(a, r + 4, b, 0, 4); Buffer.BlockCopy(a, r, b, 4, 4);
                                            Push(BitConverter.ToDouble(b, 0)); r += 8; break;
                                        }
                                    case 7: Push((double)BitConverter.ToInt32(a, r)); r += 4; break;
                                    case 8: { int i = a[r++]; Push(i < pool.Length ? pool[i] : (object)Undefined); break; }
                                    case 9: { int i = a[r] | (a[r + 1] << 8); r += 2; Push(i < pool.Length ? pool[i] : (object)Undefined); break; }
                                    default: r = q + len; break;
                                }
                            }
                            break;
                        }
                    case 0x17: Pop(); break;
                    case 0x4C: { var v = Pop(); Push(v); Push(v); break; }
                    case 0x4D: { var x = Pop(); var y = Pop(); Push(x); Push(y); break; }   // StackSwap
                    case 0x87: if (len >= 1) _registers[a[q]] = stack.Count > 0 ? stack[stack.Count - 1] : Undefined; break;

                    // --- Variablen
                    case 0x1C: Push(GetVar(target, locals, ToStr(Pop()))); break;
                    case 0x1D: { var v = Pop(); SetVar(target, locals, ToStr(Pop()), v); break; }
                    case 0x3C: { var v = Pop(); string n = ToStr(Pop()); if (locals != null) locals[n] = v; else SetVar(target, null, n, v); break; }   // DefineLocal
                    case 0x41: { string n = ToStr(Pop()); if (locals != null && !locals.ContainsKey(n)) locals[n] = Undefined; break; }               // DefineLocal2
                    case 0x4E: { var name = Pop(); var obj = Pop(); Push(GetMember(obj, ToStr(name))); break; }
                    case 0x4F: { var v = Pop(); var name = Pop(); var obj = Pop(); SetMember(obj, ToStr(name), v); break; }
                    case 0x22: { int idx = (int)ToNum(Pop()); var t = Pop(); Push(GetProp(t is Instance ti ? ti : ResolveClip(target, ToStr(t)), idx)); break; }
                    case 0x23: { var v = Pop(); int idx = (int)ToNum(Pop()); var t = Pop(); SetProp(t is Instance ti ? ti : ResolveClip(target, ToStr(t)), idx, v); break; }
                    case 0x3A: { Pop(); Pop(); Push(true); break; }   // Delete
                    case 0x3B: { Pop(); Push(true); break; }          // Delete2
                    case 0x44: Push(TypeOf(Pop())); break;
                    case 0x45: Push(Pop() is Instance ti2 ? PathOf(ti2) : (object)Undefined); break;   // TargetPath
                    case 0x54: { Pop(); Pop(); Push(false); break; }  // InstanceOf

                    // --- Arithmetik / Vergleich / Logik
                    case 0x0A: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(x + b); break; }
                    case 0x0B: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(x - b); break; }
                    case 0x0C: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(x * b); break; }
                    case 0x0D: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(b == 0 ? double.NaN : x / b); break; }
                    case 0x3F: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(b == 0 ? double.NaN : x % b); break; }
                    case 0x0E: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(x == b); break; }
                    case 0x0F: { double b = ToNum(Pop()), x = ToNum(Pop()); Push(x < b); break; }
                    case 0x10: { bool b = ToBool(Pop()), x = ToBool(Pop()); Push(x && b); break; }
                    case 0x11: { bool b = ToBool(Pop()), x = ToBool(Pop()); Push(x || b); break; }
                    case 0x12: Push(!ToBool(Pop())); break;
                    case 0x13: { string b = ToStr(Pop()), x = ToStr(Pop()); Push(x == b); break; }
                    case 0x14: Push((double)ToStr(Pop()).Length); break;
                    case 0x15: { int cnt = (int)ToNum(Pop()), idx = (int)ToNum(Pop()); string s = ToStr(Pop()); Push(SubStr(s, idx - 1, cnt)); break; }
                    case 0x18: Push(Math.Truncate(ToNum(Pop()))); break;
                    case 0x21: { string b = ToStr(Pop()), x = ToStr(Pop()); Push(x + b); break; }
                    case 0x29: { string b = ToStr(Pop()), x = ToStr(Pop()); Push(string.CompareOrdinal(x, b) < 0); break; }
                    case 0x47: { var b = Pop(); var x = Pop(); if (x is string || b is string) Push(ToStr(x) + ToStr(b)); else Push(ToNum(x) + ToNum(b)); break; }
                    case 0x48: { var b = Pop(); var x = Pop(); Push(Less(x, b)); break; }
                    case 0x67: { var b = Pop(); var x = Pop(); Push(Less(b, x)); break; }
                    case 0x49: { var b = Pop(); var x = Pop(); Push(LooseEq(x, b)); break; }
                    case 0x66: { var b = Pop(); var x = Pop(); Push(StrictEq(x, b)); break; }
                    case 0x50: Push(ToNum(Pop()) + 1); break;
                    case 0x51: Push(ToNum(Pop()) - 1); break;
                    case 0x4A: Push(ToNum(Pop())); break;
                    case 0x4B: Push(ToStr(Pop())); break;
                    case 0x60: { int b = (int)ToNum(Pop()), x = (int)ToNum(Pop()); Push((double)(x & b)); break; }
                    case 0x61: { int b = (int)ToNum(Pop()), x = (int)ToNum(Pop()); Push((double)(x | b)); break; }
                    case 0x62: { int b = (int)ToNum(Pop()), x = (int)ToNum(Pop()); Push((double)(x ^ b)); break; }
                    case 0x63: { int b = (int)ToNum(Pop()), x = (int)ToNum(Pop()); Push((double)(x << (b & 31))); break; }
                    case 0x64: { int b = (int)ToNum(Pop()), x = (int)ToNum(Pop()); Push((double)(x >> (b & 31))); break; }
                    case 0x30: Push((double)UnityEngine.Random.Range(0, Mathf.Max(1, (int)ToNum(Pop())))); break;   // RandomNumber
                    case 0x34: Push((double)(Time.realtimeSinceStartup - StartTime) * 1000.0); break;              // GetTime
                    case 0x32: { var s = ToStr(Pop()); Push((double)(s.Length > 0 ? s[0] : 0)); break; }
                    case 0x33: Push(((char)(int)ToNum(Pop())).ToString()); break;

                    // --- Sprünge
                    case 0x99: p = p + (short)(a[q] | (a[q + 1] << 8)); break;
                    case 0x9D: { short off = (short)(a[q] | (a[q + 1] << 8)); if (ToBool(Pop())) p = p + off; break; }

                    // --- Funktionen / Objekte
                    case 0x9B:   // DefineFunction
                        {
                            int r = q; int s0 = r; while (a[r] != 0) r++;
                            string fname = Encoding.UTF8.GetString(a, s0, r - s0); r++;
                            int np = a[r] | (a[r + 1] << 8); r += 2;
                            var ps = new string[np];
                            for (int i = 0; i < np; i++) { int s1 = r; while (a[r] != 0) r++; ps[i] = Encoding.UTF8.GetString(a, s1, r - s1); r++; }
                            int size = a[r] | (a[r + 1] << 8);
                            var fn = new AsFunction { Code = a, Start = p, Length = size, Params = ps, Pool = pool, Scope = target, Name = fname };
                            p += size;
                            if (fname.Length == 0) Push(fn); else SetVar(target, locals, fname, fn);
                            break;
                        }
                    case 0x3D:   // CallFunction
                        {
                            string fname = ToStr(Pop()); int n = (int)ToNum(Pop());
                            var args = new object[Mathf.Max(0, n)]; for (int i = 0; i < args.Length; i++) args[i] = Pop();
                            Push(CallFunction(target, locals, fname, args));
                            break;
                        }
                    case 0x52:   // CallMethod
                        {
                            var mname = Pop(); var obj = Pop(); int n = (int)ToNum(Pop());
                            var args = new object[Mathf.Max(0, n)]; for (int i = 0; i < args.Length; i++) args[i] = Pop();
                            Push(CallMethod(obj, mname is Undef || mname == null ? "" : ToStr(mname), args, target));
                            break;
                        }
                    case 0x3E: ret = Pop(); return true;   // Return
                    case 0x40:   // NewObject
                        {
                            string cname = ToStr(Pop()); int n = (int)ToNum(Pop());
                            var args = new object[Mathf.Max(0, n)]; for (int i = 0; i < args.Length; i++) args[i] = Pop();
                            if (cname == "Array") { var arr = new AsArray(); if (n > 1 || (n == 1 && !(args[0] is double))) arr.Items.AddRange(args); Push(arr); }
                            else Push(new AsObject());
                            break;
                        }
                    case 0x42: { int n = (int)ToNum(Pop()); var arr = new AsArray(); for (int i = 0; i < n; i++) arr.Items.Add(Pop()); Push(arr); break; }   // InitArray
                    case 0x43: { int n = (int)ToNum(Pop()); var o = new AsObject(); for (int i = 0; i < n; i++) { var v = Pop(); o.Members[ToStr(Pop())] = v; } Push(o); break; }   // InitObject
                    case 0x46: Pop(); Push(null); break;   // Enumerate (Ende-Markierung)
                    case 0x94:   // With – Block ohne Objekt-Scope ausführen
                        Pop(); break;

                    // --- Sonstiges
                    case 0x26: Pop(); break;   // Trace
                    case 0x83:
                        {
                            int r = q; int s0 = r; while (r < q + len && a[r] != 0) r++;
                            string url = Encoding.UTF8.GetString(a, s0, r - s0); r++;
                            int s1 = r; while (r < q + len && a[r] != 0) r++;
                            string win = r > s1 ? Encoding.UTF8.GetString(a, s1, r - s1) : "";
                            RaiseUrl(url, win);
                            break;
                        }
                    case 0x9A: { string win = ToStr(Pop()); string url = ToStr(Pop()); RaiseUrl(url, win); break; }
                    case 0x24: { Pop(); Pop(); Pop(); break; }   // CloneSprite
                    case 0x25: Pop(); break;                      // RemoveSprite
                    case 0x27:   // StartDrag: Ziel, lockCenter, constrain [, x1, y1, x2, y2]
                        {
                            var t = Pop(); bool lockC = ToBool(Pop()); bool con = ToBool(Pop());
                            Rect? r = null;
                            if (con) { float y2 = (float)ToNum(Pop()), x2 = (float)ToNum(Pop()), y1 = (float)ToNum(Pop()), x1 = (float)ToNum(Pop()); r = Rect.MinMaxRect(x1, y1, x2, y2); }
                            var ti = t is Instance tin ? tin : ResolveClip(self, ToStr(t));
                            if (ti != null) BeginDrag(ti.Owner, lockC, r);
                            break;
                        }
                    case 0x28: EndDrag(); break;                  // EndDrag
                    case 0x8A: case 0x8D: break;                  // WaitForFrame
                    default: break;
                }
            }
            return false;
        }

        float StartTime { get { if (_startTime < 0f) _startTime = Time.realtimeSinceStartup; return _startTime; } }

        void RaiseUrl(string url, string args)
        {
            if (url != null && url.StartsWith("FSCommand:", StringComparison.OrdinalIgnoreCase))
                FSCommand?.Invoke(url.Substring(10), args);
        }

        // ------------------------------------------------------------------ Werte
        static double ToNum(object v)
        {
            switch (v)
            {
                case double d: return d;
                case bool b: return b ? 1 : 0;
                case string s: return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : (s.Length == 0 ? 0 : double.NaN);
                case null: return 0;
                default: return v is Undef ? 0 : double.NaN;
            }
        }

        static bool ToBool(object v)
        {
            switch (v)
            {
                case bool b: return b;
                case double d: return d != 0 && !double.IsNaN(d);
                case string s: return s.Length > 0 && !(double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r == 0);
                case null: return false;
                default: return !(v is Undef);
            }
        }

        static string ToStr(object v)
        {
            switch (v)
            {
                case string s: return s;
                case double d: return double.IsNaN(d) ? "NaN" : (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15 ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture));
                case bool b: return b ? "true" : "false";
                case null: return "null";
                case Instance i: return PathOf(i);
                case AsArray arr: { var sb = new StringBuilder(); for (int k = 0; k < arr.Items.Count; k++) { if (k > 0) sb.Append(','); sb.Append(ToStr(arr.Items[k])); } return sb.ToString(); }
                default: return v is Undef ? "" : v.ToString();
            }
        }

        static string TypeOf(object v)
        {
            if (v is double) return "number";
            if (v is string) return "string";
            if (v is bool) return "boolean";
            if (v is Instance) return "movieclip";
            if (v is AsFunction || v is Builtin) return "function";
            if (v == null) return "null";
            if (v is Undef) return "undefined";
            return "object";
        }

        static bool Less(object x, object b)
        {
            if (x is string xs && b is string bs) return string.CompareOrdinal(xs, bs) < 0;
            return ToNum(x) < ToNum(b);
        }

        static bool StrictEq(object x, object b)
        {
            if (x == null || b == null) return x == b;
            if (x is Undef || b is Undef) return x is Undef && b is Undef;
            if (x.GetType() != b.GetType()) return false;
            if (x is double xd) return xd == (double)b;
            if (x is string xs) return xs == (string)b;
            if (x is bool xb) return xb == (bool)b;
            return ReferenceEquals(x, b);
        }

        static bool LooseEq(object x, object b)
        {
            bool xn = x == null || x is Undef, bn = b == null || b is Undef;
            if (xn || bn) return xn && bn;
            if (x is string xs && b is string bs) return xs == bs;
            if (x is double || b is double || x is bool || b is bool)
            {
                double dx = ToNum(x), db = ToNum(b);
                return dx == db;
            }
            return ReferenceEquals(x, b);
        }

        static string SubStr(string s, int start, int count)
        {
            if (start < 0) start = 0;
            if (start >= s.Length || count <= 0) return "";
            return s.Substring(start, Mathf.Min(count, s.Length - start));
        }

        static string CStr(byte[] a, int q, int end)
        {
            int s0 = q; while (q < end && a[q] != 0) q++;
            return Encoding.UTF8.GetString(a, s0, q - s0);
        }

        // ------------------------------------------------------------------ Clips / Pfade
        Instance ResolveClip(Instance from, string path)
        {
            if (string.IsNullOrEmpty(path)) return from;
            if (path == "_root" || path == "/" || path == "_level0") return Root;
            path = path.Replace("_root.", "/").Replace("_level0.", "/");
            if (path.StartsWith("_root/")) path = path.Substring(5);
            if (path.StartsWith("/")) return Find(path)?.Sprite ?? (path == "/" ? Root : null);
            var inst = from;
            // Slash-Syntax ("../sq0", "../../:x") nur an '/' trennen – sonst zerfällt ".." in leere Teile; Punkt-Syntax ("_parent.sq0") an '.'
            var parts = path.IndexOf('/') >= 0 ? path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                                               : path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (inst == null) return null;
                if (part == ".." || part == "_parent") { inst = inst.Owner?.Parent; continue; }
                if (part == "this") continue;
                DisplayObject hit = null;
                foreach (var d in inst.List.Values) if (string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase)) { hit = d; break; }
                inst = hit?.Sprite;
            }
            return inst;
        }

        Instance Child(Instance inst, string name)
        {
            if (inst == null) return null;
            foreach (var d in inst.List.Values) if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d.Sprite;
            return null;
        }

        /// <summary>Benanntes Kind als Skriptwert: Clip-Instanz oder Button-Objekt.</summary>
        object ChildObj(Instance inst, string name)
        {
            if (inst == null) return null;
            foreach (var d in inst.List.Values)
            {
                if (!string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Sprite != null) return d.Sprite;
                if (Movie.Chars.TryGetValue(d.CharId, out var ch) && ch is VmfMovie.Button) return BtnObj(d);
                return null;
            }
            return null;
        }

        void GotoValue(Instance target, object fv, bool play)
        {
            if (target == null) return;
            if (fv is string s && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                // "clip:label" erlaubt
                int c = s.LastIndexOf(':');
                if (c >= 0) { target = ResolveClip(target, s.Substring(0, c)); s = s.Substring(c + 1); if (target == null) return; }
                if (!target.Timeline.Labels.TryGetValue(s, out var f)) return;
                target.Playing = play;
                GotoFrame(target, f, true);   // wie Flash: stop() im Ziel-Frame gilt
            }
            else
            {
                int f = (int)ToNum(fv) - 1;
                target.Playing = play;
                GotoFrame(target, f, true);   // wie Flash: stop() im Ziel-Frame gilt
            }
        }

        string VarKey(Instance scope, string name) => (scope == null ? "/" : PathOf(scope)) + ":" + name;

        object GetVar(Instance scope, Dictionary<string, object> locals, string name)
        {
            if (string.IsNullOrEmpty(name)) return Undefined;
            if (locals != null && locals.TryGetValue(name, out var lv)) return lv;
            switch (name)
            {
                case "_root": case "_level0": return Root;
                case "this": return scope;
                case "_parent": return scope?.Owner?.Parent ?? (object)Undefined;
                case "Math": return MathObj;
                case "Key": return KeyObj;
                case "Stage": return StageObj;
                case "_global": return _globalObj;
            }
            // Pfad-Schreibweisen
            if (name.IndexOf(':') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf('.') >= 0)
            {
                int c = name.LastIndexOf(':'), dot = name.LastIndexOf('.');
                if (c < 0 && (dot < 0 || name.IndexOf('/') >= 0))   // reiner Clip-Pfad ("../sq0", "/pause_menu")
                {
                    var clip = ResolveClip(scope, name);
                    return clip != null ? clip : Undefined;
                }
                string path = c >= 0 ? name.Substring(0, c) : name.Substring(0, dot);
                string var = c >= 0 ? name.Substring(c + 1) : name.Substring(dot + 1);
                var owner = path.Length == 0 ? Root : ResolveClip(scope, path);
                if (owner == null)
                {
                    // Punkt-Pfad über Objektverweise ("_root.Clip.IsCardLoaded": Clip ist eine Variable, kein Kind-Clip)
                    if (c < 0 && name.IndexOf('/') < 0) { var o = WalkMembers(scope, locals, path); return o != null && !(o is Undef) ? GetMember(o, var) : Undefined; }
                    return Undefined;
                }
                return GetMember(owner, var);
            }
            // Clip-Variable, Kind-Clip, global
            var key = VarKey(scope, name);
            if (_objVars.TryGetValue(key, out var ov)) return ov;
            if (_vars.TryGetValue(key, out var sv)) return sv;
            var ch = ChildObj(scope, name);
            if (ch != null) return ch;
            if (_globals.TryGetValue(name, out var gv)) return gv;
            if (_objVars.TryGetValue(VarKey(Root, name), out ov)) return ov;
            if (_vars.TryGetValue(VarKey(Root, name), out sv)) return sv;
            return Undefined;
        }

        void SetVar(Instance scope, Dictionary<string, object> locals, string name, object v)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (locals != null && locals.ContainsKey(name)) { locals[name] = v; return; }
            if (name.IndexOf(':') >= 0 || name.IndexOf('.') >= 0)
            {
                int c = name.LastIndexOf(':'), dot = name.LastIndexOf('.');
                string path = c >= 0 ? name.Substring(0, c) : name.Substring(0, dot);
                string var = c >= 0 ? name.Substring(c + 1) : name.Substring(dot + 1);
                var owner = path.Length == 0 ? Root : ResolveClip(scope, path);
                if (owner != null) SetMember(owner, var, v);
                else if (c < 0 && name.IndexOf('/') < 0)
                {
                    var o = WalkMembers(scope, locals, path);   // "_root.Clip.IsCardLoaded = true"
                    if (o != null && !(o is Undef)) SetMember(o, var, v);
                }
                return;
            }
            StoreClipVar(scope ?? Root, name, v);
        }

        /// <summary>"a.b.c" Glied für Glied auflösen: erstes über GetVar (Variable, _root, this …), danach Mitglieder (auch Objektverweise).</summary>
        object WalkMembers(Instance scope, Dictionary<string, object> locals, string dotted)
        {
            var parts = dotted.Split('.');
            object o = GetVar(scope, locals, parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                if (o == null || o is Undef) return Undefined;
                o = GetMember(o, parts[i]);
            }
            return o;
        }

        void StoreClipVar(Instance clip, string name, object v)
        {
            var key = VarKey(clip, name);
            if (string.Equals(name, "onEnterFrame", StringComparison.OrdinalIgnoreCase))
            {
                if (v is AsFunction) { if (!_enterFrame.Contains(clip)) _enterFrame.Add(clip); }
                else _enterFrame.Remove(clip);
            }
            if (v is string || v is double || v is bool) { _vars[key] = ToStr(v); _objVars.Remove(key); }
            else if (v == null || v is Undef) { _vars.Remove(key); _objVars.Remove(key); }
            else { _objVars[key] = v; _vars.Remove(key); }
            // typisierte Werte für Vergleiche behalten
            if (v is double || v is bool) _objVars[key] = v;
        }

        readonly AsObject _globalObj = new AsObject();

        object GetMember(object obj, string name)
        {
            switch (obj)
            {
                case Instance inst:
                    {
                        var d = inst.Owner;
                        switch (name)
                        {
                            case "_x": return d != null ? (double)d.Matrix.Tx : 0.0;
                            case "_y": return d != null ? (double)d.Matrix.Ty : 0.0;
                            case "_alpha": return d != null ? (double)(d.Cx.Mul.w * 100f) : 100.0;
                            case "_visible": return d == null || d.Visible;
                            case "_currentframe": return (double)(inst.Frame + 1);
                            case "_totalframes": case "_framesloaded": return (double)inst.Timeline.FrameCount;
                            case "_name": return d?.Name ?? "";
                            case "_xscale": return d != null ? (double)(new Vector2(d.Matrix.A, d.Matrix.B).magnitude * 100f) : 100.0;
                            case "_yscale": return d != null ? (double)(new Vector2(d.Matrix.C, d.Matrix.D).magnitude * 100f) : 100.0;
                            case "_rotation": return d != null ? (double)(Mathf.Atan2(d.Matrix.B, d.Matrix.A) * Mathf.Rad2Deg) : 0.0;
                            case "_parent": return inst.Owner?.Parent ?? (object)Undefined;
                            case "_target": return PathOf(inst);
                            case "_width":
                            case "_height":
                                {
                                    var r = LocalBounds(d);
                                    return r.HasValue ? (double)(name == "_width" ? r.Value.width : r.Value.height) : 0.0;
                                }
                        }
                        var key = VarKey(inst, name);
                        if (_objVars.TryGetValue(key, out var ov)) return ov;
                        if (_vars.TryGetValue(key, out var sv)) return sv;
                        var ch = ChildObj(inst, name);
                        if (ch != null) return ch;
                        if (string.Equals(name, "gotoAndStop", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "gotoAndPlay", StringComparison.OrdinalIgnoreCase)) return new Builtin(name);
                        return Undefined;
                    }
                case AsObject o: return o.Members.TryGetValue(name, out var mv) ? mv : Undefined;
                case ButtonObj bo:
                    {
                        var d = bo.D;
                        switch (name)
                        {
                            case "_x": return (double)d.Matrix.Tx;
                            case "_y": return (double)d.Matrix.Ty;
                            case "_alpha": return (double)(d.Cx.Mul.w * 100f);
                            case "_visible": return d.Visible;
                            case "_name": return d.Name ?? "";
                            case "_parent": return d.Parent ?? (object)Undefined;
                            case "enabled": if (!bo.Members.ContainsKey(name)) return true; break;
                        }
                        return bo.Members.TryGetValue(name, out var bv) ? bv : Undefined;
                    }
                case FlashList fl:
                    if (name == "Name") return fl.Name;
                    return Undefined;
                case AsArray arr:
                    if (name == "length") return (double)arr.Items.Count;
                    if (int.TryParse(name, out int idx)) return idx >= 0 && idx < arr.Items.Count ? arr.Items[idx] : Undefined;
                    return Undefined;
                case string s:
                    if (name == "length") return (double)s.Length;
                    return Undefined;
                case Builtin b when b == MathObj:
                    if (name == "PI") return Math.PI;
                    return new Builtin("Math." + name);
                case Builtin b when b == StageObj:
                    if (name == "width") return (double)VmfMovie.StageWidth;
                    if (name == "height") return (double)VmfMovie.StageHeight;
                    return Undefined;
            }
            return Undefined;
        }

        void SetMember(object obj, string name, object v)
        {
            switch (obj)
            {
                case Instance inst:
                    {
                        int idx = PropIndex(name);
                        if (idx >= 0) { SetProp(inst, idx, v); return; }
                        StoreClipVar(inst, name, v);
                        return;
                    }
                case AsObject o: o.Members[name] = v; return;
                case ButtonObj bo:
                    {
                        int bi = PropIndex(name);
                        if (bi >= 0) { SetPropD(bo.D, bi, v); return; }
                        bo.Members[name] = v;
                        return;
                    }
                case AsArray arr:
                    if (int.TryParse(name, out int i) && i >= 0) { while (arr.Items.Count <= i) arr.Items.Add(Undefined); arr.Items[i] = v; }
                    return;
            }
        }

        static int PropIndex(string name)
        {
            switch (name)
            {
                case "_x": return 0;
                case "_y": return 1;
                case "_xscale": return 2;
                case "_yscale": return 3;
                case "_alpha": return 6;
                case "_visible": return 7;
                case "_rotation": return 10;
            }
            return -1;
        }

        object GetProp(Instance inst, int idx)
        {
            if (inst == null) return Undefined;
            switch (idx)
            {
                case 0: return GetMember(inst, "_x");
                case 1: return GetMember(inst, "_y");
                case 2: return GetMember(inst, "_xscale");
                case 3: return GetMember(inst, "_yscale");
                case 4: return GetMember(inst, "_currentframe");
                case 5: return GetMember(inst, "_totalframes");
                case 6: return GetMember(inst, "_alpha");
                case 7: return GetMember(inst, "_visible");
                case 8: return GetMember(inst, "_width");
                case 9: return GetMember(inst, "_height");
                case 10: return GetMember(inst, "_rotation");
                case 11: return PathOf(inst);
                case 12: return GetMember(inst, "_framesloaded");
                case 13: return GetMember(inst, "_name");
            }
            return 0.0;
        }

        void SetProp(Instance inst, int idx, object v) => SetPropD(inst?.Owner, idx, v);

        void SetPropD(DisplayObject d, int idx, object v)
        {
            if (d == null) return;
            float f = (float)ToNum(v);
            if (idx == 6) d.ScriptCx = true;
            else if (idx != 7) d.ScriptMatrix = true;
            switch (idx)
            {
                case 0: d.Matrix.Tx = f; break;
                case 1: d.Matrix.Ty = f; break;
                case 2: { float cur = new Vector2(d.Matrix.A, d.Matrix.B).magnitude; if (cur > 1e-6f) { float k = f / 100f / cur; d.Matrix.A *= k; d.Matrix.B *= k; } else { d.Matrix.A = f / 100f; } break; }
                case 3: { float cur = new Vector2(d.Matrix.C, d.Matrix.D).magnitude; if (cur > 1e-6f) { float k = f / 100f / cur; d.Matrix.C *= k; d.Matrix.D *= k; } else { d.Matrix.D = f / 100f; } break; }
                case 6: d.Cx.Mul.w = Mathf.Clamp01(f / 100f); break;
                case 7: d.Visible = ToBool(v); break;
                case 10:
                    {
                        float sx = new Vector2(d.Matrix.A, d.Matrix.B).magnitude, sy = new Vector2(d.Matrix.C, d.Matrix.D).magnitude;
                        float r = f * Mathf.Deg2Rad;
                        d.Matrix.A = Mathf.Cos(r) * sx; d.Matrix.B = Mathf.Sin(r) * sx; d.Matrix.C = -Mathf.Sin(r) * sy; d.Matrix.D = Mathf.Cos(r) * sy;
                        break;
                    }
            }
        }

        // ------------------------------------------------------------------ Aufrufe
        object Invoke(AsFunction fn, object thisObj, object[] args)
        {
            if (_depth > 24) return Undefined;
            var locals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fn.Params.Length; i++) locals[fn.Params[i]] = i < args.Length ? args[i] : Undefined;
            var argArr = new AsArray(); argArr.Items.AddRange(args);
            locals["arguments"] = argArr;
            // wie Flash: unqualifizierte Namen im Funktionsrumpf gelten im Clip, in dem die Funktion definiert wurde
            // (z.B. loading_anim.onEnterFrame = function(){ if (isListLoaded) ... } liest isListLoaded des Menüs); "this" bleibt der Aufrufer
            var scope = fn.Scope ?? thisObj as Instance;
            locals["this"] = thisObj ?? (object)scope;
            _depth++;
            try
            {
                // Konstantenpool der Definition gilt im Funktionsrumpf weiter
                var code = PrefixPool(fn);
                Exec(code, 0, code.Length, scope, locals, thisObj, out var ret);
                return ret;
            }
            finally { _depth--; }
        }

        // Funktionsrumpf mit vorangestelltem ConstantPool (der Pool der Definition gilt im Rumpf)
        readonly Dictionary<AsFunction, byte[]> _fnCode = new Dictionary<AsFunction, byte[]>();
        byte[] PrefixPool(AsFunction fn)
        {
            if (_fnCode.TryGetValue(fn, out var c)) return c;
            var ms = new System.IO.MemoryStream();
            if (fn.Pool != null && fn.Pool.Length > 0)
            {
                var body = new System.IO.MemoryStream();
                body.WriteByte((byte)(fn.Pool.Length & 0xFF)); body.WriteByte((byte)(fn.Pool.Length >> 8));
                foreach (var s in fn.Pool) { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); body.WriteByte(0); }
                var bb = body.ToArray();
                ms.WriteByte(0x88); ms.WriteByte((byte)(bb.Length & 0xFF)); ms.WriteByte((byte)(bb.Length >> 8)); ms.Write(bb, 0, bb.Length);
            }
            int end = Mathf.Min(fn.Code.Length, fn.Start + fn.Length);
            ms.Write(fn.Code, fn.Start, Mathf.Max(0, end - fn.Start));
            c = ms.ToArray();
            _fnCode[fn] = c;
            return c;
        }

        object CallFunction(Instance scope, Dictionary<string, object> locals, string name, object[] args)
        {
            var f = GetVar(scope, locals, name);
            if (f is AsFunction fn) return Invoke(fn, scope, args);
            switch (name)
            {
                case "Number": return args.Length > 0 ? ToNum(args[0]) : 0.0;
                case "String": return args.Length > 0 ? ToStr(args[0]) : "";
                case "Boolean": return args.Length > 0 && ToBool(args[0]);
                case "int": return args.Length > 0 ? Math.Truncate(ToNum(args[0])) : 0.0;
                case "parseInt": return args.Length > 0 && int.TryParse(ToStr(args[0]).Trim(), out int pi) ? (double)pi : double.NaN;
                case "parseFloat": return args.Length > 0 ? ToNum(args[0]) : double.NaN;
                case "isNaN": return args.Length == 0 || double.IsNaN(ToNum(args[0]));
                case "getTimer": return (double)(Time.realtimeSinceStartup - StartTime) * 1000.0;
                case "setInterval": case "clearInterval": case "setTimeout": case "clearTimeout": return 0.0;
                case "fscommand": FSCommand?.Invoke(args.Length > 0 ? ToStr(args[0]) : "", args.Length > 1 ? ToStr(args[1]) : ""); return Undefined;
                case "Array": { var arr = new AsArray(); arr.Items.AddRange(args); return arr; }
                case "Object": return new AsObject();
                case "stopDrag": EndDrag(); return Undefined;
                case "getTextLength": return (double)MeasureVarText(args.Length > 0 ? ToStr(args[0]) : "");   // nativ im Spiel
                case "ASFlashList":
                    {
                        // ASFlashList(Besitzer-Clip, Listenname, sichtbare Zeilen, Spalten) – im Spiel nativ
                        var l = new FlashList
                        {
                            Owner = args.Length > 0 ? args[0] as Instance ?? scope : scope,
                            Name = args.Length > 1 ? ToStr(args[1]) : "",
                            ViewSize = args.Length > 2 ? Mathf.Max(1, (int)ToNum(args[2])) : 9,
                            Columns = args.Length > 3 ? (int)ToNum(args[3]) : 1,
                        };
                        _activeList = l;
                        try { ListCreated?.Invoke(l); }
                        catch (Exception ex) { Debug.LogWarning("[TDU2] ASFlashList " + l.Name + ": " + ex.Message); }
                        return l;
                    }
                // MovieClip-Methoden als globale Funktion im aktuellen Ziel (tellTarget/SetTarget): z.B. menu_sq.attachOtherIcons()
                // ruft nach SetTarget(sq0/ic) "attachMovie(targetMovie, 'icLoaded', 0)" auf -> Symbole der Spielerkarte
                case "attachMovie":
                case "createEmptyMovieClip":
                case "duplicateMovieClip":
                case "removeMovieClip":
                case "gotoAndStop":
                case "gotoAndPlay":
                case "play":
                case "stop":
                case "nextFrame":
                case "prevFrame":
                case "swapDepths":
                case "getNextHighestDepth":
                case "getDepth":
                case "localToGlobal":
                case "globalToLocal":
                case "getBounds":
                case "hitTest":
                case "setMask":
                case "startDrag":
                    if (scope != null) return CallMethod(scope, name, args, scope);
                    break;
            }
            return Undefined;
        }

        object CallMethod(object obj, string name, object[] args, Instance scope)
        {
            if (name.Length == 0) return obj is AsFunction f0 ? Invoke(f0, scope, args) : Undefined;
            switch (obj)
            {
                case Instance inst:
                    {
                        switch (name)
                        {
                            case "gotoAndStop": if (args.Length > 0) GotoValue(inst, args[0], false); return Undefined;
                            case "gotoAndPlay": if (args.Length > 0) GotoValue(inst, args[0], true); return Undefined;
                            case "play": inst.Playing = true; return Undefined;
                            case "stop": inst.Playing = false; return Undefined;
                            case "nextFrame": GotoFrame(inst, inst.Frame + 1, true); inst.Playing = false; return Undefined;
                            case "prevFrame": GotoFrame(inst, inst.Frame - 1, true); inst.Playing = false; return Undefined;
                            case "getBytesLoaded": case "getBytesTotal": return 1.0;
                            case "hitTest": return false;
                            case "getDepth": return (double)(inst.Owner?.Depth ?? 0);
                            case "removeMovieClip": inst.Owner?.Parent?.List.Remove(inst.Owner.Depth); return Undefined;
                            case "attachMovie":
                                {
                                    // attachMovie(Verknüpfungsname, Instanzname, Tiefe [, Init-Objekt])
                                    if (args.Length < 3 || !Movie.Exports.TryGetValue(ToStr(args[0]), out int cid)) return Undefined;
                                    var d = AttachTo(inst, cid, ToStr(args[1]), (int)ToNum(args[2]));
                                    if (d?.Sprite != null && args.Length > 3 && args[3] is AsObject init)
                                        foreach (var kv in init.Members) SetMember(d.Sprite, kv.Key, kv.Value);
                                    return d?.Sprite ?? (object)Undefined;
                                }
                            case "createEmptyMovieClip":
                                {
                                    if (args.Length < 2) return Undefined;
                                    var d = AttachEmpty(inst, ToStr(args[0]), (int)ToNum(args[1]));
                                    return d?.Sprite ?? (object)Undefined;
                                }
                            case "startDrag":
                                {
                                    bool lockC = args.Length > 0 && ToBool(args[0]);
                                    Rect? r = null;
                                    if (args.Length >= 5) r = Rect.MinMaxRect((float)ToNum(args[1]), (float)ToNum(args[2]), (float)ToNum(args[3]), (float)ToNum(args[4]));
                                    BeginDrag(inst.Owner, lockC, r);
                                    return Undefined;
                                }
                            case "stopDrag": EndDrag(); return Undefined;
                            case "swapDepths": case "setMask": case "loadMovie": case "unloadMovie": case "duplicateMovieClip": return Undefined;
                        }
                        var m = GetMember(inst, name);
                        if (m is AsFunction fn) return Invoke(fn, inst, args);
                        return Undefined;
                    }
                case string s:
                    switch (name)
                    {
                        case "substr": { int st = args.Length > 0 ? (int)ToNum(args[0]) : 0; if (st < 0) st = Mathf.Max(0, s.Length + st); int n = args.Length > 1 ? (int)ToNum(args[1]) : s.Length; return SubStr(s, st, n); }
                        case "substring": { int a0 = args.Length > 0 ? Mathf.Clamp((int)ToNum(args[0]), 0, s.Length) : 0, a1 = args.Length > 1 ? Mathf.Clamp((int)ToNum(args[1]), 0, s.Length) : s.Length; if (a1 < a0) { var t = a0; a0 = a1; a1 = t; } return s.Substring(a0, a1 - a0); }
                        case "charAt": { int i = args.Length > 0 ? (int)ToNum(args[0]) : 0; return i >= 0 && i < s.Length ? s[i].ToString() : ""; }
                        case "charCodeAt": { int i = args.Length > 0 ? (int)ToNum(args[0]) : 0; return i >= 0 && i < s.Length ? (double)s[i] : double.NaN; }
                        case "indexOf": return args.Length > 0 ? (double)s.IndexOf(ToStr(args[0]), StringComparison.Ordinal) : -1.0;
                        case "lastIndexOf": return args.Length > 0 ? (double)s.LastIndexOf(ToStr(args[0]), StringComparison.Ordinal) : -1.0;
                        case "toUpperCase": return s.ToUpperInvariant();
                        case "toLowerCase": return s.ToLowerInvariant();
                        case "split": { var arr = new AsArray(); foreach (var part in s.Split(new[] { args.Length > 0 ? ToStr(args[0]) : "," }, StringSplitOptions.None)) arr.Items.Add(part); return arr; }
                        case "toString": return s;
                    }
                    return Undefined;
                case AsArray arr:
                    switch (name)
                    {
                        case "push": arr.Items.AddRange(args); return (double)arr.Items.Count;
                        case "pop": { if (arr.Items.Count == 0) return Undefined; var v = arr.Items[arr.Items.Count - 1]; arr.Items.RemoveAt(arr.Items.Count - 1); return v; }
                        case "join": { var sep = args.Length > 0 ? ToStr(args[0]) : ","; var sb = new StringBuilder(); for (int i = 0; i < arr.Items.Count; i++) { if (i > 0) sb.Append(sep); sb.Append(ToStr(arr.Items[i])); } return sb.ToString(); }
                        case "toString": return ToStr(arr);
                    }
                    return Undefined;
                case Builtin b when b == MathObj:
                    {
                        double x = args.Length > 0 ? ToNum(args[0]) : 0, y = args.Length > 1 ? ToNum(args[1]) : 0;
                        switch (name)
                        {
                            case "floor": return Math.Floor(x);
                            case "ceil": return Math.Ceiling(x);
                            case "round": return Math.Floor(x + 0.5);
                            case "abs": return Math.Abs(x);
                            case "min": return Math.Min(x, y);
                            case "max": return Math.Max(x, y);
                            case "sqrt": return Math.Sqrt(x);
                            case "pow": return Math.Pow(x, y);
                            case "sin": return Math.Sin(x);
                            case "cos": return Math.Cos(x);
                            case "atan2": return Math.Atan2(x, y);
                            case "random": return (double)UnityEngine.Random.value;
                        }
                        return double.NaN;
                    }
                case Builtin b when b == KeyObj:
                    return name == "getCode" ? 0.0 : (object)false;
                case AsObject o:
                    if (o.Members.TryGetValue(name, out var mv) && mv is AsFunction ofn) return Invoke(ofn, null, args);
                    return Undefined;
                case FlashList fl: return ListMethod(fl, name);
                case ButtonObj bo:
                    if (bo.Members.TryGetValue(name, out var bm) && bm is AsFunction bfn) return Invoke(bfn, bo, args);
                    return Undefined;
                case double d:
                    if (name == "toString") return ToStr(d);
                    return Undefined;
            }
            return Undefined;
        }

        // ------------------------------------------------------------------ Eingabe von außen (wie das Spiel)
        /// <summary>
        /// Ruft eine im Film definierte Funktion auf (z.B. Move("up"), A(), B()) – an dem obersten sichtbaren Clip, der sie
        /// definiert (so landet die Eingabe beim gerade geöffneten Menü). Liefert true, wenn eine Funktion gefunden wurde.
        /// </summary>
        public bool Input(string function, params object[] args)
        {
            if (Root == null || string.IsNullOrEmpty(function)) return false;
            // native Liste hat den Fokus: Hoch/Runter bewegt die Auswahl (wie im Spiel)
            if (function == "Move" && _activeList != null && _activeList.Active && Shown(_activeList.Owner) && args != null && args.Length > 0)
            {
                string dir = args[0] as string;
                if (dir == "up" || dir == "down") { ListMove(_activeList, dir == "up" ? -1 : 1); return true; }
            }
            var order = new Dictionary<Instance, int>();
            int counter = 0;
            void Walk(Instance inst)
            {
                order[inst] = counter++;
                foreach (var d in inst.List.Values)
                    if (d.Sprite != null && d.Visible && d.ClipDepth == 0 && !(d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f)) Walk(d.Sprite);
            }
            Walk(Root);
            AsFunction best = null; Instance bestInst = null; int bestOrder = -1;
            string suffix = ":" + function;
            // Fokus wie im Spiel: _root.Clip = gerade aktives Menü (openMenu: "_root.Clip = this"). Zuerst dort bzw. in seinen
            // sichtbaren Unter-Clips suchen, sonst im ganzen Film (oberster sichtbarer Clip)
            var focus = GetMember(Root, "Clip") as Instance;
            string focusPath = focus != null && order.ContainsKey(focus) ? PathOf(focus) : null;
            for (int pass = focusPath != null ? 0 : 1; pass < 2 && best == null; pass++)
            {
                foreach (var kv in _objVars)
                {
                    if (!(kv.Value is AsFunction fn) || !kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    string path = kv.Key.Substring(0, kv.Key.Length - suffix.Length);
                    if (pass == 0 && !(path == focusPath || path.StartsWith(focusPath + "/", StringComparison.OrdinalIgnoreCase))) continue;
                    var inst = path == "/" || path.Length == 0 ? Root : ResolveClip(Root, path);
                    if (inst == null || !order.TryGetValue(inst, out int o)) continue;   // unsichtbar oder entfernt
                    if (pass == 0 && inst == focus) o = int.MaxValue;                    // das Fokus-Menü selbst hat Vorrang
                    if (o > bestOrder) { bestOrder = o; best = fn; bestInst = inst; }
                }
            }
            if (best == null) return false;
            var a = new object[args?.Length ?? 0];
            for (int i = 0; i < a.Length; i++) a[i] = args[i] is int iv ? (object)(double)iv : args[i] is float fv ? (object)(double)fv : args[i];
            try { InvokeTop(best, bestInst, a); }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Flash-Eingabe " + function + ": " + ex.Message); }
            return true;
        }

        // ------------------------------------------------------------------ Debug-Schnittstelle (Editor-Fenster "TDU2/Flash Debugger")
        /// <summary>Alle Variablen als "/pfad:name" -> Anzeigewert (Zahl/Bool/Text, sonst Typ).</summary>
        public List<KeyValuePair<string, string>> DebugVariables()
        {
            var res = new List<KeyValuePair<string, string>>(_vars.Count + 64);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _objVars)
            {
                if (kv.Value is AsFunction) continue;
                seen.Add(kv.Key);
                string v = kv.Value is double || kv.Value is bool || kv.Value is string ? ToStr(kv.Value)
                         : kv.Value is AsArray arr ? "[" + ToStr(arr) + "]"
                         : kv.Value is Instance inst ? "<clip " + PathOf(inst) + ">"
                         : kv.Value is FlashList fl ? "<ASFlashList " + fl.Name + " " + fl.Rows.Count + " Zeilen>"
                         : "<" + (kv.Value?.GetType().Name ?? "null") + ">";
                if (kv.Value is double) v += "  (Zahl)"; else if (kv.Value is bool) v += "  (Bool)";
                res.Add(new KeyValuePair<string, string>(kv.Key, v));
            }
            foreach (var kv in _vars) if (!seen.Contains(kv.Key)) res.Add(kv);
            res.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return res;
        }

        /// <summary>Alle definierten Funktionen als "/pfad:name (parameter)".</summary>
        public List<KeyValuePair<string, string>> DebugFunctions()
        {
            var res = new List<KeyValuePair<string, string>>();
            foreach (var kv in _objVars)
                if (kv.Value is AsFunction fn) res.Add(new KeyValuePair<string, string>(kv.Key, string.Join(", ", fn.Params ?? Array.Empty<string>())));
            res.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return res;
        }

        // Welche Namen (Variablen/Funktionen/Clips) ein Funktionsrumpf benutzt – aus den Push-Konstanten des Bytecodes
        readonly Dictionary<AsFunction, HashSet<string>> _fnNames = new Dictionary<AsFunction, HashSet<string>>();
        HashSet<string> NamesOf(AsFunction fn)
        {
            if (_fnNames.TryGetValue(fn, out var set)) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var a = fn.Code; int p = fn.Start, end = Mathf.Min(a.Length, fn.Start + fn.Length);
            var pool = fn.Pool ?? Array.Empty<string>();
            try
            {
                while (p < end)
                {
                    int op = a[p++];
                    if (op == 0) break;
                    if (op < 0x80) continue;
                    int len = a[p] | (a[p + 1] << 8); int q = p + 2; p = q + len;
                    if (op == 0x88)   // eigener Konstantenpool im Rumpf
                    {
                        int n = a[q] | (a[q + 1] << 8); q += 2; var np = new string[n];
                        for (int i = 0; i < n; i++) { int s0 = q; while (a[q] != 0) q++; np[i] = Encoding.UTF8.GetString(a, s0, q - s0); q++; }
                        pool = np;
                    }
                    else if (op == 0x96)
                    {
                        while (q < p)
                        {
                            int t = a[q++];
                            switch (t)
                            {
                                case 0: { int s0 = q; while (a[q] != 0) q++; Add(Encoding.UTF8.GetString(a, s0, q - s0)); q++; break; }
                                case 1: case 7: q += 4; break;
                                case 2: case 3: break;
                                case 4: case 5: q += 1; break;
                                case 6: q += 8; break;
                                case 8: { int i = a[q++]; if (i < pool.Length) Add(pool[i]); break; }
                                case 9: { int i = a[q] | (a[q + 1] << 8); q += 2; if (i < pool.Length) Add(pool[i]); break; }
                                default: q = p; break;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            _fnNames[fn] = set;
            return set;

            void Add(string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                set.Add(v);
                // "_root.NbCars", "../:x_pos", "/:Money" -> auch der reine Name
                int c = Mathf.Max(v.LastIndexOf(':'), v.LastIndexOf('.'));
                if (c >= 0 && c + 1 < v.Length) set.Add(v.Substring(c + 1));
            }
        }

        /// <summary>Funktionen ("/pfad:name"), deren Code den Variablennamen benutzt – die muss man nach einer Änderung aufrufen,
        /// damit Symbole/Frames/Anzeigen neu gesetzt werden (Textfelder mit dieser Variable aktualisieren sich von selbst).</summary>
        public List<string> DebugFunctionsUsing(string variable)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(variable)) return res;
            int c = variable.LastIndexOf(':');
            string name = c >= 0 ? variable.Substring(c + 1) : variable;
            foreach (var kv in _objVars)
                if (kv.Value is AsFunction fn && NamesOf(fn).Contains(name)) res.Add(kv.Key);
            res.Sort(StringComparer.Ordinal);
            return res;
        }

        /// <summary>Variable direkt setzen ("/pfad:name"); Zahlen und true/false werden typisiert gespeichert.</summary>
        public void DebugSetVariable(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!key.Contains(":")) key = "/:" + key;
            _vars[key] = value ?? "";
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) _objVars[key] = d;
            else if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) _objVars[key] = true;
            else if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) _objVars[key] = false;
            else _objVars.Remove(key);
        }

        /// <summary>Funktion "/pfad:name" mit Argumenten (Zahlen, true/false, sonst Text) aufrufen.</summary>
        public bool DebugCall(string key, string args)
        {
            int c = key.LastIndexOf(':');
            if (c < 0) return false;
            string path = key.Substring(0, c), fn = key.Substring(c + 1);
            var list = new List<object>();
            if (!string.IsNullOrWhiteSpace(args))
                foreach (var part in args.Split(','))
                {
                    var t = part.Trim();
                    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) list.Add(d);
                    else if (t == "true" || t == "false") list.Add(t == "true");
                    else list.Add(t.Trim('"', '\''));
                }
            return CallClip(path.Length == 0 ? "/" : path, fn, list.ToArray());
        }

        /// <summary>Angezeigter Text eines Textfelds (Variable oder Starttext).</summary>
        public string DebugText(DisplayObject d)
        {
            if (d == null || !Movie.Chars.TryGetValue(d.CharId, out var ch) || !(ch is VmfMovie.EditText t)) return null;
            string s = null;
            if (!string.IsNullOrEmpty(t.Variable) && !_vars.TryGetValue(ResolveVar(d.Parent, t.Variable), out s)) _vars.TryGetValue(t.Variable, out s);
            return s ?? t.Text;
        }

        /// <summary>Pfade aller sichtbaren Clips, die eine Funktion dieses Namens definieren (z.B. "DialogBox").</summary>
        public List<string> FunctionOwners(string function)
        {
            var res = new List<string>();
            string suffix = ":" + function;
            foreach (var kv in _objVars)
            {
                if (!(kv.Value is AsFunction) || !kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                string path = kv.Key.Substring(0, kv.Key.Length - suffix.Length);
                var inst = path == "/" || path.Length == 0 ? Root : ResolveClip(Root, path);
                if (inst != null && Shown(inst)) res.Add(path.Length == 0 ? "/" : path);
            }
            return res;
        }

        /// <summary>Funktion eines bestimmten Clips aufrufen ("/pause_menu/menu_sq", "Clic", 2).</summary>
        public bool CallClip(string path, string function, params object[] args)
        {
            var inst = string.IsNullOrEmpty(path) || path == "/" ? Root : ResolveClip(Root, path);
            if (inst == null) return false;
            if (!(GetMember(inst, function) is AsFunction fn)) return false;
            var a = new object[args?.Length ?? 0];
            for (int i = 0; i < a.Length; i++) a[i] = args[i] is int iv ? (object)(double)iv : args[i] is float fv ? (object)(double)fv : args[i];
            try { InvokeTop(fn, inst, a); }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Flash-Aufruf " + function + ": " + ex.Message); }
            return true;
        }

        /// <summary>Wert (Zahl/Bool/Text) für Vergleiche im Skript typisiert setzen ("/:IsInOptions", true).</summary>
        public void SetValue(string name, object value)
        {
            if (AutoPin) _pinVars[name] = new PinVar { Value = value, Typed = true };
            if (value is int iv) value = (double)iv; else if (value is float fv) value = (double)fv;
            SetVar(Root, null, name.StartsWith("/:") ? name.Substring(1) : name, value);
        }

        /// <summary>Diagnose: Anzeigebaum (Name, Charakter, Frame/Label, Sichtbarkeit, Alpha) als Text.</summary>
        public string DumpTree(int maxDepth = 6)
        {
            var sb = new StringBuilder();
            void Walk(Instance inst, string ind, int depth)
            {
                foreach (var d in inst.List.Values)
                {
                    string kind = d.Sprite != null ? "clip" : (Movie.Chars.TryGetValue(d.CharId, out var c) ? c.GetType().Name : "?");
                    string frame = "";
                    if (d.Sprite != null)
                    {
                        string lab = null;
                        foreach (var kv in d.Sprite.Timeline.Labels) if (kv.Value <= d.Sprite.Frame && (lab == null || d.Sprite.Timeline.Labels[lab] < kv.Value)) lab = kv.Key;
                        frame = $" f={d.Sprite.Frame}/{d.Sprite.Timeline.FrameCount}{(lab != null ? " [" + lab + "]" : "")}{(d.Sprite.Playing ? " play" : "")}";
                    }
                    sb.Append(ind).Append(d.Depth).Append(' ').Append(d.Name ?? "-").Append(" #").Append(d.CharId).Append(' ').Append(kind).Append(frame)
                      .Append(d.Visible ? "" : " HIDDEN").Append(d.ClipDepth > 0 ? " MASK" : "")
                      .Append(d.Cx.Mul.w < 0.999f ? $" a={d.Cx.Mul.w:0.00}" : "")
                      .Append($" @({d.Matrix.Tx:0},{d.Matrix.Ty:0}) s=({d.Matrix.A:0.00},{d.Matrix.D:0.00})").Append('\n');
                    if (d.Sprite != null && depth < maxDepth) Walk(d.Sprite, ind + "  ", depth + 1);
                }
            }
            if (Root != null)
            {
                sb.Append("root f=").Append(Root.Frame).Append(Root.Playing ? " play" : "").Append('\n');
                Walk(Root, "", 0);
            }
            sb.Append("--- Funktionen:\n");
            foreach (var kv in _objVars) if (kv.Value is AsFunction) sb.Append(kv.Key).Append('\n');
            return sb.ToString();
        }
    }
}
