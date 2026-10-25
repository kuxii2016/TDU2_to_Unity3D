using System;
using System.Text;

namespace TDU2.IO
{
    /// <summary>
    /// Little-endian Lesehilfen. Alle TDU2-PC-Dateien sind little-endian.
    /// Thread-sicher (keine Unity-API), damit Parsing im Hintergrund laufen kann.
    /// </summary>
    public static class Bin
    {
        public static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        public static int I32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        public static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
        public static short I16(byte[] d, int o) => (short)(d[o] | (d[o + 1] << 8));
        public static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);
        public static ulong U64(byte[] d, int o) => BitConverter.ToUInt64(d, o);

        /// <summary>4-Zeichen-Tag so wie er in der Datei steht (z.B. "HASH", "INS.", "DXVB").</summary>
        public static string Tag(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 4);

        public static bool TagIs(byte[] d, int o, string tag)
        {
            return d[o] == tag[0] && d[o + 1] == tag[1] && d[o + 2] == tag[2] && d[o + 3] == tag[3];
        }

        /// <summary>IEEE 754 half -> float (ohne Unity-API, damit thread-sicher).</summary>
        public static float Half(ushort h)
        {
            int sign = (h >> 15) & 1;
            int exp = (h >> 10) & 0x1f;
            int mant = h & 0x3ff;
            float v;
            if (exp == 0) v = mant / 1024f * 6.103515625e-05f;            // subnormal
            else if (exp == 31) v = mant == 0 ? float.PositiveInfinity : float.NaN;
            else v = (1f + mant / 1024f) * (float)Math.Pow(2, exp - 15);
            return sign != 0 ? -v : v;
        }

        public static float HalfAt(byte[] d, int o) => Half(U16(d, o));

        /// <summary>Latin-1 Bytes -> string (ohne Codepage-Abhängigkeit, IL2CPP-sicher).</summary>
        public static string Latin1(byte[] d, int o, int len)
        {
            var c = new char[len];
            for (int i = 0; i < len; i++) c[i] = (char)d[o + i];
            return new string(c);
        }

        /// <summary>Nullterminierter ASCII-String fester Länge.</summary>
        public static string CString(byte[] d, int o, int maxLen)
        {
            int n = 0;
            while (n < maxLen && o + n < d.Length && d[o + n] != 0) n++;
            return Encoding.ASCII.GetString(d, o, n);
        }

        /// <summary>
        /// Eden-"Keys" sind 8 Byte. Viele sind lesbar ("V_CHEN_C"), andere sind komprimiert
        /// (hohe Bytes kodieren Silben). Für Anzeige werden nicht druckbare Bytes als ~xx ausgegeben.
        /// </summary>
        public static string KeyToString(ulong key)
        {
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                byte b = (byte)(key >> (8 * i));
                if (b == 0) break;
                if (b >= 32 && b < 127) sb.Append((char)b);
                else sb.Append('~').Append(b.ToString("x2"));
            }
            return sb.Length == 0 ? key.ToString("X16") : sb.ToString();
        }

        public static string KeyHex(ulong key)
        {
            var b = BitConverter.GetBytes(key);
            var sb = new StringBuilder(16);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Key aus einem ASCII-String (max. 8 Zeichen) bauen, z.B. "COLOR".</summary>
        public static ulong KeyFromString(string s)
        {
            ulong k = 0;
            for (int i = 0; i < s.Length && i < 8; i++) k |= (ulong)(byte)s[i] << (8 * i);
            return k;
        }
    }
}
