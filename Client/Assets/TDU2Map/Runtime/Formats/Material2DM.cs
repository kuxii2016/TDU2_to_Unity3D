using System.Collections.Generic;
using TDU2.Map.IO;

namespace TDU2.Map.Formats
{
    public struct MaterialLayer
    {
        public ulong Semantic;   // 8 Byte, z.B. "COLOR", "SHADOW", "NORMAL", "DETAIL1" (teils komprimiert)
        public ulong TextureKey; // Key einer .2db (BMAP) Textur
        public byte AddressU;    // 1 = Wrap, 2 = Clamp, 3 = Mirror (beobachtet)
        public byte AddressV;
        public string SemanticName => Bin.KeyToString(Semantic);
    }

    public sealed class MaterialDef
    {
        public ulong Key;
        public string Name;
        public byte[] Flags = new byte[16];
        /// <summary>Shader-Set des Originals (MAT.+0x310), z.B. TD_Road / TD_Ground / TD_Building.</summary>
        public ulong ShaderSet;
        /// <summary>Diffusfarbe aus dem Farbblock (MAT.+0xC0 ambient/diffuse/specular/emissive). Interieur-Farb-
        /// materialien (z. B. "370Z_Int_Noir_01") tragen ihre Grundfarbe nur hier, nicht als RAMPCLR1-Parameter.</summary>
        public UnityEngine.Color Diffuse = UnityEngine.Color.white;
        public const ulong ShaderRoad = 0xc8ad93c95455291bUL, ShaderGround = 0xb0f1234b0f123473UL;
        public List<MaterialLayer> Layers = new List<MaterialLayer>();
        /// <summary>Shader-Parameter (Key = 8-Byte-Name, z.B. "RAMPCLR1" = Grundfarbe bei Autolack) -> float4.</summary>
        public readonly Dictionary<ulong, UnityEngine.Vector4> Params = new Dictionary<ulong, UnityEngine.Vector4>();

        public bool TryGetParam(string name, out UnityEngine.Vector4 v) => Params.TryGetValue(Bin.KeyFromString(name), out v);

        /// <summary>Material-Key = "gemangelter" Name in Großbuchstaben (Bytes zyklisch auf 8 Byte aufaddiert).</summary>
        public static ulong KeyFromName(string name)
        {
            var b = new byte[8];
            string u = name.ToUpperInvariant();
            for (int i = 0; i < u.Length; i++) b[i % 8] = (byte)(b[i % 8] + (byte)u[i]);
            return System.BitConverter.ToUInt64(b, 0);
        }

        public static readonly ulong SemColor = Bin.KeyFromString("COLOR");
        public static readonly ulong SemShadow = Bin.KeyFromString("SHADOW");
        public static readonly ulong SemNormal = Bin.KeyFromString("NORMAL");
        public static readonly ulong SemDetail = Bin.KeyFromString("DETAIL");
        public static readonly ulong SemDetail1 = Bin.KeyFromString("DETAIL1");
        public static readonly ulong SemDetail2 = Bin.KeyFromString("DETAIL2");

        public bool TryGetLayer(ulong semantic, out MaterialLayer layer)
        {
            foreach (var l in Layers)
                if (l.Semantic == semantic && l.TextureKey != 0) { layer = l; return true; }
            layer = default;
            return false;
        }

        public bool IsOcean => Name != null && Name.ToLowerInvariant().Contains("ocean");
        /// <summary>FX-Materialien (Lichtkegel, Halos) – volumetrische Effekte, die Unity nicht nachbildet.</summary>
        public bool IsEffect => Name != null && Name.StartsWith("FX_", System.StringComparison.OrdinalIgnoreCase);
        /// <summary>Straßen: Haupttextur ist die DETAIL-Ebene auf dem letzten UV-Satz, COLOR ist nur Variation.</summary>
        public bool IsRoad => ShaderSet == ShaderRoad || Name != null && (Name.StartsWith("Road", System.StringComparison.OrdinalIgnoreCase) || Name.StartsWith("crossroad", System.StringComparison.OrdinalIgnoreCase));
        /// <summary>TD_Ground (Plätze, Gehwege, Sockel): Farbe = COLOR x DETAIL.r, DETAIL auf UV-Satz 2 (sonst 1).</summary>
        public bool IsGround => ShaderSet == ShaderGround;
    }

