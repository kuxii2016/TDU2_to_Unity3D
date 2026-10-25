using System;
using System.Text;

namespace TDU2.IO
{
    /// <summary>
    /// Big-endian Lesehilfen. Die meisten TDU2-PC-Dateien sind little-endian (<see cref="Bin"/>),
    /// aber die Rennstrecken-Definitionen (.ig2-Challenges) speichern ihre Zahlen in Big-Endian.
    /// </summary>
    public static class BigEndian
    {
        public static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
        public static short I16(byte[] d, int o) => (short)U16(d, o);

        public static uint U32(byte[] d, int o) =>
            ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

        public static int I32(byte[] d, int o) =>
            (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        public static float F32(byte[] d, int o) => BitConverter.Int32BitsToSingle(I32(d, o));

        /// <summary>4-Zeichen-Tag so wie es in der Datei steht.</summary>
        public static string Tag(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 4);

        public static bool TagIs(byte[] d, int o, string tag) =>
            o >= 0 && o + 4 <= d.Length &&
            d[o] == tag[0] && d[o + 1] == tag[1] && d[o + 2] == tag[2] && d[o + 3] == tag[3];
    }
}
