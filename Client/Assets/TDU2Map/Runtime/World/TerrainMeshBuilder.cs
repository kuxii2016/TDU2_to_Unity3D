using System.Collections.Generic;
using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map.World
{
    /// <summary>Ein Terrainstück (Teilrechteck eines Höhenrasters), zugeordnet zu einem Sektor oder einer Area.</summary>
    public sealed class TerrainPiece
    {
        public SectorKey Sector;       // Sektor, dem das Stück gehört (LOD1-Stücke)
        public AreaKey Area;           // Area (LOD2-Stücke)
        public Vector3 WorldOrigin;    // Weltposition der Stückecke
        public MeshData Mesh;
    }

    /// <summary>
    /// Baut Gitter-Meshes aus Höhenrastern (thread-sicher, liefert MeshData).
    ///   LOD0 = kompletter Sektor (257² Vertices, 3 m)
    ///   LOD1 = Sektor-Footprints aus dem Area-Raster (24 m), inkl. halber Stücke am Rand ungerader Reihen
    ///   LOD2 = Area-Footprints aus dem Insel-Raster (384 m)
    /// Alle Stücke bekommen "Skirts" (nach unten gezogene Ränder), damit zwischen LOD-Stufen keine Risse sichtbar sind.
    /// </summary>
    public static class TerrainMeshBuilder
    {
        /// <param name="g">Höhenraster</param>
        /// <param name="x0">erste Spalte (Sample-Index)</param>
        /// <param name="z0">erste Zeile</param>
        /// <param name="nx">Anzahl Zellen in X</param>
        /// <param name="nz">Anzahl Zellen in Z</param>
        /// <param name="uvExtent">Rasterlokale Meter, die UV 0..1 entsprechen (Colormap-Abdeckung)</param>
        /// <param name="skirt">Skirt-Tiefe in Metern (0 = keine)</param>
        public static MeshData BuildGrid(HeightGrid g, int x0, int z0, int nx, int nz, Vector2 uvExtent, float skirt, string name, bool[] holes = null)
        {
            var md = new MeshData { Name = name };
            float sp = g.Spacing;
            int vx = nx + 1, vz = nz + 1;
            for (int j = 0; j < vz; j++)
            {
                for (int i = 0; i < vx; i++)
                {
                    int gx = x0 + i, gz = z0 + j;
                    float h = g.Get(gx, gz);
                    md.Positions.Add(new Vector3(i * sp, h, j * sp));
                    md.Normals.Add(g.NormalAt(gx, gz));
                    md.UV0.Add(new Vector2(gx * sp / uvExtent.x, gz * sp / uvExtent.y));
                }
            }
            var tris = new List<int>(nx * nz * 6 + (skirt > 0 ? (nx + nz) * 24 : 0));
            for (int j = 0; j < nz; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    // Loch (Tunnelportal) -> Zelle auslassen; Maske ist auf das ganze Raster bezogen
                    if (holes != null)
                    {
                        int hc = g.Size - 1, hx = x0 + i, hz = z0 + j;
                        if (hx < hc && hz < hc && holes[hz * hc + hx]) continue;
                    }
                    int v00 = j * vx + i, v10 = v00 + 1, v01 = v00 + vx, v11 = v01 + 1;
                    // Uhrzeigersinn von oben gesehen (Unity-Frontface)
                    tris.Add(v00); tris.Add(v01); tris.Add(v10);
                    tris.Add(v10); tris.Add(v01); tris.Add(v11);
                }
            }

            if (skirt > 0f)
            {
                AddSkirt(md, tris, vx, vz, skirt, Edge.Bottom, holes, x0, z0, nx, nz, g.Size - 1);
                AddSkirt(md, tris, vx, vz, skirt, Edge.Top, holes, x0, z0, nx, nz, g.Size - 1);
                AddSkirt(md, tris, vx, vz, skirt, Edge.Left, holes, x0, z0, nx, nz, g.Size - 1);
                AddSkirt(md, tris, vx, vz, skirt, Edge.Right, holes, x0, z0, nx, nz, g.Size - 1);
            }
            md.SubMeshes.Add(tris);
            md.SubMeshMaterialIndex.Add(0);
            return md;
        }

        enum Edge { Bottom, Top, Left, Right }

        static void AddSkirt(MeshData md, List<int> tris, int vx, int vz, float depth, Edge edge,
                             bool[] holes, int x0, int z0, int nx, int nz, int cellSize)
        {
            int count = (edge == Edge.Bottom || edge == Edge.Top) ? vx : vz;
            int start = md.Positions.Count;
            var skip = new bool[count - 1];
            if (holes != null)
                for (int k = 0; k < count - 1; k++)
                {
                    int ci, cj;
                    switch (edge)
                    {
                        case Edge.Bottom: ci = k; cj = 0; break;
                        case Edge.Top: ci = k; cj = nz - 1; break;
                        case Edge.Left: ci = 0; cj = k; break;
                        default: ci = nx - 1; cj = k; break;
                    }
                    int hx = x0 + ci, hz = z0 + cj;
                    if (hx < cellSize && hz < cellSize && holes[hz * cellSize + hx]) skip[k] = true;
                }
            for (int k = 0; k < count; k++)
            {
                int src = EdgeIndex(edge, k, vx, vz);
                var p = md.Positions[src];
                md.Positions.Add(new Vector3(p.x, p.y - depth, p.z));
                md.Normals.Add(md.Normals[src]);
                md.UV0.Add(md.UV0[src]);
            }
            for (int k = 0; k < count - 1; k++)
            {
                if (skip[k]) continue;   // kein Skirt quer über einer Portalöffnung
                int a = EdgeIndex(edge, k, vx, vz), b = EdgeIndex(edge, k + 1, vx, vz);
                int c = start + k, d = start + k + 1;
                // beidseitig, damit die Wicklung egal ist
                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(b); tris.Add(d); tris.Add(c);
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        }

        static int EdgeIndex(Edge e, int k, int vx, int vz)
        {
            switch (e)
            {
                case Edge.Bottom: return k;
                case Edge.Top: return (vz - 1) * vx + k;
                case Edge.Left: return k * vx;
                default: return k * vx + (vx - 1);
            }
        }

        /// <summary>LOD0: kompletter Sektor.</summary>
        public static MeshData BuildSector(HeightGrid g, float skirt, bool[] holes = null)
        {
            int n = g.Size - 1;
            return BuildGrid(g, 0, 0, n, n, new Vector2(g.Extent, g.Extent), skirt, "SectorTerrain", holes);
        }

        /// <summary>
        /// LOD1: zerlegt ein Area-Raster (257², 24 m) in Sektor-Footprints (32×32 Zellen, ungerade Reihen um 16 Zellen versetzt).
        /// </summary>
        public static List<TerrainPiece> BuildAreaPieces(HeightGrid g, AreaKey area, float skirt)
        {
            var list = new List<TerrainPiece>();
            int cellsPerSector = Mathf.RoundToInt(WorldLayout.SectorSize / g.Spacing); // 32
            int half = cellsPerSector / 2;
            var uvExt = new Vector2(g.Extent, g.Extent);
            var areaOrigin = WorldLayout.AreaOrigin(area);
            for (int d = 0; d < WorldLayout.SectorsPerArea; d++)
            {
                int z0 = d * cellsPerSector;
                if ((d & 1) == 0)
                {
                    for (int c = 0; c < WorldLayout.SectorsPerArea; c++)
                        list.Add(Piece(g, c * cellsPerSector, z0, cellsPerSector, cellsPerSector, uvExt, skirt, new SectorKey(area.X, area.Z, c, d), areaOrigin));
                }
                else
                {
                    // linker halber Streifen gehört zu Sektor 7 der vorherigen Area
                    list.Add(Piece(g, 0, z0, half, cellsPerSector, uvExt, skirt, new SectorKey(area.X - 1, area.Z, 7, d), areaOrigin));
                    for (int c = 0; c < WorldLayout.SectorsPerArea - 1; c++)
                        list.Add(Piece(g, half + c * cellsPerSector, z0, cellsPerSector, cellsPerSector, uvExt, skirt, new SectorKey(area.X, area.Z, c, d), areaOrigin));
                    // rechter halber Streifen = linke Hälfte von Sektor 7 dieser Area
                    list.Add(Piece(g, half + 7 * cellsPerSector, z0, half, cellsPerSector, uvExt, skirt, new SectorKey(area.X, area.Z, 7, d), areaOrigin));
                }
            }
            return list;
        }

        static TerrainPiece Piece(HeightGrid g, int x0, int z0, int nx, int nz, Vector2 uvExt, float skirt, SectorKey key, Vector3 gridOrigin)
        {
            return new TerrainPiece
            {
                Sector = key,
                Area = key.Area,
                WorldOrigin = gridOrigin + new Vector3(x0 * g.Spacing, 0, z0 * g.Spacing),
                Mesh = BuildGrid(g, x0, z0, nx, nz, uvExt, skirt, key.ToString() + "_LOD1"),
            };
        }

        /// <summary>LOD2: Area-Footprints aus dem Insel-Raster. Colormap deckt areasX×areasZ Areas ab.</summary>
        public static List<TerrainPiece> BuildIslandPieces(HeightGrid g, int areasX, int areasZ, float skirt)
        {
            var list = new List<TerrainPiece>();
            int cellsPerArea = Mathf.RoundToInt(WorldLayout.AreaSize / g.Spacing); // 16
            var uvExt = new Vector2(Mathf.Max(1, areasX) * WorldLayout.AreaSize, Mathf.Max(1, areasZ) * WorldLayout.AreaSize);
            for (int b = 0; b < areasZ; b++)
                for (int a = 0; a < areasX; a++)
                {
                    int x0 = a * cellsPerArea, z0 = b * cellsPerArea;
                    if (x0 + cellsPerArea >= g.Size || z0 + cellsPerArea >= g.Size) continue;
                    var key = new AreaKey(a, b);
                    list.Add(new TerrainPiece
                    {
                        Area = key,
                        WorldOrigin = new Vector3(x0 * g.Spacing, 0, z0 * g.Spacing),
                        Mesh = BuildGrid(g, x0, z0, cellsPerArea, cellsPerArea, uvExt, skirt, key + "_LOD2"),
                    });
                }
            return list;
        }
    }
}
