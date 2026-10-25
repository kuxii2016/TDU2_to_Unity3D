using UnityEngine;

namespace TDU2
{
    /// <summary>Thread-sichere Mathe-Helfer (keine nativen Unity-Aufrufe).</summary>
    public static class MathUtil
    {
        /// <summary>Entspricht Quaternion.Euler (Unity-Reihenfolge Z, X, Y), aber rein managed.</summary>
        public static Quaternion Euler(float xDeg, float yDeg, float zDeg)
        {
            float d2r = Mathf.Deg2Rad * 0.5f;
            float cx = Mathf.Cos(xDeg * d2r), sx = Mathf.Sin(xDeg * d2r);
            float cy = Mathf.Cos(yDeg * d2r), sy = Mathf.Sin(yDeg * d2r);
            float cz = Mathf.Cos(zDeg * d2r), sz = Mathf.Sin(zDeg * d2r);
            var qx = new Quaternion(sx, 0, 0, cx);
            var qy = new Quaternion(0, sy, 0, cy);
            var qz = new Quaternion(0, 0, sz, cz);
            return qy * qx * qz;
        }

        /// <summary>TRS-Matrix rein managed.</summary>
        public static Matrix4x4 TRS(Vector3 t, Quaternion q, Vector3 s)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            float xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
            var m = new Matrix4x4();
            m.m00 = (1 - 2 * (yy + zz)) * s.x; m.m01 = (2 * (xy - wz)) * s.y; m.m02 = (2 * (xz + wy)) * s.z; m.m03 = t.x;
            m.m10 = (2 * (xy + wz)) * s.x; m.m11 = (1 - 2 * (xx + zz)) * s.y; m.m12 = (2 * (yz - wx)) * s.z; m.m13 = t.y;
            m.m20 = (2 * (xz - wy)) * s.x; m.m21 = (2 * (yz + wx)) * s.y; m.m22 = (1 - 2 * (xx + yy)) * s.z; m.m23 = t.z;
            m.m30 = 0; m.m31 = 0; m.m32 = 0; m.m33 = 1;
            return m;
        }

        /// <summary>Deterministischer Hash -> [0,1).</summary>
        public static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
