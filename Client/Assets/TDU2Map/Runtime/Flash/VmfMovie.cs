using System;
using System.Collections.Generic;
using TDU2.IO;
using UnityEngine;

namespace TDU2.Flash
{
    /// <summary>
    /// Inhalt eines VMF-Films (siehe Docs/TDU2_VMF_Flash_Format.md): Charaktere (Shapes, Sprites, Bitmaps, Textfelder)
    /// und die Hauptzeitleiste. Thread-sicher ladbar (keine Unity-Objekte).
    /// Koordinaten in Pixeln (Twips / 20), Bühne 1280 x 960.
    /// </summary>
    public sealed class VmfMovie
    {
        public const float StageWidth = 1280f, StageHeight = 960f;

        public abstract class Character { public int Id; }

        public sealed class FillStyle
        {
            public int Type;                 // 0 solid, 0x10 linear, 0x12 radial, 0x13 focal, 0x40..0x43 bitmap
            public Color32 Color;
            public Matrix2D Matrix;
            public byte[] Ratios; public Color32[] Colors;
            public int BitmapId;
        }
        public sealed class LineStyle { public float Width; public Color32 Color; }
        public sealed class Path { public int Fill0, Fill1, Line; public List<Vector2> Points = new List<Vector2>(); }

        public sealed class Shape : Character
        {
            public Rect Bounds;
            /// <summary>DefineMorphShape: Endform-Begrenzung (Bounds = Startform); Zwischenstand über DisplayObject.Ratio (0..65535).</summary>
            public bool Morph; public Rect EndBounds;
            public List<FillStyle> Fills = new List<FillStyle>();
            public List<LineStyle> Lines = new List<LineStyle>();
            public List<Path> Paths = new List<Path>();
        }

        public sealed class Bitmap : Character { public int Width, Height; public float U0, V0, U1, V1; public int Page = -1; public string TextureName; }

        /// <summary>
        /// Tag 125 (Eden-Font): u16 id, u16 texId (0 -> fla_&lt;id&gt;), u8 flags, Name\0, u16 n, u16 nTex,
        /// nTex Glyphen à 26 Byte {u16 code, f32 u0, u1, v0, v1, s16 w, h, xoff, top (1/1024 em)}, danach weitere Tabellen.
        /// Glyphen-Textur: weiß auf schwarz (Luminanz = Deckkraft).
        /// </summary>
        public sealed class Font : Character
        {
            public string Name, TextureName;
            public int Ascent = 1024, Descent, Leading;
            public readonly Dictionary<char, Glyph> Glyphs = new Dictionary<char, Glyph>();
            public readonly Dictionary<char, short> Advances = new Dictionary<char, short>();
        }
        public struct Glyph { public float U0, U1, V0, V1; public short W, H, XOff, Top; }

        public sealed class EditText : Character
        {
            public Rect Bounds; public string Variable, Text; public float Height; public Color32 Color = new Color32(255, 255, 255, 255);
            public int Align; public int FontId;
            public bool Multiline, WordWrap, Html, Password;
            /// <summary>Anfangstext begann mit "_" (Entwickler-/Schlüsseltext, im Spiel immer ersetzt).</summary>
            public bool KeyText;
            public float LeftMargin, RightMargin, Indent, Leading;
        }

        /// <summary>Zeitleiste (Hauptfilm oder Sprite): je Frame die Steuer-Tags (PlaceObject2, RemoveObject2, DoAction).</summary>
        public sealed class Timeline : Character
        {
            public readonly List<List<FrameOp>> Frames = new List<List<FrameOp>>();
            public readonly Dictionary<string, int> Labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int FrameCount => Frames.Count;
        }

        public enum OpKind { Place, Remove, Action }
        public sealed class FrameOp
        {
            public OpKind Kind; public int Depth;
            // Place
            public bool Move, HasChar, HasMatrix, HasCx, HasRatio, HasName, HasClip;
            public int CharId, Ratio, ClipDepth; public Matrix2D Matrix; public ColorTransform Cx; public string Name;
            // Action
            public byte[] Actions;
        }

