using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using TDU2.Map.IO;

namespace TDU2.Map.Formats
{
    /// <summary>
    /// Sector-...pgr = Grastyp-Karte (RLE).
    ///   Datei-Header 16 Byte (".RLE"), Chunk "DATA" @0x10, payload @0x20:
    ///   u16 Breite (1024), u16 Höhe (1024), u32 Anzahl Typen, dann Höhe × u32 Zeilen-Offsets (relativ zu 0x20),
    ///   Zeilendaten: Paare {u8 (Anzahl-1), u8 Wert}. Wert 255 = kein Gras, sonst Typ = Wert*16 (+ Varianten).
    ///   Eine Zelle = 768 m / 1024 = 0,75 m. Zeile = Z-Richtung, Spalte = X-Richtung.
    /// </summary>
    public sealed class GrassMap
    {
        public int Width, Height;
        public byte[] Cells;   // [z * Width + x]

        public static GrassMap Parse(byte[] d)
        {
            if (d == null || d.Length < 0x30 || !Bin.TagIs(d, 0x10, "DATA")) return null;
            const int baseOff = 0x20;
            int w = Bin.U16(d, baseOff), h = Bin.U16(d, baseOff + 2);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            var m = new GrassMap { Width = w, Height = h, Cells = new byte[w * h] };
            for (int r = 0; r < h; r++)
            {
                int p = baseOff + (int)Bin.U32(d, baseOff + 8 + r * 4);
                int x = 0;
                while (x < w && p + 1 < d.Length)
                {
                    int run = d[p] + 1;
                    byte v = d[p + 1];
                    p += 2;
                    int e = System.Math.Min(w, x + run);
                    for (int i = x; i < e; i++) m.Cells[r * w + i] = v;
                    x = e;
                }
            }
            return m;
        }

        public byte Sample(float localX, float localZ, float sectorSize = 768f)
        {
            int x = (int)(localX / sectorSize * Width), z = (int)(localZ / sectorSize * Height);
            if (x < 0 || z < 0 || x >= Width || z >= Height) return 255;
            return Cells[z * Width + x];
        }
    }

    public sealed class GrassLayerDef
    {
        public int BitmapIndex = -1;
        public int TypeVariation;
        public float VariationProbability;
        public float Height = 0.5f, Width = 1.5f, SizeDelta = 0.1f;
        public bool UseNoiseScale;
        public float ScalePower = 1f, ScaleClamp;
    }

    public sealed class GrassTypeDef
    {
        public int Type;
        public string Name;
        public GrassLayerDef Grass = new GrassLayerDef();
        public GrassLayerDef Bush = new GrassLayerDef();
    }

    /// <summary>fx/grasstypes.xml der Insel.</summary>
    public sealed class GrassTypes
    {
        public readonly Dictionary<int, GrassTypeDef> Types = new Dictionary<int, GrassTypeDef>();
        /// <summary>Typen in Dateireihenfolge – der Wert der Klassenkarte (.pgr) ist der Index hierin (Spezifikation 8.4).</summary>
        public readonly List<GrassTypeDef> Ordered = new List<GrassTypeDef>();

        public static GrassTypes Parse(string xml)
        {
            var gt = new GrassTypes();
            var doc = new XmlDocument();
            doc.LoadXml(xml.TrimStart('\uFEFF'));
            foreach (XmlNode n in doc.SelectNodes("//GrassType"))
            {
                var t = new GrassTypeDef
                {
                    Type = Int(n, "Type", 0),
                    Name = n.Attributes?["Name"]?.Value ?? "",
                };
                var g = n.SelectSingleNode("Grass");
                if (g != null) t.Grass = Layer(g);
                var b = n.SelectSingleNode("Bush");
                if (b != null) t.Bush = Layer(b);
                gt.Types[t.Type] = t;
                gt.Ordered.Add(t);
            }
            return gt;
        }

        static GrassLayerDef Layer(XmlNode n) => new GrassLayerDef
        {
            BitmapIndex = Int(n, "BitmapIndex", -1),
            TypeVariation = Int(n, "TypeVariation", 0),
            VariationProbability = Flt(n, "VariationProbability", 0),
            Height = Flt(n, "Height", 0.5f),
            Width = Flt(n, "Width", 1.5f),
            SizeDelta = Flt(n, "SizeDelta", 0.1f),
            UseNoiseScale = Int(n, "UseNoiseScale", 0) != 0,
            ScalePower = Flt(n, "ScalePower", 1f),
            ScaleClamp = Flt(n, "ScaleClamp", 0f),
        };

        static int Int(XmlNode n, string a, int def) =>
            n.Attributes?[a] != null && int.TryParse(n.Attributes[a].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        static float Flt(XmlNode n, string a, float def) =>
            n.Attributes?[a] != null && float.TryParse(n.Attributes[a].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        /// <summary>
        /// Karte-Wert -> Grastyp. Wert*16 ist die Typ-Familie; die Low-Bits (4/8/12) sind
        /// Varianten (normal / OffRoad-High/Medium). Bevorzugt den exakten Typ, sonst +4, sonst das erste Mitglied.
        /// </summary>
        public GrassTypeDef Resolve(byte mapValue)
        {
            if (mapValue == 255) return null;
            // Klassenwert = Index des Typs in der Datei (Hawaii: Type 0, 92, 110 … – keine 16er-Raster)
            if (mapValue < Ordered.Count) return Ordered[mapValue];
            int baseType = mapValue * 16;
            if (Types.TryGetValue(baseType, out var t) && !IsOffRoad(t)) return t;
            if (Types.TryGetValue(baseType + 4, out t)) return t;
            GrassTypeDef best = null;
            for (int i = 0; i < 16; i++)
                if (Types.TryGetValue(baseType + i, out t) && (best == null || !IsOffRoad(t))) { best = t; if (!IsOffRoad(t)) break; }
            return best;
        }

        static bool IsOffRoad(GrassTypeDef t) => t.Name != null && t.Name.Contains("OffRoad");
    }
}
