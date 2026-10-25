using System;
using System.Collections.Generic;
using System.IO;

namespace TDU2.IO
{
    /// <summary>Eintrag in einem TDU2-.bnk-Archiv.</summary>
    public sealed class BnkEntry
    {
        public int Index;
        public string Name;        // Dateiname inkl. Endung, z.B. "sector-3-3-3-3.3dg"
        public string Extension;   // klein, mit Punkt, z.B. ".3dg"
        public string Directory;   // originaler Build-Pfad (Info)
        public long Offset;
        public int Size;
        public ushort Type;        // Bank-Kategorie (4 = Terrain, 5 = Objekte, 14 = Library ...)
        public ushort SubType;     // 0=3dd 1=3dg 2=2dm 3=2db ...
        public uint Hash;

        public override string ToString() => $"{Name} ({Size} bytes, type {Type}/{SubType})";
    }

    /// <summary>
    /// Leser für das Eden-Bankformat ("BANK", in der Datei als "KNAB" gespeichert).
    ///
    /// Header (alle Werte little-endian):
    ///   0x00 u32 0x40, 0x04 u32 Prüfsumme, 0x08 "KNAB", 0x18 u32 Dateigröße,
    ///   0x28 u32 Anzahl Dateien, 0x2C u32 Version (2010),
    ///   0x30 Offset Dateitabelle, 0x34 Offset Typtabelle, 0x38 Offset Namensbaum,
    ///   0x3C Offset Reihenfolge-Tabelle, 0x44 Start der Daten.
    /// Jede Sektion beginnt mit u32 Größe + u32 Hash, Daten ab +8.
    ///   Dateitabelle: je 20 Byte {u32 offset, u32 size, u32 stamp, u32 hash, u32 align}
    ///   Typtabelle  : je 4 Byte  {u16 subType, u16 type}
    ///   Namensbaum  : Präfix-Baum. Byte >= 0x80 => Ordner mit Länge (0x100-b), danach Kinderzahl (LEB128-Varint);
    ///                 Byte < 0x80 => Blatt (Dateiname ohne Endung) mit Länge b.
    ///                 Die Endung ist der direkte Eltern-"Ordner" (".3dg" ...).
    ///   Reihenfolge : Namensindex -> Dateiindex (u8, bzw. u16 wenn Sektionsgröße = 2*Anzahl).
    ///   Neu gepackte Bänke (Version 2026, Mod-Werkzeuge): Namensbaum ohne 0x80-Markierung (siehe WalkNameV2),
    ///   Typtabelle mit Platzhaltern (DE AD 40 06), Dateien nicht auf 2048 Byte ausgerichtet.
    /// </summary>
    public sealed class BnkArchive
    {
        public string FilePath { get; private set; }
        public readonly List<BnkEntry> Entries = new List<BnkEntry>();
        readonly object _lock = new object();

        public static BnkArchive Open(string path)
        {
            var a = new BnkArchive { FilePath = path };
            a.ReadHeader();
            return a;
        }

        void ReadHeader()
        {
            byte[] head;
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var first = new byte[0x48];
                ReadExactly(fs, first, 0, first.Length);
                if (!Bin.TagIs(first, 8, "KNAB"))
                    throw new InvalidDataException("Keine TDU2-Bank (KNAB fehlt): " + FilePath);
                int headerSize = (int)Bin.U32(first, 0x44);
                if (headerSize <= 0x48 || headerSize > fs.Length) headerSize = (int)Math.Min(fs.Length, 1 << 20);
                head = new byte[headerSize];
                fs.Position = 0;
                ReadExactly(fs, head, 0, headerSize);
            }

            int nFiles = (int)Bin.U32(head, 0x28);
            int secFiles = (int)Bin.U32(head, 0x30);
            int secTypes = (int)Bin.U32(head, 0x34);
            int secNames = (int)Bin.U32(head, 0x38);
            int secOrder = (int)Bin.U32(head, 0x3C);

            for (int i = 0; i < nFiles; i++)
            {
                int o = secFiles + 8 + i * 20;
                var e = new BnkEntry
                {
                    Index = i,
                    Offset = Bin.U32(head, o),
                    Size = (int)Bin.U32(head, o + 4),
                    Hash = Bin.U32(head, o + 12),
                };
                int t = secTypes + 8 + i * 4;
                e.SubType = Bin.U16(head, t);
                e.Type = Bin.U16(head, t + 2);
                Entries.Add(e);
            }

            // Namensbaum
            var names = new List<string[]>();
            int namesSize = (int)Bin.U32(head, secNames);
            int p = secNames + 8;
            int end = Math.Min(secNames + 8 + namesSize, secOrder);
            var stack = new List<string>();
            // Variante A (Original, Version 2010): Ordner = Byte >= 0x80. Variante B (neu gepackte Bänke, z.B. Version 2026
            // aus Mod-Werkzeugen): Ordner = [Länge][Kinderzahl][Name], Datei = [Länge][Name]; Dateien sind die Kinder eines
            // Endungsknotens (".2db" ...). Erkennung am ersten Byte.
            bool v2 = p < end && head[p] != 0 && head[p] < 0x80;
            while (p < end && head[p] != 0)
                p = v2 ? WalkNameV2(head, p, end, stack, names) : WalkName(head, p, stack, names);

