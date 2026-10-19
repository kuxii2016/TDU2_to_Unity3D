using System;
using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>
    /// Textur-Animation (.uva, "ANM!", Docs/tdu2-specs.md §5.5): Spuren Rotation, U-/V-Versatz, U-/V-Skalierung, linear zwischen
    /// den Schlüsseln, Schleife über die Länge. Liefert die Werte für _MainTex_ST (Rotation wird nicht dargestellt).
    /// </summary>
    public sealed class RaceUva
    {
        public string Name;
        public ulong Key;   // +16: Schlüssel, auf den die UVA.-Einträge der .2dm verweisen
        public float Length = 1f;
        readonly float[][] _times = new float[5][];
        readonly float[][] _values = new float[5][];

        public static RaceUva Parse(byte[] d, string name)
        {
            if (d == null || d.Length < 68 || d[0] != 'A' || d[1] != 'N' || d[2] != 'M' || d[3] != '!') return null;
            var u = new RaceUva { Name = name, Key = BitConverter.ToUInt64(d, 16) };
            int p;
            uint mask;
            if (BitConverter.ToUInt32(d, 12) == 160)
            {
                // alte Variante: Länge bei +36, Maske bei +44
                u.Length = BitConverter.ToSingle(d, 36);
                mask = BitConverter.ToUInt32(d, 44);
                p = 52;
                if (p + 4 <= d.Length && d[p] == 'M' && d[p + 1] == 'A') p += 4;
                for (int k = 0; k < 5; k++)
                {
                    if ((mask & (1u << k)) == 0) continue;
                    if (p + 4 > d.Length) break;
                    int n = BitConverter.ToInt32(d, p); p += 4;
                    if (n < 0 || p + n * 8 > d.Length) break;
                    u._times[k] = new float[n]; u._values[k] = new float[n];
                    for (int i = 0; i < n; i++) u._times[k][i] = BitConverter.ToSingle(d, p + i * 4);
                    p += n * 4;
                    for (int i = 0; i < n; i++) u._values[k][i] = BitConverter.ToSingle(d, p + i * 4);
                    p += n * 4;
                }
                return u;
            }
            u.Length = BitConverter.ToSingle(d, 36);
            mask = BitConverter.ToUInt32(d, 56);
            p = 64;
            if (d[p] == 'M' && d[p + 1] == 'A' && d[p + 2] == 'T') p += 4;
            for (int k = 0; k < 5; k++)
            {
                if ((mask & (1u << k)) == 0) continue;
                if (p + 8 > d.Length) break;
                int n = BitConverter.ToInt32(d, p); p += 4;
                p += 4;   // Länge der Spur
                if (n < 0 || p + n * 8 > d.Length) break;
                u._times[k] = new float[n]; u._values[k] = new float[n];
                for (int i = 0; i < n; i++) u._times[k][i] = BitConverter.ToSingle(d, p + i * 4);
                p += n * 4;
                for (int i = 0; i < n; i++) u._values[k][i] = BitConverter.ToSingle(d, p + i * 4);
                p += n * 4;
            }
            if (u.Length <= 0.001f) u.Length = 1f;
            return u;
        }

        float Track(int k, float t, float def)
        {
            var ts = _times[k]; var vs = _values[k];
            if (ts == null || ts.Length == 0) return def;
            if (ts.Length == 1 || t <= ts[0]) return vs[0];
            for (int i = 1; i < ts.Length; i++)
            {
                if (t > ts[i]) continue;
                float span = ts[i] - ts[i - 1];
                return span > 1e-6f ? Mathf.Lerp(vs[i - 1], vs[i], (t - ts[i - 1]) / span) : vs[i];
            }
            return vs[vs.Length - 1];
        }

        /// <summary>Werte zur Zeit time (Sekunden, läuft in Schleife): xy = Skalierung, zw = Versatz (für _MainTex_ST).</summary>
        public Vector4 ST(float time)
        {
            float t = Mathf.Repeat(time, Length);
            return new Vector4(Track(3, t, 1f), Track(4, t, 1f), Track(1, t, 0f), Track(2, t, 0f));
        }
    }

    /// <summary>Spielt .uva-Animationen auf Materialien ab (_MainTex_ST).</summary>
    public sealed class RaceUvaAnimator : MonoBehaviour
    {
        public readonly List<(Material mat, RaceUva anim)> Items = new List<(Material, RaceUva)>();

        void Update()
        {
            float t = Time.time;
            foreach (var (m, a) in Items)
                if (m != null && a != null) m.SetVector("_MainTex_ST", a.ST(t));
        }
    }
}
