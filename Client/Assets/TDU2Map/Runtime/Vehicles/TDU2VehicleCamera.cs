using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Fahrzeugkameras aus camerasingame.xmb (je Auto, Fehlendes aus den Standardsätzen):
    ///   20/21 Verfolger nah/fern: Position/Ziel-Offsets interpolieren zwischen follow_*_offset (bei follow_speed_offset km/h)
    ///          und follow_*_max_offset (bei follow_speed_max_offset), FOV zwischen fov_min und fov_max über fov_speed_min..max.
    ///   22 Stoßstange, 23 Cockpit, 24 Motorhaube: feste Punkte fixed_pos/fixed_targ im Fahrzeugraum.
    ///   4x: zugehörige Rückblick-Kamera (backviewmode).
    /// Spielkoordinaten sind rechtshändig (+X = links) -> X wird gespiegelt.
    /// </summary>
    public sealed class TDU2VehicleCamera : MonoBehaviour
    {
        public TDU2Vehicle Target;
        public CameraDatabase Cameras;
        public int CarId;
        public int Mode = 20;
        public bool LookBack;
        public float FollowStiffness = 6f;

        public bool IsInterior => CurrentDef != null && CurrentDef.IsFixed && (CurrentDef.Mode % 10) == 3;
        public CarCamera CurrentDef { get; private set; }

        Camera _cam;
        float _origNear;
        Vector3 _smoothPos, _velRef;
        Quaternion _smoothYaw = Quaternion.identity;
        bool _init;
        float _orbitYaw, _orbitPitch;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (_cam != null) _origNear = _cam.nearClipPlane;
        }

        void OnEnable() => FloatingOrigin.Shifted += OnShift;
        void OnShift(Vector3 d) { _smoothPos -= d; }

        void OnDisable()
        {
            FloatingOrigin.Shifted -= OnShift;
            if (_cam != null) _cam.nearClipPlane = _origNear > 0 ? _origNear : 1f;
        }

        public void NextMode()
        {
            var order = Cameras != null ? Cameras.SwitchOrder : new[] { 21, 20, 23, 24, 22 };
            int idx = System.Array.IndexOf(order, Mode);
            Mode = order[(idx + 1 + order.Length) % order.Length];
            _init = false;
        }

        [Tooltip("Blickrichtung bleibt nach dem Loslassen der Maustaste stehen (sonst langsam zurück zur Mitte)")]
        public bool KeepLookDirection = false;
        public float MouseSensitivity = 3f;

        /// <summary>Rechte Maustaste halten + Maus bewegen = umsehen (außen: um das Auto kreisen, innen: Kopf drehen).
        /// Mittlere Maustaste = Blick zurücksetzen.</summary>
        [Tooltip("Innenansicht: Maus bewegt den Kopf auch ohne gedrückte Taste (wie im Spiel)")]
        public bool FreeLookInterior = false;
        float _lastLook;

        void UpdateOrbit(float maxYaw, float minPitch, float maxPitch, bool interior)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButtonDown(2)) { _orbitYaw = 0f; _orbitPitch = 0f; }
            float mx = 0f, my = 0f;
            try { mx = Input.GetAxis("Mouse X"); my = Input.GetAxis("Mouse Y"); } catch (System.Exception) { }
            // Tastatur: Numpad 4/6/8/2 bzw. Bild auf/ab umsehen

            if (TDU2Radio.IsOpen)
            {
                return;
            }
            float kx = 0;// (Input.GetKey(KeyCode.Keypad6) ? 1f : 0f) - (Input.GetKey(KeyCode.Keypad4) ? 1f : 0f);
            float ky = 0;// (Input.GetKey(KeyCode.Keypad8) ? 1f : 0f) - (Input.GetKey(KeyCode.Keypad2) ? 1f : 0f);
            bool held = Input.GetMouseButton(1);
            bool free = interior && FreeLookInterior && (Mathf.Abs(mx) > 0.001f || Mathf.Abs(my) > 0.001f);
            if (held || free || kx != 0f || ky != 0f)
            {
                _orbitYaw = Mathf.Clamp(_orbitYaw + mx * MouseSensitivity + kx * 120f * Time.deltaTime, -maxYaw, maxYaw);
                _orbitPitch = Mathf.Clamp(_orbitPitch - my * MouseSensitivity * 0.7f - ky * 80f * Time.deltaTime, minPitch, maxPitch);
                _lastLook = Time.time;
                if (held) return;
            }
            if (Time.time - _lastLook < 1.5f) return;   // kurz halten, dann zurück zur Mitte
