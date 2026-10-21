using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace TDU2.Map.Flash
{
    /// <summary>
    /// Korrekturen für die Original-Flash-Oberflächen (falsche Positionen, Größen, Texte), ohne die Filme zu verändern.
    /// Datei: Assets/TDU2Map/Resources/TDU2FlashPatches.txt (im Build über Resources), eine Zeile je Korrektur:
    ///   film | pfad | befehl | werte
    ///   shopsavatar | /menu_item/item_cart/#29 | offset | 4 -2        Versatz in Bühnenpixeln (zusätzlich zur Zeitleiste)
    ///   shopsavatar | /menu_item/item_cart/#29 | pos    | 15 4         feste Position (ersetzt _x/_y der Zeitleiste)
    ///   shopsavatar | /menu_item/item_cart     | scale  | 1.1 1.1      Skalierung (Faktor)
    ///   shopsavatar | /menu_item/item_cart     | alpha  | 0.5          Deckkraft 0..1
    ///   shopsavatar | /menu_item/item_cart     | visible| 0            ausblenden (1 = immer zeigen)
    ///   shopsavatar | /menu_item/#3            | text   | Hallo        eigener Text eines Textfelds
    ///   *           | ...                                              gilt für alle Filme
    /// film = Name der Bank ohne Endung (shopsavatar, home, hud …). Pfade wie im Flash Debugger ("#n" = unbenannt, Tiefe n).
    /// Die Korrekturen werden nach jedem Bild erneut angewendet (auch wenn die Zeitleiste das Objekt neu anlegt).
    /// Zeilen mit # am Anfang sind Kommentare.
    /// </summary>
    public sealed partial class VmfPlayer
    {
        public sealed class Patch
        {
            public string Film, Path, Command, Value;
            public override string ToString() => $"{Film} | {Path} | {Command} | {Value}";
        }

        public const string PatchResource = "TDU2FlashPatches";
        static List<Patch> _allPatches;
        static int _patchVersion;
        List<Patch> _myPatches; int _myPatchVersion = -1;

        /// <summary>Alle Korrekturen (aus Resources/TDU2FlashPatches.txt, im Editor direkt aus der Datei).</summary>
        public static List<Patch> AllPatches
        {
            get
            {
                if (_allPatches == null) ReloadPatches();
                return _allPatches;
            }
        }

        /// <summary>Datei neu einlesen (Flash Debugger nach dem Speichern).</summary>
        public static void ReloadPatches()
        {
            string text = null;
#if UNITY_EDITOR
            try { if (File.Exists(PatchFileEditor)) text = File.ReadAllText(PatchFileEditor); } catch (Exception) { }
#endif
            if (text == null) { var ta = Resources.Load<TextAsset>(PatchResource); text = ta != null ? ta.text : ""; }
            _allPatches = ParsePatches(text);
            _patchVersion++;
        }

        /// <summary>Pfad der Datei im Projekt (nur Editor).</summary>
        public static string PatchFileEditor => System.IO.Path.Combine(Application.dataPath, "TDU2Map", "Resources", PatchResource + ".txt");

        public static List<Patch> ParsePatches(string text)
        {
            var list = new List<Patch>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var p = line.Split(new[] { '|' }, 4);
                if (p.Length < 3) continue;
                list.Add(new Patch { Film = p[0].Trim(), Path = p[1].Trim(), Command = p[2].Trim().ToLowerInvariant(), Value = p.Length > 3 ? p[3].Trim() : "" });
            }
            return list;
        }

        /// <summary>Name des Films für die Korrekturen (Bank ohne Endung, z.B. "shopsavatar").</summary>
        public string FilmName => string.IsNullOrEmpty(BankPath) ? "" : System.IO.Path.GetFileNameWithoutExtension(BankPath);

        List<Patch> MyPatches()
        {
            var all = AllPatches;
            if (_myPatches != null && _myPatchVersion == _patchVersion) return _myPatches;
            _myPatches = new List<Patch>();
            string film = FilmName;
            foreach (var p in all)
                if (p.Film == "*" || string.Equals(p.Film, film, StringComparison.OrdinalIgnoreCase)) _myPatches.Add(p);
            _myPatchVersion = _patchVersion;
            return _myPatches;
        }

        void ApplyPatches()
        {
            var list = MyPatches();
            if (list.Count == 0) return;
            foreach (var p in list)
            {
                var d = Find(p.Path);
                if (d != null) ApplyPatch(d, p.Command, p.Value);
            }
        }

        static float F(string s, float fallback = 0f) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static Vector2 V2(string s, float fallback = 0f)
        {
            var p = (s ?? "").Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            float x = p.Length > 0 ? F(p[0], fallback) : fallback;
            float y = p.Length > 1 ? F(p[1], x) : x;
            return new Vector2(x, y);
        }

        /// <summary>Eine Korrektur auf ein Objekt anwenden (auch direkt aus dem Debugger).</summary>
        public static void ApplyPatch(DisplayObject d, string command, string value)
        {
            if (d == null) return;
            switch ((command ?? "").ToLowerInvariant())
            {
                case "offset": d.Offset = V2(value); break;
                case "pos": d.Position = V2(value); break;
                case "scale": d.ScaleMul = V2(value, 1f); break;
                case "alpha": d.Cx.Mul.w = Mathf.Clamp01(F(value, 1f)); d.ScriptCx = true; break;
                case "visible": d.Visible = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "text": d.TextOverride = (value ?? "").Replace("\\n", "\n"); break;
            }
        }

        // ------------------------------------------------------------------ Code-Helfer (für eigene Steuerung)
        /// <summary>Versatz zusätzlich zur Zeitleiste (Bühnenpixel im Eltern-Clip).</summary>
        public void SetOffset(string path, Vector2 offset) { var d = Find(path); if (d != null) d.Offset = offset; }
        /// <summary>Skalierung (Faktor je Achse) zusätzlich zur Zeitleiste.</summary>
        public void SetScale(string path, Vector2 scale) { var d = Find(path); if (d != null) d.ScaleMul = scale; }
        /// <summary>Deckkraft 0..1 (wie _alpha / 100).</summary>
        public void SetAlpha(string path, float alpha) { var d = Find(path); if (d != null) { d.Cx.Mul.w = Mathf.Clamp01(alpha); d.ScriptCx = true; } }
        /// <summary>Text eines Textfelds direkt setzen (auch ohne Variable); null = wieder Variable/Starttext.</summary>
        public void SetObjectText(string path, string text) { var d = Find(path); if (d != null) d.TextOverride = text; }
    }
}
