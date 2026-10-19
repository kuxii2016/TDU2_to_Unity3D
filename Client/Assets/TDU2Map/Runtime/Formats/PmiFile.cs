using System.Collections.Generic;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    public struct PmiInstance
    {
        public Vector3 Position;   // sektor-lokal (m)
        public Quaternion Rotation;
        public Vector3 Scale;
        public ushort Extra;       // Bits 0-8 Culling-Zelle, 9-15 Klasse
        public Color Tint;         // (alt, ungenutzt)
        /// <summary>Leitplanken (Art 3): erste Zusatzmatrix (Spaltenform), wird VOR der Platzierung angewendet (legt die längs -Z
        /// modellierte 20-m-Planke an den Straßenrand). null bei allen anderen Arten.</summary>
        public Matrix4x4? Pre;
        /// <summary>Leitplanken (Art 3): alle Knochenmatrizen (Spaltenform, Index = Knochen-Index im Mesh). Die Planke wird
        /// damit an die Straße angepasst – ohne sie entstehen an jedem Pfosten kleine Stufen.</summary>
        public Matrix4x4[] Bones;
    }

    public sealed class PmiGroup
    {
        public ulong Key;          // INS-Key in der Library (commonworld.bnk), z.B. "V_CHEN_C"
        public ushort Unknown;     // Sichtweite (~60 x Modellradius)
        public ushort Version;     // Art: 1 normal (O, V), 2 World-Actor (A), 3 Leitplanke (S), 4 Schild mit Text, 5 Effekt (F)
        public ushort ExtraBytes;  // Zusatzbytes je Instanz (Art 3: 7-8 Matrizen, Art 4: Schildtext)
        public List<PmiInstance> Instances = new List<PmiInstance>();
        public string Name => Bin.KeyToString(Key);
    }

    /// <summary>
    /// .pmi = "Placed Mesh Instances" pro Sektor:
    ///   Sector-...-V.pmi : Vegetation (Bäume: V_CHEN = Eiche, V_HPIN = Pinie, V_OLIV, V_FIG, V_CACT ...)
    ///   Sector-...-O.pmi : weitere Objekte (Büsche, Felsen ...)
    ///   Sector-...-A.pmi : Actors (Mülltonnen, Schilder ...) – Keys verweisen auf scenarics/packs/worldactors.bnk
    ///   Sector-...-F/S.pmi : (meist leer)
    ///
    ///   MOBA-Chunk @0x10, payload: u32 Gruppen, float hMin, float hMax, ... Dichteraster; Kinder ab MOBA+hdr:
    ///   MOB.-Chunk payload: u64 Key, u32 Anzahl, u16 ?, u16 Version, danach je Instanz 32 Byte:
    ///     +0 u16 x, +2 u16 y, +4 u16 z   (x,z: v/65535*1024-128 ; y: hMin + v/65535*(hMax-hMin))
    ///     +6 u16 sx, sy, sz             (Skalierung, 0x4000 = 1.0)
    ///     +12 s16 rotX, +14 u16 rotY, +16 s16 rotZ (65536 = 360°)
    ///     +18 u16 ?, +20 half3 Farbe1, +26 half3 Farbe2
    /// </summary>
    public sealed class PmiFile
    {
        public float HeightMin, HeightMax;
        public readonly List<PmiGroup> Groups = new List<PmiGroup>();
        public int SkippedGroups;

        public const float RangeMin = -128f, RangeSize = 1024f;

        public static PmiFile Parse(byte[] d)
        {
            var f = new PmiFile();
            if (d == null || d.Length < 0x30 || !Bin.TagIs(d, 0x10, "MOBA")) return f;
            int mobaSize = (int)Bin.U32(d, 0x18), mobaHdr = (int)Bin.U32(d, 0x1C);
            f.HeightMin = Bin.F32(d, 0x24);
            f.HeightMax = Bin.F32(d, 0x28);
            float hRange = f.HeightMax - f.HeightMin;
            foreach (var c in EdenChunks.Read(d, 0x10 + mobaHdr, 0x10 + mobaSize))
            {
                if (c.Tag != "MOB.") continue;
                int p = c.DataOffset;
                // MOB. (Spezifikation 7.4): Key8, u16 Anzahl, u16 Zusatzbytes, u16 Sichtweite, u16 Art; dann Einträge zu
                // 32 + (Zusatzbytes auf 16 aufgerundet) Byte. Art 3 = Leitplanken (Matrizen), Art 4 = Schilder (Text, Tafel leer).
                var g = new PmiGroup
                {
                    Key = Bin.U64(d, p),
                    Unknown = Bin.U16(d, p + 12),
                    Version = Bin.U16(d, p + 14),
                    ExtraBytes = Bin.U16(d, p + 10),
                };
                if (g.Version < 1 || g.Version > 5) { f.SkippedGroups++; continue; }
                int cnt = Bin.U16(d, p + 8);
                int stride = 32 + ((g.ExtraBytes + 15) / 16) * 16;
                int ip = c.Offset + 32;
                for (int i = 0; i < cnt; i++, ip += stride)
                {
                    if (ip + stride > c.End) break;
                    float x = Bin.U16(d, ip) / 65535f * RangeSize + RangeMin;
                    float y = f.HeightMin + Bin.U16(d, ip + 2) / 65535f * hRange;
                    float z = Bin.U16(d, ip + 4) / 65535f * RangeSize + RangeMin;
                    var s = new Vector3(Bin.U16(d, ip + 6), Bin.U16(d, ip + 8), Bin.U16(d, ip + 10)) / 16384f;
                    if (s.x <= 0.001f || s.y <= 0.001f || s.z <= 0.001f) s = Vector3.one;
                    const float toDeg = 360f / 65536f;
                    // Spezifikation: p' = p · S · Rx(Nicken) · Rz(Rollen) · Ry(Richtung) + Position (Zeilenvektoren)
                    //  -> Unity (Spaltenvektoren): Ry * Rz * Rx; die Achsrichtungen entsprechen Quaternion.AngleAxis.
                    float pitch = Bin.I16(d, ip + 12) * toDeg, head = Bin.U16(d, ip + 14) * toDeg, roll = Bin.I16(d, ip + 16) * toDeg;
                    var rot = Quaternion.AngleAxis(head, Vector3.up) * Quaternion.AngleAxis(roll, Vector3.forward) * Quaternion.AngleAxis(pitch, Vector3.right);
                    Matrix4x4? pre = null;
                    Matrix4x4[] bones = null;
                    if (g.Version == 3 && g.ExtraBytes >= 64)
                    {
                        // Zusatzmatrizen (Zeilenvektor-Form, Verschiebung in der 4. Zeile) -> Spaltenform; die erste = Pre
                        int nb = Mathf.Min(g.ExtraBytes / 64, 16);
                        bones = new Matrix4x4[nb];
                        for (int b = 0; b < nb; b++)
                        {
                            int mp = ip + 32 + b * 64;
                            var m = new Matrix4x4();
                            for (int r = 0; r < 4; r++)
                                for (int col = 0; col < 4; col++)
                                    m[col, r] = Bin.F32(d, mp + (r * 4 + col) * 4);
                            bones[b] = m;
                        }
                        pre = bones[0];
                    }
                    g.Instances.Add(new PmiInstance
                    {
                        Position = new Vector3(x, y, z),
                        Rotation = rot,
                        Scale = s,
                        Extra = Bin.U16(d, ip + 18),
                        Pre = pre,
                        Bones = bones,
                    });
                }
                f.Groups.Add(g);
            }
            return f;
        }
    }
}
