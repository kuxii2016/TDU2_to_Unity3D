using System;
using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Tastenbelegung fürs Fahren (je Aktion zwei Tasten), gespeichert in PlayerPrefs "TDU2.Key.&lt;Aktion&gt;.0/1".
    /// Wird vom Fahrzeug-Manager abgefragt und im Optionsmenü "Steuerung" (Flash-Liste controls_list) angezeigt/geändert.
    /// Eigene Aktion: in <see cref="All"/> eintragen, dann TDU2Controls.Held("Name") / Pressed("Name") benutzen.
    /// </summary>
    public static class TDU2Controls
    {
        public sealed class Action
        {
            public string Id, Name, NameEn;
            public KeyCode Default1, Default2;
            public KeyCode Key1, Key2;
        }

        //A("accessories", "Zubehör (Verdeck/Fenster/Blinker)", "Accessories", KeyCode.V, KeyCode.None), Bugged Menue for now no option
        public static readonly List<Action> All = new List<Action>
        {
            A("accel",      "Gas",                        "Accelerate",           KeyCode.W, KeyCode.UpArrow),
            A("brake",      "Bremse / Rückwärts",         "Brake / Reverse",      KeyCode.S, KeyCode.DownArrow),
            A("left",       "Lenken links",               "Steer left",           KeyCode.A, KeyCode.LeftArrow),
            A("right",      "Lenken rechts",              "Steer right",          KeyCode.D, KeyCode.RightArrow),
            A("handbrake",  "Handbremse",                 "Handbrake",            KeyCode.Space, KeyCode.None),
            A("gearup",     "Hochschalten",               "Gear up",              KeyCode.E, KeyCode.LeftShift),
            A("geardown",   "Runterschalten",             "Gear down",            KeyCode.Q, KeyCode.LeftControl),
            A("horn",       "Hupe",                       "Horn",                 KeyCode.H, KeyCode.None),
            A("starter",    "Motor an/aus",               "Engine on/off",        KeyCode.I, KeyCode.None),
            A("doors",      "Türen öffnen/schließen",     "Open/close doors",     KeyCode.K, KeyCode.None),
            A("windows",    "Fenster öffnen/schließen",   "Open/close windows",   KeyCode.N, KeyCode.None),
            A("lookback",   "Nach hinten schauen",        "Look back",            KeyCode.B, KeyCode.None),
            A("camera",     "Kamera wechseln",            "Change camera",        KeyCode.C, KeyCode.None),
            A("lights",     "Licht",                      "Headlights",           KeyCode.L, KeyCode.None),
            A("flash",      "Lichthupe",                  "Flash headlights",     KeyCode.F, KeyCode.None),
            A("blinkl",     "Blinker links",              "Indicator left",       KeyCode.Q, KeyCode.None),
            A("blinkr",     "Blinker rechts",             "Indicator right",      KeyCode.Y, KeyCode.None),   // M ist die Karte
            A("hazard",     "Warnblinker",                "Hazard lights",        KeyCode.X, KeyCode.None),
            A("reset",      "Fahrzeug zurücksetzen",      "Reset vehicle",        KeyCode.R, KeyCode.None),
            A("gearbox",    "Automatik / Manuell",        "Gearbox mode",         KeyCode.G, KeyCode.None),
            A("aids",       "Fahrhilfen wechseln",        "Driving aids",         KeyCode.U, KeyCode.None),   // T ist der Chat
            A("minimap",    "GPS-Karte ein/aus",          "Minimap on/off",       KeyCode.J, KeyCode.None),
            A("map",        "Karte",                      "Map",                  KeyCode.M, KeyCode.None),
            A("chat",       "Chat",                       "Chat",                 KeyCode.T, KeyCode.None),
        };

        static Action A(string id, string de, string en, KeyCode k1, KeyCode k2) => new Action { Id = id, Name = de, NameEn = en, Default1 = k1, Default2 = k2, Key1 = k1, Key2 = k2 };

        static bool _loaded;
        static readonly Dictionary<string, Action> _byId = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Load();
        }

        /// <summary>Gespeicherte Belegung laden (verwirft ungespeicherte Änderungen).</summary>
        public static void Load()
        {
            _byId.Clear();
            foreach (var a in All)
            {
                _byId[a.Id] = a;
                a.Key1 = (KeyCode)PlayerPrefs.GetInt("TDU2.Key." + a.Id + ".0", (int)a.Default1);
                a.Key2 = (KeyCode)PlayerPrefs.GetInt("TDU2.Key." + a.Id + ".1", (int)a.Default2);
            }
            // neue Aktionen ohne gespeicherte Taste: ihre Standardtaste anderen Aktionen wegnehmen (z.B. T: früher Fahrhilfen, jetzt Chat)
            foreach (var a in All)
            {
                if (PlayerPrefs.HasKey("TDU2.Key." + a.Id + ".0") || a.Key1 == KeyCode.None) continue;
                foreach (var o in All)
                {
                    if (o == a) continue;
                    if (o.Key1 == a.Key1) o.Key1 = o.Default1 != a.Key1 ? o.Default1 : KeyCode.None;
                    if (o.Key2 == a.Key1) o.Key2 = KeyCode.None;
                }
            }
            _loaded = true;
        }

        public static void Save()
        {
            foreach (var a in All)
            {
                PlayerPrefs.SetInt("TDU2.Key." + a.Id + ".0", (int)a.Key1);
                PlayerPrefs.SetInt("TDU2.Key." + a.Id + ".1", (int)a.Key2);
            }
            PlayerPrefs.Save();
        }

        public static void ResetDefaults() { foreach (var a in All) { a.Key1 = a.Default1; a.Key2 = a.Default2; } }

        public static Action Get(string id) { EnsureLoaded(); return _byId.TryGetValue(id, out var a) ? a : null; }

        /// <summary>true, solange eine Texteingabe (Chat) die Tastatur hat – dann melden Held/Pressed nichts.</summary>
        public static bool Blocked;

        /// <summary>Taste gedrückt gehalten.</summary>
        public static bool Held(string id)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Blocked) return false;
            var a = Get(id);
            return a != null && ((a.Key1 != KeyCode.None && Input.GetKey(a.Key1)) || (a.Key2 != KeyCode.None && Input.GetKey(a.Key2)));
