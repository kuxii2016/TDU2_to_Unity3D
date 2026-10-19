using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Map.Flash;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Minimap wie im Spiel (HudMap.cpp): Straßennetz aus &lt;Insel&gt;.bnk / &lt;Insel&gt;_ROAD_DETAIL.IA wird in ein
    /// Render Target gezeichnet (HUDMAP_RT) und im Flash-Clip /HUD/minimap aus frontend/&lt;res&gt;/hud.bnk an Stelle des
    /// Platzhalters "mmap_lure" eingeblendet. Rahmen, Hintergrund, Spielerpfeil ("cursor") und Nordmarke ("north")
    /// sind die Originalgrafiken. Fahrtrichtung oben, Zoom abhängig von der Geschwindigkeit.
    /// Ohne hud.bnk: Ersatzdarstellung als runde Scheibe.
    ///
    /// ROAD_DETAIL.IA ("DI41DAEH"):
    ///   u32 Anzahl Datensätze, danach je Datensatz "ATED", u32 id, u32 nSeg, nSeg x u32 Offset (relativ zum Datensatz)
    ///   Segment: u16 nPunkte, u16 Straßentyp (1 Feldweg, 3 Stadt, 4/5/6 Land/Haupt), u32, u32 Flags, u32 Hash, u8, u8, u16
    ///            Punkt 0: f32 x,y,z + Pad (16 B); je Bézierstück: Kontrollpunkt 1 (12 B), Kontrollpunkt 2 (12 B), Anker (16 B)
    ///   Koordinaten im Spielraum (vor MirrorX/CenterWorld des Streamers).
    /// </summary>
    public sealed class TDU2Minimap : MonoBehaviour
    {
        public TDU2Vehicle Vehicle;
        public TDU2MapStreamer Streamer;
        public bool ShowMinimap = true;
        [Tooltip("Durchmesser relativ zur Bildschirmhöhe")]
        public float Size = 0.26f;
        public float Margin = 0.035f;
        [Tooltip("Sichtradius in Metern im Stand / bei hoher Geschwindigkeit")]
        public float RangeSlow = 170f, RangeFast = 420f;
        public Color Background = new Color(0.03f, 0.07f, 0.12f, 0.62f);
        // Farben wie GamePC.cpr [HudMap] (HIGHWAY 29 180 215, ROAD 0 255 255)
        public Color RoadMain = new Color32(29, 180, 215, 255);
        public Color RoadTown = new Color32(0, 255, 255, 235);
        public Color RoadDirt = new Color32(0, 255, 255, 200);
        public Color Arrow = new Color(1f, 0.62f, 0.05f, 1f);
        public string Error { get; private set; }
        [Tooltip("Online-Blip der Minimap (/HUD/minimap/ic_status: connected/disconnected) – live änderbar")]
        public bool OnlineStatus = false;
        [Tooltip("Spielerzahl der Minimap (/:nb_FRplayers in /HUD/minimap/nb_players/in_out) – live änderbar")]
        public int PlayerCount = 1;
        bool? _appliedOnline; int _appliedPlayers = -1; string _lastOnlineVar, _lastPlayersVar;

        public static string ExtraEnterCode;

        public static bool ExtraEnterLocked;

        public static int RaceCount;

        public static readonly List<Vector3> RaceBlips = new List<Vector3>();
        public static readonly Dictionary<string, string> ExtraSpotNumbers = new Dictionary<string, string>();

        sealed class Segment { public int Type; public Vector3[] Pts; }

        Task<List<Segment>> _task;
        Mesh _mesh;
        RenderTexture _rt;
        Material _mat;
        string _bank;
        float _range;
        GUIStyle _label;
        VmfPlayer _hud;
        string _hudBank;
        Camera _mapCam;
        Material _roadMat;
        Mesh _routeMesh;
        Racing.RaceDefinition _route;
        int _routeNext = -1;
        int _routeBuiltNext = -2;
        Vector3 _meshOrigin;
        Rect? _roadBounds;

        /// <summary>Lage einer Weltposition im Straßennetz der Insel: (0,0) = Südwest, (1,1) = Nordost; null = Netz noch nicht geladen.</summary>
        public Vector2? IslandUV(Vector3 world)
        {
            if (!_roadBounds.HasValue) return null;
            var root = Streamer != null ? Streamer.MapRoot : null;
            var shift = root != null ? root.position - _meshOrigin : Vector3.zero;
            var r = _roadBounds.Value;
            return new Vector2((world.x - shift.x - r.xMin) / Mathf.Max(1f, r.width), (world.z - shift.z - r.yMin) / Mathf.Max(1f, r.height));
        }
        const int MapLayer = 31;
        const float MapPlaneY = -20000f;
        // mmap_lure: Shape 0..108.75 x 0..98 skaliert 1.67/1.84 -> Kartenfeld 181.6 x 180.3 (Bühnenpixel) im Clip minimap
        static readonly Vector2 LureCenter = new Vector2(90.8f, 90.2f);
        const float NorthRadius = 80f;
        static readonly string[] HiddenMinimapParts =
        {
            "objectif_01", "objectif_02", "objectif_03", "objectif_04", "objectif_05", "objectif_06", "objectif_07", "objectif_08",
            "barrage", "gps_status", "emp", "distance", "gps_distance",
        };

        public void Bind(TDU2MapStreamer streamer, TDU2Vehicle vehicle)
        {
            Vehicle = vehicle; Streamer = streamer;
            LoadHud(streamer != null && streamer.Index != null ? streamer.Index.BnkDir : null);
            var bank = streamer != null && streamer.Index != null ? streamer.Index.IslandBank : null;
            if (bank == null || bank == _bank) return;
            _bank = bank; Error = null;
            if (_mesh != null) { Destroy(_mesh); _mesh = null; }
            _task = Task.Run(() =>
            {
                var segs = Load(bank);
                // Topologie (ROAD_MAP): Straßen enden an Knoten mit Position -> Kreuzungen/Kreisel exakt verbinden
                try { _net = IaNetwork.LoadFromBank(bank); } catch (Exception) { _net = null; }
                return segs;
            });
        }

        void LoadHud(string bnkDir)
        {
            if (bnkDir == null) return;
            _bnkDir = bnkDir;
            TDU2Radio.Instance?.SetBnkDir(bnkDir);
            string bank = null;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                bank = CarDatabase.FindFile(Path.Combine(bnkDir, "frontend", res), "hud.bnk");
                if (bank != null) break;
            }
            if (bank == null || bank == _hudBank) return;
            try
            {
                if (_hud == null) { _hud = gameObject.AddComponent<VmfPlayer>(); _hud.GuiDepth = 1; }
                _hud.Load(bank);
                _hudBank = bank;
                TDU2Radio.Instance?.SetHudPlayer(_hud);
                _hud.GotoLabel("/", "hud", false);
                _hud.GotoLabel("/HUD", "16_9", false);
                _hud.GotoLabel("/HUD/minimap", "default", false);
                // nur die Minimap aus hud.vmf zeigen (Rest des Spiel-HUDs: Rennen, Telefon, Hilfe ...)
                foreach (var d in _hud.Root.List.Values) d.Visible = string.Equals(d.Name, "HUD", StringComparison.OrdinalIgnoreCase);
                var hudClip = _hud.Find("/HUD");
                if (hudClip?.Sprite != null)
                    foreach (var d in hudClip.Sprite.List.Values)
                        d.Visible = string.Equals(d.Name, "minimap", StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, "enter", StringComparison.OrdinalIgnoreCase);
                _spotChar = _hud.Find("/HUD/enter/Spot")?.CharId ?? 0;
                _hud.GotoLabel("/HUD/enter", "off", false);
                _enterShown = false;
                foreach (var n in HiddenMinimapParts) _hud.SetVisible("/HUD/minimap/" + n, false);
                // Platzhaltertexte der Navi-Anzeige ("2500" + Einheit) leeren – ohne aktive Route zeigt das Spiel nichts
                _hud.SetVariable("/:obj_Distance", ""); _hud.SetVariable("/:gps_Unit", "");
                _hud.SetPosition("/HUD/minimap/cursor", LureCenter);
                // Online-Status (Blip) und Spielerzahl wie im Spiel – angewendet in UpdateOnlineDisplay()
                _appliedOnline = null; _appliedPlayers = -1;
                _hud.SetVariable("/:IsOnline", OnlineStatus ? "1" : "0");
                _hud.SetVariable("/:NbPlayers", PlayerCount.ToString());
                _lastOnlineVar = _hud.GetVariable("/:IsOnline"); _lastPlayersVar = _hud.GetVariable("/:NbPlayers");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TDU2] Minimap: hud.bnk nicht nutzbar, Ersatzdarstellung. " + ex.Message);
                if (_hud != null) { Destroy(_hud); _hud = null; }
                _hudBank = null;
            }
        }

        volatile IaNetwork _net;

        static List<Segment> Load(string bankPath)
        {
            var bank = BnkArchive.Open(bankPath);
            var e = bank.FindByExt(".ia", "_ROAD_DETAIL");
            if (e == null) foreach (var x in bank.Entries) if (x.Name.IndexOf("ROAD_DETAIL", StringComparison.OrdinalIgnoreCase) >= 0) { e = x; break; }
            if (e == null) throw new FileNotFoundException("ROAD_DETAIL.IA nicht in " + Path.GetFileName(bankPath));
            return Parse(bank.Read(e));
        }

        static List<Segment> Parse(byte[] d)
        {
            var list = new List<Segment>();
            if (d.Length < 12 || d[0] != (byte)'D' || d[1] != (byte)'I') throw new InvalidDataException("kein ROAD_DETAIL (DI41DAEH)");
            int n = BitConverter.ToInt32(d, 8), p = 12;
            for (int r = 0; r < n && p + 12 <= d.Length; r++)
            {
                if (d[p] != (byte)'A' || d[p + 1] != (byte)'T' || d[p + 2] != (byte)'E' || d[p + 3] != (byte)'D') break;
                int ns = BitConverter.ToInt32(d, p + 8);
                int end = p + 12 + 4 * ns;
                for (int s = 0; s < ns; s++)
                {
                    int q = p + BitConverter.ToInt32(d, p + 12 + 4 * s);
                    if (q < 0 || q + 36 > d.Length) continue;
                    int np = BitConverter.ToUInt16(d, q), type = BitConverter.ToUInt16(d, q + 2);
                    q += 20;
                    int pieces = Math.Max(0, (np - 1) / 3);
                    if (q + 16 + pieces * 40 > d.Length) continue;
                    var pts = new Vector3[1 + 3 * pieces];
                    pts[0] = V(d, q); q += 16;
                    for (int k = 0; k < pieces; k++)
                    {
                        pts[1 + 3 * k] = V(d, q); pts[2 + 3 * k] = V(d, q + 12); pts[3 + 3 * k] = V(d, q + 24);
                        q += 40;
                    }
                    if (pieces > 0) list.Add(new Segment { Type = type, Pts = pts });
                    if (q > end) end = q;
                }
                p = end;
            }
            return list;
        }

        static Vector3 V(byte[] d, int o) => new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));

        // ---- Straßenpunkte (Spielkoordinaten) für die große Karte: Teleport nur auf Straßen wie im Spiel
        readonly Dictionary<long, List<Vector3>> _roadGrid = new Dictionary<long, List<Vector3>>();
        const float RoadCell = 256f;
        static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        void BuildRoadIndex(List<Segment> segs)
        {
            _roadGrid.Clear();
            foreach (var s in segs)
            {
                int pieces = (s.Pts.Length - 1) / 3;
                for (int k = 0; k < pieces; k++)
                {
                    Vector3 a = s.Pts[3 * k], c1 = s.Pts[3 * k + 1], c2 = s.Pts[3 * k + 2], b = s.Pts[3 * k + 3];
                    int n = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / 8f), 1, 64);   // alle ~8 m ein Punkt
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)n, u = 1f - t;
                        var p = u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
                        var key = CellKey(Mathf.FloorToInt(p.x / RoadCell), Mathf.FloorToInt(p.z / RoadCell));
                        if (!_roadGrid.TryGetValue(key, out var l)) _roadGrid[key] = l = new List<Vector3>();
                        l.Add(p);
                    }
                }
            }
        }

        /// <summary>Nächster Straßenpunkt (Spielkoordinaten, mit Höhe) im Umkreis maxDist; false = keine Straße in der Nähe.</summary>
        public bool NearestRoad(Vector3 game, float maxDist, out Vector3 point)
        {
            point = default;
            if (_roadGrid.Count == 0) return false;
            int r = Mathf.CeilToInt(maxDist / RoadCell);
            int cx = Mathf.FloorToInt(game.x / RoadCell), cz = Mathf.FloorToInt(game.z / RoadCell);
            float best = maxDist * maxDist; bool found = false;
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (!_roadGrid.TryGetValue(CellKey(x, z), out var l)) continue;
                    foreach (var p in l)
                    {
                        float dx = p.x - game.x, dz = p.z - game.z, d = dx * dx + dz * dz;
                        if (d < best) { best = d; point = p; found = true; }
                    }
                }
            return found;
        }

        List<Segment> _segs;
        float _wMul = 1f;

        void BuildMesh(List<Segment> segs)
        {
            _segs = segs;
            _wMul = 1f;
            var root = Streamer != null ? Streamer.MapRoot : null;
            _mesh = BuildRoadMesh(segs, root, null);
            _meshOrigin = root != null ? root.position : Vector3.zero;
            // Ausdehnung des Straßennetzes (Kartenebene) – z.B. für die Positionsmarke der Inselkarte im Pausemenü
            var vb = _mesh.vertices;
            if (vb.Length > 0)
            {
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                foreach (var v in vb) { x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x); z0 = Mathf.Min(z0, v.z); z1 = Mathf.Max(z1, v.z); }
                _roadBounds = Rect.MinMaxRect(x0, z0, x1, z1);
            }
        }

        /// <summary>Straßennetz geladen (für die große Karte).</summary>
        public bool HasRoads => _segs != null && _segs.Count > 0;
        /// <summary>Material der Straßen (Vertexfarbe, ZTest Always – liegt über der Welt).</summary>
        public Material RoadMaterial { get { if (!EnsureMaterial()) return null; if (_roadMat == null) { _roadMat = new Material(_mat.shader) { hideFlags = HideFlags.HideAndDontSave }; _roadMat.mainTexture = Texture2D.whiteTexture; _roadMat.SetFloat("_Circle", 0f); } return _roadMat; } }

        /// <summary>
        /// Straßennetz für die große Karte: Breite × <paramref name="widthMul"/> (Mindestbreite in Bildpunkten), Höhe aus dem
        /// Höhenraster (damit die Straßen bei geneigter Kamera auf dem Gelände liegen). Weltkoordinaten zum Zeitpunkt des
        /// Aufrufs; mit Matrix4x4.Translate(MapRoot.position - origin) zeichnen (Floating Origin).
        /// </summary>
        public Mesh BuildMapRoads(float widthMul, out Vector3 origin)
        {
            var root = Streamer != null ? Streamer.MapRoot : null;
            origin = root != null ? root.position : Vector3.zero;
            if (_segs == null) return null;
            float keep = _wMul;
            _wMul = Mathf.Max(1f, widthMul);
            try
            {
                var st = Streamer;
                return BuildRoadMesh(_segs, root, (x, z) => st != null && st.TryGetGroundHeight(new Vector3(x, 0f, z), out float y) ? y + 1f : 0f);
            }
            finally { _wMul = keep; }
        }

        Mesh BuildRoadMesh(List<Segment> segs, Transform root, Func<float, float, float> height)
        {
            var verts = new List<Vector3>(1 << 18);
            var cols = new List<Color32>(1 << 18);
            var idx = new List<int>(1 << 19);
            var ends = new List<(Vector2 p, float half, Color c, Vector2 dir)>();
            var samples = new List<(Vector2 p, int seg)>();   // Straßenpunkte für die Verlängerung offener Enden
            // Reihenfolge: Feldwege unten, Hauptstraßen oben
            foreach (int pass in new[] { 1, 3, 6 })
            {
                foreach (var s in segs)
                {
                    int cls = s.Type == 1 ? 1 : s.Type == 3 ? 3 : 6;
                    if (cls != pass) continue;
                    Color c = cls == 1 ? RoadDirt : cls == 3 ? RoadTown : RoadMain;
                    float half = (cls == 1 ? 3.2f : cls == 3 ? 3.8f : 5.5f) * _wMul;
                    Vector2 prev = Map(root, s.Pts[0]);
                    int segId = ends.Count;
                    // Endrichtung nach außen (Bezier-Tangente am Anfang/Ende)
                    Vector2 p0 = prev, p1 = Map(root, s.Pts[1]), pn = Map(root, s.Pts[s.Pts.Length - 1]), pm = Map(root, s.Pts[s.Pts.Length - 2]);
                    ends.Add((p0, half, c, (p0 - p1).sqrMagnitude > 1e-6f ? (p0 - p1).normalized : Vector2.zero));
                    ends.Add((pn, half, c, (pn - pm).sqrMagnitude > 1e-6f ? (pn - pm).normalized : Vector2.zero));
                    samples.Add((prev, segId));
                    for (int k = 0; k + 3 < s.Pts.Length; k += 3)
                    {
                        Vector2 a = Map(root, s.Pts[k]), b = Map(root, s.Pts[k + 1]), cc = Map(root, s.Pts[k + 2]), e = Map(root, s.Pts[k + 3]);
                        float len = (b - a).magnitude + (cc - b).magnitude + (e - cc).magnitude;
                        int steps = Mathf.Clamp(Mathf.CeilToInt(len / 20f), 1, 16);
                        for (int i = 1; i <= steps; i++)
                        {
                            float t = i / (float)steps, u = 1 - t;
                            Vector2 pt = u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * cc + t * t * t * e;
                            AddQuad(verts, cols, idx, prev, pt, half, c);
                            prev = pt;
                            samples.Add((pt, segId));
                        }
                    }
                }
            }
            if (_net != null && _net.Nodes.Count > 0) AddNodeJunctions(root, ends, samples, verts, cols, idx);
            else AddJunctions(ends, samples, verts, cols, idx);
            // Kartenebene in die Welt-XZ-Ebene (Minimap: weit unterhalb der Insel, nur die Minimap-Kamera sieht sie)
            for (int i = 0; i < verts.Count; i++) { var q = verts[i]; verts[i] = new Vector3(q.x, height != null ? height(q.x, q.y) : 0f, q.y); }
            var mesh = new Mesh { name = height != null ? "TDU2_MapRoads" : "TDU2_Minimap", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(idx, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return mesh;
        }

        /// <summary>
        /// Kreuzungen: ROAD_DETAIL endet vor dem Kreuzungsmittelpunkt (die Kreuzungsflächen sind eigene Meshes) –
        /// bei normalen Kreuzungen 10–20 m, bei großen Kreuzungen/Kreiseln 20–45 m.
        /// Enden werden verbunden, wenn sie nah beieinander liegen (&lt; 22 m) oder sich bis 45 m gegenüberstehen
        /// (beide zeigen aufeinander zu); jede Gruppe wird mit ihrem Mittelpunkt verbunden. Übrige offene Enden werden
        /// in Fahrtrichtung bis zur nächsten Straße (≤ 45 m, Kegel ±37°) verlängert.
        /// </summary>
        static void AddJunctions(List<(Vector2 p, float half, Color c, Vector2 dir)> ends, List<(Vector2 p, int seg)> samples,
            List<Vector3> verts, List<Color32> cols, List<int> idx)
        {
            const float RNear = 22f, RFar = 45f;
            int n = ends.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var grid = new Dictionary<long, List<int>>();
            long Key(int gx, int gz) => ((long)gx << 32) ^ (uint)gz;
            for (int i = 0; i < n; i++)
            {
                int gx = Mathf.FloorToInt(ends[i].p.x / RFar), gz = Mathf.FloorToInt(ends[i].p.y / RFar);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue(Key(gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                if ((i >> 1) == (j >> 1)) continue;   // beide Enden desselben Segments
                                var v = ends[j].p - ends[i].p; float dist = v.magnitude;
                                if (dist >= RFar) continue;
                                bool join = dist < RNear;
                                if (!join && dist > 1e-3f)
                                {
                                    var u = v / dist;
                                    join = Vector2.Dot(ends[i].dir, u) > 0.5f && Vector2.Dot(ends[j].dir, -u) > 0.5f;
                                }
                                if (join) parent[Find(i)] = Find(j);
                            }
                if (!grid.TryGetValue(Key(gx, gz), out var own)) grid[Key(gx, gz)] = own = new List<int>();
                own.Add(i);
            }
            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!clusters.TryGetValue(r, out var l)) clusters[r] = l = new List<int>();
                l.Add(i);
            }
            var singles = new List<int>();
            foreach (var cl in clusters.Values)
            {
                if (cl.Count < 2) { singles.Add(cl[0]); continue; }
                Vector2 c = Vector2.zero; float maxHalf = 0f; Color col = Color.white; float spread = 0f;
                foreach (int i in cl) c += ends[i].p;
                c /= cl.Count;
                foreach (int i in cl)
                {
                    spread = Mathf.Max(spread, (ends[i].p - c).magnitude);
                    if (ends[i].half > maxHalf) { maxHalf = ends[i].half; col = ends[i].c; }
                }
                if (spread < 1f) continue;   // reine Fortsetzung (Enden liegen aufeinander)
                foreach (int i in cl)
                    if ((ends[i].p - c).sqrMagnitude > 0.25f) AddQuad(verts, cols, idx, ends[i].p, c, ends[i].half, ends[i].c);
                AddDisc(verts, cols, idx, c, maxHalf * 1.15f, col);
            }

            // offene Enden in Fahrtrichtung bis zur nächsten Straße verlängern
            if (singles.Count == 0) return;
            var sgrid = new Dictionary<long, List<int>>();
            for (int k = 0; k < samples.Count; k++)
            {
                long key = Key(Mathf.FloorToInt(samples[k].p.x / RFar), Mathf.FloorToInt(samples[k].p.y / RFar));
                if (!sgrid.TryGetValue(key, out var l)) sgrid[key] = l = new List<int>();
                l.Add(k);
            }
            foreach (int i in singles)
            {
                var e = ends[i];
                if (e.dir == Vector2.zero) continue;
                int gx = Mathf.FloorToInt(e.p.x / RFar), gz = Mathf.FloorToInt(e.p.y / RFar);
                float best = float.MaxValue; Vector2 target = Vector2.zero;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (sgrid.TryGetValue(Key(gx + dx, gz + dz), out var l))
                            foreach (int k in l)
                            {
                                if (samples[k].seg == (i & ~1)) continue;   // eigenes Segment
                                var v = samples[k].p - e.p; float dist = v.magnitude;
                                if (dist < 1f || dist >= RFar || dist >= best) continue;
                                if (Vector2.Dot(e.dir, v / dist) > 0.8f) { best = dist; target = samples[k].p; }
                            }
                if (best < RFar)
                {
                    AddQuad(verts, cols, idx, e.p, target, e.half, e.c);
                    AddDisc(verts, cols, idx, target, e.half * 1.1f, e.c);
                }
            }
        }

        /// <summary>
        /// Kreuzungen aus dem Verkehrsnetz (ROAD_MAP): jede Straße läuft von ihrem Start- zum Endknoten; ROAD_DETAIL hört
        /// vor dem Knoten auf (die Kreuzungsfläche ist ein eigenes Mesh). Jedes Straßenende wird mit der Position seines
        /// Knotens verbunden, am Knoten eine Scheibe – Kreisel (Ringstraßen zwischen Knoten) und große Kreuzungen stimmen so.
        /// Breite/Farbe vom passenden ROAD_DETAIL-Ende.
        /// </summary>
        void AddNodeJunctions(Transform root, List<(Vector2 p, float half, Color c, Vector2 dir)> ends, List<(Vector2 p, int seg)> samples,
            List<Vector3> verts, List<Color32> cols, List<int> idx)
        {
            // Kreisel: alle Zufahrten enden am selben Knoten in der Kreiselmitte, der Ring selbst ist eigene Straße.
            // Eine Speiche zur Mitte füllte den Kreisel aus ("Stern"). Lange Lücken (> NodeGap) werden deshalb an die
            // nächste andere Straße angeschlossen (Ring / Hauptstraße), nur kurze direkt an den Knoten.
            const float NodeGap = 14f, JoinMax = 30f, SCell = 16f;
            var sgrid = new Dictionary<long, List<int>>();
            long SKey(int gx, int gz) => ((long)gx << 32) ^ (uint)gz;
            for (int i = 0; i < samples.Count; i++)
            {
                long k = SKey(Mathf.FloorToInt(samples[i].p.x / SCell), Mathf.FloorToInt(samples[i].p.y / SCell));
                if (!sgrid.TryGetValue(k, out var l)) sgrid[k] = l = new List<int>();
                l.Add(i);
            }
            bool NearestOther(Vector2 p, int seg, Vector2 dir, float maxD, out Vector2 hit)
            {
                hit = default; float best = maxD * maxD; bool ok = false;
                int r = Mathf.CeilToInt(maxD / SCell);
                int gx = Mathf.FloorToInt(p.x / SCell), gz = Mathf.FloorToInt(p.y / SCell);
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                        if (sgrid.TryGetValue(SKey(gx + dx, gz + dz), out var l))
                            foreach (int i in l)
                            {
                                if (samples[i].seg == seg) continue;
                                var v = samples[i].p - p; float d = v.sqrMagnitude;
                                if (d >= best) continue;
                                // nicht nach hinten (in die eigene Straße zurück)
                                if (dir.sqrMagnitude > 0.5f && d > 4f && Vector2.Dot(v, dir) < 0f) continue;
                                best = d; hit = samples[i].p; ok = true;
                            }
                return ok;
            }
            var net = _net;
            const float Cell = 8f;
            var grid = new Dictionary<long, List<int>>();
            long Key(int gx, int gz) => ((long)gx << 32) ^ (uint)gz;
            for (int i = 0; i < ends.Count; i++)
            {
                long k = Key(Mathf.FloorToInt(ends[i].p.x / Cell), Mathf.FloorToInt(ends[i].p.y / Cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(i);
            }
            bool Style(Vector2 p, out float half, out Color col) => StyleIdx(p, out half, out col, out _);
            bool StyleIdx(Vector2 p, out float half, out Color col, out int endIdx)
            {
                half = 4f * _wMul; col = RoadTown; float best = 3f * 3f * _wMul * _wMul; bool ok = false; endIdx = -1;
                int gx = Mathf.FloorToInt(p.x / Cell), gz = Mathf.FloorToInt(p.y / Cell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue(Key(gx + dx, gz + dz), out var l))
                            foreach (int i in l)
                            {
                                float d = (ends[i].p - p).sqrMagnitude;
                                if (d < best) { best = d; half = ends[i].half; col = ends[i].c; ok = true; endIdx = i; }
                            }
                return ok;
            }
            var nodeHalf = new Dictionary<int, (float half, Color c)>();
            foreach (var road in net.Roads.Values)
            {
                if (road.Pieces.Count == 0) continue;
                var first = road.Pieces[0].Points; var last = road.Pieces[road.Pieces.Count - 1].Points;
                if (first == null || first.Length == 0 || last == null || last.Length == 0) continue;
                for (int side = 0; side < 2; side++)
                {
                    int nodeId = side == 0 ? road.StartNode : road.EndNode;
                    if (nodeId == 0 || !net.Nodes.TryGetValue(nodeId, out var node)) continue;
                    Vector2 e = Map(root, side == 0 ? first[0] : last[last.Length - 1]);
                    Vector2 c = Map(root, node.Pos);
                    if (!StyleIdx(e, out float half, out Color col, out int ei)) { half = 4f * _wMul; col = RoadTown; }
                    float gap = (e - c).magnitude;
                    if (gap > NodeGap)
                    {
                        int seg = ei >= 0 ? ei - (ei & 1) : -1;
                        Vector2 dir = ei >= 0 ? ends[ei].dir : (c - e) / Mathf.Max(gap, 1e-3f);
                        if (NearestOther(e, seg, dir, Mathf.Min(JoinMax, gap), out var hit))
                        {
                            if ((hit - e).sqrMagnitude > 0.25f) AddQuad(verts, cols, idx, e, hit, half, col);
                            continue;   // Kreisel/Einmündung: nicht zur Knotenmitte
                        }
                        if (gap > 45f) continue;
                    }
                    if (gap > 0.5f) AddQuad(verts, cols, idx, e, c, half, col);
                    if (!nodeHalf.TryGetValue(nodeId, out var nh) || half > nh.half) nodeHalf[nodeId] = (half, col);
                }
            }
            foreach (var kv in nodeHalf)
                if (net.Nodes.TryGetValue(kv.Key, out var node)) AddDisc(verts, cols, idx, Map(root, node.Pos), kv.Value.half * 1.1f, kv.Value.c);
        }

        static void AddDisc(List<Vector3> verts, List<Color32> cols, List<int> idx, Vector2 c, float r, Color col)
        {
            int b0 = verts.Count;
            verts.Add(c); cols.Add(col);
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                verts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r); cols.Add(col);
            }
            for (int k = 0; k < 8; k++) { idx.Add(b0); idx.Add(b0 + 1 + k); idx.Add(b0 + 1 + (k + 1) % 8); }
        }

        // Kartenebene: x = Welt-X, y = Welt-Z (Spiegelung/Zentrierung des Streamers bereits enthalten)
        static Vector2 Map(Transform root, Vector3 game)
        {
            var w = root != null ? root.TransformPoint(game) : game;
            return new Vector2(w.x, w.z);
        }

        static void AddQuad(List<Vector3> v, List<Color32> c, List<int> idx, Vector2 a, Vector2 b, float half, Color col)
        {
            var dir = b - a;
            float l = dir.magnitude;
            if (l < 1e-3f) return;
            dir /= l;
            var nrm = new Vector2(-dir.y, dir.x) * half;
            var ext = dir * half * 0.6f;   // Überstand schließt Fugen in Kurven
            a -= ext; b += ext;
            int i0 = v.Count;
            v.Add(a - nrm); v.Add(a + nrm); v.Add(b + nrm); v.Add(b - nrm);
            Color32 cc = col; c.Add(cc); c.Add(cc); c.Add(cc); c.Add(cc);
            idx.Add(i0); idx.Add(i0 + 1); idx.Add(i0 + 2); idx.Add(i0); idx.Add(i0 + 2); idx.Add(i0 + 3);
        }

        bool EnsureMaterial()
        {
            if (_mat != null) return true;
            var sh = Shader.Find("TDU2/Minimap");
            if (sh == null) { Error = "Shader TDU2/Minimap fehlt"; return false; }
            _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        /// <summary>
        /// Online-Blip und Spielerzahl wie das Spiel setzen (das Spiel steuert die Clips direkt, im Film gibt es dafür keine Variable):
        ///   /HUD/minimap/ic_status            -> Label "connected" / "disconnected"
        ///   /:nb_FRplayers                    -> Text der Spielerzahl
        ///   /HUD/minimap/nb_players/in_out    -> "in" einblenden (bei Änderung der Zahl)
        /// Änderbar über OnlineStatus/PlayerCount (Inspector) oder im Flash Debugger über die Variablen /:IsOnline und /:NbPlayers.
        /// </summary>
        void UpdateOnlineDisplay()
        {
            // Änderung aus dem Flash Debugger übernehmen
            string ov = _hud.GetVariable("/:IsOnline"), pv = _hud.GetVariable("/:NbPlayers");
            if (ov != _lastOnlineVar) { _lastOnlineVar = ov; OnlineStatus = ov == "1" || string.Equals(ov, "true", StringComparison.OrdinalIgnoreCase); }
            if (pv != _lastPlayersVar) { _lastPlayersVar = pv; if (int.TryParse(pv, out int n)) PlayerCount = n; }

            if (_appliedOnline != OnlineStatus)
            {
                _appliedOnline = OnlineStatus;
                _hud.GotoLabel("/HUD/minimap/ic_status", OnlineStatus ? "connected" : "disconnected", true);
                _hud.SetVariable("/:IsOnline", OnlineStatus ? "1" : "0"); _lastOnlineVar = _hud.GetVariable("/:IsOnline");
                _hud.SetVariable("/:nb_FRplayers", PlayerCount.ToString());
                _hud.GotoLabel("/HUD/minimap/nb_players/in_out", OnlineStatus ? "in" : "out", true);
            }
            if (_appliedPlayers != PlayerCount)
            {
                bool first = _appliedPlayers < 0;
                _appliedPlayers = PlayerCount;
                _hud.SetVariable("/:nb_FRplayers", PlayerCount.ToString());
                _hud.SetVariable("/:nb_players", PlayerCount.ToString());
                _hud.SetVariable("/:NbPlayers", PlayerCount.ToString()); _lastPlayersVar = _hud.GetVariable("/:NbPlayers");
                // Spielerzahl einblenden (offline nicht – wie im Spiel nur in einer Online-Sitzung)
                if (OnlineStatus || !first) _hud.GotoLabel("/HUD/minimap/nb_players/in_out", "in", true);
            }
            TDU2Radio.Instance.canPlayRadio = true;
        }

        void LateUpdate()
        {
            if (_task != null && _task.IsCompleted)
            {
                if (_task.IsFaulted) { Error = _task.Exception?.GetBaseException().Message; Debug.LogWarning("[TDU2] Minimap: " + Error); }
                else
                {
                    try { BuildMesh(_task.Result); BuildRoadIndex(_task.Result); Debug.Log($"[TDU2] Minimap: {_task.Result.Count} Straßensegmente"); }
                    catch (Exception ex) { Error = ex.Message; Debug.LogWarning("[TDU2] Minimap: " + ex); }
                }
                _task = null;
            }
            bool show = ShowMinimap && Vehicle != null && Vehicle.isActiveAndEnabled;
            if (_hud != null) _hud.Visible = show;
            if (_hud != null) UpdateOnlineDisplay();
            if (!show || _mesh == null || !EnsureMaterial()) return;

            int px = Mathf.Clamp(Mathf.RoundToInt(Screen.height * Size), 64, 1024);
            if (_hud != null)
            {
                var r = _hud.ScreenRect("/HUD/minimap/mmap_lure");
                if (r.HasValue) px = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(r.Value.width, r.Value.height)), 64, 1024);
            }
            if (_rt == null || _rt.width != px)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(px, px, 0, RenderTextureFormat.ARGB32) { name = "TDU2_Minimap", antiAliasing = 4, hideFlags = HideFlags.DontSave };
                _rt.Create();
            }
            float target = Mathf.Lerp(RangeSlow, RangeFast, Mathf.Clamp01(Vehicle.SpeedKmh / 220f));
            _range = _range <= 0 ? target : Mathf.Lerp(_range, target, 1f - Mathf.Exp(-Time.deltaTime * 1.5f));

            var pos = Vehicle.transform.position;
            float heading = Heading();
            RenderMap(pos);
            if (_hud != null)
            {
                _hud.SetDynamicTexture("mmap_lure", _rt, true);
                // Nordmarke wandert am Kartenrand entlang (Bühne: y nach unten)
                float h = heading * Mathf.Deg2Rad;
                var dir = new Vector2(-Mathf.Sin(h), -Mathf.Cos(h));
                float k = NorthRadius / Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.y));
                _hud.SetPosition("/HUD/minimap/north", LureCenter + dir * k);
                UpdateSpotIcons(pos);
            }
        }

        /// <summary>
        /// Straßen mit einer eigenen Ortho-Kamera von oben in die RenderTexture zeichnen: Bild-oben = Fahrtrichtung.
        /// (Kamera statt GL-Matrizen: Unity kümmert sich um die Y-Spiegelung der RenderTexture je Grafik-API.)
        /// </summary>
        void RenderMap(Vector3 carPos)
        {
            if (_mapCam == null)
            {
                var go = new GameObject("TDU2 Minimap Camera") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                _mapCam = go.AddComponent<Camera>();
                _mapCam.enabled = false;
                _mapCam.orthographic = true;
                _mapCam.clearFlags = CameraClearFlags.SolidColor;
                _mapCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _mapCam.cullingMask = 1 << MapLayer;
                _mapCam.nearClipPlane = 1f; _mapCam.farClipPlane = 200f;
                _mapCam.allowHDR = false; _mapCam.allowMSAA = false;
                _mapCam.useOcclusionCulling = false;
            }
            if (_roadMat == null) { _roadMat = new Material(_mat.shader) { hideFlags = HideFlags.HideAndDontSave }; _roadMat.mainTexture = Texture2D.whiteTexture; _roadMat.SetFloat("_Circle", 0f); }
            var root = Streamer != null ? Streamer.MapRoot : null;
            var shift = root != null ? root.position - _meshOrigin : Vector3.zero;   // Floating Origin
            shift.y = 0f;
            var fwd = Vehicle.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            _mapCam.targetTexture = _rt;
            _mapCam.orthographicSize = _range;
            _mapCam.transform.SetPositionAndRotation(new Vector3(carPos.x, MapPlaneY + 100f, carPos.z), Quaternion.LookRotation(Vector3.down, fwd.normalized));
            Graphics.DrawMesh(_mesh, Matrix4x4.Translate(new Vector3(shift.x, MapPlaneY, shift.z)), _roadMat, MapLayer, _mapCam, 0, null, false, false);
            EnsureRouteMesh();
            DrawRouteMesh();
            _mapCam.Render();
        }

        // ---------------------------------------------------------------- Spot-Icons + Einfahrt-Anzeige (hud.vmf)
        int _spotChar;
        bool _enterShown;
        string _enterLabel;
        const int SpotDepthBase = 30000, MaxSpotIcons = 14;
        const float LureHalf = 90f;
        readonly string[] _slotLabel = new string[MaxSpotIcons];
        readonly int[] _slotLogo = new int[MaxSpotIcons];

        /// <summary>Datenbank für die Händler-Logos (Marke des Bestands); vom Fahrzeug-Manager gesetzt.</summary>
        public CarDatabase Database;
        string _bnkDir;
        Texture2D _brandAtlas; bool _brandAtlasTried;
        readonly Dictionary<int, Texture2D> _logoTex = new Dictionary<int, Texture2D>();

        /// <summary>
        /// Markenlogo als eigene Textur: frontend/AllRes/logotexturepage.bnk -> brands_map.2db (1024 x 256, 16 x 4 Kacheln à 64 px,
        /// Index = DBBrands._logo_global_index). Wie die übrigen .2db-Rohdaten liegt die oberste Bildzeile bei v = 0.
        /// </summary>
        Texture2D LogoTexture(int index)
        {
            if (index < 0) return null;
            if (_logoTex.TryGetValue(index, out var t)) return t;
            if (!_brandAtlasTried)
            {
                _brandAtlasTried = true;
                try
                {
                    var f = _bnkDir != null ? CarDatabase.FindFile(Path.Combine(_bnkDir, "frontend", "AllRes"), "logotexturepage.bnk") : null;
                    if (f != null)
                    {
                        var bank = BnkArchive.Open(f);
                        var e = bank.Find("brands_map.2db");
                        if (e != null) _brandAtlas = TextureDef.Parse(bank.Read(e), "brands_map").CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp, false, true);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[TDU2] Minimap: Markenlogos nicht ladbar: " + ex.Message); }
            }
            if (_brandAtlas == null) return null;
            int cols = Mathf.Max(1, _brandAtlas.width / 64), col = index % cols, row = index / cols;
            if ((row + 1) * 64 > _brandAtlas.height) return null;
            if (SystemInfo.copyTextureSupport != CopyTextureSupport.None)
            {
                // Blockweise GPU-Kopie im DXT-Format (64 px = 16 Blöcke, Zeile 0 der Rohdaten = y 0)
                t = new Texture2D(64, 64, _brandAtlas.format, false) { name = "Logo_" + index, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                t.Apply(false, true);
                Graphics.CopyTexture(_brandAtlas, 0, 0, col * 64, row * 64, 64, 64, t, 0, 0, 0, 0);
            }
            else
            {
                t = new Texture2D(64, 64, TextureFormat.RGBA32, false) { name = "Logo_" + index, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                t.SetPixels(_brandAtlas.GetPixels(col * 64, row * 64, 64, 64));
                t.Apply(false, true);
            }
            _logoTex[index] = t;
            return t;
        }

        /// <summary>Händler-Icon wie im Spiel: "ic" auf "brands" + Logo in ic/logo/lure; ohne Logo das Auto-Symbol ("lod").</summary>
        /// <summary>Markenlogo eines Händler-Spots (Code aus spots.bin) aus logotexturepage.bnk; null wenn unbekannt.</summary>
        public Texture2D DealerLogoTexture(string code)
        {
            int li = Database != null && code != null ? Database.DealerLogo(code) : -1;
            return LogoTexture(li);
        }

        void ApplyDealerLogo(string spotPath, string code)
        {
            int li = Database != null && code != null ? Database.DealerLogo(code) : -1;
            var tex = LogoTexture(li);
            if (tex != null)
            {
                _hud.GotoLabel(spotPath + "/ic", "brands", false);
                _hud.SetClipTexture(spotPath + "/ic/logo/lure", tex);
            }
            else _hud.GotoLabel(spotPath + "/ic", "lod", false);
        }
        [Tooltip("Größe der Spot-Icons auf der Minimap (1 = Originalgröße)")]
        public float SpotIconScale = 0.55f;

        /// <summary>Spot-Typ (Code aus spots.bin) -> Label des Spot-Icons (Sprite "Spot" in hud.vmf, wie auf der großen Karte).</summary>
        /// <summary>Zusätzliche Spots (z.B. Meisterschaftsrennen, siehe Racing.TDU2Championships): Code -> Symbol-Label / Anzeigename.</summary>
        public static readonly Dictionary<string, string> ExtraSpotLabels = new Dictionary<string, string>();
        public static readonly Dictionary<string, string> ExtraSpotNames = new Dictionary<string, string>();
        /// <summary>Bild für den Karten-Tooltip zusätzlicher Spots: Pfad unter frontend/AllRes/MapScreens ohne .bnk (z.B. "contest/cl-4-1").</summary>
        public static readonly Dictionary<string, string> ExtraSpotPictures = new Dictionary<string, string>();

        public static string SpotLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (ExtraSpotLabels.TryGetValue(code, out var extra)) return extra;
            var c = code.ToUpperInvariant();
            if (c.StartsWith("ECDC")) return "carDealer";
            if (c.StartsWith("ECD")) return "carDealer";
            if (c.StartsWith("EBD")) return "bikeDealer";
            if (c.StartsWith("EPH")) return "home";
            if (c.StartsWith("ECH")) return "club";
            if (c.StartsWith("ETUN")) return "tuning";
            if (c.StartsWith("ECW")) return "carWash";
            if (c.StartsWith("ESTICK")) return "carPaint";
            if (c.StartsWith("ESTOL")) return "stolenCar";
            if (c.StartsWith("ERS")) return "rschool";   // Renn-Akademie (ERS_A/B/C)
            if (c.StartsWith("ERE")) return "estates";
            if (c.StartsWith("EAS")) return "clothes";
            if (c.StartsWith("EHD")) return "hair";
            if (c.StartsWith("ESUR")) return "surgery";
            if (c.StartsWith("EGAM")) return "gamblingHall";
            if (c.StartsWith("ELANDCAA")) return "casino";   // Hawaii: Landeplatz, Flug zur Casino-Insel
            if (c.StartsWith("ELAND")) return "airport";     // Casino-Insel: Landeplatz, Rückflug nach Hawaii
            if (c.StartsWith("EAIR")) return "airport";
            if (c.StartsWith("ECAS")) return "casino";
            if (c.StartsWith("EUC")) return "auctionHall";
            if (c.StartsWith("EPHOT")) return "photograph";
            return null;
        }

        void UpdateSpotIcons(Vector3 carPos)
        {
            if (_hud == null || _spotChar == 0 || Streamer == null || Streamer.Spots == null || Streamer.MapRoot == null) return;
            var fwd = Vehicle.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            float k = LureHalf / Mathf.Max(1f, _range);
            var list = new List<(float d, Vector2 p, string label, string code)>();
            foreach (var s in Streamer.Spots.Spots)
            {
                string label = SpotLabel(s.Code);
                if (label == null) continue;
                var w = Streamer.MapRoot.TransformPoint(s.Position);
                var rel = w - carPos; rel.y = 0f;
                float x = Vector3.Dot(rel, right) * k, y = Vector3.Dot(rel, fwd) * k;
                if (Mathf.Abs(x) > LureHalf - 6f || Mathf.Abs(y) > LureHalf - 6f) continue;   // außerhalb des Kartenfelds
                list.Add((rel.sqrMagnitude, LureCenter + new Vector2(x, -y), label, s.Code));
            }
            list.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < MaxSpotIcons; i++)
            {
                string name = "spot_" + i;
                if (i >= list.Count)
                {
                    if (_slotLabel[i] != null) { _hud.Detach("/HUD/minimap", SpotDepthBase + i); _slotLabel[i] = null; }
                    continue;
                }
                var d = _hud.AttachSprite("/HUD/minimap", _spotChar, name, SpotDepthBase + i);
                if (d == null) continue;
                int logoKey = list[i].label == "carDealer" ? (list[i].code ?? "").GetHashCode() : 0;
                if (_slotLabel[i] != list[i].label || _slotLogo[i] != logoKey || d.Sprite == null || d.Sprite.Frame < 0)
                {
                    _hud.GotoLabel("/HUD/minimap/" + name, list[i].label, false);
                    if (list[i].label == "carDealer") ApplyDealerLogo("/HUD/minimap/" + name, list[i].code);
                    _slotLabel[i] = list[i].label; _slotLogo[i] = logoKey;
                }
                d.Matrix = new Matrix2D { A = SpotIconScale, D = SpotIconScale };
                d.Position = list[i].p;
                d.Visible = true;
            }
            UpdatePlayerIcons(carPos, right, fwd, k);
        }

        // ------------------------------------------------------------------ Mitspieler auf der Minimap (Spot-Symbol Label "player")
        const int PlayerDepthBase = SpotDepthBase + 200, MaxPlayerIcons = 16;
        readonly bool[] _playerSlot = new bool[MaxPlayerIcons];
        [Tooltip("Mitspieler außerhalb des Kartenausschnitts am Rand zeigen")]
        public bool ClampPlayersToEdge = true;

        void UpdatePlayerIcons(Vector3 carPos, Vector3 right, Vector3 fwd, float k)
        {
            var gd = Online.TDU2GameData.Instance;
            var list = new List<(float d, Vector2 p)>();
            if (gd != null && gd.Online)
                foreach (var pl in gd.Players.Values)
                {
                    if (!pl.Online || !pl.InCar || !pl.HasPos) continue;
                    var w = Streamer.MapRoot.TransformPoint(pl.GamePos);
                    var rel = w - carPos; rel.y = 0f;
                    float x = Vector3.Dot(rel, right) * k, y = Vector3.Dot(rel, fwd) * k;
                    float lim = LureHalf - 6f, m = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
                    if (m > lim)
                    {
                        if (!ClampPlayersToEdge) continue;
                        x *= lim / m; y *= lim / m;   // am Rand in Richtung des Spielers
                    }
                    list.Add((rel.sqrMagnitude, LureCenter + new Vector2(x, -y)));
                }
            list.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < MaxPlayerIcons; i++)
            {
                if (i >= list.Count)
                {
                    if (_playerSlot[i]) { _hud.Detach("/HUD/minimap", PlayerDepthBase + i); _playerSlot[i] = false; }
                    continue;
                }
                string name = "player_" + i;
                var d = _hud.AttachSprite("/HUD/minimap", _spotChar, name, PlayerDepthBase + i);
                if (d == null) continue;
                if (!_playerSlot[i] || d.Sprite == null || d.Sprite.Frame < 0) { _hud.GotoLabel("/HUD/minimap/" + name, "player", false); _playerSlot[i] = true; }
                d.Matrix = new Matrix2D { A = SpotIconScale, D = SpotIconScale };
                d.Position = list[i].p;
                d.Visible = true;
            }
        }

        /// <summary>Original-Einfahrtsanzeige (/HUD/enter) mit Spot-Icon; null = ausblenden.</summary>
        public void ShowEnter(string spotCode)
        {
            if (_hud == null) return;
            if (spotCode == null)
            {
                if (_enterShown) { _hud.GotoLabel("/HUD/enter", "out", true); _enterShown = false; _enterLabel = null; }
                return;
            }
            string label = SpotLabel(spotCode) ?? "default";
            if (!_enterShown || _enterLabel != label)
            {
                _hud.SetVariable("/:m_Enter", "Betreten");
                _hud.SetVariable("/:m_Locked", "Gesperrt");
                // Zustand, den das Spiel vor "on" setzt (Frame-Skripte von /HUD/enter werten ihn aus):
                // Spot-Art fürs Icon, nicht gesperrt -> enter_txt "ok" statt "locked"
                _hud.SetVariable("/:WhatSpot", label);
                _hud.SetVariable("/:WhatSpotType", "spot");
                _hud.SetVariable("/:IsSpotLocked", "0");
                _hud.SetVariable("/:IsChaLocked", "0");
                _hud.SetVariable("/:reward_type", "0");
                _hud.SetVariable("/:IsCarOK", "1");
                _hud.GotoLabel("/HUD/enter", "on", true);
                _hud.GotoLabel("/HUD/enter/enter_txt", "ok", false);
                _hud.GotoLabel("/HUD/enter/Spot", label, false);
                if (label == "carDealer") ApplyDealerLogo("/HUD/enter/Spot", spotCode);
                _enterShown = true; _enterLabel = label;
            }
        }

        public bool HasOriginalHud => _hud != null;

        /// <summary>Zugriff auf den Original-HUD (hud.vmf) fǬr andere Komponenten, z.B. das Rennen-HUD.</summary>
        public VmfPlayer HudPlayer => _hud;

        // ------------------------------------------------------------------ Rennstrecke auf der Minimap (RaceRoute)

        /// <summary>Rennstrecke anzeigen (Checkpoints der Prüfung); null = ausblenden.</summary>
        public void SetRaceRoute(Racing.RaceDefinition race)
        {
            _route = race;
            _routeNext = race != null ? 1 : -1;
            _routeBuiltNext = -2;   // Neubau erzwingen
        }

        /// <summary>Fortschritt setzen (Index des nächsten Tors); färbt den befahrenen Teil um.</summary>
        public void SetRaceProgress(int nextIndex)
        {
            if (_routeNext == nextIndex) return;
            _routeNext = nextIndex;
        }

        void EnsureRouteMesh()
        {
            if (_route == null || _route.Checkpoints.Count < 2) { if (_routeMesh != null) _routeMesh.Clear(); return; }
            if (_routeMesh != null && _routeBuiltNext == _routeNext) return;
            Racing.RaceRoute.Build(_route.Checkpoints, _routeNext, out var verts, out var cols, out var tris);
            if (_routeMesh == null) _routeMesh = new Mesh { name = "TDU2_RaceRoute", indexFormat = IndexFormat.UInt16, hideFlags = HideFlags.DontSave };
            _routeMesh.Clear();
            _routeMesh.vertices = verts;
            _routeMesh.colors32 = cols;
            _routeMesh.triangles = tris;
            _routeMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            _routeBuiltNext = _routeNext;
        }

        void DrawRouteMesh()
        {
            if (_route == null || _routeMesh == null || _routeMesh.vertexCount == 0) return;
            // Checkpoints liegen in Inselkoordinaten -> Kartenebene wie die Straßen (MapRoot bildet in die Welt ab).
            if (Streamer == null || Streamer.MapRoot == null) return;
            var m = Streamer.MapRoot.localToWorldMatrix * Matrix4x4.Translate(new Vector3(0f, MapPlaneY, 0f));
            Graphics.DrawMesh(_routeMesh, m, _roadMat, MapLayer, _mapCam, 0, null, false, false);
        }

        float Heading()
        {
            var f = Vehicle.transform.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        void OnGUI()
        {
            if (_hud != null) return;   // Originaldarstellung über hud.vmf
            if (!ShowMinimap || _rt == null || _mesh == null || Vehicle == null || !Vehicle.isActiveAndEnabled || _mat == null) return;
            if (Event.current.type != EventType.Repaint) return;
            float size = Screen.height * Size, m = Screen.height * Margin;
            float x0 = m, y0 = Screen.height - m - size;
            float cx = x0 + size * 0.5f, cy = y0 + size * 0.5f, R = size * 0.5f;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            // Rand + Hintergrund
            _mat.mainTexture = Texture2D.whiteTexture;
            _mat.SetFloat("_Circle", 1f);
            _mat.SetPass(0);
            Quad(cx - R - 3, cy - R - 3, 2 * R + 6, 2 * R + 6, new Color(0.85f, 0.9f, 1f, 0.55f));
            Quad(cx - R, cy - R, 2 * R, 2 * R, Background);
            // Straßen
            _mat.mainTexture = _rt;
            _mat.SetPass(0);
            Quad(x0, y0, size, size, Color.white);
            // Spielerpfeil
            _mat.mainTexture = Texture2D.whiteTexture;
            _mat.SetFloat("_Circle", 0f);
            _mat.SetPass(0);
            float a = size * 0.045f;
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(0, 0, 0, 0.6f));
            Tri(cx, cy - a * 1.35f - 1.5f, cx - a - 1.5f, cy + a + 1.5f, cx + a + 1.5f, cy + a + 1.5f);
            GL.Color(Arrow);
            Tri(cx, cy - a * 1.2f, cx - a * 0.85f, cy + a, cx, cy + a * 0.45f);
            Tri(cx, cy - a * 1.2f, cx, cy + a * 0.45f, cx + a * 0.85f, cy + a);
            GL.End();
            GL.PopMatrix();

            // Norden
            float h = Heading() * Mathf.Deg2Rad;
            float nx = cx - Mathf.Sin(h) * (R - size * 0.06f), ny = cy - Mathf.Cos(h) * (R - size * 0.06f);
            if (_label == null) _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _label.fontSize = Mathf.Max(10, Mathf.RoundToInt(size * 0.07f));
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.7f);
            GUI.Label(new Rect(nx - 19, ny - 19, 40, 40), "N", _label);
            GUI.color = new Color(1f, 0.85f, 0.3f, 1f);
            GUI.Label(new Rect(nx - 20, ny - 20, 40, 40), "N", _label);
            GUI.color = old;
        }

        static void Quad(float x, float y, float w, float h, Color c)
        {
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.TexCoord2(0, 1); GL.Vertex3(x, y, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(x + w, y, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(x + w, y + h, 0);
            GL.TexCoord2(0, 0); GL.Vertex3(x, y + h, 0);
            GL.End();
        }

        static void Tri(float ax, float ay, float bx, float by, float cx, float cy)
        {
            GL.Vertex3(ax, ay, 0); GL.Vertex3(bx, by, 0); GL.Vertex3(cx, cy, 0);
        }

        void OnDestroy()
        {
            if (TDU2Radio.Instance != null)
                TDU2Radio.Instance.canPlayRadio = false;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_mesh != null) Destroy(_mesh);
            if (_routeMesh != null) Destroy(_routeMesh);
            if (_mat != null) Destroy(_mat);
            if (_roadMat != null) Destroy(_roadMat);
            if (_mapCam != null) Destroy(_mapCam.gameObject);
        }
    }
}
