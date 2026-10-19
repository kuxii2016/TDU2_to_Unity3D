using System;
using UnityEngine;

namespace TDU2.Map.World
{
    public readonly struct AreaKey : IEquatable<AreaKey>
    {
        public readonly int X, Z;
        public AreaKey(int x, int z) { X = x; Z = z; }
        public bool Equals(AreaKey o) => X == o.X && Z == o.Z;
        public override bool Equals(object obj) => obj is AreaKey o && Equals(o);
        public override int GetHashCode() => X * 397 ^ Z;
        public override string ToString() => $"area-{X}-{Z}";
    }

    /// <summary>Sektor "sector-A-B-C-D": A/B = Area, C = Spalte (X), D = Zeile (Z) innerhalb der Area.</summary>
    public readonly struct SectorKey : IEquatable<SectorKey>
    {
        public readonly int A, B, C, D;
        public SectorKey(int a, int b, int c, int d) { A = a; B = b; C = c; D = d; }
        public AreaKey Area => new AreaKey(A, B);
        public bool Equals(SectorKey o) => A == o.A && B == o.B && C == o.C && D == o.D;
        public override bool Equals(object obj) => obj is SectorKey o && Equals(o);
        public override int GetHashCode() => ((A * 31 + B) * 31 + C) * 31 + D;
        public override string ToString() => $"sector-{A}-{B}-{C}-{D}";
    }

    /// <summary>
    /// Koordinatensystem (Spielkoordinaten = Unity-Koordinaten, Y oben, linkshändig wie D3D):
    ///   Insel-Raster : 16×16 Areas à 6144 m (Insel-LOD 257² Höhen, 384 m Abstand, Ursprung 0/0)
    ///   Area A-B     : Ursprung (A*6144, B*6144), 8×8 Sektoren
    ///   Sektor C-D   : Ursprung (A*6144 + C*768 + (D ungerade ? 384 : 0), B*6144 + D*768)
    ///                  -> Sektorreihen mit ungeradem D sind um einen halben Sektor nach +X versetzt
    ///                     (Ziegelmuster). Sektor C=7 einer ungeraden Reihe ragt 384 m in die nächste Area.
    /// </summary>
    public static class WorldLayout
    {
        public const float AreaSize = 6144f;
        public const float SectorSize = 768f;
        public const int SectorsPerArea = 8;
        public const float StaggerX = 384f;
        public const float IslandGridSize = 98304f; // 16 Areas
        public const float IslandSpacing = 384f, AreaSpacing = 24f, SectorSpacing = 3f;

        public static Vector3 AreaOrigin(AreaKey a) => new Vector3(a.X * AreaSize, 0, a.Z * AreaSize);

        public static Vector3 SectorOrigin(SectorKey k) =>
            new Vector3(k.A * AreaSize + k.C * SectorSize + ((k.D & 1) != 0 ? StaggerX : 0f), 0, k.B * AreaSize + k.D * SectorSize);

        public static Rect SectorRect(SectorKey k)
        {
            var o = SectorOrigin(k);
            return new Rect(o.x, o.z, SectorSize, SectorSize);
        }

        public static Rect AreaRect(AreaKey a) => new Rect(a.X * AreaSize, a.Z * AreaSize, AreaSize, AreaSize);

        /// <summary>Welcher Sektor enthält die Weltposition (x,z)?</summary>
        public static SectorKey SectorAt(float x, float z)
        {
            int b = Mathf.FloorToInt(z / AreaSize);
            int d = Mathf.FloorToInt((z - b * AreaSize) / SectorSize);
            d = Mathf.Clamp(d, 0, SectorsPerArea - 1);
            float xs = x - ((d & 1) != 0 ? StaggerX : 0f);
            int a = Mathf.FloorToInt(xs / AreaSize);
            int c = Mathf.Clamp(Mathf.FloorToInt((xs - a * AreaSize) / SectorSize), 0, SectorsPerArea - 1);
            return new SectorKey(a, b, c, d);
        }

        public static AreaKey AreaAt(float x, float z) => new AreaKey(Mathf.FloorToInt(x / AreaSize), Mathf.FloorToInt(z / AreaSize));

        /// <summary>Kürzeste Distanz (XZ) eines Punktes zu einem Rechteck.</summary>
        public static float DistanceToRect(Vector2 p, Rect r)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
