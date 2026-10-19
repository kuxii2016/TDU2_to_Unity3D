using System.Collections.Generic;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    /// <summary>Gruppenkopf einer Challenge (TNOC + ECIL): Lizenz und Startposition der Gruppe.</summary>
    public sealed class SpotGroup
    {
        public ulong License;       // _hashcode aus DBLicense
        public Vector3 Position;    // Startpunkt der Gruppe (Inselkoordinaten)
        public uint Road, Piece;
        public float Heading;
    }

    /// <summary>Ein Rennstart (2EGI = Challenge, TNVE = Event): Name, Startpunkt, Lizenz und Einstellungsblock.</summary>
    public sealed class SpotEntry
    {
        public string Name;         // z.B. "rsa1t01" (= Bnk/ig2/<Name>.ig2)
        public bool IsEvent;
        public ulong License;       // Lizenz der umgebenden TNOC-Gruppe (0 = unbekannt)
        public Vector3 Position;    // Startposition (Inselkoordinaten)
        public uint Road, Piece;
        public float Heading;       // Startrichtung (Bogenmaß um Y)
        public int SettingsOffset;  // Offset des eingebetteten IGE2-Einstellungsblocks
        public int SettingsLength;
    }

    /// <summary>
    /// scenarics/spotworld.bin (.wtps, little-endian) – alle Rennstarts einer Insel.
    ///   "WTPS" u32 Version(3), danach Tags im Wechsel:
    ///     "TNOC" u32 n, u32 Lizenz + "ECIL" f32[3] Position, u32 road, u32 piece, f32 heading,
    ///     "2EGI" char[8] Name, f32[3] Position, u32 road, u32 piece, f32 heading + IGE2-Einstellungsblock,
    ///     "TNVE" wie 2EGI, Block ohne IGE2-Header + u32 0.
    /// </summary>
    public sealed class SpotWorldFile
    {
        public int Version;
        public readonly List<SpotGroup> Groups = new List<SpotGroup>();
        public readonly List<SpotEntry> Entries = new List<SpotEntry>();

        public SpotEntry Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var e in Entries) if (string.Equals(e.Name, name, System.StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public List<SpotEntry> EntriesFor(ulong license)
        {
            var list = new List<SpotEntry>();
            foreach (var e in Entries) if (e.License == license) list.Add(e);
            return list;
        }

        public static bool LooksLikeSpotWorld(byte[] b) => b != null && b.Length >= 8 && Bin.TagIs(b, 0, "WTPS");

        public static SpotWorldFile Parse(byte[] b)
        {
            var f = new SpotWorldFile();
            if (!LooksLikeSpotWorld(b)) return f;
            f.Version = Bin.I32(b, 4);

            ulong curLic = 0;
            int p = 8;
            while (p + 4 <= b.Length)
            {
                string tag = Bin.Tag(b, p);
                if (tag == "TNOC")
                {
                    if (p + 12 > b.Length) break;
                    ulong lic = Bin.U32(b, p + 8);
                    int q = p + 12;
                    var g = new SpotGroup { License = lic };
                    if (Bin.TagIs(b, q, "ECIL") && q + 28 <= b.Length)
                    {
                        g.Position = new Vector3(Bin.F32(b, q + 4), Bin.F32(b, q + 8), Bin.F32(b, q + 12));
                        g.Road = Bin.U32(b, q + 16);
                        g.Piece = Bin.U32(b, q + 20);
                        g.Heading = Bin.F32(b, q + 24);
                        p = q + 28;
                    }
                    else p = q;
                    curLic = lic;
                    f.Groups.Add(g);
                }
                else if (tag == "2EGI" || tag == "TNVE")
                {
                    if (p + 40 > b.Length) break;
                    bool isEvent = tag == "TNVE";
                    var e = new SpotEntry
                    {
                        Name = Bin.CString(b, p + 8, 8),
                        IsEvent = isEvent,
                        License = curLic,
                        Position = new Vector3(Bin.F32(b, p + 16), Bin.F32(b, p + 20), Bin.F32(b, p + 24)),
                        Road = Bin.U32(b, p + 28),
                        Piece = Bin.U32(b, p + 32),
                        Heading = Bin.F32(b, p + 36),
                    };
                    int q = p + 40;
                    e.SettingsOffset = q;
                    int mdsc = -1;
                    if (Bin.TagIs(b, q, "IGE2")) mdsc = q + 0x1c;
                    else if (Bin.TagIs(b, q, "MDSC")) mdsc = q;
                    if (mdsc >= 0 && Bin.TagIs(b, mdsc, "MDSC"))
                    {
                        int msize = BigEndian.I32(b, mdsc + 4);   // eingebetteter IGE2-Block ist big-endian
                        p = mdsc + msize;
                    }
                    else { p = q + 4; }
                    e.SettingsLength = p - q;
                    if (isEvent && p + 4 <= b.Length) p += 4;   // Event endet mit u32 0
                    f.Entries.Add(e);
                }
                else p += 4;
            }
            return f;
        }
    }
}