        /// <summary>DefineButton2 (Tag 34): Zustands-Datensätze (Up/Over/Down/Hit) und Bedingungsaktionen (rollOver, release ...).</summary>
        public sealed class Button : Character
        {
            public readonly List<ButtonRecord> Records = new List<ButtonRecord>();
            public readonly List<ButtonAction> Actions = new List<ButtonAction>();
        }
        public sealed class ButtonRecord { public int States, CharId, Depth; public Matrix2D Matrix = Matrix2D.Identity; public ColorTransform Cx = ColorTransform.Identity; }
        /// <summary>Bedingungen (u16): 0x01 IdleToOverUp (rollOver), 0x02 OverUpToIdle (rollOut), 0x04 OverUpToOverDown (press),
        /// 0x08 OverDownToOverUp (release), 0x10 OverDownToOutDown (dragOut), 0x20 OutDownToOverDown (dragOver), 0x40 OutDownToIdle (releaseOutside), 0x80 IdleToOverDown.</summary>
        public sealed class ButtonAction { public int Conditions; public byte[] Code; }

        public readonly Dictionary<int, Character> Chars = new Dictionary<int, Character>();
        public Timeline Root = new Timeline { Id = -1 };
        public float FrameRate = 30f;
        public readonly Dictionary<int, int> PageOf = new Dictionary<int, int>();
        /// <summary>ExportAssets (Tag 56): Verknüpfungsname -> Charakter-ID (für attachMovie).</summary>
        public readonly Dictionary<string, int> Exports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- Laden
        public static VmfMovie Parse(byte[] d, Func<string, bool> textureExists = null)
        {
            if (d == null || d.Length < 19 || d[0] != 'F' || d[1] != 'M' || d[2] != 'V') throw new FormatException("no falid VMF File");
            var m = new VmfMovie { FrameRate = Mathf.Max(1, (d[14] << 8) | d[15]) };
            int o = 19;
            if (d[18] != 0)
            {
                int n = Bin.U16(d, 19); o = 21;
                for (int i = 0; i < n; i++, o += 4) m.PageOf[Bin.U16(d, o + 2)] = Bin.U16(d, o);
                o += 2;
            }
            m.ReadTimeline(d, o, d.Length, m.Root);
            foreach (var c in m.Chars.Values)
                if (c is Bitmap b)
                {
                    string own = $"fla_{b.Id:0000}";
                    if (textureExists != null && textureExists(own)) b.TextureName = own;
                    else if (m.PageOf.TryGetValue(b.Id, out var pg)) { b.Page = pg; b.TextureName = $"page_{pg + 1:000}"; }
                    else b.TextureName = own;
                }
            return m;
        }

        void ReadTimeline(byte[] d, int o, int end, Timeline tl)
        {
            var cur = new List<FrameOp>();
            while (o + 2 <= end)
            {
                int h = Bin.U16(d, o), code = h >> 6, len = h & 0x3F, p = o + 2;
                if (len == 0x3F) { len = (int)Bin.U32(d, p); p += 4; }
                if (p + len > end) break;
                o = p + len;
                try
                {
                    switch (code)
                    {
                        case 0: goto done;
                        case 1: tl.Frames.Add(cur); cur = new List<FrameOp>(); break;
                        case 43: { int q = p; tl.Labels[CString(d, ref q, p + len)] = tl.Frames.Count; break; }
                        case 26: cur.Add(ReadPlace2(d, p, len)); break;
                        case 28: cur.Add(new FrameOp { Kind = OpKind.Remove, Depth = Bin.U16(d, p) }); break;
                        case 5: cur.Add(new FrameOp { Kind = OpKind.Remove, Depth = Bin.U16(d, p + 2) }); break;
                        case 12: { var a = new byte[len]; Buffer.BlockCopy(d, p, a, 0, len); cur.Add(new FrameOp { Kind = OpKind.Action, Actions = a }); break; }
                        case 2: case 22: case 32: ReadShape(d, p, len, code == 2 ? 1 : code == 22 ? 2 : 3); break;
                        case 46: ReadMorph(d, p, len); break;
                        case 126:
                            {
                                var b = new Bitmap
                                {
                                    Id = Bin.U16(d, p),
                                    Width = Bin.U16(d, p + 2),
                                    Height = Bin.U16(d, p + 4),
                                    U0 = Bin.F32(d, p + 6),
                                    V0 = Bin.F32(d, p + 10),
                                    U1 = Bin.F32(d, p + 14),
                                    V1 = Bin.F32(d, p + 18)
                                };
                                Chars[b.Id] = b; break;
                            }
                        case 127:
                            {
                                var sp = new Timeline { Id = Bin.U16(d, p) };
                                int inner = (int)Bin.U32(d, p + 4);
                                ReadTimeline(d, p + 8, Math.Min(p + len, p + 8 + inner), sp);
                                Chars[sp.Id] = sp; break;
                            }
                        case 37: ReadEditText(d, p, len); break;
                        case 125: ReadFont(d, p, len); break;
                        case 34: ReadButton2(d, p, len); break;
                        case 56:
                            {
                                int n = Bin.U16(d, p), q = p + 2;
                                for (int i = 0; i < n && q + 2 < p + len; i++)
                                {
                                    int cid = Bin.U16(d, q); q += 2;
                                    string name = CString(d, ref q, p + len);
                                    if (!string.IsNullOrEmpty(name) && !Exports.ContainsKey(name)) Exports[name] = cid;
                                }
                                break;
                            }
                    }
                }
                catch (Exception) { /* einzelne Tags überspringen */ }
            }
        done:
            if (cur.Count > 0 || tl.Frames.Count == 0) tl.Frames.Add(cur);
        }