#endif
            if (!KeepLookDirection)
            {
                float k = 1f - Mathf.Exp(-2f * Time.deltaTime);
                _orbitYaw = Mathf.Lerp(_orbitYaw, 0f, k); _orbitPitch = Mathf.Lerp(_orbitPitch, 0f, k);
            }
        }

        static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        void LateUpdate()
        {
            if (Target == null) return;
            var def = Cameras?.Get(CarId, Mode);
            if (def != null && LookBack)
            {
                var back = Cameras.Get(CarId, def.BackMode);
                if (back != null) def = back;
            }
            CurrentDef = def;
            var car = Target.transform;
            float speed = Target.SpeedKmh;

            if (def == null)
            {
                // Rückfall: einfache Verfolgerkamera
                var p = car.TransformPoint(new Vector3(0, 2f, -6.5f));
                transform.position = Vector3.Lerp(transform.position, p, Time.deltaTime * 5f);
                transform.rotation = Quaternion.LookRotation(car.TransformPoint(new Vector3(0, 1f, 2f)) - transform.position, Vector3.up);
                return;
            }

            float fovT = Mathf.InverseLerp(def.FovSpeedMin, Mathf.Max(def.FovSpeedMin + 1f, def.FovSpeedMax), speed);
            if (_cam != null)
            {
                _cam.fieldOfView = Mathf.Lerp(def.FovMin, def.FovMax, fovT);
                _cam.nearClipPlane = def.IsFixed ? Mathf.Clamp(def.Near, 0.03f, 0.3f) : Mathf.Max(0.3f, Mathf.Min(_origNear > 0 ? _origNear : 1f, 0.5f));
            }

            if (def.IsFixed)
            {
                Vector3 pos = car.TransformPoint(Mirror(def.FixedPos));
                Vector3 tgt = car.TransformPoint(Mirror(def.FixedTarget));
                transform.position = pos;
                var baseRot = Quaternion.LookRotation(tgt - pos, car.up);
                // Umsehen (rechte Maustaste / Pfeiltasten nicht belegt): Kopf drehen, Loslassen -> langsam zurück
                UpdateOrbit(170f, -60f, 70f, true);
                transform.rotation = baseRot * Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
                _init = false;
                return;
            }

            // Verfolger
            float t = Mathf.InverseLerp(def.FollowSpeed, Mathf.Max(def.FollowSpeed + 1f, def.FollowSpeedMax), speed);
            float posZ = Mathf.Lerp(def.FollowPos, def.FollowPosMax, t);
            float posY = Mathf.Lerp(def.FollowPosH, def.FollowPosMaxH, t);
            float tgZ = Mathf.Lerp(def.FollowTarg, def.FollowTargMax, t);
            float tgY = Mathf.Lerp(def.FollowTargH, def.FollowTargMaxH, t);

            // Gierwinkel des Autos verzögert folgen (Kamera schwingt in Kurven leicht aus)
            var flatFwd = Vector3.ProjectOnPlane(car.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-4f) flatFwd = transform.forward;
            var yaw = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
            if (!_init) { _smoothYaw = yaw; }
            _smoothYaw = Quaternion.Slerp(_smoothYaw, yaw, 1f - Mathf.Exp(-FollowStiffness * Time.deltaTime));

            UpdateOrbit(180f, -20f, 60f, false);
            var rot = _smoothYaw * Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            Vector3 carPos = car.position;
            Vector3 desired = carPos + rot * new Vector3(0f, posY, posZ);
            Vector3 target = carPos + _smoothYaw * new Vector3(0f, tgY, tgZ);
            // Höhe weich nachführen (Sprünge/Kuppen), horizontal starr am verzögerten Gierwinkel
            if (!_init) { _smoothPos = desired; _init = true; }
            _smoothPos = new Vector3(desired.x, Mathf.SmoothDamp(_smoothPos.y, desired.y, ref _velRef.y, 0.12f), desired.z);
            transform.position = _smoothPos;
            transform.rotation = Quaternion.LookRotation(target - _smoothPos, Vector3.up);
        }
    }
}
