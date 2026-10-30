using System;
using System.Collections.Generic;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>
    /// Straßenschilder mit Text (Docs/tdu2-specs.md §7.7): die Modelle (Ibiza RS_DirL_*/RS_DirR_*/RS_Dist_*, Hawaii RS_DA-RS_DF,
    /// RS_EX) tragen leere Tafeln; das Spiel schrieb die Zeilen zur Laufzeit aus den Zusatzbytes der .pmi-Platzierungen (Art 4).
    ///   Zusatzbytes: +0 Vorlage (= Layout-Index in panel.ppl), danach je Textfeld des Layouts genau "max. Zeichen" Codes.
    ///   Code = Glyphen-Index im Atlas rsalphab.2db (UVMP-Rechteck), 0 = Leerzeichen; Ausrichtung steckt in den Leerzeichen.
    ///   panel.ppl LAYO: je Layout die Textfelder (Rechteck auf dem Schildmodell in Metern, oben-links/unten-rechts, z = Abstand
    ///   vor der Tafel), Breite/Zeilenhöhe in Texeln, max. Zeichen, Flag (0 = weiße, 1 = schwarze Schrift).
    /// Je Sektor ein Text-Mesh (alle Schilder), Material TDU2/ObjectTransparent mit dem Glyphen-Atlas.
    /// </summary>
    public sealed class RoadSignLayout
    {
        public struct Glyph { public int U0, U1, V0, V1; }
        public struct Box { public Vector3 TopLeft, BottomRight; public int Width, Height, MaxChars, Flag; }

        public readonly List<Glyph> Glyphs = new List<Glyph>();
        public readonly List<Box[]> Layouts = new List<Box[]>();
        public readonly List<Color32[]> Colors = new List<Color32[]>();
        public int AtlasW = 256, AtlasH = 256;

        /// <summary>panel.ppl aus commonworld.bnk lesen (thread-sicher). null, wenn nicht vorhanden.</summary>
        public static RoadSignLayout Load(string commonWorldBank)
        {
            var bank = BnkArchive.Open(commonWorldBank);
            var e = bank?.Find("panel.ppl");
            if (e == null) return null;
            var l = Parse(bank.Read(e));
            var t = bank.Find("rsalphab.2db");
            if (l != null && t != null)
            {
                var hd = bank.ReadPartial(t, 0, 0x30);
                if (hd != null && hd.Length >= 0x2C) { l.AtlasW = Math.Max(1, (int)Bin.U16(hd, 0x28)); l.AtlasH = Math.Max(1, (int)Bin.U16(hd, 0x2A)); }
            }
            return l;
        }

        static int FindTag(byte[] d, string tag)
        {
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == tag[0] && d[i + 1] == tag[1] && d[i + 2] == tag[2] && d[i + 3] == tag[3]) return i;
            return -1;
        }

        public static RoadSignLayout Parse(byte[] d)
        {
            if (d == null) return null;
            var l = new RoadSignLayout();
            int u = FindTag(d, "UVMP"), lo = FindTag(d, "LAYO");
            if (u < 0 || lo < 0) return null;
            // Segment: Tag, u32, u32 Größe, u32 Größe, dann Nutzdaten
            int p = u + 16;
            int n = (int)Bin.U32(d, p); p += 16;
            for (int i = 0; i < n && p + 16 <= d.Length; i++, p += 16)
                l.Glyphs.Add(new Glyph { U0 = Bin.U16(d, p), U1 = Bin.U16(d, p + 2), V0 = Bin.U16(d, p + 4), V1 = Bin.U16(d, p + 6) });
            p = lo + 16;
            int m = (int)Bin.U32(d, p); p += 16;
            for (int i = 0; i < m && p + 16 <= d.Length; i++)
            {
                int bc = (int)Bin.U32(d, p);
                var cols = new Color32[3];
                for (int c = 0; c < 3; c++) cols[c] = new Color32(d[p + 4 + 4 * c], d[p + 5 + 4 * c], d[p + 6 + 4 * c], 255);
                p += 16;
                var boxes = new Box[Math.Max(0, bc)];
                for (int b = 0; b < bc && p + 32 <= d.Length; b++, p += 32)
                    boxes[b] = new Box
                    {
                        TopLeft = new Vector3(Bin.F32(d, p), Bin.F32(d, p + 4), Bin.F32(d, p + 8)),
                        BottomRight = new Vector3(Bin.F32(d, p + 12), Bin.F32(d, p + 16), Bin.F32(d, p + 20)),
                        Width = Bin.U16(d, p + 24), Height = Bin.U16(d, p + 26), MaxChars = d[p + 28], Flag = d[p + 29],
                    };
                l.Layouts.Add(boxes);
                l.Colors.Add(cols);
            }
            return l;
        }

        /// <summary>Glyphen eines Schilds als Quads anhängen (Schildmodell-Raum -> toLocal).</summary>
        public int AddSign(byte[] codes, Matrix4x4 toLocal, List<Vector3> v, List<Vector2> uv, List<Color32> col, List<Vector3> nrm, List<int> tri)
        {
            if (codes == null || codes.Length < 2) return 0;
            int tpl = codes[0];
            if (tpl <= 0 || tpl >= Layouts.Count) return 0;
            var boxes = Layouts[tpl];
            var cols = Colors[tpl];
            var n = toLocal.MultiplyVector(Vector3.forward).normalized;
            int pos = 1, quads = 0;
            const float avgGlyph = 12.5f;   // mittlere Glyphenbreite in Zeilen-Texeln (für die Größe kurzer Felder)
            foreach (var b in boxes)
            {
                int cnt = b.MaxChars;
                if (pos + cnt > codes.Length) break;
                float bw = Mathf.Abs(b.BottomRight.x - b.TopLeft.x), bh = Mathf.Abs(b.TopLeft.y - b.BottomRight.y);
                if (bw < 1e-3f || bh < 1e-3f || b.Height <= 0) { pos += cnt; continue; }
                // Meter je Zeilen-Texel: Zeilenhöhe füllt das Feld, sonst begrenzt durch die Feldbreite
                float s = Mathf.Min(bh / b.Height, bw / Mathf.Max(1f, cnt * avgGlyph));
                float glyphH = b.Height * s;
                float x = Mathf.Min(b.TopLeft.x, b.BottomRight.x);
                float yTop = (b.TopLeft.y + b.BottomRight.y) * 0.5f + glyphH * 0.5f;
                float z = Mathf.Max(b.TopLeft.z, b.BottomRight.z) + 0.004f;
                var c = cols[Mathf.Clamp(b.Flag, 0, 2)];
                for (int k = 0; k < cnt; k++)
                {
                    int code = codes[pos + k];
                    if (code < 0 || code >= Glyphs.Count) continue;
                    var g = Glyphs[code];
                    int gh = Math.Max(1, g.V1 - g.V0);
                    float f = b.Height / (float)gh;               // Glyphe auf Zeilenhöhe
                    float w = (g.U1 - g.U0) * f * s;
                    if (code != 0 && w > 0f)
                    {
                        int bi = v.Count;
                        v.Add(toLocal.MultiplyPoint3x4(new Vector3(x, yTop, z)));
                        v.Add(toLocal.MultiplyPoint3x4(new Vector3(x + w, yTop, z)));
                        v.Add(toLocal.MultiplyPoint3x4(new Vector3(x + w, yTop - glyphH, z)));
                        v.Add(toLocal.MultiplyPoint3x4(new Vector3(x, yTop - glyphH, z)));
                        float u0 = g.U0 / (float)AtlasW, u1 = g.U1 / (float)AtlasW;
                        // Texturen werden wie im Spiel ungespiegelt hochgeladen (D3D: v nach unten) -> v direkt in Texeln/Höhe
                        float v0 = g.V0 / (float)AtlasH, v1 = g.V1 / (float)AtlasH;
                        uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u1, v0)); uv.Add(new Vector2(u1, v1)); uv.Add(new Vector2(u0, v1));
                        for (int q = 0; q < 4; q++) { col.Add(c); nrm.Add(n); }
                        tri.Add(bi); tri.Add(bi + 1); tri.Add(bi + 2); tri.Add(bi); tri.Add(bi + 2); tri.Add(bi + 3);
                        quads++;
                    }
                    x += w;
                }
                pos += cnt;
            }
            return quads;
        }
    }
}