        void ReadButton2(byte[] d, int p, int len)
        {
            int end = p + len;
            var b = new Button { Id = Bin.U16(d, p) };
            int q = p + 3;                       // id, Flags (TrackAsMenu)
            int actOff = Bin.U16(d, q);
            int actStart = actOff != 0 ? q + actOff : end;
            q += 2;
            while (q < end && q < actStart && d[q] != 0)
            {
                var br = new BitReader(d, q);
                int fl = br.U8();
                var r = new ButtonRecord { States = fl & 0x0F, CharId = br.U16(), Depth = br.U16() };
                r.Matrix = br.Matrix();
                r.Cx = br.CxForm(true);
                if ((fl & 0x10) != 0) break;     // Filterliste (Flash 8) – nicht unterstützt
                if ((fl & 0x20) != 0) br.U8();   // Blendmodus
                q = br.Pos;
                b.Records.Add(r);
            }
            q = actStart;
            while (actOff != 0 && q + 4 <= end)
            {
                int size = Bin.U16(d, q), cond = Bin.U16(d, q + 2);
                int s0 = q + 4, e = size == 0 ? end : Math.Min(end, q + size);
                if (e > s0)
                {
                    var code = new byte[e - s0];
                    Buffer.BlockCopy(d, s0, code, 0, code.Length);
                    b.Actions.Add(new ButtonAction { Conditions = cond, Code = code });
                }
                if (size == 0) break;
                q += size;
            }
            Chars[b.Id] = b;
        }

        static FrameOp ReadPlace2(byte[] d, int p, int len)
        {
            var br = new BitReader(d, p);
            int f = br.U8();
            var op = new FrameOp { Kind = OpKind.Place, Depth = br.U16(), Move = (f & 1) != 0 };
            if ((f & 2) != 0) { op.HasChar = true; op.CharId = br.U16(); }
            if ((f & 4) != 0) { op.HasMatrix = true; op.Matrix = br.Matrix(); }
            if ((f & 8) != 0) { op.HasCx = true; op.Cx = br.CxForm(true); }
            if ((f & 16) != 0) { op.HasRatio = true; op.Ratio = br.U16(); }
            if ((f & 32) != 0) { op.HasName = true; int q = br.Pos; op.Name = CString(d, ref q, p + len); br.Pos = q; }
            if ((f & 64) != 0) { op.HasClip = true; op.ClipDepth = br.U16(); }
            return op;
        }

