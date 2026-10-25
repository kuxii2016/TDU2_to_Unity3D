using System;
using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    public enum DrivingAids { Secure, Sport, HyperSport, Off }

    /// <summary>
    /// Fahrphysik nach den TDU2-Daten:
    ///  - db_data.cpr/DBCarPhysicsData: Masse, Schwerpunkt (Lastverteilung %, Höhe), Abmessungen, Federraten/Dämpfer/Stabis,
    ///    Federweg, Bodenfreiheit, Getriebe (Übersetzungen, Achse, Schaltzeiten), Motor (Drehzahlen, Trägheit, Begrenzer),
    ///    Bremsen (Bremsweg 100-0, Bremsbalance), Reibwerte (längs/quer je Achse), Aerodynamik (cx, Abtrieb vorne/hinten,
    ///    Bodeneffekt), Antrieb (1 = Allrad, 3 = Front, 4 = Heck), Differentiale (Sperrwert %), ABS/ESP-Schlupfgrenzen je Modus.
    ///  - &lt;car&gt;_max.xmb / _min.xmb: Volllast- und Schubmomentkurve.
    ///  - Physics.cpr [Car]: Lenkwinkel/-geschwindigkeit über Tempo, Winkeldämpfung je Fahrhilfe, Extra-Schwerkraft,
    ///    NbLoopForMiddlePrecision (Unterschritte), TC/ABS-Aktivierungsgeschwindigkeiten.
    /// Reifen: Magic-Formula-Ã¤hnliche Kurven mit kombiniertem Schlupf (Reibellipse), Radspin implizit integriert.
    /// Koordinaten: Unity-Fahrzeugraum (+Z vorne, +X rechts, +Y oben).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class TDU2Vehicle : MonoBehaviour
    {
        public sealed class Wheel
        {
            public string Name;
            public bool Front, Left, Driven;
            public Vector3 Anchor;
            public float Radius, Width, Mass, Inertia;
            public float SpringK, DamperC, ArbK, TravelUp, Droop, StaticLoad;
            public float MuLong, MuLat;
            public float Compression, PrevCompression, SteerAngle, Omega, SpinAngle;
            public float Fz, SlipRatio, SlipAngle, LongSlipVel, LatSlipVel, DriveTorque, BrakeTorque, Fx, Fy, FxStep, FyStep;
            public bool Grounded;
            public Vector3 ContactPoint, ContactNormal = Vector3.up;
            public Collider GroundCollider;
            public int Surface;
            public Transform Fork, Stirrup, Disc;
            public Transform Spin2;
            public Vector3 ForkBasePos;
            public Quaternion StirrupBaseRot, DiscBaseRot, Spin2BaseRot;
            public float SteerSign = 1f;
        }

        public CarPhysics Car;
        public PhysicsConfig Config;
        public Curve TorqueMax, TorqueMin;
        public Wheel[] Wheels = Array.Empty<Wheel>();
        [Header("Drivinghelper/Gearbox")]
        public DrivingAids Aids = DrivingAids.Sport;
        public bool AutomaticGearbox = true;
        public bool AutoHold = true;
        public float AutoHoldSpeed = 0.6f;
        [Tooltip("Grip Skalierung (TDU2-Values ~1.9 - 2.0).")]
        [Range(0.3f, 1.2f)] public float GripScale = Gameconfig.vehicleGrip;
        public LayerMask GroundMask = ~(1 << 2);
        public Func<Vector3, (bool ok, float y)> GroundFallback;
        public float AccelPedal, BrakePedal, Steer, HandBrake;
        public float Throttle { get; private set; }
        public float Brake { get; private set; }
        public float HoldAmount { get; private set; }
        public float Rpm { get; private set; }
        public int Gear { get; private set; } = 1;   // -1 = R, 0 = N
        public float EngineTorque { get; private set; }
        public float SpeedKmh { get; private set; }
        public float ForwardSpeed { get; private set; }
        public bool Shifting => _shiftTimer > 0f;
        public event Action<int, int> GearChanged;
        public bool AnyWheelGrounded { get; private set; }

        Rigidbody _rb;
        float _idleW, _redW, _maxW, _engineW, _shiftTimer, _pendingGear, _limiterTimer, _airTime, _reverseTimer, _tcCut, _shiftCooldown;
        float _engineInertia, _brakeTorqueFront, _brakeTorqueRear, _handBrakeTorque;
        float _frontDriveShare;
        float _steerCurrent;
        float[] _gearRatios;
        int _nbGears;

        const float Rho = 1.225f;

        /// <summary>
        /// Init new Vehicle
        /// </summary>
        public void Setup()
        {
            _rb = GetComponent<Rigidbody>();
            var c = Car;
            bool isBike = c != null && c.IsBike;
            _rb.mass = Mathf.Max(isBike ? 100f : 200f, c != null ? c.Mass : 150f);
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.maxAngularVelocity = isBike ? 40f : 30f;
            #if UNITY_6000_0_OR_NEWER
            _rb.linearDamping = 0f;
            #else
            _rb.drag = 0f;
            #endif

            #region Vehicle Center/Gravity
            float zf = 0, zr = 0, yWheel = 0; int nf = 0, nr = 0;
            foreach (var w in Wheels) { if (w.Front) { zf += w.Anchor.z; nf++; } else { zr += w.Anchor.z; nr++; } yWheel += w.Anchor.y; }
            if (nf > 0) zf /= nf; if (nr > 0) zr /= nr; if (Wheels.Length > 0) yWheel /= Wheels.Length;
            float frontShare = c != null ? Mathf.Clamp(c.GravityCenterLength / 100f, 0.2f, 0.8f) : 0.5f;
            float cgZ = Mathf.Lerp(zr, zf, frontShare);
            float cgY = yWheel + (c != null ? Mathf.Clamp(c.GravityCenterHeight, -10f, 80f) * 0.01f : 0.4f);
            if (isBike) cgY = Mathf.Max(yWheel + 0.25f, cgY);
            _rb.centerOfMass = new Vector3(0f, cgY, cgZ);
            #endregion

            float L = c != null ? c.Length * 0.001f : 2.2f, W = c != null ? c.Width * 0.001f : 0.8f, H = c != null ? c.Height * 0.001f : 1.1f, m = _rb.mass;
            const float k = 0.75f;
            if (isBike)
            {
                // Bikes need more roll inertia damping tendency - reduce lateral inertia a bit
                _rb.inertiaTensor = new Vector3(m / 12f * (H * H + L * L) * k * 0.8f, m / 12f * (W * W + L * L) * k * 1.6f, m / 12f * (W * W + H * H) * k);
            }
            else
            {
                _rb.inertiaTensor = new Vector3(m / 12f * (H * H + L * L) * k, m / 12f * (W * W + L * L) * k, m / 12f * (W * W + H * H) * k);
            }
            _rb.inertiaTensorRotation = Quaternion.identity;

            float g = Physics.gravity.magnitude;
            foreach (var w in Wheels)
            {
                #region Wheel fenders
                float axleShare = w.Front ? frontShare : 1f - frontShare;
                int perAxle = 0; foreach (var o in Wheels) if (o.Front == w.Front) perAxle++;
                w.StaticLoad = m * g * axleShare / Mathf.Max(1, perAxle);
                float rate = w.Front ? c.SuspensionRateFront : c.SuspensionRateRear;
                float damp = w.Front ? c.DamperRateFront : c.DamperRateRear;
                float arb = w.Front ? c.AntiRollFront : c.AntiRollRear;
                w.SpringK = Mathf.Max(5000f, rate * 1000f);
                float cornerMass = w.StaticLoad / g;
                float crit = 2f * Mathf.Sqrt(w.SpringK * cornerMass);
                w.DamperC = Mathf.Clamp(damp * 1000f, 0.25f * crit, 0.9f * crit);
                w.ArbK = Mathf.Max(0f, arb * 1000f);
                float travel = Mathf.Max(0.06f, (w.Front ? c.SuspensionLengthFront : c.SuspensionLengthRear) * 0.001f);
                w.TravelUp = travel * 0.6f;
                w.Droop = travel * 0.4f + 0.05f;
                w.Mass = w.Front ? c.WheelMassFront : c.WheelMassRear;
                w.Inertia = Mathf.Max(0.3f, 0.75f * w.Mass * w.Radius * w.Radius);
                w.MuLong = (w.Front ? c.MaxLongFrictionFront : c.MaxLongFrictionRear);
                w.MuLat = (w.Front ? c.MaxLatFrictionFront : c.MaxLatFrictionRear);
                w.Driven = c.Drive == 1 || (c.Drive == 3 && w.Front) || (c.Drive != 1 && c.Drive != 3 && !w.Front);
                #endregion
            }
            _frontDriveShare = c.Drive == 1 ? Mathf.Clamp(1f - Mathf.Clamp(c.DiffValueCentral, 0f, 100f) / 100f * 0.6f, 0.3f, 0.5f) : c.Drive == 3 ? 1f : 0f;

            #region Engine
            _idleW = Mathf.Max(500f, c.RpmMin) * Mathf.PI / 30f;
            _redW = Mathf.Max(c.RedLine, c.RpmMin + 1000f) * Mathf.PI / 30f;
            _maxW = Mathf.Max(c.RpmMax, c.RedLine) * Mathf.PI / 30f;
            _engineW = _idleW;
            _engineInertia = Mathf.Clamp(c.EngineInertia * 0.01f, 0.08f, 1.2f);

            _nbGears = Mathf.Clamp(c.NbGears, 1, 11);
            _gearRatios = new float[_nbGears];
            for (int i = 0; i < _nbGears; i++) _gearRatios[i] = c.GearRatios[i] > 0f ? c.GearRatios[i] : Mathf.Max(0.5f, 3.5f - i * 0.45f);
            #endregion
            #region Breaks
            float distM = c != null && c.Braking100To0 > 20f ? c.Braking100To0 * 0.3048f : (isBike ? 30f : 40f);
            float decel = Mathf.Clamp(27.78f * 27.78f / (2f * distM), 6f, isBike ? 14f : 16f) * 1.25f;
            float total = m * decel;
            float bal = c != null ? Mathf.Clamp(c.BrakeBalance, 30f, 80f) / 100f : (isBike ? 0.7f : 0.6f);
            float rFront = 0.33f, rRear = 0.33f;
            int cntF = 0, cntR = 0;
            foreach (var w in Wheels) { if (w.Front) { rFront = w.Radius; cntF++; } else { rRear = w.Radius; cntR++; } }
            if (cntF == 0) cntF = 1; if (cntR == 0) cntR = 1;
            _brakeTorqueFront = total * bal * (0.5f / cntF) * rFront;
            _brakeTorqueRear = total * (1f - bal) * (0.5f / cntR) * rRear;
            _handBrakeTorque = m * g * 0.35f * rRear / Mathf.Max(1, cntR);
            #endregion
            Gear = 1;
        }

        void Awake() { _rb = GetComponent<Rigidbody>(); }

        void FixedUpdate()
        {
            if (Car == null || Wheels.Length == 0) return;
            float dt = Time.fixedDeltaTime;
            var tr = transform;
            bool isBike = Car != null && Car.IsBike;
            #if UNITY_6000_0_OR_NEWER
            Vector3 vel = _rb.linearVelocity;
            #else
            Vector3 vel = _rb.velocity;
            #endif
            ForwardSpeed = Vector3.Dot(vel, tr.forward);
            SpeedKmh = vel.magnitude * 3.6f;
            float g = Physics.gravity.magnitude;
            UpdateSteering(dt);
            UpdateGearbox(dt);
            if (Gear < 0) { Throttle = Mathf.Clamp01(BrakePedal); Brake = Mathf.Clamp01(AccelPedal); }
            else { Throttle = Mathf.Clamp01(AccelPedal); Brake = Mathf.Clamp01(BrakePedal); }
            float holdTarget = AutoHold && Throttle < 0.05f && Mathf.Abs(ForwardSpeed) < AutoHoldSpeed ? 1f : 0f;
            HoldAmount = Mathf.MoveTowards(HoldAmount, holdTarget, dt * (holdTarget > HoldAmount ? 3f : 20f));

            AnyWheelGrounded = false;
            foreach (var w in Wheels) Suspension(w, dt);
            if (!isBike)
            {
                for (int i = 0; i < Wheels.Length; i++)
                    for (int j = i + 1; j < Wheels.Length; j++)
                    {
                        var a = Wheels[i]; var b = Wheels[j];
                        if (a.Front != b.Front || a.Left == b.Left) continue;
                        float diff = (a.Grounded ? a.Compression : -a.Droop) - (b.Grounded ? b.Compression : -b.Droop);
                        float f = a.ArbK * diff;
                        if (a.Grounded) a.Fz = Mathf.Max(0f, a.Fz + f);
                        if (b.Grounded) b.Fz = Mathf.Max(0f, b.Fz - f);
                    }
            }
            else BikeBalance(dt);

            // ---------------------------------------------------- Antrieb + Reifen (Unterschritte)
            int subCount = isBike ? 4 : Mathf.Clamp(Mathf.RoundToInt(Config != null ? Config.Get("NbLoopForMiddlePrecision", 3) : 3), 1, 8);
            float hStep = dt / subCount;
            foreach (var w in Wheels) { w.Fx = 0f; w.Fy = 0f; ComputeContactVelocities(w); }
            float sumFxTotal = 0f;
            for (int s = 0; s < subCount; s++) sumFxTotal += Powertrain(hStep, 1f / subCount);

            // ---------------------------------------------------- KrÃ¤fte anwenden
            foreach (var w in Wheels)
            {
                if (!w.Grounded) continue;
                Vector3 n = w.ContactNormal;
                Vector3 fwd = WheelForward(w, n);
                Vector3 right = Vector3.Cross(n, fwd);
                Vector3 force = n * w.Fz + fwd * w.Fx + right * w.Fy;
                _rb.AddForceAtPosition(force, w.ContactPoint);
                if (w.GroundCollider != null && w.GroundCollider.attachedRigidbody != null)
                    w.GroundCollider.attachedRigidbody.AddForceAtPosition(-force, w.ContactPoint);
            }

            Aerodynamics(vel);
            Stability(dt, g);
            OffroadDamping(vel, dt);
        }

        // ------------------------------------------------------------------ Untergrund (Asphalt / Gras / Erde)
        [Header("Untergrund")]
        [Tooltip("Reibwert-Faktor auf Erde/Feldwegen bzw. Gras (Asphalt = 1)")]
        [Range(0.3f, 1f)] public float DirtGrip = 0.82f, GrassGrip = 0.72f;
        [Tooltip("GelÃ¤ndewagen (Allrad): DÃ¤mpfung OffRoad* statt NotOffRoad* aus Physics.cpr")]
        public bool OffRoadCar;
        /// <summary>Reibwert-Faktor je Untergrund (0..1). Geländewagen nutzen die DB-Felder _offroad_*_friction_percent.</summary>
        void SurfaceGrip(int surface, out float lat, out float lon)
        {
            lat = 1f; lon = 1f;
            if (surface == 0 || Car == null) return;
            bool offroad = OffRoadCar || Car.IsOffroad || Car.OffroadReal;
            if (surface == 2)   // Erde
            {
                lat = offroad ? PctFrac(Car.OffroadDirtLatFrictionPercent, DirtGrip) : DirtGrip;
                lon = offroad ? PctFrac(Car.OffroadDirtLongFrictionPercent, DirtGrip) : DirtGrip;
            }
            else                // Gras
            {
                lat = offroad ? PctFrac(Car.OffroadGrassLatFrictionPercent, GrassGrip) : GrassGrip;
                lon = offroad ? PctFrac(Car.OffroadGrassLongFrictionPercent, GrassGrip) : GrassGrip;
            }
        }
        static float PctFrac(float percent, float fallback) => percent > 1.5f ? Mathf.Clamp01(percent / 100f) : percent > 0f ? percent : fallback;
        /// <summary>Anteil der RÃ¤der mit Bodenkontakt je Untergrund (0 Asphalt, 1 Gras, 2 Erde) â€“ fÃ¼r Sound/Effekte.</summary>
        public readonly float[] SurfaceShare = { 1f, 0f, 0f };

        static readonly Dictionary<int, int> _surfCache = new Dictionary<int, int>();

        /// <summary>
        /// Untergrund aus dem Collider: Feldwege sind im Original keine eigenen Straßen-Meshes, sondern Gelände (Heightmap mit
        /// Erd-Detail) -> Terrain = Erde. Objekte mit Gras-/Erd-Material (TD_Ground Set_Grass, dirt...) entsprechend, sonst Asphalt.
        /// </summary>
        public static int SurfaceOf(Collider c)
        {
            if (c == null) return 0;
            int id = c.GetInstanceID();
            if (_surfCache.TryGetValue(id, out int s)) return s;
            s = 0;
            if (c.name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0) s = 2;
            else
            {
                var r = c.GetComponent<Renderer>();
                var mats = r != null ? r.sharedMaterials : null;
                if (mats != null && mats.Length > 0)
                {
                    int grass = 0, dirt = 0;
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        string n = m.name + " " + (m.mainTexture != null ? m.mainTexture.name : "") + " " + (m.HasProperty("_DetailTex") && m.GetTexture("_DetailTex") != null ? m.GetTexture("_DetailTex").name : "");
                        n = n.ToLowerInvariant();
                        if (n.Contains("grass")) grass++;
                        else if (n.Contains("earth") || n.Contains("sand") || n.Contains("gravel") || n.Contains("terre") || n.Contains("d2_") || n.Contains("dirt_road")) dirt++;
                    }
                    if (grass * 2 > mats.Length) s = 1;
                    else if (dirt * 2 > mats.Length) s = 2;
                }
            }
            if (_surfCache.Count > 20000) _surfCache.Clear();
            _surfCache[id] = s;
            return s;
        }

        /// <summary>Physics.cpr: (Not)OffRoadDirt/GrassLinearDamping über km/h bremst auf Erde/Gras wie im Spiel.</summary>
        void OffroadDamping(Vector3 vel, float dt)
        {
            int n = 0; float g0 = 0f, g1 = 0f, g2 = 0f;
            foreach (var w in Wheels)
            {
                if (!w.Grounded) continue;
                n++;
                if (w.Surface == 1) g1++; else if (w.Surface == 2) g2++; else g0++;
            }
            if (n == 0) return;
            SurfaceShare[0] = g0 / n; SurfaceShare[1] = g1 / n; SurfaceShare[2] = g2 / n;
            if (Config == null || (g1 + g2) <= 0f) return;
            string pre = (OffRoadCar || (Car != null && (Car.IsOffroad || Car.OffroadReal))) ? "OffRoad" : "NotOffRoad";
            float dDirt = Config.GetCurve(pre + "DirtLinearDamping", 0.1f).Evaluate(SpeedKmh);
            float dGrass = Config.GetCurve(pre + "GrassLinearDamping", 0.15f).Evaluate(SpeedKmh);
            float d = dDirt * SurfaceShare[2] + dGrass * SurfaceShare[1];
            if (d <= 0f) return;
            // Havok-Lineardämpfung: v *= 1 / (1 + d dt) -> als Kraft (nur waagerecht, damit Sprünge nicht gebremst werden)
            var hv = new Vector3(vel.x, 0f, vel.z);
            float k = 1f - 1f / (1f + d * dt);
            _rb.AddForce(-hv * (k / dt) * _rb.mass);
        }

        // ------------------------------------------------------------------ Lenkung (Physics.cpr: SteeringMaxAngle / SteeringVelocity über km/h)
        void UpdateSteering(float dt)
        {
            bool isBike = Car != null && Car.IsBike;
            float maxAngle = Config != null ? Config.GetCurve("SteeringMaxAngle", 25f).Evaluate(SpeedKmh) : 25f;
            float sens = Config != null ? Config.GetCurve("SteeringSensitivity", 1f).Evaluate(SpeedKmh) : 1f;
            float vel = Config != null ? Config.GetCurve("SteeringVelocity", 360f).Evaluate(SpeedKmh) : 360f;
            if (isBike)
            {
                maxAngle = Mathf.Clamp(maxAngle * 0.7f, 16f, 28f);
                sens = sens / 0.9f;
                vel = vel * 0.8f;
            }
            float target = Mathf.Clamp(Steer, -1f, 1f) * maxAngle * sens;
            _steerCurrent = Mathf.MoveTowards(_steerCurrent, target, vel * dt);

            float wb = Mathf.Max(1f, Car.Wheelbase * 0.001f), track = Mathf.Max(1f, Car.TrackFront * 0.001f);
            float a = _steerCurrent * Mathf.Deg2Rad;
            foreach (var w in Wheels)
            {
                float toe = (w.Front ? Car.ToeFront : Car.ToeRear) * (w.Left ? -1f : 1f) * 0.1f;
                if (!w.Front) { w.SteerAngle = toe; continue; }
                if (Mathf.Abs(a) < 1e-4f) { w.SteerAngle = toe; continue; }
                float r = wb / Mathf.Tan(Mathf.Abs(a));
                bool inner = (a > 0f) != w.Left;   // Rechtskurve: rechtes Rad innen
                float ang = Mathf.Atan(wb / (r + (inner ? -0.5f : 0.5f) * track)) * Mathf.Rad2Deg;
                w.SteerAngle = Mathf.Sign(a) * ang + toe;
            }
        }

        public float SteeringAngle => _steerCurrent;

        void Suspension(Wheel w, float dt)
        {
            var tr = transform;
            Vector3 up = tr.up;
            Vector3 anchor = tr.TransformPoint(w.Anchor);
            Vector3 origin = anchor + up * w.TravelUp;
            float maxDist = w.TravelUp + w.Droop + w.Radius;
            w.Grounded = false; w.GroundCollider = null;
            float dist = 0f;
            if (Physics.Raycast(origin, -up, out var hit, maxDist, GroundMask, QueryTriggerInteraction.Ignore))
            {
                w.Grounded = true; dist = hit.distance; w.ContactNormal = hit.normal; w.ContactPoint = hit.point; w.GroundCollider = hit.collider;
                w.Surface = SurfaceOf(hit.collider);
            }
            else if (GroundFallback != null)
            {
                var (ok, gy) = GroundFallback(anchor);
                if (ok)
                {
                    // senkrechter Abstand entlang -up bis zur Ebene y = gy
                    float denom = Mathf.Max(0.2f, up.y);
                    float d = (origin.y - gy) / denom;
                    if (d >= 0f && d <= maxDist) { w.Grounded = true; dist = d; w.ContactNormal = Vector3.up; w.ContactPoint = origin - up * d; }
                    else if (d < 0f) { w.Grounded = true; dist = 0f; w.ContactNormal = Vector3.up; w.ContactPoint = origin; }
                }
            }
            w.PrevCompression = w.Compression;
            if (!w.Grounded)
            {
                w.Compression = -w.Droop; w.Fz = 0f;
                return;
            }
            AnyWheelGrounded = true;
            // Radmitte relativ zur Konstruktionslage (+ = eingefedert)
            w.Compression = w.TravelUp - (dist - w.Radius);
            float cv = (w.Compression - w.PrevCompression) / dt;
            float f = w.StaticLoad + w.SpringK * w.Compression + w.DamperC * cv;
            if (w.Compression > w.TravelUp * 0.9f) f += w.SpringK * 8f * (w.Compression - w.TravelUp * 0.9f);   // Anschlagpuffer
            // Normalkraft entlang der Kontaktnormalen skaliert
            w.Fz = Mathf.Max(0f, f) * Mathf.Clamp01(Vector3.Dot(w.ContactNormal, up) * 1.2f);
        }

        Vector3 WheelForward(Wheel w, Vector3 n)
        {
            var tr = transform;
            Vector3 f = Quaternion.AngleAxis(w.SteerAngle, tr.up) * tr.forward;
            f = Vector3.ProjectOnPlane(f, n);
            return f.sqrMagnitude > 1e-6f ? f.normalized : tr.forward;
        }

        float[] _vx = new float[8], _vy = new float[8];

        void ComputeContactVelocities(Wheel w)
        {
            int i = Array.IndexOf(Wheels, w);
            if (i < 0 || i >= _vx.Length) return;
            if (!w.Grounded) { _vx[i] = Vector3.Dot(_rb.GetPointVelocity(transform.TransformPoint(w.Anchor)), transform.forward); _vy[i] = 0f; return; }
            Vector3 v = _rb.GetPointVelocity(w.ContactPoint);
            if (w.GroundCollider != null && w.GroundCollider.attachedRigidbody != null) v -= w.GroundCollider.attachedRigidbody.GetPointVelocity(w.ContactPoint);
            Vector3 fwd = WheelForward(w, w.ContactNormal);
            Vector3 right = Vector3.Cross(w.ContactNormal, fwd);
            _vx[i] = Vector3.Dot(v, fwd);
            _vy[i] = Vector3.Dot(v, right);
        }

        float Powertrain(float h, float weight)
        {
            var c = Car;
            // Fahrhilfen
            bool isBikePhy = c != null && c.IsBike;
            float absSlip, tcSlip;
            if (c != null)
            {
                switch (Aids)
                {
                    case DrivingAids.Secure: absSlip = c.AbsSlipSecure; tcSlip = c.TcSlipSecure; break;
                    case DrivingAids.HyperSport: absSlip = c.AbsSlipHyper; tcSlip = c.TcSlipHyper; break;
                    case DrivingAids.Off: absSlip = -10f; tcSlip = -1f; break;
                    default: absSlip = c.AbsSlipSport; tcSlip = c.TcSlipSport; break;
                }
            }
            else
            {
                absSlip = isBikePhy ? -5f : -10f; tcSlip = isBikePhy ? -1f : -1f;
            }
            bool absOn = (c != null ? c.Abs : true) && Aids != DrivingAids.Off && SpeedKmh > (Config?.Get("AbsControlVelocityMin", 1f) ?? 1f);
            bool tcOn = (c != null ? c.TractionControl : true) && tcSlip > 0f;

            // ---- Motor / Kupplung
            float throttle = Mathf.Clamp01(Throttle) * (1f - _tcCut);
            if (_limiterTimer > 0f) { _limiterTimer -= h; throttle = 0f; }
            float ratio = CurrentRatio();
            bool inGear = Gear != 0 && !Shifting;
            // Während des Schaltvorgangs kein Gas: Kraftschluss ist offen (Gear = 0), Drehzahl fällt ab.
            // Erst beim Einkuppeln wird wieder Moment aufgenommen kein "Gas durchschalten".
            if (Shifting) throttle = 0f;

            float wheelW = 0f; int nDriven = 0;
            foreach (var w in Wheels) if (w.Driven) { wheelW += w.Omega; nDriven++; }
            if (nDriven > 0) wheelW /= nDriven;

            // Vorzeichenbehaftet: Räder drehen in Gangrichtung? (sonst Kupplung offen sonst wärde das
            // Motorbremsmoment ein rückwärts rollendes Auto im 1. Gang weiter rückwärts schieben)
            float gearDir = Gear < 0 ? -1f : 1f;
            float coupledW = wheelW * ratio * gearDir;
            bool slipping = false;
            float clutchTorque = 0f;
            float rpm;
            if (inGear && coupledW >= _idleW * 1.05f)
            {
                _engineW = coupledW;
                rpm = _engineW * 30f / Mathf.PI;
                if (rpm >= c.RedLine && c.RupteurTime > 0f) _limiterTimer = c.RupteurTime;
                EngineTorque = EngineTorqueAt(rpm, throttle);
            }
            else
            {
                rpm = _engineW * 30f / Mathf.PI;
                float te = EngineTorqueAt(rpm, throttle);
                float friction = (c.EngineDamping > 0 ? c.EngineDamping : 0.1f) * _engineW * 0.5f;
                if (inGear && throttle > 0.02f)
                {
                    slipping = true;
                    float launchW = Mathf.Lerp(_idleW * 1.2f, Mathf.Min(c.TorqueRpm, c.RedLine * 0.7f) * Mathf.PI / 30f, throttle);
                    clutchTorque = Mathf.Max(0f, te) * Mathf.Clamp01(0.6f + 0.4f * throttle);
                    _engineW = Mathf.MoveTowards(_engineW, launchW, (te - friction) / _engineInertia * h);
                }
                else
                {
                    _engineW += (te - friction) / _engineInertia * h;
                }
                if (_engineW < _idleW) _engineW = Mathf.MoveTowards(_engineW, _idleW, 2000f * h);
                _engineW = Mathf.Clamp(_engineW, _idleW * 0.5f, _maxW);
                rpm = _engineW * 30f / Mathf.PI;
                if (rpm >= c.RedLine) _limiterTimer = Mathf.Max(_limiterTimer, c.RupteurTime);
                EngineTorque = slipping ? clutchTorque : te;
            }
            Rpm = rpm;

            float driveTotal = 0f;   // Moment an den Antriebsrädern (Summe)
            float dir = Gear < 0 ? -1f : 1f;
            if (inGear) driveTotal = (slipping ? clutchTorque : EngineTorque) * ratio * 0.92f * dir;

            float frontT = driveTotal * _frontDriveShare, rearT = driveTotal * (1f - _frontDriveShare);
            AxleDiff(true, frontT, c.DiffValueFront, h);
            AxleDiff(false, rearT, c.DiffValueRear, h);

            float extraInertia = (inGear && !slipping && nDriven > 0) ? _engineInertia * ratio * ratio / nDriven : 0f;

            // ---- Bremsen
            float brakeIn = Mathf.Clamp01(Brake);
            float sumFx = 0f;
            float tcMaxSlip = 0f;
            for (int i = 0; i < Wheels.Length; i++)
            {
                var w = Wheels[i];
                float bt = Mathf.Max(brakeIn, HoldAmount * 0.6f) * (w.Front ? _brakeTorqueFront : _brakeTorqueRear);
                if (!w.Front) bt += Mathf.Clamp01(HandBrake) * _handBrakeTorque;
                // ABS: Bremse lösen, wenn Längsschlupf unter die Grenze fällt
                bool isBikeB = Car != null && Car.IsBike;
                if (absOn && brakeIn > 0.05f && w.Grounded && w.SlipRatio < absSlip && HandBrake < 0.1f) bt *= isBikeB ? 0.4f : 0.25f;
                w.BrakeTorque = bt;

                TireAndSpin(w, i, h, w.Driven ? extraInertia : 0f);
                w.Fx += w.FxStep * weight;
                w.Fy += w.FyStep * weight;
                sumFx += w.FxStep;
                if (w.Driven && w.Grounded) tcMaxSlip = Mathf.Max(tcMaxSlip, w.SlipRatio);
            }

            // ---- Traktionskontrolle
            // Schlupfgrenze der DB als Vielfaches des Peak-Schlupfs (Sport 0.8 = knapp unter dem Optimum, HyperSport 10 = fast aus)
            bool isBikeT = Car != null && Car.IsBike;
            float tcKappa = 0.10f * Mathf.Clamp(tcSlip, 0.6f, 10f);
            if (tcOn && tcMaxSlip > tcKappa) _tcCut = Mathf.MoveTowards(_tcCut, isBikeT ? 0.7f : 0.9f, h * 8f);
            else _tcCut = Mathf.MoveTowards(_tcCut, 0f, h * 4f);
            return sumFx;
        }

        float EngineTorqueAt(float rpm, float throttle)
        {
            float tMax = TorqueMax != null ? TorqueMax.Evaluate(rpm) : Car.TorqueNm;
            float tMin = TorqueMin != null ? TorqueMin.Evaluate(rpm) : -Car.TorqueNm * 0.1f;
            if (rpm < Car.RpmMin * 0.9f) tMin = 0f;
            return Mathf.Lerp(tMin, tMax, throttle);
        }

        float CurrentRatio()
        {
            if (Gear == 0) return 0f;
            float r = Gear < 0 ? (_gearRatios.Length > 0 ? _gearRatios[0] * 1.1f : 3.2f) : _gearRatios[Mathf.Clamp(Gear - 1, 0, _gearRatios.Length - 1)];
            return r * Mathf.Max(1f, Car.FinalDrive);
        }

        /// <summary>Offenes/Sperr-Differential: Grundaufteilung 50/50, Sperrmoment ~ Drehzahldifferenz, begrenzt durch Sperrwert (%).</summary>
        void AxleDiff(bool front, float torque, float lockPercent, float h)
        {
            Wheel l = null, r = null;
            foreach (var w in Wheels) if (w.Front == front) { if (w.Left) l = w; else r = w; }
            if (l == null || r == null) { foreach (var w in Wheels) if (w.Front == front) w.DriveTorque = torque; return; }
            float half = torque * 0.5f;
            float lockMax = Mathf.Abs(torque) * Mathf.Clamp01(lockPercent / 100f) * 0.5f + 30f * Mathf.Clamp01(lockPercent / 100f);
            float dw = l.Omega - r.Omega;
            float iAvg = (l.Inertia + r.Inertia) * 0.5f;
            float lockT = Mathf.Clamp(dw * iAvg / Mathf.Max(h, 1e-4f) * 0.5f, -lockMax, lockMax);
            l.DriveTorque = half - lockT;
            r.DriveTorque = half + lockT;
        }

        void TireAndSpin(Wheel w, int index, float h, float extraInertia)
        {
            float vx = _vx[index], vy = _vy[index];
            float inertia = w.Inertia + extraInertia;
            if (!w.Grounded)
            {
                w.FxStep = 0f; w.FyStep = 0f;
                float tq = w.DriveTorque - Mathf.Sign(w.Omega) * Mathf.Min(Mathf.Abs(w.Omega) * inertia / h, w.BrakeTorque);
                w.Omega += tq / inertia * h;
                w.Omega *= 1f - 0.2f * h;
                w.SlipRatio = 0f; w.SlipAngle = 0f; w.LongSlipVel = 0f; w.LatSlipVel = 0f;
                return;
            }

            float R = w.Radius;
            float fz = w.Fz;
            float fz0 = Mathf.Max(1f, w.StaticLoad);
            float loadSens = Mathf.Clamp(1f - 0.08f * (fz / fz0 - 1f), 0.7f, 1.1f);
            // Untergrund-Reibwert: Asphalt = 1. GelÃ¤ndewagen behalten auf Erde/Gras laut DB-Feld
            // _offroad_(dirt|grass)_(lat|long)_friction_percent ihren Grip (80..100 %), normale Autos verlieren mehr.
            SurfaceGrip(w.Surface, out float sgLat, out float sgLong);
            // Regen (TDU2Weather.GroundWet 0..1): Reibwert -> WetFrictionPercent der Fahrzeugdatenbank (z.B. 80 %)
            float wet = TDU2.Map.World.TDU2Weather.GroundWet;
            if (wet > 0f && Car != null)
            {
                float wf = Car.WetFrictionPercent > 1.5f ? Car.WetFrictionPercent / 100f : Car.WetFrictionPercent > 0f ? Car.WetFrictionPercent : 0.8f;
                float rw = Mathf.Lerp(1f, Mathf.Clamp(wf, 0.4f, 1f), wet);
                sgLat *= rw; sgLong *= rw;
            }
            float muX = w.MuLong * GripScale * loadSens * sgLong;
            float muY = w.MuLat * GripScale * loadSens * sgLat;

            float denom = Mathf.Max(Mathf.Abs(vx), 4f);
            float alpha = Mathf.Atan2(vy, denom);

            //float Fx(float om, out float fyOut)
            //{
            //    float kappa = (om * R - vx) / denom;
            //    Combined(kappa, alpha, muX, muY, fz, out float fx, out float fy);
            //    fyOut = fy;
            //    return fx;
            //}
            //float fx0 = Fx(w.Omega, out float fy0);
            //float dOm = 0.5f;
            //float fx1 = Fx(w.Omega + dOm, out _);
            //float k = (fx1 - fx0) / dOm;            // dFx/dÏ‰ (>= 0)
            //if (k < 0f) k = 0f;

            //// Rollwiderstand
            //float rollT = 0.012f * fz * R * Mathf.Clamp(w.Omega, -1f, 1f);

            //// implizites Euler fÃ¼r den linearen Anteil: I*(Ï‰'-Ï‰)/h = T - (Fx0 + k(Ï‰'-Ï‰))R - Brems/Roll
            //float torqueNoBrake = w.DriveTorque - fx0 * R - rollT;
            //float omNew = w.Omega + h * torqueNoBrake / (inertia + h * k * R);
            //// Bremse: wirkt gegen die Drehrichtung, kann das Rad maximal blockieren
            //if (w.BrakeTorque > 0f)
            //{
            //    float maxDelta = w.BrakeTorque * h / (inertia + h * k * R);
            //    if (Mathf.Abs(omNew) <= maxDelta) omNew = 0f;
            //    else omNew -= Mathf.Sign(omNew) * maxDelta;
            //}
            //w.Omega = omNew;
            // Längskraft als Funktion von omega (für implizite Integration linearisiert)
            float Fx(float om, out float fyOut)
            {
                float kappa = (om * R - vx) / denom;
                Combined(kappa, alpha, muX, muY, fz, out float fx, out float fy);
                fyOut = fy;
                return fx;
            }

            // Hier können wir den Kompromiss zwischen Reifenkraft und Dämpfung anpassen
            float fx0 = Fx(w.Omega, out float fy0);
            float dOm = 0.5f;
            float fx1 = Fx(w.Omega + dOm, out _);
            float k = (fx1 - fx0) / dOm;            // dFx/dω (>= 0)
            if (k < 0f) k = 0f;

            // Rollwiderstand
            float rollT = 0.012f * fz * R * Mathf.Clamp(w.Omega, -1f, 1f);

            // Hier die Stabilität erhöhen durch verstärkte Dämpfung
            if (Aids == DrivingAids.Secure)
            {
                k *= 1.3f; // Erhöhte Reibungskraft im sicheren Modus
            }

            // implizites Euler für den linearen Anteil: I*(ω'-ω)/h = T - (Fx0 + k(ω'-ω))R - Brems/Roll
            float torqueNoBrake = w.DriveTorque - fx0 * R - rollT;
            float omNew = w.Omega + h * torqueNoBrake / (inertia + h * k * R);

            // Bremse: wirkt gegen die Drehrichtung, kann das Rad maximal blockieren
            if (w.BrakeTorque > 0f)
            {
                float maxDelta = w.BrakeTorque * h / (inertia + h * k * R);
                if (Mathf.Abs(omNew) <= maxDelta) omNew = 0f;
                else omNew -= Mathf.Sign(omNew) * maxDelta;
            }
            w.Omega = omNew;
            w.SpinAngle += w.Omega * h * Mathf.Rad2Deg;

            float fxFinal = Fx(w.Omega, out float fyFinal);
            w.FxStep = fxFinal;
            w.FyStep = fyFinal;
            w.SlipRatio = (w.Omega * R - vx) / denom;
            w.SlipAngle = alpha;
            w.LongSlipVel = vx - w.Omega * R;
            w.LatSlipVel = Mathf.Abs(vy);
        }

        /// <summary>Kombinierter Schlupf (normierte Magic Formula, Reibellipse).</summary>
        static void Combined(float kappa, float alpha, float muX, float muY, float fz, out float fx, out float fy)
        {
            const float kPeak = 0.10f, aPeak = 0.14f;
            float sx = kappa / kPeak;
            float sy = Mathf.Tan(Mathf.Clamp(alpha, -1.4f, 1.4f)) / Mathf.Tan(aPeak);
            float s = Mathf.Sqrt(sx * sx + sy * sy);
            if (s < 1e-5f) { fx = 0f; fy = 0f; return; }
            // Form C = 1.6 (Peak bei s = 1, bei durchdrehenden Rädern ~70 % Restgrip) abgeglichen am 458:
            // 0-100 km/h 3.5 s (DB 3.4 s), Vmax 329 km/h (DB 325 km/h) mit Sport-ESP
            float mx = Mathf.Sin(1.6f * Mathf.Atan(1.5f * s));
            float my = mx;
            fx = muX * fz * mx * (sx / s);
            fy = -muY * fz * my * (sy / s);
        }

        void Aerodynamics(Vector3 vel)
        {
            var c = Car;
            float v = vel.magnitude;
            if (v < 0.5f) return;
            float area = c.Width * c.Height * 1e-6f * 0.82f;
            float cd = c.Cx > 0f ? c.Cx : 0.35f;
            Vector3 drag = -vel.normalized * (0.5f * Rho * cd * area * v * v);
            _rb.AddForce(drag);

            float kmh = v * 3.6f;
            float liftV = Mathf.Max(50f, c.LiftVelocity);
            float q = (kmh / liftV) * (kmh / liftV);
            float g = Physics.gravity.magnitude;
            Vector3 down = -transform.up;
            float zf = 0, zr = 0; int nf = 0, nr = 0;
            foreach (var w in Wheels) { if (w.Front) { zf += w.Anchor.z; nf++; } else { zr += w.Anchor.z; nr++; } }
            if (nf > 0) zf /= nf; if (nr > 0) zr /= nr;
            if (c.LiftLoadFront > 0f) _rb.AddForceAtPosition(down * c.LiftLoadFront * g * q, transform.TransformPoint(new Vector3(0, 0.3f, zf)));
            if (c.LiftLoadRear > 0f) _rb.AddForceAtPosition(down * c.LiftLoadRear * g * q, transform.TransformPoint(new Vector3(0, 0.3f, zr)));
            if (c.GroundEffectLoad > 0f && AnyWheelGrounded)
            {
                float ge = Mathf.Max(50f, c.GroundEffectVelocity);
                float qg = (kmh / ge) * (kmh / ge);
                _rb.AddForce(down * c.GroundEffectLoad * g * qg);
            }
        }

        //        void Stability(float dt, float g)
        //        {
        //            if (Config == null) return;
        //            string key = Aids == DrivingAids.Secure ? "SecureAngularDamping" : Aids == DrivingAids.HyperSport || Aids == DrivingAids.Off ? "HyperSportAngularDamping" : "SportAngularDamping";
        //            float damping = Config.GetCurve(key, 0.3f).Evaluate(SpeedKmh);
        //            if (Car != null && Car.IsBike)
        //                damping = Mathf.Max(damping, Mathf.Lerp(0.8f, 0.95f, Mathf.Clamp01(SpeedKmh / 160f)));
        //#if UNITY_6000_0_OR_NEWER
        //            _rb.angularDamping = damping;
        //#else
        //            _rb.angularDrag = damping;
        //#endif
        //            // Extra-Schwerkraft wenn nicht alle RÃ¤der Bodenkontakt haben (hÃ¤lt das Auto am Boden)
        //            bool allGrounded = true;
        //            foreach (var w in Wheels) if (!w.Grounded) { allGrounded = false; break; }
        //            if (!allGrounded) _airTime += dt; else _airTime = 0f;
        //            float delay = Config.Get("ExtraGravityAccelDelay", 0.01f), dur = Mathf.Max(0.01f, Config.Get("ExtraGravityAccelDuration", 0.1f));
        //            if (_airTime > delay)
        //            {
        //                float t = Mathf.Clamp01((_airTime - delay) / dur);
        //                float extra = Mathf.Lerp(Config.Get("ExtraGravityMin", 0.1f), Config.Get("ExtraGravityMax", 1f), t);
        //                _rb.AddForce(Vector3.down * g * extra * _rb.mass);
        //            }
        //        }
        // In der Stability-Methode (ca. Zeile 764)
        void Stability(float dt, float g)
        {
            if (Config == null) return;

            // Hier den Winkeldämpfungsfaktor erhöhen für sicheren Modus
            string key = Aids == DrivingAids.Secure ? "SecureAngularDamping" :
                         Aids == DrivingAids.HyperSport || Aids == DrivingAids.Off ?
                         "HyperSportAngularDamping" : "SportAngularDamping";

            // Erhöhen wir den Winkeldämpfungsfaktor für Secure-Modus
            float damping = Config.GetCurve(key, 0.3f).Evaluate(SpeedKmh);
            if (Aids == DrivingAids.Secure)
                damping *= 1.5f; // Stärkere Dämpfung im sicheren Modus

            if (Car != null && Car.IsBike)
                damping = Mathf.Max(damping, Mathf.Lerp(0.8f, 0.95f, Mathf.Clamp01(SpeedKmh / 160f)));

#if UNITY_6000_0_OR_NEWER
            _rb.angularDamping = damping;
#else
    _rb.angularDrag = damping;
#endif

            // Extra-Schwerkraft wenn nicht alle Räder Bodenkontakt haben (hält das Auto am Boden)
            bool allGrounded = true;
            foreach (var w in Wheels) if (!w.Grounded) { allGrounded = false; break; }
            if (!allGrounded) _airTime += dt; else _airTime = 0f;
            float delay = Config.Get("ExtraGravityAccelDelay", 0.01f), dur = Mathf.Max(0.01f, Config.Get("ExtraGravityAccelDuration", 0.1f));
            if (_airTime > delay)
            {
                float t = Mathf.Clamp01((_airTime - delay) / dur);
                float extra = Mathf.Lerp(Config.Get("ExtraGravityMin", 0.1f), Config.Get("ExtraGravityMax", 1f), t);
                _rb.AddForce(Vector3.down * g * extra * _rb.mass);
            }
        }

        /// <summary>
        /// Bikes: Der Rigidbody wird aufrecht gehalten (RÃ¼ckstellmoment + DÃ¤mpfung der Roll-/Nickrate);
        /// die SchrÃ¤glage ist rein visuell und folgt der Querbeschleunigung. So bleibt das Fahrzeug
        /// mit der 2-Rad-Physik beherrschbar, ohne dass es bei Stillstand umkippt.
        /// </summary>
        void BikeBalance(float dt)
        {
            var tr = transform;
            float speedFactor = Mathf.Clamp01(Mathf.Abs(ForwardSpeed) / 12f);
            if (AnyWheelGrounded)
            {
                Vector3 up = tr.up;
                float tilt = Vector3.Angle(up, Vector3.up) * Mathf.Deg2Rad;
                if (tilt > 1e-3f)
                {
                    Vector3 axis = Vector3.Cross(up, Vector3.up);
                    if (axis.sqrMagnitude > 1e-8f)
                    {
                        float strength = Mathf.Lerp(12f, 6f, speedFactor);
                        _rb.AddTorque(axis.normalized * tilt * _rb.mass * strength);
                    }
                }
                Vector3 av = _rb.angularVelocity;
                float rollRate = Vector3.Dot(av, tr.forward);
                float pitchRate = Vector3.Dot(av, tr.right);
                _rb.AddTorque(-tr.forward * rollRate * _rb.mass * 1.8f);
                _rb.AddTorque(-tr.right * pitchRate * _rb.mass * 0.9f);
            }

            float yawRate = Vector3.Dot(_rb.angularVelocity, tr.up);
#if UNITY_6000_0_OR_NEWER
            float vFwd = Vector3.Dot(_rb.linearVelocity, tr.forward);
#else
            float vFwd = Vector3.Dot(_rb.velocity, tr.forward);
#endif
            float latAcc = vFwd * yawRate;
            float maxLean = Car != null && Car.MaxLeanAngle > 0f ? Car.MaxLeanAngle : 45f;
            _leanTargetDeg = Mathf.Clamp(Mathf.Atan2(latAcc, 9.81f) * Mathf.Rad2Deg * 0.75f, -maxLean * 0.8f, maxLean * 0.8f);
            _leanVis = Mathf.MoveTowards(_leanVis, _leanTargetDeg, dt * 100f);
        }

        // ------------------------------------------------------------------ Getriebe (Automatik wie im Spiel / manuell)
        void UpdateGearbox(float dt)
        {
            if (_shiftTimer > 0f)
            {
                _shiftTimer -= dt;
                if (_shiftTimer <= 0f) { int old = Gear; Gear = (int)_pendingGear; GearChanged?.Invoke(old, Gear); }
                return;
            }
            var c = Car;
            float rpm = Rpm;
            bool stopped = Mathf.Abs(ForwardSpeed) < 0.6f;

            // RÃ¼ckwÃ¤rtsgang: Bremse im Stand halten (TDU-Verhalten), Gas im RÃ¼ckwÃ¤rtsgang = Bremse
            if (stopped && BrakePedal > 0.3f && AccelPedal < 0.05f && Gear > 0)
            {
                _reverseTimer += dt;
                if (_reverseTimer > (Config?.Get("ReverseTime", 0.6f) ?? 0.6f)) { Shift(-1, 0f); _reverseTimer = 0f; }
            }
            else if (stopped && AccelPedal > 0.3f && Gear < 0) { Shift(1, 0f); _reverseTimer = 0f; }
            else if (stopped && AccelPedal > 0.3f && Gear == 0 && !Shifting) Shift(1, 0f);
            else _reverseTimer = 0f;

            if (!AutomaticGearbox || Gear <= 0) return;
            if (_shiftCooldown > 0f) _shiftCooldown -= dt;
            float throttle = Mathf.Clamp01(AccelPedal);
            // Hochschalt-Drehzahl: bevorzugt Tacho-Blauwert aus der DB (_blue_line / _blue_line_sport),
            // sonst Nennleistungsdrehzahl; bei wenig Gas frÃ¼her schalten.
            float blue = c.BlueLine > 0f ? Mathf.Lerp(c.BlueLine * 0.55f, c.BlueLine, throttle) : 0f;
            if (blue < c.TorqueRpm) blue = Mathf.Lerp(Mathf.Max(c.TorqueRpm, c.RpmMin * 2.5f), Mathf.Min(c.RedLine, c.PowerRpm * 1.06f) * 0.97f, throttle);
            float upRpm = Mathf.Min(blue, c.RedLine * 0.99f);
            bool drivenSlip = false;
            foreach (var w in Wheels) if (w.Driven && Mathf.Abs(w.SlipRatio) > 0.3f) drivenSlip = true;
            if (Gear < _nbGears && rpm > upRpm && !drivenSlip && ForwardSpeed > 1f)
            {
                Shift(Gear + 1, c.ShiftTimeUp);
                _shiftCooldown = Mathf.Max(0.4f, c.ShiftTimeUp + 0.25f);
                return;
            }
            if (Gear > 1 && _shiftCooldown <= 0f)
            {
                float lowerRatio = _gearRatios[Gear - 2] * c.FinalDrive;
                float rpmLower = rpm * lowerRatio / Mathf.Max(0.01f, CurrentRatio());
                // Runterschalten erst, wenn der Motor wirklich abwÃ¼rgt (deutlich unter der Hochschaltschwelle,
                // Hysterese ~45 %) â€“ verhindert das Hoch/Runter-Pendeln kurz nach dem Hochschalten.
                float downRpm = Mathf.Max(c.RpmMin * 1.2f, upRpm * 0.45f);
                if (rpm < downRpm && rpmLower < c.RedLine * 0.88f)
                {
                    Shift(Gear - 1, c.ShiftTimeDown);
                    _shiftCooldown = Mathf.Max(0.4f, c.ShiftTimeDown + 0.25f);
                }
            }
        }

        public void Shift(int gear, float time)
        {
            gear = Mathf.Clamp(gear, -1, _nbGears);
            if (gear == Gear) return;
            if (time <= 0f) { int old = Gear; Gear = gear; GearChanged?.Invoke(old, gear); return; }
            _pendingGear = gear;
            _shiftTimer = Mathf.Max(0.02f, time);
            int o2 = Gear; Gear = 0;            // wÃ¤hrend des Schaltens Kraftschluss offen
            GearChanged?.Invoke(o2, 0);
            Gear = 0;
        }

        public void ShiftUp() { if (Gear < _nbGears) Shift(Gear + 1, Car.ShiftTimeUp); }
        public void ShiftDown() { if (Gear > -1) Shift(Gear - 1, Car.ShiftTimeDown); }

        /// <summary>Auto aufrichten und anhalten.</summary>
        public void ResetVehicle(Vector3 position, Quaternion rotation)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector3.zero;
