using System.Collections.Generic;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    public struct PrimInfo
    {
        public int IndexBufferOffset;   // absoluter Offset des DXIB-Chunks
        public int VertexBufferOffset;  // absoluter Offset des DXVB-Chunks
        public int MaterialIndex;       // Index in die MBNK-Materialliste der Instanz
        public int PrimitiveType;       // 4 = Triangle-Strip (immer beobachtet)
    }

    /// <summary>Dekodierter Vertexbuffer.</summary>
    public sealed class VertexData
    {
        public int Count;
        public Vector3[] Positions;
        public float[] Heights;         // nur Terrain (Format 0x0F: 1 float pro Vertex)
        public Vector3[] Normals;
        public Color32[] Colors;
        public Vector2[][] UVs = System.Array.Empty<Vector2[]>();
        public Vector4[] Tangents;
        public bool IsSprite;            // Billboard-Sprites (Flag 0x800)
        public Vector2[] SpriteOffsets;  // Eckoffset (Meter, View-Space) pro Vertex
        public Vector4[] BoneIdx, BoneW;  // Skinning (Leitplanken): bis 4 Knochen-Indizes und Gewichte
    }

    /// <summary>Plattformunabhängige Mesh-Daten (im Hintergrund erzeugbar, Mesh wird im Main-Thread gebaut).</summary>
    public sealed class MeshData
    {
        public string Name;
        public List<Vector3> Positions = new List<Vector3>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<Color32> Colors = new List<Color32>();
        public List<Vector2> UV0 = new List<Vector2>();
        public List<Vector2> UV1 = new List<Vector2>();   // 2. UV-Satz (Detail/Lightmap) bzw. Sprite-Offsets
        public List<Vector2> UV2 = new List<Vector2>();   // letzter UV-Satz (Straßen-Detail)
        public List<Vector2> UV3 = new List<Vector2>();   // 3. UV-Satz bei 4 Sätzen (Straßen: MARKS2 = Pfeile/Startraster)
        public bool HasUV3;
        /// <summary>Skinning-Daten (Mesh-UV-Kanal 4 = Knochen-Indizes, 5 = Gewichte) für das Biegen der Leitplanken.</summary>
        public List<Vector4> BoneIdx = new List<Vector4>(), BoneW = new List<Vector4>();
        public bool HasBones;
        public List<List<int>> SubMeshes = new List<List<int>>();
        public List<int> SubMeshMaterialIndex = new List<int>();
        public List<bool> SubMeshIsSprite = new List<bool>();
        public List<int> SubMeshUvSets = new List<int>();   // Anzahl UV-Sätze des Vertexbuffers pro Submesh
        public bool HasColors, HasUV1, HasUV2;
        public float BoundsPadding;   // Sprites: Bounds um die Eckoffsets vergrößern

        public Mesh ToMesh()
        {
            var m = new Mesh { name = Name ?? "TDU2Mesh" };
            if (Positions.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(Positions);
            if (Normals.Count == Positions.Count) m.SetNormals(Normals);
            if (Colors.Count == Positions.Count) m.SetColors(Colors); // fehlende Farben = 128 (neutral bei x2)
            if (UV0.Count == Positions.Count) m.SetUVs(0, UV0);
            // UV1/UV2 immer setzen (bei fehlendem UV-Satz mit UV0 gefüllt) – sonst samplen Detail/Shadow-Layer
            // im Shader einen konstanten Texel bei (0,0) und das Mesh erscheint einfarbig/weiß.
            if (UV1.Count == Positions.Count) m.SetUVs(1, UV1);
            if (UV2.Count == Positions.Count) m.SetUVs(2, UV2);
            if (HasUV3 && UV3.Count == Positions.Count) m.SetUVs(3, UV3);
            if (HasBones && BoneIdx.Count == Positions.Count && BoneW.Count == Positions.Count) { m.SetUVs(4, BoneIdx); m.SetUVs(5, BoneW); }
            m.subMeshCount = SubMeshes.Count;
            for (int i = 0; i < SubMeshes.Count; i++) m.SetTriangles(SubMeshes[i], i, false);
            m.RecalculateBounds();
            if (BoundsPadding > 0f) { var b = m.bounds; b.Expand(BoundsPadding * 2f); m.bounds = b; }
            if (Normals.Count != Positions.Count) m.RecalculateNormals();
            return m;
        }
    }

    /// <summary>
    /// .3dg = Geometrie.
    ///   GEOA: HASH (key -> GEOM-Offset) + GEOM-Chunks; GEOM enthält PRIM-Chunks (und ggf. BONE).
    ///   PRIM payload: +0 u32 DXIB-Offset, +4 u32 DXVB-Offset, +16 u16 Materialindex, +18 u16 Primitivtyp (4=Strip)
    ///   VBA. : DXVB-Chunks. payload: +0 u32 Vertexanzahl, +4 16 Byte Deklaration, Daten ab Chunk+48:
    ///          Stream 0: Positionen (float3, bzw. float1 = Höhe beim Terrain), danach Stream 1 interleaved:
    ///          [normal half4][color D3DCOLOR][color2][uv float2 * n][tangent half4][binormal half4][extra 12B]
    ///   IBA. : DXIB-Chunks. payload: u32 Indexanzahl, u32 Format (1 = u16), Indizes ab Chunk+32 (Triangle-Strip).
    /// </summary>
    public sealed class Geometry3DG
    {
        public byte[] Data;
        public Dictionary<ulong, int> GeoHash = new Dictionary<ulong, int>();
        public readonly List<int> GeomOffsets = new List<int>();
        public int FirstVertexBuffer = -1;

        public static Geometry3DG Parse(byte[] d)
        {
            var g = new Geometry3DG { Data = d };
            foreach (var c in EdenChunks.TopLevel(d))
            {
                if (c.Tag == "GEOA")
                {
                    var list = new List<Chunk>();
                    g.GeoHash = EdenChunks.ReadArray(d, c, "GEOM", list);
                    foreach (var x in list) g.GeomOffsets.Add(x.Offset);
                }
                else if (c.Tag == "VBA.")
                {
                    var vbs = EdenChunks.Children(d, c);
                    if (vbs.Count > 0) g.FirstVertexBuffer = vbs[0].Offset;
                }
            }
            return g;
        }

        public List<PrimInfo> GetPrims(int geomOffset)
        {
            var d = Data;
            var r = new List<PrimInfo>();
            if (geomOffset <= 0 || geomOffset + 16 > d.Length || !Bin.TagIs(d, geomOffset, "GEOM")) return r;
            int size = (int)Bin.U32(d, geomOffset + 8), hs = (int)Bin.U32(d, geomOffset + 12);
            foreach (var c in EdenChunks.Read(d, geomOffset + hs, geomOffset + size))
            {
                if (c.Tag != "PRIM") continue;
                int p = c.DataOffset;
                r.Add(new PrimInfo
                {
                    IndexBufferOffset = (int)Bin.U32(d, p),
                    VertexBufferOffset = (int)Bin.U32(d, p + 4),
                    MaterialIndex = Bin.U16(d, p + 16),
                    PrimitiveType = Bin.U16(d, p + 18),
                });
            }
            return r;
        }

        /// <summary>
        /// Dekodiert einen DXVB-Chunk. Das Stream-Layout hängt vom Flag-Wort bei Chunk+44 ab:
        ///   0x0140: Stream 0 = Position; Stream 1 = [normal][color][color2][uv*n][tangent][binormal][extra]
        ///   0x1040: Stream 0 = Position + UVs;  Stream 1 = restliche Attribute
        ///   0x0840: Stream 0 = Position + "Normal" (= Sprite-Eckoffset xy) + UVs; Stream 1 = Rest (Farbe)
        ///           -> Billboard-Sprites (Blätter): 4 Vertices pro Sprite mit gleichem Mittelpunkt
        ///   0x0240: (Skinning, nicht unterstützt)
        /// </summary>
        public VertexData ReadVertexBuffer(int vbOffset)
        {
            var d = Data;
            if (vbOffset <= 0 || vbOffset + 48 > d.Length || !Bin.TagIs(d, vbOffset, "DXVB")) return null;
            int chunkSize = (int)Bin.U32(d, vbOffset + 8);
            int n = (int)Bin.U32(d, vbOffset + 16);
            int dec = vbOffset + 20;           // 16 Byte Deklaration
            uint flags = Bin.U32(d, vbOffset + 44);
            // 0x0240 / 0x2040: skinned – Knochen-Indizes (Slot 7, u8[4]) und Gewichte (Slot 8, N-1 x f32) liegen im Float-Stream
            // hinter Position (und UVs). Z.B. die Leitplanken SF_GLISS (TD_Ground, 4-Bone-Skinning mit den 7 Matrizen der -S.pmi).
            bool skinned = (flags & 0x200) != 0;
            int boneBytes = 0;
            if (skinned)
            {
                if (d[dec + 14] != 0) boneBytes += 4;
                if (d[dec + 16] != 0) boneBytes += 4 * Mathf.Max(0, (d[dec + 17] & 0x0F) - 1);
            }
            byte posPresent = d[dec + 0], posFmt = d[dec + 1];
            bool hasNormal = d[dec + 2] != 0;
            bool hasColor = d[dec + 4] != 0;
            bool hasColor2 = d[dec + 6] != 0;
            int uvCount = d[dec + 8];
            bool hasTangent = d[dec + 10] != 0;
            bool hasBinormal = d[dec + 12] != 0;
            bool hasExtra = d[dec + 14] != 0;
            bool sprite = (flags & 0x800) != 0;
            if ((flags & 0xFFFF) == 0 && posPresent != 0 && posFmt != 0x0F)
                return ReadPlanar(d, vbOffset, n, hasNormal, hasColor, hasColor2, uvCount, hasTangent);
            bool uvInStream0 = sprite || (flags & 0x1000) != 0 || (flags & 0x2000) != 0;
            bool normalInStream0 = sprite;

            int posSize = posPresent == 0 ? 0 : (posFmt == 0x0F ? 4 : 12);
            // Offsets innerhalb der Streams berechnen
            int s0 = posSize, s1 = 0;
            int oNormal = -1, oColor = -1, oTangent = -1;
            bool normalS0 = false, uvS0 = uvInStream0;
            if (hasNormal) { if (normalInStream0) { oNormal = s0; s0 += 8; normalS0 = true; } else { oNormal = s1; s1 += 8; } }
            int oUv;
            if (uvInStream0) { oUv = s0; s0 += uvCount * 8; }
            else
            {
                if (hasColor) { oColor = s1; s1 += 4; }
                if (hasColor2) s1 += 4;
                oUv = s1; s1 += uvCount * 8;
            }
            int oBone = s0;
            s0 += boneBytes;   // Knochendaten: immer im Float-Stream, nach Position/UVs
            if (uvInStream0)
            {
                if (hasColor) { oColor = s1; s1 += 4; }
                if (hasColor2) s1 += 4;
            }
            if (hasTangent) { oTangent = s1; s1 += 8; }
            if (hasBinormal) s1 += 8;
            if (hasExtra && !skinned) s1 += 12;   // bei Skinning ist Slot 7 = Knochen-Indizes (Float-Stream)

            int base0 = vbOffset + 48;
            int base1 = base0 + s0 * n;
            int end = vbOffset + chunkSize;
            if (base1 + s1 * n > end || n <= 0) return null;

            var v = new VertexData { Count = n, IsSprite = sprite };
            if (posPresent != 0)
            {
                if (posFmt == 0x0F)
                {
                    v.Heights = new float[n];
                    for (int i = 0; i < n; i++) v.Heights[i] = Bin.F32(d, base0 + i * s0);
                }
                else
                {
                    v.Positions = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        int q = base0 + i * s0;
                        v.Positions[i] = new Vector3(Bin.F32(d, q), Bin.F32(d, q + 4), Bin.F32(d, q + 8));
                    }
                }
            }
            if (hasNormal)
            {
                int b = normalS0 ? base0 : base1, st = normalS0 ? s0 : s1;
                if (sprite)
                {
                    v.SpriteOffsets = new Vector2[n];
                    for (int i = 0; i < n; i++) { int q = b + i * st + oNormal; v.SpriteOffsets[i] = new Vector2(Bin.HalfAt(d, q), Bin.HalfAt(d, q + 2)); }
                }
                else
                {
                    v.Normals = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        int q = b + i * st + oNormal;
                        var nv = new Vector3(Bin.HalfAt(d, q), Bin.HalfAt(d, q + 2), Bin.HalfAt(d, q + 4));
                        float len = nv.magnitude;
                        v.Normals[i] = len > 1e-6f && !float.IsNaN(len) ? nv / len : Vector3.up;
                    }
                }
            }
            if (hasColor)
            {
                v.Colors = new Color32[n];
                for (int i = 0; i < n; i++) { int q = base1 + i * s1 + oColor; v.Colors[i] = new Color32(d[q + 2], d[q + 1], d[q], d[q + 3]); }
            }
            if (uvCount > 0)
            {
                int b = uvS0 ? base0 : base1, st = uvS0 ? s0 : s1;
                v.UVs = new Vector2[uvCount][];
                for (int u = 0; u < uvCount; u++)
                {
                    v.UVs[u] = new Vector2[n];
                    for (int i = 0; i < n; i++) { int q = b + i * st + oUv + u * 8; v.UVs[u][i] = new Vector2(Bin.F32(d, q), Bin.F32(d, q + 4)); }
                }
            }
            if (skinned && d[dec + 14] != 0)
            {
                int nw = d[dec + 16] != 0 ? Mathf.Clamp((d[dec + 17] & 0x0F) - 1, 0, 3) : 0;
                v.BoneIdx = new Vector4[n]; v.BoneW = new Vector4[n];
                for (int i = 0; i < n; i++)
                {
                    int q = base0 + i * s0 + oBone;
                    v.BoneIdx[i] = new Vector4(d[q], d[q + 1], d[q + 2], d[q + 3]);
                    float w0 = nw > 0 ? Bin.F32(d, q + 4) : 1f, w1 = nw > 1 ? Bin.F32(d, q + 8) : 0f, w2 = nw > 2 ? Bin.F32(d, q + 12) : 0f;
                    float w3 = Mathf.Max(0f, 1f - w0 - w1 - w2);
                    if (nw == 0) w3 = 0f;
                    else if (nw == 1) { w1 = w3; w3 = 0f; }
                    else if (nw == 2) { w2 = w3; w3 = 0f; }
                    v.BoneW[i] = new Vector4(w0, w1, w2, w3);
                }
            }
            if (hasTangent)
            {
                v.Tangents = new Vector4[n];
                for (int i = 0; i < n; i++) { int q = base1 + i * s1 + oTangent; v.Tangents[i] = new Vector4(Bin.HalfAt(d, q), Bin.HalfAt(d, q + 2), Bin.HalfAt(d, q + 4), 1f); }
            }
            return v;
        }

        /// <summary>
        /// Flagwort 0: jedes Attribut liegt als eigener, zusammenhängender Block hinter dem vorigen
        /// (Position[n], Normal[n] half4, Farbe[n], Farbe2[n], UV0[n], UV1[n] ..., Tangente[n], Binormale[n]) – z.B. Reifen (t_*p_oh.bnk).
        /// </summary>
        VertexData ReadPlanar(byte[] d, int vbOffset, int n, bool hasNormal, bool hasColor, bool hasColor2, int uvCount, bool hasTangent)
        {
            int end = vbOffset + (int)Bin.U32(d, vbOffset + 8);
            int need = n * (12 + (hasNormal ? 8 : 0) + (hasColor ? 4 : 0) + (hasColor2 ? 4 : 0) + uvCount * 8);
            int p = vbOffset + 48;
            if (n <= 0 || p + need > end) return null;
            var v = new VertexData { Count = n };
            v.Positions = new Vector3[n];
            for (int i = 0; i < n; i++, p += 12) v.Positions[i] = new Vector3(Bin.F32(d, p), Bin.F32(d, p + 4), Bin.F32(d, p + 8));
            if (hasNormal)
            {
                v.Normals = new Vector3[n];
                for (int i = 0; i < n; i++, p += 8)
                {
                    var nv = new Vector3(Bin.HalfAt(d, p), Bin.HalfAt(d, p + 2), Bin.HalfAt(d, p + 4));
                    float len = nv.magnitude;
                    v.Normals[i] = len > 1e-6f && !float.IsNaN(len) ? nv / len : Vector3.up;
                }
            }
            if (hasColor)
            {
                v.Colors = new Color32[n];
                for (int i = 0; i < n; i++, p += 4) v.Colors[i] = new Color32(d[p + 2], d[p + 1], d[p], d[p + 3]);
            }
            if (hasColor2) p += 4 * n;
            v.UVs = new Vector2[uvCount][];
            for (int u = 0; u < uvCount; u++)
            {
                v.UVs[u] = new Vector2[n];
                for (int i = 0; i < n; i++, p += 8) v.UVs[u][i] = new Vector2(Bin.F32(d, p), Bin.F32(d, p + 4));
            }
            if (hasTangent && p + 8 * n <= end)
            {
                v.Tangents = new Vector4[n];
                for (int i = 0; i < n; i++, p += 8) v.Tangents[i] = new Vector4(Bin.HalfAt(d, p), Bin.HalfAt(d, p + 2), Bin.HalfAt(d, p + 4), 1f);
            }
            return v;
        }

        public ushort[] ReadIndexBuffer(int ibOffset)
        {
            var d = Data;
            if (ibOffset <= 0 || ibOffset + 32 > d.Length || !Bin.TagIs(d, ibOffset, "DXIB")) return System.Array.Empty<ushort>();
            int n = (int)Bin.U32(d, ibOffset + 16);
            int size = (int)Bin.U32(d, ibOffset + 8);
            n = Mathf.Min(n, (size - 32) / 2);
            var r = new ushort[n];
            for (int i = 0; i < n; i++) r[i] = Bin.U16(d, ibOffset + 32 + i * 2);
            return r;
        }

        /// <summary>Triangle-Strip -> Dreiecksliste (degenerierte Dreiecke werden verworfen, Wicklung wie D3D).</summary>
        public static void StripToList(ushort[] strip, int baseVertex, List<int> outTris)
        {
            for (int i = 0; i + 2 < strip.Length; i++)
            {
                int a = strip[i], b = strip[i + 1], c = strip[i + 2];
                if (a == b || b == c || a == c) continue;
                if ((i & 1) == 0) { outTris.Add(baseVertex + a); outTris.Add(baseVertex + b); outTris.Add(baseVertex + c); }
                else { outTris.Add(baseVertex + b); outTris.Add(baseVertex + a); outTris.Add(baseVertex + c); }
            }
        }

        public static void ListToList(ushort[] list, int baseVertex, List<int> outTris)
        {
            for (int i = 0; i + 2 < list.Length; i += 3)
            {
                outTris.Add(baseVertex + list[i]); outTris.Add(baseVertex + list[i + 1]); outTris.Add(baseVertex + list[i + 2]);
            }
        }

        /// <summary>
        /// Baut MeshData für eine Geometrie (alle PRIMs als Submeshes). Gibt null zurück, wenn der Key fehlt.
        /// </summary>
        public MeshData BuildMesh(ulong geoKey, bool skipEmpty = true)
        {
            if (!GeoHash.TryGetValue(geoKey, out int geom)) return null;
            var prims = GetPrims(geom);
            var md = new MeshData { Name = Bin.KeyToString(geoKey) };
            var vbBase = new Dictionary<int, int>();
            var vbSprite = new Dictionary<int, bool>();
            var vbUvSets = new Dictionary<int, int>();
            foreach (var pr in prims)
            {
                bool isSprite = false;
                if (!vbBase.TryGetValue(pr.VertexBufferOffset, out int baseV))
                {
                    var vb = ReadVertexBuffer(pr.VertexBufferOffset);
                    if (vb == null || vb.Positions == null) continue;
                    baseV = md.Positions.Count;
                    vbBase[pr.VertexBufferOffset] = baseV;
                    vbSprite[pr.VertexBufferOffset] = vb.IsSprite;
                    vbUvSets[pr.VertexBufferOffset] = vb.UVs.Length;
                    md.Positions.AddRange(vb.Positions);
                    if (vb.Normals != null) md.Normals.AddRange(vb.Normals);
                    else for (int i = 0; i < vb.Count; i++) md.Normals.Add(Vector3.up);
                    if (vb.Colors != null) { md.Colors.AddRange(vb.Colors); md.HasColors = true; }
                    else for (int i = 0; i < vb.Count; i++) md.Colors.Add(new Color32(128, 128, 128, 255));
                    if (vb.UVs.Length > 0) md.UV0.AddRange(vb.UVs[0]);
                    else for (int i = 0; i < vb.Count; i++) md.UV0.Add(Vector2.zero);
                    if (vb.IsSprite && vb.SpriteOffsets != null) { md.UV1.AddRange(vb.SpriteOffsets); md.HasUV1 = true; }
                    else if (vb.UVs.Length > 1) { md.UV1.AddRange(vb.UVs[1]); md.HasUV1 = true; }
                    else if (vb.UVs.Length > 0) md.UV1.AddRange(vb.UVs[0]);
                    else for (int i = 0; i < vb.Count; i++) md.UV1.Add(Vector2.zero);
                    if (vb.UVs.Length > 2) { md.UV2.AddRange(vb.UVs[vb.UVs.Length - 1]); md.HasUV2 = true; }
                    else if (vb.UVs.Length > 0) md.UV2.AddRange(vb.UVs[vb.UVs.Length - 1]);
                    else for (int i = 0; i < vb.Count; i++) md.UV2.Add(Vector2.zero);
                    if (vb.UVs.Length == 2) md.HasUV2 = true;
                    if (vb.UVs.Length > 3) { md.UV3.AddRange(vb.UVs[2]); md.HasUV3 = true; }
                    else for (int i = 0; i < vb.Count; i++) md.UV3.Add(Vector2.zero);
                    if (vb.BoneIdx != null) { md.BoneIdx.AddRange(vb.BoneIdx); md.BoneW.AddRange(vb.BoneW); md.HasBones = true; }
                    else for (int i = 0; i < vb.Count; i++) { md.BoneIdx.Add(Vector4.zero); md.BoneW.Add(new Vector4(1f, 0f, 0f, 0f)); }
                }
                vbSprite.TryGetValue(pr.VertexBufferOffset, out isSprite);
                var ib = ReadIndexBuffer(pr.IndexBufferOffset);
                var tris = new List<int>(ib.Length * 3);
                int pt = pr.PrimitiveType & 0xFF;
                if (pt == 4 || pt == 5) StripToList(ib, baseV, tris);
                else ListToList(ib, baseV, tris);
                int vcount = md.Positions.Count;
                for (int t = tris.Count - 3; t >= 0; t -= 3)
                    if (tris[t] >= vcount || tris[t + 1] >= vcount || tris[t + 2] >= vcount) tris.RemoveRange(t, 3);
                if (skipEmpty && tris.Count == 0) continue;
                if (isSprite)
                    for (int i = baseV; i < vcount && i < md.UV1.Count; i++) md.BoundsPadding = Mathf.Max(md.BoundsPadding, md.UV1[i].magnitude);
                md.SubMeshes.Add(tris);
                md.SubMeshMaterialIndex.Add(pr.MaterialIndex);
                md.SubMeshIsSprite.Add(isSprite);
                vbUvSets.TryGetValue(pr.VertexBufferOffset, out int uvSets);
                md.SubMeshUvSets.Add(uvSets);
            }
            if (md.SubMeshes.Count == 0) return null;
            return md;
        }
    }
}
