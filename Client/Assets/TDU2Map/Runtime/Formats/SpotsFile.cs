using System.Collections.Generic;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    public sealed class Spot
    {
        public int Index;
        public ulong Key;          // komprimierter Name
        public string Code;        // lesbarer Teil, z.B. "ECD_ITAL" (= exterior/ecd_ital.bnk)
        public Vector3 Position;   // Weltposition (Inselkoordinaten)
        public Vector3 Position2;  // zweite Position (Ein-/Ausfahrt)
        public float Value1, Heading2;
        public int AreaX, AreaZ, SectorX, SectorZ; // Bytes 52..55
        public ushort TypeA, TypeB;                // u16 @256/@258
        public Matrix4x4 Box1, Box2;               // zwei Transformationen (Trigger/Kamera?)

        public string Category => SpotsFile.CategoryFromCode(Code);
    }

    /// <summary>
    /// level/spots.bin = POIs (Händler, Tuning, Häuser, Waschanlagen ...).
    ///   ".SPT" u32 Version(2) u32 1 u32 Hash, "SPTA" @0x10, Einträge ab 0x20 à 272 Byte:
    ///   +0 u64 Key, +8 char[8] Code, +16 float3 Position, +28 float, +32 float, +36 float,
    ///   +40 float3 Position2, +52 u8 areaX, areaZ, sectorX, sectorZ, +56 float Richtung,
    ///   +64 u16[32] Sektor-Liste (0xFFFF = leer), +128 2× float4x4, +256 u16 typA, u16 typB.
    /// </summary>
    public sealed class SpotsFile
    {
        public readonly List<Spot> Spots = new List<Spot>();
        const int Stride = 272;

        public static SpotsFile Parse(byte[] d)
        {
            var f = new SpotsFile();
            if (d == null || d.Length < 0x20 || !Bin.TagIs(d, 0, ".SPT")) return f;
            int n = (d.Length - 0x20) / Stride;
            for (int i = 0; i < n; i++)
            {
                int o = 0x20 + i * Stride;
                var s = new Spot
                {
                    Index = i,
                    Key = Bin.U64(d, o),
                    Code = Bin.CString(d, o + 8, 8),
                    Position = new Vector3(Bin.F32(d, o + 16), Bin.F32(d, o + 20), Bin.F32(d, o + 24)),
                    Value1 = Bin.F32(d, o + 32),
                    Position2 = new Vector3(Bin.F32(d, o + 40), Bin.F32(d, o + 44), Bin.F32(d, o + 48)),
                    AreaX = d[o + 52],
                    AreaZ = d[o + 53],
                    SectorX = d[o + 54],
                    SectorZ = d[o + 55],
                    Heading2 = Bin.F32(d, o + 56),
                    TypeA = Bin.U16(d, o + 256),
                    TypeB = Bin.U16(d, o + 258),
                    Box1 = ReadMatrix(d, o + 128),
                    Box2 = ReadMatrix(d, o + 192),
                };
                if (float.IsNaN(s.Position.x)) continue;
                //Debug.Log("Spotreader: " + s.Code + " : " + s.Key);
                f.Spots.Add(s);
            }
            return f;
        }

        static Matrix4x4 ReadMatrix(byte[] d, int p)
        {
            var m = new Matrix4x4();
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    m[col, row] = Bin.F32(d, p + (row * 4 + col) * 4);
            return m;
        }

        /// <summary>Kategorie aus dem Code-Präfix (Namen wie in exterior/*.bnk). Mit '?' markierte sind geraten.</summary>
        public static string CategoryFromCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "Unbekannt";
            var c = code.ToUpperInvariant();
            if (c.StartsWith("ECDC")) return "Autohändler (Classic?)";
            if (c.StartsWith("ECD")) return "Autohändler";
            if (c.StartsWith("EBD")) return "Motorradhändler?";
            if (c.StartsWith("ETUN")) return "Tuning";
            if (c.StartsWith("ECW")) return "Waschanlage?";
            if (c.StartsWith("EPH")) return "Haus/Immobilie (EPH)";
            if (c.StartsWith("ECH")) return "Haus (ECH)?";
            if (c.StartsWith("EAS")) return "Shop (EAS)?";
            if (c.StartsWith("EHD")) return "Shop (EHD)?";
            if (c.StartsWith("EGAM")) return "Comunity Racing Center";
            if (c.StartsWith("EAIR")) return "Flughafen";
            if (c.StartsWith("EDI")) return "EDI?";
            if (c.StartsWith("ELAND")) return "Landeplatz";
            if (c.StartsWith("ECAS")) return "Casino";
            return "Spot";
        }
    }
}