    /// <summary>
    /// .2dm = Materialbibliothek.
    ///   MATA: HASH + MAT.-Chunks. MAT. (Offsets relativ zum Chunkanfang):
    ///     +0x10 u64 Key, +0x20 char[32] Name, +0x40 Flags,
    ///     +0xC0.. Farben (float4 ambient/diffuse/specular/emissive),
    ///     +0x10C u16 Layer-Anzahl, +0x110 Layer à 32 Byte {u64 Semantik, u64 Texturkey, 8 Byte 0, u8 addrU, u8 addrV, u8, u8, 4 Byte}
    ///     danach Shader-Hash und Shaderparameter (z.B. "AREASIZE", "WINDFACT" ...).
    /// </summary>
    public sealed class Material2DM
    {
        /// <summary>Material-/Knoten-Key aus einem Namen (siehe <see cref="MaterialDef.KeyFromName"/>).</summary>
        public static ulong KeyFromName(string name) => MaterialDef.KeyFromName(name);

        public readonly Dictionary<ulong, MaterialDef> Materials = new Dictionary<ulong, MaterialDef>();

        public static Material2DM Parse(byte[] d)
        {
            var lib = new Material2DM();
            foreach (var c in EdenChunks.TopLevel(d))
            {
                if (c.Tag != "MATA") continue;
                foreach (var m in EdenChunks.Children(d, c))
                {
                    if (m.Tag != "MAT.") continue;
                    var def = ParseMat(d, m);
                    if (def != null) lib.Materials[def.Key] = def;
                }
            }
            return lib;
        }

        static MaterialDef ParseMat(byte[] d, Chunk m)
        {
            int o = m.Offset;
            if (m.Size < 0x110) return null;
            var def = new MaterialDef
            {
                Key = Bin.U64(d, o + 0x10),
                Name = Bin.CString(d, o + 0x20, 32),
            };
            System.Array.Copy(d, o + 0x40, def.Flags, 0, 16);
            if (o + 0xD0 <= m.End)
                def.Diffuse = new UnityEngine.Color(Bin.F32(d, o + 0xC0), Bin.F32(d, o + 0xC4), Bin.F32(d, o + 0xC8), Bin.F32(d, o + 0xCC));
            if (o + 0x318 <= m.End) def.ShaderSet = Bin.U64(d, o + 0x310);
            int n = Bin.U16(d, o + 0x10C);
            if (n > 16) n = 16;
            for (int i = 0; i < n; i++)
            {
                int p = o + 0x110 + i * 32;
                if (p + 32 > m.End) break;
                var l = new MaterialLayer
                {
                    Semantic = Bin.U64(d, p),
                    TextureKey = Bin.U64(d, p + 8),
                    AddressU = d[p + 24],
                    AddressV = d[p + 25],
                };
                if (l.Semantic == 0 && l.TextureKey == 0) continue;
                def.Layers.Add(l);
            }
            ParseParams(d, m, def, o + 0x110 + n * 32);
            return def;
        }

        /// <summary>
        /// Parameterliste nach den Layern: Deskriptoren à 16 Byte {u64 Name, u8 2, u8 0, u8 Flag, u8 0, u32 16},
        /// direkt danach je Parameter ein float4 (gleiche Reihenfolge).
        /// </summary>
        static void ParseParams(byte[] d, Chunk m, MaterialDef def, int start)
        {
            int end = m.End;
            bool IsDesc(int q) => q + 16 <= end && d[q + 8] == 2 && d[q + 9] == 0 && Bin.U32(d, q + 12) == 16 && Bin.U64(d, q) != 0;
            int p = start;
            while (p + 16 <= end && !IsDesc(p)) p += 8;
            var names = new List<ulong>();
            while (IsDesc(p)) { names.Add(Bin.U64(d, p)); p += 16; }
            for (int i = 0; i < names.Count; i++)
            {
                int q = p + 16 * i;
                if (q + 16 > end) break;
                def.Params[names[i]] = new UnityEngine.Vector4(Bin.F32(d, q), Bin.F32(d, q + 4), Bin.F32(d, q + 8), Bin.F32(d, q + 12));
            }
        }
    }
}
