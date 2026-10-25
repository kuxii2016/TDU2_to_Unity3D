using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>
    /// Liefert die Tunnel-Dreiecke (Boden/Wände/Decke) einer Sektor-Bank in Bank-lokalen Koordinaten,
    /// damit ein Sektor das Portal eines Nachbarsektors mit ausschneiden kann (Tunnel über Sektorgrenze).
    /// Ergebnis pro Bankpfad gecacht. Nicht-Tunnel-Bänke werden über einen billigen .2dm-Scan erkannt und
    /// ohne Bau der .3dg-Meshes übersprungen (Ladezeit). Thread-sicher.
    /// </summary>
    public static class TunnelTriangles
    {
        static readonly ConcurrentDictionary<string, Vector3[]> Cache = new ConcurrentDictionary<string, Vector3[]>();

        public static Vector3[] Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return Array.Empty<Vector3>();
            return Cache.GetOrAdd(path, Extract);
        }

        static Vector3[] Extract(string path)
        {
            try
            {
                if (!File.Exists(path)) return Array.Empty<Vector3>();
                var bank = BnkArchive.Open(path);

                // Günstige Vorprüfung: nur .2dm einlesen und nach Tunnel-Material suchen.
                bool anyTunnel = false;
                foreach (var e in bank.Entries)
                {
                    if (!e.Extension.Equals(".2dm", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var mats = Material2DM.Parse(bank.Read(e));
                        foreach (var kv in mats.Materials)
                            if (TerrainHoles.IsTunnelRoad(kv.Value)) { anyTunnel = true; break; }
                    }
                    catch { }
                    if (anyTunnel) break;
                }
                if (!anyTunnel) return Array.Empty<Vector3>();

                var byStem = new Dictionary<string, BnkEntry[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in bank.Entries)
                {
                    var x = e.Extension.ToLowerInvariant();
                    int slot = x == ".3dd" ? 0 : x == ".3dg" ? 1 : x == ".2dm" ? 2 : -1;
                    if (slot < 0) continue;
                    var stem = e.Name.Substring(0, e.Name.Length - e.Extension.Length);
                    if (!byStem.TryGetValue(stem, out var arr)) byStem[stem] = arr = new BnkEntry[3];
                    arr[slot] = e;
                }
                var sets = new List<ObjectSet>();
                foreach (var kv in byStem)
                {
                    var a = kv.Value;
                    if (a[0] == null || a[1] == null) continue;
                    try { sets.Add(ObjectSet.Load(bank, a[0], a[1], a[2], null)); }
                    catch { }
                }
                var tri = TerrainHoles.ExtractTunnelTriangles(sets);
                foreach (var s in sets) s.Release();
                return tri.Count == 0 ? Array.Empty<Vector3>() : tri.ToArray();
            }
            catch { return Array.Empty<Vector3>(); }
        }
    }
}