        // ---------------------------------------------------------------- Shapes
        static List<FillStyle> ReadFills(BitReader br, int ver, bool morph)
        {
            int n = br.U8(); if (n == 0xFF && ver >= 2) n = br.U16();
            var list = new List<FillStyle>(n);
            for (int i = 0; i < n; i++)
            {
                var fs = new FillStyle { Type = br.U8() };
                if (fs.Type == 0) { fs.Color = br.Rgb(ver >= 3 || morph); if (morph) br.Rgb(true); }
                else if (fs.Type == 0x10 || fs.Type == 0x12 || fs.Type == 0x13)
                {
                    fs.Matrix = br.Matrix(); if (morph) br.Matrix();
                    int g = br.U8(), cnt = g & 15;
                    fs.Ratios = new byte[cnt]; fs.Colors = new Color32[cnt];
                    for (int k = 0; k < cnt; k++) { fs.Ratios[k] = (byte)br.U8(); fs.Colors[k] = br.Rgb(ver >= 3 || morph); if (morph) { br.U8(); br.Rgb(true); } }
                    if (fs.Type == 0x13) br.U16();
                }
                else if (fs.Type >= 0x40 && fs.Type <= 0x43) { fs.BitmapId = br.U16(); fs.Matrix = br.Matrix(); if (morph) br.Matrix(); }
                else throw new FormatException("Füllstil " + fs.Type);
                list.Add(fs);
            }
            return list;
        }

        static List<LineStyle> ReadLines(BitReader br, int ver, bool morph)
        {
            int n = br.U8(); if (n == 0xFF) n = br.U16();
            var list = new List<LineStyle>(n);
            for (int i = 0; i < n; i++)
            {
                var ls = new LineStyle { Width = br.U16() / 20f };
                if (morph) br.U16();
                ls.Color = br.Rgb(ver >= 3 || morph);
                if (morph) br.Rgb(true);
                list.Add(ls);
            }
            return list;
        }

        /// <summary>
        /// VMF-Shape: Kopf/Stile wie SWF, die Geometrie aber als einfache Pfadliste:
        /// u16 n; je Pfad: u8 Linienstil, u16 fill0, u16 fill1, i32 x, i32 y (Twips), u16 Kanten,
        /// je Kante i32 cx, cy, ax, ay (Quadratic Bezier, gerade wenn c == a), u8 Flag.
        /// </summary>
        void ReadShape(byte[] d, int p, int len, int ver)
        {
            var br = new BitReader(d, p);
            var s = new Shape { Id = br.U16(), Bounds = br.Rect() };
            s.Fills = ReadFills(br, ver, false);
            s.Lines = ReadLines(br, ver, false);
            int q = br.Pos, end = p + len;
            int n = Bin.U16(d, q); q += 2;
            for (int i = 0; i < n && q + 15 <= end; i++)
            {
                var path = new Path { Line = d[q], Fill0 = Bin.U16(d, q + 1), Fill1 = Bin.U16(d, q + 3) };
                int x = (int)Bin.U32(d, q + 5), y = (int)Bin.U32(d, q + 9), ne = Bin.U16(d, q + 13);
                q += 15;
                path.Points.Add(new Vector2(x / 20f, y / 20f));
                for (int k = 0; k < ne && q + 16 <= end; k++, q += 16)
                {
                    int cx = (int)Bin.U32(d, q), cy = (int)Bin.U32(d, q + 4), ax = (int)Bin.U32(d, q + 8), ay = (int)Bin.U32(d, q + 12);
                    AddEdge(path.Points, x, y, cx, cy, ax, ay);
                    x = ax; y = ay;
                }
                q++;   // Flag (0/1)
                s.Paths.Add(path);
            }
            Chars[s.Id] = s;
        }

        static void AddEdge(List<Vector2> pts, int x, int y, int cx, int cy, int ax, int ay)
        {
            if (cx == ax && cy == ay) { pts.Add(new Vector2(ax / 20f, ay / 20f)); return; }
            int steps = Mathf.Clamp((Mathf.Abs(ax - x) + Mathf.Abs(ay - y)) / 40, 2, 16);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps, u = 1f - t;
                pts.Add(new Vector2((u * u * x + 2 * u * t * cx + t * t * ax) / 20f, (u * u * y + 2 * u * t * cy + t * t * ay) / 20f));
            }
        }