#else
            _rb.velocity = Vector3.zero;
#endif
            _rb.angularVelocity = Vector3.zero;
            _rb.position = position; _rb.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            foreach (var w in Wheels) { w.Omega = 0f; w.Compression = 0f; w.PrevCompression = 0f; }
            Gear = 1; _shiftTimer = 0f; _engineW = _idleW; _shiftCooldown = 0f;
        }

        public void SetVelocity(Vector3 v)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = v;
#else
            _rb.velocity = v;
#endif
            foreach (var w in Wheels) w.Omega = Vector3.Dot(v, transform.forward) / Mathf.Max(0.1f, w.Radius);
        }

        // ------------------------------------------------------------------ Visuals (RÃ¤der, Lenkrad)
        void LateUpdate()
        {
            foreach (var w in Wheels)
            {
                if (w.Fork != null) w.Fork.localPosition = w.ForkBasePos + Vector3.up * Mathf.Clamp(w.Compression, -w.Droop, w.TravelUp);
                if (w.Stirrup != null) w.Stirrup.localRotation = w.StirrupBaseRot * Quaternion.AngleAxis(w.SteerAngle * w.SteerSign, Vector3.up);
                if (w.Disc != null) w.Disc.localRotation = w.DiscBaseRot * Quaternion.AngleAxis(w.SpinAngle, Vector3.right);
                if (w.Spin2 != null) w.Spin2.localRotation = w.Spin2BaseRot * Quaternion.AngleAxis(w.SpinAngle, Vector3.right);
            }
            if (SteeringWheel != null) SteeringWheel.localRotation = _steeringWheelBase * Quaternion.AngleAxis(-_steerCurrent * 12f * SteeringWheelSign, Vector3.forward);
            // Bike: Lenker (BARS) dreht mit, Modell lehnt sich in die Kurve
            if (BikeSteerNode != null) BikeSteerNode.localRotation = _bikeSteerBase * Quaternion.AngleAxis(_steerCurrent * BikeSteerSign, Vector3.up);
            if (ModelRoot != null && Car != null && Car.IsBike)
                ModelRoot.localRotation = _modelRootBase * Quaternion.AngleAxis(-_leanVis * LeanSign, Vector3.forward);
        }

        public Transform SteeringWheel;
        public float SteeringWheelSign = 1f;
        Quaternion _steeringWheelBase = Quaternion.identity;

        [Header("Bike (Motorrad)")]
        [Tooltip("Modellwurzel (CarModel.Visual) â€“ bekommt die sichtbare SchrÃ¤glage.")]
        public Transform ModelRoot;
        [Tooltip("Lenker-Knoten (BARS) â€“ dreht mit der Lenkung.")]
        public Transform BikeSteerNode;
        public float BikeSteerSign = 1f;
        [Tooltip("Vorzeichen der sichtbaren SchrÃ¤glage (1 oder -1).")]
        public float LeanSign = 1f;
        Quaternion _bikeSteerBase = Quaternion.identity;
        Quaternion _modelRootBase = Quaternion.identity;
        float _leanVis, _leanTargetDeg;
        /// <summary>
        /// Lenkrichtung der sichtbaren RÃ¤der empirisch prÃ¼fen: Ein positiver Lenkwinkel dreht in der Physik um +Hochachse
        /// (rechts). Gespiegeltes Visual und um 180Â° gedrehte Radknoten kehren die Richtung je nach Rad um â€“ deshalb
        /// wird jedes Rad einmal probeweise eingeschlagen und SteerSign bei Bedarf umgedreht.
        /// </summary>
        void CalibrateSteerSigns()
        {
            var up = transform.up; var fwd = transform.forward;
            foreach (var w in Wheels)
            {
                if (w.Stirrup == null) continue;
                var q0 = w.Stirrup.rotation;
                w.Stirrup.localRotation = w.StirrupBaseRot * Quaternion.AngleAxis(10f * w.SteerSign, Vector3.up);
                var q1 = w.Stirrup.rotation;
                w.Stirrup.localRotation = w.StirrupBaseRot;
                var v = (q1 * Quaternion.Inverse(q0)) * fwd;
                float s = Vector3.SignedAngle(fwd, Vector3.ProjectOnPlane(v, up), up);
                if (s < 0f) w.SteerSign = -w.SteerSign;
            }
        }

        public void CaptureVisualBase()
        {
            foreach (var w in Wheels)
            {
                if (w.Fork != null) w.ForkBasePos = w.Fork.localPosition;
                if (w.Stirrup != null) w.StirrupBaseRot = w.Stirrup.localRotation;
                if (w.Disc != null) w.DiscBaseRot = w.Disc.localRotation;
                if (w.Spin2 != null) w.Spin2BaseRot = w.Spin2.localRotation;
            }
            CalibrateSteerSigns();
            if (SteeringWheel != null) _steeringWheelBase = SteeringWheel.localRotation;
            if (BikeSteerNode != null) { _bikeSteerBase = BikeSteerNode.localRotation; CalibrateBikeSteerSign(); }
            if (ModelRoot != null) _modelRootBase = ModelRoot.localRotation;
        }

        /// <summary>Lenkrichtung des Bikes prÃ¼fen (Lenker dreht das Vorderrad in der Physik nach rechts bei positivem Lenkwinkel).</summary>
        void CalibrateBikeSteerSign()
        {
            Transform wheel = null;
            foreach (var w in Wheels) if (w.Front && (w.Fork != null || w.Disc != null)) { wheel = w.Fork != null ? w.Fork : w.Disc; break; }
            if (wheel == null) return;
            var fwd = transform.forward; var up = transform.up;
            var q0 = wheel.rotation;
            BikeSteerNode.localRotation = _bikeSteerBase * Quaternion.AngleAxis(10f * BikeSteerSign, Vector3.up);
            var q1 = wheel.rotation;
            BikeSteerNode.localRotation = _bikeSteerBase;
            var v = (q1 * Quaternion.Inverse(q0)) * fwd;
            float s = Vector3.SignedAngle(fwd, Vector3.ProjectOnPlane(v, up), up);
            if (s < 0f) BikeSteerSign = -BikeSteerSign;
        }
    }
}
