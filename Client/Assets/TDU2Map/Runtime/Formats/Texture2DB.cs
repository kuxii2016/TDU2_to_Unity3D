using System;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    /// <summary>
    /// .2db = Textur ("BMAP"-Chunk).
    ///   Datei: 16 Byte Header, BMAP-Chunk ab 0x10.
    ///   0x20 u64 Key, 0x28 u16 Breite, 0x2A u16 Höhe, 0x2C u16 Tiefe/Faces (6 = Cubemap),
    ///   0x2E u8 Mipmaps, 0x2F u8 Mipmaps, 0x30 u32 Format (0x84 = DXT1, 0x88 = DXT5, Bit 0x40 = Cubemap),
    ///   Pixeldaten ab 0x50 (D3D-Reihenfolge: Mip 0 .. n, Blöcke mindestens 4x4).
    /// </summary>
    public sealed class TextureDef
    {
        public ulong Key;
        public int Width, Height, Depth, Mips, Format;
        public byte[] Data;
        public const int DataOffset = 0x50;
        public string Name;

        public bool IsCube => (Format & 0x40) != 0 || Depth == 6;
        public int BaseFormat => Format & ~0x40;

        public static TextureDef Parse(byte[] d, string name = null)
        {
            if (d == null || d.Length < DataOffset || !Bin.TagIs(d, 0x10, "BMAP")) return null;
            var def = new TextureDef
            {
                Key = Bin.U64(d, 0x20),
                Width = Bin.U16(d, 0x28),
                Height = Bin.U16(d, 0x2A),
                Depth = Bin.U16(d, 0x2C),
                Mips = Math.Max(1, (int)d[0x2E]),
                Format = (int)Bin.U32(d, 0x30),
                Data = d,
                Name = name,
            };
            int maxMips = Mathf.FloorToInt(Mathf.Log(Mathf.Max(def.Width, def.Height), 2f)) + 1;
            if (def.Mips > maxMips) def.Mips = maxMips;
            return def;
        }

        /// <summary>Liest nur den Key aus den ersten 0x28 Bytes einer .2db.</summary>
        public static ulong PeekKey(byte[] head) => head != null && head.Length >= 0x28 ? Bin.U64(head, 0x20) : 0;

        static int BlockBytes(int baseFormat) => baseFormat == 0x84 ? 8 : 16;

        static int MipChainSize(int w, int h, int mips, int blockBytes)
        {
            int total = 0;
            for (int i = 0; i < mips; i++)
            {
                int mw = Math.Max(1, w >> i), mh = Math.Max(1, h >> i);
                total += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * blockBytes;
            }
            return total;
        }

        /// <summary>Erzeugt eine Unity-Textur (nur im Main-Thread aufrufen).</summary>
        public Texture2D CreateTexture(TextureWrapMode wrapU = TextureWrapMode.Repeat, TextureWrapMode wrapV = TextureWrapMode.Repeat, bool linear = false, bool keepReadable = false)
        {
            if (IsCube) return null;
            int avail = Data.Length - DataOffset;
            Texture2D tex = null;
            int bf = BaseFormat;
            if (bf == 0x84 || bf == 0x88)
            {
                int bb = BlockBytes(bf);
                int mips = Mips;
                int maxMips = Mathf.FloorToInt(Mathf.Log(Mathf.Max(Width, Height), 2f)) + 1;
                if (mips > maxMips) mips = maxMips;
                while (mips > 1 && MipChainSize(Width, Height, mips, bb) > avail) mips--;
                int size = MipChainSize(Width, Height, mips, bb);
                if (size > avail || Width % 4 != 0 || Height % 4 != 0) return null;
                var fmt = bf == 0x84 ? TextureFormat.DXT1 : TextureFormat.DXT5;
                tex = new Texture2D(Width, Height, fmt, mips, linear);
                var raw = new byte[size];
                Buffer.BlockCopy(Data, DataOffset, raw, 0, size);
                tex.LoadRawTextureData(raw);
                tex.Apply(false, !keepReadable);
            }
            else if (bf == 0x86)
            {
                // DXT3 wird von Unity nicht direkt unterstützt -> CPU-Dekodierung von Mip 0
                var px = DecodeDxt3(Data, DataOffset, Width, Height);
                if (px == null) return null;
                tex = new Texture2D(Width, Height, TextureFormat.RGBA32, true, linear);
                tex.SetPixels32(px);
                tex.Apply(true, !keepReadable);
            }
            else
            {
                DebugManager.Warning($"Unknown texture format: 0x{Format:x} ({Name ?? Bin.KeyToString(Key)})");
                return null;
            }
            tex.name = Name ?? Bin.KeyToString(Key);
            if (LooksPacked()) PackedTextures.Add(tex.GetInstanceID());
            MeanLuminance[tex.GetInstanceID()] = MeanLum();
            tex.wrapModeU = wrapU;
            tex.wrapModeV = wrapV;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            return tex;
        }

        /// <summary>
        /// "Gepackte" Detailtexturen (d1_*, d2_*, beton_01 ...): nur R = Graustufen-Detail, G/B = andere Daten
        /// (wirken unbehandelt rot). Erkennung über die DXT-Blockfarben von Mip 0 (Instanz-IDs der erzeugten Texturen).
        /// </summary>
        public static readonly System.Collections.Generic.HashSet<int> PackedTextures = new System.Collections.Generic.HashSet<int>();

        /// <summary>Mittlere Helligkeit (0..1) der erzeugten Texturen, z.B. zum Normieren von Overlays.</summary>
        public static readonly System.Collections.Generic.Dictionary<int, float> MeanLuminance = new System.Collections.Generic.Dictionary<int, float>();

        public float MeanLum()
        {
            int bf = BaseFormat;
            if ((bf != 0x84 && bf != 0x88) || Data == null) return 0.5f;
            int bb = BlockBytes(bf), colOff = bf == 0x88 ? 8 : 0;
            int blocks = Math.Max(1, Width / 4) * Math.Max(1, Height / 4);
            int step = Math.Max(1, blocks / 2048);
            double sum = 0; int n = 0;
            for (int i = 0; i < blocks; i += step)
            {
                int o = DataOffset + i * bb + colOff;
                if (o + 4 > Data.Length) break;
                for (int k = 0; k < 2; k++)
                {
                    int c = Data[o + k * 2] | (Data[o + k * 2 + 1] << 8);
                    sum += 0.3 * ((c >> 11) & 31) / 31.0 + 0.59 * ((c >> 5) & 63) / 63.0 + 0.11 * (c & 31) / 31.0; n++;
                }
            }
            return n > 0 ? (float)(sum / n) : 0.5f;
        }

        public bool LooksPacked()
        {
            int bf = BaseFormat;
            if ((bf != 0x84 && bf != 0x88) || Data == null) return false;
            int bb = BlockBytes(bf), colOff = bf == 0x88 ? 8 : 0;
            int blocks = Math.Max(1, Width / 4) * Math.Max(1, Height / 4);
            int step = Math.Max(1, blocks / 2048);
            double r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < blocks; i += step)
            {
                int o = DataOffset + i * bb + colOff;
                if (o + 4 > Data.Length) break;
                for (int k = 0; k < 2; k++)
                {
                    int c = Data[o + k * 2] | (Data[o + k * 2 + 1] << 8);
                    r += ((c >> 11) & 31) / 31.0; g += ((c >> 5) & 63) / 63.0; b += (c & 31) / 31.0; n++;
                }
            }
            if (n == 0) return false;
            r /= n; g /= n; b /= n;
            return r > 0.2 && g < 0.35 * r && b < 0.2 * r;
        }

        public static TextureWrapMode WrapFromEden(byte mode)
        {
            switch (mode)
            {
                // Eden: 1 = Wrap, 2 = Clamp (Lightmaps, Colormaps), 3 = Mirror (beobachtet; Lightmaps/Colormaps stehen immer auf 2)
                case 2: return TextureWrapMode.Clamp;
                case 3: return TextureWrapMode.Mirror;
                case 4: return TextureWrapMode.Clamp;
                default: return TextureWrapMode.Repeat;
            }
        }

        static Color32[] DecodeDxt3(byte[] d, int off, int w, int h)
        {
            int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4);
            if (off + bw * bh * 16 > d.Length) return null;
            var px = new Color32[w * h];
            var pal = new Color32[4];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int b = off + (by * bw + bx) * 16;
                    ushort c0 = Bin.U16(d, b + 8), c1 = Bin.U16(d, b + 10);
                    pal[0] = C565(c0); pal[1] = C565(c1);
                    pal[2] = Lerp(pal[0], pal[1], 1, 3); pal[3] = Lerp(pal[0], pal[1], 2, 3);
                    uint bits = Bin.U32(d, b + 12);
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x >= w || y >= h) continue;
                        var c = pal[(bits >> (2 * i)) & 3];
                        int an = (d[b + (i >> 1)] >> ((i & 1) * 4)) & 0xF;
                        c.a = (byte)(an * 17);
                        px[y * w + x] = c;
                    }
                }
            return px;
        }

        static Color32 C565(ushort c) => new Color32((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), 255);
        static Color32 Lerp(Color32 a, Color32 b, int num, int den) => new Color32(
            (byte)((a.r * (den - num) + b.r * num) / den), (byte)((a.g * (den - num) + b.g * num) / den), (byte)((a.b * (den - num) + b.b * num) / den), 255);
    }
}
