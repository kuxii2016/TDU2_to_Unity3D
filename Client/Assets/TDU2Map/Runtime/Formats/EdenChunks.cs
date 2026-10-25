using System.Collections.Generic;
using TDU2.IO;

namespace TDU2.Formats
{
    /// <summary>
    /// Eden-Ressourcen (.3dd/.3dg/.2dm/.2db/.pmi ...) bestehen aus Chunks:
    ///   Datei-Header 16 Byte: u32 version, u32 0, u32 dateigröße, char[4] typ (".3DD")
    ///   Chunk: char[4] tag, u32 flags, u32 gesamtgröße, u32 headergröße
    ///          Eigene Daten liegen zwischen +16 und +headergröße, Kind-Chunks ab +headergröße.
    /// Alle Zeiger in den Strukturen sind absolute Offsets innerhalb der Datei
    /// (REAL-Chunk = Relokationstabelle für das In-Place-Laden der Engine).
    /// </summary>
    public struct Chunk
    {
        public string Tag;
        public int Offset;
        public int Size;
        public int HeaderSize;
        public int DataOffset => Offset + 16;
        public int ChildrenOffset => Offset + HeaderSize;
        public int End => Offset + Size;
        public override string ToString() => $"{Tag}@{Offset:x} size={Size} hdr={HeaderSize}";
    }

    public static class EdenChunks
    {
        public static List<Chunk> Read(byte[] d, int start, int end)
        {
            var list = new List<Chunk>();
            int o = start;
            while (o + 16 <= end)
            {
                int size = (int)Bin.U32(d, o + 8);
                int hs = (int)Bin.U32(d, o + 12);
                if (size < 16 || o + size > end) break;
                list.Add(new Chunk { Tag = Bin.Tag(d, o), Offset = o, Size = size, HeaderSize = hs });
                o += size;
            }
            return list;
        }

        public static List<Chunk> TopLevel(byte[] d) => Read(d, 16, d.Length);

        public static List<Chunk> Children(byte[] d, Chunk c)
        {
            if (c.HeaderSize >= c.Size) return new List<Chunk>();
            return Read(d, c.ChildrenOffset, c.End);
        }

        public static bool TryFind(List<Chunk> list, string tag, out Chunk c)
        {
            foreach (var x in list) if (x.Tag == tag) { c = x; return true; }
            c = default;
            return false;
        }

        /// <summary>HASH-Chunk: Einträge a 16 Byte {u64 key, u32 offset, u32 0}.</summary>
        public static Dictionary<ulong, int> ReadHash(byte[] d, Chunk hash)
        {
            var r = new Dictionary<ulong, int>();
            for (int p = hash.DataOffset; p + 16 <= hash.End; p += 16)
            {
                ulong key = Bin.U64(d, p);
                r[key] = (int)Bin.U32(d, p + 8);
            }
            return r;
        }

        /// <summary>Für Array-Chunks (OBJA, INSA, HIEA, GEOA, MATA): Hash-Tabelle + Elemente.</summary>
        public static Dictionary<ulong, int> ReadArray(byte[] d, Chunk array, string elementTag, List<Chunk> elements)
        {
            Dictionary<ulong, int> hash = null;
            foreach (var c in Children(d, array))
            {
                if (c.Tag == "HASH") hash = ReadHash(d, c);
                else if (elementTag == null || c.Tag == elementTag) elements?.Add(c);
            }
            return hash ?? new Dictionary<ulong, int>();
        }
    }
}
