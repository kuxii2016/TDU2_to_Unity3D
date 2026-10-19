using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TDU2.Map.Flash
{
    /// <summary>
    /// Textsatz der Flash-Textfelder wie im Spiel:
    ///   * Zeilenumbrüche: "\r", "\n", "\r\n" und das wörtliche "\n" aus den DBLoc-Texten; Umbruch an Wortgrenzen bei WordWrap.
    ///   * TDU2-Markup (statt HTML): "#color:RRGGBB#" bzw. "#color:RRGGBBAA#" … "#nocolor#", "#command:NAME#" = Taste der Aktion.
    ///   * HTML-Felder (Flag HTML): &lt;br&gt;, &lt;p&gt;, &lt;font color="#RRGGBB"&gt;, &amp;amp; usw.
    /// Ausrichtung je Zeile (links/rechts/mittig), Ränder/Einzug/Zeilenabstand aus DefineEditText.
    /// </summary>
    public sealed partial class VmfPlayer
    {
        /// <summary>Taste zu "#command:NAME#" (z.B. CAR_ACCEL) – setzt das Spiel; Standard: Name ohne Präfix.</summary>
        public static Func<string, string> CommandKeyName;

        struct TextRun { public string Text; public Color32? Color; }
        sealed class LaidChar { public char C; public float X; public int Line; public Color32? Color; }
        sealed class TextLayout { public readonly List<LaidChar> Chars = new List<LaidChar>(); public readonly List<float> LineWidth = new List<float>(); public float MaxWidth; }

        readonly Dictionary<(VmfMovie.EditText, string, float), TextLayout> _layoutCache = new Dictionary<(VmfMovie.EditText, string, float), TextLayout>();

        /// <summary>Markup in farbige Abschnitte zerlegen (Zeilenumbrüche als '\n').</summary>
        static List<TextRun> ParseMarkup(string s, bool html)
        {
            var runs = new List<TextRun>();
            if (string.IsNullOrEmpty(s)) return runs;
            s = s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n").Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new StringBuilder();
            Color32? color = null;
            var stack = new Stack<Color32?>();
            void Flush() { if (sb.Length > 0) { runs.Add(new TextRun { Text = sb.ToString(), Color = color }); sb.Length = 0; } }
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '#')
                {
                    int end = s.IndexOf('#', i + 1);
                    if (end > i)
                    {
                        string tag = s.Substring(i + 1, end - i - 1);
                        if (tag.StartsWith("color:", StringComparison.OrdinalIgnoreCase) && TryHex(tag.Substring(6), out var col)) { Flush(); color = col; i = end + 1; continue; }
                        if (string.Equals(tag, "nocolor", StringComparison.OrdinalIgnoreCase)) { Flush(); color = null; i = end + 1; continue; }
                        if (tag.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
                        {
                            string name = tag.Substring(8);
                            string key = CommandKeyName?.Invoke(name);
                            sb.Append('[').Append(string.IsNullOrEmpty(key) ? name : key).Append(']');
                            i = end + 1; continue;
                        }
                    }
                }
                else if (html && c == '<')
                {
                    int end = s.IndexOf('>', i + 1);
                    if (end > i)
                    {
                        string tag = s.Substring(i + 1, end - i - 1).Trim();
                        string low = tag.ToLowerInvariant();
                        if (low == "br" || low == "br/" || low == "br /" || low == "/p") sb.Append('\n');
                        else if (low.StartsWith("font"))
                        {
                            Flush(); stack.Push(color);
                            int ci = low.IndexOf("color", StringComparison.Ordinal);
                            if (ci >= 0)
                            {
                                int h = tag.IndexOf('#', ci);
                                if (h >= 0 && h + 7 <= tag.Length && TryHex(tag.Substring(h + 1, 6), out var col)) color = col;
                            }
                        }
                        else if (low == "/font") { Flush(); color = stack.Count > 0 ? stack.Pop() : null; }
                        i = end + 1; continue;
                    }
                }
                else if (html && c == '&')
                {
                    int end = s.IndexOf(';', i + 1);
                    if (end > i && end - i <= 8)
                    {
                        string ent = s.Substring(i + 1, end - i - 1);
                        string rep = ent == "amp" ? "&" : ent == "lt" ? "<" : ent == "gt" ? ">" : ent == "quot" ? "\"" : ent == "apos" ? "'" : ent == "nbsp" ? " " : null;
                        if (rep != null) { sb.Append(rep); i = end + 1; continue; }
                    }
                }
                sb.Append(c);
                i++;
            }
            Flush();
            return runs;
        }

        static bool TryHex(string h, out Color32 c)
        {
            c = default;
            h = h.Trim().TrimStart('#');
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            if ((h.Length != 6 && h.Length != 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            if (h.Length == 6) c = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
            else c = new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            if (h.Length == 8 && c.a == 0) c.a = 255;   // RRGGBB00 kommt als "deckend" vor
            return true;
        }

        /// <summary>Text ohne Markup (für Messungen/Ersatzschrift).</summary>
        static string PlainText(string s, bool html)
        {
            var sb = new StringBuilder();
            foreach (var r in ParseMarkup(s, html)) sb.Append(r.Text);
            return sb.ToString();
        }

        TextLayout Layout(VmfMovie.EditText t, VmfMovie.Font font, string s, float size)
        {
            var key = (t, s, size);
            if (_layoutCache.TryGetValue(key, out var lay)) return lay;
            if (_layoutCache.Count > 2048) _layoutCache.Clear();
            lay = new TextLayout();
            var runs = ParseMarkup(s, t.Html);
            if (t.Password) for (int r = 0; r < runs.Count; r++) runs[r] = new TextRun { Text = new string('*', runs[r].Text.Length), Color = runs[r].Color };
            float avail = t.Bounds.width - 4f - t.LeftMargin - t.RightMargin;
            bool wrap = t.WordWrap && avail > size;

            // Zeichen mit Farbe sammeln
            var chars = new List<(char c, Color32? col)>();
            foreach (var r in runs) foreach (var c in r.Text) chars.Add((c, r.Color));

            int line = 0; float x = t.Indent; int lineStart = lay.Chars.Count;
            int lastSpace = -1;   // Index in lay.Chars des letzten Leerzeichens dieser Zeile
            void EndLine()
            {
                float w = 0f;
                for (int k = lineStart; k < lay.Chars.Count; k++) { var lc = lay.Chars[k]; if (lc.C != ' ') w = Mathf.Max(w, lc.X + Advance(font, lc.C, size)); }
                lay.LineWidth.Add(w);
                lay.MaxWidth = Mathf.Max(lay.MaxWidth, w);
            }
            foreach (var (c, col) in chars)
            {
                if (c == '\n')
                {
                    EndLine(); line++; x = 0f; lineStart = lay.Chars.Count; lastSpace = -1;
                    continue;
                }
                float adv = Advance(font, c, size);
                if (wrap && c != ' ' && x + adv > avail && lay.Chars.Count > lineStart)
                {
                    if (lastSpace >= lineStart)
                    {
                        // ab dem letzten Leerzeichen in die nächste Zeile
                        var moved = lay.Chars.GetRange(lastSpace + 1, lay.Chars.Count - lastSpace - 1);
                        lay.Chars.RemoveRange(lastSpace, lay.Chars.Count - lastSpace);
                        EndLine(); line++; lineStart = lay.Chars.Count; x = 0f;
                        foreach (var m in moved) { m.X = x; m.Line = line; lay.Chars.Add(m); x += Advance(font, m.C, size); }
                    }
                    else { EndLine(); line++; lineStart = lay.Chars.Count; x = 0f; }
                    lastSpace = -1;
                    if (c == ' ') continue;
                }
                if (c == ' ') lastSpace = lay.Chars.Count;
                lay.Chars.Add(new LaidChar { C = c, X = x, Line = line, Color = col });
                x += adv;
            }
            EndLine();
            _layoutCache[key] = lay;
            return lay;
        }

        float LineHeight(VmfMovie.EditText t, VmfMovie.Font font, float size)
        {
            float asc = FontAscent(font);
            return size * (asc + Mathf.Max(200f, 1024f - asc * 0.85f)) / 1024f * 0.92f + t.Leading;
        }

        void DrawTextLaidOut(VmfMovie.EditText t, VmfMovie.Font font, Texture tex, string s, float size, Matrix2D m, Vector4 baseMul, ColorTransform cx)
        {
            var lay = Layout(t, font, s, size);
            float lh = LineHeight(t, font, size);
            float baseline0 = t.Bounds.yMin + 2f + size * FontAscent(font) / 1024f;
            float k = size / 1024f;
            float left = t.Bounds.xMin + 2f + t.LeftMargin, right = t.Bounds.xMax - 2f - t.RightMargin;
            foreach (var lc in lay.Chars)
            {
                if (!font.Glyphs.TryGetValue(lc.C, out var g) || g.H <= 0 || g.V1 <= g.V0) continue;
                float lw = lc.Line < lay.LineWidth.Count ? lay.LineWidth[lc.Line] : 0f;
                float x0 = t.Align == 1 ? right - lw : t.Align == 2 ? (left + right - lw) * 0.5f : left;
                float baseline = baseline0 + lc.Line * lh;
                var r = new Rect(x0 + lc.X - g.XOff * k, baseline - g.Top * k, g.W * k, g.H * k);
                Vector4 mul = baseMul;
                if (lc.Color.HasValue)
                {
                    var c = lc.Color.Value;
                    mul = Vector4.Scale(new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f), cx.Mul);
                }
                DrawGlyph(tex, r, g, m, mul, cx.Add);
            }
        }

        /// <summary>Ersatzschrift (Unity) mit Rich-Text-Farben.</summary>
        static string UnityRichText(string s, bool html)
        {
            var sb = new StringBuilder();
            foreach (var r in ParseMarkup(s, html))
            {
                string txt = r.Text.Replace("<", "<​");
                if (r.Color.HasValue) { var c = r.Color.Value; sb.Append($"<color=#{c.r:X2}{c.g:X2}{c.b:X2}{c.a:X2}>").Append(txt).Append("</color>"); }
                else sb.Append(txt);
            }
            return sb.ToString();
        }
    }
}
