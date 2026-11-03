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
        /// <summary>Gruppe der Belegung – wie die drei Listen des Original-Menüs (_root.WhatListe: driving / walkmode / menu).</summary>
        public enum Category { Driving = 0, Walk = 1, Menu = 2 }

        public sealed class Action
        {
            public string Id, Name, NameEn;
            public KeyCode Default1, Default2;
            public KeyCode Key1, Key2;
            public Category Cat;
        }

        //A("accessories", "Zubehör (Verdeck/Fenster/Blinker)", "Accessories", KeyCode.V, KeyCode.None), Bugged Menue for now no option
        public static readonly List<Action> All = new List<Action>
        {
            // ---- Fahren (_root.WhatListe "driving")
            A("accel",      "Gas",                        "Accelerate",           KeyCode.W, KeyCode.UpArrow),
            A("brake",      "Bremse / Rückwärts",         "Brake / Reverse",      KeyCode.S, KeyCode.DownArrow),
            A("left",       "Lenken links",               "Steer left",           KeyCode.A, KeyCode.LeftArrow),
            A("right",      "Lenken rechts",              "Steer right",          KeyCode.D, KeyCode.RightArrow),
            A("handbrake",  "Handbremse",                 "Handbrake",            KeyCode.Space, KeyCode.None),
            A("clutch",     "Kupplung",                   "Clutch",               KeyCode.LeftAlt, KeyCode.None),
            A("gearup",     "Hochschalten",               "Gear up",              KeyCode.E, KeyCode.LeftShift),
            A("geardown",   "Runterschalten",             "Gear down",            KeyCode.Q, KeyCode.LeftControl),
            A("gear1",      "1. Gang (H-Schaltung)",      "1st gear (H-shifter)", KeyCode.Alpha1, KeyCode.None),
            A("gear2",      "2. Gang (H-Schaltung)",      "2nd gear (H-shifter)", KeyCode.Alpha2, KeyCode.None),
            A("gear3",      "3. Gang (H-Schaltung)",      "3rd gear (H-shifter)", KeyCode.Alpha3, KeyCode.None),
            A("gear4",      "4. Gang (H-Schaltung)",      "4th gear (H-shifter)", KeyCode.Alpha4, KeyCode.None),
            A("gear5",      "5. Gang (H-Schaltung)",      "5th gear (H-shifter)", KeyCode.Alpha5, KeyCode.None),
            A("gear6",      "6. Gang (H-Schaltung)",      "6th gear (H-shifter)", KeyCode.Alpha6, KeyCode.None),
            A("gear7",      "7. Gang (H-Schaltung)",      "7th gear (H-shifter)", KeyCode.Alpha7, KeyCode.None),
            A("gearR",      "Rückwärtsgang (H-Schaltung)", "Reverse (H-shifter)", KeyCode.Alpha0, KeyCode.None),
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
            A("reset",      "Zurück auf die Straße",      "Back on track",        KeyCode.R, KeyCode.None),
            A("gearbox",    "Automatik / Manuell",        "Gearbox mode",         KeyCode.G, KeyCode.None),
            A("aids",       "Fahrhilfen wechseln",        "Driving aids",         KeyCode.U, KeyCode.None),   // T ist der Chat
            A("minimap",    "GPS-Karte ein/aus",          "Minimap on/off",       KeyCode.J, KeyCode.None),
            A("map",        "Karte",                      "Map",                  KeyCode.M, KeyCode.None),
            A("chat",       "Chat",                       "Chat",                 KeyCode.T, KeyCode.None),
            // ---- Laufen / Avatar (_root.WhatListe "walkmode")
            A("walk_forward", "Vorwärts gehen",           "Move forward",         KeyCode.W, KeyCode.UpArrow, Category.Walk),
            A("walk_back",    "Rückwärts gehen",          "Move backward",        KeyCode.S, KeyCode.DownArrow, Category.Walk),
            A("walk_left",    "Nach links gehen",         "Move left",            KeyCode.A, KeyCode.LeftArrow, Category.Walk),
            A("walk_right",   "Nach rechts gehen",        "Move right",           KeyCode.D, KeyCode.RightArrow, Category.Walk),
            A("walk_run",     "Rennen",                   "Run",                  KeyCode.LeftShift, KeyCode.None, Category.Walk),
            A("walk_use",     "Benutzen / Ansprechen",    "Use / Talk",           KeyCode.E, KeyCode.Return, Category.Walk),
            A("walk_turnl",   "Drehen links",             "Turn left",            KeyCode.Q, KeyCode.None, Category.Walk),
            A("walk_turnr",   "Drehen rechts",            "Turn right",           KeyCode.None, KeyCode.None, Category.Walk),
            // ---- Menü (_root.WhatListe "menu"): EINE Bestätigungstaste für alle Menüs/Filme
            A("validate",   "Bestätigen",                 "Validate",             KeyCode.Return, KeyCode.None, Category.Menu),
            A("back",       "Zurück",                     "Back",                 KeyCode.Backspace, KeyCode.None, Category.Menu),
            A("menu_up",    "Menü hoch",                  "Menu up",              KeyCode.UpArrow, KeyCode.W, Category.Menu),
            A("menu_down",  "Menü runter",                "Menu down",            KeyCode.DownArrow, KeyCode.S, Category.Menu),
            A("menu_left",  "Menü links",                 "Menu left",            KeyCode.LeftArrow, KeyCode.A, Category.Menu),
            A("menu_right", "Menü rechts",                "Menu right",           KeyCode.RightArrow, KeyCode.D, Category.Menu),
            A("menu_opt1",  "Option 1",                   "Option 1",             KeyCode.X, KeyCode.None, Category.Menu),
            A("menu_opt2",  "Option 2",                   "Option 2",             KeyCode.Y, KeyCode.None, Category.Menu),
        };

        static Action A(string id, string de, string en, KeyCode k1, KeyCode k2, Category cat = Category.Driving)
            => new Action { Id = id, Name = de, NameEn = en, Default1 = k1, Default2 = k2, Key1 = k1, Key2 = k2, Cat = cat };

        /// <summary>Aktionen einer Gruppe (Reihenfolge wie in <see cref="All"/>).</summary>
        public static List<Action> InCategory(Category c)
        {
            var l = new List<Action>();
            foreach (var a in All) if (a.Cat == c) l.Add(a);
            return l;
        }

        /// <summary>Gruppe zu _root.WhatListe des Films ("driving", "walkmode", "menu").</summary>
        public static Category CategoryOf(string whatListe)
        {
            string w = (whatListe ?? "").Trim().ToLowerInvariant();
            if (w.StartsWith("walk") || w.StartsWith("avat")) return Category.Walk;
            if (w.StartsWith("menu") || w.StartsWith("casino")) return Category.Menu;
            return Category.Driving;
        }

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
                    if (o == a || o.Cat != a.Cat) continue;   // gleiche Taste in anderen Gruppen ist erlaubt (W fährt und geht)
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

        /// <summary>Taste gedrückt gehalten (Tastatur oder Gamepad/Lenkrad-Knopf laut Geräteprofil).</summary>
        public static bool Held(string id)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Blocked) return false;
            var a = Get(id);
            if (a != null && ((a.Key1 != KeyCode.None && Input.GetKey(a.Key1)) || (a.Key2 != KeyCode.None && Input.GetKey(a.Key2)))) return true;
            return TDU2InputDevices.ButtonHeld(id);
