using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map.Traffic
{
    /// <summary>
    /// Ein befahrbarer Weg als Polylinie in Inselkoordinaten: eine Spur einer Straße (LaneTrack) oder die Fahrt durch eine
    /// Kreuzung (Hermite-Kurve vom Spurende zum Spuranfang der nächsten Straße). Mit Bogenlänge und Tempoprofil
    /// (Tempoklasse, Kurvenradius, rechtzeitiges Bremsen vor langsameren Stellen).
    /// </summary>
    public class TrafficTrack
    {
        public Vector3[] P;
        public Vector3[] T;          // Tangente (Einheitsvektor)
        public float[] S;            // Bogenlänge
        public float[] Limit;        // erlaubtes Tempo (m/s) inkl. Bremsweg zur nächsten langsameren Stelle
        public float Length;

        public const float LateralAccel = 2.6f;   // m/s² in Kurven
        public const float Brake = 2.2f;          // m/s² komfortables Bremsen

        public void Finish(float[] baseLimit)
        {
            int n = P.Length;
            S = new float[n]; T = new Vector3[n]; Limit = new float[n];
            for (int i = 1; i < n; i++) S[i] = S[i - 1] + Vector3.Distance(P[i - 1], P[i]);
            Length = n > 0 ? S[n - 1] : 0f;
            for (int i = 0; i < n; i++)
            {
                var d = P[Mathf.Min(n - 1, i + 1)] - P[Mathf.Max(0, i - 1)];
                T[i] = d.sqrMagnitude > 1e-8f ? d.normalized : (i > 0 ? T[i - 1] : Vector3.forward);
            }
            // Kurvenradius aus der Richtungsänderung über ±1 Punkt
            for (int i = 0; i < n; i++)
            {
                float lim = baseLimit[i];
                if (i > 0 && i < n - 1)
                {
                    var a = new Vector2(T[i - 1].x, T[i - 1].z); var b = new Vector2(T[i + 1].x, T[i + 1].z);
                    float ang = Vector2.Angle(a, b) * Mathf.Deg2Rad;
                    float ds = Mathf.Max(0.5f, S[i + 1] - S[i - 1]);
                    float k = ang / ds;
                    if (k > 1e-4f) lim = Mathf.Min(lim, Mathf.Sqrt(LateralAccel / k));
                }
                Limit[i] = Mathf.Max(2.5f, lim);
            }
            // rückwärts: rechtzeitig bremsen
            for (int i = n - 2; i >= 0; i--)
                Limit[i] = Mathf.Min(Limit[i], Mathf.Sqrt(Limit[i + 1] * Limit[i + 1] + 2f * Brake * (S[i + 1] - S[i])));
        }

        int Index(float s)
        {
            int lo = 0, hi = S.Length - 1;
            if (s <= 0f) return 0;
            if (s >= Length) return Mathf.Max(0, hi - 1);
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (S[m] <= s) lo = m; else hi = m; }
            return lo;
        }

        public void Sample(float s, out Vector3 pos, out Vector3 tan)
        {
            if (P.Length == 1) { pos = P[0]; tan = T[0]; return; }
            int i = Index(s);
            float seg = Mathf.Max(1e-5f, S[i + 1] - S[i]);
            float k = Mathf.Clamp01((s - S[i]) / seg);
            pos = Vector3.Lerp(P[i], P[i + 1], k);
            tan = Vector3.Slerp(T[i], T[i + 1], k);
            if (s > Length) pos += T[P.Length - 1] * (s - Length);
            else if (s < 0f) pos += T[0] * s;
        }

        public float LimitAt(float s)
        {
            if (Limit.Length == 0) return 10f;
            int i = Index(s);
            return Mathf.Min(Limit[i], Limit[Mathf.Min(Limit.Length - 1, i + 1)]);
        }

        /// <summary>Niedrigstes erlaubtes Tempo innerhalb der nächsten <paramref name="ahead"/> Meter (für die Planung).</summary>
        public float MinLimit(float from, float ahead)
        {
            float m = float.MaxValue;
            int i = Index(from);
            for (; i < S.Length && S[i] <= from + ahead; i++) m = Mathf.Min(m, Limit[i]);
            return m == float.MaxValue ? LimitAt(from) : m;
        }

        public Vector3 Start => P[0];
        public Vector3 End => P[P.Length - 1];
        public Vector3 StartDir => T[0];
        public Vector3 EndDir => T[T.Length - 1];
    }

    /// <summary>Spur einer Straße (Index 0 = innen, an der Mittellinie) in Fahrtrichtung.</summary>
    public sealed class LaneTrack : TrafficTrack
    {
        public int RoadId;
        public bool Forward;          // true = vom Start- zum Endknoten (Spuren rechts der Straßenrichtung)
        public int Lane, LaneCount;
        public int FromNode, ToNode;
        public float BaseSpeed;       // Tempoklasse (m/s) am Spurende
    }

    /// <summary>Fahrt durch eine Kreuzung.</summary>
    public sealed class JunctionTrack : TrafficTrack
    {
        public LaneTrack From, To;
        public int Node;
        /// <summary>-1 links, 0 geradeaus, 1 rechts (für die Blinker).</summary>
        public int Turn;
    }

    /// <summary>
    /// Spuren des Verkehrsnetzes (lazy erzeugt und zwischengespeichert), Nachfolger an Kreuzungen und ein Raster für
    /// "welche Straßen liegen in der Nähe" (Spawnen, Wiedereinfädeln).
    /// </summary>
    public sealed class TrafficLanes
    {
        public readonly IaNetwork Net;
        readonly Dictionary<(int, bool, int), LaneTrack> _lanes = new Dictionary<(int, bool, int), LaneTrack>();
        readonly Dictionary<(LaneTrack, LaneTrack), JunctionTrack> _junctions = new Dictionary<(LaneTrack, LaneTrack), JunctionTrack>();
        readonly Dictionary<(int, int), List<int>> _grid = new Dictionary<(int, int), List<int>>();
        public const float Cell = 128f;
        /// <summary>Radius der Kreuzungsfläche je Knoten (Abstand der Straßenenden zum Mittelpunkt).</summary>
        public readonly Dictionary<int, float> NodeRadius = new Dictionary<int, float>();

        public TrafficLanes(IaNetwork net)
        {
            Net = net;
            foreach (var r in net.Roads.Values)
            {
                var seen = new HashSet<(int, int)>();
                foreach (var pc in r.Pieces)
                    for (int i = 0; i < pc.Points.Length; i += 3)
                    {
                        var p = pc.Points[i];
                        var key = (Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));
                        if (!seen.Add(key)) continue;
                        if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
                        l.Add(r.Id);
                    }
                // Lücken in langen Segmenten: Zwischenpunkte
                foreach (var pc in r.Pieces)
                    for (int i = 0; i + 3 < pc.Points.Length; i += 3)
                    {
                        var a = pc.Points[i]; var b = pc.Points[i + 3];
                        int steps = Mathf.CeilToInt(Vector3.Distance(a, b) / (Cell * 0.5f));
                        for (int k = 1; k < steps; k++)
                        {
                            var p = Vector3.Lerp(a, b, k / (float)steps);
                            var key = (Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));
                            if (!seen.Add(key)) continue;
                            if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
                            l.Add(r.Id);
                        }
                    }
                // Kreuzungsradius
                var first = r.Pieces[0].Points[0];
                var lastPc = r.Pieces[r.Pieces.Count - 1];
                var last = lastPc.Points[lastPc.Points.Length - 1];
                if (net.Nodes.TryGetValue(r.StartNode, out var ns)) Grow(ns, first);
                if (net.Nodes.TryGetValue(r.EndNode, out var ne)) Grow(ne, last);
            }
        }

        void Grow(IaNode n, Vector3 p)
        {
            float d = new Vector2(p.x - n.Pos.x, p.z - n.Pos.z).magnitude;
            if (d > 80f) return;
            NodeRadius.TryGetValue(n.Id, out var cur);
            NodeRadius[n.Id] = Mathf.Max(cur, d);
        }

        public int LaneCount(IaRoad r, bool forward) => forward ? r.LanesFwd : r.LanesBack;

        /// <summary>Straßen in der Nähe (Rasterzellen im Umkreis).</summary>
        public IEnumerable<int> RoadsNear(Vector3 p, float radius)
        {
            int r = Mathf.CeilToInt(radius / Cell);
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            var seen = new HashSet<int>();
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                    if (_grid.TryGetValue((x, z), out var l))
                        foreach (var id in l) if (seen.Add(id)) yield return id;
        }

        /// <summary>Rasterzellen mit Straßen in einem Ring um p (für zufällige Spawnpunkte).</summary>
        public void CellsInRing(Vector3 p, float rMin, float rMax, List<(int, int)> outCells)
        {
            outCells.Clear();
            int r = Mathf.CeilToInt(rMax / Cell);
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    var c = new Vector2((x + 0.5f) * Cell - p.x, (z + 0.5f) * Cell - p.z).magnitude;
                    if (c < rMin - Cell || c > rMax + Cell) continue;
                    if (_grid.ContainsKey((x, z))) outCells.Add((x, z));
                }
        }

        public List<int> RoadsInCell((int, int) cell) => _grid.TryGetValue(cell, out var l) ? l : null;

        // ------------------------------------------------------------------ Spuren
        public LaneTrack Lane(int roadId, bool forward, int lane)
        {
            if (!Net.Roads.TryGetValue(roadId, out var road)) return null;
            int count = LaneCount(road, forward);
            if (count <= 0) return null;
            lane = Mathf.Clamp(lane, 0, count - 1);
            var key = (roadId, forward, lane);
            if (_lanes.TryGetValue(key, out var t)) return t;
            t = BuildLane(road, forward, lane, count);
            _lanes[key] = t;
            return t;
        }

        LaneTrack BuildLane(IaRoad road, bool forward, int lane, int count)
        {
            var pts = new List<Vector3>();
            var off = new List<float>();
            var hgt = new List<float>();
            var lim = new List<float>();
            foreach (var pc in road.Pieces)
            {
                Net.Profiles.TryGetValue(pc.ProfileId, out var prof);
                var centers = prof?.LaneCenters(forward);
                float o, h;
                if (centers != null && centers.Count > 0)
                {
                    var c = centers[Mathf.Clamp(lane, 0, centers.Count - 1)];
                    o = c.offset; h = c.height;
                    // Spur liegt auf der falschen Seite (Profil ohne Gegenrichtung)? Rechtsverkehr erzwingen nur bei Zweirichtungsstraßen
                }
                else
                {
                    float w = 3.5f;
                    int lanesOther = forward ? road.LanesBack : road.LanesFwd;
                    float baseOff = lanesOther > 0 ? 0f : -count * w * 0.5f;
                    o = baseOff + (lane + 0.5f) * w;
                    if (!forward) o = -o;
                    h = 0.1f;
                }
                float speed = Net.SpeedOf(pc.SpeedClass);
                for (int i = 0; i + 3 < pc.Points.Length; i += 3)
                {
                    var a = pc.Points[i]; var c1 = pc.Points[i + 1]; var c2 = pc.Points[i + 2]; var b = pc.Points[i + 3];
                    float chord = Vector3.Distance(a, c1) + Vector3.Distance(c1, c2) + Vector3.Distance(c2, b);
                    int steps = Mathf.Clamp(Mathf.CeilToInt(chord / 3f), 1, 64);
                    for (int k = (pts.Count == 0 ? 0 : 1); k <= steps; k++)
                    {
                        float t = k / (float)steps, u = 1f - t;
                        var p = u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
                        if (pts.Count > 0 && (p - pts[pts.Count - 1]).sqrMagnitude < 0.01f) continue;
                        pts.Add(p); off.Add(o); hgt.Add(h); lim.Add(speed);
                    }
                }
                if (pc.Points.Length == 1 && pts.Count == 0) { pts.Add(pc.Points[0]); off.Add(o); hgt.Add(h); lim.Add(speed); }
            }
            int n = pts.Count;
            if (n < 2)
            {
                var only = n == 1 ? pts[0] : Vector3.zero;
                float o0 = off.Count > 0 ? off[0] : 0f;
                pts.Clear(); off.Clear(); hgt.Clear(); lim.Clear();
                pts.Add(only); pts.Add(only + Vector3.forward * 0.5f);
                off.Add(o0); off.Add(o0); hgt.Add(0.1f); hgt.Add(0.1f); lim.Add(8f); lim.Add(8f);
                n = 2;
            }
            // Spurwechsel zwischen Abschnitten (andere Profile) weich verteilen
            SmoothOffsets(pts, off, 18f);

            // Mittellinie -> Spur: rechts der Straßenrichtung = (-t.z, 0, t.x) (rechtshändige Inselkoordinaten)
            var lanePts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var d = pts[Mathf.Min(n - 1, i + 1)] - pts[Mathf.Max(0, i - 1)];
                var right = new Vector3(-d.z, 0f, d.x);
                right = right.sqrMagnitude > 1e-8f ? right.normalized : Vector3.right;
                lanePts[i] = pts[i] + right * off[i] + Vector3.up * hgt[i];
            }
            var limits = lim.ToArray();
            if (!forward) { Array.Reverse(lanePts); Array.Reverse(limits); }
            var t2 = new LaneTrack
            {
                RoadId = road.Id,
                Forward = forward,
                Lane = lane,
                LaneCount = count,
                FromNode = forward ? road.StartNode : road.EndNode,
                ToNode = forward ? road.EndNode : road.StartNode,
                P = lanePts,
                BaseSpeed = limits[limits.Length - 1],
            };
            t2.Finish(limits);
            return t2;
        }

        static void SmoothOffsets(List<Vector3> pts, List<float> off, float dist)
        {
            int n = off.Count;
            bool change = false;
            for (int i = 1; i < n; i++) if (Mathf.Abs(off[i] - off[i - 1]) > 0.05f) { change = true; break; }
            if (!change) return;
            var cum = new float[n];
            for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
            var src = off.ToArray();
            int lo = 0, hi = 0;
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                while (hi < n && cum[hi] <= cum[i] + dist * 0.5f) { sum += src[hi]; hi++; }
                while (lo < n && cum[lo] < cum[i] - dist * 0.5f) { sum -= src[lo]; lo++; }
                off[i] = sum / Mathf.Max(1, hi - lo);
            }
        }

        // ------------------------------------------------------------------ Kreuzungen
        /// <summary>Mögliche Fortsetzungen am Ende einer Spur (Straße + Richtung), ohne Wenden – es sei denn, es gibt nichts anderes.</summary>
        public void Successors(LaneTrack from, List<(int road, bool forward)> outList)
        {
            outList.Clear();
            if (!Net.Nodes.TryGetValue(from.ToNode, out var node) || from.ToNode == 0)
            {
                // Sackgasse: wenden, falls Gegenspur vorhanden
                if (Net.Roads.TryGetValue(from.RoadId, out var self) && LaneCount(self, !from.Forward) > 0) outList.Add((from.RoadId, !from.Forward));
                return;
            }
            foreach (var rid in node.Roads)
            {
                if (!Net.Roads.TryGetValue(rid, out var r)) continue;
                if (r.StartNode == node.Id && r.LanesFwd > 0) Add(rid, true);
                if (r.EndNode == node.Id && r.LanesBack > 0) Add(rid, false);
            }
            void Add(int rid, bool fwd)
            {
                if (rid == from.RoadId && fwd != from.Forward && Net.Roads[rid].StartNode != Net.Roads[rid].EndNode) return;   // Wenden später
                // scharfe Kehren (> 150°) auslassen
                var next = Lane(rid, fwd, 0);
                if (next == null) return;
                float ang = Vector2.Angle(new Vector2(from.EndDir.x, from.EndDir.z), new Vector2(next.StartDir.x, next.StartDir.z));
                if (ang > 150f) return;
                outList.Add((rid, fwd));
            }
            if (outList.Count == 0 && Net.Roads.TryGetValue(from.RoadId, out var me) && LaneCount(me, !from.Forward) > 0) outList.Add((from.RoadId, !from.Forward));
        }

        /// <summary>Zielspur auf der nächsten Straße: gleiche Position von außen gezählt (rechte Spur bleibt rechts).</summary>
        public LaneTrack NextLane(LaneTrack from, int road, bool forward)
        {
            if (!Net.Roads.TryGetValue(road, out var r)) return null;
            int count = LaneCount(r, forward);
            if (count <= 0) return null;
            int fromOuter = from.LaneCount - 1 - from.Lane;
            int lane = Mathf.Clamp(count - 1 - fromOuter, 0, count - 1);
            return Lane(road, forward, lane);
        }

        public JunctionTrack Junction(LaneTrack from, LaneTrack to)
        {
            if (from == null || to == null) return null;
            if (_junctions.TryGetValue((from, to), out var j)) return j;
            j = new JunctionTrack { From = from, To = to, Node = from.ToNode };
            var a = from.End; var b = to.Start;
            var ta = from.EndDir; var tb = to.StartDir;
            float dist = Vector3.Distance(a, b);
            bool uturn = Vector3.Dot(ta, tb) < -0.5f;
            float m = uturn ? Mathf.Max(dist, 8f) * 1.6f : Mathf.Max(dist, 1f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(dist / 2f) + 2, 4, 40);
            var pts = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps, t2 = t * t, t3 = t2 * t;
                pts[i] = (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * m * ta + (-2 * t3 + 3 * t2) * b + (t3 - t2) * m * tb;
            }
            j.P = pts;
            float baseLim = Mathf.Min(from.BaseSpeed, to.Limit.Length > 0 ? to.Limit[0] : 10f, 17f);
            var lim = new float[pts.Length];
            for (int i = 0; i < lim.Length; i++) lim[i] = baseLim;
            j.Finish(lim);
            float cy = ta.z * tb.x - ta.x * tb.z;   // > 0: links (rechtshändig, Y oben)
            float dot = Vector3.Dot(new Vector3(ta.x, 0, ta.z).normalized, new Vector3(tb.x, 0, tb.z).normalized);
            j.Turn = dot > 0.9f ? 0 : (cy > 0f ? -1 : 1);
            _junctions[(from, to)] = j;
            return j;
        }

        /// <summary>Nächster Spurpunkt zu p (Inselkoordinaten), passend zur Richtung (Wiedereinfädeln nach einem Unfall).</summary>
        public bool Nearest(Vector3 p, Vector3 dir, float maxDist, out LaneTrack best, out float bestS)
        {
            best = null; bestS = 0f;
            float bestD = maxDist;
            foreach (var rid in RoadsNear(p, maxDist + 40f))
            {
                var r = Net.Roads[rid];
                for (int f = 0; f < 2; f++)
                {
                    bool fwd = f == 0;
                    int cnt = LaneCount(r, fwd);
                    for (int l = 0; l < cnt; l++)
                    {
                        var t = Lane(rid, fwd, l);
                        if (t == null) continue;
                        for (int i = 0; i < t.P.Length; i++)
                        {
                            var d = t.P[i] - p; d.y *= 0.5f;
                            float dd = d.magnitude;
                            if (dd >= bestD) continue;
                            if (dir.sqrMagnitude > 0.01f && Vector3.Dot(t.T[i], dir) < 0.6f) continue;
                            bestD = dd; best = t; bestS = t.S[i];
                        }
                    }
                }
            }
            return best != null;
        }
    }
}
