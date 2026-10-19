using System.Collections.Generic;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    /// <summary>Verweis einer Instanz auf ein OBJ (eine LOD-Stufe).</summary>
    public struct LodRef
    {
        public ulong ObjKey;      // Key des OBJ = Key der Geometrie im .3dg (GEOA-Hash)
        public int ObjOffset;     // Offset des OBJ.-Chunks
        public float RawDistance; // Wert aus der Datei (vermutlich quadratische Distanz)
        public int PrimCount;
        public Vector4 Sphere;    // Bounding-Sphere (lokal)
    }

    public sealed class InsRecord
    {
        public int Offset;
        public ulong Key;          // nur gesetzt, wenn über den INSA-Hash gefunden
        public Matrix4x4 Matrix;   // bereits nach Unity-Konvention (Spaltenvektoren) transponiert
        public int MaterialBankOffset;
        public LodRef[] Lods;
    }

    public sealed class HieRecord
    {
        public int Offset;
        public int InsOffset;
        public int ChildCount;
        public int ParentOffset;   // 0 = Wurzel
    }

    public struct HeightmapInfo
    {
        public bool Present;
        public float HalfSize;     // 384 (Sektor) / 3072 (Area) / 49152 (Insel)
        public float Radius;
        public int Tiles;          // 16
        public float Spacing;      // 3 / 24 / 384 m
    }

    /// <summary>
    /// .3dd = Szenenbeschreibung.
    ///   MBNA: Liste von MBNK (Material-Banken: je 16 Byte {u64 materialKey, 8 Byte pad})
    ///   OBJA: HASH + OBJ. (payload: 3x u32 0, u32 primCount, float4 sphere) – oder HMAP beim Terrain
    ///   INSA: HASH + INS. (payload 160 Byte):
    ///         +0  float4x4 Matrix (D3D, Zeilenvektoren, Translation in Zeile 3)
    ///         +68 u32 Offset des MBNK (Materialliste für diese Instanz)
    ///         +72 i32 (-1), +76 u16 0xCDCD, +78 u16 Anzahl LODs
    ///         +80 LOD-Einträge à 20 Byte {u32 Zeiger auf OBJA-Hash-Eintrag, float Distanz, 12 Byte 0}
    ///   HIEA: HASH + HIE. (payload 16 Byte: u32 0, u32 INS-Offset, u16 Kinder, u16 'AP', u32 Eltern-HIE-Offset)
    /// </summary>
    public sealed class Scene3DD
    {
        public byte[] Data;
        public Dictionary<ulong, int> ObjHash = new Dictionary<ulong, int>();
        public Dictionary<ulong, int> InsHash = new Dictionary<ulong, int>();
        public Dictionary<ulong, int> HieHash = new Dictionary<ulong, int>();
        public readonly List<int> ObjOffsets = new List<int>();
        public readonly List<int> InsOffsets = new List<int>();
        public readonly List<HieRecord> Hierarchy = new List<HieRecord>();
        public readonly List<int> MaterialBankOffsets = new List<int>();
        public HeightmapInfo Heightmap;

        readonly Dictionary<int, InsRecord> _insCache = new Dictionary<int, InsRecord>();

        public static Scene3DD Parse(byte[] d)
        {
            var s = new Scene3DD { Data = d };
            foreach (var c in EdenChunks.TopLevel(d))
            {
                switch (c.Tag)
                {
                    case "MBNA":
                        foreach (var m in EdenChunks.Children(d, c))
                            if (m.Tag == "MBNK") s.MaterialBankOffsets.Add(m.Offset);
                        break;
                    case "OBJA":
                        {
                            var list = new List<Chunk>();
                            s.ObjHash = EdenChunks.ReadArray(d, c, null, list);
                            foreach (var o in list)
                            {
                                if (o.Tag == "OBJ.") s.ObjOffsets.Add(o.Offset);
                                else if (o.Tag == "HMAP") s.Heightmap = ReadHmap(d, o);
                            }
                            break;
                        }
                    case "INSA":
                        {
                            var list = new List<Chunk>();
                            s.InsHash = EdenChunks.ReadArray(d, c, "INS.", list);
                            foreach (var o in list) s.InsOffsets.Add(o.Offset);
                            break;
                        }
                    case "HIEA":
                        {
                            var list = new List<Chunk>();
                            s.HieHash = EdenChunks.ReadArray(d, c, "HIE.", list);
                            foreach (var o in list)
                            {
                                int p = o.DataOffset;
                                s.Hierarchy.Add(new HieRecord
                                {
                                    Offset = o.Offset,
                                    InsOffset = (int)Bin.U32(d, p + 4),
                                    ChildCount = Bin.U16(d, p + 8),
                                    ParentOffset = (int)Bin.U32(d, p + 12),
                                });
                            }
                            break;
                        }
                }
            }
            // Keys der Instanzen zuordnen (für Library-Lookups und Namen)
            foreach (var kv in s.InsHash)
                s.GetIns(kv.Value).Key = kv.Key;
            return s;
        }

        static HeightmapInfo ReadHmap(byte[] d, Chunk c)
        {
            int p = c.DataOffset;
            return new HeightmapInfo
            {
                Present = true,
                HalfSize = Bin.F32(d, p + 16),
                Radius = Bin.F32(d, p + 28),
                Tiles = (int)Bin.U32(d, p + 32),
                Spacing = Bin.F32(d, p + 44),
            };
        }

        public InsRecord GetIns(int offset)
        {
            if (_insCache.TryGetValue(offset, out var r)) return r;
            var d = Data;
            int p = offset + 16;
            var m = new Matrix4x4();
            // D3D-Zeile i -> Unity-Spalte i (Transponieren)
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    m[col, row] = Bin.F32(d, p + (row * 4 + col) * 4);
            int nLod = Bin.U16(d, p + 78);
            if (nLod > 4) nLod = 4;
            var lods = new List<LodRef>();
            for (int i = 0; i < nLod; i++)
            {
                int le = p + 80 + i * 20;
                int ptr = (int)Bin.U32(d, le);
                float dist = Bin.F32(d, le + 4);
                if (ptr <= 0 || ptr + 16 > d.Length) continue;
                var lr = new LodRef
                {
                    ObjKey = Bin.U64(d, ptr),
                    ObjOffset = (int)Bin.U32(d, ptr + 8),
                    RawDistance = dist,
                };
                if (lr.ObjOffset > 0 && lr.ObjOffset + 48 <= d.Length && Bin.TagIs(d, lr.ObjOffset, "OBJ."))
                {
                    int q = lr.ObjOffset + 16;
                    lr.PrimCount = (int)Bin.U32(d, q + 12);
                    lr.Sphere = new Vector4(Bin.F32(d, q + 16), Bin.F32(d, q + 20), Bin.F32(d, q + 24), Bin.F32(d, q + 28));
                }
                lods.Add(lr);
            }
            r = new InsRecord
            {
                Offset = offset,
                Matrix = m,
                MaterialBankOffset = (int)Bin.U32(d, p + 68),
                Lods = lods.ToArray(),
            };
            _insCache[offset] = r;
            return r;
        }

        /// <summary>Materialliste (MBNK) einer Instanz. Index = PRIM.materialIndex.</summary>
        public ulong[] ReadMaterialBank(int mbnkOffset)
        {
            var d = Data;
            if (mbnkOffset <= 0 || mbnkOffset + 16 > d.Length || !Bin.TagIs(d, mbnkOffset, "MBNK"))
                return System.Array.Empty<ulong>();
            int size = (int)Bin.U32(d, mbnkOffset + 8);
            int n = (size - 16) / 16;
            var r = new ulong[n];
            for (int i = 0; i < n; i++) r[i] = Bin.U64(d, mbnkOffset + 16 + i * 16);
            return r;
        }

        /// <summary>Weltmatrix eines HIE-Knotens (Produkt über die Elternkette).</summary>
        public Matrix4x4 WorldMatrix(HieRecord h, Dictionary<int, HieRecord> byOffset)
        {
            var m = GetIns(h.InsOffset).Matrix;
            int guard = 0;
            var cur = h;
            while (cur.ParentOffset != 0 && byOffset.TryGetValue(cur.ParentOffset, out var parent) && guard++ < 64)
            {
                m = GetIns(parent.InsOffset).Matrix * m;
                cur = parent;
            }
            return m;
        }
    }
}