#else
            return false;
#endif
        }

        /// <summary>Taste in diesem Bild gedrückt (Tastatur oder Gamepad/Lenkrad-Knopf laut Geräteprofil).</summary>
        public static bool Pressed(string id)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Blocked) return false;
            var a = Get(id);
            if (a != null && ((a.Key1 != KeyCode.None && Input.GetKeyDown(a.Key1)) || (a.Key2 != KeyCode.None && Input.GetKeyDown(a.Key2)))) return true;
            switch (id)   // Menü-Richtungen: Steuerkreuz / linker Stick (TDU2Nav, mit Wiederholung)
            {
                case "menu_up": return TDU2Nav.PadUp();
                case "menu_down": return TDU2Nav.PadDown();
                case "menu_left": return TDU2Nav.PadLeft();
                case "menu_right": return TDU2Nav.PadRight();
            }
            return TDU2InputDevices.ButtonPressed(id);
#else
            return false;
#endif
        }

        /// <summary>
        /// Bestätigen in Menüs/Filmen/Spots – überall dieselbe, im Optionsmenü (Steuerung → Menü) belegbare Taste
        /// (Standard Enter) plus Ziffernblock-Enter und Gamepad A.
        /// </summary>
        public static bool ConfirmPressed()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Blocked) return false;
            return Pressed("validate") || Input.GetKeyDown(KeyCode.KeypadEnter);
