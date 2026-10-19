using UnityEngine;

namespace TDU2.Map
{
    /// <summary>Einfache Freiflug-Kamera: rechte Maustaste halten + WASD/QE, Shift = schneller, Mausrad = Tempo.</summary>
    public sealed class FlyCamera : MonoBehaviour
    {
        public float Speed = 60f;
        public float FastMultiplier = 8f;
        public float LookSensitivity = 3f;
        float _yaw, _pitch;

        void Start()
        {
            var e = transform.eulerAngles;
            _yaw = e.y; _pitch = e.x;
        }

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            float scroll = Input.mouseScrollDelta.y;
            if (scroll != 0) Speed = Mathf.Clamp(Speed * (scroll > 0 ? 1.25f : 0.8f), 1f, 20000f);

            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * LookSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * LookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
            }
            else
            {
                var e = transform.eulerAngles; _yaw = e.y; _pitch = e.x > 180 ? e.x - 360 : e.x;
            }

            var v = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) v += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) v += Vector3.back;
            if (Input.GetKey(KeyCode.A)) v += Vector3.left;
            if (Input.GetKey(KeyCode.D)) v += Vector3.right;
            if (Input.GetKey(KeyCode.E)) v += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) v += Vector3.down;
            float s = Speed * (Input.GetKey(KeyCode.LeftShift) ? FastMultiplier : 1f);
            transform.Translate(v * s * Time.unscaledDeltaTime, Space.Self);
#endif
        }
    }
}
