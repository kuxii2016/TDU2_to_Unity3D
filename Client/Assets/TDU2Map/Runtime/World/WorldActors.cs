using System;
using System.Collections.Generic;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>
    /// World Actors der Insel (islands/&lt;insel&gt;/scenarics/packs/worldactors.bnk): Ampeln (SF_Xin_*), Schilder (RS_*),
    /// Bänke, Mülleimer, Absperrungen. Die "A"-Platzierungen der Sektoren (-A.pmi, Kind 2) nennen als Schlüssel den
    /// __Hc eines Entity__Actor (bzw. __TrafficLights) aus entities.xmb; dessen "mesh" verweist über hashcodes.xmb auf
    /// .3dd/.3dg/.2dm der Bank (Rückfall: Dateistamm = __Name ohne "_act").
    /// </summary>
    public sealed class WorldActorLib
    {
        public string Path;
        public BankModel Model;
        public readonly Dictionary<ulong, ObjectSet> ByHc = new Dictionary<ulong, ObjectSet>();
        public readonly Dictionary<ulong, string> NameOf = new Dictionary<ulong, string>();
        /// <summary>Ampeln (ArrayOf_Entity__Actor__TrafficLights): werden nach dem Aufbau bei TDU2TrafficLights angemeldet.</summary>
        public readonly HashSet<ulong> TrafficLights = new HashSet<ulong>();
        public readonly List<string> Log = new List<string>();

        /// <summary>Thread-sicher (Hintergrund): Bank öffnen, alle Actor-Modelle vorbereiten.</summary>
        public static WorldActorLib Load(string path, GlobalTextureCache textures)
        {
            var lib = new WorldActorLib { Path = path };
            lib.Model = BankModel.Load(path, BankKind.Generic, false, false, false);
            var bank = lib.Model.Bank;
            textures?.IndexBank(bank);
            try { TDU2.Interior.InteriorFx.PrepareUva(bank); } catch (Exception) { }   // z.B. Pfeil-Schilder (fleche_*.uva)

            var entEntry = bank.Find("entities.xmb");
            if (entEntry == null) { lib.Log.Add("entities.xmb fehlt"); return lib; }
            var ent = XmbFile.Parse(Unwrap(bank.Read(entEntry)));

            // rsc-Hash -> Bankeintrag (hashcodes.xmb: FileIndices / FileReferences)
            var byRsc = new Dictionary<ulong, BnkEntry>();
            var hcEntry = bank.Find("hashcodes.xmb");
            if (hcEntry != null)
            {
                var hc = XmbFile.Parse(Unwrap(bank.Read(hcEntry)));
                var byIndex = new Dictionary<int, BnkEntry>();
                foreach (var e in bank.Entries) byIndex[e.Index] = e;
                var groups = hc?.Get("___CmdSaveFileInfoArray");
                if (groups != null)
                    foreach (var g in groups.Items)
                    {
                        var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                        if (idx == null || refs == null) continue;
                        int n = Math.Min(idx.Items.Count, refs.Items.Count);
                        for (int i = 0; i < n; i++)
                            if (byIndex.TryGetValue(idx.Items[i].AsInt(-1), out var be)) byRsc[refs.Items[i].AsULong()] = be;
                    }
            }

            var cache = new Dictionary<string, ObjectSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in ent.Children)
            {
                if (!section.Name.StartsWith("ExportType_", StringComparison.Ordinal)) continue;
                foreach (var arr in section.Children)
                {
                    if (!arr.IsArray) continue;
                    foreach (var it in arr.Items)
                    {
                        var mesh = it.Get("mesh");
                        if (mesh == null) continue;
                        ulong hcKey = it.U("__Hc");
                        string name = it.S("__Name", "");
                        if (hcKey == 0) continue;
                        lib.NameOf[hcKey] = name;
                        if (arr.Name.IndexOf("TrafficLights", StringComparison.OrdinalIgnoreCase) >= 0) lib.TrafficLights.Add(hcKey);
                        BnkEntry dd = null, dg = null, dm = null;
                        foreach (var r in mesh.Items)
                        {
                            if (!byRsc.TryGetValue(r.U("rsc"), out var be) || be == null) continue;
                            int sub = r.I("rscSubType", -1);
                            if (sub == 0) dd = be; else if (sub == 1) dg = be; else if (sub == 2) dm = be;
                        }
                        if (dd == null || dg == null)
                        {
                            string stem = name.EndsWith("_act", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
                            foreach (var e in bank.Entries)
                            {
                                if (!e.Name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                                if (e.Extension == ".3dd" && dd == null) dd = e;
                                else if (e.Extension == ".3dg" && dg == null) dg = e;
                                else if (e.Extension == ".2dm" && dm == null) dm = e;
                            }
                        }
                        if (dd == null || dg == null) { lib.Log.Add(name + ": Modell fehlt"); continue; }
                        if (!cache.TryGetValue(dd.Name, out var set))
                        {
                            try { set = ObjectSet.Load(bank, dd, dg, dm, lib.Log); set.Placements = new List<Matrix4x4>(); }
                            catch (Exception ex) { lib.Log.Add(name + ": " + ex.Message); set = null; }
                            cache[dd.Name] = set;
                        }
                        if (set != null) lib.ByHc[hcKey] = set;
                    }
                }
            }
            return lib;
        }

        static byte[] Unwrap(byte[] b)
        {
            if (b == null || XmbFile.IsXmb(b)) return b;
            for (int i = 1; i + 4 <= b.Length && i < 64; i++)
                if (b[i] == 'X' && b[i + 1] == 'M' && b[i + 2] == 'B' && b[i + 3] == 'F')
                {
                    var r = new byte[b.Length - i];
                    Array.Copy(b, i, r, 0, r.Length);
                    return r;
                }
            return b;
        }

        /// <summary>
        /// Hauptthread: die Actors eines Sektors unter parent bauen (Platzierungen relativ zum Sektor-Ursprung).
        /// Gibt die Zahl gebauter Instanzen zurück.
        /// </summary>
        public int Build(PmiFile pmi, Transform parent, GlobalTextureCache textures, BuildResources res, ObjectBuildOptions opt, Transform mapRoot = null)
        {
            if (pmi == null || parent == null || Model == null) return 0;
            int count = 0;
            Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex = (k, u, v) => textures.Get(k, u, v);
            foreach (var g in pmi.Groups)
            {
                if (!ByHc.TryGetValue(g.Key, out var set) || set == null) continue;
                var list = new List<Matrix4x4>(g.Instances.Count);
                foreach (var inst in g.Instances)
                {
                    var m = MathUtil.TRS(inst.Position, inst.Rotation, inst.Scale);
                    list.Add(inst.Pre.HasValue ? m * inst.Pre.Value : m);
                }
                if (list.Count == 0) continue;
                var holder = new GameObject(NameOf.TryGetValue(g.Key, out var n) ? n : "Actor").transform;
                holder.SetParent(parent, false);
                set.Placements = list;
                List<GameObject> built = null;
                try { built = ObjectBuilder.BuildObjects(set, getTex, holder, res, opt); }
                finally { set.Placements = new List<Matrix4x4>(); }
                if (built != null && mapRoot != null && TrafficLights.Contains(g.Key))
                    foreach (var go in built) if (go != null) TDU2TrafficLights.Register(go.transform, mapRoot);
                count += list.Count;
            }
            return count;
        }
    }
}
