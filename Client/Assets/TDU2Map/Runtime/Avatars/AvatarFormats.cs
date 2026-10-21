using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Avatars
{
    /// <summary>
    /// .xsb – Skelett (Eden-Container ".XSB", Segment SKEL):
    ///   +32 u32 n, +36 i32 parent[n], u32 key4_sorted[n], u32 bone_of_sorted[n], pad 16,
    ///   lokale Ruhepose[n] à 48 Byte (f32[4] Quaternion, f32[4] Position, f32[4] Skalierung),
    ///   f32[16][n] inverse Bind-Matrizen (Zeilenvektoren, Translation in Zeile 3), Key8-Namen[n] in sortierter Reihenfolge.
    /// Quaternionen drehen Zeilenvektoren: für Unity (Spaltenvektoren) konjugiert (-x, -y, -z, w).
    /// Alle Werte im Spielraum (rechtshändig, +Z vorn, +X links) – der Avatar hängt unter einem gespiegelten Elternteil.
    /// </summary>
    public sealed class XsbSkeleton
    {
        public int Count;
        public int[] Parent;
        public uint[] Hash;           // Key4 je Knochen (Spuren der .anm finden ihren Knochen darüber)
        public ulong[] Key;           // Key8 (gefalteter Name) je Knochen
        public Quaternion[] Rot;
        public Vector3[] Pos, Scale;
        public Matrix4x4[] InvBind;   // für Spaltenvektoren (Unity-Bindpose)
        public readonly Dictionary<uint, int> ByHash = new Dictionary<uint, int>();
        public readonly Dictionary<ulong, int> ByKey = new Dictionary<ulong, int>();

        public int Find(string name) => ByKey.TryGetValue(MaterialDef.KeyFromName(name), out int i) ? i : -1;

        public static XsbSkeleton Parse(byte[] d)
        {
            if (d == null || d.Length < 0x40) return null;
            int n = (int)Bin.U32(d, 0x20);
            if (n <= 0 || n > 512) return null;
            var s = new XsbSkeleton
            {
                Count = n,
                Parent = new int[n],
                Hash = new uint[n],
                Key = new ulong[n],
                Rot = new Quaternion[n],
                Pos = new Vector3[n],
                Scale = new Vector3[n],
                InvBind = new Matrix4x4[n],
            };
            int p = 0x24;
            for (int i = 0; i < n; i++) s.Parent[i] = Bin.I32(d, p + 4 * i);
            int a = p + 4 * n;
            int b = a + 4 * n;
            int c = (b + 4 * n + 15) & ~15;
            int m = c + 48 * n;
            int k = m + 64 * n;
            if (k + 8 * n > d.Length) return null;
            for (int j = 0; j < n; j++)
            {
                int bone = (int)Bin.U32(d, b + 4 * j);
                if (bone < 0 || bone >= n) continue;
                s.Hash[bone] = Bin.U32(d, a + 4 * j);
                s.Key[bone] = Bin.U64(d, k + 8 * j);
            }
            for (int i = 0; i < n; i++)
            {
                int q = c + 48 * i;
                s.Rot[i] = Norm(new Quaternion(-Bin.F32(d, q), -Bin.F32(d, q + 4), -Bin.F32(d, q + 8), Bin.F32(d, q + 12)));
                s.Pos[i] = new Vector3(Bin.F32(d, q + 16), Bin.F32(d, q + 20), Bin.F32(d, q + 24));
                var sc = new Vector3(Bin.F32(d, q + 32), Bin.F32(d, q + 36), Bin.F32(d, q + 40));
                s.Scale[i] = sc.sqrMagnitude < 1e-8f ? Vector3.one : sc;
                var mm = new Matrix4x4();
                int r = m + 64 * i;
                // Datei: Zeilen-Major für Zeilenvektoren = Spalten-Major für Spaltenvektoren -> mat[row, col] = f[col * 4 + row]
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        mm[row, col] = Bin.F32(d, r + 4 * (col * 4 + row));
                s.InvBind[i] = mm;
            }
            for (int i = 0; i < n; i++)
            {
                if (!s.ByHash.ContainsKey(s.Hash[i])) s.ByHash[s.Hash[i]] = i;
                if (!s.ByKey.ContainsKey(s.Key[i])) s.ByKey[s.Key[i]] = i;
            }
            return s;
        }

        internal static Quaternion Norm(Quaternion q)
        {
            float l = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (l < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / l, q.y / l, q.z / l, q.w / l);
        }
    }

    /// <summary>Eine Kurve (Rotation, Position oder Skalierung) einer .anm-Spur.</summary>
    public sealed class AnmCurve
    {
        public float[] Times;
        public Vector4[] Values;   // Rotation: Unity-Quaternion (bereits konjugiert), sonst xyz
        public bool IsRotation;

        public Quaternion SampleRot(float t)
        {
            int n = Values.Length;
            if (n == 1 || t <= Times[0]) return ToQ(Values[0]);
            for (int i = 1; i < n; i++)
            {
                if (t <= Times[i])
                {
                    float f = (t - Times[i - 1]) / Mathf.Max(1e-6f, Times[i] - Times[i - 1]);
                    return Quaternion.Slerp(ToQ(Values[i - 1]), ToQ(Values[i]), f);
                }
            }
            return ToQ(Values[n - 1]);
        }

        public Vector3 SampleVec(float t)
        {
            int n = Values.Length;
            if (n == 1 || t <= Times[0]) return Values[0];
            for (int i = 1; i < n; i++)
            {
                if (t <= Times[i])
                {
                    float f = (t - Times[i - 1]) / Mathf.Max(1e-6f, Times[i] - Times[i - 1]);
                    return Vector3.Lerp(Values[i - 1], Values[i], f);
                }
            }
            return Values[n - 1];
        }

        static Quaternion ToQ(Vector4 v) => new Quaternion(v.x, v.y, v.z, v.w);
    }

    public sealed class AnmTrack
    {
        public uint Hash;
        public int Mask;          // Bit 0 Rotation, 1 Position, 2 Skalierung, 3 IK-Spur, 4 Wurzelbewegung
        public AnmCurve Rot, Pos, Scl;
    }

    /// <summary>
    /// .anm – Skelett-Clip ("ANM!", DATA): u32 Spuren, f32 Länge, f32 Länge inkl. Auslauf, u32, f32 Auslauf, u32 0,
    /// Spuren hintereinander: u16 Maske, u16 0xffff, u32 Größe, u32 Key4-Knochen, je gesetztem Bit 0-2 eine Kurve:
    ///   u16 n, u16 fmt, f32 Länge; Zeiten: fmt&amp;1 ? u16[n-1] (t = q/65535·Länge, erster Schlüssel bei 0, auf 4 aufgefüllt) : f32[n];
    ///   fmt&amp;4: f32 min, f32 Spanne, Werte u16 (min + q/65535·Spanne); fmt&amp;2: Werte i16 (Rotation xyz/32767, w = +√(1−|xyz|²),
    ///   Position/Skalierung q/16383); sonst f32[4] Rotation / f32[3] Position; fmt&amp;0x20: 3 Extras je Schlüssel
    ///   (Float davor bzw. quantisiert (e0, e1, x, y, z, e2)); Kurve auf 4 Byte aufgefüllt.
    /// </summary>
    public sealed class AnmClip
    {
        public string Name;
        public float Length;
        public readonly List<AnmTrack> Tracks = new List<AnmTrack>();

        public static AnmClip Parse(byte[] d, string name = null)
        {
            if (d == null || d.Length < 0x30 || !Bin.TagIs(d, 0, "ANM!")) return null;
            int p = 8 + (int)Bin.U32(d, 4);
            if (p + 32 > d.Length || !Bin.TagIs(d, p, "DATA")) return null;
            var clip = new AnmClip { Name = name };
            int n = (int)Bin.U32(d, p + 8);
            clip.Length = Bin.F32(d, p + 12);
            int q = p + 8 + 24;
            for (int i = 0; i < n && q + 12 <= d.Length; i++)
            {
                int mask = Bin.U16(d, q);
                int size = (int)Bin.U32(d, q + 4);
                if (size < 12 || q + size > d.Length) break;
                var t = new AnmTrack { Mask = mask, Hash = Bin.U32(d, q + 8) };
                if ((mask & 8) == 0)
                {
                    int r = q + 12;
                    try
                    {
                        if ((mask & 1) != 0) t.Rot = ReadCurve(d, ref r, true);
                        if ((mask & 2) != 0) t.Pos = ReadCurve(d, ref r, false);
                        if ((mask & 4) != 0) t.Scl = ReadCurve(d, ref r, false);
                        clip.Tracks.Add(t);
                    }
                    catch (Exception) { /* unbekanntes Format: Spur auslassen */ }
                }
                q += size;
            }
            return clip;
        }

        static AnmCurve ReadCurve(byte[] d, ref int p, bool rot)
        {
            int n = Bin.U16(d, p), fmt = Bin.U16(d, p + 2);
            float len = Bin.F32(d, p + 4);
            p += 8;
            if (n <= 0 || n > 100000) throw new FormatException("anm");
            var c = new AnmCurve { Times = new float[n], Values = new Vector4[n], IsRotation = rot };
            if ((fmt & 1) != 0)
            {
                c.Times[0] = 0f;
                for (int i = 1; i < n; i++) c.Times[i] = Bin.U16(d, p + 2 * (i - 1)) / 65535f * len;
                int bytes = 2 * (n - 1);
                p += bytes + (bytes % 4 != 0 ? 2 : 0);
            }
            else
            {
                for (int i = 0; i < n; i++) c.Times[i] = Bin.F32(d, p + 4 * i);
                p += 4 * n;
            }
            float mn = 0f, ext = 0f;
            if ((fmt & 4) != 0) { mn = Bin.F32(d, p); ext = Bin.F32(d, p + 4); p += 8; }
            int start = p;
            bool extras = (fmt & 0x20) != 0;
            for (int k = 0; k < n; k++)
            {
                if ((fmt & 4) != 0)
                {
                    int o = extras ? p + 4 : p;
                    float x = mn + Bin.U16(d, o) / 65535f * ext, y = mn + Bin.U16(d, o + 2) / 65535f * ext, z = mn + Bin.U16(d, o + 4) / 65535f * ext;
                    p += extras ? 12 : 6;
                    c.Values[k] = rot ? RotFromXyz(x, y, z) : new Vector4(x, y, z, 0f);
                }
                else if ((fmt & 2) != 0)
                {
                    int o = extras ? p + 4 : p;
                    short qx = Bin.I16(d, o), qy = Bin.I16(d, o + 2), qz = Bin.I16(d, o + 4);
                    p += extras ? 12 : 6;
                    if (rot) c.Values[k] = RotFromXyz(qx / 32767f, qy / 32767f, qz / 32767f);
                    else c.Values[k] = new Vector4(qx / 16383f, qy / 16383f, qz / 16383f, 0f);
                }
                else
                {
                    if (extras) p += 12;
                    if (rot)
                    {
                        var q = XsbSkeleton.Norm(new Quaternion(-Bin.F32(d, p), -Bin.F32(d, p + 4), -Bin.F32(d, p + 8), Bin.F32(d, p + 12)));
                        c.Values[k] = new Vector4(q.x, q.y, q.z, q.w);
                        p += 16;
                    }
                    else { c.Values[k] = new Vector4(Bin.F32(d, p), Bin.F32(d, p + 4), Bin.F32(d, p + 8), 0f); p += 12; }
                }
            }
            int used = p - start;
            if (used % 4 != 0) p += 4 - used % 4;
            return c;
        }

        static Vector4 RotFromXyz(float x, float y, float z)
        {
            float w = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y - z * z));
            return new Vector4(-x, -y, -z, w);   // konjugiert (Zeilen- -> Spaltenvektoren)
        }
    }

    /// <summary>Ein gehäuteter Primitiv eines Avatar-Meshes (Körper, Kopf, Kleidung), Knochen bereits als Skelettindizes.</summary>
    public sealed class SkinPrim
    {
        public Vector3[] Pos;
        public Vector3[] Nrm;
        public Vector4[] Tan;
        public Vector2[][] Uv;
        public BoneWeight[] Weights;
        public int[] Tris;
        public int MaterialIndex;
    }

    /// <summary>
    /// Gehäutete Vertexbuffer der Avatare (Layoutwort 0x0240: Float-Strom Position, Knochenindizes u8[4], Gewichte (N-1)·f32;
    /// 0x2040: Float-Strom Position, UVs, Knochen; Hauptstrom Normale/Farbe/(UVs)/Tangente/Binormale). Die Indizes eines
    /// Vertex zeigen in die BONE-Palette seines PRIM (Kind-Chunk), deren Einträge Knochen des Skeletts sind.
    /// </summary>
    public static class SkinnedGeometry
    {
        public static List<SkinPrim> Read(Geometry3DG geo, int geomOffset)
        {
            var list = new List<SkinPrim>();
            var d = geo.Data;
            if (geomOffset <= 0 || geomOffset + 16 > d.Length || !Bin.TagIs(d, geomOffset, "GEOM")) return list;
            int gsize = (int)Bin.U32(d, geomOffset + 8), ghs = (int)Bin.U32(d, geomOffset + 12);
            foreach (var c in EdenChunks.Read(d, geomOffset + ghs, geomOffset + gsize))
            {
                if (c.Tag != "PRIM") continue;
                int p = c.DataOffset;
                int ib = (int)Bin.U32(d, p), vb = (int)Bin.U32(d, p + 4);
                int mat = Bin.U16(d, p + 16);
                int primType = d[p + 18];
                int nb = d[p + 19];
                int[] palette = null;
                foreach (var ch in EdenChunks.Children(d, c))
                {
                    if (ch.Tag != "BONE") continue;
                    palette = new int[nb];
                    for (int i = 0; i < nb && ch.DataOffset + 4 * i + 4 <= d.Length; i++) palette[i] = (int)Bin.U32(d, ch.DataOffset + 4 * i);
                }
                var prim = ReadVb(d, vb, palette);
                if (prim == null) continue;
                prim.MaterialIndex = mat;
                var strip = geo.ReadIndexBuffer(ib);
                var tris = new List<int>(strip.Length * 3);
                if (primType == 4 || primType == 5) Geometry3DG.StripToList(strip, 0, tris);
                else Geometry3DG.ListToList(strip, 0, tris);
                int vc = prim.Pos.Length;
                for (int t = tris.Count - 3; t >= 0; t -= 3)
                    if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) tris.RemoveRange(t, 3);
                prim.Tris = tris.ToArray();
                if (prim.Tris.Length > 0) list.Add(prim);
            }
            return list;
        }

        static SkinPrim ReadVb(byte[] d, int vb, int[] palette)
        {
            if (vb <= 0 || vb + 48 > d.Length || !Bin.TagIs(d, vb, "DXVB")) return null;
            int chunkSize = (int)Bin.U32(d, vb + 8);
            int n = (int)Bin.U32(d, vb + 16);
            int dec = vb + 20;
            uint flags = Bin.U32(d, vb + 44);
            if ((flags & 0x2200) == 0 || n <= 0) return null;
            bool uvS0 = (flags & 0x2000) != 0;
            bool hasNormal = d[dec + 2] != 0, hasColor = d[dec + 4] != 0, hasColor2 = d[dec + 6] != 0;
            int nuv = d[dec + 8];
            bool hasTan = d[dec + 10] != 0, hasBin = d[dec + 12] != 0, hasIdx = d[dec + 14] != 0;
            int nw = d[dec + 16] != 0 ? Mathf.Clamp((d[dec + 17] & 0x0F) - 1, 0, 3) : 0;
            int s0 = 12 + (uvS0 ? 8 * nuv : 0) + (hasIdx ? 4 : 0) + 4 * nw;
            int oUv0 = 12;
            int oIdx = 12 + (uvS0 ? 8 * nuv : 0);
            int s1 = 0, oN = -1, oUv1 = -1, oT = -1;
            if (hasNormal) { oN = s1; s1 += 8; }
            if (hasColor) s1 += 4;
            if (hasColor2) s1 += 4;
            if (!uvS0) { oUv1 = s1; s1 += 8 * nuv; }
            if (hasTan) { oT = s1; s1 += 8; }
            if (hasBin) s1 += 8;
            int b0 = vb + 48, b1 = b0 + s0 * n;
            if (b1 + s1 * n > vb + chunkSize) return null;
            var pr = new SkinPrim { Pos = new Vector3[n], Nrm = new Vector3[n], Weights = new BoneWeight[n], Uv = new Vector2[Math.Max(1, nuv)][] };
            for (int u = 0; u < pr.Uv.Length; u++) pr.Uv[u] = new Vector2[n];
            if (hasTan) pr.Tan = new Vector4[n];
            for (int i = 0; i < n; i++)
            {
                int q = b0 + i * s0;
                pr.Pos[i] = new Vector3(Bin.F32(d, q), Bin.F32(d, q + 4), Bin.F32(d, q + 8));
                for (int u = 0; u < nuv; u++)
                {
                    int o = uvS0 ? q + oUv0 + 8 * u : b1 + i * s1 + oUv1 + 8 * u;
                    pr.Uv[u][i] = new Vector2(Bin.F32(d, o), Bin.F32(d, o + 4));
                }
                int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
                if (hasIdx) { i0 = d[q + oIdx]; i1 = d[q + oIdx + 1]; i2 = d[q + oIdx + 2]; i3 = d[q + oIdx + 3]; }
                float w0 = nw > 0 ? Bin.F32(d, q + oIdx + 4) : 1f;
                float w1 = nw > 1 ? Bin.F32(d, q + oIdx + 8) : 0f;
                float w2 = nw > 2 ? Bin.F32(d, q + oIdx + 12) : 0f;
                float rest = Mathf.Max(0f, 1f - w0 - w1 - w2);
                float w3 = 0f;
                if (nw == 1) w1 = rest; else if (nw == 2) w2 = rest; else if (nw >= 3) w3 = rest;
                var bw = new BoneWeight
                {
                    boneIndex0 = Map(palette, i0),
                    weight0 = w0,
                    boneIndex1 = Map(palette, i1),
                    weight1 = w1,
                    boneIndex2 = Map(palette, i2),
                    weight2 = w2,
                    boneIndex3 = Map(palette, i3),
                    weight3 = w3,
                };
                pr.Weights[i] = Normalize(bw);
                if (hasNormal)
                {
                    int o = b1 + i * s1 + oN;
                    var nv = new Vector3(Bin.HalfAt(d, o), Bin.HalfAt(d, o + 2), Bin.HalfAt(d, o + 4));
                    float l = nv.magnitude;
                    pr.Nrm[i] = l > 1e-6f && !float.IsNaN(l) ? nv / l : Vector3.up;
                }
                else pr.Nrm[i] = Vector3.up;
                if (hasTan)
                {
                    int o = b1 + i * s1 + oT;
                    var tv = new Vector3(Bin.HalfAt(d, o), Bin.HalfAt(d, o + 2), Bin.HalfAt(d, o + 4));
                    float l = tv.magnitude;
                    tv = l > 1e-6f && !float.IsNaN(l) ? tv / l : Vector3.right;
                    pr.Tan[i] = new Vector4(tv.x, tv.y, tv.z, 1f);
                }
            }
            if (nuv == 0) for (int i = 0; i < n; i++) pr.Uv[0][i] = Vector2.zero;
            return pr;
        }

        static int Map(int[] palette, int i) => palette != null && i < palette.Length ? palette[i] : i;

        static BoneWeight Normalize(BoneWeight b)
        {
            float s = b.weight0 + b.weight1 + b.weight2 + b.weight3;
            if (s <= 1e-6f) { b.weight0 = 1f; b.weight1 = b.weight2 = b.weight3 = 0f; return b; }
            b.weight0 /= s; b.weight1 /= s; b.weight2 /= s; b.weight3 /= s;
            return b;
        }
    }
}
