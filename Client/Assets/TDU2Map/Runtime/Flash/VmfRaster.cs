using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Flash
{
    /// <summary>CPU-Pixel einer .2db-Textur (DXT1/DXT5 dekodiert), für Bitmapfüllungen beim Rastern.</summary>
    public sealed class PixelImage
    {
        public int Width, Height;
        public Color32[] Pixels;   // Zeile 0 = oben

        public Color32 Sample(float x, float y)
        {
            int ix = Mathf.Clamp((int)x, 0, Width - 1), iy = Mathf.Clamp((int)y, 0, Height - 1);
            return Pixels[iy * Width + ix];
        }

        public static PixelImage FromTextureDef(TextureDef t)
        {
            if (t == null || t.Data == null) return null;
            int bf = t.BaseFormat, w = t.Width, h = t.Height;
            if ((bf != 0x84 && bf != 0x88) || w < 4 || h < 4) return null;
            var img = new PixelImage { Width = w, Height = h, Pixels = new Color32[w * h] };
            int bw = w / 4, bh = h / 4, bb = bf == 0x84 ? 8 : 16, o = TextureDef.DataOffset;
            var d = t.Data;
            if (o + bw * bh * bb > d.Length) return null;
            var pal = new Color32[4]; var alpha = new byte[8];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int b = o + (by * bw + bx) * bb, c = bf == 0x88 ? b + 8 : b;
                    ushort c0 = Bin.U16(d, c), c1 = Bin.U16(d, c + 2);
                    pal[0] = C565(c0); pal[1] = C565(c1);
                    if (c0 > c1 || bf == 0x88) { pal[2] = Mix(pal[0], pal[1], 2, 1); pal[3] = Mix(pal[0], pal[1], 1, 2); }
                    else { pal[2] = Mix(pal[0], pal[1], 1, 1); pal[3] = new Color32(0, 0, 0, 0); }
                    uint bits = Bin.U32(d, c + 4);
                    ulong abits = 0;
                    if (bf == 0x88)
                    {
                        alpha[0] = d[b]; alpha[1] = d[b + 1];
                        if (alpha[0] > alpha[1]) for (int i = 2; i < 8; i++) alpha[i] = (byte)(((8 - i) * alpha[0] + (i - 1) * alpha[1]) / 7);
                        else { for (int i = 2; i < 6; i++) alpha[i] = (byte)(((6 - i) * alpha[0] + (i - 1) * alpha[1]) / 5); alpha[6] = 0; alpha[7] = 255; }
                        for (int i = 0; i < 6; i++) abits |= (ulong)d[b + 2 + i] << (8 * i);
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        var px = pal[(bits >> (2 * i)) & 3];
                        if (bf == 0x88) px.a = alpha[(abits >> (3 * i)) & 7];
                        img.Pixels[(by * 4 + (i >> 2)) * w + bx * 4 + (i & 3)] = px;
                    }
                }
            return img;
        }

        static Color32 C565(ushort c) => new Color32((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), 255);
        static Color32 Mix(Color32 a, Color32 b, int wa, int wb)
        {
            int s = wa + wb;
            return new Color32((byte)((a.r * wa + b.r * wb) / s), (byte)((a.g * wa + b.g * wb) / s), (byte)((a.b * wa + b.b * wb) / s), 255);
        }
    }

    /// <summary>
    /// Rastert VMF-Shapes auf der CPU in ein RGBA-Bild (Shape-Koordinaten, Maßstab = Pixel pro Einheit).
    /// Füllungen: even-odd je Füllstil (Kanten mit fill0 oder fill1 = Stil), 3x3 Supersampling.
    /// Linien: je Segment ein Rechteck, vereinigt per Maximum.
    /// </summary>
    public static class VmfRaster
    {
        public sealed class Result { public int Width, Height; public Color32[] Pixels; public float OriginX, OriginY, Scale; }

        const int SS = 3;

        // Arbeitspuffer je Thread wiederverwenden (Rastern läuft auch im Hintergrund-Thread des VmfPlayer) – sonst je große
        // Shape zig MB Garbage -> GC-Ruckler im Spiel
        [ThreadStatic] static Vector4[] _accBuf;
        [ThreadStatic] static float[] _covBuf, _localBuf;
        static T[] Buf<T>(ref T[] b, int n) { if (b == null || b.Length < n) b = new T[Math.Max(n, 1024)]; Array.Clear(b, 0, n); return b; }

        public static Result Render(VmfMovie.Shape s, float scale, Func<int, (PixelImage img, VmfMovie.Bitmap bmp)> bitmap)
        {
            var bnd = s.Bounds;
            int W = Mathf.Max(1, Mathf.CeilToInt(bnd.width * scale) + 2), H = Mathf.Max(1, Mathf.CeilToInt(bnd.height * scale) + 2);
            if ((long)W * H > 2_500_000) { float k = Mathf.Sqrt(2_500_000f / ((long)W * H)); scale *= k; W = Mathf.CeilToInt(bnd.width * scale) + 2; H = Mathf.CeilToInt(bnd.height * scale) + 2; }
            float ox = bnd.xMin - 1f / scale, oy = bnd.yMin - 1f / scale;
            int N = W * H;
            var acc = Buf(ref _accBuf, N);   // vormultipliziert

            // Füllungen
            var groups = new Dictionary<int, List<Vector4>>();
            foreach (var p in s.Paths)
            {
                for (int side = 0; side < 2; side++)
                {
                    int f = side == 0 ? p.Fill0 : p.Fill1;
                    if (f <= 0 || f > s.Fills.Count) continue;
                    if (side == 1 && p.Fill1 == p.Fill0) continue;
                    if (!groups.TryGetValue(f, out var list)) groups[f] = list = new List<Vector4>();
                    for (int i = 0; i + 1 < p.Points.Count; i++) list.Add(new Vector4(p.Points[i].x, p.Points[i].y, p.Points[i + 1].x, p.Points[i + 1].y));
                }
            }
            var cov = Buf(ref _covBuf, N);
            foreach (var kv in groups)
            {
                Array.Clear(cov, 0, N);
                if (!Coverage(kv.Value, W, H, ox, oy, scale, cov, false)) continue;
                Composite(acc, cov, W, H, ox, oy, scale, s.Fills[kv.Key - 1], bitmap);
            }
            // Linien
            foreach (var p in s.Paths)
            {
                if (p.Line <= 0 || p.Line > s.Lines.Count) continue;
                var ls = s.Lines[p.Line - 1];
                float hw = Mathf.Max(ls.Width, 1f / scale) * 0.5f;
                Array.Clear(cov, 0, N);
                var quad = new List<Vector4>(4);
                for (int i = 0; i + 1 < p.Points.Count; i++)
                {
                    Vector2 a = p.Points[i], b = p.Points[i + 1], dir = b - a;
                    float l = dir.magnitude; if (l < 1e-5f) continue;
                    var n = new Vector2(-dir.y, dir.x) / l * hw;
                    Vector2 q0 = a + n, q1 = b + n, q2 = b - n, q3 = a - n;
                    quad.Clear();
                    quad.Add(new Vector4(q0.x, q0.y, q1.x, q1.y)); quad.Add(new Vector4(q1.x, q1.y, q2.x, q2.y));
                    quad.Add(new Vector4(q2.x, q2.y, q3.x, q3.y)); quad.Add(new Vector4(q3.x, q3.y, q0.x, q0.y));
                    Coverage(quad, W, H, ox, oy, scale, cov, true);
                }
                var fs = new VmfMovie.FillStyle { Type = 0, Color = ls.Color };
                Composite(acc, cov, W, H, ox, oy, scale, fs, bitmap);
            }

            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var c = acc[y * W + x];
                    // Unity-Texturen: Zeile 0 = unten -> vertikal spiegeln
                    int di = (H - 1 - y) * W + x;
                    if (c.w <= 1e-4f) { px[di] = new Color32(0, 0, 0, 0); continue; }
                    px[di] = new Color32((byte)Mathf.Clamp(c.x / c.w * 255f, 0, 255), (byte)Mathf.Clamp(c.y / c.w * 255f, 0, 255),
                        (byte)Mathf.Clamp(c.z / c.w * 255f, 0, 255), (byte)Mathf.Clamp(c.w * 255f, 0, 255));
                }
            return new Result { Width = W, Height = H, Pixels = px, OriginX = ox, OriginY = oy, Scale = scale };
        }

        /// <summary>even-odd Abdeckung; union = true: Maximum mit vorhandener Abdeckung (für Linienrechtecke).</summary>
        static bool Coverage(List<Vector4> edges, int W, int H, float ox, float oy, float sc, float[] cov, bool union)
        {
            int n = edges.Count; if (n == 0) return false;
            float s = sc * SS;
            var x0 = new float[n]; var y0 = new float[n]; var x1 = new float[n]; var y1 = new float[n];
            float ymin = float.MaxValue, ymax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var e = edges[i];
                x0[i] = (e.x - ox) * s; y0[i] = (e.y - oy) * s; x1[i] = (e.z - ox) * s; y1[i] = (e.w - oy) * s;
                ymin = Mathf.Min(ymin, Mathf.Min(y0[i], y1[i])); ymax = Mathf.Max(ymax, Mathf.Max(y0[i], y1[i]));
            }
            int Wss = W * SS, Hss = H * SS;
            int r0 = Mathf.Max(0, Mathf.FloorToInt(ymin)), r1 = Mathf.Min(Hss - 1, Mathf.CeilToInt(ymax));
            var xs = new List<float>(16);
            int N = W * H;
            var local = union ? Buf(ref _localBuf, N) : cov;
            const float w = 1f / (SS * SS);
            bool any = false;
            for (int row = r0; row <= r1; row++)
            {
                float yc = row + 0.5f;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    float a = y0[i], b = y1[i];
                    if ((a <= yc && b > yc) || (b <= yc && a > yc)) xs.Add(x0[i] + (yc - a) * (x1[i] - x0[i]) / (b - a));
                }
                if (xs.Count < 2) continue;
                xs.Sort();
                int py = row / SS;
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int a = Mathf.Max(0, Mathf.CeilToInt(xs[k] - 0.5f)), b = Mathf.Min(Wss, Mathf.CeilToInt(xs[k + 1] - 0.5f));
                    for (int sx = a; sx < b; sx++) local[py * W + sx / SS] += w;
                    if (b > a) any = true;
                }
            }
            if (union) for (int i = 0; i < N; i++) cov[i] = Mathf.Max(cov[i], Mathf.Min(1f, local[i]));
            return any;
        }

        static void Composite(Vector4[] acc, float[] cov, int W, int H, float ox, float oy, float sc, VmfMovie.FillStyle fs,
            Func<int, (PixelImage img, VmfMovie.Bitmap bmp)> bitmap)
        {
            Matrix2D inv = Matrix2D.Identity;
            PixelImage img = null; VmfMovie.Bitmap bmp = null;
            bool grad = fs.Type == 0x10 || fs.Type == 0x12 || fs.Type == 0x13, bm = fs.Type >= 0x40;
            if (grad || bm)
            {
                var m = fs.Matrix; m.A /= 20f; m.B /= 20f; m.C /= 20f; m.D /= 20f;   // Linearteil Twips -> Pixel
                inv = m.Inverse();
                if (bm)
                {
                    if (bitmap != null) { var r = bitmap(fs.BitmapId); img = r.img; bmp = r.bmp; }
                    if (img == null) return;   // dynamische/fehlende Bitmap (z.B. 65535) -> nicht zeichnen
                }
            }
            bool repeat = fs.Type == 0x40 || fs.Type == 0x42;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float c = cov[y * W + x]; if (c <= 0f) continue;
                    if (c > 1f) c = 1f;
                    Color32 col;
                    if (fs.Type == 0) col = fs.Color;
                    else
                    {
                        var p = new Vector2((x + 0.5f) / sc + ox, (y + 0.5f) / sc + oy);
                        var uv = inv.Apply(p);
                        if (grad)
                        {
                            float g = fs.Type == 0x10 ? (uv.x + 16384f) / 32768f : Mathf.Sqrt(uv.x * uv.x + uv.y * uv.y) / 16384f;
                            col = Gradient(fs, Mathf.Clamp01(g) * 255f);
                        }
                        else
                        {
                            // Bitmap-Pixel -> Ausschnitt in der Quelltextur
                            float bw = Mathf.Max(1, bmp.Width), bh = Mathf.Max(1, bmp.Height);
                            float u = uv.x / bw, v = uv.y / bh;
                            if (repeat) { u -= Mathf.Floor(u); v -= Mathf.Floor(v); } else { u = Mathf.Clamp01(u); v = Mathf.Clamp01(v); }
                            float tx = Mathf.Lerp(bmp.U0, bmp.U1, u) * img.Width, ty = Mathf.Lerp(bmp.V0, bmp.V1, v) * img.Height;
                            col = img.Sample(tx, ty);
                        }
                    }
                    float a = col.a / 255f * c;
                    var dst = acc[y * W + x];
                    acc[y * W + x] = new Vector4(col.r / 255f * a + dst.x * (1 - a), col.g / 255f * a + dst.y * (1 - a), col.b / 255f * a + dst.z * (1 - a), a + dst.w * (1 - a));
                }
        }

        static Color32 Gradient(VmfMovie.FillStyle fs, float r)
        {
            var rs = fs.Ratios; var cs = fs.Colors;
            if (rs == null || rs.Length == 0) return new Color32(0, 0, 0, 0);
            if (r <= rs[0]) return cs[0];
            for (int i = 1; i < rs.Length; i++)
                if (r <= rs[i]) return Color32.Lerp(cs[i - 1], cs[i], (r - rs[i - 1]) / Mathf.Max(1f, rs[i] - rs[i - 1]));
            return cs[cs.Length - 1];
        }
    }
}
