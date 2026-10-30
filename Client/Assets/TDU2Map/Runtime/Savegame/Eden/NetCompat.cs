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
    /// <summary>Ersatz für .NET-5-APIs, Bereichs-Indizes und unsafe-Code (Unity: kein unsafe, .NET Standard 2.1).</summary>
    internal static class NetCompat
    {
        public static byte[] Sub(byte[] a, int start, int end)
        {
            if (start < 0 || end > a.Length || end < start) throw new InvalidDataException("Bereich außerhalb der Daten.");
            var r = new byte[end - start];
            Buffer.BlockCopy(a, start, r, 0, r.Length);
            return r;
        }

        public static bool Same(byte[] a, byte[] b, int n)
        {
            if (a.Length < n || b.Length < n) return false;
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>ISO-8859-1: ein Byte je Zeichen (Zeichen &gt; 255 -> '?').</summary>
        public static byte[] Latin1Bytes(string s)
        {
            var r = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) r[i] = s[i] <= 255 ? (byte)s[i] : (byte)'?';
            return r;
        }

        public static string Latin1String(byte[] a, int off, int len)
        {
            var c = new char[len];
            for (int i = 0; i < len; i++) c[i] = (char)a[off + i];
            return new string(c);
        }

        public static uint SingleToUInt32Bits(float v) => unchecked((uint)BitConverter.SingleToInt32Bits(v));

        public static ulong DoubleToUInt64Bits(double v) => unchecked((ulong)BitConverter.DoubleToInt64Bits(v));

        public static float ReadSingleLittleEndian(ReadOnlySpan<byte> s) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s));

        public static double ReadDoubleLittleEndian(ReadOnlySpan<byte> s) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(s));
    }
}
