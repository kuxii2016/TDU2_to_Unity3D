using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Menü-Navigation für Tastatur UND Gamepad/Lenkrad an einer Stelle (alle Flash-Menüs, Karte, Interieurs):
    ///  - Richtungen: Pfeiltasten bzw. Steuerkreuz (Unity-Achsen 6/7, Lenkräder: Hut-Schalter) und beim Pad der linke Stick,
    ///    mit Wiederholung beim Halten (0,4 s Verzögerung, dann alle 0,12 s).
    ///  - Zurück: Esc/Rücktaste bzw. B (Profil MENU_BACK), X/Y: Tasten X/Y bzw. Pad X/Y (MENU_OPTION_1/2),
    ///    LB/RB (MENU_SORT_LEFT/RIGHT), Start (MENU_PAUSE). Bestätigen bleibt TDU2Controls.ConfirmPressed().
    /// </summary>
    public static class TDU2Nav
    {
        static int _frame = -1;
        static int _dx, _dy;
        static float _nextX, _nextY;
        static bool _l, _r, _u, _d;
        static float _lastPad = -100f;

        /// <summary>Wurde zuletzt das Gamepad zum Navigieren benutzt (für Karten-Cursor u.ä.)?</summary>
        public static bool PadActive => Time.unscaledTime - _lastPad < 30f;

        static void Poll()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _l = _r = _u = _d = false;
            float x = 0f, y = 0f;
            if (TDU2InputDevices.HasDevice && !TDU2Controls.Blocked)
            {
                // Steuerkreuz (oben = +) – nur echte Hut-Achsen (Lenkräder: Achse 6/7 sind oft Pedale/Regler mit Ruhe ±1)
                if (!TDU2InputDevices.IsWheel || TDU2InputDevices.IsHatAxis(6)) x = TDU2InputDevices.RawAxis(6);
                if (!TDU2InputDevices.IsWheel || TDU2InputDevices.IsHatAxis(7)) y = TDU2InputDevices.RawAxis(7);
                if (!TDU2InputDevices.IsWheel)
                {
                    float sx = TDU2InputDevices.RawAxis(1), sy = -TDU2InputDevices.RawAxis(2);   // linker Stick (Unity: unten = +)
                    if (Mathf.Abs(sx) > Mathf.Abs(x)) x = sx;
                    if (Mathf.Abs(sy) > Mathf.Abs(y)) y = sy;
                }
            }
            int dx = x > 0.55f ? 1 : x < -0.55f ? -1 : 0;
            int dy = y > 0.55f ? 1 : y < -0.55f ? -1 : 0;
            // nur die stärkere Richtung (Stick schräg -> kein Doppelschritt)
            if (dx != 0 && dy != 0) { if (Mathf.Abs(x) >= Mathf.Abs(y)) dy = 0; else dx = 0; }
            float t = Time.unscaledTime;
            if (dx != 0 && (dx != _dx || t >= _nextX)) { if (dx > 0) _r = true; else _l = true; _nextX = t + (dx != _dx ? 0.4f : 0.12f); _lastPad = t; }
            if (dy != 0 && (dy != _dy || t >= _nextY)) { if (dy > 0) _u = true; else _d = true; _nextY = t + (dy != _dy ? 0.4f : 0.12f); _lastPad = t; }
            _dx = dx; _dy = dy;
        }

        // ---- nur Gerät
        public static bool PadUp() { Poll(); return _u; }
        public static bool PadDown() { Poll(); return _d; }
        public static bool PadLeft() { Poll(); return _l; }
        public static bool PadRight() { Poll(); return _r; }
        public static bool PadUpHeld() { Poll(); return _dy > 0; }
        public static bool PadDownHeld() { Poll(); return _dy < 0; }
        public static bool PadLeftHeld() { Poll(); return _dx < 0; }
        public static bool PadRightHeld() { Poll(); return _dx > 0; }
        static bool Btn(string id) { if (TDU2Controls.Blocked) return false; bool b = TDU2InputDevices.ButtonPressed(id); if (b) _lastPad = Time.unscaledTime; return b; }
        public static bool PadBack() => Btn("back");
        public static bool PadX() => Btn("menu_opt1");
        public static bool PadY() => Btn("menu_opt2");
        public static bool LB() => Btn("menu_sortl");
        public static bool RB() => Btn("menu_sortr");
        public static bool StartPressed() => Btn("pause");

        /// <summary>Linker Stick analog (Pad), x = rechts, y = oben, mit Totzone.</summary>
        public static Vector2 Stick
        {
            get
            {
                if (!TDU2InputDevices.HasDevice || TDU2InputDevices.IsWheel || TDU2Controls.Blocked) return Vector2.zero;
                var v = new Vector2(TDU2InputDevices.RawAxis(1), -TDU2InputDevices.RawAxis(2));
                float m = v.magnitude;
                if (m < 0.2f) return Vector2.zero;
                _lastPad = Time.unscaledTime;
                return v / m * Mathf.Clamp01((m - 0.2f) / 0.8f);
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // ---- Tastatur ODER Gerät (Ersatz für Input.GetKeyDown(KeyCode.UpArrow) usw. in den Menüs)
        public static bool Up() => Input.GetKeyDown(KeyCode.UpArrow) || PadUp();
        public static bool Down() => Input.GetKeyDown(KeyCode.DownArrow) || PadDown();
        public static bool Left() => Input.GetKeyDown(KeyCode.LeftArrow) || PadLeft();
        public static bool Right() => Input.GetKeyDown(KeyCode.RightArrow) || PadRight();
        public static bool UpHeld() => Input.GetKey(KeyCode.UpArrow) || PadUpHeld();
        public static bool DownHeld() => Input.GetKey(KeyCode.DownArrow) || PadDownHeld();
        public static bool LeftHeld() => Input.GetKey(KeyCode.LeftArrow) || PadLeftHeld();
        public static bool RightHeld() => Input.GetKey(KeyCode.RightArrow) || PadRightHeld();
        /// <summary>Zurück: Esc oder Rücktaste oder B.</summary>
        public static bool Back() => Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace) || PadBack();
        public static bool X() => Input.GetKeyDown(KeyCode.X) || PadX();
        public static bool Y() => Input.GetKeyDown(KeyCode.Y) || PadY();
#endif
    }
}
