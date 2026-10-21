using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map
{
    /// <summary>
    /// GamePC.cpr (Konfiguration der Original-exe, XTEA wie die übrigen .cpr) – nur die aktiven (nicht auskommentierten) Zeilen.
    /// Für den Spielstart wird ausgewertet (wie das Spiel ohne Online-Spielstand):
    ///   AddLevel = "Ibiza"                 Startinsel (Hawai_Route/Dubai/Casino werden nicht als Start genommen)
    ///   DEFAULTSTARTPOSITION x y z         Startposition (nur falls aktiv und nicht 0 0 0)
    ///   [CarStateConfig] DefaultHouse "EPH_L2E_0345I", DefaultHouse2 …   Haus, in dem man beginnt (je Insel das vorhandene)
    ///   DefaultCarConfig "Ferrari" "F430" "Scuderia"                    aktuelles Auto
    ///   DefaultGarageConfig 379 …          Autos in der Garage (_id_car)
    ///   DefaultMoney 27000                 Startgeld
    /// </summary>
    public sealed class TDU2GamePC
    {
        public sealed class Entry
        {
            public string Section = "", Key = "";
            public string[] Args = Array.Empty<string>();
            public override string ToString() => $"[{Section}] {Key} {string.Join(" ", Args)}";
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public string Path;

        public string StartIsland;              // "ibiza" / "hawai" (Bnk/islands/<name>)
        public bool HasStartPosition;
        public Vector3 StartPosition;
        public readonly List<string> Houses = new List<string>();
        public string[] DefaultCar = Array.Empty<string>();
        public readonly List<int> GarageCars = new List<int>();
        public long DefaultMoney;

        static TDU2GamePC _cached;
        static string _cachedKey;

        /// <summary>GamePC.cpr suchen (StreamingAssets, neben bzw. über dem Bnk-Ordner) und lesen; null = nicht gefunden.</summary>
        public static TDU2GamePC Load(string bnkDir)
        {
            string key = bnkDir ?? "";
            if (_cached != null && _cachedKey == key) return _cached;
            var cands = new List<string>();
            try { cands.Add(Application.streamingAssetsPath); } catch (Exception) { }
            if (!string.IsNullOrEmpty(bnkDir))
            {
                try
                {
                    var d = new DirectoryInfo(bnkDir);
                    for (int i = 0; i < 3 && d != null; i++, d = d.Parent) cands.Add(d.FullName);
                }
                catch (Exception) { }
            }
            foreach (var dir in cands)
            {
                string f = FindFile(dir, "GamePC.cpr");
                if (f == null) continue;
                try
                {
                    var g = Parse(File.ReadAllBytes(f));
                    g.Path = f;
                    Debug.Log($"[TDU2] GamePC.cpr: {f} – Start {g.StartIsland ?? "-"}, Haus {string.Join("/", g.Houses)}, " +
                              $"Auto {string.Join(" ", g.DefaultCar)}, Garage {string.Join(" ", g.GarageCars)}, Geld {g.DefaultMoney}" +
                              (g.HasStartPosition ? $", Position {g.StartPosition}" : ""));
                    _cached = g; _cachedKey = key;
                    return g;
                }
                catch (Exception ex) { Debug.LogWarning("[TDU2] GamePC.cpr nicht lesbar: " + f + " – " + ex.Message); }
            }
            Debug.LogWarning("[TDU2] GamePC.cpr nicht gefunden (StreamingAssets oder neben Euro/Bnk)");
            _cached = null; _cachedKey = key;
            return null;
        }

        static string FindFile(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                if (!Directory.Exists(dir)) return null;
                string p = System.IO.Path.Combine(dir, name);
                if (File.Exists(p)) return p;
                foreach (var f in Directory.GetFiles(dir, "*.cpr"))
                    if (string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            catch (Exception) { }
            return null;
        }

        public static TDU2GamePC Parse(byte[] raw)
        {
            byte[] b = LooksLikeText(raw) ? raw : CprCrypto.Decrypt(raw);
            int len = b.Length;
            while (len > 0 && b[len - 1] == 0) len--;
            // Latin-1 (Kommentare französisch); ohne Encoding.GetEncoding – das fehlt in Unity-Builds ohne I18N
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append((char)b[i]);
            string text = sb.ToString();
            var g = new TDU2GamePC();
            string section = "";
            foreach (var rawLine in text.Split('\n'))
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    int e = line.IndexOf(']');
                    section = e > 0 ? line.Substring(1, e - 1).Trim() : line.Substring(1).Trim();
                    continue;
                }
                var tok = Tokenize(line);
                if (tok.Count == 0) continue;
                var en = new Entry { Section = section, Key = tok[0] };
                tok.RemoveAt(0);
                if (tok.Count > 0 && tok[0] == "=") tok.RemoveAt(0);
                en.Args = tok.ToArray();
                g.Entries.Add(en);
            }
            g.Interpret();
            return g;
        }

        static bool LooksLikeText(byte[] b)
        {
            if (b == null || b.Length == 0) return false;
            int n = Math.Min(b.Length, 256), bad = 0;
            for (int i = 0; i < n; i++) { byte c = b[i]; if (c < 9 || (c > 13 && c < 32)) bad++; }
            return bad == 0;
        }

        /// <summary>"//"-Kommentar abschneiden (außerhalb von Anführungszeichen).</summary>
        static string StripComment(string s)
        {
            bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') q = !q;
                else if (!q && c == '/' && i + 1 < s.Length && s[i + 1] == '/') return s.Substring(0, i);
            }
            return s;
        }

        static List<string> Tokenize(string s)
        {
            var r = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    int e = s.IndexOf('"', i + 1);
                    if (e < 0) e = s.Length;
                    r.Add(s.Substring(i + 1, e - i - 1));
                    i = e + 1;
                    continue;
                }
                if (c == '=') { r.Add("="); i++; continue; }
                int st = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=' && s[i] != '"') i++;
                r.Add(s.Substring(st, i - st));
            }
            return r;
        }

        static bool Is(Entry e, string key) => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase);

        static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        void Interpret()
        {
            foreach (var e in Entries)
            {
                if (Is(e, "AddLevel") && StartIsland == null && e.Args.Length > 0)
                {
                    // Nur echte Inseln als Start (Casino/Hawai_Route/Dubai nicht)
                    string lv = e.Args[0].Trim().ToLowerInvariant();
                    if (lv == "ibiza" || lv == "hawai") StartIsland = lv;
                }
                else if (Is(e, "DEFAULTSTARTPOSITION") && !HasStartPosition && e.Args.Length >= 3)
                {
                    var p = new Vector3(F(e.Args[0]), F(e.Args[1]), F(e.Args[2]));
                    if (p.sqrMagnitude > 1f) { StartPosition = p; HasStartPosition = true; }
                }
                else if ((Is(e, "DefaultHouse") || Is(e, "DefaultHouse2")) && e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
                {
                    // DefaultHouse vor DefaultHouse2
                    if (Is(e, "DefaultHouse")) Houses.Insert(0, e.Args[0].Trim()); else Houses.Add(e.Args[0].Trim());
                }
                else if (Is(e, "DefaultCarConfig") && DefaultCar.Length == 0 && e.Args.Length > 0) DefaultCar = e.Args;
                else if (Is(e, "DefaultGarageConfig") && GarageCars.Count == 0)
                {
                    foreach (var a in e.Args) if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0) GarageCars.Add(id);
                }
                else if (Is(e, "DefaultMoney") && e.Args.Length > 0)
                    long.TryParse(e.Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out DefaultMoney);
            }
        }

        /// <summary>Erster Wert eines Schlüssels (z.B. "LockWalkmode"), sonst null.</summary>
        public string Get(string key, string section = null)
        {
            foreach (var e in Entries)
                if (Is(e, key) && (section == null || string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase)))
                    return e.Args.Length > 0 ? e.Args[0] : "";
            return null;
        }

        /// <summary>Erstes Standard-Haus, das auf der geladenen Insel liegt.</summary>
        public Spot FindHouse(IList<Spot> spots)
        {
            foreach (var h in Houses) { var s = FindSpot(spots, h); if (s != null) return s; }
            return null;
        }

        /// <summary>
        /// Spot über den vollen Namen aus der Datenbank (z.B. "EPH_L2E_0345I"). spots.bin enthält nur 8 Zeichen Code ("EPH_L2E")
        /// und einen 8-Byte-Schlüssel = Zeichen des vollen Namens auf 8 Bytes aufsummiert (Byte k = Summe der Zeichen k, k+8, …).
        /// Bei Namen mit Endung "I" weicht das Byte dieser Stelle ab – es wird nicht verglichen.
        /// </summary>
        public static Spot FindSpot(IList<Spot> spots, string name)
        {
            if (spots == null || string.IsNullOrEmpty(name)) return null;
            foreach (var s in spots) if (string.Equals(s.Code, name, StringComparison.OrdinalIgnoreCase)) return s;
            int us = name.LastIndexOf('_');
            string code = us > 0 ? name.Substring(0, us) : name;
            string up = name.ToUpperInvariant();
            foreach (var s in spots)
                if (string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase) && KeyMatches(s.Key, up)) return s;
            return null;
        }

        static bool KeyMatches(ulong key, string name)
        {
            var sum = new int[8];
            for (int i = 0; i < name.Length; i++) sum[i & 7] += name[i];
            int skip = name.EndsWith("I", StringComparison.Ordinal) ? (name.Length - 1) & 7 : -1;
            for (int k = 0; k < 8; k++)
            {
                if (k == skip) continue;
                if ((byte)(key >> (8 * k)) != (byte)sum[k]) return false;
            }
            return true;
        }
    }
}
