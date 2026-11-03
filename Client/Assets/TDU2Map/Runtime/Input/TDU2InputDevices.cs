using System;
using System.Collections.Generic;
using System.IO;
using TDU2.Formats;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Gamepad- und Lenkradunterstützung (Unity Input Manager, Achsen "TDU2 Joy Axis 1..10" in ProjectSettings/InputManager.asset).
    ///
    /// Erkennung wie im Original: der Name des Geräts (Input.GetJoystickNames) wählt das Geräteprofil aus StreamingAssets
    /// (DeviceLogitechG27.xmb, DeviceX360Pad.xmb, DeviceThrustmasterRGT_FFB_Clutch.xmb …, KeyMap: ActionName -&gt; ChannelName).
    /// Kanäle: STICK_0_LEFT/RIGHT (X), STICK_0_UP/DOWN (Y), STICK_0_FORWARD/BACKWARD (Z), STICK_1_* (Rx/Ry/Rz),
    /// SLIDER_0/1_*, BUTTON_n. Unbekannte Lenkräder bekommen das G27-Profil, unbekannte Pads das Xbox-360-Profil.
    ///
    /// Pedale werden selbst kalibriert (Ruhestellung = Wert beim Erkennen, gedrückt = weitester Ausschlag), Einstellungen aus
    /// dem Optionsmenü (Steuerung → Lenkrad): Totzone, Linearität, Gas/Bremse/Kupplung-Linearität.
    /// </summary>
    public static class TDU2InputDevices
    {
        public enum DeviceMode { Keyboard = 0, Gamepad = 1, Wheel = 2 }

        /// <summary>Gewähltes Gerät (Optionsmenü "WhatDevice"). Tastatur: Gamepad/Lenkrad werden trotzdem gelesen, wenn eines steckt.</summary>
        public static DeviceMode Mode = DeviceMode.Keyboard;
        /// <summary>Einstellungen 0..1 (Optionsmenü, Regler 0..10).</summary>
        public static float DeadZone = 0.05f, SteerLinearity = 0.5f, ThrottleLinearity = 0.5f, BrakeLinearity = 0.5f, ClutchLinearity = 0.5f, SpeedFactor = 0.5f;
        /// <summary>Mausempfindlichkeit-Faktor (Optionsmenü, 1 = Standard) – für Laufmodus/Kameras.</summary>
        public static float MouseSensitivity = 1f;
        public static bool MouseInvert;

        public const int AxisCount = 10;
        /// <summary>Geräte, die einzeln gelesen werden (Input Manager: "TDU2 Joy&lt;j&gt; Axis &lt;i&gt;", joyNum j).</summary>
        public const int JoyCount = 6;

        public static string DeviceName { get; private set; } = "";
        public static string ProfileName { get; private set; } = "";
        public static bool IsWheel { get; private set; }
        public static bool HasDevice => DeviceName.Length > 0;
        /// <summary>Unity-Joystick-Nummer (1-basiert) des Hauptgeräts, 0 = keins.</summary>
        public static int DeviceJoy { get; private set; }
        /// <summary>Getrennte H-Schaltung (z.B. Thrustmaster TH8A, Fanatec ClubSport Shifter), "" = keine.</summary>
        public static string ShifterName { get; private set; } = "";
        public static int ShifterJoy { get; private set; }
        /// <summary>Rohwert einer Unity-Joystickachse 1..10 des Hauptgeräts (−1..+1, 0 ohne Gerät).</summary>
        public static float RawAxis(int i) { Poll(); return HasDevice && _axesOk && i >= 1 && i <= AxisCount ? _axis[DeviceJoy, i] : 0f; }
        /// <summary>
        /// Achse des Hauptgeräts ist ein Hut-Schalter/Steuerkreuz (nur −1/0/+1, ruht bei 0)? Erst nach der ersten Benutzung bekannt.
        /// Lenkräder haben auf den Achsen 6/7 oft Pedale/Regler (Ruhe ±1) – die dürfen die Menü-Navigation nicht "drücken".
        /// </summary>
        public static bool IsHatAxis(int i)
        {
            Poll();
            int j = DeviceJoy;
            return j >= 1 && j <= JoyCount && i >= 1 && i <= AxisCount && _backToZero[j, i] && !_analog[j, i];
        }

        /// <summary>Rohwert einer Achse eines bestimmten Geräts (Joystick 1..6).</summary>
        public static float RawAxis(int joy, int i) { Poll(); return _axesOk && joy >= 1 && joy <= JoyCount && i >= 1 && i <= AxisCount ? _axis[joy, i] : 0f; }

        struct Channel
        {
            public int Joy;         // 0 = Hauptgerät, sonst Unity-Joystick 1..6
            public int Axis;        // 1..10, 0 = Knopf
            public float Sign;      // Richtung des Ausschlags (+1/-1); Pedal: Druckrichtung
            public int Button;      // Knopf-Index am Gerät
            public bool Full;       // Pedal über die ganze Achse (Ruhe am Anschlag -1/+1), sonst 0..1 (Pad-Trigger)
            public bool Valid => Axis > 0 || Button >= 0;
        }

        static readonly Dictionary<string, Channel> _map = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Channel> _override = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        static readonly float[,] _axis = new float[JoyCount + 1, AxisCount + 1];
        static readonly bool[,] _seen = new bool[JoyCount + 1, AxisCount + 1];   // Achse hat schon einen Wert gemeldet
        static readonly bool[,] _backToZero = new bool[JoyCount + 1, AxisCount + 1], _analog = new bool[JoyCount + 1, AxisCount + 1];
        static readonly string[,] _axisNames = BuildAxisNames();
        static string[] _joyNames = new string[0];
        static bool _axesOk = true;
        static int _frame = -1;
        static float _nextScan;
        static readonly Dictionary<string, bool> _prevDown = new Dictionary<string, bool>();
        static readonly Dictionary<string, bool> _curDown = new Dictionary<string, bool>();

        static string[,] BuildAxisNames()
        {
            var n = new string[JoyCount + 1, AxisCount + 1];
            for (int j = 0; j <= JoyCount; j++)
                for (int i = 1; i <= AxisCount; i++) n[j, i] = j == 0 ? "TDU2 Joy Axis " + i : "TDU2 Joy" + j + " Axis " + i;
            return n;
        }

        // Aktionen des Spiels (TDU2Controls-Id) -> Aktionsname im Geräteprofil
        static readonly Dictionary<string, string> ActionOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "handbrake", "CAR_HBRAKE" }, { "gearup", "CAR_GUP" }, { "geardown", "CAR_GDOWN" }, { "camera", "CAR_CAMERA" },
            { "lookback", "CAR_BACKVIEW" }, { "lights", "CAR_LIGHT" }, { "horn", "CAR_HORN" }, { "map", "MAP" }, { "reset", "BACK_ON_TRACK" },
            { "blinkl", "CAR_LEFT_BLINKER" }, { "blinkr", "CAR_RIGHT_BLINKER" }, { "windows", "CAR_WINDOWS" },
            { "gear1", "CAR_G1ST" }, { "gear2", "CAR_G2ND" }, { "gear3", "CAR_G3RD" }, { "gear4", "CAR_G4TH" }, { "gear5", "CAR_G5TH" },
            { "gear6", "CAR_G6TH" }, { "gear7", "CAR_G7TH" }, { "gearR", "CAR_GREV" },
            { "validate", "MENU_VALIDATE" }, { "back", "MENU_BACK" }, { "menu_opt1", "MENU_OPTION_1" }, { "menu_opt2", "MENU_OPTION_2" },
            { "walk_use", "MENU_VALIDATE" }, { "walk_run", "AVT_RISE_ZOOM_IN" },
            { "menu_sortl", "MENU_SORT_LEFT" }, { "menu_sortr", "MENU_SORT_RIGHT" }, { "pause", "MENU_PAUSE" }, { "menu_help", "MENU_HELP" },
            { "map_zoomin", "MAP_CURSOR_ZOOMIN" }, { "map_zoomout", "MAP_CURSOR_ZOOMOUT" }, { "map_gpsdel", "REMOVE_GPS_TARGET" },
        };

        // Analoge Aktionen (Optionsmenü-Belegung) -> Aktion im Profil; nicht in ActionOf, sonst zählte ein halb getretenes Pedal
        // zusätzlich als voll gedrückte Taste
        static readonly Dictionary<string, string> AnalogOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "accel", "CAR_ACCEL" }, { "brake", "CAR_BRAKE" }, { "left", "CAR_LEFT" }, { "right", "CAR_RIGHT" }, { "clutch", "CAR_CLUTCH" },
        };

        /// <summary>Getrennte Schaltungen (eigenes USB-Gerät) – vor den Lenkrad-Profilen prüfen ("Thrustmaster TH8A" …).</summary>
        static readonly string[] ShifterMatch = { "th8", "shifter", "gear shift", "gearshift", "sequential", "sq shifter", "h-shift" };

        // Gerätename -> Profil (wie TDU2: Datei je unterstütztem Gerät)
        static readonly (string match, string file, bool wheel)[] Profiles =
        {
            ("g27", "DeviceLogitechG27", true), ("g25", "DeviceLogitechG25", true), ("driving force gt", "DeviceDrivingForceGT", true),
            ("dfgt", "DeviceDrivingForceGT", true), ("driving force pro", "DeviceDrivingForcePro", true), ("momo", "DeviceMomoForce", true),
            ("911", "DeviceFanatec911TurboS", true), ("fanatec", "DeviceFanatec911TurboS", true), ("f430", "DeviceThrustmasterF430_FFB", true),
            ("rgt", "DeviceThrustmasterRGT_FFB_Clutch", true), ("t500", "DeviceThrustmasterT500RS", true), ("subsonic", "DeviceSubsonicRacinPro", true),
            ("dual action", "DeviceLogitechDualActionUSB", false), ("dual analog", "ThrustmasterDualAnalog3", false),
            // neuere Lenkräder ohne eigenes Profil:
            //  Logitech wie G27 (Gas Y, Bremse Rz, Kupplung Slider 1)
            ("g29", "DeviceLogitechG27", true), ("g920", "DeviceLogitechG27", true), ("g923", "DeviceLogitechG27", true), ("logitech", "DeviceLogitechG27", true),
            //  Thrustmaster (T300/T150/TMX/TX/T248/TS-XW/T-GT, Pedale T3PA/T-LCM): wie T500RS (Gas Rz, Bremse Y, Kupplung Slider 0)
            ("thrustmaster", "DeviceThrustmasterT500RS", true), ("t300", "DeviceThrustmasterT500RS", true), ("t150", "DeviceThrustmasterT500RS", true),
            ("tmx", "DeviceThrustmasterT500RS", true), ("t248", "DeviceThrustmasterT500RS", true), ("ts-xw", "DeviceThrustmasterT500RS", true),
            ("t-gt", "DeviceThrustmasterT500RS", true), ("t818", "DeviceThrustmasterT500RS", true),
            //  Fanatec (CSL/ClubSport/Podium/GT DD): wie 911 Turbo S
            ("csl", "DeviceFanatec911TurboS", true), ("clubsport", "DeviceFanatec911TurboS", true), ("podium", "DeviceFanatec911TurboS", true),
            ("gt dd", "DeviceFanatec911TurboS", true),
            //  alles andere mit "wheel"/"racing" im Namen (Moza, Simucube, …): wie G27
            ("wheel", "DeviceLogitechG27", true), ("racing", "DeviceLogitechG27", true),
            // Pads zuletzt (Lenkräder "für Xbox" sollen oben als Lenkrad erkannt werden)
            ("xbox", "DeviceX360Pad", false), ("xinput", "DeviceX360Pad", false), ("360", "DeviceX360Pad", false),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            DeviceName = ""; ProfileName = ""; IsWheel = false; DeviceJoy = 0; ShifterName = ""; ShifterJoy = 0;
            _map.Clear(); _override.Clear(); _frame = -1; _nextScan = 0f; _axesOk = true; _joyNames = new string[0];
            Array.Clear(_seen, 0, _seen.Length); Array.Clear(_axis, 0, _axis.Length);
            Array.Clear(_backToZero, 0, _backToZero.Length); Array.Clear(_analog, 0, _analog.Length);
        }

        static bool IsShifterName(string low) { foreach (var m in ShifterMatch) if (low.Contains(m)) return true; return false; }

        static (string file, bool wheel) ProfileFor(string low)
        {
            foreach (var p in Profiles) if (low.Contains(p.match)) return (p.file, p.wheel);
            // Ohne Herstellertreiber meldet Windows nur die USB-Produktnummer als Namen (z.B. "B696" = Thrustmaster T248,
            // "C24F" = Logitech G29) – vorher als Gamepad erkannt: Lenkung/Pedale liefen als "linker Stick"/"Steuerkreuz"
            // durch alle Menüs.
            if (IsHexId(low, 'b', '6')) return ("DeviceThrustmasterT500RS", true);   // Thrustmaster 044F:B6xx
            if (IsHexId(low, 'c', '2')) return ("DeviceLogitechG27", true);          // Logitech 046D:C2xx (Lenkräder)
            return (null, false);
        }

        /// <summary>Name ist nur eine vierstellige Produktnummer, die mit den beiden Zeichen beginnt ("b696", "c24f").</summary>
        static bool IsHexId(string low, char a, char b)
        {
            low = low.Trim();
            if (low.Length != 4 || low[0] != a || low[1] != b) return false;
            for (int i = 2; i < 4; i++) if (!Uri.IsHexDigit(low[i])) return false;
            return true;
        }

        // ------------------------------------------------------------------ Erkennen
        /// <summary>
        /// Alle Geräte durchgehen (Input.GetJoystickNames, Index+1 = Unity-Joystick-Nummer). Hauptgerät nach "WhatDevice":
        /// Lenkrad -> erstes Lenkrad, Gamepad -> erstes Pad, Tastatur/Automatik -> Lenkrad, sonst Pad. Eine getrennte H-Schaltung
        /// wird zusätzlich gemerkt. Früher: das erste Gerät mit Namen, und alle Achsen aller Geräte gemischt (joyNum 0) –
        /// mit Pad + Lenkrad + Schaltung sprangen Lenkung/Pedale zwischen den Geräten.
        /// </summary>
        static void Scan()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;
            string[] names = new string[0];
