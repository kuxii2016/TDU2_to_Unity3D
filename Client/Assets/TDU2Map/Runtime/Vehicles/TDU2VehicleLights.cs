using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Fahrzeuglichter wie im Spiel:
    ///   - Lichtgläser (Materialien LGT_FL/FR/RL/RR/LGT): Emission mit der TEXLIGHT-Textur (lgt_on.2db)
    ///   - Flares an den Ankerpunkten des Modells: FA_L_* Scheinwerfer, FA_B_* Bremse, FA_R_* Rückfahrlicht,
    ///     FA_S_* Rücklicht, FA_W_* Blinker (FL/FR/RL/RR = Seite)
    ///   - echte Unity-Lichter: zwei Spotlights für die Scheinwerfer, rotes Punktlicht beim Bremsen, weißes beim Rückwärtsfahren
    /// Tasten (im Fahrzeug-Manager): L Licht (aus/Abblend/Fern), N Blinker links, M Blinker rechts, K Warnblinker.
    /// </summary>
    public sealed class TDU2VehicleLights : MonoBehaviour
    {
        public enum Kind { Head, Brake, Reverse, Tail, Indicator }

        public TDU2Vehicle Vehicle;
        /// <summary>0 = aus, 1 = Abblendlicht, 2 = Fernlicht</summary>
        public int Headlights;
        public bool IndicatorLeft, IndicatorRight, Hazard;
        /// <summary>Ohne eigenes TDU2Vehicle gesteuert (Mitspieler-Autos): Bremse/Rückwärts kommen aus ExtBrake/ExtReverse.</summary>
        public bool External;
        public bool ExtBrake, ExtReverse;
        /// <summary>Lichthupe: solange gesetzt, Fernlicht an.</summary>
        public bool Flash;
        public float BlinkHz = 1.5f;
        public bool UnityLights = true;
        [Tooltip("Rote/weiße Punktlichter hinter dem Auto beim Bremsen/Rückwärtsfahren (im Spiel nicht vorhanden – erzeugen einen Lichtfleck)")]
        public bool RearPointLights = false;
        public float HeadlightIntensity = 2f;
        public float FlareSize = 0.16f;
        [Tooltip("Gesamthelligkeit von Glas-Leuchten und Flares (bei Tag niedrig, nachts ~1)")]
        [Range(0f, 2f)] public float Brightness = 0.3f;
        [Tooltip("Helligkeit der Lichtgläser (lgt_on-Textur, wie im Spiel selbstleuchtend) – unabhängig von den Flares")]
        [Range(0f, 4f)] public float LensBrightness = 1.0f;

        sealed class Flare { public Transform Anchor; public Transform Quad; public Material Mat; public Kind Kind; public int Side; public Vector3 Local; }

        readonly List<Flare> _flares = new List<Flare>();
        readonly List<(Material mat, int pos)> _lenses = new List<(Material, int)>();   // pos: 0 vorne, 1 hinten, 2 beides; Seite in hohen Bits
        readonly List<Object> _owned = new List<Object>();
        Light[] _head;
        Light _brakeLight, _reverseLight;
        float _blinkT;
        static Texture2D _flareTex;
        static Mesh _quad;

        public bool BlinkOn => (_blinkT * BlinkHz) % 1f < 0.5f;

        readonly List<Material> _dash = new List<Material>();

        public void Setup(TDU2Vehicle vehicle, CarModel model)
        {
            Vehicle = vehicle;
            if (model == null) return;
            _dash.AddRange(model.DashMaterials);

            // Lichtgläser zuordnen
            foreach (var m in model.LightMaterials)
            {
                string n = m.name.ToUpperInvariant();
                int side = n.EndsWith("FL") || n.EndsWith("RL") ? 1 : n.EndsWith("FR") || n.EndsWith("RR") ? 2 : 0;
                int pos = n.EndsWith("FL") || n.EndsWith("FR") ? 0 : n.EndsWith("RL") || n.EndsWith("RR") ? 1 : 2;
                _lenses.Add((m, pos | (side << 4)));
            }

            // Flares an den FA_-Ankern
            var shader = Shader.Find("TDU2/Flare");
            foreach (var kv in model.Nodes)
            {
                string n = kv.Key.ToUpperInvariant();
                if (!n.StartsWith("FA_") || n.Length < 6) continue;
                Kind k;
                switch (n[3])
                {
                    case 'L': k = Kind.Head; break;
                    case 'B': k = Kind.Brake; break;
                    case 'R': k = Kind.Reverse; break;
                    case 'S': k = Kind.Tail; break;
                    case 'W': k = Kind.Indicator; break;
                    default: continue;
                }
                int side = n.Contains("_FL") || n.Contains("_RL") ? 1 : n.Contains("_FR") || n.Contains("_RR") ? 2 : 0;
                if (shader != null) _flares.Add(CreateFlare(kv.Value, k, side, shader));
            }

            if (UnityLights)
            {
                var heads = new List<Transform>();
                foreach (var f in _flares) if (f.Kind == Kind.Head && heads.Count < 2 && !f.Anchor.name.EndsWith("1_C")) heads.Add(f.Anchor);
                if (heads.Count == 0) heads.Add(null);
                _head = new Light[heads.Count];
                for (int i = 0; i < heads.Count; i++)
                {
                    var go = new GameObject("Headlight" + i);
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = heads[i] != null ? transform.InverseTransformPoint(heads[i].position) : new Vector3(0, 0.7f, 2.2f);
                    var l = go.AddComponent<Light>();
                    l.type = LightType.Spot;
                    l.color = new Color(1f, 0.95f, 0.85f);
                    l.shadows = LightShadows.None;
                    l.renderMode = LightRenderMode.ForcePixel;
                    l.enabled = false;
                    _head[i] = l;
                }
                _brakeLight = CreatePoint("BrakeLight", Center(Kind.Brake, Kind.Tail, new Vector3(0, 0.7f, -2.2f)), new Color(1f, 0.08f, 0.04f), 5f);
                _reverseLight = CreatePoint("ReverseLight", Center(Kind.Reverse, Kind.Tail, new Vector3(0, 0.6f, -2.2f)), new Color(1f, 1f, 0.95f), 7f);
            }
        }

        Vector3 Center(Kind k, Kind alt, Vector3 fallback)
        {
            Vector3 sum = Vector3.zero; int c = 0;
            foreach (var f in _flares) if (f.Kind == k) { sum += transform.InverseTransformPoint(f.Anchor.position); c++; }
            if (c == 0) foreach (var f in _flares) if (f.Kind == alt) { sum += transform.InverseTransformPoint(f.Anchor.position); c++; }
            return c > 0 ? sum / c + new Vector3(0, 0, -0.3f) : fallback;
        }

        Light CreatePoint(string name, Vector3 local, Color c, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = local;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.range = range; l.shadows = LightShadows.None; l.enabled = false;
            return l;
        }

        Flare CreateFlare(Transform anchor, Kind k, int side, Shader shader)
        {
            var go = new GameObject("Flare_" + anchor.name);
            go.transform.SetParent(transform, false);   // nicht unter das gespiegelte Visual
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = QuadMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mat = new Material(shader) { name = "Flare_" + k };
            mat.mainTexture = FlareTexture();
            mat.SetColor("_Color", FlareColor(k));
            mat.SetFloat("_Intensity", 0f);
            mr.sharedMaterial = mat;
            _owned.Add(mat);
            var f = new Flare { Anchor = anchor, Quad = go.transform, Mat = mat, Kind = k, Side = side, Local = transform.InverseTransformPoint(anchor.position) };
            go.SetActive(false);
            return f;
        }

        static Color FlareColor(Kind k)
        {
            switch (k)
            {
                case Kind.Head: return new Color(1f, 0.95f, 0.85f);
                case Kind.Brake: return new Color(1f, 0.06f, 0.03f);
                case Kind.Tail: return new Color(0.8f, 0.03f, 0.02f);
                case Kind.Reverse: return new Color(1f, 1f, 1f);
                default: return new Color(1f, 0.45f, 0.02f);
            }
        }

        static Mesh QuadMesh()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "FlareQuad" };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _quad.RecalculateBounds();
            return _quad;
        }

        static Texture2D FlareTexture()
        {
            if (_flareTex != null) return _flareTex;
            const int S = 64;
            _flareTex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "TDU2_Flare", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f, dy = (y + 0.5f) / S * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float glow = Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f);
                    float core = Mathf.Pow(Mathf.Clamp01(1f - r * 3f), 1.5f);
                    float streak = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dy) * 14f), 2f) * Mathf.Clamp01(1f - Mathf.Abs(dx)) * 0.35f;
                    byte v = (byte)(Mathf.Clamp01(glow * 0.6f + core + streak) * 255f);
                    px[y * S + x] = new Color32(v, v, v, v);
                }
            _flareTex.SetPixels32(px);
            _flareTex.Apply(true, true);
            return _flareTex;
        }

        void Update()
        {
            _blinkT += Time.deltaTime;
            if (!IndicatorLeft && !IndicatorRight && !Hazard) _blinkT = 0f;
        }

        void LateUpdate()
        {
            if (Vehicle == null && !External) return;
            float brake = External ? (ExtBrake ? 1f : 0f) : Mathf.Clamp01(Vehicle.Brake) > 0.05f ? 1f : 0f;
            bool reverse = External ? ExtReverse : Vehicle.Gear < 0;
            int hl = Flash ? 2 : Headlights;
            float tail = Headlights > 0 ? 1f : 0f;
            float head = hl == 0 ? 0f : hl == 1 ? 1f : 1.4f;
            bool blink = BlinkOn;
            bool left = (IndicatorLeft || Hazard) && blink, right = (IndicatorRight || Hazard) && blink;

            // Lichtgläser
            foreach (var (mat, code) in _lenses)
            {
                if (mat == null) continue;
                int pos = code & 0xF, side = code >> 4;
                float ind = (side == 1 && left) || (side == 2 && right) || (side == 0 && (left || right)) ? 1f : 0f;
                if (mat.HasProperty("_UseMask") && mat.GetFloat("_UseMask") > 0.5f)
                {
                    // Funktionsmaske (lgt_g): jede Leuchte nur mit ihrer eigenen Funktion
                    float lights = pos == 0 ? Mathf.Min(1f, head) : pos == 1 ? tail * 0.45f : (head > 0f ? 0.7f : 0f);
                    mat.SetFloat("_LgtLights", lights);
                    mat.SetFloat("_LgtBrake", brake);
                    mat.SetFloat("_LgtReverse", reverse ? 1f : 0f);
                    mat.SetFloat("_LgtBlink", ind);
                    mat.SetFloat("_Emission", 1.3f * LensBrightness);
                    continue;
                }
                float e = pos == 0 ? head * 0.9f : pos == 1 ? Mathf.Max(tail * 0.45f, Mathf.Max(brake * 1.3f, reverse ? 1f : 0f)) : Mathf.Max(head * 0.6f, brake * 1.3f);
                e = Mathf.Max(e, ind * 1.2f);
                mat.SetFloat("_Emission", e * LensBrightness);
            }

            // Flares
            var cam = Camera.main;
            Vector3 fwd = transform.forward;
            foreach (var f in _flares)
            {
                float k;
                switch (f.Kind)
                {
                    case Kind.Head: k = head; break;
                    case Kind.Brake: k = brake; break;
                    case Kind.Reverse: k = reverse ? 1f : 0f; break;
                    case Kind.Tail: k = tail * 0.5f; break;
                    default: k = (f.Side == 1 && left) || (f.Side == 2 && right) || (f.Side == 0 && (left || right)) ? 1f : 0f; break;
                }
                var p = transform.TransformPoint(f.Local);
                if (k > 0.001f && cam != null)
                {
                    var toCam = cam.transform.position - p;
                    float dist = toCam.magnitude;
                    toCam /= Mathf.Max(0.001f, dist);
                    // Scheinwerfer leuchten nach vorn, Rücklichter nach hinten, Blinker rundum
                    float facing = f.Kind == Kind.Head ? Vector3.Dot(toCam, fwd) : f.Kind == Kind.Indicator ? 1f : -Vector3.Dot(toCam, fwd);
                    k *= f.Kind == Kind.Indicator ? 1f : Mathf.Clamp01(facing * 1.5f + 0.15f);
                    if (dist < 0.5f) k = 0f;   // Cockpitkamera direkt dahinter
                }
                bool on = k > 0.01f && cam != null;
                if (f.Quad.gameObject.activeSelf != on) f.Quad.gameObject.SetActive(on);
                if (!on) continue;
                f.Quad.position = p + (cam.transform.position - p).normalized * 0.05f;
                f.Quad.rotation = Quaternion.LookRotation(f.Quad.position - cam.transform.position, cam.transform.up);
                float size = FlareSize * (f.Kind == Kind.Head ? 1.3f : f.Kind == Kind.Tail ? 0.7f : 1f);
                f.Quad.localScale = Vector3.one * size;
                f.Mat.SetFloat("_Intensity", k * Brightness);
            }

            // Instrumentenbeleuchtung (mit dem Licht eingeschaltet heller)
            foreach (var m in _dash) if (m != null) m.SetFloat("_SelfIllum", Headlights > 0 ? 0.4f : 0.12f);

            // Unity-Lichter
            if (_head != null)
                foreach (var l in _head)
                {
                    if (l == null) continue;
                    l.enabled = hl > 0;
                    l.range = hl == 2 ? 140f : 70f;
                    l.spotAngle = hl == 2 ? 50f : 75f;
                    l.intensity = HeadlightIntensity * (hl == 2 ? 1.4f : 1f);
                    l.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(hl == 2 ? 1f : 5f, transform.right) * fwd, transform.up);
                }
            if (_brakeLight != null) { _brakeLight.enabled = RearPointLights && (brake > 0f || tail > 0f); _brakeLight.intensity = (brake > 0f ? 1.2f : 0.3f) * Brightness * 2f; _brakeLight.range = 3f; }
            if (_reverseLight != null) { _reverseLight.enabled = RearPointLights && reverse; _reverseLight.intensity = 0.8f * Brightness * 2f; _reverseLight.range = 4f; }
        }

        void OnDestroy()
        {
            foreach (var o in _owned) if (o != null) Destroy(o);
            _owned.Clear();
        }
    }
}
