// Portiert aus EdenSavegame (TDU2 Savegame editor) für Unity (C# 9 / .NET Standard 2.1, ohne unsafe).
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EdenSavegame
{
    /// <summary>
    /// Eden Games proprietary SHA-1 variant used for per-chunk integrity in TDU2
    /// save containers. Identical to SHA-1 except each of the 16 initial message
    /// words is read as a single byte at block[i*4 + 3].
    /// </summary>
    public static class EdenSha1
    {
        public static uint Rol32(uint x, int n) => (x << n) | (x >> (32 - n));

        public static byte[] Compute(byte[] chunk)
        {
            ulong bitLen = (ulong)chunk.Length * 8;

            int total = chunk.Length + 1;
            while (total % 64 != 56)
                total++;
            total += 8;

            var padded = new byte[total];
            Array.Copy(chunk, padded, chunk.Length);
            padded[chunk.Length] = 0x80;
            for (int i = 0; i < 8; i++)
                padded[total - 8 + i] = (byte)(bitLen >> (56 - i * 8));

            uint h0 = 0x67452301, h1 = 0xefcdab89, h2 = 0x98badcfe, h3 = 0x10325476, h4 = 0xc3d2e1f0;
            var w = new uint[80];

            for (int blk = 0; blk < padded.Length; blk += 64)
            {
                for (int i = 0; i < 16; i++)
                    w[i] = padded[blk + i * 4 + 3];
                for (int i = 16; i < 80; i++)
                    w[i] = Rol32(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);

                uint a = h0, b = h1, c = h2, d = h3, e = h4;
                for (int i = 0; i < 80; i++)
                {
                    uint f, k;
                    if (i < 20) { f = (b & c) | (~b & d); k = 0x5a827999; }
                    else if (i < 40) { f = b ^ c ^ d; k = 0x6ed9eba1; }
                    else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8f1bbcdc; }
                    else { f = b ^ c ^ d; k = 0xca62c1d6; }

                    uint temp = Rol32(a, 5) + f + e + k + w[i];
                    e = d; d = c; c = Rol32(b, 30); b = a; a = temp;
                }

                h0 += a; h1 += b; h2 += c; h3 += d; h4 += e;
            }

            var outp = new byte[20];
            WriteBe(outp, 0, h0);
            WriteBe(outp, 4, h1);
            WriteBe(outp, 8, h2);
            WriteBe(outp, 12, h3);
            WriteBe(outp, 16, h4);
            return outp;
        }

        public static void WriteBe(byte[] buf, int off, uint v)
        {
            buf[off] = (byte)(v >> 24);
            buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8);
            buf[off + 3] = (byte)v;
        }
    }
}
