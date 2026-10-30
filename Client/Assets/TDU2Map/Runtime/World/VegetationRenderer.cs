using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.World
{
    /// <summary>
    /// Zeichnet die .pmi-Platzierungen (Bäume, Büsche, Felsen) eines Sektors per GPU-Instancing
    /// mit distanzbasierter LOD-Auswahl (LOD0 = Mesh, LOD1 = Impostor/Low-Poly laut Library).
    /// </summary>
    public sealed class VegetationRenderer : MonoBehaviour
    {
        sealed class Batch
        {
            public LibraryObject Obj;
            public Vector3[] LocalPos;        // im Map-Root-Raum
            public Matrix4x4[] LocalMatrix;   // im Map-Root-Raum
            public Matrix4x4[] WorldMatrix;
            public List<int>[] PerLod;
            public Mesh CollisionMesh;        // nur feste Submeshes von LOD0 (Stamm, Felsen, Pfosten – kein Laub)
            public GameObject[] Colliders;
            public bool Capsule;              // hohe Objekte ohne feste Submeshes (Bäume mit Alpha-Rinde, Laternen): Stamm-Kapsel
            public Vector3 CapCenter; public float CapHeight, CapRadius;
        }

        [Tooltip("Hohe Objekte mit schmalem Fuß (Laternen, Ampeln, Schilder) bekommen nur eine Kapsel um den Mast")]
        public bool PoleCapsules = true;
        sealed class PoleInfo { public bool Ok; public Vector3 Center; public float Height, Radius; }
        static readonly Dictionary<Mesh, PoleInfo> _poles = new Dictionary<Mesh, PoleInfo>();

        /// <summary>
        /// Mast-Erkennung am Kollisionsmesh: Höhe &gt; 2,5 m und die Punkte im untersten Meter liegen in einem Fuß &lt; 1,2 m.
        /// Dann Kapsel von unten bis oben (max. 10 m) um den Fuß.
        /// </summary>
        static bool TryPole(Mesh m, out Vector3 center, out float height, out float radius)
        {
            center = Vector3.zero; height = radius = 0f;
            if (m == null) return false;
            if (!_poles.TryGetValue(m, out var info))
            {
                info = new PoleInfo();
                try
                {
                    var v = m.vertices; var t = m.GetTriangles(0);
                    if (t.Length >= 3)
                    {
                        float minY = float.MaxValue, maxY = float.MinValue;
                        foreach (int i in t) { float y = v[i].y; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                        float h = maxY - minY;
                        if (h > 2.5f)
                        {
                            float cut = minY + Mathf.Min(1.0f, h * 0.3f);
                            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                            foreach (int i in t)
                            {
                                var p = v[i]; if (p.y > cut) continue;
                                if (p.x < x0) x0 = p.x; if (p.x > x1) x1 = p.x; if (p.z < z0) z0 = p.z; if (p.z > z1) z1 = p.z;
                            }
                            float ex = Mathf.Max(x1 - x0, z1 - z0);
                            if (x0 <= x1 && ex < 1.2f)
                            {
                                info.Ok = true;
                                info.Radius = Mathf.Clamp(ex * 0.5f, 0.08f, 0.6f);
                                info.Height = Mathf.Max(Mathf.Min(h, 10f), info.Radius * 2f + 0.01f);
                                info.Center = new Vector3((x0 + x1) * 0.5f, minY + info.Height * 0.5f, (z0 + z1) * 0.5f);
                            }
                        }
                    }
                }
                catch (System.Exception) { info.Ok = false; }
                _poles[m] = info;
            }
            if (!info.Ok) return false;
            center = info.Center; height = info.Height; radius = info.Radius;
            return true;
        }

        [Tooltip("Collider für Platzierungen (Felsen, Pfosten, Leitplanken, Baumstämme) im Umkreis des Betrachters")]
        public bool Colliders = true;
        public float ColliderRadius = 160f;
        public int MaxCollidersPerFrame = 40;
        Transform _colliderRoot;
        static readonly Dictionary<LibraryObject, Mesh> _collisionMeshes = new Dictionary<LibraryObject, Mesh>();

        public Transform Viewer;
        public Transform MapRoot;
        public float MaxDistance = 800f;
        /// <summary>Sichtweiten-Faktor für alle Vegetations-Renderer (TDU2RenderDistance: x4 … /4).</summary>
        public static float DistanceMultiplier = 1f;
        float _appliedMul = 1f;
        /// <summary>true, wenn die Library-Meshes vorgespiegelt sind (siehe ObjectLibrary.MirrorMeshes).</summary>
        public bool MirroredMeshes;
        public bool CullLastLod = false;
        public ShadowCastingMode Shadows = ShadowCastingMode.On;
        public int RebucketEveryFrames = 8;

        readonly List<Batch> _batches = new List<Batch>();
        readonly Matrix4x4[] _tmp = new Matrix4x4[1023];
        Matrix4x4 _rootMatrix;
        Vector3 _lastViewer = new Vector3(float.MaxValue, 0, 0);
        int _frame;

        public int InstanceCount { get; private set; }

        public void Add(LibraryObject obj, List<Matrix4x4> localMatrices)
        {
            if (obj == null || localMatrices.Count == 0) return;
            var b = new Batch
            {
                Obj = obj,
                LocalMatrix = localMatrices.ToArray(),
                LocalPos = new Vector3[localMatrices.Count],
                WorldMatrix = new Matrix4x4[localMatrices.Count],
                PerLod = new List<int>[obj.Lods.Length],
            };
            for (int i = 0; i < b.LocalMatrix.Length; i++) b.LocalPos[i] = b.LocalMatrix[i].GetColumn(3);
            for (int l = 0; l < b.PerLod.Length; l++) b.PerLod[l] = new List<int>();
            b.CollisionMesh = CollisionMeshFor(obj);
            // Masten (Laternen, Ampeln, Schilder): statt des ganzen Meshes (Ausleger, Leuchtenkopf) eine Kapsel um den Mast
            if (b.CollisionMesh != null && PoleCapsules && TryPole(b.CollisionMesh, out var pc, out float ph, out float pr))
            {
                b.CollisionMesh = null;
                b.Capsule = true; b.CapCenter = pc; b.CapHeight = ph; b.CapRadius = pr;
            }
            else if (b.CollisionMesh == null && obj.Lods.Length > 0 && obj.Lods[0].Mesh != null)
            {
                var bb = obj.Lods[0].Mesh.bounds;
                if (bb.size.y > 2f)
                {
                    b.Capsule = true;
                    b.CapHeight = Mathf.Min(bb.size.y, 6f);
                    b.CapRadius = Mathf.Clamp(Mathf.Min(bb.size.x, bb.size.z) * 0.07f, 0.12f, 0.45f);
                    b.CapCenter = new Vector3(bb.center.x, bb.min.y + b.CapHeight * 0.5f, bb.center.z);
                }
            }
            if (b.CollisionMesh != null || b.Capsule) b.Colliders = new GameObject[b.LocalMatrix.Length];
            _batches.Add(b);
            InstanceCount += b.LocalMatrix.Length;
            _rootMatrix = new Matrix4x4(); // erzwingt Neuberechnung
        }

        void UpdateWorldMatrices()
        {
            var rm = MapRoot != null ? MapRoot.localToWorldMatrix : Matrix4x4.identity;
            if (rm == _rootMatrix) return;
            _rootMatrix = rm;
            foreach (var b in _batches)
                for (int i = 0; i < b.LocalMatrix.Length; i++) b.WorldMatrix[i] = MirroredMeshes ? rm * b.LocalMatrix[i] * UnityUtil.MirrorXMatrix : rm * b.LocalMatrix[i];
            _lastViewer = new Vector3(float.MaxValue, 0, 0);
        }

        void Rebucket(Vector3 viewerLocal)
        {
            float mul = DistanceMultiplier;
            float maxSq = MaxDistance * mul * MaxDistance * mul;
            foreach (var b in _batches)
            {
                foreach (var l in b.PerLod) l.Clear();
                var lods = b.Obj.Lods;
                for (int i = 0; i < b.LocalPos.Length; i++)
                {
                    float dSq = (b.LocalPos[i] - viewerLocal).sqrMagnitude;
                    if (dSq > maxSq) continue;
                    float d = Mathf.Sqrt(dSq);
                    int chosen = -1;
                    for (int l = 0; l < lods.Length; l++)
                        if (d <= lods[l].MaxDistance * mul) { chosen = l; break; }
                    if (chosen < 0 && !CullLastLod) chosen = lods.Length - 1;
                    if (chosen >= 0) b.PerLod[chosen].Add(i);
                }
            }
        }

        static int _billboardFrame = -1;

        static readonly List<VegetationRenderer> _all = new List<VegetationRenderer>();
        void OnEnable() { if (!_all.Contains(this)) _all.Add(this); }
        void OnDisable() => _all.Remove(this);

        /// <summary>
        /// Platzierte Objekte, deren Name mit einem der Präfixe beginnt (z.B. Straßenlampen "SF_LIGH"), im Umkreis um eine
        /// Weltposition: Weltmatrix (mit Spiegelung), lokale Bounds von LOD0 und Name. Für TDU2Weather (Lampenlicht nachts).
        /// </summary>
        public static void FindInstances(string[] prefixes, Vector3 worldPos, float radius, List<(Matrix4x4 m, Bounds b, string name)> result)
        {
            float r2 = radius * radius;
            foreach (var vr in _all)
            {
                if (vr == null) continue;
                vr.UpdateWorldMatrices();
                foreach (var b in vr._batches)
                {
                    if (b.Obj == null || b.Obj.Lods.Length == 0 || b.Obj.Lods[0].Mesh == null || b.Obj.Name == null) continue;
                    bool ok = false;
                    foreach (var p in prefixes) if (b.Obj.Name.StartsWith(p, System.StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                    if (!ok) continue;
                    var lb = b.Obj.Lods[0].Mesh.bounds;
                    for (int i = 0; i < b.WorldMatrix.Length; i++)
                    {
                        var m = b.WorldMatrix[i];
                        var pos = (Vector3)m.GetColumn(3);
                        if ((pos - worldPos).sqrMagnitude > r2) continue;
                        result.Add((m, lb, b.Obj.Name));
                    }
                }
            }
        }

        void Update()
        {
            if (_batches.Count == 0) return;
            UpdateWorldMatrices();
            var cam = Viewer != null ? Viewer : (Camera.main != null ? Camera.main.transform : null);
            if (cam == null) return;
            if (_billboardFrame != Time.frameCount && Camera.main != null)
            {
                // Billboard-Achsen der Spielkamera für den Schattenpass von TDU2/Sprite (View-Matrix-Zeilen = Kamera rechts/oben/-vorne)
                _billboardFrame = Time.frameCount;
                var vm = Camera.main.worldToCameraMatrix;
                Shader.SetGlobalVector("_TDU2BillboardRight", new Vector4(vm.m00, vm.m01, vm.m02, 1f));
                Shader.SetGlobalVector("_TDU2BillboardUp", new Vector4(vm.m10, vm.m11, vm.m12, 1f));
                Shader.SetGlobalVector("_TDU2BillboardFwd", new Vector4(vm.m20, vm.m21, vm.m22, 1f));
            }
            var vl = MapRoot != null ? MapRoot.InverseTransformPoint(cam.position) : cam.position;
            if (++_frame % RebucketEveryFrames == 0 || (vl - _lastViewer).sqrMagnitude > 25f || _appliedMul != DistanceMultiplier)
            {
                Rebucket(vl);
                _lastViewer = vl;
                _appliedMul = DistanceMultiplier;
            }
            if (Colliders && _frame % 10 == 0) UpdateColliders(vl);

            int layer = gameObject.layer;
            foreach (var b in _batches)
            {
                for (int l = 0; l < b.PerLod.Length; l++)
                {
                    var idx = b.PerLod[l];
                    if (idx.Count == 0) continue;
                    var lod = b.Obj.Lods[l];
                    int done = 0;
                    while (done < idx.Count)
                    {
                        int n = Mathf.Min(1023, idx.Count - done);
                        for (int k = 0; k < n; k++) _tmp[k] = b.WorldMatrix[idx[done + k]];
                        for (int s = 0; s < lod.Mesh.subMeshCount && s < lod.Materials.Length; s++)
                        {
                            var mat = lod.Materials[s];
                            if (mat == null) continue;
                            // gemeinsame Materialien ohne Instancing (Pool-Wasser TD_Water, Fallback): einzeln zeichnen
                            if (!mat.enableInstancing)
                            {
                                for (int k = 0; k < n; k++) Graphics.DrawMesh(lod.Mesh, _tmp[k], mat, layer, null, s, null, Shadows, true);
                                continue;
                            }
                            Graphics.DrawMeshInstanced(lod.Mesh, s, mat, _tmp, n, null, Shadows, true, layer);
                        }
                        done += n;
                    }
                }
            }
        }

        /// <summary>Collider-Mesh aus den festen Submeshes von LOD0 (Material opak, kein Alpha-Test/Laub); null = keine Kollision.</summary>
        static Mesh CollisionMeshFor(LibraryObject obj)
        {
            if (obj == null || obj.Lods == null || obj.Lods.Length == 0 || obj.Lods[0].Mesh == null) return null;
            if (_collisionMeshes.TryGetValue(obj, out var cached)) return cached;
            Mesh result = null;
            var lod = obj.Lods[0];
            var src = lod.Mesh;
            try
            {
                var tris = new List<int>();
                // Pflanzen (Laub/Billboards) -> Alpha-Test-Teile ohne Kollision; sonst (Zäune, Gitter, Schilder) mit Kollision
                bool plant = false;
                foreach (var pm in lod.Materials)
                    if (pm != null && pm.shader != null && (pm.shader.name == "TDU2/Foliage" || pm.shader.name == "TDU2/Sprite" || pm.shader.name == "TDU2/Grass")) { plant = true; break; }
                for (int sm = 0; sm < src.subMeshCount && sm < lod.Materials.Length; sm++)
                {
                    var m = lod.Materials[sm];
                    if (m == null || m.renderQueue >= 2500) continue;   // TDU2/Object steht in der Queue AlphaTest (2450) – Alpha-Test entscheidet _Cutoff
                    if (plant && m.HasProperty("_Cutoff") && m.GetFloat("_Cutoff") > 0.01f) continue;
                    if (m.shader != null && (m.shader.name == "TDU2/Foliage" || m.shader.name == "TDU2/Sprite" || m.shader.name == "TDU2/Grass")) continue;
                    if (src.GetTopology(sm) != MeshTopology.Triangles) continue;
                    tris.AddRange(src.GetTriangles(sm));
                }
                if (tris.Count >= 3)
                {
                    result = new Mesh { name = src.name + "_col", hideFlags = HideFlags.DontSave };
                    if (src.vertexCount > 65000) result.indexFormat = IndexFormat.UInt32;
                    result.vertices = src.vertices;
                    result.SetTriangles(tris, 0);
                    result.RecalculateBounds();
                    // flache Bodenteile (Gullydeckel, Markierungen, Bordsteinkanten < 25 cm) nicht blockieren lassen
                    if (result.bounds.size.y < 0.25f || !UnityUtil.IsCollidable(result)) { Object.Destroy(result); result = null; }
                }
            }
            catch (System.Exception) { result = null; }   // Mesh nicht lesbar
            _collisionMeshes[obj] = result;
            return result;
        }

        void UpdateColliders(Vector3 viewerLocal)
        {
            if (MapRoot == null) return;
            if (_colliderRoot == null)
            {
                _colliderRoot = new GameObject("PlacementColliders").transform;
                _colliderRoot.SetParent(MapRoot, false);
            }
            float rIn = ColliderRadius * ColliderRadius, rOut = ColliderRadius * 1.3f * ColliderRadius * 1.3f;
            int created = 0;
            foreach (var b in _batches)
            {
                if (b.Colliders == null) continue;
                for (int i = 0; i < b.LocalPos.Length; i++)
                {
                    float dSq = (b.LocalPos[i] - viewerLocal).sqrMagnitude;
                    var c = b.Colliders[i];
                    if (c == null && dSq < rIn && created < MaxCollidersPerFrame)
                    {
                        var m = b.LocalMatrix[i];
                        c = new GameObject("col") { layer = gameObject.layer };
                        var t = c.transform;
                        t.SetParent(_colliderRoot, false);
                        t.localPosition = m.GetColumn(3);
                        t.localRotation = m.rotation;
                        var sc = m.lossyScale;
                        if (MirroredMeshes) sc.x = -sc.x;   // Instanzmatrix * MirrorX (Mesh ist vorgespiegelt)
                        t.localScale = sc;
                        if (b.CollisionMesh != null) c.AddComponent<MeshCollider>().sharedMesh = b.CollisionMesh;
                        else
                        {
                            // Primitive Collider vertragen keine negative Skalierung: Spiegelung in den Mittelpunkt verlegen
                            var cc = b.CapCenter;
                            if (t.localScale.x < 0f) { t.localScale = new Vector3(-t.localScale.x, t.localScale.y, t.localScale.z); cc.x = -cc.x; }
                            var cap = c.AddComponent<CapsuleCollider>();
                            cap.direction = 1; cap.center = cc; cap.height = b.CapHeight; cap.radius = b.CapRadius;
                        }
                        b.Colliders[i] = c;
                        created++;
                    }
                    else if (c != null && dSq > rOut)
                    {
                        Destroy(c);
                        b.Colliders[i] = null;
                    }
                }
            }
        }

        void OnDestroy()
        {
            if (_colliderRoot != null) Destroy(_colliderRoot.gameObject);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.6f);
            foreach (var b in _batches)
                for (int i = 0; i < b.WorldMatrix.Length; i += 4)
                    Gizmos.DrawWireCube(b.WorldMatrix[i].GetColumn(3), Vector3.one * 2f);
        }
    }
}
