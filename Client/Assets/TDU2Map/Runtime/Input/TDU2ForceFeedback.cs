using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Force Feedback und Vibration:
    ///  - Lenkrad: Logitech Steering Wheel SDK (Assets/Plugins/LogitechSDK/LogitechSteeringWheelEnginesWrapper.dll, x64).
    ///    Rückstellkraft (Feder) abhängig vom Tempo, Lenkmoment aus der Seitenkraft der Vorderräder, Dämpfung, Rüttelstrecke
    ///    auf Erde/Gras, Stöße bei Kollisionen, "in der Luft", Schaltblitz-LEDs (G25/G27/G29 …).
    ///    Voraussetzung: Logitech G HUB bzw. Logitech Gaming Software installiert; das SDK bedient nur Logitech-Lenkräder.
    ///  - Alle anderen Lenkräder (Thrustmaster, Fanatec …): DirectInput-Force-Feedback (<see cref="TDU2DirectInputFfb"/>),
    ///    gleiche Effekte über Konstantkraft/Feder/Dämpfer/Sinus; Treiber des Herstellers muss installiert sein.
    ///  - Gamepad: XInputDotNet (Assets/Plugins/XInputDotNet): linker Motor = Motor/Untergrund/Stöße, rechter Motor = Radschlupf
    ///    und Drehzahlbegrenzer.
    /// Einstellungen aus dem Optionsmenü (Steuerung → Lenkrad): Force Feedback an/aus, Vibration (Stärke), Dämpfung, Tempo-Faktor.
    /// Im Menü/Pause (timeScale 0), ohne Fokus oder ohne Auto wird alles gestoppt.
    /// </summary>
    public sealed class TDU2ForceFeedback : MonoBehaviour
    {
        // ------------------------------------------------------------------ Einstellungen (TDU2Options)
        public static bool Enabled = true;            // "force_feedback"
        public static float Strength = 0.5f;          // "vibration" 0..1
        public static float Damping = 0.5f;           // "damping" 0..1
        [Tooltip("Lenkmoment umkehren, falls das Rad bei Kurvenfahrt in die falsche Richtung zieht")]
        public bool InvertAligningTorque = false;

        static TDU2ForceFeedback _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (_instance != null || !Application.isPlaying) return;
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return;
            var go = new GameObject("TDU2 Force Feedback");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<TDU2ForceFeedback>();
        }

        TDU2VehicleManager _man;
        float _nextFind;
        bool _logiOk, _logiTried, _logiFfb;
        int _padIndex = -1; float _nextPadScan;
        bool _padOk = true;
        float _impact, _impactSide;   // Stoß (0..1) und Richtung (seitlich)
        bool _springOn, _damperOn, _dirtOn, _airOn, _constOn;

        void Update()
        {
            if (_man == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 1f;
#if UNITY_2023_1_OR_NEWER
                _man = FindFirstObjectByType<TDU2VehicleManager>();
#else
                _man = FindObjectOfType<TDU2VehicleManager>();
#endif
            }
            var v = _man != null ? _man.Current : null;
            bool active = Enabled && v != null && Time.timeScale > 0f && Application.isFocused && !TDU2Controls.Blocked && v.Car != null;
            UpdateImpact(v, active);
            UpdateWheel(v, active);
            UpdatePad(v, active);
        }

        // ------------------------------------------------------------------ Untergrund: Asphalt oder Feldweg?
        // Die Räder melden "Erde", sobald sie den Gelände-Kollider treffen – auf dem auch die meisten asphaltierten Straßen liegen.
        // Deshalb zusätzlich das Straßennetz fragen: nahe einer Straße, die kein Feldweg ist (Art 1), gilt der Untergrund als Asphalt.
        [Tooltip("Lenkrad: Rütteln auf Asphalt im Verhältnis zum Feldweg (0 = aus)")]
        public float PavedRumble = 0f;
        [Tooltip("Pad: Drehzahl-Brummen (kleiner Motor) im Leerlauf bzw. an der Drehzahlgrenze")]
        public float RpmVibrationIdle = 0.04f, RpmVibrationRedline = 0.2f;
        [Tooltip("Abstand zur Straßenmitte (m), bis zu dem eine asphaltierte Straße zählt")]
        public float PavedHalfWidth = 9f;
        float _surfAt = -1f; bool _offPaved = true;

        bool OffPaved(TDU2Vehicle v)
        {
            if (Time.unscaledTime < _surfAt) return _offPaved;
            _surfAt = Time.unscaledTime + 0.25f;
            var mm = _man != null ? _man.Minimap : null;
            var root = _man != null && _man.Streamer != null ? _man.Streamer.MapRoot : null;
            if (mm == null || root == null || v == null) { _offPaved = true; return true; }   // ohne Straßennetz wie bisher
            int t = mm.NearestRoadType(root.InverseTransformPoint(v.transform.position), PavedHalfWidth);
            _offPaved = t < 0 || t == 1;
            return _offPaved;
        }

        /// <summary>Diagnose (Input-Overlay): zuletzt gesetzte Pad-Motoren (links = groß/tief, rechts = klein/hoch), Stoß, Untergrund.</summary>
        public static float LastPadLow, LastPadHigh;
        public static bool LastOffPaved;
        public static float Impact => _instance != null ? _instance._impact : 0f;

        // ------------------------------------------------------------------ Stöße (echte Kollisionen)
        // Früher: Geschwindigkeitssprung je Bild – bei hoher Bildrate rüttelte schon das Nachfedern im Stand, und umgefahrene
        // Schilder/Poller (die per Auslöser fallen, bevor das Auto sie berührt) gaben gar nichts. Jetzt zwei echte Quellen:
        //  * Kollisionen des Autos (Wände, Leitplanken, Verkehr): TDU2ImpactListener am Fahrzeug -> OnVehicleCollision
        //  * umgefahrene Welt-Objekte (WorldActorBody.Break) -> ReportHit
        [Tooltip("Aufprallgeschwindigkeit (m/s, senkrecht zur Fläche), ab der eine Kollision spürbar ist")]
        public float MinCollisionSpeed = 0.8f;
        [Tooltip("Abklingen eines Stoßes (pro Sekunde)")]
        public float ImpactDecay = 2.5f;

        void UpdateImpact(TDU2Vehicle v, bool active)
        {
            _impact = Mathf.MoveTowards(_impact, 0f, Time.unscaledDeltaTime * ImpactDecay);
            if (!active) { _impact = 0f; return; }
            var rb = v != null ? v.GetComponent<Rigidbody>() : null;
            if (rb != null && rb.GetComponent<TDU2ImpactListener>() == null) rb.gameObject.AddComponent<TDU2ImpactListener>();
        }

        void AddImpact(float strength, Vector3 dirWorld, TDU2Vehicle v)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= _impact) return;
            _impact = strength;
            dirWorld.y = 0f;
            _impactSide = dirWorld.sqrMagnitude > 1e-6f && v != null ? Vector3.Dot(dirWorld.normalized, v.transform.right) : 0f;
        }

        static TDU2Vehicle CurrentFor(Rigidbody rb)
        {
            if (_instance == null || rb == null || _instance._man == null) return null;
            var v = _instance._man.Current;
            return v != null && v.GetComponent<Rigidbody>() == rb ? v : null;
        }

        /// <summary>Kollision des Spielerautos (vom TDU2ImpactListener).</summary>
        public static void OnVehicleCollision(Rigidbody rb, Collision c)
        {
            var v = CurrentFor(rb);
            if (v == null || c == null || c.contactCount == 0) return;
            var n = c.GetContact(0).normal;
            float vn = Mathf.Abs(Vector3.Dot(c.relativeVelocity, n));
            // Boden (Aufsetzen nach Sprung/Bodenwelle) nur bei hartem Aufschlag
            if (n.y > 0.75f && vn < 4f) return;
            if (vn < _instance.MinCollisionSpeed) return;
            _instance.AddImpact(0.3f + (vn - _instance.MinCollisionSpeed) / 10f, -n, v);
        }

        /// <summary>Welt-Objekt (Schild, Poller, Ampel …) wurde von diesem Körper umgefahren; mass = Masse des Teils.</summary>
        public static void ReportHit(Rigidbody by, float mass, Vector3 hitDirWorld)
        {
            var v = CurrentFor(by);
            if (v == null) return;
            _instance.AddImpact(Mathf.Lerp(0.35f, 0.75f, Mathf.Clamp01(mass / 150f)), hitDirWorld, v);
        }

        // ------------------------------------------------------------------ Lenkrad
        // Logitech-Lenkräder: Logitech-SDK (mit Schaltblitz-LEDs); alle anderen (Thrustmaster, Fanatec, …) und Logitech ohne
        // G HUB: DirectInput-Force-Feedback (TDU2DirectInputFfb, herstellerunabhängig).
        TDU2DirectInputFfb _di;
        bool _diTried; string _diFor;

        static bool IsLogitechName(string n)
        {
            n = (n ?? "").ToLowerInvariant();
            return n.Contains("logitech") || n.Contains("g25") || n.Contains("g27") || n.Contains("g29") || n.Contains("g920") || n.Contains("g923")
                || n.Contains("driving force") || n.Contains("momo");
        }

        void UpdateWheel(TDU2Vehicle v, bool active)
        {
            if (!TDU2InputDevices.IsWheel && !_logiOk && (_di == null || !_di.Ok)) return;
            string dev = TDU2InputDevices.DeviceName;
            if (!_logiTried && IsLogitechName(dev)) TryLogitech();
            bool useLogi = _logiOk && IsLogitechName(dev);
            if (!useLogi && (!_diTried || _diFor != dev))
            {
                _diTried = true; _diFor = dev;
                _di?.Dispose();
                _di = new TDU2DirectInputFfb();
                if (!_di.Open(dev)) { _di.Dispose(); _di = null; }
            }

            // ---- gemeinsame Werte (Prozent wie im Logitech-SDK)
            bool on = active && v != null;
            int springCoef = 0, springSat = 0, constMag = 0, damp = 0, rough = 0; bool air = false;
            if (on)
            {
                float s = Mathf.Clamp01(Strength * 2f);   // Regler 5/10 = volle Original-Stärke
                float kmh = Mathf.Abs(v.SpeedKmh);
                float speedT = Mathf.Clamp01(kmh / Mathf.Lerp(250f, 80f, TDU2InputDevices.SpeedFactor));
                bool grounded = v.AnyWheelGrounded;
                air = !grounded;
                // Rückstellkraft: im Stand weich, mit Tempo fester
                springSat = Mathf.RoundToInt(85f * s);
                springCoef = grounded ? Mathf.RoundToInt(Mathf.Lerp(15f, 75f, speedT) * s) : 5;
                // Lenkmoment aus der Seitenkraft der Vorderräder (wird leicht beim Untersteuern), Stöße obendrauf
                float fy = 0f, fz = 0f;
                foreach (var w in v.Wheels) if (w.Front && w.Grounded) { fy += w.Fy; fz += w.Fz; }
                float mu = fz > 1f ? Mathf.Clamp(fy / fz, -1.5f, 1.5f) : 0f;
                constMag = grounded ? Mathf.RoundToInt(Mathf.Clamp(mu * 45f * s * (InvertAligningTorque ? -1f : 1f), -100f, 100f)) : 0;
                if (_impact > 0.05f) constMag = Mathf.Clamp(constMag + Mathf.RoundToInt(_impactSide * _impact * 100f * s), -100, 100);
                // Dämpfung (Regler), im Stand etwas stärker
                damp = Mathf.RoundToInt(Mathf.Lerp(40f, 15f, speedT) * Damping * 2f * s);
                // Feldweg/Gelände rüttelt; auf Asphalt nur ein kaum spürbares Grundrauschen
                int roughWheels = 0;
                foreach (var w in v.Wheels) if (w.Grounded && w.Surface != 0) roughWheels++;
                if (kmh > 5f)
                {
                    if (roughWheels > 0 && OffPaved(v)) rough = Mathf.RoundToInt(Mathf.Lerp(10f, 40f, speedT) * s);
                    else if (grounded) rough = Mathf.RoundToInt(Mathf.Lerp(0f, PavedRumble * 40f, speedT) * s);
                }
            }

            if (useLogi) ApplyLogitech(v, on, springCoef, springSat, constMag, damp, rough, air);
            else if (_di != null && _di.Ok)
            {
                try
                {
                    if (!on) { _di.Silence(); return; }
                    _di.Spring(springCoef, springSat);
                    _di.Constant(InvertDirectInput ? -constMag : constMag);
                    _di.Damper(damp);
                    _di.Rumble(air ? 0 : rough);
                }
                catch (Exception ex) { Debug.LogWarning("[TDU2] DirectInput Force Feedback abgeschaltet: " + ex.Message); _di.Dispose(); _di = null; }
            }
        }

        [Tooltip("DirectInput (Thrustmaster/Fanatec): Richtung der Konstantkraft umkehren, falls Lenkmoment/Stöße falsch herum ziehen")]
        public bool InvertDirectInput = false;

        void TryLogitech()
        {
            _logiTried = true;
            try
            {
                _logiOk = Logi.LogiSteeringInitialize(true);
                if (_logiOk)
                {
                    Logi.LogiUpdate();
                    var sb = new StringBuilder(256);
                    Logi.LogiGetFriendlyProductName(0, sb, 256);
                    _logiOk = Logi.LogiIsConnected(0);
                    Debug.Log("[TDU2] Logitech-Lenkrad-SDK: " + (_logiOk ? sb.ToString() : "kein Logitech-Lenkrad – nehme DirectInput"));
                }
                else Debug.Log("[TDU2] Logitech-Lenkrad-SDK nicht verfügbar (G HUB?) – nehme DirectInput");
            }
            catch (Exception ex)
            {
                _logiOk = false;
                Debug.Log("[TDU2] Logitech-Lenkrad-SDK nicht ladbar (" + ex.Message + ") – nehme DirectInput");
            }
        }

        void ApplyLogitech(TDU2Vehicle v, bool on, int springCoef, int springSat, int constMag, int damp, int rough, bool air)
        {
            try
            {
                if (!Logi.LogiUpdate() || !Logi.LogiIsConnected(0)) { _logiFfb = false; return; }
                _logiFfb = Logi.LogiHasForceFeedback(0);
                if (!on || !_logiFfb) { StopWheel(); return; }
                Logi.LogiPlaySpringForce(0, 0, springSat, springCoef); _springOn = true;
                if (!air && (constMag != 0 || _constOn)) { Logi.LogiPlayConstantForce(0, constMag); _constOn = true; }
                if (damp > 0) { Logi.LogiPlayDamperForce(0, damp); _damperOn = true; }
                else if (_damperOn) { Logi.LogiStopDamperForce(0); _damperOn = false; }
                if (rough > 0) { Logi.LogiPlayDirtRoadEffect(0, rough); _dirtOn = true; }
                else if (_dirtOn) { Logi.LogiStopDirtRoadEffect(0); _dirtOn = false; }
                if (air) { if (!_airOn) { Logi.LogiPlayCarAirborne(0); _airOn = true; } }
                else if (_airOn) { Logi.LogiStopCarAirborne(0); _airOn = false; }
                if (v.Car != null) Logi.LogiPlayLeds(0, v.Rpm, v.Car.RedLine * 0.75f, v.Car.RedLine);   // Schaltblitz
            }
            catch (Exception ex)
            {
                _logiOk = false;
                Debug.LogWarning("[TDU2] Logitech Force Feedback abgeschaltet: " + ex.Message);
            }
        }

        void StopWheel()
        {
            try { _di?.Silence(); } catch (Exception) { }
            if (!_logiOk) return;
            try
            {
                if (_springOn) { Logi.LogiStopSpringForce(0); _springOn = false; }
                if (_constOn) { Logi.LogiStopConstantForce(0); _constOn = false; }
                if (_damperOn) { Logi.LogiStopDamperForce(0); _damperOn = false; }
                if (_dirtOn) { Logi.LogiStopDirtRoadEffect(0); _dirtOn = false; }
                if (_airOn) { Logi.LogiStopCarAirborne(0); _airOn = false; }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ Gamepad (XInputDotNet)
        void UpdatePad(TDU2Vehicle v, bool active)
        {
            if (!_padOk || TDU2InputDevices.IsWheel) return;
            try
            {
                if (_padIndex < 0 || Time.unscaledTime >= _nextPadScan)
                {
                    _nextPadScan = Time.unscaledTime + 2f;
                    int found = -1;
                    for (int i = 0; i < 4 && found < 0; i++)
                        if (XInputDotNetPure.GamePad.GetState((XInputDotNetPure.PlayerIndex)i).IsConnected) found = i;
                    if (found != _padIndex && _padIndex >= 0) XInputDotNetPure.GamePad.SetVibration((XInputDotNetPure.PlayerIndex)_padIndex, 0f, 0f);
                    _padIndex = found;
                }
                if (_padIndex < 0) return;
                var pi = (XInputDotNetPure.PlayerIndex)_padIndex;
                if (!active || Strength <= 0.001f) { XInputDotNetPure.GamePad.SetVibration(pi, 0f, 0f); return; }

                float s = Mathf.Clamp01(Strength * 2f);
                float kmh = Mathf.Abs(v.SpeedKmh);
                float low = 0f, high = 0f;
                // Klare Regeln:
                //  * Drehzahl: feines Brummen im kleinen Motor (immer, auch im Stand), am Begrenzer pulsierend
                //  * Feldweg/Gelände (laut Straßennetz, nicht Asphalt auf Gelände-Kollider): Rütteln im großen Motor
                //  * Kollision/umgefahrenes Objekt: kurzer kräftiger Stoß in beiden Motoren
                // Asphalt selbst rüttelt nicht.
                float rpmT = v.Car != null && v.Car.RedLine > 0f ? Mathf.Clamp01(v.Rpm / v.Car.RedLine) : 0f;
                if (v.Rpm > 50f) high += Mathf.Lerp(RpmVibrationIdle, RpmVibrationRedline, rpmT * rpmT);
                if (rpmT > 0.97f) high += (Mathf.Repeat(Time.unscaledTime * 12f, 1f) < 0.5f ? 0.25f : 0f);
                int rough = 0;
                foreach (var w in v.Wheels) if (w.Grounded && w.Surface != 0) rough++;
                if (kmh > 3f && rough > 0 && OffPaved(v)) low += Mathf.Lerp(0.12f, 0.4f, Mathf.Clamp01(kmh / 120f));
                low += _impact; high += _impact * 0.5f;
                LastPadLow = Mathf.Clamp01(low * s); LastPadHigh = Mathf.Clamp01(high * s); LastOffPaved = _offPaved;
                XInputDotNetPure.GamePad.SetVibration(pi, LastPadLow, LastPadHigh);
            }
            catch (Exception ex)
            {
                // DLL fehlt (z.B. Nicht-Windows) -> Vibration dauerhaft aus; sonst (Pad kurz weg) später neu versuchen
                if (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException) _padOk = false;
                else { _padIndex = -1; _nextPadScan = Time.unscaledTime + 3f; }
                Debug.LogWarning("[TDU2] XInput-Vibration: " + ex.Message);
            }
        }

        void StopAll()
        {
            StopWheel();
            try { if (_padOk && _padIndex >= 0) XInputDotNetPure.GamePad.SetVibration((XInputDotNetPure.PlayerIndex)_padIndex, 0f, 0f); } catch (Exception) { }
        }

        void OnApplicationFocus(bool focus) { if (!focus) StopAll(); }

        void OnDestroy()
        {
            StopAll();
            if (_logiOk) { try { Logi.LogiSteeringShutdown(); } catch (Exception) { } _logiOk = false; }
            _di?.Dispose(); _di = null;
            if (_instance == this) _instance = null;
        }

        // ------------------------------------------------------------------ Logitech Steering Wheel SDK (nur das Nötige)
        static class Logi
        {
            const string Dll = "LogitechSteeringWheelEnginesWrapper";
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiSteeringInitialize(bool ignoreXInputControllers);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiSteeringShutdown();
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiUpdate();
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiGetFriendlyProductName(int index, StringBuilder buffer, int bufferSize);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiIsConnected(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiHasForceFeedback(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiPlaySpringForce(int index, int offsetPercentage, int saturationPercentage, int coefficientPercentage);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiStopSpringForce(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiPlayConstantForce(int index, int magnitudePercentage);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiStopConstantForce(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiPlayDamperForce(int index, int coefficientPercentage);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiStopDamperForce(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiPlayDirtRoadEffect(int index, int magnitudePercentage);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiStopDirtRoadEffect(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiPlayCarAirborne(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiStopCarAirborne(int index);
            [DllImport(Dll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] public static extern bool LogiPlayLeds(int index, float currentRPM, float rpmFirstLedTurnsOn, float rpmRedLine);
        }
    }
}
