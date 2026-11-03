using System.Collections.Generic;
using TDU2.Interior;
using TDU2.Online;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2
{
    /// <summary>
    /// Debug free-fly camera (F4). Wraps the existing <see cref="FlyCamera"/> and temporarily disables the vehicle
    /// camera / first-person controller so the camera can be flown anywhere – for inspecting races, traffic routes
    /// and cutscenes without leaving the car. The previous component states are restored when the tool is closed.
    /// Controls: hold right mouse button to look, WASD move, Q/E down/up, Shift fast, mouse wheel speed, F4 exit.
    /// </summary>
    public sealed class TDU2FlyCameraTool : MonoBehaviour
    {
        public static TDU2FlyCameraTool Instance { get; private set; }

        public KeyCode ToggleKey = KeyCode.F4;
        public float Speed = 80f;

        public bool Active { get; private set; }

        FlyCamera _fly;
        bool _flyWasEnabled;
        bool _flyAdded;
        float _suppressAt;
        bool _blockedByMe;
        CursorLockMode _savedLock;
        bool _savedCursor;
        GUIStyle _style;
        Texture2D _bg;

        readonly Dictionary<Behaviour, bool> _suppressed = new Dictionary<Behaviour, bool>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("TDU2 Fly Camera Tool");
            DontDestroyOnLoad(go);
            go.AddComponent<TDU2FlyCameraTool>();
        }

        public static TDU2FlyCameraTool Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("TDU2 Fly Camera Tool");
            DontDestroyOnLoad(go);
            return go.AddComponent<TDU2FlyCameraTool>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Active) Stop();
            if (_bg != null) Destroy(_bg);
        }

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(ToggleKey) && !TDU2GameData.ChatTyping)
            {
                if (Active) Stop(); else Begin();
            }
#endif
        }

        void LateUpdate()
        {
            if (!Active) return;
            // A car spawn / respawn re-enables the vehicle camera; keep the free camera in charge.
            if (Time.unscaledTime >= _suppressAt)
            {
                _suppressAt = Time.unscaledTime + 0.25f;
                Suppress();
            }
            // Keep the car controls locked even if another owner (dev menu / save screen) releases the block while the
            // free camera is still active.
            if (!TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }
            if (_fly != null && !_fly.enabled) _fly.enabled = true;
        }

        /// <summary>Turn the free camera on. Returns false when there is no main camera.</summary>
        public bool Begin()
        {
            if (Active) return true;
            var cam = Camera.main;
            if (cam == null) return false;

            _savedLock = Cursor.lockState;
            _savedCursor = Cursor.visible;

            _flyAdded = false;
            _fly = cam.GetComponent<FlyCamera>();
            if (_fly == null) { _fly = cam.gameObject.AddComponent<FlyCamera>(); _flyAdded = true; }
            _flyWasEnabled = _fly.enabled;
            _fly.enabled = true;
            if (_fly.Speed < 5f) _fly.Speed = Speed;

            Suppress();

            _blockedByMe = !TDU2Controls.Blocked;
            TDU2Controls.Blocked = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Active = true;
            return true;
        }

        public void Stop()
        {
            if (!Active) return;
            Active = false;

            if (_fly != null && _flyAdded) Destroy(_fly);
            else if (_fly != null) _fly.enabled = _flyWasEnabled;
            _fly = null;

            foreach (var kv in _suppressed)
                if (kv.Key != null) kv.Key.enabled = kv.Value;
            _suppressed.Clear();

            Cursor.lockState = _savedLock;
            Cursor.visible = _savedCursor;
            if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; }
        }

        public void Toggle()
        {
            if (Active) Stop(); else Begin();
        }

        void Suppress()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                foreach (var c in cam.GetComponents<TDU2VehicleCamera>()) Suppress(c);
                foreach (var c in cam.GetComponents<TDU2FirstPerson>()) Suppress(c);
            }
#if UNITY_2023_1_OR_NEWER
            foreach (var c in FindObjectsByType<TDU2VehicleCamera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) Suppress(c);
            foreach (var c in FindObjectsByType<TDU2FirstPerson>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) Suppress(c);
#else
            foreach (var c in FindObjectsOfType<TDU2VehicleCamera>()) Suppress(c);
            foreach (var c in FindObjectsOfType<TDU2FirstPerson>()) Suppress(c);
#endif
        }

        void Suppress(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            if (!_suppressed.ContainsKey(b)) _suppressed[b] = true;
            b.enabled = false;
        }

        void OnGUI()
        {
            if (!Active) return;
            GUI.depth = -700;
            float k = Screen.height / 1080f;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { richText = false, alignment = TextAnchor.MiddleCenter };
                _style.font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 16);
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
                _bg.Apply();
            }
            _style.fontSize = Mathf.Max(10, Mathf.RoundToInt(16f * k));
            _style.wordWrap = true;
            _style.clipping = TextClipping.Overflow;
            const string hint = "FREE CAMERA   ·   RMB look   ·   WASD move   ·   Q/E down/up   ·   Shift fast   ·   Wheel speed   ·   F4 exit";
            float pad = 12f * k;
            float maxW = Mathf.Max(240f * k, Screen.width - 40f * k);
            float natW = _style.CalcSize(new GUIContent(hint)).x;
            float w = Mathf.Min(maxW, natW + pad * 2f);
            float h = _style.CalcHeight(new GUIContent(hint), Mathf.Max(80f, w - pad * 2f)) + 10f * k;
            var r = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 20f * k, w, h);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.8f);
            GUI.DrawTexture(r, _bg);
            GUI.color = old;
            _style.normal.textColor = new Color(0.55f, 1f, 0.55f);
            GUI.Label(new Rect(r.x + pad, r.y + 4f * k, r.width - pad * 2f, r.height - 8f * k), hint, _style);
        }
    }
}