#if ENABLE_LEGACY_INPUT_MANAGER
            try { names = Input.GetJoystickNames() ?? new string[0]; } catch (Exception) { }
#endif
            _joyNames = names;
            int wheelJ = 0, padJ = 0, shiftJ = 0, unknownJ = 0;
            for (int k = 0; k < names.Length && k < JoyCount; k++)
            {
                string n = names[k]?.Trim() ?? "";
                if (n.Length == 0) continue;
                string low = n.ToLowerInvariant();
                if (IsShifterName(low)) { if (shiftJ == 0) shiftJ = k + 1; continue; }
                var (file, wheel) = ProfileFor(low);
                if (wheel) { if (wheelJ == 0) wheelJ = k + 1; }
                else { if (padJ == 0) padJ = k + 1; if (file == null && unknownJ == 0) unknownJ = k + 1; }
            }
            // Unbekanntes Gerät neben einer getrennten H-Schaltung: das ist ein Lenkrad (Pads werden nicht mit Schaltung benutzt)
            bool guessWheel = wheelJ == 0 && shiftJ != 0 && unknownJ != 0;
            if (guessWheel) { wheelJ = unknownJ; if (padJ == unknownJ) padJ = 0; }
            int pick = Mode == DeviceMode.Gamepad ? (padJ != 0 ? padJ : wheelJ) : (wheelJ != 0 ? wheelJ : padJ);
            string name = pick > 0 ? names[pick - 1].Trim() : "";
            ShifterJoy = shiftJ; ShifterName = shiftJ > 0 ? names[shiftJ - 1].Trim() : "";
            if (name == DeviceName && pick == DeviceJoy) return;
            DeviceName = name; DeviceJoy = pick;
            _map.Clear(); _override.Clear();
            if (name.Length == 0) { ProfileName = ""; IsWheel = false; Debug.Log("[TDU2] Eingabegerät getrennt"); return; }
            var pf = ProfileFor(name.ToLowerInvariant());
            if (pf.file == null && guessWheel && pick == wheelJ) pf = ("DeviceThrustmasterT500RS", true);   // Lenkrad ohne bekannten Namen
            string pfile = pf.file ?? "DeviceX360Pad"; bool pwheel = pf.file != null && pf.wheel;
            IsWheel = pwheel;
            ProfileName = pfile;
            if (!LoadProfile(pfile)) DefaultProfile(pwheel);
            if (pwheel)   // Pedale am Lenkrad laufen über die ganze Achse (Ruhe am Anschlag)
                foreach (var act in new[] { "CAR_ACCEL", "CAR_BRAKE", "CAR_CLUTCH" })
                    if (_map.TryGetValue(act, out var c) && c.Axis > 0) { c.Full = true; _map[act] = c; }
            LoadOverrides();
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < names.Length; k++) if (!string.IsNullOrEmpty(names[k])) sb.Append(" [").Append(k + 1).Append("] ").Append(names[k]);
            Debug.Log($"[TDU2] Eingabegerät erkannt: \"{name}\" (Joystick {pick}) -> Profil {pfile} ({(pwheel ? "Lenkrad" : "Gamepad")}, {_map.Count} Belegungen, {_override.Count} eigene)" +
                      (shiftJ > 0 ? $", Schaltung \"{ShifterName}\" (Joystick {shiftJ})" : "") + " – Geräte:" + sb);
        }

        /// <summary>Geräteprofil (XMB aus StreamingAssets) lesen: KeyMap ActionName -&gt; ChannelName.</summary>
        static bool LoadProfile(string file)
        {
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, file + ".xmb");
                if (!File.Exists(path)) return false;
                var root = XmbFile.Parse(File.ReadAllBytes(path));
                var km = root?.Get("KeyMap");
                if (km == null) return false;
                bool xinput = file.IndexOf("360", StringComparison.Ordinal) >= 0 || file.IndexOf("XBOX", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (var it in km.Items)
                {
                    string action = it.S("ActionName", "");
                    string ch = it.S("ChannelName", "");
                    int devType = it.I("DevType", 0);
                    if (action.Length == 0 || ch.Length == 0 || devType == 0) continue;   // DevType 0 = Tastatur
                    var c = ParseChannel(ch, xinput);
                    if (c.Valid && !_map.ContainsKey(action)) _map[action] = c;
                }
                return _map.Count > 0;
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Geräteprofil " + file + ": " + ex.Message); return false; }
        }

        static void DefaultProfile(bool wheel)
        {
            if (wheel)
            {
                _map["CAR_LEFT"] = ParseChannel("STICK_0_LEFT", false); _map["CAR_RIGHT"] = ParseChannel("STICK_0_RIGHT", false);
                _map["CAR_ACCEL"] = ParseChannel("STICK_0_UP", false); _map["CAR_BRAKE"] = ParseChannel("STICK_1_BACKWARD", false);
                _map["CAR_CLUTCH"] = ParseChannel("SLIDER_1_BACKWARD", false);
                _map["CAR_GUP"] = ParseChannel("BUTTON_4", false); _map["CAR_GDOWN"] = ParseChannel("BUTTON_5", false);
            }
            else
            {
                _map["CAR_LEFT"] = ParseChannel("STICK_0_LEFT", true); _map["CAR_RIGHT"] = ParseChannel("STICK_0_RIGHT", true);
                _map["CAR_ACCEL"] = ParseChannel("BUTTON_7", true); _map["CAR_BRAKE"] = ParseChannel("BUTTON_6", true);
                _map["CAR_HBRAKE"] = ParseChannel("BUTTON_2", true); _map["CAR_GUP"] = ParseChannel("BUTTON_3", true);
                _map["CAR_GDOWN"] = ParseChannel("BUTTON_1", true); _map["MENU_VALIDATE"] = ParseChannel("BUTTON_0", true);
                _map["MENU_BACK"] = ParseChannel("BUTTON_1", true);
            }
        }

        /// <summary>
        /// Kanalname des Profils -&gt; Unity-Achse/Knopf. Xbox-Pads (XInput) nummerieren im Original anders als Unity:
        /// BUTTON_6/7 = Trigger links/rechts (Unity Achse 9/10), 8 = Start (7), 9 = Back (6), 10/11 = Sticks (8/9).
        /// </summary>
        static Channel ParseChannel(string ch, bool xinput)
        {
            var c = new Channel { Axis = 0, Sign = 0f, Button = -1 };
            string u = ch.ToUpperInvariant();
            if (u.StartsWith("BUTTON_"))
            {
                if (!int.TryParse(u.Substring(7), out int b)) return c;
                if (xinput)
                {
                    switch (b)
                    {
                        case 6: c.Axis = 9; c.Sign = 1f; return c;    // linker Trigger
                        case 7: c.Axis = 10; c.Sign = 1f; return c;   // rechter Trigger
                        case 8: b = 7; break;
                        case 9: b = 6; break;
                        case 10: b = 8; break;
                        case 11: b = 9; break;
                    }
                }
                c.Button = b;
                return c;
            }
            int baseAxis;
            string rest;
            if (u.StartsWith("STICK_0_")) { baseAxis = 1; rest = u.Substring(8); }
            else if (u.StartsWith("STICK_1_")) { baseAxis = 4; rest = u.Substring(8); }
            else if (u.StartsWith("SLIDER_0_")) { baseAxis = 7; rest = u.Substring(9); }
            else if (u.StartsWith("SLIDER_1_")) { baseAxis = 8; rest = u.Substring(9); }
            else return c;
            bool slider = baseAxis >= 7;
            switch (rest)
            {
                case "LEFT": c.Axis = slider ? baseAxis : baseAxis; c.Sign = -1f; break;
                case "RIGHT": c.Axis = baseAxis; c.Sign = 1f; break;
                case "UP": c.Axis = slider ? baseAxis : baseAxis + 1; c.Sign = -1f; break;     // Unity: oben = negativ
                case "DOWN": c.Axis = slider ? baseAxis : baseAxis + 1; c.Sign = 1f; break;
                case "FORWARD": c.Axis = slider ? baseAxis : baseAxis + 2; c.Sign = -1f; break;
                case "BACKWARD": c.Axis = slider ? baseAxis : baseAxis + 2; c.Sign = 1f; break;
            }
            if (xinput && baseAxis == 1 && (rest == "UP" || rest == "DOWN")) c.Axis = 2;
            if (xinput && baseAxis == 4 && (rest == "LEFT" || rest == "RIGHT")) c.Axis = 4;
            if (xinput && baseAxis == 4 && (rest == "UP" || rest == "DOWN")) c.Axis = 5;
            return c;
        }

        // ------------------------------------------------------------------ Lesen (einmal je Bild)
        static void Poll()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            Scan();
            _prevDown.Clear();
            foreach (var kv in _curDown) _prevDown[kv.Key] = kv.Value;
            _curDown.Clear();
            if (!_axesOk) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            for (int j = 1; j <= JoyCount; j++)
            {
                if (j > _joyNames.Length || string.IsNullOrEmpty(_joyNames[j - 1])) continue;
                for (int i = 1; i <= AxisCount; i++)
                {
                    float v;
                    try { v = Input.GetAxisRaw(_axisNames[j, i]); }
                    catch (Exception)
                    {
                        _axesOk = false;
                        Debug.LogWarning("[TDU2] Achsen \"TDU2 Joy1..6 Axis 1..10\" fehlen im Input Manager (ProjectSettings/InputManager.asset) – Gamepad/Lenkrad nur mit Knöpfen");
                        return;
                    }
                    _axis[j, i] = v;
                    if (v != 0f) _seen[j, i] = true;   // DirectInput meldet eine Achse erst nach der ersten Bewegung (davor 0)
                    else if (_seen[j, i]) _backToZero[j, i] = true;
                    float av = Mathf.Abs(v);
                    if (av > 0.1f && av < 0.9f) _analog[j, i] = true;   // Zwischenwerte: Pedal/Regler, kein Hut-Schalter
                }
            }
