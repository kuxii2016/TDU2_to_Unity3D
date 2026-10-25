using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// GPS wie im Spiel: Ziel auf der großen Karte setzen (Mini-Menü "Ziel GPS" bzw. Rechtsklick), kürzester Weg über das
    /// Straßennetz (ROAD_MAP/ROAD_DETAIL.IA, Dijkstra, Einbahnstraßen über die Spuren), Anzeige auf der Minimap
    /// (Pfad, Restentfernung /HUD/minimap/gps_distance mit /:obj_Distance + /:gps_Unit, Richtungs-Kranz
    /// /HUD/minimap/distance/obj_dist_01..61 – Bild 1 = hell, 101 = aus –, Meldung /HUD/minimap/gps_status "open" mit
    /// /:IsGPSremoved). Am Ziel wird es entfernt. Ansagen aus Sound/Voices/&lt;Sprache&gt;/GPS.bnk (VFGPSVoiceNNN_.wav,
    /// Zuordnung in <see cref="TDU2GpsSettings"/>).
    /// Wird von <see cref="TDU2Minimap"/> angelegt.
    /// </summary>
    public sealed class TDU2Gps : MonoBehaviour
    {
        public static TDU2Gps Instance { get; private set; }
        public TDU2Minimap Minimap;

        public bool HasTarget { get; private set; }
        /// <summary>Ziel in Spielkoordinaten (Insel).</summary>
        public Vector3 TargetGame { get; private set; }
        /// <summary>Restweg in Metern (-1 = unbekannt).</summary>
        public float Remaining { get; private set; } = -1f;
        /// <summary>Route (Spielkoordinaten), Auto -> Ziel.</summary>
        public readonly List<Vector3> Route = new List<Vector3>();
        public event Action Changed;

        // ------------------------------------------------------------------ Straßennetz als Graph
        sealed class RoadPoly { public int Id, Start, End; public bool Fwd, Back; public Vector3[] P; public float[] S; public float Len; }
        IaNetwork _graphNet;
        readonly Dictionary<int, RoadPoly> _roads = new Dictionary<int, RoadPoly>();
        readonly Dictionary<int, List<RoadPoly>> _adj = new Dictionary<int, List<RoadPoly>>();
        readonly Dictionary<long, List<(RoadPoly r, int seg)>> _grid = new Dictionary<long, List<(RoadPoly, int)>>();
        const float Cell = 64f;
        Task _buildTask;

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        void Awake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        bool GraphReady => _graphNet != null && _buildTask == null && _roads.Count > 0;

        void EnsureGraph()
        {
            var net = Minimap != null ? Minimap.Network : null;
            if (net == null || net == _graphNet) return;
            if (_buildTask != null) { if (!_buildTask.IsCompleted) return; _buildTask = null; return; }
            _graphNet = net;
            _roads.Clear(); _adj.Clear(); _grid.Clear();
            _buildTask = Task.Run(() => BuildGraph(net));
        }

        void BuildGraph(IaNetwork net)
        {
            var roads = new Dictionary<int, RoadPoly>();
            var adj = new Dictionary<int, List<RoadPoly>>();
            var grid = new Dictionary<long, List<(RoadPoly, int)>>();
            foreach (var r in net.Roads.Values)
            {
                var pts = new List<Vector3>();
                foreach (var pc in r.Pieces)
                {
                    var p = pc.Points;
                    if (p == null || p.Length == 0) continue;
                    for (int k = 0; k + 3 < p.Length; k += 3)
                    {
                        Vector3 a = p[k], c1 = p[k + 1], c2 = p[k + 2], b = p[k + 3];
                        float est = (c1 - a).magnitude + (c2 - c1).magnitude + (b - c2).magnitude;
                        int n = Mathf.Clamp(Mathf.CeilToInt(est / 12f), 1, 24);
                        for (int i = (pts.Count == 0 ? 0 : 1); i <= n; i++)
                        {
                            float t = i / (float)n, u = 1f - t;
                            pts.Add(u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b);
                        }
                    }
                    if (p.Length == 1 && pts.Count == 0) pts.Add(p[0]);
                }
                if (pts.Count < 2) continue;
                var rp = new RoadPoly { Id = r.Id, Start = r.StartNode, End = r.EndNode, P = pts.ToArray(), S = new float[pts.Count] };
                for (int i = 1; i < pts.Count; i++) rp.S[i] = rp.S[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
                rp.Len = rp.S[pts.Count - 1];
                // Einbahnstraßen: Spuren mit (Fwd) bzw. gegen (Back) die Straßenrichtung; ohne Angabe beide Richtungen
                rp.Fwd = r.LanesFwd > 0 || r.LanesBack == 0;
                rp.Back = r.LanesBack > 0 || r.LanesFwd == 0;
                roads[r.Id] = rp;
                if (rp.Start != 0) { if (!adj.TryGetValue(rp.Start, out var l)) adj[rp.Start] = l = new List<RoadPoly>(); l.Add(rp); }
                if (rp.End != 0 && rp.End != rp.Start) { if (!adj.TryGetValue(rp.End, out var l2)) adj[rp.End] = l2 = new List<RoadPoly>(); l2.Add(rp); }
                for (int i = 0; i + 1 < rp.P.Length; i++)
                {
                    Vector3 a = rp.P[i], b = rp.P[i + 1];
                    int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / Cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / Cell);
                    int z0 = Mathf.FloorToInt(Mathf.Min(a.z, b.z) / Cell), z1 = Mathf.FloorToInt(Mathf.Max(a.z, b.z) / Cell);
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long key = Key(x, z);
                            if (!grid.TryGetValue(key, out var gl)) grid[key] = gl = new List<(RoadPoly, int)>();
                            gl.Add((rp, i));
                        }
                }
            }
            lock (_roads)
            {
                foreach (var kv in roads) _roads[kv.Key] = kv.Value;
                foreach (var kv in adj) _adj[kv.Key] = kv.Value;
                foreach (var kv in grid) _grid[kv.Key] = kv.Value;
            }
        }

        /// <summary>Nächster Punkt auf einer Straße (Spielkoordinaten); s = Weg ab Straßenanfang.</summary>
        bool Project(Vector3 g, float maxDist, out RoadPoly road, out float s, out Vector3 point)
        {
            road = null; s = 0f; point = g;
            float best = maxDist * maxDist;
            int r = Mathf.CeilToInt(maxDist / Cell);
            int cx = Mathf.FloorToInt(g.x / Cell), cz = Mathf.FloorToInt(g.z / Cell);
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (!_grid.TryGetValue(Key(x, z), out var l)) continue;
                    foreach (var (rp, i) in l)
                    {
                        Vector3 a = rp.P[i], b = rp.P[i + 1];
                        var ab = new Vector2(b.x - a.x, b.z - a.z); var ag = new Vector2(g.x - a.x, g.z - a.z);
                        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(ag, ab) / ab.sqrMagnitude) : 0f;
                        var q = Vector3.Lerp(a, b, t);
                        float dx = q.x - g.x, dz = q.z - g.z, d = dx * dx + dz * dz;
                        if (d < best) { best = d; road = rp; point = q; s = rp.S[i] + (rp.S[i + 1] - rp.S[i]) * t; }
                    }
                }
            return road != null;
        }

        /// <summary>Teilstück einer Straße von s0 nach s1 (rückwärts, wenn s1 &lt; s0).</summary>
        static void AppendSub(List<Vector3> o, RoadPoly r, float s0, float s1)
        {
            Vector3 At(float s)
            {
                int i = Array.BinarySearch(r.S, s);
                if (i >= 0) return r.P[i];
                i = ~i;
                if (i <= 0) return r.P[0];
                if (i >= r.P.Length) return r.P[r.P.Length - 1];
                float t = (s - r.S[i - 1]) / Mathf.Max(1e-4f, r.S[i] - r.S[i - 1]);
                return Vector3.Lerp(r.P[i - 1], r.P[i], t);
            }
            o.Add(At(s0));
            if (s1 >= s0) { for (int i = 0; i < r.P.Length; i++) if (r.S[i] > s0 && r.S[i] < s1) o.Add(r.P[i]); }
            else { for (int i = r.P.Length - 1; i >= 0; i--) if (r.S[i] < s0 && r.S[i] > s1) o.Add(r.P[i]); }
            o.Add(At(s1));
        }

        sealed class Heap
        {
            readonly List<(float d, int n)> _h = new List<(float, int)>();
            public int Count => _h.Count;
            public void Push(float d, int n)
            {
                _h.Add((d, n)); int i = _h.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (_h[p].d <= _h[i].d) break; (_h[p], _h[i]) = (_h[i], _h[p]); i = p; }
            }
            public (float d, int n) Pop()
            {
                var top = _h[0]; var last = _h[_h.Count - 1]; _h.RemoveAt(_h.Count - 1);
                if (_h.Count > 0)
                {
                    _h[0] = last; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, m = i;
                        if (l < _h.Count && _h[l].d < _h[m].d) m = l;
                        if (r < _h.Count && _h[r].d < _h[m].d) m = r;
                        if (m == i) break;
                        (_h[m], _h[i]) = (_h[i], _h[m]); i = m;
                    }
                }
                return top;
            }
        }

        /// <summary>Kürzester Weg von (ra, sa) nach (rb, sb) über die Knoten; null = keiner.</summary>
        List<Vector3> ShortestPath(RoadPoly ra, float sa, RoadPoly rb, float sb, bool oneWay, out float len)
        {
            len = 0f;
            bool F(RoadPoly x) => !oneWay || x.Fwd;
            bool B(RoadPoly x) => !oneWay || x.Back;
            float bestDirect = float.MaxValue;
            if (ra == rb && ((sb >= sa && F(ra)) || (sb < sa && B(ra)))) bestDirect = Mathf.Abs(sb - sa);

            var dist = new Dictionary<int, float>();
            var prev = new Dictionary<int, (int node, RoadPoly road)>();   // node -> (vorheriger Knoten, Straße); node 0 = Start
            var heap = new Heap();
            if (ra.Start != 0 && B(ra)) { dist[ra.Start] = sa; prev[ra.Start] = (0, ra); heap.Push(sa, ra.Start); }
            if (ra.End != 0 && F(ra) && (!dist.TryGetValue(ra.End, out var de) || ra.Len - sa < de)) { dist[ra.End] = ra.Len - sa; prev[ra.End] = (0, ra); heap.Push(ra.Len - sa, ra.End); }
            float best = bestDirect; int bestNode = 0; bool bestAtStart = false;
            var done = new HashSet<int>();
            while (heap.Count > 0)
            {
                var (d, n) = heap.Pop();
                if (!done.Add(n)) continue;
                if (d >= best) break;
                if (n == rb.Start && F(rb) && d + sb < best) { best = d + sb; bestNode = n; bestAtStart = true; }
                if (n == rb.End && B(rb) && d + rb.Len - sb < best) { best = d + rb.Len - sb; bestNode = n; bestAtStart = false; }
                if (!_adj.TryGetValue(n, out var list)) continue;
                foreach (var r in list)
                {
                    int to; bool ok;
                    if (r.Start == n) { to = r.End; ok = F(r); } else { to = r.Start; ok = B(r); }
                    if (!ok || to == 0) continue;
                    float nd = d + r.Len;
                    if (!dist.TryGetValue(to, out var od) || nd < od) { dist[to] = nd; prev[to] = (n, r); heap.Push(nd, to); }
                }
            }
            if (best == float.MaxValue) return null;
            var o = new List<Vector3>();
            len = best;
            if (bestNode == 0) { AppendSub(o, ra, sa, sb); return o; }
            // Knotenkette rückwärts
            var chain = new List<(int node, RoadPoly road)>();
            int cur = bestNode;
            int guard = 0;
            while (cur != 0 && guard++ < 100000)
            {
                var pv = prev[cur];
                chain.Add((cur, pv.road));
                cur = pv.node;
            }
            chain.Reverse();
            // erstes Stück: vom Auto zum ersten Knoten
            var first = chain[0];
            AppendSub(o, ra, sa, first.node == ra.Start && (ra.End != first.node || sa < ra.Len * 0.5f) ? 0f : ra.Len);
            for (int i = 1; i < chain.Count; i++)
            {
                var road = chain[i].road;
                int from = chain[i - 1].node;
                if (road.Start == from) AppendSub(o, road, 0f, road.Len); else AppendSub(o, road, road.Len, 0f);
            }
            AppendSub(o, rb, bestAtStart ? 0f : rb.Len, sb);
            return o;
        }

        // ------------------------------------------------------------------ Ziel
        /// <summary>Ziel setzen (Spielkoordinaten); wird auf die nächste Straße gelegt.</summary>
        public void SetTarget(Vector3 game, bool announce = true)
        {
            EnsureGraph();
            if (GraphReady && Project(game, 400f, out _, out _, out var p)) game = p;
            TargetGame = game; HasTarget = true; Route.Clear(); Remaining = -1f;
            _nextRoute = 0f; _lastCue = null; _arrivedSaid = false;
            ShowStatus(false);
            if (announce) Voice(TDU2GpsSettings.VoiceTargetSet);
            DebugManager.Debug("GPS: Ziel " + game);
            try { Changed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>Ziel entfernen (am Ziel angekommen: <paramref name="arrived"/>).</summary>
        public void ClearTarget(bool arrived = false)
        {
            if (!HasTarget) return;
            HasTarget = false; Route.Clear(); Remaining = -1f;
            ShowStatus(true);
            if (arrived) Voice(TDU2GpsSettings.VoiceArrived);
            DebugManager.Debug("GPS: Ziel " + (arrived ? "erreicht" : "entfernt"));
            try { Changed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>Ziel in der Nähe dieses Punkts (Spielkoordinaten)?</summary>
        public bool IsTargetNear(Vector3 game, float radius = 25f)
        {
            if (!HasTarget) return false;
            var d = TargetGame - game; d.y = 0f;
            return d.sqrMagnitude < radius * radius;
        }

        // ------------------------------------------------------------------ Ablauf
        float _nextRoute;
        Vector3 _routeStartGame;
        string _lastCue;
        bool _arrivedSaid;

        void Update()
        {
            EnsureGraph();
            if (_buildTask != null && _buildTask.IsCompleted)
            {
                if (_buildTask.IsFaulted) Debug.LogWarning("GPS: Straßennetz: " + _buildTask.Exception?.GetBaseException().Message);
                else DebugManager.Debug("GPS: Straßennetz " + _roads.Count + " Straßen, " + _adj.Count + " Knoten");
                _buildTask = null;
            }
            var hud = Minimap != null ? Minimap.HudPlayer : null;
            var veh = Minimap != null ? Minimap.Vehicle : null;
            var root = Minimap != null && Minimap.Streamer != null ? Minimap.Streamer.MapRoot : null;
            if (!HasTarget || veh == null || root == null) { UpdateHud(hud, null, null, false); return; }
            var car = root.InverseTransformPoint(veh.transform.position);
            var flat = TargetGame - car; flat.y = 0f;
            if (flat.magnitude < TDU2GpsSettings.ArriveDistance) { ClearTarget(true); UpdateHud(hud, null, null, false); return; }
            if (GraphReady && Time.time >= _nextRoute)
            {
                _nextRoute = Time.time + 0.75f;
                ComputeRoute(car);
            }
            UpdateCues(car, veh.transform, root);
            UpdateHud(hud, veh.transform, root, true);
        }

        void ComputeRoute(Vector3 car)
        {
            Route.Clear(); Remaining = -1f;
            if (!Project(car, 300f, out var ra, out var sa, out var pa)) { Remaining = Flat(TargetGame - car); return; }
            if (!Project(TargetGame, 400f, out var rb, out var sb, out var pb)) { Remaining = Flat(TargetGame - car); return; }
            var path = ShortestPath(ra, sa, rb, sb, true, out float len) ?? ShortestPath(ra, sa, rb, sb, false, out len);
            if (path == null) { Remaining = Flat(TargetGame - car); return; }
            Route.Add(car);
            Route.AddRange(path);
            Route.Add(TargetGame);
            Remaining = len + Flat(pa - car) + Flat(TargetGame - pb);
            _routeStartGame = car;
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        // ------------------------------------------------------------------ Ansagen (Abbiegen)
        void UpdateCues(Vector3 car, Transform carT, Transform root)
        {
            if (Route.Count < 3 || !TDU2GpsSettings.Voice) return;
            // Fahrtrichtung im Spielraum
            var fw = root.InverseTransformDirection(carT.forward); fw.y = 0f;
            if (fw.sqrMagnitude < 1e-4f) return;
            fw.Normalize();
            // Route läuft entgegen der Fahrtrichtung -> wenden
            var first = Route[Mathf.Min(2, Route.Count - 1)] - car; first.y = 0f;
            if (first.magnitude > 8f && Vector3.Dot(first.normalized, fw) < -0.6f) { Cue("uturn", TDU2GpsSettings.VoiceUTurn); return; }
            // nächste deutliche Richtungsänderung entlang der Route (über ~25 m gemittelt)
            float along = 0f;
            for (int i = 1; i + 1 < Route.Count && along < 260f; i++)
            {
                along += Flat(Route[i] - Route[i - 1]);
                var a = Dir(i, -1); var b = Dir(i, +1);
                if (a == Vector3.zero || b == Vector3.zero) continue;
                float ang = Vector3.SignedAngle(a, b, Vector3.up);
                if (Mathf.Abs(ang) < 35f) continue;
                bool right = ang > 0f;
                if (root.lossyScale.x < 0f) right = !right;   // gespiegelte Insel (MirrorX)
                string id = (right ? "R" : "L") + ":" + Mathf.RoundToInt(Route[i].x) + ":" + Mathf.RoundToInt(Route[i].z);
                if (along < 45f) Cue("now" + id, right ? TDU2GpsSettings.VoiceTurnRightNow : TDU2GpsSettings.VoiceTurnLeftNow);
                else if (along < 220f) Cue("soon" + id, right ? TDU2GpsSettings.VoiceTurnRightSoon : TDU2GpsSettings.VoiceTurnLeftSoon);
                return;
            }
        }

        Vector3 Dir(int i, int sign)
        {
            float acc = 0f; int j = i;
            while (acc < 25f) { int k = j + sign; if (k < 0 || k >= Route.Count) break; acc += Flat(Route[k] - Route[j]); j = k; }
            var d = sign > 0 ? Route[j] - Route[i] : Route[i] - Route[j];
            d.y = 0f;
            return d.sqrMagnitude > 1f ? d.normalized : Vector3.zero;
        }

        void Cue(string id, int voice)
        {
            if (_lastCue == id) return;
            _lastCue = id;
            Voice(voice);
        }

        // ------------------------------------------------------------------ Stimme (Sound/Voices/<lang>/GPS.bnk)
        AudioSource _audio;
        BnkArchive _voiceBank; string _voiceLang;
        readonly Dictionary<int, AudioClip> _clips = new Dictionary<int, AudioClip>();

        /// <summary>Ansage Nummer <paramref name="index"/> (VFGPSVoice001..) abspielen; 0 = keine.</summary>
        public void Voice(int index)
        {
            if (index <= 0 || !TDU2GpsSettings.Voice) return;
            var clip = Clip(index);
            if (clip == null) return;
            if (_audio == null) { _audio = gameObject.AddComponent<AudioSource>(); _audio.playOnAwake = false; _audio.spatialBlend = 0f; _audio.ignoreListenerPause = true; }
            _audio.volume = TDU2GpsSettings.VoiceVolume;
            _audio.Stop();
            _audio.clip = clip;
            _audio.Play();
        }

        /// <summary>Anzahl der Ansagen in der GPS-Bank der aktuellen Sprache.</summary>
        public int VoiceCount { get { EnsureVoiceBank(); int n = 0; if (_voiceBank != null) foreach (var e in _voiceBank.All(".wav")) n++; return n; } }

        void EnsureVoiceBank()
        {
            string lang = string.IsNullOrEmpty(TDU2GpsSettings.VoiceLanguage) ? FlashLocalization.LangCode(Application.systemLanguage) : TDU2GpsSettings.VoiceLanguage;
            if (_voiceBank != null && _voiceLang == lang) return;
            _voiceLang = lang; _voiceBank = null;
            foreach (var c in _clips.Values) if (c != null) Destroy(c);
            _clips.Clear();
            string bnk = Minimap != null && Minimap.Streamer != null && Minimap.Streamer.Index != null ? Minimap.Streamer.Index.BnkDir : null;
            if (string.IsNullOrEmpty(bnk)) return;
            foreach (var l in new[] { lang, "us" })
            {
                string dir = Path.Combine(bnk, "sound", "voices", l);
                string f = Directory.Exists(dir) ? CarDatabase.FindFile(dir, "gps.bnk") : null;
                if (f == null) continue;
                try { _voiceBank = BnkArchive.Open(f); break; } catch (Exception ex) { Debug.LogWarning("[TDU2] GPS-Stimme: " + ex.Message); }
            }
        }

        AudioClip Clip(int index)
        {
            EnsureVoiceBank();
            if (_voiceBank == null) return null;
            if (_clips.TryGetValue(index, out var c)) return c;
            c = null;
            var e = _voiceBank.Find("VFGPSVoice" + index.ToString("000") + "_.wav");
            if (e != null && WavDecoder.TryDecode(_voiceBank.Read(e), out var data, out int ch, out int rate) && data != null && ch > 0)
            {
                c = AudioClip.Create("GPS " + index, data.Length / ch, ch, rate, false);
                c.SetData(data, 0);
            }
            _clips[index] = c;
            return c;
        }

        // ------------------------------------------------------------------ HUD (hud.vmf /HUD/minimap)
        readonly List<(string path, float ang)> _dots = new List<(string, float)>();
        VmfPlayer _dotsFor;
        bool _hudShown;
        readonly int[] _dotFrame = new int[64];

        void ShowStatus(bool removed)
        {
            var hud = Minimap != null ? Minimap.HudPlayer : null;
            if (hud == null) return;
            hud.SetVisible("/HUD/minimap/gps_status", true);
            hud.SetValue("/:IsGPSremoved", removed);
            hud.GotoLabel("/HUD/minimap/gps_status", "open", true);
        }

        void InitDots(VmfPlayer hud)
        {
            _dots.Clear(); _dotsFor = hud;
            var ring = hud.Find("/HUD/minimap/distance");
            if (ring?.Sprite == null) return;
            var pts = new List<(string, Vector2)>();
            foreach (var d in ring.Sprite.List.Values)
                if (d.Name != null && d.Name.StartsWith("obj_dist_", StringComparison.OrdinalIgnoreCase)) pts.Add(("/HUD/minimap/distance/" + d.Name, new Vector2(d.Matrix.Tx, d.Matrix.Ty)));
            if (pts.Count == 0) return;
            Vector2 c = Vector2.zero; foreach (var p in pts) c += p.Item2; c /= pts.Count;
            foreach (var (path, p) in pts) _dots.Add((path, Mathf.Atan2(p.x - c.x, -(p.y - c.y)) * Mathf.Rad2Deg));   // 0° = oben (Fahrtrichtung), im Uhrzeigersinn
            for (int i = 0; i < _dotFrame.Length; i++) _dotFrame[i] = -1;
        }

        void UpdateHud(VmfPlayer hud, Transform carT, Transform root, bool active)
        {
            if (hud == null) return;
            if (!active)
            {
                if (_hudShown)
                {
                    _hudShown = false;
                    hud.SetVisible("/HUD/minimap/distance", false);
                    hud.SetVisible("/HUD/minimap/gps_distance", false);
                    hud.SetVariable("/:obj_Distance", ""); hud.SetVariable("/:gps_Unit", "");
                }
                return;
            }
            if (_dotsFor != hud || _dots.Count == 0) InitDots(hud);
            _hudShown = true;
            hud.SetVisible("/HUD/minimap/distance", true);
            hud.SetVisible("/HUD/minimap/gps_distance", true);
            float m = Remaining >= 0f ? Remaining : 0f;
            var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            if (TDU2GpsSettings.Miles)
            {
                float mi = m / 1609.344f;
                hud.SetVariable("/:obj_Distance", mi < 0.1f ? Mathf.RoundToInt(m * 1.09361f).ToString() : mi.ToString(mi < 10f ? "0.0" : "0", de));
                hud.SetVariable("/:gps_Unit", mi < 0.1f ? "yd" : "mi");
            }
            else
            {
                hud.SetVariable("/:obj_Distance", m < 1000f ? (Mathf.RoundToInt(m / 10f) * 10).ToString() : (m / 1000f).ToString(m < 10000f ? "0.0" : "0", de));
                hud.SetVariable("/:gps_Unit", m < 1000f ? "m" : "km");
            }
            // Richtungs-Kranz: Punkte in Richtung Ziel (Luftlinie) hell, nach außen ausblendend
            if (carT == null || root == null) return;
            var tw = root.TransformPoint(TargetGame) - carT.position; tw.y = 0f;
            var fw = carT.forward; fw.y = 0f;
            if (tw.sqrMagnitude < 1e-4f || fw.sqrMagnitude < 1e-6f) return;
            float bearing = Vector3.SignedAngle(fw, tw, Vector3.up);
            for (int i = 0; i < _dots.Count && i < _dotFrame.Length; i++)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle(_dots[i].ang, bearing));
                float lit = Mathf.Clamp01(1f - diff / TDU2GpsSettings.RingSpread);
                int frame = 1 + Mathf.RoundToInt((1f - lit) * 100f);   // Bild 1 = voll, 101 = unsichtbar
                if (frame == _dotFrame[i]) continue;
                _dotFrame[i] = frame;
                hud.SetFrame(_dots[i].path, frame);
            }
        }

        // ------------------------------------------------------------------ Pfad auf der Minimap
        Mesh _mesh; int _meshVersion = -1; int _routeVersion; float _meshRange;
        int _lastCount; Vector3 _lastFirst;

        /// <summary>Pfad als Band (Spielkoordinaten) für die Minimap-Kamera; Breite relativ zum Sichtradius.</summary>
        public Mesh RouteMesh(float range)
        {
            if (!HasTarget || Route.Count < 2) return null;
            if (Route.Count != _lastCount || Route[0] != _lastFirst) { _routeVersion++; _lastCount = Route.Count; _lastFirst = Route[0]; }
            float half = Mathf.Max(1.5f, range * TDU2GpsSettings.PathWidth);
            if (_mesh != null && _meshVersion == _routeVersion && Mathf.Abs(_meshRange - half) < half * 0.15f) return _mesh;
            _meshVersion = _routeVersion; _meshRange = half;
            var v = new List<Vector3>(Route.Count * 2); var c = new List<Color32>(Route.Count * 2); var t = new List<int>(Route.Count * 6);
            Color32 col = TDU2GpsSettings.PathColor;
            for (int i = 0; i < Route.Count; i++)
            {
                var cur = Route[i];
                var prev = i > 0 ? Route[i - 1] : cur; var next = i < Route.Count - 1 ? Route[i + 1] : cur;
                var d = new Vector2(next.x - prev.x, next.z - prev.z);
                if (d.sqrMagnitude < 1e-6f) d = new Vector2(0f, 1f);
                d.Normalize();
                var side = new Vector2(-d.y, d.x) * half;
                v.Add(new Vector3(cur.x - side.x, 0.5f, cur.z - side.y)); c.Add(col);
                v.Add(new Vector3(cur.x + side.x, 0.5f, cur.z + side.y)); c.Add(col);
                if (i > 0) { int b = (i - 1) * 2; t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b + 1); t.Add(b + 3); t.Add(b + 2); }
            }
            if (_mesh == null) _mesh = new Mesh { name = "TDU2_GpsRoute", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            _mesh.Clear();
            _mesh.SetVertices(v); _mesh.SetColors(c); _mesh.SetTriangles(t, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return _mesh;
        }
    }

    /// <summary>GPS-Einstellungen (auch im F1-Menü). Ansagen: Nummer der Datei VFGPSVoiceNNN_.wav in Sound/Voices/&lt;Sprache&gt;/GPS.bnk, 0 = keine.</summary>
    public static class TDU2GpsSettings
    {
        public static bool Voice = true;
        public static float VoiceVolume = 1f;
        /// <summary>Sprache der Ansagen ("ge", "us", "fr" …); leer = Systemsprache.</summary>
        public static string VoiceLanguage = "ge";
        public static int VoiceTargetSet = 0;
        public static int VoiceArrived = 0;
        public static int VoiceTurnLeftSoon = 0;
        public static int VoiceTurnRightSoon = 0;
        public static int VoiceTurnLeftNow = 0;
        public static int VoiceTurnRightNow = 0;
        public static int VoiceUTurn = 0;
        /// <summary>Ziel erreicht unter diesem Abstand (m).</summary>
        public static float ArriveDistance = 30f;
        /// <summary>Breite des Pfads relativ zum Sichtradius der Minimap.</summary>
        public static float PathWidth = 0.020f;
        public static Color32 PathColor = new Color32(255, 150, 20, 255);
        /// <summary>Öffnungswinkel des Richtungs-Kranzes (Grad bis ganz dunkel).</summary>
        public static float RingSpread = 30f;
        public static bool Miles = false;
    }

    /// <summary>
    /// Lesezeichen und Favoriten-Spots der großen Karte. Angemeldet im Spielstand auf dem Server (SaveData.bookmarks /
    /// favoriteSpots, wie Sticker und Kleidung – nach Neuinstallation wieder da), sonst lokal (PlayerPrefs).
    /// Lokal gespeicherte Lesezeichen werden beim ersten Zugriff mit Anmeldung in den Spielstand übernommen.
    /// </summary>
    public static class TDU2Bookmarks
    {
        public sealed class Mark { public int Id; public string Island = ""; public Vector3 Pos; }
        /// <summary>Höchstzahl (danach sperrt der Film "Lesezeichen setzen" mit m_MaxBookmarks_).</summary>
        public static int Max = 100;

        static List<Mark> _list;
        static string _key;
        static readonly HashSet<string> _fav = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static Online.SaveData Save => Online.TDU2Session.LoggedIn ? Online.TDU2Session.Save : null;
        static string Key => Online.TDU2Session.LoggedIn ? "TDU2.Bookmarks." + Online.TDU2Session.Nick : "TDU2.Bookmarks.offline";

        public static List<Mark> All { get { Load(); return _list; } }
        public static bool CanAdd { get { Load(); return _list.Count < Max; } }

        static void Load()
        {
            var sv = Save;
            string key = Key + (sv != null ? "|srv" : "");
            if (_list != null && _key == key) return;
            _key = key; _list = new List<Mark>(); _fav.Clear();
            // lokal (offline bzw. aus älteren Versionen)
            var local = new List<Mark>(); var localFav = new List<string>();
            string s = "", f = "";
            try { s = PlayerPrefs.GetString(Key, ""); f = PlayerPrefs.GetString(Key + ".fav", ""); } catch (Exception) { }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in s.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length < 5 || !int.TryParse(p[0], out int id)) continue;
                float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out float x);
                float.TryParse(p[3], System.Globalization.NumberStyles.Float, ci, out float y);
                float.TryParse(p[4], System.Globalization.NumberStyles.Float, ci, out float z);
                local.Add(new Mark { Id = id, Island = p[1], Pos = new Vector3(x, y, z) });
            }
            foreach (var c in f.Split(';')) if (c.Length > 0) localFav.Add(c);
            if (sv == null)
            {
                _list.AddRange(local);
                foreach (var c in localFav) _fav.Add(c);
                return;
            }
            sv.bookmarks ??= new List<Online.BookmarkEntry>();
            sv.favoriteSpots ??= new List<string>();
            foreach (var b in sv.bookmarks) if (b != null) _list.Add(new Mark { Id = b.id, Island = b.island ?? "", Pos = new Vector3(b.x, b.y, b.z) });
            foreach (var c in sv.favoriteSpots) if (!string.IsNullOrEmpty(c)) _fav.Add(c);
            // lokale übernehmen (einmalig, danach lokal leeren)
            if (local.Count > 0 || localFav.Count > 0)
            {
                foreach (var m in local)
                {
                    bool dup = false;
                    foreach (var e in _list) if (e.Island == m.Island && (e.Pos - m.Pos).sqrMagnitude < 1f) { dup = true; break; }
                    if (!dup && _list.Count < Max) { m.Id = NextId(); _list.Add(m); }
                }
                foreach (var c in localFav) _fav.Add(c);
                try { PlayerPrefs.DeleteKey(Key); PlayerPrefs.DeleteKey(Key + ".fav"); PlayerPrefs.Save(); } catch (Exception) { }
                Save_();
            }
        }

        static int NextId() { int id = 1; foreach (var m in _list) id = Math.Max(id, m.Id + 1); return id; }

        static void Save_()
        {
            var sv = Save;
            if (sv != null)
            {
                sv.bookmarks = new List<Online.BookmarkEntry>();
                foreach (var m in _list) sv.bookmarks.Add(new Online.BookmarkEntry { id = m.Id, island = m.Island, x = m.Pos.x, y = m.Pos.y, z = m.Pos.z });
                sv.favoriteSpots = new List<string>(_fav);
                Online.TDU2Session.SaveAsync();
                return;
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (var m in _list) sb.Append(m.Id).Append(';').Append(m.Island).Append(';').Append(m.Pos.x.ToString(ci)).Append(';').Append(m.Pos.y.ToString(ci)).Append(';').Append(m.Pos.z.ToString(ci)).Append('\n');
            try { PlayerPrefs.SetString(Key, sb.ToString()); PlayerPrefs.SetString(Key + ".fav", string.Join(";", _fav)); PlayerPrefs.Save(); } catch (Exception) { }
        }

        public static Mark Add(string island, Vector3 game)
        {
            Load();
            if (_list.Count >= Max) return null;
            var mk = new Mark { Id = NextId(), Island = island ?? "", Pos = game };
            _list.Add(mk); Save_();
            return mk;
        }

        public static void Remove(int id) { Load(); _list.RemoveAll(m => m.Id == id); Save_(); }

        public static void SetFavorite(string spotCode, bool on)
        {
            Load();
            if (string.IsNullOrEmpty(spotCode)) return;
            if (on) _fav.Add(spotCode); else _fav.Remove(spotCode);
            Save_();
        }

        public static bool IsFavorite(string spotCode) { Load(); return !string.IsNullOrEmpty(spotCode) && _fav.Contains(spotCode); }
    }
}