#else
            return false;
#endif
        }

        /// <summary>Taste in diesem Bild gedrückt.</summary>
        public static bool Pressed(string id)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Blocked) return false;
            var a = Get(id);
            return a != null && ((a.Key1 != KeyCode.None && Input.GetKeyDown(a.Key1)) || (a.Key2 != KeyCode.None && Input.GetKeyDown(a.Key2)));
#else
            return false;
#endif
        }

        /// <summary>Neue Haupttaste setzen; dieselbe Taste wird bei anderen Aktionen entfernt.</summary>
        public static void Bind(Action a, KeyCode key)
        {
            if (a == null) return;
            foreach (var o in All)
            {
                if (o == a) continue;
                if (o.Key1 == key) o.Key1 = KeyCode.None;
                if (o.Key2 == key) o.Key2 = KeyCode.None;
            }
            if (a.Key2 == key) a.Key2 = KeyCode.None;
            a.Key1 = key;
        }

        public static void Unbind(Action a) { if (a != null) { a.Key1 = KeyCode.None; a.Key2 = KeyCode.None; } }

        public static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.None: return "";
                // die Spielschrift hat keine Pfeil-Zeichen
                case KeyCode.UpArrow: return "Up";
                case KeyCode.DownArrow: return "Down";
                case KeyCode.LeftArrow: return "Left";
                case KeyCode.RightArrow: return "Right";
                case KeyCode.Space: return "Space";
                case KeyCode.LeftShift: return "Shift L";
                case KeyCode.RightShift: return "Shift R";
                case KeyCode.LeftControl: return "Strg L";
                case KeyCode.RightControl: return "Strg R";
                case KeyCode.LeftAlt: return "Alt";
                case KeyCode.Return: return "Enter";
                case KeyCode.Backspace: return "Rücktaste";
                case KeyCode.Mouse0: return "Maus L";
                case KeyCode.Mouse1: return "Maus R";
                case KeyCode.Mouse2: return "Maus M";
            }
            var s = k.ToString();
            if (s.StartsWith("Alpha")) return s.Substring(5);
            if (s.StartsWith("Keypad")) return "Num " + s.Substring(6);
            return s;
        }

        public static string BindText(Action a)
        {
            string k1 = KeyName(a.Key1), k2 = KeyName(a.Key2);
            if (k1.Length == 0) return k2.Length == 0 ? "-" : k2;
            return k2.Length == 0 ? k1 : k1 + " / " + k2;
        }

        /// <summary>Erste in diesem Bild gedrückte Taste (für "Taste zuweisen"); None wenn keine.</summary>
        public static KeyCode AnyKeyDown()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!Input.anyKeyDown) return KeyCode.None;
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None || (k >= KeyCode.JoystickButton0 && k <= KeyCode.Joystick8Button19)) continue;
                if (k == KeyCode.Mouse0) continue;   // Linksklick bedient das Menü
                if (Input.GetKeyDown(k)) return k;
            }
#endif
            return KeyCode.None;
        }
    }
}