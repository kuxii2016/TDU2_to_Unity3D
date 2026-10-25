using System;
using System.Collections.Generic;
using System.IO;
using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map.Flash
{
    /// <summary>
    /// Texte der Flash-Menüs wie im Spiel:
    ///   database/db_data.cpr  DBLoc  : _flash (Variablenname, z.B. "m_Resume_") -> _hashcode (+ Flags je Film: _main, _hud, _shops ...)
    ///   database/db_&lt;lang&gt;.cpr DBData : HashCode -> Text (ge = Deutsch, us = Englisch, fr, it, sp, ru, ja, ko, ch, cz, pl)
    /// Das Spiel setzt diese Werte als _root-Variablen der Filme (_root.m_Resume_ usw.).
    /// </summary>
    public static class FlashLocalization
    {
        static Dictionary<string, string> _texts;
        static string _loadedKey;
        static readonly object _lock = new object();

        /// <summary>Sprachkürzel der Datenbankdateien zur Systemsprache.</summary>
        public static string LangCode(SystemLanguage l)
        {
            switch (l)
            {
                case SystemLanguage.German: return "ge";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Spanish: return "sp";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Chinese: case SystemLanguage.ChineseSimplified: case SystemLanguage.ChineseTraditional: return "ch";
                case SystemLanguage.Czech: return "cz";
                case SystemLanguage.Polish: return "pl";
                default: return "us";
            }
        }

        /// <summary>Variablenname -> Text (threadsicher, einmal je Bnk-Ordner/Sprache geladen).</summary>
        public static Dictionary<string, string> Load(string bnkDir, string lang)
        {
            lock (_lock)
            {
                string key = bnkDir + "|" + lang;
                if (_texts != null && _loadedKey == key) return _texts;
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    string db = Path.Combine(bnkDir, "database");
                    string dataPath = Path.Combine(db, "db_data.cpr");
                    string textPath = Path.Combine(db, "db_" + lang + ".cpr");
                    if (!File.Exists(textPath)) textPath = Path.Combine(db, "db_us.cpr");
                    if (File.Exists(dataPath) && File.Exists(textPath))
                    {
                        // HashCode -> Text
                        var troot = XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(textPath)));
                        var lines = new Dictionary<int, string>();
                        foreach (var it in ItemsOf(troot))
                        {
                            var t = it.S("Text") ?? it.S("Line");
                            if (t != null) lines[it.I("HashCode")] = t.Replace("\r\n", "\n").Replace('\r', '\n');
                        }
                        var droot = XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(dataPath)));
                        var loc = droot.Get("DBLoc");
                        if (loc != null)
                            foreach (var r in loc.Items)
                            {
                                string name = r.S("_flash", "");
                                if (string.IsNullOrEmpty(name) || result.ContainsKey(name)) continue;
                                if (lines.TryGetValue(r.I("_hashcode"), out var txt)) result[name] = txt;
                            }
                    }
                }
                catch (Exception ex) 
                { 
                    DebugManager.Error("Flash text not loadable: " + ex.Message);
                }
                _texts = result; _loadedKey = key;
                return result;
            }
        }

        static IEnumerable<XmbNode> ItemsOf(XmbNode root)
        {
            if (root == null) yield break;
            if (root.IsArray) { foreach (var i in root.Items) yield return i; yield break; }
            foreach (var c in root.Children)
                if (c.IsArray) { foreach (var i in c.Items) yield return i; yield break; }
        }

        /// <summary>Alle Texte als _root-Variablen in den Film schreiben.</summary>
        public static void Apply(VmfPlayer p, Dictionary<string, string> texts)
        {
            if (p == null || texts == null) return;
            foreach (var kv in texts) p.SetVariable("/:" + kv.Key, kv.Value);
        }
    }
}
