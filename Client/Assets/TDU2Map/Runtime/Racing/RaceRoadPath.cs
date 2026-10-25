using System.Collections.Generic;
using TDU2.Traffic;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Route einer Prüfung entlang der Straßen (Verkehrsnetz _ROAD_MAP/_ROAD_DETAIL.IA, <see cref="TrafficLanes"/>):
    /// zwischen zwei Wegpunkten wird über die Spuren der kürzeste Weg gesucht (Dijkstra/A*, Start in beide Richtungen,
    /// Ziel = erste Spur, die am Wegpunkt vorbeiführt). Ergebnis: Polylinie in Inselkoordinaten.
    /// Wird für Minimap-Linie, Fahrbahnmarkierung (Trajectory), Startrichtung und die Gegner benutzt.
    /// Ohne Verkehrsnetz oder ohne Treffer: gerade Verbindung.
    /// </summary>
    public static class RaceRoadPath
    {
        public static float SnapRadius = 30f;     // Wegpunkt -> Straße
        public static float GoalRadius = 18f;     // Spur führt am Ziel vorbei
        public static int MaxExpand = 6000;

        public static TrafficLanes FindLanes()
        {
#if UNITY_2023_1_OR_NEWER
            var t = UnityEngine.Object.FindFirstObjectByType<TDU2Traffic>();
#else
            var t = UnityEngine.Object.FindObjectOfType<TDU2Traffic>();
#endif
            return t != null ? t.Lanes : null;
        }

        /// <summary>Straßenroute durch alle Wegpunkte. seg[i] = Index des Wegpunkt-Abschnitts (0 = erster -> zweiter).</summary>
        public static List<Vector3> Route(TrafficLanes lanes, IList<Vector3> pts, out List<int> seg, out int roadLegs)
        {
            var res = new List<Vector3>(); seg = new List<int>(); roadLegs = 0;
            if (pts == null || pts.Count == 0) return res;
            res.Add(pts[0]); seg.Add(0);
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var leg = lanes != null ? Leg(lanes, pts[i], pts[i + 1]) : null;
                if (leg != null && leg.Count > 1) roadLegs++;
                else leg = new List<Vector3> { pts[i], pts[i + 1] };
                for (int k = 1; k < leg.Count; k++) { res.Add(leg[k]); seg.Add(i); }
            }
            return res;
        }

        sealed class Node
        {
            public LaneTrack Lane; public float SIn; public float G; public Node Prev;
        }

        /// <summary>Weg a -> b über die Spuren (null = keiner gefunden oder Umweg &gt; 4x Luftlinie).</summary>
        static List<Vector3> Leg(TrafficLanes lanes, Vector3 a, Vector3 b)
        {
            float direct = Flat(b - a).magnitude;
            if (direct < 3f) return new List<Vector3> { a, b };
            // Startspuren: alle Spuren (Spur 0 je Richtung) nahe a, ab dem nächsten Punkt
            var open = new List<Node>();
            var best = new Dictionary<LaneTrack, float>();
            foreach (var t in LanesNear(lanes, a, SnapRadius))
            {
                float s = NearestS(t, a, out float d);
                if (d > SnapRadius) continue;
                var n = new Node { Lane = t, SIn = s, G = d };
                open.Add(n);
            }
            if (open.Count == 0) return null;
            var succ = new List<(int road, bool forward)>();
            Node goal = null; float goalS = 0f, goalCost = float.MaxValue;
            int expand = 0;
            while (open.Count > 0 && expand++ < MaxExpand)
            {
                // kleinstes f = g + h
                int bi = 0; float bf = float.MaxValue;
                for (int i = 0; i < open.Count; i++)
                {
                    var o = open[i];
                    float f = o.G + Flat(b - o.Lane.End).magnitude * 0.9f;
                    if (f < bf) { bf = f; bi = i; }
                }
                var cur = open[bi]; open.RemoveAt(bi);
                if (cur.G >= goalCost) break;
                if (best.TryGetValue(cur.Lane, out float bg) && bg <= cur.G) continue;
                best[cur.Lane] = cur.G;
                // Ziel auf dieser Spur (hinter dem Einstieg)?
                float gs = NearestS(cur.Lane, b, out float gd, cur.SIn);
                if (gd <= GoalRadius)
                {
                    float cost = cur.G + (gs - cur.SIn) + gd;
                    if (cost < goalCost) { goalCost = cost; goal = cur; goalS = gs; }
                    continue;
                }
                float g2 = cur.G + (cur.Lane.Length - cur.SIn);
                if (g2 > direct * 4f + 200f) continue;
                lanes.Successors(cur.Lane, succ);
                foreach (var (road, fwd) in succ)
                {
                    var nl = lanes.NextLane(cur.Lane, road, fwd);
                    if (nl == null) continue;
                    var j = lanes.Junction(cur.Lane, nl);
                    float jl = j != null ? j.Length : 0f;
                    open.Add(new Node { Lane = nl, SIn = 0f, G = g2 + jl, Prev = cur });
                }
            }
            if (goal == null) return null;
            // Polylinie zusammensetzen
            var chain = new List<Node>();
            for (var n = goal; n != null; n = n.Prev) chain.Add(n);
            chain.Reverse();
            var pts = new List<Vector3> { a };
            for (int i = 0; i < chain.Count; i++)
            {
                var n = chain[i];
                float sEnd = i == chain.Count - 1 ? goalS : n.Lane.Length;
                AddTrack(pts, n.Lane, n.SIn, sEnd);
                if (i + 1 < chain.Count)
                {
                    var j = lanes.Junction(n.Lane, chain[i + 1].Lane);
                    if (j != null) AddTrack(pts, j, 0f, j.Length);
                }
            }
            pts.Add(b);
            float len = 0f;
            for (int i = 1; i < pts.Count; i++) len += Flat(pts[i] - pts[i - 1]).magnitude;
            if (len > direct * 3f + 60f) return null;   // unplausibler Umweg (z.B. Tore abseits der Straße)
            return pts;
        }

        static void AddTrack(List<Vector3> pts, TrafficTrack t, float s0, float s1)
        {
            if (t == null || t.P == null) return;
            for (int i = 0; i < t.P.Length; i++)
            {
                float s = t.S[i];
                if (s < s0 - 0.01f || s > s1 + 0.01f) continue;
                if (pts.Count > 0 && (pts[pts.Count - 1] - t.P[i]).sqrMagnitude < 0.25f) continue;
                pts.Add(t.P[i]);
            }
        }

        static IEnumerable<LaneTrack> LanesNear(TrafficLanes lanes, Vector3 p, float r)
        {
            foreach (var rid in lanes.RoadsNear(p, r + 40f))
            {
                if (!lanes.Net.Roads.TryGetValue(rid, out var road)) continue;
                for (int f = 0; f < 2; f++)
                {
                    bool fwd = f == 0;
                    if (lanes.LaneCount(road, fwd) <= 0) continue;
                    var t = lanes.Lane(rid, fwd, 0);
                    if (t != null) yield return t;
                }
            }
        }

        static float NearestS(TrafficTrack t, Vector3 p, out float dist, float minS = 0f)
        {
            dist = float.MaxValue; float bs = 0f;
            for (int i = 0; i + 1 < t.P.Length; i++)
            {
                if (t.S[i + 1] < minS) continue;
                var a = t.P[i]; var b = t.P[i + 1];
                var ab = Flat(b - a); float l2 = ab.sqrMagnitude;
                float u = l2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(Flat(p - a), ab) / l2) : 0f;
                var q = a + (b - a) * u;
                float d = Flat(p - q).magnitude;
                float s = Mathf.Lerp(t.S[i], t.S[i + 1], u);
                if (s < minS) continue;
                if (d < dist) { dist = d; bs = s; }
            }
            return bs;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>Route der Prüfung berechnen und an der Definition speichern (Start -> Tore in Fahrreihenfolge).</summary>
        public static void Compute(RaceDefinition race)
        {
            if (race == null) return;
            var lanes = FindLanes();
            var seq = race.TargetSequence();
            var pts = new List<Vector3> { race.StartPosition };
            foreach (int i in seq) pts.Add(race.Checkpoints[i].Position);
            race.RoadPath = Route(lanes, pts, out var seg, out int roadLegs);
            // Abschnitt -> Checkpoint-Index des Ziel-Tors - 1 (Färbung wie RaceRoute.SegmentColor)
            race.RoadPathSeg = new List<int>(seg.Count);
            foreach (int k in seg) race.RoadPathSeg.Add(k < seq.Count ? seq[k] - 1 : race.Checkpoints.Count - 2);
            TDU2DebugLog.Event($"Straßenroute {race.Name}: {pts.Count - 1} Abschnitte, {roadLegs} über Straßen, {race.RoadPath.Count} Punkte, Netz {(lanes != null ? "da" : "FEHLT")}");
        }
    }
}