        /// <summary>DefineMorphShape: Stile wie SWF, Kanten als normale SWF-Shape-Records (Startform wird verwendet).</summary>
        void ReadMorph(byte[] d, int p, int len)
        {
            var br = new BitReader(d, p);
            var s = new Shape { Id = br.U16(), Bounds = br.Rect(), Morph = true };
            s.EndBounds = br.Rect(); br.Pos += 4;
            s.Fills = ReadFills(br, 3, true);
            s.Lines = ReadLines(br, 3, true);
            int nf = br.UB(4), nl = br.UB(4);
            float x = 0, y = 0; int f0 = 0, f1 = 0, ln = 0;
            Path cur = null;
            void Start() { cur = new Path { Fill0 = f0, Fill1 = f1, Line = ln }; cur.Points.Add(new Vector2(x, y)); s.Paths.Add(cur); }
            Start();
            int guard = 0;
            while (br.Pos < p + len && guard++ < 100000)
            {
                if (br.UB(1) == 0)
                {
                    int flags = br.UB(5);
                    if (flags == 0) break;
                    if ((flags & 1) != 0) { int nb = br.UB(5); x = br.SB(nb) / 20f; y = br.SB(nb) / 20f; }
                    if ((flags & 2) != 0) f0 = br.UB(nf);
                    if ((flags & 4) != 0) f1 = br.UB(nf);
                    if ((flags & 8) != 0) ln = br.UB(nl);
                    Start();
                }
                else if (br.UB(1) == 1)
                {
                    int nb = br.UB(4) + 2; float dx = 0, dy = 0;
                    if (br.UB(1) == 1) { dx = br.SB(nb) / 20f; dy = br.SB(nb) / 20f; }
                    else if (br.UB(1) == 1) dy = br.SB(nb) / 20f; else dx = br.SB(nb) / 20f;
                    x += dx; y += dy; cur.Points.Add(new Vector2(x, y));
                }
                else
                {
                    int nb = br.UB(4) + 2;
                    float cx = x + br.SB(nb) / 20f, cy = y + br.SB(nb) / 20f, ax = cx + br.SB(nb) / 20f, ay = cy + br.SB(nb) / 20f;
                    for (int i = 1; i <= 8; i++)
                    {
                        float t = i / 8f, u = 1f - t;
                        cur.Points.Add(new Vector2(u * u * x + 2 * u * t * cx + t * t * ax, u * u * y + 2 * u * t * cy + t * t * ay));
                    }
                    x = ax; y = ay;
                }
            }
            Chars[s.Id] = s;
        }

        void ReadFont(byte[] d, int p, int len)
        {
            int end = p + len;
            var f = new Font { Id = Bin.U16(d, p) };
            int tex = Bin.U16(d, p + 2);
            int q = p + 5;
            f.Name = CString(d, ref q, end);
            f.TextureName = $"fla_{(tex != 0 ? tex : f.Id):0000}";
            if (q + 4 > end) { Fonts[f.Id] = f; return; }
            int n = Bin.U16(d, q), nTex = Bin.U16(d, q + 2); q += 4;
            for (int i = 0; i < nTex && q + 26 <= end; i++, q += 26)
            {
                char c = (char)Bin.U16(d, q);
                f.Glyphs[c] = new Glyph
                {
                    U0 = Bin.F32(d, q + 2),
                    U1 = Bin.F32(d, q + 6),
                    V0 = Bin.F32(d, q + 10),
                    V1 = Bin.F32(d, q + 14),
                    W = (short)Bin.U16(d, q + 18),
                    H = (short)Bin.U16(d, q + 20),
                    XOff = (short)Bin.U16(d, q + 22),
                    Top = (short)Bin.U16(d, q + 24),
                };
            }
            // Fonts mit eigener Zusatztextur (texId != 0, weichgezeichnete Variante) haben eine zweite Glyphentabelle gleicher Größe
            if (tex != 0) q += 26 * nTex;
            // Danach wie SWF DefineFont2 (1/1024 em): n x u16 (Offsets, 0), n x u16 Zeichencodes,
            // u16 Ascent, s16 Descent, s16 Leading, n x s16 Vorschub (Advance), Bounds, Kerning
            int codes = q + 2 * n, adv = codes + 2 * n + 6;
            if (adv + 2 * n <= end)
            {
                f.Ascent = Bin.U16(d, codes + 2 * n); f.Descent = (short)Bin.U16(d, codes + 2 * n + 2); f.Leading = (short)Bin.U16(d, codes + 2 * n + 4);
                for (int i = 0; i < n; i++) f.Advances[(char)Bin.U16(d, codes + 2 * i)] = (short)Bin.U16(d, adv + 2 * i);
            }
            // Fonts belegen eigene IDs; EditText referenziert sie über FontId
            Fonts[f.Id] = f;
        }

