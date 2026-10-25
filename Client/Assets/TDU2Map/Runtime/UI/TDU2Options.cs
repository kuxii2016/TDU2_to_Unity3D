using System;
using System.Collections.Generic;
using TDU2.Flash;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Spielseite der Original-Optionsmenüs (MAIN.vmf, "options"). Die Flash-Seiten legen ihre Zeilen selbst fest
    /// (setParam: [id, Titel, Typ, _root-Variable, Befehl, Sperre]); das Programm liefert die Werte und reagiert auf Befehle:
    ///
    ///   FSCommand "&lt;präfix&gt;open"        Seite öffnet  -> alle _root-Variablen der Seite setzen (Text bzw. Zahl)
    ///   FSCommand "&lt;befehl&gt;:scpr" / ":scne"  Auswahlfeld links/rechts -> Wert ändern, _root-Variable (Text) sofort neu setzen
    ///   FSCommand "&lt;befehl&gt;:slid:updt"   Schieberegler geändert -> neuer Wert steht bereits in der _root-Variable (0..Max)
    ///   FSCommand "&lt;präfix&gt;vali" / "canc"  Übernehmen / Abbrechen
    ///
    /// Präfixe: opts:vide: (Grafik), opts:audi: (Audio), opts:gase: (Spiel), opts:disp: (Anzeige), opts:lang: (Sprache),
    /// opts:gama: (Helligkeit), opts:cont:sede: / opts:cont:mous: / opts:cont:weel: (Steuerung: Gerät, Maus, Lenkrad).
    ///
    /// Eigene Einträge: <see cref="Choice"/> / <see cref="Slider"/> registrieren (Befehl, Variable, Werte, Anwenden).
    /// Werte werden in PlayerPrefs ("TDU2.Opt.&lt;variable&gt;") gespeichert und beim Start angewendet.
    /// </summary>
    public sealed class TDU2Options
    {
        public sealed class Entry
        {
            public string Cmd;          // vollständiger Befehl, z.B. "opts:vide:prrs"
            public string Var;          // _root-Variable ohne "_root.", z.B. "resolution"
            public bool IsSlider;
            public int Max = 20;        // Schieberegler 0..Max (Flash: initSlider-Maximum)
            public Func<string[]> Options;   // Auswahlfeld: Texte
            public int Value;           // Index bzw. Reglerstand
            public int Default;
            public Action<int> Apply;   // Wert anwenden (Unity-Einstellung)
        }

        readonly Dictionary<string, Entry> _byCmd = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly List<Entry> _all = new List<Entry>();
        readonly Func<string, string, string> _t;   // Text (DBLoc-Schlüssel, Ersatz)
        public TDU2VehicleManager Manager;
        /// <summary>Wird nach einem Sprachwechsel aufgerufen (Texte neu laden).</summary>
        public Action<string> LanguageChanged;

        public static readonly string[] LangCodes = { "ge", "us", "fr", "it", "sp", "ru", "pl", "cz", "ja", "ko", "ch" };
        public static readonly string[] LangNames = { "Deutsch", "English", "Français", "Italiano", "Español", "Русский", "Polski", "Čeština", "日本語", "한국어", "中文" };

        public TDU2Options(Func<string, string, string> text, TDU2VehicleManager manager)
        {
            _t = text; Manager = manager;
            string On = T("m_On", "Ein"), Off = T("m_Off", "Aus");
            string[] OffOn = { Off, On };
            string[] LMH = { T("m_Low", "Niedrig"), T("m_Medium", "Mittel"), T("m_High", "Hoch") };

            // ---------------- Grafik (opts:vide:)
            Choice("opts:vide:prrs", "resolution", () => Resolutions(out _), CurrentResolution(), i =>
            {
                var list = ResolutionList();
                if (i >= 0 && i < list.Count) Screen.SetResolution(list[i].x, list[i].y, Screen.fullScreenMode);
            });
            Choice("opts:vide:prfr", "frequency", () => new[] { "60 Hz", "75 Hz", "100 Hz", "120 Hz", "144 Hz", T("m_Auto", "Auto") }, 5,
                i => Application.targetFrameRate = i >= 5 ? -1 : new[] { 60, 75, 100, 120, 144 }[i]);
            Choice("opts:vide:vsyn", "VSync", () => OffOn, 1, i => QualitySettings.vSyncCount = i);
            Choice("opts:vide:praa", "anti_alias", () => new[] { Off, "2x", "4x", "8x" }, 2, i => QualitySettings.antiAliasing = new[] { 0, 2, 4, 8 }[i]);
            Choice("opts:vide:prlo", "lod", () => LMH, 2, i => { QualitySettings.lodBias = new[] { 0.6f, 1f, 2f }[i]; });
            Choice("opts:vide:caop", "car_opponent", () => LMH, 2, null);
            Choice("opts:vide:txfl", "texture", () => LMH, 2, i => QualitySettings.globalTextureMipmapLimit = 2 - i);
            Choice("opts:vide:fxqu", "fx_qual", () => LMH, 2, i =>
            {
                QualitySettings.shadows = i == 0 ? ShadowQuality.HardOnly : ShadowQuality.All;
                QualitySettings.shadowResolution = i == 0 ? ShadowResolution.Low : i == 1 ? ShadowResolution.Medium : ShadowResolution.VeryHigh;
                QualitySettings.shadowDistance = new[] { 60f, 120f, 250f }[i];
            });
            Slider("opts:gama:gams", "gamma", 18, 9, null);

            // ---------------- Audio (opts:audi:)
            Slider("opts:audi:mavo", "master_volume", 20, 20, v => ApplyVolume());
            Slider("opts:audi:muvo", "music_volume", 20, 14, null);
            Slider("opts:audi:vevo", "vehicules_volume", 20, 20, v => ApplyVolume());
            Slider("opts:audi:vovo", "voices_volume", 20, 16, null);
            Slider("opts:audi:fxvo", "sfx_volume", 20, 18, null);
            Slider("opts:audi:voip", "voiceIP_volume", 20, 16, null);
            Choice("opts:audi:mike", "mike", () => OffOn, 0, null);
            Choice("opts:audi:pkem", "mute_pokemote", () => OffOn, 0, null);
            Choice("opts:audi:sedr", "AudioDriver", () => new[] { "Standard" }, 0, null);

            // ---------------- Spieleinstellungen (opts:gase:) – Fahrhilfen und Getriebe gehen direkt ans Fahrzeug
            Choice("opts:gase:prdh", "DrivingHelp", () => new[] { T("m_Secure", "Fahrhilfe"), T("m_Sport", "Sport"), T("m_Hypersport", "Hypersport"), Off }, 1, i =>
            {
                var aids = (DrivingAids)i;   // Secure, Sport, HyperSport, Off
                if (Manager != null) { Manager.Aids = aids; if (Manager.Current != null) Manager.Current.Aids = aids; }
            });
            Choice("opts:gase:gear", "gearbox", () => new[] { T("m_Automatic", "Automatik"), T("m_Manual", "Manuell") }, 0, i =>
            {
                if (Manager != null) { Manager.AutomaticGearbox = i == 0; if (Manager.Current != null) Manager.Current.AutomaticGearbox = i == 0; }
            });
            Choice("opts:gase:invc", "InvertCam", () => OffOn, 0, null);
            Choice("opts:gase:inax", "InvertYAxis", () => OffOn, 0, null);
            Choice("opts:gase:alch", "AllowChase", () => OffOn, 1, null);
            Choice("opts:gase:valu", "StakeValue", () => new[] { "1 000 $", "5 000 $", "10 000 $", "50 000 $" }, 0, null);

            // ---------------- Anzeige (opts:disp:)
            Choice("opts:disp:usim", "UnitSystem", () => new[] { "Metrisch (km/h)", "Imperial (mph)" }, 0, null);
            Choice("opts:disp:dicp", "DispSpeed", () => OffOn, 1, i => { var hud = Manager != null ? Manager.GetComponent<TDU2VehicleHud>() : null; if (hud != null) hud.ShowHud = i == 1; });
            Choice("opts:disp:gpfi", "GPSStatus", () => OffOn, 1, i => { if (Manager?.Minimap != null) Manager.Minimap.ShowMinimap = i == 1; });
            Choice("opts:disp:frim", "DispFrim", () => OffOn, 1, null);

            // ---------------- Sprache (opts:lang:)
            Choice("opts:lang:prlg", "WhatLanguage", () => LangNames, Array.IndexOf(LangCodes, FlashLocalization.LangCode(Application.systemLanguage)), i =>
            {
                if (i >= 0 && i < LangCodes.Length) LanguageChanged?.Invoke(LangCodes[i]);
            });

            // ---------------- Steuerung (opts:cont:...)
            Choice("opts:cont:sede:whde", "WhatDevice", () => new[] { T("m_Keyboard_M", "Tastatur / Maus"), "Gamepad", T("m_Wheel", "Lenkrad") }, 0, null);
            Slider("opts:cont:mous:mose", "mouse_sensitivity", 100, 50, null);
            Choice("opts:cont:mous:moin", "mouse_invert", () => OffOn, 0, null);
            Slider("opts:cont:weel:vibr", "vibration", 10, 5, null);
            Choice("opts:cont:weel:ffbk", "force_feedback", () => OffOn, 1, null);
            Slider("opts:cont:weel:lisl", "linearity", 10, 5, null);
            Slider("opts:cont:weel:spfa", "speed_dfactor", 10, 5, null);
            Slider("opts:cont:weel:damp", "damping", 10, 5, null);
            Slider("opts:cont:weel:dezo", "dead_zone", 10, 1, null);
            Slider("opts:cont:weel:clut", "clutch", 10, 5, null);
            Slider("opts:cont:weel:thro", "throttle", 10, 5, null);
            Slider("opts:cont:weel:brak", "brake", 10, 5, null);
        }

        string T(string key, string fallback) { var s = _t?.Invoke(key, null); return string.IsNullOrEmpty(s) ? fallback : s; }

        // ------------------------------------------------------------------ Registrierung
        public Entry Choice(string cmd, string var, Func<string[]> options, int def, Action<int> apply)
        {
            var e = new Entry { Cmd = cmd, Var = var, Options = options, Default = Mathf.Max(0, def), Apply = apply };
            e.Value = PlayerPrefs.GetInt("TDU2.Opt." + var, e.Default);
            Register(e);
            return e;
        }

        public Entry Slider(string cmd, string var, int max, int def, Action<int> apply)
        {
            var e = new Entry { Cmd = cmd, Var = var, IsSlider = true, Max = max, Default = def, Apply = apply };
            e.Value = Mathf.Clamp(PlayerPrefs.GetInt("TDU2.Opt." + var, def), 0, max);
            Register(e);
            return e;
        }

        void Register(Entry e) { _byCmd[e.Cmd] = e; _all.Add(e); }

        /// <summary>Gespeicherte Werte beim Start anwenden.</summary>
        public void ApplyAll()
        {
            foreach (var e in _all)
            {
                if (e.Var == "resolution" || e.Var == "WhatLanguage") continue;   // nicht ungefragt umschalten
                try { e.Apply?.Invoke(e.Value); } catch (Exception ex) { Debug.LogWarning("[TDU2] Option " + e.Var + ": " + ex.Message); }
            }
        }

        public Entry Get(string var) { foreach (var e in _all) if (string.Equals(e.Var, var, StringComparison.OrdinalIgnoreCase)) return e; return null; }

        /// <summary>Lautstärke Gesamt x Fahrzeuge (0..20) -> AudioListener.</summary>
        void ApplyVolume()
        {
            var m = Get("master_volume"); var v = Get("vehicules_volume");
            AudioListener.volume = (m != null ? m.Value / 20f : 1f) * (v != null ? v.Value / 20f : 1f);
        }

        // ------------------------------------------------------------------ Werte in den Film schreiben
        public void Publish(VmfPlayer ui, string prefix = null)
        {
            foreach (var e in _all)
            {
                if (prefix != null && !e.Cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.IsSlider) ui.SetValue("/:" + e.Var, e.Value);
                else ui.SetVariable("/:" + e.Var, Text(e));
            }
        }

        string Text(Entry e)
        {
            var o = e.Options?.Invoke();
            if (o == null || o.Length == 0) return "";
            e.Value = Mathf.Clamp(e.Value, 0, o.Length - 1);
            return o[e.Value];
        }

        /// <summary>FSCommand der Optionsseiten verarbeiten; true = erkannt.</summary>
        public bool Handle(VmfPlayer ui, string cmd)
        {
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith("opts:", StringComparison.OrdinalIgnoreCase)) return false;
            if (cmd.EndsWith("open", StringComparison.OrdinalIgnoreCase)) { Publish(ui, cmd.Substring(0, cmd.Length - 4)); return true; }
            if (cmd.EndsWith("vali", StringComparison.OrdinalIgnoreCase) || cmd.EndsWith("canc", StringComparison.OrdinalIgnoreCase)) { PlayerPrefs.Save(); return true; }

            string baseCmd = cmd; int dir = 0; bool slider = false;
            if (cmd.EndsWith(":scne", StringComparison.OrdinalIgnoreCase)) { dir = 1; baseCmd = cmd.Substring(0, cmd.Length - 5); }
            else if (cmd.EndsWith(":scpr", StringComparison.OrdinalIgnoreCase)) { dir = -1; baseCmd = cmd.Substring(0, cmd.Length - 5); }
            else if (cmd.EndsWith(":slid:updt", StringComparison.OrdinalIgnoreCase)) { slider = true; baseCmd = cmd.Substring(0, cmd.Length - 10); }
            if (!_byCmd.TryGetValue(baseCmd, out var e)) return true;   // unbekannte Zeile: ignorieren

            if (slider || e.IsSlider)
            {
                if (slider)
                {
                    var s = ui.GetVariable("/:" + e.Var);
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        e.Value = Mathf.Clamp((int)Math.Round(d), 0, e.Max);
                }
                else e.Value = Mathf.Clamp(e.Value + dir, 0, e.Max);
            }
            else
            {
                int n = e.Options?.Invoke()?.Length ?? 0;
                if (n > 0) e.Value = ((e.Value + dir) % n + n) % n;
                ui.SetVariable("/:" + e.Var, Text(e));   // Auswahlfeld liest den Text direkt danach (updateText)
            }
            PlayerPrefs.SetInt("TDU2.Opt." + e.Var, e.Value);
            try { e.Apply?.Invoke(e.Value); } catch (Exception ex) { Debug.LogWarning("[TDU2] Option " + e.Var + ": " + ex.Message); }
            return true;
        }

        // ------------------------------------------------------------------ Auflösungen
        static List<Vector2Int> ResolutionList()
        {
            var list = new List<Vector2Int>();
            foreach (var r in Screen.resolutions)
            {
                var v = new Vector2Int(r.width, r.height);
                if (!list.Contains(v)) list.Add(v);
            }
            if (list.Count == 0) list.Add(new Vector2Int(Screen.width, Screen.height));
            return list;
        }

        static string[] Resolutions(out List<Vector2Int> list)
        {
            list = ResolutionList();
            var s = new string[list.Count];
            for (int i = 0; i < s.Length; i++) s[i] = list[i].x + " x " + list[i].y;
            return s;
        }

        static int CurrentResolution()
        {
            var list = ResolutionList();
            int i = list.IndexOf(new Vector2Int(Screen.width, Screen.height));
            return i >= 0 ? i : list.Count - 1;
        }
    }
}