#endif
        }

        static int JoyOf(Channel c) => c.Joy > 0 ? c.Joy : DeviceJoy;

        static KeyCode JoyKey(int joy, int button) =>
            joy >= 1 && joy <= 8 && button >= 0 && button < 20 ? KeyCode.Joystick1Button0 + (joy - 1) * 20 + button : KeyCode.None;

        /// <summary>Ausschlag 0..1 eines Kanals in seine Richtung.</summary>
        static float Amount(Channel c)
        {
            int j = JoyOf(c);
            if (j <= 0) return 0f;
            if (c.Axis <= 0)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                var k = JoyKey(j, c.Button);
                return k != KeyCode.None && Input.GetKey(k) ? 1f : 0f;
#else
                return 0f;
#endif
            }
            if (j > JoyCount) return 0f;
            float v = _axis[j, c.Axis];
            if (c.Full)
            {
                // Pedal (−1..+1): Ruhe am Gegenanschlag. Solange die Achse nie gemeldet hat, ist sie 0 = "halb" -> als losgelassen werten
                if (!_seen[j, c.Axis]) return 0f;
                return Mathf.Clamp01((1f + v * c.Sign) * 0.5f);
            }
            return Mathf.Clamp01(v * c.Sign);
        }

        static float Curve(float x, float linearity)
        {
            // Linearität 0.5 = linear, darunter feiner um die Mitte, darüber direkter
            float p = Mathf.Lerp(2.2f, 0.6f, Mathf.Clamp01(linearity));
            return Mathf.Pow(Mathf.Clamp01(x), p);
        }

        /// <summary>Gerät lesen: steckt eines, wird es benutzt (auch bei "Tastatur", dann zusätzlich zur Tastatur).</summary>
        static bool Use => HasDevice;

        /// <summary>Lenkung −1..+1 (0 ohne Gerät).</summary>
        public static float Steer
        {
            get
            {
                Poll();
                if (!Use) return 0f;
                float l = Ch("CAR_LEFT", out var cl) ? Amount(cl) : 0f;
                float r = Ch("CAR_RIGHT", out var cr) ? Amount(cr) : 0f;
                float s = r - l;
                float dz = Mathf.Clamp(DeadZone, 0f, 0.5f);
                float a = Mathf.Abs(s);
                if (a < dz) return 0f;
                a = (a - dz) / (1f - dz);
                // Lenkrad: "Lenkempfindlichkeit" wirkt als Verstärkung über den ganzen Regler (Mitte = 1:1, links = mehr Lenkraddrehung
                // für vollen Einschlag, rechts = direkter). Früher auf >= 0.5 begrenzt und nur als Kurve -> die linke Hälfte tat nichts.
                if (IsWheel) return Mathf.Clamp(Mathf.Sign(s) * a * WheelSteerGain, -1f, 1f);
                return Mathf.Sign(s) * Curve(a, SteerLinearity);
            }
        }

        /// <summary>Verstärkung der Lenkung am Lenkrad aus dem Regler "Lenkempfindlichkeit" (0 -> 0.5, 0.5 -> 1, 1 -> 2.5).</summary>
        public static float WheelSteerGain
        {
            get
            {
                float l = Mathf.Clamp01(SteerLinearity);
                return l <= 0.5f ? Mathf.Lerp(0.5f, 1f, l * 2f) : Mathf.Lerp(1f, 2.5f, (l - 0.5f) * 2f);
            }
        }

        public static float Throttle { get { Poll(); return Use && Ch("CAR_ACCEL", out var c) ? Curve(Pedal(Amount(c)), ThrottleLinearity) : 0f; } }
        public static float Brake { get { Poll(); return Use && Ch("CAR_BRAKE", out var c) ? Curve(Pedal(Amount(c)), BrakeLinearity) : 0f; } }
        public static float Clutch { get { Poll(); return Use && Ch("CAR_CLUTCH", out var c) ? Curve(Pedal(Amount(c)), ClutchLinearity) : 0f; } }
        public static bool HasClutchPedal { get { Poll(); return Use && Ch("CAR_CLUTCH", out var c) && c.Axis > 0; } }

        /// <summary>Kanal einer Profil-Aktion: eigene Belegung (Optionsmenü) vor dem Geräteprofil.</summary>
        static bool Ch(string action, out Channel c) => _override.TryGetValue(action, out c) || _map.TryGetValue(action, out c);

        static float Pedal(float x) => x < 0.03f ? 0f : x;

        /// <summary>
        /// Umsehen mit dem rechten Stick (Gamepad): x = rechts, y = oben, −1..+1, mit Totzone. Lenkräder: null.
        /// XInput-Pads in Unity: rechter Stick = 4./5. Achse (oben negativ).
        /// </summary>
        public static Vector2 Look
        {
            get
            {
                Poll();
                if (!Use || IsWheel || !_axesOk || DeviceJoy <= 0 || DeviceJoy > JoyCount) return Vector2.zero;
                var v = new Vector2(_axis[DeviceJoy, 4], -_axis[DeviceJoy, 5]);
                float m = v.magnitude;
                const float dz = 0.2f;
                if (m < dz) return Vector2.zero;
                return v / m * Mathf.Clamp01((m - dz) / (1f - dz));
            }
        }

        /// <summary>Laufen (Avatar) mit dem Gerät: x = rechts, y = vorwärts (Profil AVT_MOVE_*, sonst linker Stick).</summary>
        public static Vector2 Move
        {
            get
            {
                Poll();
                if (!Use) return Vector2.zero;
                float A(string act, string fallback)
                {
                    if (_map.TryGetValue(act, out var c)) return Amount(c);
                    return Amount(ParseChannel(fallback, ProfileName.IndexOf("360", StringComparison.Ordinal) >= 0));
                }
                var v = new Vector2(A("AVT_MOVE_RIGHT", "STICK_0_RIGHT") - A("AVT_MOVE_LEFT", "STICK_0_LEFT"),
                                    A("AVT_MOVE_FORWARD", "STICK_0_UP") - A("AVT_MOVE_BACKWARD", "STICK_0_DOWN"));
                float dz = Mathf.Max(0.15f, DeadZone);
                return v.magnitude < dz ? Vector2.zero : v;
            }
        }

        /// <summary>
        /// Gang der H-Schaltung: 1..7, -1 = R, 0 = keiner eingelegt (Neutral), int.MinValue = keine Schaltung.
        /// Reihenfolge: im Optionsmenü auf Joystick-Knöpfe gelegte Gänge (Steuerung → Fahren, "1. Gang" … – z.B. TH8A),
        /// dann das Geräteprofil des Lenkrads, dann eine erkannte getrennte Schaltung mit Standardbelegung (Knopf 1..7 = Gang 1..7, 8 = R).
        /// </summary>
        public static int HGear
        {
            get
            {
                Poll();
                string[] ids = { "gearR", "gear1", "gear2", "gear3", "gear4", "gear5", "gear6", "gear7" };
                bool anyBound = false;
                for (int g = 0; g < ids.Length; g++)
                {
                    if (!TDU2Controls.IsJoystickBound(ids[g])) continue;
                    anyBound = true;
                    if (TDU2Controls.JoystickHeld(ids[g])) return g == 0 ? -1 : g;
                }
                if (anyBound) return 0;
                if (Use)
                {
                    bool any = false;
                    string[] gp = { "CAR_GREV", "CAR_G1ST", "CAR_G2ND", "CAR_G3RD", "CAR_G4TH", "CAR_G5TH", "CAR_G6TH", "CAR_G7TH" };
                    for (int i = 0; i < gp.Length; i++)
                    {
                        if (!_map.TryGetValue(gp[i], out var c)) continue;
                        any = true;
                        if (Amount(c) > 0.5f) return i == 0 ? -1 : i;
                    }
                    if (any) return 0;
                }
#if ENABLE_LEGACY_INPUT_MANAGER
                if (ShifterJoy > 0)
                {
                    for (int g = 1; g <= 7; g++) if (Input.GetKey(JoyKey(ShifterJoy, g - 1))) return g;
                    if (Input.GetKey(JoyKey(ShifterJoy, 7))) return -1;
                    return 0;
                }
#endif
                return int.MinValue;
            }
        }

        /// <summary>Knopf einer Spiel-Aktion (TDU2Controls-Id) laut Geräteprofil gehalten.</summary>
        public static bool ButtonHeld(string id)
        {
            Poll();
            if (!Use || id == null || !ActionOf.TryGetValue(id, out var act) || !_map.TryGetValue(act, out var c)) return false;
            bool down = Amount(c) > 0.5f;
            _curDown[id] = down;
            return down;
        }

        /// <summary>Knopf einer Spiel-Aktion in diesem Bild gedrückt.</summary>
        public static bool ButtonPressed(string id)
        {
            Poll();
            if (!Use || id == null || !ActionOf.TryGetValue(id, out var act) || !_map.TryGetValue(act, out var c)) return false;
            if (c.Axis <= 0)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                var k = JoyKey(JoyOf(c), c.Button);
                return k != KeyCode.None && Input.GetKeyDown(k);
#else
                return false;
#endif
            }
            // Achse als Knopf (Trigger): Flanke über die Bilder
            bool down = Amount(c) > 0.5f;
            bool was = _prevDown.TryGetValue(id, out var w) && w;
            _curDown[id] = down;
            return down && !was;
        }

        // ------------------------------------------------------------------ Eigene Belegung (Optionsmenü → Steuerung → Fahren)
        // Analoge Aktionen (Gas, Bremse, Kupplung, Lenken) lassen sich im Binder per Achse belegen: Pedal treten bzw. Lenkrad
        // drehen. Gespeichert je Hauptgerät (PlayerPrefs), das Zielgerät über seinen Namen (Pedale/Schaltung können eigene
        // USB-Geräte sein). Format: "<Gerätename>|<Achse>|<Richtung>|<voll 0/1>".
        static string PrefKey(string action) => "TDU2.Axis." + DeviceName + "." + action;

        /// <summary>Ist die Aktion (TDU2Controls-Id) analog belegbar (Achse)?</summary>
        public static bool IsAnalogAction(string id) => id != null && AnalogOf.ContainsKey(id);

        static void LoadOverrides()
        {
            _override.Clear();
            foreach (var act in AnalogOf.Values)
            {
                string v;
                try { v = PlayerPrefs.GetString(PrefKey(act), ""); } catch (Exception) { v = ""; }
                if (string.IsNullOrEmpty(v)) continue;
                var parts = v.Split('|');
                if (parts.Length < 4 || !int.TryParse(parts[1], out int ax) || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sg)) continue;
                int joy = 0;
                for (int k = 0; k < _joyNames.Length && k < JoyCount; k++)
                    if (string.Equals((_joyNames[k] ?? "").Trim(), parts[0], StringComparison.OrdinalIgnoreCase)) { joy = k + 1; break; }
                if (joy == 0) continue;   // Gerät gerade nicht angeschlossen
                _override[act] = new Channel { Joy = joy, Axis = ax, Sign = sg >= 0f ? 1f : -1f, Button = -1, Full = parts[3] == "1" };
            }
        }

        /// <summary>Eigene Achsen-Belegungen dieses Geräts löschen (Optionsmenü "Standard").</summary>
        public static void ClearOverrides()
        {
            foreach (var act in AnalogOf.Values) { try { PlayerPrefs.DeleteKey(PrefKey(act)); } catch (Exception) { } }
            _override.Clear();
            try { PlayerPrefs.Save(); } catch (Exception) { }
        }

        static readonly float[,] _capture = new float[JoyCount + 1, AxisCount + 1];
        static readonly bool[,] _captureSeen = new bool[JoyCount + 1, AxisCount + 1];

        /// <summary>Binder geöffnet: Ausgangswerte aller Achsen merken.</summary>
        public static void BeginAxisCapture()
        {
            Poll();
            for (int j = 1; j <= JoyCount; j++)
                for (int i = 1; i <= AxisCount; i++) { _capture[j, i] = _axis[j, i]; _captureSeen[j, i] = _seen[j, i]; }
        }

        /// <summary>
        /// Binder: hat sich eine Achse deutlich bewegt (Pedal getreten / Lenkrad gedreht)? Dann als Belegung der Aktion speichern.
        /// Ruhestellung: am Anschlag (|Ausgang| &gt; 0,5) oder – bei Lenkrädern, wenn die Achse noch nie gemeldet hat – angenommen.
        /// </summary>
        public static bool CaptureAxis(string id, out string text)
        {
            text = null;
            Poll();
            if (!IsAnalogAction(id) || !HasDevice) return false;
            string act = AnalogOf[id];
            bool pedal = act == "CAR_ACCEL" || act == "CAR_BRAKE" || act == "CAR_CLUTCH";
            int bj = 0, bi = 0; float bd = 0.6f;
            for (int j = 1; j <= JoyCount; j++)
            {
                if (j > _joyNames.Length || string.IsNullOrEmpty(_joyNames[j - 1])) continue;
                for (int i = 1; i <= AxisCount; i++)
                {
                    // Achse meldet zum ersten Mal (DirectInput: erst nach Bewegung): dieser Wert ist der Ausgangspunkt, nicht 0
                    if (!_captureSeen[j, i] && _seen[j, i]) { _capture[j, i] = _axis[j, i]; _captureSeen[j, i] = true; continue; }
                    float d = Mathf.Abs(_axis[j, i] - _capture[j, i]);
                    if (d > bd) { bd = d; bj = j; bi = i; }
                }
            }
            if (bj == 0) return false;
            float start = _capture[bj, bi], now = _axis[bj, bi];
            float sign = now - start >= 0f ? 1f : -1f;
            bool full = pedal && (Mathf.Abs(start) > 0.5f || IsWheel);
            string dev = (_joyNames[bj - 1] ?? "").Trim();
            var ch = new Channel { Joy = bj, Axis = bi, Sign = sign, Button = -1, Full = full };
            _override[act] = ch;
            try
            {
                PlayerPrefs.SetString(PrefKey(act), dev + "|" + bi + "|" + sign.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (full ? "1" : "0"));
                PlayerPrefs.Save();
            }
            catch (Exception) { }
            text = ChannelText(ch);
            Debug.Log("[TDU2] Belegung " + id + " -> " + dev + " " + text + (full ? " (Pedal, ganze Achse)" : ""));
            return true;
        }

        static string ChannelText(Channel c)
        {
            if (!c.Valid) return "";
            string dev = c.Joy > 0 && c.Joy != DeviceJoy ? "J" + c.Joy + " " : "";
            return c.Axis > 0 ? dev + "Achse " + c.Axis + (c.Sign >= 0f ? "+" : "−") : dev + "Knopf " + (c.Button + 1);
        }

        /// <summary>Belegung am Gerät für die Steuerungsliste (z.B. "Achse 3−", "Knopf 6"), "" = keine.</summary>
        public static string BindingText(string id)
        {
            Poll();
            if (!HasDevice || id == null) return "";
            Channel c;
            if (AnalogOf.TryGetValue(id, out var act)) return Ch(act, out c) ? ChannelText(c) : "";
            if (ActionOf.TryGetValue(id, out var a2) && _map.TryGetValue(a2, out c)) return ChannelText(c);
            return "";
        }

        /// <summary>Für Diagnose/Optionen: Text des erkannten Geräts.</summary>
        public static string Describe() => HasDevice ? DeviceName + " (" + ProfileName + ")" + (ShifterJoy > 0 ? " + " + ShifterName : "") : "kein Gamepad/Lenkrad";
    }
}