        public readonly Dictionary<int, Font> Fonts = new Dictionary<int, Font>();

        static bool LooksLikeVarRef(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
            foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '/')) return false;
            if (s.StartsWith(":") || s.StartsWith("/:") || s.StartsWith("./") || s.StartsWith("../")) return s.IndexOf(':') >= 0 && s.Length > 2;
            return s.StartsWith("_root.") || s.StartsWith("root.") || s.StartsWith("_parent.") || s.StartsWith("parent.");
        }

        void ReadEditText(byte[] d, int p, int len)
        {
            var br = new BitReader(d, p);
            var e = new EditText { Id = br.U16(), Bounds = br.Rect() };
            int f1 = br.U8(), f2 = br.U8();
            if ((f1 & 0x01) != 0) { e.FontId = br.U16(); e.Height = br.U16() / 20f; }
            if ((f1 & 0x04) != 0) { e.Color = new Color32(d[br.Pos], d[br.Pos + 1], d[br.Pos + 2], d[br.Pos + 3]); br.Pos += 4; }
            if ((f1 & 0x02) != 0) br.Pos += 2;
            e.WordWrap = (f1 & 0x40) != 0; e.Multiline = (f1 & 0x20) != 0; e.Password = (f1 & 0x10) != 0; e.Html = (f2 & 0x02) != 0;
            if ((f2 & 0x20) != 0)
            {
                e.Align = d[br.Pos];
                e.LeftMargin = BitConverter.ToUInt16(d, br.Pos + 1) / 20f;
                e.RightMargin = BitConverter.ToUInt16(d, br.Pos + 3) / 20f;
                e.Indent = BitConverter.ToUInt16(d, br.Pos + 5) / 20f;
                e.Leading = BitConverter.ToInt16(d, br.Pos + 7) / 20f;
                br.Pos += 9;
            }
            int q = br.Pos;
            e.Variable = CString(d, ref q, p + len);
            // Eden: nach dem Variablennamen u32 (Flags/Hash), danach der Anfangstext (UTF-8, "_xyz" = Lokalisierungsschlüssel)
            if ((f1 & 0x80) != 0 && q + 4 < p + len)
            {
                q += 4;
                int ts = q; while (q < p + len && d[q] != 0) q++;
                var t = System.Text.Encoding.UTF8.GetString(d, ts, q - ts);
                string raw = t;
                if (t.StartsWith("_")) { t = t.Substring(1); e.KeyText = true; }
                bool printable = t.Length > 0;
                foreach (var ch in t) if (ch < 0x20 && ch != '\n' && ch != '\r') { printable = false; break; }
                e.Text = printable ? t : "";
                // Eden: Feld ohne Variable, dessen Text ein Variablenverweis ist (":PlayerName", "_root.m_MapDescGamblin_",
                // "./../:txt_help", "parent.memberGrade") – wie eine Variable behandeln
                if (string.IsNullOrEmpty(e.Variable) && printable && LooksLikeVarRef(raw))
                {
                    string v = raw;
                    if (v.StartsWith("root.")) v = "_" + v;
                    else if (v.StartsWith("parent.")) v = "_" + v;
                    e.Variable = v; e.Text = ""; e.KeyText = false;
                }
            }
            Chars[e.Id] = e;
        }

        static string CString(byte[] d, ref int q, int end)
        {
            int s = q; while (q < end && d[q] != 0) q++;
            var c = new char[q - s]; for (int i = 0; i < c.Length; i++) c[i] = (char)d[s + i];
            if (q < end) q++;
            return new string(c);
        }
    }

    /// <summary>2D-Affinmatrix wie SWF: x' = A x + C y + Tx, y' = B x + D y + Ty.</summary>
    public struct Matrix2D
    {
        public float A, B, C, D, Tx, Ty;
        public static readonly Matrix2D Identity = new Matrix2D { A = 1, D = 1 };
        public static Matrix2D operator *(Matrix2D m, Matrix2D n) => new Matrix2D
        {
            A = m.A * n.A + m.C * n.B,
            B = m.B * n.A + m.D * n.B,
            C = m.A * n.C + m.C * n.D,
            D = m.B * n.C + m.D * n.D,
            Tx = m.A * n.Tx + m.C * n.Ty + m.Tx,
            Ty = m.B * n.Tx + m.D * n.Ty + m.Ty,
        };
        public Vector2 Apply(Vector2 p) => new Vector2(A * p.x + C * p.y + Tx, B * p.x + D * p.y + Ty);
        public float Det => A * D - B * C;
        public Matrix2D Inverse()
        {
            float det = Det; if (Mathf.Abs(det) < 1e-12f) return Identity;
            float i = 1f / det;
            return new Matrix2D { A = D * i, B = -B * i, C = -C * i, D = A * i, Tx = (C * Ty - D * Tx) * i, Ty = (B * Tx - A * Ty) * i };
        }
        public static Matrix2D Rotation(float deg)
        {
            float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Matrix2D { A = c, B = s, C = -s, D = c };
        }
        public float ScaleEstimate => Mathf.Sqrt(Mathf.Abs(Det));
    }

    public struct ColorTransform
    {
        public Vector4 Mul, Add;   // Add in 0..1 (bereits /255)
        public static readonly ColorTransform Identity = new ColorTransform { Mul = Vector4.one, Add = Vector4.zero };
        public static ColorTransform operator *(ColorTransform parent, ColorTransform child) => new ColorTransform
        {
            Mul = Vector4.Scale(parent.Mul, child.Mul),
            Add = Vector4.Scale(parent.Mul, child.Add) + parent.Add,
        };
    }

    /// <summary>Bit-Leser für SWF-Strukturen (RECT, MATRIX, CXFORM).</summary>
    public sealed class BitReader
    {
        readonly byte[] _d; int _p, _b;
        public BitReader(byte[] d, int p) { _d = d; _p = p; }
        public int Pos { get { Align(); return _p; } set { _p = value; _b = 0; } }
        public void Align() { if (_b != 0) { _b = 0; _p++; } }
        public int UB(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                v = (v << 1) | ((_d[_p] >> (7 - _b)) & 1);
                if (++_b == 8) { _b = 0; _p++; }
            }
            return v;
        }
        public int SB(int n) { if (n == 0) return 0; int v = UB(n); return (v & (1 << (n - 1))) != 0 ? v - (1 << n) : v; }
        public int U8() { Align(); return _d[_p++]; }
        public int U16() { Align(); int v = _d[_p] | (_d[_p + 1] << 8); _p += 2; return v; }
        public Rect Rect()
        {
            int n = UB(5); float x0 = SB(n) / 20f, x1 = SB(n) / 20f, y0 = SB(n) / 20f, y1 = SB(n) / 20f; Align();
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }
        public Matrix2D Matrix()
        {
            var m = Matrix2D.Identity;
            if (UB(1) == 1) { int n = UB(5); m.A = SB(n) / 65536f; m.D = SB(n) / 65536f; }
            if (UB(1) == 1) { int n = UB(5); m.B = SB(n) / 65536f; m.C = SB(n) / 65536f; }
            int t = UB(5); m.Tx = SB(t) / 20f; m.Ty = SB(t) / 20f;
            Align();
            return m;
        }
        public ColorTransform CxForm(bool alpha)
        {
            var c = ColorTransform.Identity;
            int ha = UB(1), hm = UB(1), n = UB(4);
            if (hm == 1) { c.Mul = new Vector4(SB(n) / 256f, SB(n) / 256f, SB(n) / 256f, alpha ? SB(n) / 256f : 1f); }
            if (ha == 1) { c.Add = new Vector4(SB(n) / 255f, SB(n) / 255f, SB(n) / 255f, alpha ? SB(n) / 255f : 0f); }
            Align();
            return c;
        }
        public Color32 Rgb(bool alpha)
        {
            Align();
            var c = new Color32(_d[_p], _d[_p + 1], _d[_p + 2], alpha ? _d[_p + 3] : (byte)255);
            _p += alpha ? 4 : 3;
            return c;
        }
    }
}