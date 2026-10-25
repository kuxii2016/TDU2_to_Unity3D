using System;
using System.Collections.Generic;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TDU2.World
{
    public sealed class ObjectBuildOptions
    {
        public float LodDistanceScale = 3f;   // Distanz = sqrt(Dateiwert) * Faktor
        public bool CullLastLod = false;      // letzte LOD-Stufe ausblenden?
        public bool SkipOcean = true;         // Ozean-Meshes weglassen (eigene Wasserfläche)
        public bool SkipEffects = true;       // FX_* (Lichtkegel/Volumen) weglassen
        public ShadowCastingMode CastShadows = ShadowCastingMode.On;
        public float AssumedFov = 60f;
    }

    /// <summary>Sammelt alle erzeugten Unity-Ressourcen, damit sie beim Entladen zerstört werden.</summary>
    public sealed class BuildResources
    {
        public readonly List<Object> Owned = new List<Object>();
        // Caches pro Szene (Schlüssel enthält die Materialbibliothek der Szene): mehrere Exteriors in einer Kachel
        // verwenden oft gleiche Objekt-/Materialnamen (gleicher Hash) mit unterschiedlichem Inhalt.
        public readonly Dictionary<(object scope, ulong key), Material> Materials = new Dictionary<(object, ulong), Material>();
        public readonly Dictionary<(object scope, long key), (Mesh mesh, Material[] mats)> Renderables = new Dictionary<(object, long), (Mesh, Material[])>();

        public void Destroy()
        {
            foreach (var o in Owned) if (o != null) UnityUtil.SafeDestroy(o);
            Owned.Clear(); Materials.Clear(); Renderables.Clear();
        }
    }

    public static class ObjectBuilder
    {
        /// <summary>Distanz aus dem Dateiwert (vermutlich quadratisch gespeichert). 0 = unbegrenzt.</summary>
        public static float LodDistance(float raw, float scale) => raw > 0f ? Mathf.Sqrt(raw) * scale : float.PositiveInfinity;

        /// <summary>Mesh + Materialien für eine Geometrie, gefiltert (Ozean) und gecacht.</summary>
        public static (Mesh mesh, Material[] mats) GetRenderable(
            MeshData md, ulong geoKey, ulong[] materialBank, Material2DM matLib,
            Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex,
            BuildResources res, ObjectBuildOptions opt, int bankOffset)
        {
            object scope = (object)matLib ?? md ?? (object)res;
            var cacheKey = (scope, (long)geoKey ^ ((long)bankOffset << 1));
            if (res.Renderables.TryGetValue(cacheKey, out var cached)) return cached;
            if (md == null) { res.Renderables[cacheKey] = (null, null); return (null, null); }

            var keep = new List<int>();
            var mats = new List<Material>();
            for (int i = 0; i < md.SubMeshes.Count; i++)
            {
                int mi = md.SubMeshMaterialIndex[i];
                bool sprite = i < md.SubMeshIsSprite.Count && md.SubMeshIsSprite[i];
                ulong mk = mi >= 0 && mi < materialBank.Length ? materialBank[mi] : 0;
                MaterialDef def = null;
                if (mk != 0 && matLib != null) matLib.Materials.TryGetValue(mk, out def);
                if (def != null && def.IsOcean && opt.SkipOcean) continue;
                // FX_*: Lichtvolumen weglassen – außer den Lichtkegeln der Laternen (FX_LIGHT/FX_SHAFT), die nachts leuchten
                if (def != null && opt.SkipEffects && def.IsEffect && !IsLampBeam(def.Name)) continue;
                if (sprite && MaterialFactory.IsSrp) continue; // Billboard-Shader nur für Built-in RP
                int uvSets = i < md.SubMeshUvSets.Count ? md.SubMeshUvSets[i] : 1;
                bool tintUv = uvSets >= 3;   // DETAIL2 (Farbpalette/Schmutz) liegt auf UV-Satz 3
                bool fourUv = uvSets >= 4;   // Straßen: MARKS2 auf UV-Satz 3
                var cacheMat = (scope, (sprite ? mk ^ 0x5350524954450000UL : mk) ^ (tintUv ? 0x8000000000000000UL : 0UL) ^ (fourUv ? 0x4000000000000000UL : 0UL));
                if (!res.Materials.TryGetValue(cacheMat, out var mat))
                {
                    mat = MaterialFactory.CreateObjectMaterial(def, getTex, sprite, tintUv, fourUv);
                    res.Materials[cacheMat] = mat;
                    if (mat != null && mat != MaterialFactory.Water && mat != MaterialFactory.Fallback) res.Owned.Add(mat);
                }
                if (mat == null) continue; // ausgeblendet (z.B. Leuchteffekte am Tag)
                keep.Add(i);
                mats.Add(mat);
            }
            if (keep.Count == 0) { res.Renderables[cacheKey] = (null, null); return (null, null); }

            MeshData use = md;
            if (keep.Count != md.SubMeshes.Count)
            {
                use = new MeshData
                {
                    Name = md.Name,
                    Positions = md.Positions,
                    Normals = md.Normals,
                    Colors = md.Colors,
                    UV0 = md.UV0,
                    UV1 = md.UV1,
                    UV2 = md.UV2,
                    UV3 = md.UV3,
                    HasUV3 = md.HasUV3,
                    BoneIdx = md.BoneIdx,
                    BoneW = md.BoneW,
                    HasBones = md.HasBones,
                    HasColors = md.HasColors,
                    HasUV1 = md.HasUV1,
                    HasUV2 = md.HasUV2,
                    BoundsPadding = md.BoundsPadding,
                };
                foreach (var k in keep)
                {
                    use.SubMeshes.Add(md.SubMeshes[k]); use.SubMeshMaterialIndex.Add(md.SubMeshMaterialIndex[k]);
                    if (k < md.SubMeshUvSets.Count) use.SubMeshUvSets.Add(md.SubMeshUvSets[k]);
                }
            }
            var mesh = use.ToMesh();
            res.Owned.Add(mesh);
            var r = (mesh, mats.ToArray());
            res.Renderables[cacheKey] = r;
            return r;
        }

        /// <summary>
        /// Instanziert alle Objekte einer Bank (Hierarchie HIE -> INS -> LODs) unter <paramref name="parent"/>.
        /// Positionen sind relativ zum Ursprung der Bank (Sektor-/Area-/Inselecke).
        /// </summary>
        /// <summary>Lichtkegel/-schein der Straßenlaternen (FX_LIGHT, FX_SHAFT): nur nachts sichtbar (MaterialFactory _NightOnly).</summary>
        public static bool IsLampBeam(string name) => name != null && (name.StartsWith("FX_LIGHT", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FX_SHAFT", StringComparison.OrdinalIgnoreCase));

        public static List<GameObject> BuildObjects(BankModel model, Transform parent, GlobalTextureCache global, BuildResources res, ObjectBuildOptions opt, Action<GameObject, Vector3> onCreated = null)
        {
            var all = new List<GameObject>();
            Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex = (k, u, v) => model.GetTexture(k, u, v, global);
            foreach (var set in model.AllObjectSets())
                all.AddRange(BuildObjects(set, getTex, parent, res, opt, onCreated));
            return all;
        }

        public static List<GameObject> BuildObjects(ObjectSet set, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex, Transform parent, BuildResources res, ObjectBuildOptions opt, Action<GameObject, Vector3> onCreated = null)
        {
            if (set?.Placements == null) return BuildObjectsAt(set, getTex, parent, res, opt, onCreated);
            var all = new List<GameObject>();
            int i = 0;
            foreach (var pm in set.Placements)
            {
                var holder = new GameObject($"{set.Name}#{i++}");
                holder.transform.SetParent(parent, false);
                ApplyMatrix(holder.transform, pm);
                all.Add(holder);
                BuildObjectsAt(set, getTex, holder.transform, res, opt, onCreated == null ? null : (Action<GameObject, Vector3>)((go, c) => onCreated(go, pm.MultiplyPoint3x4(c))));
            }
            return all;
        }

        static List<GameObject> BuildObjectsAt(ObjectSet set, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex, Transform parent, BuildResources res, ObjectBuildOptions opt, Action<GameObject, Vector3> onCreated)
        {
            var created = new List<GameObject>();
            var scene = set?.Scene;
            if (scene == null) return created;

            var byOffset = new Dictionary<int, HieRecord>();
            foreach (var h in scene.Hierarchy) byOffset[h.Offset] = h;

            IEnumerable<(InsRecord ins, Matrix4x4 m)> Leaves()
            {
                if (scene.Hierarchy.Count > 0)
                {
                    foreach (var h in scene.Hierarchy)
                    {
                        var ins = scene.GetIns(h.InsOffset);
                        if (ins.Lods.Length == 0) continue;
                        yield return (ins, scene.WorldMatrix(h, byOffset));
                    }
                }
                else
                {
                    foreach (var off in scene.InsOffsets)
                    {
                        var ins = scene.GetIns(off);
                        if (ins.Lods.Length == 0) continue;
                        yield return (ins, ins.Matrix);
                    }
                }
            }

            foreach (var (ins, m) in Leaves())
            {
                var bankKeys = scene.ReadMaterialBank(ins.MaterialBankOffset);
                var lodRenderables = new List<(Mesh mesh, Material[] mats, float dist)>();
                foreach (var lod in ins.Lods)
                {
                    set.Meshes.TryGetValue(lod.ObjKey, out var md);
                    var r = GetRenderable(md, lod.ObjKey, bankKeys, set.Materials, getTex, res, opt, ins.MaterialBankOffset);
                    if (r.mesh == null) continue;
                    lodRenderables.Add((r.mesh, r.mats, LodDistance(lod.RawDistance, opt.LodDistanceScale)));
                }
                if (lodRenderables.Count == 0) continue;

                string name = ins.Key != 0 ? Bin.KeyToString(ins.Key) : Bin.KeyToString(ins.Lods[0].ObjKey);
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                ApplyMatrix(go.transform, m);
                created.Add(go);
                SetupLods(go, lodRenderables, opt);

                if (onCreated != null)
                {
                    var s = ins.Lods[0].Sphere;
                    onCreated(go, m.MultiplyPoint3x4(new Vector3(s.x, s.y, s.z)));
                }
            }
            return created;
        }

        public static void ApplyMatrix(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            var sx = new Vector3(m.m00, m.m10, m.m20).magnitude;
            var sy = new Vector3(m.m01, m.m11, m.m21).magnitude;
            var sz = new Vector3(m.m02, m.m12, m.m22).magnitude;
            if (sx < 1e-6f || sy < 1e-6f || sz < 1e-6f) { t.localRotation = Quaternion.identity; t.localScale = Vector3.one; return; }
            var fwd = new Vector3(m.m02, m.m12, m.m22) / sz;
            var up = new Vector3(m.m01, m.m11, m.m21) / sy;
            // negative Determinante (Spiegelung) -> X-Skalierung negativ
            float det = m.determinant;
            t.localRotation = Quaternion.LookRotation(fwd, up);
            t.localScale = new Vector3(det < 0 ? -sx : sx, sy, sz);
        }

        static void SetupLods(GameObject go, List<(Mesh mesh, Material[] mats, float dist)> lods, ObjectBuildOptions opt)
        {
            bool needGroup = lods.Count > 1 || (opt.CullLastLod && !float.IsInfinity(lods[0].dist));
            if (!needGroup)
            {
                AddRenderer(go, lods[0].mesh, lods[0].mats, opt);
                return;
            }
            var renderers = new List<Renderer[]>();
            for (int i = 0; i < lods.Count; i++)
            {
                var child = new GameObject("LOD" + i);
                child.transform.SetParent(go.transform, false);
                renderers.Add(new Renderer[] { AddRenderer(child, lods[i].mesh, lods[i].mats, opt) });
            }
            var group = go.AddComponent<LODGroup>();
            var arr = new LOD[lods.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = new LOD(1f / (i + 2), renderers[i]);
            group.SetLODs(arr);
            group.RecalculateBounds();

            float size = Mathf.Max(group.size, 0.01f);
            float k = 2f * Mathf.Tan(opt.AssumedFov * 0.5f * Mathf.Deg2Rad);
            float prev = 1f;
            for (int i = 0; i < arr.Length; i++)
            {
                bool last = i == arr.Length - 1;
                float d = lods[i].dist;
                float h;
                if (last && (!opt.CullLastLod || float.IsInfinity(d))) h = 0.0001f;
                else h = float.IsInfinity(d) ? 0.0001f : size / (d * k);
                h = Mathf.Clamp(h, 0.00001f, prev * 0.999f);
                arr[i].screenRelativeTransitionHeight = h;
                prev = h;
            }
            group.SetLODs(arr);
        }

        static Renderer AddRenderer(GameObject go, Mesh mesh, Material[] mats, ObjectBuildOptions opt)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = opt.CastShadows;
            return mr;
        }
    }
}
