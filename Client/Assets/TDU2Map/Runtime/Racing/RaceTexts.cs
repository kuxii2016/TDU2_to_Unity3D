using System;
using System.Collections.Generic;
using System.IO;
using TDU2.Formats;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Texte der Spieldatenbank nach HashCode (database/db_&lt;sprache&gt;.cpr), z.B. Prüfungsnamen (ig2 "dbna"),
    /// Beschreibungen ("dbd1"), Lizenzbeschreibung (DBLicense._championshipdesc_hc) und Ergebnismeldungen (1625..1628).
    /// </summary>
    public static class RaceTexts
    {
        static Dictionary<ulong, string> _lines;
        static string _key;
        static readonly object _lock = new object();

        public const ulong MsgTestFailed = 1625, MsgTestPassed = 1626, MsgLicenceWon = 1627, MsgLicenceWonLong = 1628;
        public const ulong MsgStopVehicle = 998, MsgStopVehicleAgain = 1000;

        public static void Ensure(string bnkDir, string lang)
        {
            if (string.IsNullOrEmpty(bnkDir)) return;
            string key = bnkDir + "|" + lang;
            lock (_lock)
            {
                if (_lines != null && _key == key) return;
                var d = new Dictionary<ulong, string>();
                try
                {
                    string db = Path.Combine(bnkDir, "database");
                    string p = Path.Combine(db, "db_" + lang + ".cpr");
                    if (!File.Exists(p)) p = Path.Combine(db, "db_us.cpr");
                    if (File.Exists(p))
                    {
                        var root = XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(p)));
                        foreach (var it in Items(root))
                        {
                            var t = it.S("Text") ?? it.S("Line");
                            if (t != null) d[it.U("HashCode")] = t.Replace("\r\n", "\n").Replace('\r', '\n');
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[TDU2] Rennschul-Texte: " + ex.Message); }
                _lines = d; _key = key;
            }
        }

        static IEnumerable<XmbNode> Items(XmbNode root)
        {
            if (root == null) yield break;
            if (root.IsArray) { foreach (var i in root.Items) yield return i; yield break; }
            foreach (var c in root.Children)
                if (c.IsArray) { foreach (var i in c.Items) yield return i; yield break; }
        }

        public static string Get(ulong hc, string fallback = "")
        {
            lock (_lock) { return _lines != null && hc != 0 && _lines.TryGetValue(hc, out var t) ? t : fallback; }
        }
    }
}
