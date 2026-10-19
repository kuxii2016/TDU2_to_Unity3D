using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Bodenschatten (wie im Spiel: Schattentextur "shadow.2db" aus common_car.bnk unter dem Auto, auf den Boden projiziert)
    /// und Reifenspuren (Bremsspuren/Driftspuren bei Längs- oder Querschlupf). Die Spuren liegen in Weltkoordinaten
    /// in einem Ringpuffer-Mesh und verblassen nicht (älteste werden überschrieben).
    /// </summary>
    public sealed class TDU2VehicleEffects : MonoBehaviour
    {
        public TDU2Vehicle Vehicle;
        [Header("Bodenschatten")]
        public bool GroundShadow = true;
        [Range(0, 1)] public float ShadowStrength = 0.75f;
        [Header("Reifenspuren")]
        public bool Skidmarks = true;
        public int MaxSkidQuads = 3000;
        public float SkidLongSlip = 3.5f;     // m/s Längsschlupf (Durchdrehen/Blockieren)
        public float SkidLatSlip = 2.2f;      // m/s Querschlupf (Driften)
        [Range(0, 1)] public float SkidOpacity = 0.55f;
        [Tooltip("Fahrspuren auf Erde/Gras (Farbe der eingedrückten Erde)")]
        public Color32 OffroadColor = new Color32(70, 52, 34, 255);
        [Range(0, 1)] public float OffroadOpacity = 0.6f;
        [Header("Staub (Erde/Feldwege)")]
        public bool Dust = true;
        public Color DustColor = new Color(0.62f, 0.52f, 0.40f, 0.35f);
        DustCloud _dust;
        bool[] _lastOff;

        GameObject _shadow;
        Material _shadowMat;
        Vector3 _shadowSize;
        static SkidBuffer _skid;   // gemeinsam für alle Fahrzeuge (bleibt beim Autowechsel liegen)
        Vector3[] _lastPos;
        bool[] _lastValid;
        int[] _lastIndex;

        public void Setup(TDU2Vehicle v, CarModel model, CarAssets assets)
        {
            Vehicle = v;
            var b = model != null ? model.BodyBounds : new Bounds(Vector3.up * 0.6f, new Vector3(1.9f, 1.2f, 4.5f));
            _shadowSize = new Vector3(b.size.x * 1.25f, 1f, b.size.z * 1.12f);
            if (GroundShadow)
            {
                var sh = Shader.Find("TDU2/CarShadow");
                if (sh != null)
                {
                    _shadowMat = new Material(sh) { name = "CarGroundShadow" };
                    var tex = assets?.FindTexture("shadow");
                    _shadowMat.mainTexture = tex != null ? tex : BlobTexture();
                    _shadow = new GameObject("GroundShadow");
                    _shadow.AddComponent<MeshFilter>().sharedMesh = Quad();
                    var mr = _shadow.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = _shadowMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
            }
            int n = v != null ? v.Wheels.Length : 0;
            _lastPos = new Vector3[n]; _lastValid = new bool[n]; _lastIndex = new int[n]; _lastOff = new bool[n];
            if (Dust) _dust = CreateDust();
            if (Skidmarks && _skid == null)
            {
                var sh = Shader.Find("TDU2/Skidmark");
                if (sh != null) _skid = new SkidBuffer(MaxSkidQuads, new Material(sh) { name = "Skidmarks", mainTexture = SkidTexture() });
            }
        }

        static Mesh _quad;
        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "ShadowQuad" };
            _quad.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _quad.RecalculateNormals(); _quad.RecalculateBounds();
            return _quad;
        }

        static Texture2D _blob, _skidTex;
        static Texture2D BlobTexture()
        {
            if (_blob != null) return _blob;
            const int S = 64;
            _blob = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "CarBlob" };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = Mathf.Abs((x + 0.5f) / S * 2f - 1f), dy = Mathf.Abs((y + 0.5f) / S * 2f - 1f);
                    float d = Mathf.Pow(Mathf.Pow(dx, 4f) + Mathf.Pow(dy, 4f), 0.25f);   // abgerundetes Rechteck
                    byte v = (byte)(Mathf.Clamp01(1f - Mathf.SmoothStep(0.55f, 1f, d)) * 255);
                    px[y * S + x] = new Color32(v, v, v, v);
                }
            _blob.SetPixels32(px); _blob.Apply(true, true);
            return _blob;
        }

        static Texture2D SkidTexture()
        {
            if (_skidTex != null) return _skidTex;
            const int W = 32, H = 64;
            _skidTex = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "Skid" };
            var px = new Color32[W * H];
            var rnd = new System.Random(7);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float edge = Mathf.Clamp01(Mathf.Min(u, 1f - u) * 6f);
                    float groove = 0.75f + 0.25f * Mathf.Sin(u * 40f);
                    float noise = 0.8f + 0.2f * (float)rnd.NextDouble();
                    byte a = (byte)(edge * groove * noise * 255);
                    px[y * W + x] = new Color32(20, 18, 16, a);
                }
            _skidTex.SetPixels32(px); _skidTex.Apply(true, true);
            return _skidTex;
        }

        void LateUpdate()
        {
            if (Vehicle == null) return;
            UpdateShadow();
            UpdateSkids();
            UpdateDust();
        }

        /// <summary>
        /// Staubwolke hinter den Rädern auf Erde/Gras. Eigenes kleines Partikelsystem (Kamera-Billboards in einem Mesh),
        /// weil das Unity-Modul "Particle System" im Projekt nicht aktiviert ist.
        /// </summary>
        sealed class DustCloud
        {
            struct P { public Vector3 Pos, Vel; public float Age, Life, Size; }
            readonly P[] _p; int _count;
            readonly Mesh _mesh; readonly GameObject _go; readonly Material _mat;
            readonly Vector3[] _v; readonly Color32[] _c; readonly Vector2[] _uv; readonly int[] _t;
            readonly Color _col;

            public DustCloud(int max, Color col, Texture tex)
            {
                _col = col;
                _p = new P[max];
                _v = new Vector3[max * 4]; _c = new Color32[max * 4]; _uv = new Vector2[max * 4]; _t = new int[max * 6];
                for (int i = 0; i < max; i++)
                {
                    int b = i * 4;
                    _uv[b] = new Vector2(0, 0); _uv[b + 1] = new Vector2(0, 1); _uv[b + 2] = new Vector2(1, 1); _uv[b + 3] = new Vector2(1, 0);
                    _t[i * 6] = b; _t[i * 6 + 1] = b + 1; _t[i * 6 + 2] = b + 2; _t[i * 6 + 3] = b; _t[i * 6 + 4] = b + 2; _t[i * 6 + 5] = b + 3;
                }
                _mesh = new Mesh { name = "Dust" };
                _mesh.MarkDynamic();
                _mesh.vertices = _v; _mesh.colors32 = _c; _mesh.uv = _uv; _mesh.triangles = _t;
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                _mat = new Material(sh) { name = "Dust", mainTexture = tex, renderQueue = 3000 };
                _go = new GameObject("TDU2 Dust");
                _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var mr = _go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            public void Emit(Vector3 pos, Vector3 vel, int n)
            {
                for (int k = 0; k < n; k++)
                {
                    if (_count >= _p.Length) return;
                    _p[_count++] = new P
                    {
                        Pos = pos + Random.insideUnitSphere * 0.3f,
                        Vel = vel + Random.insideUnitSphere * 0.8f,
                        Life = Random.Range(1.2f, 2.2f),
                        Size = Random.Range(1.2f, 2.4f),
                    };
                }
            }

            public void Shift(Vector3 d) { for (int i = 0; i < _count; i++) _p[i].Pos -= d; }

            public void Update(float dt)
            {
                var cam = Camera.main;
                Vector3 right = cam != null ? cam.transform.right : Vector3.right, up = cam != null ? cam.transform.up : Vector3.up;
                int n = 0;
                for (int i = 0; i < _count; i++)
                {
                    var p = _p[i];
                    p.Age += dt;
                    if (p.Age >= p.Life) continue;
                    p.Vel *= 1f / (1f + 1.5f * dt);
                    p.Vel.y += 0.15f * dt;
                    p.Pos += p.Vel * dt;
                    _p[n++] = p;
                }
                _count = n;
                for (int i = 0; i < _p.Length; i++)
                {
                    int b = i * 4;
                    if (i >= _count) { if (_c[b].a == 0 && _v[b] == Vector3.zero) continue; _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = default; continue; }
                    var p = _p[i];
                    float t = p.Age / p.Life;
                    float a = Mathf.Clamp01(t / 0.15f) * (1f - t);
                    float s = p.Size * Mathf.Lerp(0.6f, 1.8f, t) * 0.5f;
                    Vector3 r = right * s, u = up * s;
                    _v[b] = p.Pos - r - u; _v[b + 1] = p.Pos - r + u; _v[b + 2] = p.Pos + r + u; _v[b + 3] = p.Pos + r - u;
                    var c = new Color(_col.r, _col.g, _col.b, _col.a * a);
                    _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = c;
                }
                _mesh.vertices = _v; _mesh.colors32 = _c;
            }

            public void Destroy()
            {
                if (_go != null) Object.Destroy(_go);
                if (_mesh != null) Object.Destroy(_mesh);
                if (_mat != null) Object.Destroy(_mat);
            }
        }

        DustCloud CreateDust() => new DustCloud(400, DustColor, BlobTexture());

        float _dustAcc;
        void UpdateDust()
        {
            if (_dust == null) return;
            float kmh = Vehicle.SpeedKmh;
            var ws = Vehicle.Wheels;
            _dustAcc += Time.deltaTime;
            float dt = _dustAcc;
            if (_dustAcc >= 0.04f) { _dustAcc = 0f; } else dt = 0f;   // Ausstoß ~25x je Sekunde, Bewegung jedes Bild
            for (int i = 0; i < ws.Length && dt > 0f; i++)
            {
                var w = ws[i];
                if (!w.Grounded || w.Surface == 0 || w.Front && ws.Length > 2 && kmh > 1f && !w.Driven) continue;
                float slip = Mathf.Abs(w.LongSlipVel) + Mathf.Abs(w.LatSlipVel);
                float rate = Mathf.Clamp01((kmh - 10f) / 70f) * 14f + slip * 3f;   // Partikel je Sekunde
                if (w.Surface == 1) rate *= 0.35f;                                  // Gras staubt kaum
                int count = Mathf.FloorToInt(rate * dt + Random.value);
                if (count <= 0) continue;
                _dust.Emit(w.ContactPoint + Vector3.up * 0.25f,
                    -Vehicle.transform.forward * Mathf.Min(kmh / 3.6f * 0.15f, 3f) + Vector3.up * 0.4f, count);
            }
            _dust.Update(Time.deltaTime);
        }

        void UpdateShadow()
        {
            if (_shadow == null) return;
            var tr = Vehicle.transform;
            Vector3 c = tr.TransformPoint(new Vector3(0f, 0.5f, 0f));
            float gy; Vector3 n = Vector3.up;
            const int mask = ~(1 << 2);   // Fahrzeug liegt auf Layer 2 (Ignore Raycast)
            if (Physics.Raycast(c, Vector3.down, out var hit, 6f, mask, QueryTriggerInteraction.Ignore)) { gy = hit.point.y; n = hit.normal; }
            else { _shadow.SetActive(false); return; }
            float height = c.y - 0.5f - gy;
            float k = Mathf.Clamp01(1f - height / 3f);
            _shadow.SetActive(k > 0.02f);
            var fwd = Vector3.ProjectOnPlane(tr.forward, n);
            if (fwd.sqrMagnitude < 1e-4f) fwd = tr.forward;
            _shadow.transform.SetPositionAndRotation(new Vector3(c.x, gy + 0.03f, c.z), Quaternion.LookRotation(fwd.normalized, n));
            _shadow.transform.localScale = _shadowSize * (1f + height * 0.15f);
            _shadowMat.SetFloat("_Strength", ShadowStrength * k);
        }

        void UpdateSkids()
        {
            if (_skid == null) return;
            var ws = Vehicle.Wheels;
            for (int i = 0; i < ws.Length && i < _lastPos.Length; i++)
            {
                var w = ws[i];
                float slip = Mathf.Max(Mathf.Abs(w.LongSlipVel) / SkidLongSlip, Mathf.Abs(w.LatSlipVel) / SkidLatSlip);
                bool off = w.Surface != 0;
                // Asphalt: schwarze Gummispuren nur beim Rutschen. Erde/Gras: helle Fahrspur schon beim Rollen (Reifen drücken
                // den Boden ein), kräftiger beim Rutschen – kein Gummiabrieb.
                bool mark = w.Grounded && Vehicle.SpeedKmh > 3f && (off ? Vehicle.SpeedKmh > 8f : slip > 1f);
                if (!mark) { _lastValid[i] = false; continue; }
                if (_lastValid[i] && _lastOff[i] != off) _lastValid[i] = false;   // Wechsel Asphalt/Erde: neue Spur
                _lastOff[i] = off;
                Vector3 p = w.ContactPoint + w.ContactNormal * 0.025f;
                float a = off ? Mathf.Clamp01(0.18f + Mathf.Max(0f, slip - 0.5f) * 0.35f) * OffroadOpacity
                              : Mathf.Clamp01((slip - 1f) * 0.8f + 0.25f) * SkidOpacity;
                if (_lastValid[i] && (p - _lastPos[i]).sqrMagnitude < 0.09f) continue;   // Segmente ab ~30 cm
                var right = Vector3.Cross(w.ContactNormal, (p - _lastPos[i]).normalized);
                _lastIndex[i] = _skid.Add(p, w.ContactNormal, right, w.Width * (off ? 1.05f : 0.9f), a, _lastValid[i] ? _lastIndex[i] : -1, off ? OffroadColor : new Color32(0, 0, 0, 255));
                _lastPos[i] = p; _lastValid[i] = true;
            }
            _skid.Commit();
        }

        void OnEnable() => FloatingOrigin.Shifted += OnShift;
        void OnDisable() => FloatingOrigin.Shifted -= OnShift;
        void OnShift(Vector3 d)
        {
            if (_lastPos != null) for (int i = 0; i < _lastPos.Length; i++) _lastPos[i] -= d;
            _dust?.Shift(d);
        }

        void OnDestroy()
        {
            if (_shadow != null) Destroy(_shadow);
            if (_shadowMat != null) Destroy(_shadowMat);
            _dust?.Destroy();
        }

        /// <summary>Ringpuffer aus Quads; jedes Quad verbindet den vorherigen mit dem aktuellen Spurpunkt.</summary>
        sealed class SkidBuffer
        {
            readonly int _max;
            readonly Vector3[] _v; readonly Vector3[] _n; readonly Color32[] _c; readonly Vector2[] _uv; readonly int[] _t;
            readonly Mesh _mesh;
            GameObject _go;   // Vertices lokal zu _go (Floating Origin verschiebt nur das Objekt)
            int _next;
            bool _dirty;
            // Endpunkte der Segmente (links/rechts) pro Quad-Index für den Anschluss
            readonly Vector3[] _endL, _endR;

            public SkidBuffer(int max, Material mat)
            {
                _max = max;
                _v = new Vector3[max * 4]; _n = new Vector3[max * 4]; _c = new Color32[max * 4]; _uv = new Vector2[max * 4]; _t = new int[max * 6];
                _endL = new Vector3[max]; _endR = new Vector3[max];
                for (int q = 0; q < max; q++)
                {
                    int b = q * 4;
                    _t[q * 6] = b; _t[q * 6 + 1] = b + 1; _t[q * 6 + 2] = b + 2; _t[q * 6 + 3] = b; _t[q * 6 + 4] = b + 2; _t[q * 6 + 5] = b + 3;
                }
                _mesh = new Mesh { name = "Skidmarks", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _mesh.MarkDynamic();
                _mesh.vertices = _v; _mesh.normals = _n; _mesh.colors32 = _c; _mesh.uv = _uv; _mesh.triangles = _t;
                var go = new GameObject("TDU2 Skidmarks");
                Object.DontDestroyOnLoad(go);
                _go = go;
                FloatingOrigin.Shifted += d => { if (_go != null) _go.transform.position -= d; };
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            /// <returns>Index des neuen Endpunkts (für das nächste Segment)</returns>
            public int Add(Vector3 p, Vector3 n, Vector3 right, float width, float alpha, int prev, Color32 col)
            {
                int q = _next; _next = (_next + 1) % _max;
                if (_go != null) p -= _go.transform.position;
                var r = right * (width * 0.5f);
                Vector3 l1 = p - r, r1 = p + r;
                _endL[q] = l1; _endR[q] = r1;
                int b = q * 4;
                byte a = (byte)(Mathf.Clamp01(alpha) * 255);
                if (prev < 0)
                {
                    // Startpunkt: degeneriertes Quad (unsichtbar), dient nur als Anschluss
                    _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = p;
                    _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = new Color32(0, 0, 0, 0);
                    _uv[b] = _uv[b + 1] = _uv[b + 2] = _uv[b + 3] = Vector2.zero;
                }
                else
                {
                    _v[b] = _endL[prev]; _v[b + 1] = l1; _v[b + 2] = r1; _v[b + 3] = _endR[prev];
                    byte pa = _c[prev * 4 + 1].a > 0 ? _c[prev * 4 + 1].a : a;
                    _c[b] = _c[b + 3] = new Color32(col.r, col.g, col.b, pa);
                    _c[b + 1] = _c[b + 2] = new Color32(col.r, col.g, col.b, a);
                    float dist = Vector3.Distance(_endL[prev], l1);
                    float v0 = _uv[prev * 4 + 1].y;
                    _uv[b] = new Vector2(0, v0); _uv[b + 3] = new Vector2(1, v0);
                    _uv[b + 1] = new Vector2(0, v0 + dist / Mathf.Max(0.05f, width)); _uv[b + 2] = new Vector2(1, v0 + dist / Mathf.Max(0.05f, width));
                }
                _n[b] = _n[b + 1] = _n[b + 2] = _n[b + 3] = n;
                _dirty = true;
                return q;
            }

            public void Commit()
            {
                if (!_dirty) return;
                _dirty = false;
                _mesh.vertices = _v; _mesh.normals = _n; _mesh.colors32 = _c; _mesh.uv = _uv;
                _mesh.RecalculateBounds();
            }
        }
    }
}