            // Reihenfolge
            // Sektionsgröße / Dateianzahl = 1 (u8-Indizes) oder 2 (u16-Indizes)
            int orderSize = (int)Bin.U32(head, secOrder);
            bool wide = nFiles > 0 ? orderSize >= nFiles * 2 : false;
            for (int ni = 0; ni < names.Count && ni < nFiles; ni++)
            {
                int fi = wide ? Bin.U16(head, secOrder + 8 + ni * 2) : head[secOrder + 8 + ni];
                if (fi < 0 || fi >= nFiles) continue;
                var path = names[ni];
                var e = Entries[fi];
                string leaf = path[path.Length - 1];
                string ext = path.Length >= 2 ? path[path.Length - 2] : "";
                e.Name = leaf + ext;
                e.Extension = ext.ToLowerInvariant();
                e.Directory = path.Length > 2 ? string.Join("/", path, 0, path.Length - 2) : "";
            }
            foreach (var e in Entries)
                if (e.Name == null) { e.Name = "file" + e.Index; e.Extension = ""; e.Directory = ""; }
        }

        static int WalkName(byte[] d, int p, List<string> stack, List<string[]> outNames)
        {
            byte b = d[p];
            if (b >= 0x80)
            {
                int len = 0x100 - b;
                // Kinderzahl als LEB128-Varint (z.B. 0x80 0x01 = 128)
                int q = p + 1, count = 0, shift = 0;
                while (true)
                {
                    byte v = d[q++];
                    count |= (v & 0x7F) << shift;
                    if ((v & 0x80) == 0) break;
                    shift += 7;
                }
                string s = Bin.Latin1(d, q, len);
                p = q + len;
                stack.Add(s);
                for (int i = 0; i < count; i++) p = WalkName(d, p, stack, outNames);
                stack.RemoveAt(stack.Count - 1);
                return p;
            }
            else
            {
                int len = b;
                string s = Bin.Latin1(d, p + 1, len);
                var full = new string[stack.Count + 1];
                stack.CopyTo(full);
                full[stack.Count] = s;
                outNames.Add(full);
                return p + 1 + len;
            }
        }

        static int WalkNameV2(byte[] d, int p, int end, List<string> stack, List<string[]> outNames)
        {
            bool leaf = stack.Count > 0 && stack[stack.Count - 1].StartsWith(".");
            int len = d[p];
            if (leaf)
            {
                var full = new string[stack.Count + 1];
                stack.CopyTo(full);
                full[stack.Count] = Bin.Latin1(d, p + 1, len);
                outNames.Add(full);
                return p + 1 + len;
            }
            int q = p + 1, count = 0, shift = 0;
            while (q < end)
            {
                byte v = d[q++];
                count |= (v & 0x7F) << shift;
                if ((v & 0x80) == 0) break;
                shift += 7;
            }
            stack.Add(Bin.Latin1(d, q, len));
            p = q + len;
            for (int i = 0; i < count && p < end; i++) p = WalkNameV2(d, p, end, stack, outNames);
            stack.RemoveAt(stack.Count - 1);
            return p;
        }

        static void ReadExactly(Stream s, byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                int r = s.Read(buf, off, count);
                if (r <= 0) throw new EndOfStreamException();
                off += r; count -= r;
            }
        }

        /// <summary>Liest einen kompletten Eintrag (thread-sicher).</summary>
        public byte[] Read(BnkEntry e)
        {
            var buf = new byte[e.Size];
            lock (_lock)
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Position = e.Offset;
                    ReadExactly(fs, buf, 0, e.Size);
                }
            }
            return buf;
        }

        /// <summary>Liest nur einen Teil eines Eintrags (z.B. Texturheader).</summary>
        public byte[] ReadPartial(BnkEntry e, int offset, int count)
        {
            count = Math.Min(count, e.Size - offset);
            var buf = new byte[Math.Max(0, count)];
            if (count <= 0) return buf;
            lock (_lock)
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Position = e.Offset + offset;
                    ReadExactly(fs, buf, 0, count);
                }
            }
            return buf;
        }

        public BnkEntry Find(string name)
        {
            foreach (var e in Entries)
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Erster Eintrag mit Endung, dessen Name optional auf suffix (vor der Endung) endet.</summary>
        public BnkEntry FindByExt(string ext, string nameEndsWith = null)
        {
            foreach (var e in Entries)
            {
                if (e.Extension != ext) continue;
                if (nameEndsWith == null) return e;
                var stem = e.Name.Substring(0, e.Name.Length - ext.Length);
                if (stem.EndsWith(nameEndsWith, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        public IEnumerable<BnkEntry> All(string ext)
        {
            foreach (var e in Entries) if (e.Extension == ext) yield return e;
        }
    }
}
