using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.World
{
    /// <summary>
    /// Terrain-Löcher an Tunnelportalen (Diamond-Head-Krater, Tunnel auf Ibiza ...).
    /// Das Höhenraster selbst kennt keine Löcher (regelmäßiges 257²-Gitter ohne Indexbuffer), an den Portalen
    /// liegt das Terrain einfach über der Tunnelfahrbahn. Hier werden alle 3-m-Zellen ausgeblendet, die über einem
    /// Tunnel-Fahrbahndreieck liegen und dabei höchstens <see cref="Clearance"/> Meter über der Fahrbahn sind
    /// (= Portalöffnung). Weiter im Berg (Terrain deutlich höher als die Tunneldecke) bleibt das Terrain geschlossen.
    /// Tunnel-Fahrbahn = Straßenmaterial mit "Tunnel" im Namen oder mit LIGHT-Layer (Tunnelbeleuchtung).
    /// Thread-sicher (keine Unity-Objekte).
    /// </summary>
    public static class TerrainHoles
    {
        public static float Clearance = 7.5f;
        public static float MinAbove = 0.25f;
        static readonly ulong SemLight = Bin.KeyFromString("LIGHT");

        public static bool IsTunnelRoad(MaterialDef def)
        {
            if (def == null || !def.IsRoad) return false;
            if (def.Name != null && def.Name.IndexOf("tunnel", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return def.TryGetLayer(SemLight, out var l) && l.TextureKey != 0;
        }

        /// <param name="placement">Bank-Ursprung -> Terrain-Ursprung (Sektoren: Identität)</param>
        /// <returns>Maske [z * cells + x] (true = Loch) oder null, wenn nichts zu tun ist.</returns>
        public static bool[] Compute(HeightGrid g, IEnumerable<ObjectSet> sets)
        {
            if (g == null || sets == null) return null;
            int cells = g.Size - 1;
            bool[] mask = null;
            foreach (var set in sets)
            {
                var scene = set?.Scene;
                if (scene == null || set.Materials == null) continue;
                var byOffset = new Dictionary<int, HieRecord>();
                foreach (var h in scene.Hierarchy) byOffset[h.Offset] = h;
                var leaves = new List<(InsRecord ins, Matrix4x4 m)>();
                if (scene.Hierarchy.Count > 0)
                    foreach (var h in scene.Hierarchy) { var ins = scene.GetIns(h.InsOffset); if (ins.Lods.Length > 0) leaves.Add((ins, scene.WorldMatrix(h, byOffset))); }
                else
                    foreach (var off in scene.InsOffsets) { var ins = scene.GetIns(off); if (ins.Lods.Length > 0) leaves.Add((ins, ins.Matrix)); }

                var placements = set.Placements ?? new List<Matrix4x4> { Matrix4x4.identity };
                foreach (var (ins, m) in leaves)
                {
                    if (!set.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                    var bank = scene.ReadMaterialBank(ins.MaterialBankOffset);
                    for (int s = 0; s < md.SubMeshes.Count; s++)
                    {
                        int mi = md.SubMeshMaterialIndex[s];
                        ulong mk = mi >= 0 && mi < bank.Length ? bank[mi] : 0;
                        if (mk == 0 || !set.Materials.Materials.TryGetValue(mk, out var def) || !IsTunnelRoad(def)) continue;
                        var tris = md.SubMeshes[s];
                        foreach (var pm in placements)
                        {
                            var full = pm * m;
                            for (int t = 0; t + 2 < tris.Count; t += 3)
                            {
                                var a = full.MultiplyPoint3x4(md.Positions[tris[t]]);
                                var b = full.MultiplyPoint3x4(md.Positions[tris[t + 1]]);
                                var c = full.MultiplyPoint3x4(md.Positions[tris[t + 2]]);
                                if (mask == null) mask = new bool[cells * cells];
                                Rasterize(g, mask, cells, a, b, c);
                            }
                        }
                    }
                }
            }
            return mask;
        }

        static void Rasterize(HeightGrid g, bool[] mask, int cells, Vector3 a, Vector3 b, Vector3 c)
        {
            float sp = g.Spacing;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / sp) - 1);
            int x1 = Mathf.Min(cells - 1, Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / sp) + 1);
            int z0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / sp) - 1);
            int z1 = Mathf.Min(cells - 1, Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / sp) + 1);
            if (x0 > x1 || z0 > z1) return;
            float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(det) < 1e-6f) return;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (mask[z * cells + x]) continue;
                    // Zellmitte + Ecken prüfen (schmale Fahrbahnstreifen treffen sonst keine Zellmitte)
                    for (int k = 0; k < 5; k++)
                    {
                        float px = (k == 0 ? x + 0.5f : x + ((k - 1) & 1)) * sp;
                        float pz = (k == 0 ? z + 0.5f : z + (((k - 1) >> 1) & 1)) * sp;
                        float l1 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / det;
                        float l2 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / det;
                        float l3 = 1f - l1 - l2;
                        const float e = -0.02f;
                        if (l1 < e || l2 < e || l3 < e) continue;
                        float roadY = l1 * a.y + l2 * b.y + l3 * c.y;
                        // höchster Eckpunkt der Zelle
                        float h = Mathf.Max(Mathf.Max(g.Get(x, z), g.Get(x + 1, z)), Mathf.Max(g.Get(x, z + 1), g.Get(x + 1, z + 1)));
                        float above = h - roadY;
                        if (above > MinAbove && above < Clearance) { mask[z * cells + x] = true; break; }
                    }
                }
        }
    }
}
