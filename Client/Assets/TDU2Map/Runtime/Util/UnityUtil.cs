using UnityEngine;

namespace TDU2.Map
{
    public static class UnityUtil
    {
        /// <summary>Destroy im Play-Mode, DestroyImmediate im Editor (Edit-Mode).</summary>
        public static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        static readonly System.Collections.Generic.Dictionary<Mesh, bool> _collidable = new System.Collections.Generic.Dictionary<Mesh, bool>();

        /// <summary>
        /// true, wenn PhysX aus dem Mesh einen MeshCollider bauen kann (mind. ein nicht entartetes Dreieck, Ausdehnung in 2 Achsen).
        /// Verhindert "[Physics.PhysX] cleaning the mesh failed".
        /// </summary>
        public static bool IsCollidable(Mesh m)
        {
            if (m == null || m.vertexCount < 3) return false;
            if (_collidable.TryGetValue(m, out var ok)) return ok;
            ok = false;
            var sz = m.bounds.size;
            int axes = (sz.x > 1e-3f ? 1 : 0) + (sz.y > 1e-3f ? 1 : 0) + (sz.z > 1e-3f ? 1 : 0);
            if (axes >= 2)
            {
                try
                {
                    var v = m.vertices;
                    for (int s = 0; s < m.subMeshCount && !ok; s++)
                    {
                        if (m.GetTopology(s) != MeshTopology.Triangles) continue;
                        var t = m.GetTriangles(s);
                        for (int i = 0; i + 2 < t.Length; i += 3)
                            if (Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).sqrMagnitude > 1e-10f) { ok = true; break; }
                    }
                }
                catch (System.Exception) { ok = false; }
            }
            _collidable[m] = ok;
            return ok;
        }

        /// <summary>Kopie eines Meshes, an der YZ-Ebene gespiegelt (x = -x, Normalen/Tangenten gespiegelt, Dreiecke umgedreht).</summary>
        public static Mesh MirrorX(Mesh src)
        {
            var m = Object.Instantiate(src);
            m.name = src.name + "_mx";
            var v = m.vertices; for (int i = 0; i < v.Length; i++) v[i].x = -v[i].x; m.vertices = v;
            var n = m.normals; if (n != null && n.Length == v.Length) { for (int i = 0; i < n.Length; i++) n[i].x = -n[i].x; m.normals = n; }
            var t = m.tangents; if (t != null && t.Length == v.Length) { for (int i = 0; i < t.Length; i++) { t[i].x = -t[i].x; t[i].w = -t[i].w; } m.tangents = t; }
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var tri = m.GetTriangles(s);
                for (int i = 0; i + 2 < tri.Length; i += 3) { int a = tri[i + 1]; tri[i + 1] = tri[i + 2]; tri[i + 2] = a; }
                m.SetTriangles(tri, s, false);
            }
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Spiegelmatrix x -> -x.</summary>
        public static readonly Matrix4x4 MirrorXMatrix = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
    }
}
