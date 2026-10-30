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
    /// Non-standard DES-CBC Feistel cipher and Eden Games 6-round Key Derivation
    /// Function (KDF) reverse-engineered from TDU2 (Eden Games) address 0x0055cdf0.
    /// Raw DES without the initial/final bit permutations.
    /// </summary>
    public static class DesCrypto
    {
        public static readonly int[] PC2 =
        {
            13, 16, 10, 23,  0,  4,  2, 27, 14,  5, 20,  9,
            22, 18, 11,  3, 25,  7, 15,  6, 26, 19, 12,  1,
            40, 51, 30, 36, 46, 54, 29, 39, 50, 44, 32, 47,
            43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31
        };

        public static readonly int[] Shifts = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };

        public static readonly int[] EStandard =
        {
            31,  0,  1,  2,  3,  4,
             3,  4,  5,  6,  7,  8,
             7,  8,  9, 10, 11, 12,
            11, 12, 13, 14, 15, 16,
            15, 16, 17, 18, 19, 20,
            19, 20, 21, 22, 23, 24,
            23, 24, 25, 26, 27, 28,
            27, 28, 29, 30, 31,  0
        };

        public static readonly byte[][] KdfTables = new byte[6][];
        public static readonly uint[][] SP = new uint[8][];

        static DesCrypto()
        {
            byte[] kdfRaw = Convert.FromBase64String(Tables.KdfTablesB64);
            for (int i = 0; i < 6; i++)
            {
                var a = new byte[56];
                Array.Copy(kdfRaw, i * 56, a, 0, 56);
                KdfTables[i] = a;
            }

            byte[] spRaw = Convert.FromBase64String(Tables.SpTableB64);
            for (int i = 0; i < 8; i++)
            {
                SP[i] = new uint[64];
                for (int j = 0; j < 64; j++)
                    SP[i][j] = BinaryPrimitives.ReadUInt32LittleEndian(spRaw.AsSpan((i * 64 + j) * 4, 4));
            }
        }

        public static byte[] Rot100(byte[] arr, int sh)
        {
            int n = arr.Length;
            var res = new byte[n];
            for (int i = 0; i < n; i++)
                res[i] = arr[(i + sh) % n];
            return res;
        }

        public static int[][] DesKeySchedule(byte[] key56)
        {
            var c = new byte[28];
            var d = new byte[28];
            Array.Copy(key56, 0, c, 0, 28);
            Array.Copy(key56, 28, d, 0, 28);

            var subkeys = new int[16][];
            for (int rnd = 0; rnd < 16; rnd++)
            {
                int sh = Shifts[rnd];
                c = Rot100(c, sh);
                d = Rot100(d, sh);
                var cd = new byte[56];
                Array.Copy(c, 0, cd, 0, 28);
                Array.Copy(d, 0, cd, 28, 28);
                var sk = new int[48];
                for (int i = 0; i < 48; i++)
                    sk[i] = cd[PC2[i]];
                subkeys[rnd] = sk;
            }
            return subkeys;
        }

        public static int BitAt(uint r, int idx) => (int)((r >> (31 - idx)) & 1u);

        public static uint Feistel(uint r, int[] sk)
        {
            uint outv = 0;
            for (int sb = 0; sb < 8; sb++)
            {
                int bs = sb * 6;
                int cval =
                    ((BitAt(r, EStandard[bs]) ^ sk[bs]) << 5) |
                    ((BitAt(r, EStandard[bs + 1]) ^ sk[bs + 1]) << 4) |
                    ((BitAt(r, EStandard[bs + 2]) ^ sk[bs + 2]) << 3) |
                    ((BitAt(r, EStandard[bs + 3]) ^ sk[bs + 3]) << 2) |
                    ((BitAt(r, EStandard[bs + 4]) ^ sk[bs + 4]) << 1) |
                    (BitAt(r, EStandard[bs + 5]) ^ sk[bs + 5]);
                outv ^= SP[sb][cval];
            }
            return outv;
        }

        public static (uint L, uint R) DesEncrypt(uint l, uint r, int[][] subkeys)
        {
            for (int rnd = 0; rnd < 15; rnd++)
            {
                uint f = Feistel(r, subkeys[rnd]);
                (l, r) = (r, l ^ f);
            }
            uint ff = Feistel(r, subkeys[15]);
            return (l ^ ff, r);
        }

        public static (uint L, uint R) DesDecrypt(uint l, uint r, int[][] subkeys)
        {
            for (int rnd = 15; rnd > 0; rnd--)
            {
                uint f = Feistel(r, subkeys[rnd]);
                (l, r) = (r, l ^ f);
            }
            uint ff = Feistel(r, subkeys[0]);
            return (l ^ ff, r);
        }

        public static int[][] Kdf(byte[] keyStr)
        {
            var bitBuffer = new byte[64];
            int strPtr = 0;
            int strLen = keyStr.Length;

            for (int round = 0; round < 6; round++)
            {
                for (int i = 0; i < 8; i++)
                {
                    byte c;
                    if (strPtr < strLen && keyStr[strPtr] != 0)
                    {
                        c = keyStr[strPtr];
                        strPtr++;
                    }
                    else
                    {
                        c = 0;
                    }
                    for (int b = 0; b < 8; b++)
                        bitBuffer[i * 8 + b] ^= (byte)((c >> (7 - b)) & 1);
                }

                int[][] rSubkeys = DesKeySchedule(KdfTables[round]);
                uint l = 0, r = 0;
                for (int b = 0; b < 32; b++)
                {
                    if (bitBuffer[b] != 0) l |= 1u << (31 - b);
                    if (bitBuffer[32 + b] != 0) r |= 1u << (31 - b);
                }

                for (int rnd = 0; rnd < 15; rnd++)
                {
                    uint f = Feistel(r, rSubkeys[rnd]);
                    (l, r) = (r, l ^ f);
                }
                uint ff = Feistel(r, rSubkeys[15]);
                l ^= ff;

                for (int b = 0; b < 32; b++)
                {
                    bitBuffer[b] = (byte)((l >> (31 - b)) & 1);
                    bitBuffer[32 + b] = (byte)((r >> (31 - b)) & 1);
                }
            }

            var fileKey = new byte[56];
            Array.Copy(bitBuffer, 0, fileKey, 0, 56);
            return DesKeySchedule(fileKey);
        }
    }
}
