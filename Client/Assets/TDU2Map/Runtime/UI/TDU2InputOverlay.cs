using System.Globalization;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.UI
{
    /// <summary>
    /// Debug-Anzeige der Eingabe (Debug-Menü → INPUT → "Input overlay"): Lenkung/Gas/Bremse/Kupplung nach Kurve und Verstärkung,
    /// H-Schaltung, die Rohwerte aller Achsen des Hauptgeräts und gedrückte Knöpfe aller Geräte. Nur ein Entwickler-Werkzeug,
    /// erscheint, solange das Debug-Menü zu ist; Ein/Aus bleibt gespeichert (PlayerPrefs).
    /// </summary>
    [AddComponentMenu("TDU2/Debug: Input-Overlay")]
    public sealed class TDU2InputOverlay : MonoBehaviour
    {
        const string PrefKey = "TDU2.Debug.InputOverlay";
        static TDU2InputOverlay _instance;

        public static bool Visible
        {
            get { try { return PlayerPrefs.GetInt(PrefKey, 0) == 1; } catch { return false; } }
            set
            {
                try { PlayerPrefs.SetInt(PrefKey, value ? 1 : 0); PlayerPrefs.Save(); } catch { }
                if (value) Ensure();
            }
        }

        public static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("TDU2 Input Overlay") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<TDU2InputOverlay>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart() { if (Visible) Ensure(); }

        GUIStyle _style;
        Texture2D _white;

        void OnGUI()
        {
            if (!Visible || TDU2DevMenu.IsOpen || Event.current.type != EventType.Repaint) return;
            if (_white == null) { _white = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            float k = Mathf.Max(0.6f, Screen.height / 1080f);
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Clip };
            _style.fontSize = Mathf.RoundToInt(14 * k);
            float lh = _style.fontSize * 1.3f, w = 330f * k, pad = 8f * k, barW = 150f * k, barH = lh * 0.6f;
            int axes = TDU2InputDevices.AxisCount;
            float h = pad * 2 + lh * (7 + axes + 2 + 2);
            var box = new Rect(Screen.width - w - 16f * k, 90f * k, w, h);
            Fill(box, new Color(0f, 0f, 0f, 0.72f));
            float x = box.x + pad, y = box.y + pad, bx = box.xMax - pad - barW;

            string dev = TDU2InputDevices.HasDevice
                ? $"{TDU2InputDevices.DeviceName} (J{TDU2InputDevices.DeviceJoy}) {(TDU2InputDevices.IsWheel ? "Lenkrad" : "Pad")}"
                : "kein Gerät";
            Text(x, y, w - 2 * pad, dev, new Color(1f, 0.85f, 0.3f)); y += lh;

            // Ergebnis (nach Totzone, Kurve, Verstärkung) – das bekommt das Auto
            float steer = TDU2InputDevices.Steer;
            Text(x, y, barW, "Lenkung " + steer.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)); Center(bx, y, barW, barH, steer, new Color(0.4f, 0.8f, 1f)); y += lh;
            Text(x, y, barW, "Gas " + TDU2InputDevices.Throttle.ToString("0.00", CultureInfo.InvariantCulture)); Bar(bx, y, barW, barH, TDU2InputDevices.Throttle, new Color(0.35f, 0.9f, 0.3f)); y += lh;
            Text(x, y, barW, "Bremse " + TDU2InputDevices.Brake.ToString("0.00", CultureInfo.InvariantCulture)); Bar(bx, y, barW, barH, TDU2InputDevices.Brake, new Color(1f, 0.3f, 0.25f)); y += lh;
            Text(x, y, barW, "Kupplung " + TDU2InputDevices.Clutch.ToString("0.00", CultureInfo.InvariantCulture)); Bar(bx, y, barW, barH, TDU2InputDevices.Clutch, new Color(1f, 0.7f, 0.2f)); y += lh;
            int g = TDU2InputDevices.HGear;
            Text(x, y, w - 2 * pad, "H-Gang: " + (g < 0 ? "R" : g == 0 ? "N / –" : g.ToString()) +
                 (TDU2InputDevices.IsWheel ? "   Verstärkung " + TDU2InputDevices.WheelSteerGain.ToString("0.00", CultureInfo.InvariantCulture) : "")); y += lh;

            // Rohachsen des Hauptgeräts (−1..+1)
            Text(x, y, w - 2 * pad, "Achsen (roh) J" + TDU2InputDevices.DeviceJoy, new Color(0.75f, 0.75f, 0.75f)); y += lh;
            for (int i = 1; i <= axes; i++)
            {
                float v = TDU2InputDevices.RawAxis(i);
                Text(x, y, barW, $"Achse {i,2}  {v.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}{(TDU2InputDevices.IsHatAxis(i) ? "  (Hut)" : "")}");
                Center(bx, y, barW, barH, v, Mathf.Abs(v) > 0.05f ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.5f, 0.5f, 0.5f));
                y += lh;
            }

            // gedrückte Knöpfe aller Geräte (Unity: Joystick n Button b)
            var sb = new System.Text.StringBuilder();
#if ENABLE_LEGACY_INPUT_MANAGER
            for (int j = 1; j <= Mathf.Min(8, TDU2InputDevices.JoyCount); j++)
                for (int b = 0; b < 20; b++)
                    if (Input.GetKey(KeyCode.Joystick1Button0 + (j - 1) * 20 + b)) sb.Append("J").Append(j).Append(":").Append(b).Append(' ');
#endif
            Text(x, y, w - 2 * pad, "Knöpfe: " + (sb.Length > 0 ? sb.ToString() : "–"), new Color(0.75f, 0.75f, 0.75f)); y += lh;
            if (TDU2InputDevices.ShifterJoy > 0) Text(x, y, w - 2 * pad, "Schaltung: " + TDU2InputDevices.ShifterName + " (J" + TDU2InputDevices.ShifterJoy + ")", new Color(0.75f, 0.75f, 0.75f));
            y += lh;
            // Vibration (Pad): großer/kleiner Motor, Stoß, Untergrund laut Straßennetz
            Text(x, y, barW, "Vibration tief " + TDU2ForceFeedback.LastPadLow.ToString("0.00", CultureInfo.InvariantCulture)); Bar(bx, y, barW, barH, TDU2ForceFeedback.LastPadLow, new Color(0.8f, 0.5f, 1f)); y += lh;
            Text(x, y, w - 2 * pad, "hoch " + TDU2ForceFeedback.LastPadHigh.ToString("0.00", CultureInfo.InvariantCulture) + "   Stoß " + TDU2ForceFeedback.Impact.ToString("0.00", CultureInfo.InvariantCulture) +
                 "   " + (TDU2ForceFeedback.LastOffPaved ? "Feldweg/Gelände" : "Asphalt"), new Color(0.75f, 0.75f, 0.75f));
        }

        void Text(float x, float y, float w, string s, Color? c = null)
        {
            _style.normal.textColor = c ?? Color.white;
            GUI.Label(new Rect(x, y, w, _style.fontSize * 1.3f), s, _style);
        }

        void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, _white); GUI.color = o; }

        void Bar(float x, float y, float w, float h, float v, Color c)
        {
            float yy = y + h * 0.35f;
            Fill(new Rect(x, yy, w, h), new Color(1f, 1f, 1f, 0.12f));
            Fill(new Rect(x, yy, w * Mathf.Clamp01(v), h), c);
        }

        void Center(float x, float y, float w, float h, float v, Color c)
        {
            float yy = y + h * 0.35f, mid = x + w * 0.5f, len = w * 0.5f * Mathf.Clamp(v, -1f, 1f);
            Fill(new Rect(x, yy, w, h), new Color(1f, 1f, 1f, 0.12f));
            Fill(len >= 0 ? new Rect(mid, yy, len, h) : new Rect(mid + len, yy, -len, h), c);
            Fill(new Rect(mid - 1f, yy - 2f, 2f, h + 4f), new Color(1f, 1f, 1f, 0.6f));
        }
    }
}
