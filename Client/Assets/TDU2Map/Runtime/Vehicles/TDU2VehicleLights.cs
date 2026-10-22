
using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    public sealed class TDU2VehicleLights : MonoBehaviour
    {
        public enum Kind
        {
            Head,
            Brake,
            Reverse,
            Tail,
            Indicator
        }

        public TDU2Vehicle Vehicle;

        /// <summary>0 = aus, 1 = Abblendlicht, 2 = Fernlicht</summary>
        public int Headlights;

        public bool IndicatorLeft;
        public bool IndicatorRight;
        public bool Hazard;

        public bool External;
        public bool ExtBrake;
        public bool ExtReverse;
        public bool Flash;

        public float BlinkHz = 1.5f;
        public bool UnityLights = true;
        public bool RearPointLights = true;

        public float HeadlightIntensity = 2f;
        public float FlareSize = 0.16f;

        [Range(0f, 2f)]
        public float Brightness = 0.3f;

        [Range(0f, 4f)]
        public float LensBrightness = 1f;

        [Tooltip("Reichweite der zusätzlichen hinteren Punktlichter.")]
        public float RearLightRange = 1.5f;

        [Tooltip("Helligkeit der zusätzlichen hinteren Punktlichter.")]
        [Range(0f, 2f)]
        public float RearLightIntensity = 0.5f;

        sealed class Flare
        {
            public Transform Anchor;
            public Transform Quad;
            public Material Mat;
            public Kind Kind;
            public int Side;
            public Vector3 Local;
        }

        readonly List<Flare> _flares = new List<Flare>();

        readonly List<(Material mat, int pos)> _lenses =
            new List<(Material, int)>();

        /// <summary>Verkehrsauto-Lampen (Knotenname FA_xx -> Funktion), eigene Materialinstanz je Lampe.</summary>
        readonly List<CarModel.TrafficLamp> _trafficLamps = new List<CarModel.TrafficLamp>();

        readonly List<Object> _owned = new List<Object>();
        readonly List<Material> _dash = new List<Material>();

        Light[] _head;
        Light[] _brakeLights;
        Light[] _reverseLights;

        float _blinkT;

        static Texture2D _flareTex;
        static Mesh _quad;

        public bool BlinkOn => (_blinkT * BlinkHz) % 1f < 0.5f;

        public void Setup(TDU2Vehicle vehicle, CarModel model)
        {
            Vehicle = vehicle;

            if (model == null)
                return;

            _flares.Clear();
            _lenses.Clear();
            _dash.Clear();
            _trafficLamps.Clear();

            if (model.TrafficLamps != null)
                _trafficLamps.AddRange(model.TrafficLamps);

            if (model.DashMaterials != null)
                _dash.AddRange(model.DashMaterials);

            // Lichtgläser erkennen.
            if (model.LightMaterials != null)
            {
                foreach (var m in model.LightMaterials)
                {
                    if (m == null)
                        continue;

                    string n = m.name.ToUpperInvariant();

                    int side =
                        n.EndsWith("FL") || n.EndsWith("RL") ? 1 :
                        n.EndsWith("FR") || n.EndsWith("RR") ? 2 : 0;

                    int pos =
                        n.EndsWith("FL") || n.EndsWith("FR") ? 0 :
                        n.EndsWith("RL") || n.EndsWith("RR") ? 1 : 2;

                    _lenses.Add((m, pos | (side << 4)));
                }
            }

            // Flares an den FA_-Ankern.
            Shader shader = Shader.Find("TDU2/Flare");

            if (shader != null && model.Nodes != null)
            {
                foreach (var kv in model.Nodes)
                {
                    if (kv.Value == null || string.IsNullOrEmpty(kv.Key))
                        continue;

                    string n = kv.Key.ToUpperInvariant();

                    if (!n.StartsWith("FA_") || n.Length < 4)
                        continue;

                    Kind kind;

                    switch (n[3])
                    {
                        case 'L':
                            kind = Kind.Head;
                            break;
                        case 'B':
                            kind = Kind.Brake;
                            break;
                        case 'R':
                            kind = Kind.Reverse;
                            break;
                        case 'S':
                            kind = Kind.Tail;
                            break;
                        case 'W':
                            kind = Kind.Indicator;
                            break;
                        default:
                            continue;
                    }

                    int side = GetAnchorSide(n);

                    _flares.Add(CreateFlare(
                        kv.Value, kind, side, shader));
                }
            }

            if (!UnityLights)
                return;

            // Scheinwerfer vorne.
            var heads = new List<Transform>();

            foreach (var f in _flares)
            {
                if (f.Kind != Kind.Head || f.Anchor == null)
                    continue;

                string n = f.Anchor.name.ToUpperInvariant();

                if (n.EndsWith("1_C"))
                    continue;

                if (!heads.Contains(f.Anchor) && heads.Count < 2)
                    heads.Add(f.Anchor);
            }

            if (heads.Count == 0)
                heads.Add(null);

            _head = new Light[heads.Count];

            for (int i = 0; i < heads.Count; i++)
            {
                var go = new GameObject("Headlight" + i);
                go.transform.SetParent(transform, false);

                go.transform.localPosition = heads[i] != null
                    ? transform.InverseTransformPoint(heads[i].position)
                    : new Vector3(i == 0 ? -0.5f : 0.5f, 0.7f, 2.2f);

                var light = go.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = new Color(1f, 0.95f, 0.85f);
                light.shadows = LightShadows.None;
                light.renderMode = LightRenderMode.ForcePixel;
                light.enabled = false;

                _head[i] = light;
            }

            // Linke und rechte hintere Anker getrennt suchen.
            Transform brakeLeft = FindAnchorBySide(Kind.Brake, 1);
            Transform brakeRight = FindAnchorBySide(Kind.Brake, 2);

            Transform reverseLeft = FindAnchorBySide(Kind.Reverse, 1);
            Transform reverseRight = FindAnchorBySide(Kind.Reverse, 2);

            _brakeLights = new Light[2];
            _reverseLights = new Light[2];

            _brakeLights[0] = CreatePoint(
                "BrakeLightLeft",
                brakeLeft != null
                    ? transform.InverseTransformPoint(brakeLeft.position)
                    : new Vector3(-0.7f, 0.7f, -2.2f),
                new Color(1f, 0.04f, 0.02f));

            _brakeLights[1] = CreatePoint(
                "BrakeLightRight",
                brakeRight != null
                    ? transform.InverseTransformPoint(brakeRight.position)
                    : new Vector3(0.7f, 0.7f, -2.2f),
                new Color(1f, 0.04f, 0.02f));

            _reverseLights[0] = CreatePoint(
                "ReverseLightLeft",
                reverseLeft != null
                    ? transform.InverseTransformPoint(reverseLeft.position)
                    : new Vector3(-0.5f, 0.6f, -2.2f),
                Color.white);

            _reverseLights[1] = CreatePoint(
                "ReverseLightRight",
                reverseRight != null
                    ? transform.InverseTransformPoint(reverseRight.position)
                    : new Vector3(0.5f, 0.6f, -2.2f),
                Color.white);

        }

        static int GetAnchorSide(string name)
        {
            string n = name.ToUpperInvariant();

            if (n.Contains("_FL") || n.Contains("_RL") ||
                n.Contains("_LEFT") || n.EndsWith("_L") ||
                n.Contains("_L_"))
                return 1;

            if (n.Contains("_FR") || n.Contains("_RR") ||
                n.Contains("_RIGHT") || n.EndsWith("_R") ||
                n.Contains("_R_"))
                return 2;

            return 0;
        }

        Transform FindAnchor(Kind kind)
        {
            foreach (var f in _flares)
            {
                if (f.Kind == kind && f.Anchor != null)
                    return f.Anchor;
            }

            return null;
        }

        Transform FindAnchorBySide(Kind kind, int side)
        {
            foreach (var f in _flares)
            {
                if (f.Kind == kind &&
                    f.Anchor != null &&
                    f.Side == side)
                    return f.Anchor;
            }

            return null;
        }

        Light CreatePoint( string name, Vector3 localPosition, Color color)
        {
            localPosition.x += .05f;
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = RearLightRange;
            light.shadows = LightShadows.None;
            light.enabled = false;

            return light;
        }

        /// <summary>
        /// Schließt alle Layer aus, die vom Fahrzeug-Root und
        /// dessen Kindern verwendet werden.
        /// </summary>
        void ExcludeOwnVehicleFromLight(Light light)
        {
            if (light == null || Vehicle == null)
                return;

            Transform vehicleRoot = Vehicle.transform;

            if (vehicleRoot == null)
                return;

            // Layer einmalig sammeln.
            var ownLayers = new HashSet<int>();

            Transform[] children =
                vehicleRoot.GetComponentsInChildren<Transform>(true);

            foreach (Transform child in children)
            {
                if (child != null)
                    ownLayers.Add(child.gameObject.layer);
            }

            foreach (int layer in ownLayers)
                light.cullingMask &= ~(1 << layer);
        }

        private static readonly int LgtLightsId = Shader.PropertyToID("_LgtLights");

        private static readonly int LgtBrakeId = Shader.PropertyToID("_LgtBrake");

        private static readonly int LgtReverseId = Shader.PropertyToID("_LgtReverse");

        private static readonly int LgtBlinkId = Shader.PropertyToID("_LgtBlink");

        private void SetVehicleLightState( Material material, bool lights, bool brake, bool reverse, bool blink)
        {
            if (material == null)
                return;

            material.SetFloat(LgtLightsId, lights ? 1f : 0f);
            material.SetFloat(LgtBrakeId, brake ? 1f : 0f);
            material.SetFloat(LgtReverseId, reverse ? 1f : 0f);
            material.SetFloat(LgtBlinkId, blink ? 1f : 0f);
        }


        Flare CreateFlare( Transform anchor, Kind kind,
            int side,
            Shader shader)
        {
            var go = new GameObject("Flare_" + anchor.name);
            go.transform.SetParent(transform, false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = QuadMesh();

            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            var mat = new Material(shader)
            {
                name = "Flare_" + kind
            };

            mat.mainTexture = FlareTexture();
            mat.SetColor("_Color", FlareColor(kind));
            mat.SetFloat("_Intensity", 0f);

            mr.sharedMaterial = mat;
            _owned.Add(mat);

            var flare = new Flare
            {
                Anchor = anchor,
                Quad = go.transform,
                Mat = mat,
                Kind = kind,
                Side = side,
                Local = transform.InverseTransformPoint(anchor.position)
            };

            go.SetActive(false);
            return flare;
        }

        static Color FlareColor(Kind kind)
        {
            switch (kind)
            {
                case Kind.Head:
                    return new Color(1f, 0.95f, 0.85f);
                case Kind.Brake:
                    return new Color(1f, 0.06f, 0.03f);
                case Kind.Tail:
                    return new Color(0.8f, 0.03f, 0.02f);
                case Kind.Reverse:
                    return Color.white;
                default:
                    return new Color(1f, 0.45f, 0.02f);
            }
        }

        static Mesh QuadMesh()
        {
            if (_quad != null)
                return _quad;

            _quad = new Mesh { name = "FlareQuad" };

            _quad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f)
            };

            _quad.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };

            _quad.triangles = new[]
            {
                0, 2, 1,
                0, 3, 2
            };

            _quad.RecalculateBounds();
            return _quad;
        }

        static Texture2D FlareTexture()
        {
            if (_flareTex != null)
                return _flareTex;

            const int S = 64;

            _flareTex = new Texture2D(
                S, S, TextureFormat.RGBA32, true)
            {
                name = "TDU2_Flare",
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[S * S];

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f;
                    float dy = (y + 0.5f) / S * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);

                    float glow = Mathf.Pow(
                        Mathf.Clamp01(1f - r), 2.2f);

                    float core = Mathf.Pow(
                        Mathf.Clamp01(1f - r * 3f), 1.5f);

                    float streak =
                        Mathf.Pow(
                            Mathf.Clamp01(1f - Mathf.Abs(dy) * 14f), 2f)
                        * Mathf.Clamp01(1f - Mathf.Abs(dx))
                        * 0.35f;

                    byte v = (byte)(
                        Mathf.Clamp01(glow * 0.6f + core + streak) * 255f);

                    pixels[y * S + x] = new Color32(v, v, v, v);
                }
            }

            _flareTex.SetPixels32(pixels);
            _flareTex.Apply(true, true);

            return _flareTex;
        }

        void Update()
        {
            _blinkT += Time.deltaTime;

            if (!IndicatorLeft && !IndicatorRight && !Hazard)
                _blinkT = 0f;
        }

        void LateUpdate()
        {
            if (Vehicle == null && !External)
                return;

            float brake = External
                ? (ExtBrake ? 1f : 0f)
                : (Mathf.Clamp01(Vehicle.Brake) > 0.05f ? 1f : 0f);

            bool reverse = External
                ? ExtReverse
                : Vehicle.Gear < 0;

            int hl = Flash ? 2 : Headlights;

            float tail = Headlights > 0 ? 1f : 0f;
            float head = hl == 0 ? 0f : hl == 1 ? 1f : 1.4f;

            bool blink = BlinkOn;
            bool left = (IndicatorLeft || Hazard) && blink;
            bool right = (IndicatorRight || Hazard) && blink;

            if (Input.GetKeyDown(KeyCode.F10))
            {
                Debug.Log(
                    $"[TDU2 LIGHTS] External={External}, " +
                    $"Brake={brake}, Reverse={reverse}, " +
                    $"Gear={(Vehicle != null ? Vehicle.Gear : 0)}, " +
                    $"Flares={_flares.Count}"
                );
            }

            // Lichtgläser.
            foreach (var entry in _lenses)
            {
                Material mat = entry.mat;
                int code = entry.pos;

                if (mat == null)
                    continue;

                int pos = code & 0xF;
                int side = code >> 4;

                float ind =
                    ((side == 1 && left) ||
                     (side == 2 && right) ||
                     (side == 0 && (left || right)))
                    ? 1f : 0f;

                if (mat.HasProperty("_UseMask") &&
                    mat.GetFloat("_UseMask") > 0.5f)
                {
                    float lights =
                        pos == 0 ? Mathf.Min(1f, head) :
                        pos == 1 ? tail * 0.45f :
                        (head > 0f ? 0.7f : 0f);

                    mat.SetFloat("_LgtLights", lights);
                    mat.SetFloat("_LgtBrake", brake);
                    mat.SetFloat("_LgtReverse", reverse ? 1f : 0f);
                    mat.SetFloat("_LgtBlink", ind);
                    mat.SetFloat("_Emission", 1.3f * LensBrightness);
                    continue;
                }

                float emission =
                    pos == 0 ? head * 0.9f :
                    pos == 1
                        ? Mathf.Max(
                            tail * 0.45f,
                            Mathf.Max(brake * 1.3f, reverse ? 1f : 0f))
                        : Mathf.Max(head * 0.6f, brake * 1.3f);

                emission = Mathf.Max(emission, ind * 1.2f);
                mat.SetFloat("_Emission", emission * LensBrightness);
            }

            // Verkehrsauto-Lampen: feste Leuchtfarbe je Funktion, Stärke je Zustand.
            if (_trafficLamps.Count > 0)
            {
                foreach (var lamp in _trafficLamps)
                {
                    var mat = lamp.Mat;
                    if (mat == null) continue;

                    float e;
                    switch (lamp.Kind)
                    {
                        case CarModel.LampKind.Head:
                            e = head * 1.7f;
                            break;
                        case CarModel.LampKind.Tail:
                            e = Mathf.Max(tail * 0.9f, brake * 2.0f);
                            break;
                        case CarModel.LampKind.Brake:
                            e = brake * 2.0f;
                            break;
                        case CarModel.LampKind.Reverse:
                            e = reverse ? 1.9f : 0f;
                            break;
                        case CarModel.LampKind.Indicator:
                            e =
                                ((lamp.Side == 1 && left) ||
                                 (lamp.Side == 2 && right) ||
                                 (lamp.Side == 0 && (left || right)))
                                ? 1.9f : 0f;
                            break;
                        default:
                            e = (Flash || Hazard) && blink ? 2f : 0f;   // Rundumlicht
                            break;
                    }

                    // Nicht leuchtende Funktionen ganz aus: sonst glimmt die Lampe leicht nach.
                    mat.SetFloat("_Emission", e > 0.01f ? e * LensBrightness : 0f);
                }
            }

            // Flares.
            Camera cam = Camera.main;
            Vector3 fwd = transform.forward;

            foreach (var f in _flares)
            {
                if (f.Anchor == null || f.Quad == null || f.Mat == null)
                    continue;

                float k;

                switch (f.Kind)
                {
                    case Kind.Head:
                        k = head;
                        break;
                    case Kind.Brake:
                        k = brake;
                        break;
                    case Kind.Reverse:
                        k = reverse ? 1f : 0f;
                        break;
                    case Kind.Tail:
                        k = tail * 0.5f;
                        break;
                    default:
                        k =
                            ((f.Side == 1 && left) ||
                             (f.Side == 2 && right) ||
                             (f.Side == 0 && (left || right)))
                            ? 1f : 0f;
                        break;
                }

                Vector3 p = f.Anchor.position;

                if (k > 0.001f && cam != null)
                {
                    Vector3 toCam = cam.transform.position - p;
                    float dist = toCam.magnitude;
                    toCam /= Mathf.Max(0.001f, dist);

                    // Verkehrsautos: die Lampenflächen (FA_) sind punktförmig, sichtbar wird ihr Licht
                    // erst über den Flare. Da man sie aus jedem Winkel sieht, hier nicht wegblenden.
                    if (!External)
                    {
                        float facing =
                            f.Kind == Kind.Head
                                ? Vector3.Dot(toCam, fwd)
                                : f.Kind == Kind.Indicator
                                    ? 1f
                                    : -Vector3.Dot(toCam, fwd);

                        if (f.Kind != Kind.Indicator)
                            k *= Mathf.Clamp01(facing * 1.5f + 0.15f);
                    }

                    if (dist < 0.5f)
                        k = 0f;
                }

                bool on = k > 0.01f && cam != null;

                if (f.Quad.gameObject.activeSelf != on)
                    f.Quad.gameObject.SetActive(on);

                if (!on)
                    continue;

                f.Quad.position =
                    p + (cam.transform.position - p).normalized * 0.05f;

                f.Quad.rotation = Quaternion.LookRotation(
                    f.Quad.position - cam.transform.position,
                    cam.transform.up);

                float size =
                    FlareSize *
                    (External ? 1.8f : 1f) *
                    (f.Kind == Kind.Head ? 1.3f :
                     f.Kind == Kind.Tail ? 0.9f : 1f);

                f.Quad.localScale = Vector3.one * size;
                f.Mat.SetFloat("_Intensity", k * Brightness * (External ? 1.6f : 1f));
            }

            // Instrumentenbeleuchtung.
            foreach (var m in _dash)
            {
                if (m != null && m.HasProperty("_SelfIllum"))
                {
                    m.SetFloat(
                        "_SelfIllum",
                        Headlights > 0 ? 0.4f : 0.12f);
                }
            }

            // Unity-Scheinwerfer.
            if (_head != null)
            {
                foreach (var l in _head)
                {
                    if (l == null)
                        continue;

                    l.enabled = hl > 0;
                    l.range = hl == 2 ? 140f : 70f;
                    l.spotAngle = hl == 2 ? 50f : 75f;
                    l.intensity =
                        HeadlightIntensity * (hl == 2 ? 1.4f : 1f);

                    l.transform.rotation = Quaternion.LookRotation(
                        Quaternion.AngleAxis(
                            hl == 2 ? 1f : 5f,
                            transform.right) * fwd,
                        transform.up);
                }
            }

            // Bremslichter: beide Seiten.
            if (_brakeLights != null)
            {
                foreach (var l in _brakeLights)
                {
                    if (l == null)
                        continue;

                    l.enabled = RearPointLights && brake > 0f;
                    l.intensity = RearLightIntensity;
                    l.range = RearLightRange;
                }
            }

            // Rückfahrlichter: beide Seiten.
            if (_reverseLights != null)
            {
                foreach (var l in _reverseLights)
                {
                    if (l == null)
                        continue;

                    l.enabled = RearPointLights && reverse;
                    l.intensity = RearLightIntensity;
                    l.range = RearLightRange;
                }
            }
        }

        void OnDestroy()
        {
            foreach (var o in _owned)
            {
                if (o != null)
                    Destroy(o);
            }

            _owned.Clear();
        }
    }
}