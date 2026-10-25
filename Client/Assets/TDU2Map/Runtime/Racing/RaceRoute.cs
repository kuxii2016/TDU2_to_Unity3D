using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Baut die Geometrie der Rennstrecke für die Minimap: ein flaches Band (Ribbon) entlang der Checkpoints
    /// in Karten-/Inselkoordinaten (XZ-Ebene). Der bereits befahrene Teil (bis <c>nextIndex</c>) wird anders
    /// eingefärbt als der bevorstehende, das nächste Tor hervorgehoben.
    ///
    /// Reine Geometrie – das Zeichnen in die Minimap-RenderTexture macht <see cref="TDU2.Map.Vehicles.TDU2Minimap"/>.
    /// </summary>
    public static class RaceRoute
    {
        /// <summary>Halbe Bandbreite in Metern (Prüfungen sind kurz und eng – schmal halten, sonst verdeckt das Band die Karte).</summary>
        public static float HalfWidth = 4f;
        /// <summary>Höchstzahl Zwischenpunkte je Segment (Kurvenglättung).</summary>
        public static int Subdiv = 12;
        /// <summary>Höhe über der Straßenebene der Minimap (gleiche Ebene + gleiches Material = Z-Fighting/Flackern).</summary>
        public static float LiftY = 3f;
        /// <summary>Mindestabstand der Punkte der Straßenroute (dichte Spurpunkte erzeugen Falten im Band).</summary>
        public static float MinStep = 3f;
        /// <summary>Aktuelle Straßenroute (gesetzt beim Start einer Prüfung, <see cref="RaceRoadPath"/>); null = durch die Tore glätten.</summary>
        public static List<Vector3> RoadPath;
        public static List<int> RoadPathSeg;
        public static readonly Color32 PassedColor = new Color32(150, 80, 10, 150);    // befahren: dunkles Orange
        public static readonly Color32 RouteColor = new Color32(255, 140, 0, 240);     // bevorstehend: orange (wie das GPS im Spiel)
        public static readonly Color32 NextColor = new Color32(255, 165, 20, 255);     // aktueller Abschnitt: helles Orange

        /// <summary>
        /// Erzeugt das Band als zusammenhängenden Streifen (Dreiecke je Segment).
        /// <paramref name="cps"/> sind die Checkpoints in Reihenfolge (Index 0 = Startlinie).
        /// <paramref name="nextIndex"/> = Index des nächsten anzufahrenden Tors (Fortschritt).
        /// </summary>
        public static void Build(IList<RaceCheckpoint> cps, int nextIndex,
            out Vector3[] vertices, out Color32[] colors, out int[] triangles)
        {
            if (cps == null || cps.Count < 2)
            {
                vertices = new Vector3[0]; colors = new Color32[0]; triangles = new int[0];
                return;
            }

            var pts = new List<Vector2>(); var seg = new List<int>();
            bool useRoad = RoadPath != null && RoadPath.Count > 1 && RoadPathSeg != null && RoadPathSeg.Count == RoadPath.Count && RoadPath.Count < 30000;
            if (useRoad)
            {
                // Route entlang der Straßen (RaceRoadPath)
                for (int i = 0; i < RoadPath.Count; i++)
                {
                    var q = new Vector2(RoadPath[i].x, RoadPath[i].z);
                    if (pts.Count > 0 && i < RoadPath.Count - 1 && (q - pts[pts.Count - 1]).sqrMagnitude < MinStep * MinStep) continue;
                    pts.Add(q); seg.Add(Mathf.Max(0, RoadPathSeg[i]));
                }
            }
            else
                // Kurven glätten: Catmull-Rom durch die Tore (Unterteilung je Segment), Farbe nach dem Segment des Ausgangstors
                for (int i = 0; i < cps.Count - 1; i++)
                {
                    Vector2 P(int k) { k = Mathf.Clamp(k, 0, cps.Count - 1); return new Vector2(cps[k].Position.x, cps[k].Position.z); }
                    Vector2 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
                    int sub = Mathf.Clamp(Mathf.CeilToInt((p2 - p1).magnitude / 4f), 1, Subdiv);
                    for (int s = 0; s < sub; s++)
                    {
                        float u = s / (float)sub, u2 = u * u, u3 = u2 * u;
                        pts.Add(0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3));
                        seg.Add(i);
                    }
                }
            if (!useRoad) { pts.Add(new Vector2(cps[cps.Count - 1].Position.x, cps[cps.Count - 1].Position.z)); seg.Add(cps.Count - 2); }

            int n = pts.Count;
            var v = new List<Vector3>(n * 2);
            var c = new List<Color32>(n * 2);
            var t = new List<int>((n - 1) * 6);

            for (int i = 0; i < n; i++)
            {
                Vector2 cur = pts[i];
                Vector2 prev = i > 0 ? pts[i - 1] : cur;
                Vector2 next = i < n - 1 ? pts[i + 1] : cur;

                Vector2 dir = (next - prev);
                if (dir.sqrMagnitude < 1e-6f) dir = next - cur;
                if (dir.sqrMagnitude < 1e-6f) dir = new Vector2(0f, 1f);
                dir = dir.normalized;
                Vector2 side = new Vector2(-dir.y, dir.x) * HalfWidth;

                Color32 col = SegmentColor(seg[i], nextIndex);
                v.Add(new Vector3(cur.x - side.x, LiftY, cur.y - side.y)); c.Add(col);
                v.Add(new Vector3(cur.x + side.x, LiftY, cur.y + side.y)); c.Add(col);

                if (i > 0)
                {
                    int b = (i - 1) * 2;
                    t.Add(b); t.Add(b + 1); t.Add(b + 2);
                    t.Add(b + 1); t.Add(b + 3); t.Add(b + 2);
                }
            }

            vertices = v.ToArray(); colors = c.ToArray(); triangles = t.ToArray();
        }

        static Color32 SegmentColor(int i, int nextIndex)
        {
            // i = Index des Segments zwischen Checkpoint i und i+1; nextIndex = als Nächstes anzufahrendes Tor.
            if (i == nextIndex - 1) return NextColor;    // gerade befahrenes Segment
            if (i < nextIndex - 1) return PassedColor;   // bereits befahren
            return RouteColor;                            // noch bevorstehend
        }
    }
}