#else
            return false;
#endif
        }

        /// <summary>Zurück in Menüs (belegbar, Standard Rücktaste) plus Gamepad B. ESC bleibt das Pausemenü.</summary>
        public static bool BackPressed()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Blocked) return false;
            return Pressed("back");
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
                if (o == a || o.Cat != a.Cat) continue;   // Konflikte nur innerhalb der Gruppe
                if (o.Key1 == key) o.Key1 = KeyCode.None;
                if (o.Key2 == key) o.Key2 = KeyCode.None;
            }
            if (a.Key2 == key) a.Key2 = KeyCode.None;
            a.Key1 = key;
        }

        public static void Unbind(Action a) { if (a != null) { a.Key1 = KeyCode.None; a.Key2 = KeyCode.None; } }

        public static string KeyName(KeyCode k)
        {
            if (IsJoystickKey(k))
            {
                int n = k - KeyCode.Joystick1Button0;
                return "J" + (n / 20 + 1) + " Knopf " + (n % 20 + 1);   // Gerät + Knopf (1-basiert wie in der Windows-Systemsteuerung)
            }
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

        /// <summary>Knopf eines bestimmten Geräts (Joystick1Button0 … Joystick8Button19)?</summary>
        public static bool IsJoystickKey(KeyCode k) => k >= KeyCode.Joystick1Button0 && k <= KeyCode.Joystick8Button19;

        /// <summary>Ist die Aktion auf einen Geräteknopf gelegt (z.B. Gänge auf die TH8A-Schaltung)?</summary>
        public static bool IsJoystickBound(string id)
        {
            var a = Get(id);
            return a != null && (IsJoystickKey(a.Key1) || IsJoystickKey(a.Key2));
        }

        /// <summary>Geräteknopf der Aktion gehalten (H-Schaltung: Gang bleibt nur, solange der Hebel drin ist).</summary>
        public static bool JoystickHeld(string id)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            var a = Get(id);
            if (a == null || Blocked) return false;
            return (IsJoystickKey(a.Key1) && Input.GetKey(a.Key1)) || (IsJoystickKey(a.Key2) && Input.GetKey(a.Key2));
#else
            return false;
#endif
        }

        /// <summary>
        /// Erste in diesem Bild gedrückte Taste (für "Taste zuweisen"); None wenn keine. Geräteknöpfe zählen mit ihrem Gerät
        /// (Joystick&lt;n&gt;Button&lt;m&gt; – Lenkrad, Schaltung, Pad getrennt), nicht als gerätelose "JoystickButton".
        /// </summary>
        public static KeyCode AnyKeyDown()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!Input.anyKeyDown) return KeyCode.None;
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None || (k >= KeyCode.JoystickButton0 && k < KeyCode.Joystick1Button0)) continue;
                if (k == KeyCode.Mouse0) continue;   // Linksklick bedient das Menü
                if (Input.GetKeyDown(k)) return k;
            }
#endif
            return KeyCode.None;
        }
    }
}