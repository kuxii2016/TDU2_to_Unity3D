using System.Collections.Generic;
using TDU2.Map.Formats;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Map.World
{
    /// <summary>
    /// Erzeugt Gras/Büsche um den Betrachter aus der Grastyp-Karte (.pgr) eines Sektors.
    /// Grastypen aus fx/grasstypes.xml, Texturen aus fx/tallgrass.bnk (setgrass.2db = 16 Sprites, setbush.2db = 4 Sprites).
    /// Das Spiel generiert Gras prozedural zur Laufzeit; Dichte/Verteilung hier ist eine Annäherung.
    /// Dieses Component sitzt auf dem Sektor-Root (Ursprung = Sektorecke).
    /// </summary>
    public sealed class GrassRenderer : MonoBehaviour
    {
        public Transform Viewer;
        public HeightGrid Heights;
        public GrassMap Map;
        public GrassTypes Types;
        public Material GrassMaterial, BushMaterial;
        public int GrassAtlasCells = 16, BushAtlasCells = 4;
        /// <summary>Colormap/Splatmap des Sektors (TD_TallGrass färbt das Gras mit der Bodenfarbe, gGrassTex x gColorTex).</summary>
        public Texture GroundTex, SplatTex;
        public float ShadowStrength = 0.6f;
        MaterialPropertyBlock _mpb;

        public float Radius = 120f;
        public float ChunkSize = 32f;
        public float Spacing = 1.2f;
        public float Density = 0.7f;
        public float BushChance = 0.03f;
        public float SizeScale = 1f;
        public int MaxChunksPerFrame = 6;
        public float SectorSize = WorldLayout.SectorSize;

        readonly Dictionary<Vector2Int, GameObject> _chunks = new Dictionary<Vector2Int, GameObject>();
        readonly List<Vector2Int> _tmpRemove = new List<Vector2Int>();
        float _nextCheck;
        bool _pending;
        readonly List<(float d, Vector2Int c)> _todo = new List<(float, Vector2Int)>();

        void Update()
        {
            if (Heights == null || Map == null || Types == null || GrassMaterial == null) return;
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + (_pending ? 0f : 0.2f);   // solange Stücke fehlen: jedes Bild weiterbauen
            var cam = Viewer != null ? Viewer : (Camera.main != null ? Camera.main.transform : null);
            if (cam == null) return;
            var local = transform.InverseTransformPoint(cam.position);

            int nChunks = Mathf.CeilToInt(SectorSize / ChunkSize);
            int built = 0;
            int cx0 = Mathf.Max(0, Mathf.FloorToInt((local.x - Radius) / ChunkSize));
            int cx1 = Mathf.Min(nChunks - 1, Mathf.FloorToInt((local.x + Radius) / ChunkSize));
            int cz0 = Mathf.Max(0, Mathf.FloorToInt((local.z - Radius) / ChunkSize));
            int cz1 = Mathf.Min(nChunks - 1, Mathf.FloorToInt((local.z + Radius) / ChunkSize));
            // nächste Stücke zuerst (vorher in Rasterreihenfolge: Gras direkt am Auto kam teils zuletzt)
            _todo.Clear();
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    var c = new Vector2Int(cx, cz);
                    if (_chunks.ContainsKey(c)) continue;
                    float d = ChunkDistance(c, local);
                    if (d > Radius) continue;
                    _todo.Add((d, c));
                }
            _todo.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var (_, c) in _todo)
            {
                if (built >= MaxChunksPerFrame) break;
                _chunks[c] = BuildChunk(c);
                built++;
            }
            _pending = _todo.Count > built;

            _tmpRemove.Clear();
            foreach (var kv in _chunks)
                if (ChunkDistance(kv.Key, local) > Radius + ChunkSize) _tmpRemove.Add(kv.Key);
            foreach (var k in _tmpRemove)
            {
                var go = _chunks[k];
                if (go != null)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
                    Destroy(go);
                }
                _chunks.Remove(k);
            }
        }

        float ChunkDistance(Vector2Int c, Vector3 local)
        {
            var r = new Rect(c.x * ChunkSize, c.y * ChunkSize, ChunkSize, ChunkSize);
            return WorldLayout.DistanceToRect(new Vector2(local.x, local.z), r);
        }

        GameObject BuildChunk(Vector2Int c)
        {
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var col = new List<Color32>();
            var grassTris = new List<int>();
            var bushTris = new List<int>();

            float x0 = c.x * ChunkSize, z0 = c.y * ChunkSize;
            int steps = Mathf.Max(1, Mathf.RoundToInt(ChunkSize / Spacing));
            int seed = GetInstanceID();
            for (int j = 0; j < steps; j++)
                for (int i = 0; i < steps; i++)
                {
                    int gx = Mathf.RoundToInt(x0 / Spacing) + i, gz = Mathf.RoundToInt(z0 / Spacing) + j;
                    float r0 = MathUtil.Hash01(gx, gz, seed), r1 = MathUtil.Hash01(gx, gz, seed + 1), r2 = MathUtil.Hash01(gx, gz, seed + 2);
                    float x = x0 + (i + r0) * Spacing, z = z0 + (j + r1) * Spacing;
                    if (x < 0 || z < 0 || x >= SectorSize || z >= SectorSize) continue;
                    var t = Types.Resolve(Map.Sample(x, z, SectorSize));
                    if (t == null) continue;

                    float yaw = r2 * Mathf.PI;
                    float y = Heights.Sample(x, z);
                    float r3 = MathUtil.Hash01(gx, gz, seed + 3);
                    if (BushMaterial != null && t.Bush.BitmapIndex >= 0 && r3 < BushChance)
                    {
                        float s = 1f + (MathUtil.Hash01(gx, gz, seed + 4) - 0.5f) * 2f * t.Bush.SizeDelta;
                        float bh = t.Bush.Height * s * SizeScale;
                        AddClump(pos, nrm, uv, col, bushTris, new Vector3(x, y, z), bh * CellAspect(BushMaterial, BushAtlasCells), bh, yaw, t.Bush.BitmapIndex, BushAtlasCells, r0);
                        continue;
                    }
                    if (r3 > Density || t.Grass.BitmapIndex < 0) continue;
                    int idx = t.Grass.BitmapIndex;
                    if (t.Grass.TypeVariation > 0 && MathUtil.Hash01(gx, gz, seed + 5) < t.Grass.VariationProbability) idx = t.Grass.TypeVariation;
                    float sz = 1f + (MathUtil.Hash01(gx, gz, seed + 6) - 0.5f) * 2f * t.Grass.SizeDelta;
                    // Höhe aus grasstypes.xml, Breite aus dem Seitenverhältnis der Atlas-Zelle (sonst wirkt das Gras gestaucht;
                    // "Width" in der XML ist die Streubreite des Büschels, nicht die Kartenbreite)
                    float gh = t.Grass.Height * sz * SizeScale;
                    AddClump(pos, nrm, uv, col, grassTris, new Vector3(x, y, z), gh * CellAspect(GrassMaterial, GrassAtlasCells), gh, yaw, idx, GrassAtlasCells, r1);
                }

            var go = new GameObject($"Grass_{c.x}_{c.y}");
            go.transform.SetParent(transform, false);
            if (pos.Count == 0) return go;
            var mesh = new Mesh { name = go.name };
            if (pos.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            mesh.SetNormals(nrm);
            mesh.SetUVs(0, uv);
            // UV2 = Position in der Colormap des Sektors (wie das Terrain: Rastermeter / Ausdehnung)
            float ext = Heights.Extent > 0f ? Heights.Extent : SectorSize;
            var uv2 = new List<Vector2>(pos.Count);
            foreach (var p in pos) uv2.Add(new Vector2(p.x / ext, p.z / ext));
            mesh.SetUVs(1, uv2);
            mesh.SetColors(col);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(grassTris, 0);
            mesh.SetTriangles(bushTris, 1);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { GrassMaterial, BushMaterial != null ? BushMaterial : GrassMaterial };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            if (GroundTex != null)
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                _mpb.Clear();
                _mpb.SetTexture("_GroundTex", GroundTex);
                _mpb.SetFloat("_GroundOn", 1f);
                if (SplatTex != null) { _mpb.SetTexture("_SplatTex", SplatTex); _mpb.SetFloat("_UseSplat", 1f); _mpb.SetFloat("_ShadowStrength", ShadowStrength); }
                mr.SetPropertyBlock(_mpb);
            }
            return go;
        }

        static float CellAspect(Material m, int cells)
        {
            var t = m != null ? m.mainTexture : null;
            if (t == null || t.height <= 0 || cells <= 0) return 1.5f;
            return Mathf.Clamp((float)t.width / cells / t.height, 0.25f, 4f);
        }

        static void AddClump(List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv, List<Color32> col, List<int> tris,
            Vector3 p, float width, float height, float yaw, int atlasIndex, int cells, float tint)
        {
            float u0 = (float)atlasIndex / cells, u1 = (float)(atlasIndex + 1) / cells;
            byte shade = (byte)(200 + tint * 55);
            for (int q = 0; q < 2; q++)
            {
                float a = yaw + q * Mathf.PI * 0.5f;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (width * 0.5f);
                int b = pos.Count;
                pos.Add(p - dir); pos.Add(p + dir); pos.Add(p - dir + Vector3.up * height); pos.Add(p + dir + Vector3.up * height);
                for (int k = 0; k < 4; k++) { nrm.Add(Vector3.up); col.Add(new Color32(shade, shade, shade, 255)); }
                // D3D-Bildzeile 0 liegt in Unity bei v=0 -> Spitze (Bild oben) = v 0, Fuß = v 1
                uv.Add(new Vector2(u0, 1)); uv.Add(new Vector2(u1, 1)); uv.Add(new Vector2(u0, 0)); uv.Add(new Vector2(u1, 0));
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        void OnDestroy()
        {
            foreach (var go in _chunks.Values)
            {
                if (go == null) continue;
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            }
            _chunks.Clear();
        }
    }
}
