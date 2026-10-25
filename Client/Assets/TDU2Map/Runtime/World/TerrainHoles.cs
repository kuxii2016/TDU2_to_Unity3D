using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.World
{
    /// <summary>
    /// Terrain-Löcher an Tunnelportalen (Diamond-Head-Krater, Tunnel auf Ibiza ...).
    /// Das Höhenraster kennt keine Löcher (regelmäßiges 257²-Gitter ohne Indexbuffer), an den Portalen
    /// liegt das Terrain über der Tunnelfahrbahn. Hier werden alle 3-m-Zellen ausgeschnitten, deren Terrain
    /// in den Tunnelquerschnitt ragt (zwischen Boden und Dach) oder als dünne Decke direkt über dem Dach liegt.
    /// Tief im Berg (Terrain deutlich über dem Dach) bleibt das Terrain geschlossen.
    /// Tunnelgeometrie = Straßenmaterial mit "Tunnel" im Namen oder mit LIGHT-Layer (Tunnelbeleuchtung);
    /// diese Meshes enthalten Boden UND Wände/Decke (Röhre), daher werden Boden-/Dachhöhe getrennt erfasst.
    /// Thread-sicher (keine Unity-Objekte).
    /// </summary>
    public static class TerrainHoles
    {
        /// <summary>Wie weit über dem Tunnel-Dach die dünne Decke noch als Portalbereich gilt.</summary>
        public static float Clearance = 7.5f;
        /// <summary>Toleranz über dem Tunnel-Boden (Zellen darunter = Fahrbahnbett, bleibt).</summary>
        public static float MinAbove = 0.25f;
        /// <summary>Wie viele Zellringe um den Schnitt die Flanken mitgeschnitten werden (Rand-Stacheln).</summary>
        public static int FlankRings = 2;
        /// <summary>Flanken nur bei tiefen Tunneln: Terrain muss mind. so weit über dem Dach liegen (flache Kuppen bleiben).</summary>
        public static float DeepCover = 12f;
        /// <summary>Portal-Mund: so weit über der Fahrbahn wird an Portal-Objekten geschnitten (Decke bleibt sonst).</summary>
        public static float PortalHeight = 9f;
        /// <summary>Kollisions-Korridor an flachen Tunneln: so viele Zellringe über den Tunnelrand hinaus freiräumen (Portal-Zufahrt).</summary>
        public static int DriveFlankRings = 3;
        static readonly ulong SemLight = Bin.KeyFromString("LIGHT");

        public static bool IsTunnelRoad(MaterialDef def)
        {
            if (def == null || !def.IsRoad) return false;
            if (def.Name != null && def.Name.IndexOf("tunnel", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return def.TryGetLayer(SemLight, out var l) && l.TextureKey != 0;
        }

        /// <summary>
        /// Sammelt alle Tunnel-Dreiecke (Boden + Wände/Decke) eines Objektsatzes in Bank-lokalen Koordinaten.
        /// Rückgabe als flache Dreiecksliste (je 3 Punkte ein Dreieck) für Rasterisierung/Weitergabe an Nachbarn.
        /// </summary>
        public static List<Vector3> ExtractTunnelTriangles(IEnumerable<ObjectSet> sets)
        {
            var tri = new List<Vector3>();
            if (sets == null) return tri;
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
                                tri.Add(full.MultiplyPoint3x4(md.Positions[tris[t]]));
                                tri.Add(full.MultiplyPoint3x4(md.Positions[tris[t + 1]]));
                                tri.Add(full.MultiplyPoint3x4(md.Positions[tris[t + 2]]));
                            }
                        }
                    }
                }
            }
            return tri;
        }

        /// <summary>
        /// Portal-Objekte (Name enthält "TUN_", aber nicht "NETWORK"): das sind die sichtbaren Tunnel-Fassaden.
        /// Deren Dreiecke liefern die exakte Lage des Munds, auch wenn das Tunnel-Material ein paar Meter daneben liegt.
        /// </summary>
        public static List<Vector3> ExtractPortalTriangles(IEnumerable<ObjectSet> sets)
        {
            var tri = new List<Vector3>();
            if (sets == null) return tri;
            foreach (var set in sets)
            {
                var scene = set?.Scene;
                if (scene == null) continue;
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
                    string nm = Bin.KeyToString(ins.Lods[0].ObjKey);
                    if (nm == null || nm.IndexOf("TUN_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (nm.IndexOf("NETWORK", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!set.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                    for (int s = 0; s < md.SubMeshes.Count; s++)
                    {
                        var tris = md.SubMeshes[s];
                        foreach (var pm in placements)
                        {
                            var full = pm * m;
                            for (int t = 0; t + 2 < tris.Count; t += 3)
                            {
                                tri.Add(full.MultiplyPoint3x4(md.Positions[tris[t]]));
                                tri.Add(full.MultiplyPoint3x4(md.Positions[tris[t + 1]]));
                                tri.Add(full.MultiplyPoint3x4(md.Positions[tris[t + 2]]));
                            }
                        }
                    }
                }
            }
            return tri;
        }

        /// <param name="g">Höhenraster des Sektors</param>
        /// <param name="sets">eigene Objektsätze (Bank-lokal)</param>
        /// <param name="extra">zusätzliche Tunnel-Dreiecke (z.B. aus Nachbarsektoren), bereits in diesem Raster-Raum</param>
        /// <returns>Maske [z * cells + x] (true = ausschneiden) oder null, wenn nichts zu tun ist.</returns>
        /// <param name="driveMask">
        /// Kollisions-Maske: wie <c>mask</c>, entfernt bei flachen Tunneln zusätzlich das gesamte Terrain
        /// über der Fahrbahn im Tunnelverlauf. So bleibt der Berg sichtbar, blockiert aber nicht die Durchfahrt.
        /// </param>
        public static bool[] Compute(HeightGrid g, IEnumerable<ObjectSet> sets, List<Vector3> extra, out bool[] driveMask)
        {
            driveMask = null;
            if (g == null) return null;
            var tri = ExtractTunnelTriangles(sets);
            if (extra != null && extra.Count > 0) tri.AddRange(extra);
            var ptri = ExtractPortalTriangles(sets);
            if (tri.Count < 3 && ptri.Count < 3) return null;

            int cells = g.Size - 1, n = cells * cells;
            var rMin = new float[n];
            var rMax = new float[n];
            var touched = new bool[n];
            for (int i = 0; i < n; i++) { rMin[i] = float.PositiveInfinity; rMax[i] = float.NegativeInfinity; }
            for (int t = 0; t + 2 < tri.Count; t += 3)
                Rasterize(g, rMin, rMax, touched, cells, tri[t], tri[t + 1], tri[t + 2]);

            // Portal-Mund separat rastern: genau die sichtbare Fassade (auch wenn das Tunnel-Material daneben liegt).
            var pMin = new float[n];
            var pMax = new float[n];
            var pTouched = new bool[n];
            for (int i = 0; i < n; i++) { pMin[i] = float.PositiveInfinity; pMax[i] = float.NegativeInfinity; }
            for (int t = 0; t + 2 < ptri.Count; t += 3)
                Rasterize(g, pMin, pMax, pTouched, cells, ptri[t], ptri[t + 1], ptri[t + 2]);

            // Tiefe des Tunnels: liegt das Terrain deutlich über dem Dach, ist es ein echter Bergtunnel
            // (Dach stehen lassen, nur in den Querschnitt schneiden). Sonst flache Decke (Decke schneiden).
            bool deep = false;
            for (int i = 0; i < n && !deep; i++)
            {
                if (!touched[i]) continue;
                int x = i % cells, z = i / cells;
                float m = Mathf.Max(Mathf.Max(g.Get(x, z), g.Get(x + 1, z)), Mathf.Max(g.Get(x, z + 1), g.Get(x + 1, z + 1)));
                if (m - rMax[i] > DeepCover) deep = true;
            }

            bool[] mask = null;
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int i = z * cells + x;
                    if (!touched[i] && !pTouched[i]) continue;
                    float h00 = g.Get(x, z), h10 = g.Get(x + 1, z), h01 = g.Get(x, z + 1), h11 = g.Get(x + 1, z + 1);
                    float cMax = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
                    float cMin = Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h01, h11));
                    bool cut = false;
                    if (pTouched[i])
                        cut = cMax > pMin[i] + MinAbove && cMin < pMin[i] + PortalHeight;   // Portal-Mund öffnen
                    else if (deep)
                        cut = cMax > rMin[i] + MinAbove && cMin < rMax[i];                  // tiefer Tunnel: Dach bleibt
                    // flacher Tunnel ohne Portalobjekt: nichts schneiden
                    if (cut)
                    {
                        if (mask == null) mask = new bool[n];
                        mask[i] = true;
                    }
                }

            // Flanken-Erweiterung: an den Portalrand angrenzende Zellen mitnehmen, solange ihr Terrain
            // UNTER das Tunnel-Dach reicht (entfernt Stacheln am Lochrand, Kuppe/Dach bleibt stehen).
            if (mask != null && FlankRings > 0 && deep)
            {
                var floorArr = (float[])rMin.Clone();
                var roofArr = (float[])rMax.Clone();
                for (int ring = 0; ring < FlankRings; ring++)
                {
                    var add = new List<int>();
                    for (int z = 0; z < cells; z++)
                        for (int x = 0; x < cells; x++)
                        {
                            int i = z * cells + x;
                            if (mask[i]) continue;
                            float floorRef = float.PositiveInfinity, roofRef = float.NegativeInfinity;
                            for (int dz = -1; dz <= 1; dz++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx, nz = z + dz;
                                    if (nx < 0 || nz < 0 || nx >= cells || nz >= cells) continue;
                                    int ni = nz * cells + nx;
                                    if (!mask[ni]) continue;
                                    if (floorArr[ni] < floorRef) floorRef = floorArr[ni];
                                    if (roofArr[ni] > roofRef) roofRef = roofArr[ni];
                                }
                            if (float.IsPositiveInfinity(floorRef)) continue;
                            float h00 = g.Get(x, z), h10 = g.Get(x + 1, z), h01 = g.Get(x, z + 1), h11 = g.Get(x + 1, z + 1);
                            float cMax = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
                            float cMin = Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h01, h11));
                            if (cMax > floorRef + MinAbove && cMin < roofRef)
                            {
                                add.Add(i);
                                floorArr[i] = floorRef;
                                roofArr[i] = roofRef;
                            }
                        }
                    if (add.Count == 0) break;
                    foreach (var i in add) mask[i] = true;
                }
            }

            // Kollisions-Maske: bei flachen Tunneln zusaetzlich das Terrain ueber der ganzen Fahrbahn entfernen,
            // damit der Berg nicht als Wand wirkt (sichtbar bleibt er ueber die Sicht-Maske stehen).
            if (!deep)
            {
                for (int z = 0; z < cells; z++)
                    for (int x = 0; x < cells; x++)
                    {
                        int i = z * cells + x;
                        if (!touched[i]) continue;
                        float h00 = g.Get(x, z), h10 = g.Get(x + 1, z), h01 = g.Get(x, z + 1), h11 = g.Get(x + 1, z + 1);
                        float cMax = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
                        if (cMax <= rMin[i] + MinAbove) continue;
                        if (driveMask == null) driveMask = mask != null ? (bool[])mask.Clone() : new bool[n];
                        driveMask[i] = true;
                    }

                // Kollisions-Korridor ein paar Zellen über den Tunnelrand hinaus verlängern, bis das Terrain
                // auf Fahrbahnniveau abfällt (öffnet die Zufahrt am Portal, wo die Straße unter das Terrain taucht).
                if (driveMask != null && DriveFlankRings > 0)
                {
                    var floorArr = (float[])rMin.Clone();
                    for (int ring = 0; ring < DriveFlankRings; ring++)
                    {
                        var add = new List<int>();
                        for (int z = 0; z < cells; z++)
                            for (int x = 0; x < cells; x++)
                            {
                                int i = z * cells + x;
                                if (driveMask[i]) continue;
                                float floorRef = float.PositiveInfinity;
                                for (int dz = -1; dz <= 1; dz++)
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        int nx = x + dx, nz = z + dz;
                                        if (nx < 0 || nz < 0 || nx >= cells || nz >= cells) continue;
                                        int ni = nz * cells + nx;
                                        if (!driveMask[ni]) continue;
                                        if (floorArr[ni] < floorRef) floorRef = floorArr[ni];
                                    }
                                if (float.IsPositiveInfinity(floorRef)) continue;
                                float h00 = g.Get(x, z), h10 = g.Get(x + 1, z), h01 = g.Get(x, z + 1), h11 = g.Get(x + 1, z + 1);
                                float cMax = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
                                if (cMax > floorRef + MinAbove)
                                {
                                    add.Add(i);
                                    floorArr[i] = floorRef;
                                }
                            }
                        if (add.Count == 0) break;
                        foreach (var i in add) driveMask[i] = true;
                    }
                }
            }
            if (driveMask == null && mask != null) driveMask = (bool[])mask.Clone();
            return mask;
        }

        static void Rasterize(HeightGrid g, float[] rMin, float[] rMax, bool[] touched, int cells, Vector3 a, Vector3 b, Vector3 c)
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
                        int idx = z * cells + x;
                        touched[idx] = true;
                        if (roadY > rMax[idx]) rMax[idx] = roadY;
                        if (roadY < rMin[idx]) rMin[idx] = roadY;
                    }
                }
        }
    }
}
