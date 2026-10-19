using UnityEngine;

namespace TDU2.Map.World
{
    /// <summary>
    /// Laufen in Interieurs wie im Spiel (Ich-Perspektive, "FirstPerson"-Entity der Szene):
    /// WASD/Pfeile laufen, Shift rennen, Maus umsehen. Steuert die Hauptkamera.
    /// </summary>
    public sealed class TDU2FirstPerson : MonoBehaviour
    {
        public Camera Cam;
        public float EyeHeight = 1.65f;
        public float WalkSpeed = 1.9f, RunSpeed = 4.2f;
        public float MouseSensitivity = 2.2f;
        public bool LookEnabled = true;
        public float FieldOfView = 60f;

        CharacterController _cc;
        float _yaw, _pitch, _vy;
        float _savedFov = -1f, _savedNear = -1f;
        /// <summary>Nahe Clip-Ebene beim Laufen (sonst schneidet die Kamera Wände/Möbel direkt vor dem Gesicht ab).</summary>
        public float NearClip = 0.05f;

        void Awake()
        {
            _cc = gameObject.AddComponent<CharacterController>();
            _cc.height = 1.8f; _cc.radius = 0.28f; _cc.center = new Vector3(0f, 0.9f, 0f);
            _cc.stepOffset = 0.35f; _cc.slopeLimit = 50f; _cc.skinWidth = 0.03f;
        }

        /// <summary>Augenposition + Blickrichtung (Welt).</summary>
        public void Place(Vector3 eye, Vector3 look)
        {
            if (_cc == null) Awake();
            _cc.enabled = false;
            transform.position = eye - Vector3.up * EyeHeight;
            _cc.enabled = true;
            var flat = new Vector3(look.x, 0f, look.z);
            _yaw = flat.sqrMagnitude > 1e-6f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : 0f;
            _pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(look.normalized.y, -1f, 1f)) * Mathf.Rad2Deg, -60f, 60f);
            _vy = 0f;
            ApplyCamera();
        }

        public Ray AimRay => Cam != null ? new Ray(Cam.transform.position, Cam.transform.forward) : new Ray(transform.position, transform.forward);

        void OnEnable()
        {
            if (Cam == null) Cam = Camera.main;
            if (Cam != null && _savedFov < 0f) { _savedFov = Cam.fieldOfView; Cam.fieldOfView = FieldOfView; }
            if (Cam != null && _savedNear < 0f) { _savedNear = Cam.nearClipPlane; Cam.nearClipPlane = Mathf.Min(_savedNear, NearClip); }
        }

        void OnDisable()
        {
            if (Cam != null && _savedFov > 0f) Cam.fieldOfView = _savedFov;
            if (Cam != null && _savedNear > 0f) Cam.nearClipPlane = _savedNear;
            _savedFov = -1f; _savedNear = -1f;
        }

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (LookEnabled)
            {
                _yaw += Input.GetAxis("Mouse X") * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * MouseSensitivity, -75f, 75f);
            }
            float f = 0f, s = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) f += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) f -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) s += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) s -= 1f;
            float speed = Input.GetKey(KeyCode.LeftShift) ? RunSpeed : WalkSpeed;
#else
            float f = 0f, s = 0f, speed = WalkSpeed;
#endif
            if (!LookEnabled) { f = 0f; s = 0f; }
            var rot = Quaternion.Euler(0f, _yaw, 0f);
            var move = rot * new Vector3(s, 0f, f);
            if (move.sqrMagnitude > 1f) move.Normalize();
            if (_cc.isGrounded && _vy < 0f) _vy = -1f;
            _vy += Physics.gravity.y * Time.deltaTime;
            _vy = Mathf.Max(_vy, -30f);
            if (_cc.enabled) _cc.Move((move * speed + Vector3.up * _vy) * Time.deltaTime);
        }

        void LateUpdate() => ApplyCamera();

        void ApplyCamera()
        {
            if (Cam == null) Cam = Camera.main;
            if (Cam == null) return;
            Cam.transform.SetPositionAndRotation(transform.position + Vector3.up * EyeHeight, Quaternion.Euler(_pitch, _yaw, 0f));
        }
    }
}
