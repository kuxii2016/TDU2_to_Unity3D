using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Formats;
using TDU2.IO;
using TDU2.World;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Props einer Prüfung: Kegel und Absperrungen (ig2 DEC2) sowie die Tor-Optik der Checkpoints.
    ///
    /// Modelle wie im Spiel (EXE 0x9795a0): DEC2-Art 0/1/2 -> World-Actor "SF_Stud_act" (Kegel), 3/4 -> "RS_Separ_A_act",
    /// 5 -> "RS_Separ_B_act" (Absperrung, Endstück). Die Actors stehen in
    /// islands/&lt;insel&gt;/scenarics/packs/worldactors.bnk (entities.xmb: __Name, "mesh" = rsc-Hashes, aufgelöst über hashcodes.xmb).
    /// Fehlen Bank oder Actor, werden einfache Ersatzmodelle erzeugt.
    ///
    /// Linien (1→2, 4→5) werden mit Objekten gefüllt: Kegel alle <see cref="ConeSpacing"/> m, Absperrungen Stoß an Stoß
    /// (Länge aus den Modell-Bounds). Der Abstand selbst steht nicht in der .ig2 (vermutet).
    /// Alles hängt unter MapRoot in Inselkoordinaten (verschiebt sich mit dem Floating Origin).
    /// </summary>
    public sealed class RaceProps
    {
        public const string ConeActor = "SF_Stud_act", SeparActorA = "RS_Separ_A_act", SeparActorB = "RS_Separ_B_act";
        public static float ConeSpacing = 2.5f;
        public static float FallbackSeparLength = 2f;

        /// <summary>Wird bei einem Treffer eines Props durch das Spielerauto ausgelöst (Prop-Objekt, Aufprallgeschwindigkeit m/s).</summary>
        public event Action<GameObject, float> Hit;

        public GameObject Root { get; private set; }
        public int Count { get; private set; }
        public string Source { get; private set; } = "";

        readonly List<TDU2MapStreamer.SceneHandle> _scenes = new List<TDU2MapStreamer.SceneHandle>();
        readonly List<GameObject> _owned = new List<GameObject>();

        // ------------------------------------------------------------------ Modellbibliothek (einmal je Insel)
        sealed class Library
        {
            public string Path;
            public BankModel Model;
            public ObjectSet Cone, SeparA, SeparB;
            public float SeparLength = FallbackSeparLength;
            public bool SeparMeasured, SeparAlongX = true;
            /// <summary>Tormodelle (Entity__Actor__CheckPoint): nach CHK2-Art, sonst irgendeins.</summary>
            public readonly Dictionary<Ige2GateKind, ObjectSet> Gates = new Dictionary<Ige2GateKind, ObjectSet>();
            public ObjectSet GateAny, GateStart, GateFinish;
            /// <summary>Actors aus pack_cp.bnk nach __Name (Check_point, Start_line(_Off), Finish_line(_Off), BrakingZone_01, CP_OffRoad, Trajectory ...).</summary>
            public readonly Dictionary<string, ObjectSet> Named = new Dictionary<string, ObjectSet>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Textur-Animationen (.uva) aus pack_cp.bnk, Name ohne Endung (klein).</summary>
            public readonly Dictionary<string, RaceUva> Uva = new Dictionary<string, RaceUva>(StringComparer.OrdinalIgnoreCase);
            /// <summary>(Materialname, Texturdatei) -> Animation, aus den .2dm (Ebene -> MTP. -> UVA. -> Schlüssel der .uva).</summary>
            public readonly Dictionary<string, RaceUva> UvaByMat = new Dictionary<string, RaceUva>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Bank-Modell, aus dem ein Set stammt (worldactors.bnk oder pack_cp.bnk).</summary>
            public readonly Dictionary<ObjectSet, BankModel> ModelOf = new Dictionary<ObjectSet, BankModel>();
            public readonly Dictionary<BankModel, BankModel> Owner = new Dictionary<BankModel, BankModel>();
            public readonly Dictionary<ObjectSet, float> GateWidth = new Dictionary<ObjectSet, float>();
            public string Log = "";
        }
        static Library _lib;
        static Task<Library> _libTask;
        static string _libPath;

        /// <summary>World-Actor-Bank der Insel suchen.</summary>
        public static string WorldActorsBank(string bnkDir, string islandName)
        {
            if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(islandName)) return null;
            try
            {
                string dir = Path.Combine(bnkDir, "islands", islandName, "scenarics", "packs");
                if (!Directory.Exists(dir)) return null;
                foreach (var f in Directory.GetFiles(dir, "*.bnk"))
                    if (string.Equals(System.IO.Path.GetFileName(f), "worldactors.bnk", StringComparison.OrdinalIgnoreCase)) return f;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Lädt (im Hintergrund) die Kegel-/Absperrungsmodelle. true, sobald fertig (auch wenn nichts gefunden wurde).</summary>
        public static bool Preload(string bankPath)
        {
            if (bankPath == null) return true;
            if (_lib != null && _libPath == bankPath) return true;
            if (_libTask == null || _libPath != bankPath)
            {
                _libPath = bankPath; _lib = null;
                _libTask = Task.Run(() => LoadLibrary(bankPath));
            }
            if (!_libTask.IsCompleted) return false;
            if (_libTask.IsFaulted) { Debug.LogWarning("[TDU2] worldactors.bnk: " + _libTask.Exception?.GetBaseException().Message); _lib = new Library { Path = bankPath }; }
            else _lib = _libTask.Result;
            if (!string.IsNullOrEmpty(_lib.Log)) Debug.Log("[TDU2] Rennschul-Props: " + _lib.Log);
            return true;
        }

        /// <summary>Eine Actor-Bank (entities.xmb + hashcodes.xmb) zum Auflösen von Actor-Modellen.</summary>
        sealed class ActorBank
        {
            public string Path;
            public BankModel Model;
            public XmbNode Ent;
            public readonly Dictionary<ulong, BnkEntry> ByRsc = new Dictionary<ulong, BnkEntry>();

            public static ActorBank Open(string path, List<string> log)
            {
                if (path == null || !File.Exists(path)) return null;
                var ab = new ActorBank { Path = path };
                ab.Model = BankModel.Load(path, BankKind.Generic, false, false, false);
                var bank = ab.Model.Bank;
                var entEntry = bank.Find("entities.xmb");
                var hcEntry = bank.Find("hashcodes.xmb");
                if (entEntry == null || hcEntry == null) { log.Add("entities.xmb/hashcodes.xmb fehlt in " + System.IO.Path.GetFileName(path)); return ab; }
                ab.Ent = XmbFile.Parse(Unwrap(bank.Read(entEntry)));
                var hc = XmbFile.Parse(Unwrap(bank.Read(hcEntry)));
                var byIndex = new Dictionary<int, BnkEntry>();
                foreach (var e in bank.Entries) byIndex[e.Index] = e;
                var groups = hc.Get("___CmdSaveFileInfoArray");
                if (groups != null)
                    foreach (var g in groups.Items)
                    {
                        var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                        if (idx == null || refs == null) continue;
                        int n = Math.Min(idx.Items.Count, refs.Items.Count);
                        for (int i = 0; i < n; i++)
                        {
                            int fi = idx.Items[i].AsInt(-1);
                            if (byIndex.TryGetValue(fi, out var be)) ab.ByRsc[refs.Items[i].AsULong()] = be;
                        }
                    }
                return ab;
            }

            /// <summary>Alle Einträge (Array-Name, Objekt) aus entities.xmb.</summary>
            public IEnumerable<(string array, XmbNode item)> Items()
            {
                if (Ent == null) yield break;
                foreach (var section in Ent.Children)
                {
                    if (!section.Name.StartsWith("ExportType_", StringComparison.Ordinal)) continue;
                    foreach (var arr in section.Children)
                        if (arr.IsArray) foreach (var it in arr.Items) yield return (arr.Name, it);
                }
            }

            public ObjectSet Load(XmbNode it, string actor, List<string> log)
            {
                var bank = Model.Bank;
                var mesh = it.Get("mesh") ?? it.Get("Model") ?? it.Get("Mesh");
                BnkEntry dd = null, dg = null, dm = null;
                if (mesh != null)
                    foreach (var r in mesh.Items)
                    {
                        if (!ByRsc.TryGetValue(r.U("rsc"), out var be) || be == null) continue;
                        int sub = r.I("rscSubType", -1);
                        if (sub == 0) dd = be; else if (sub == 1) dg = be; else if (sub == 2) dm = be;
                    }
                // Rückfall: Modell über den Namen (Stamm = __Name ohne "_act")
                if (dd == null || dg == null)
                {
                    string stem = actor.EndsWith("_act", StringComparison.OrdinalIgnoreCase) ? actor.Substring(0, actor.Length - 4) : actor;
                    foreach (var e in bank.Entries)
                    {
                        if (!e.Name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                        if (e.Extension == ".3dd" && dd == null) dd = e;
                        else if (e.Extension == ".3dg" && dg == null) dg = e;
                        else if (e.Extension == ".2dm" && dm == null) dm = e;
                    }
                }
                if (dd == null || dg == null) { log.Add(actor + ": Modell nicht aufgelöst"); return null; }
                try
                {
                    var set = ObjectSet.Load(bank, dd, dg, dm, log);
                    set.Placements = new List<Matrix4x4>();
                    log.Add(actor + " -> " + dd.Name + " (" + System.IO.Path.GetFileName(Path) + ")");
                    return set;
                }
                catch (Exception ex) { log.Add(actor + ": " + ex.Message); return null; }
            }

            public ObjectSet Find(string actor, List<string> log)
            {
                foreach (var (_, it) in Items())
                    if (string.Equals(it.S("__Name", ""), actor, StringComparison.OrdinalIgnoreCase)) return Load(it, actor, log);
                return null;
            }
        }

        static Library LoadLibrary(string path)
        {
            var lib = new Library { Path = path };
            var log = new List<string>();
            var wa = ActorBank.Open(path, log);
            if (wa == null) { lib.Log = "worldactors.bnk fehlt"; return lib; }
            lib.Model = wa.Model;
            lib.Owner[wa.Model] = wa.Model;

            ObjectSet Find(string actor)
            {
                var set = wa.Find(actor, log);
                if (set == null) log.Add(actor + " nicht in worldactors.bnk");
                else lib.ModelOf[set] = wa.Model;
                return set;
            }
            lib.Cone = Find(ConeActor);
            lib.SeparA = Find(SeparActorA);
            lib.SeparB = Find(SeparActorB);

            // Checkpoint-Tore: pack_cp.bnk (EXE: "Scenarics\Packs\pack_cp.bnk", liegt im Insel-Ordner neben worldactors.bnk),
            // zusätzlich worldactors.bnk. Klasse Entity__Actor__CheckPoint (Shader td_checkpoint).
            var banks = new List<ActorBank>();
            string cpPath = null;
            try
            {
                foreach (var f in Directory.GetFiles(System.IO.Path.GetDirectoryName(path), "*.bnk"))
                    if (string.Equals(System.IO.Path.GetFileName(f), "pack_cp.bnk", StringComparison.OrdinalIgnoreCase)) cpPath = f;
            }
            catch (Exception) { }
            if (cpPath != null)
            {
                try { var cp = ActorBank.Open(cpPath, log); if (cp != null) banks.Add(cp); }
                catch (Exception ex) { log.Add("pack_cp.bnk: " + ex.Message); }
            }
            else log.Add("pack_cp.bnk nicht gefunden");
            banks.Add(wa);

            foreach (var ab in banks)
            {
                if (ab != wa)
                {
                    var uvaNames = new List<string>();
                    foreach (var e in ab.Model.Bank.Entries)
                    {
                        if (e.Extension != ".uva") continue;
                        try
                        {
                            string stem = System.IO.Path.GetFileNameWithoutExtension(e.Name);
                            var u = RaceUva.Parse(ab.Model.Bank.Read(e), stem);
                            if (u != null) { lib.Uva[stem] = u; uvaNames.Add(stem + "(" + u.Length.ToString("0.##") + "s)"); }
                        }
                        catch (Exception ex) { log.Add(e.Name + ": " + ex.Message); }
                    }
                    log.Add(System.IO.Path.GetFileName(ab.Path) + ": UVA [" + string.Join(", ", uvaNames) + "]");
                    try { LinkUva(ab.Model.Bank, lib, log); } catch (Exception ex) { log.Add("UVA-Zuordnung: " + ex.Message); }
                }
                var all = new List<string>();
                var arrays = new HashSet<string>();
                foreach (var (arr, it) in ab.Items())
                {
                    arrays.Add(arr);
                    var nm = it.S("__Name", "");
                    bool isCp = arr.IndexOf("CheckPoint", StringComparison.OrdinalIgnoreCase) >= 0
                                || (ab != wa && (it.Get("mesh") != null || it.Get("Model") != null));
                    if (!isCp || nm.Length == 0) continue;
                    all.Add(nm);
                    var set = ab.Load(it, nm, log);
                    if (set == null) continue;
                    lib.ModelOf[set] = ab.Model;
                    lib.Owner[ab.Model] = ab.Model;
                    if (!lib.Named.ContainsKey(nm)) lib.Named[nm] = set;
                    string l = nm.ToLowerInvariant();
                    var kind = l.Contains("gate") || l.Contains("porti") || l.Contains("arch") ? Ige2GateKind.Gate
                             : l.Contains("cone") || l.Contains("stud") ? Ige2GateKind.Cones
                             : l.Contains("brak") || l.Contains("stop") || l.Contains("park") ? Ige2GateKind.BrakingZone : Ige2GateKind.Normal;
                    if (!lib.Gates.ContainsKey(kind)) lib.Gates[kind] = set;
                    if (lib.GateAny == null) lib.GateAny = set;
                    if (l.Contains("finish") || l.Contains("arriv") || l.Contains("end")) lib.GateFinish = lib.GateFinish ?? set;
                    if (l.Contains("start") || l.Contains("depart")) lib.GateStart = lib.GateStart ?? set;
                }
                log.Add(System.IO.Path.GetFileName(ab.Path) + ": Checkpoint-Actors [" + string.Join(", ", all) + "], Arrays [" + string.Join(", ", arrays) + "]");
            }
            lib.Log = string.Join("; ", log);
            return lib;
        }

        /// <summary>
        /// Material -> .uva über die .2dm (Docs/tdu2-specs.md §6): MAT.-Ebene (32 Byte) +8 Texturschlüssel, +28 Offset der
        /// MTP.-Nutzdaten; MTP. +20 Offset der UVA.-Nutzdaten; diese beginnen mit dem Schlüssel der .uva (Datei +16).
        /// Materialnamen sind im Spiel auf 15 Zeichen gekürzt und mehrfach vergeben -> Schlüssel "Name|Textur".
        /// </summary>
        static void LinkUva(BnkArchive bank, Library lib, List<string> log)
        {
            var byKey = new Dictionary<ulong, RaceUva>();
            foreach (var u in lib.Uva.Values) byKey[u.Key] = u;
            var texName = new Dictionary<ulong, string>();
            foreach (var e in bank.Entries)
                if (e.Extension == ".2db") { var hd = bank.ReadPartial(e, 0, 0x28); ulong k = TextureDef.PeekKey(hd); if (k != 0) texName[k] = e.Name; }
            int links = 0;
            foreach (var e in bank.Entries)
            {
                if (e.Extension != ".2dm") continue;
                var d = bank.Read(e);
                for (int m = 0; m + 0x110 <= d.Length; m++)
                {
                    if (d[m] != 'M' || d[m + 1] != 'A' || d[m + 2] != 'T' || d[m + 3] != '.') continue;
                    string name = Bin.CString(d, m + 0x20, 32);
                    int n = Math.Min(16, (int)Bin.U16(d, m + 0x10C));
                    for (int i = 0; i < n; i++)
                    {
                        int p = m + 0x110 + i * 32;
                        if (p + 32 > d.Length) break;
                        ulong tk = Bin.U64(d, p + 8);
                        int mtp = (int)Bin.U32(d, p + 28);
                        if (mtp <= 0 || mtp + 24 > d.Length) continue;
                        int up = (int)Bin.U32(d, mtp + 20);
                        if (up <= 0 || up + 8 > d.Length) continue;
                        if (!byKey.TryGetValue(Bin.U64(d, up), out var anim)) continue;
                        texName.TryGetValue(tk, out var tn);
                        lib.UvaByMat[name + "|" + (tn ?? "")] = anim;
                        if (!lib.UvaByMat.ContainsKey(name)) lib.UvaByMat[name] = anim;
                        links++;
                    }
                }
            }
            log.Add("UVA-Zuordnungen: " + links);
        }

        /// <summary>XMBF ggf. aus dem 12-Byte-Wrapper lösen.</summary>
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

        // ------------------------------------------------------------------ Aufbau

        /// <summary>Props und Tore der Prüfung unter MapRoot aufbauen.</summary>
        public void Build(TDU2MapStreamer streamer, RaceDefinition race, Material gateMaterial, List<RaceGateVisual> gateVisuals)
        {
            Clear();
            if (streamer == null || streamer.MapRoot == null || race == null) return;
            Root = new GameObject("RaceProps " + race.Name);
            Root.transform.SetParent(streamer.MapRoot, false);

            // Platzierungen sammeln (Inselkoordinaten)
            var cones = new List<Matrix4x4>();
            var separA = new List<Matrix4x4>();
            var separB = new List<Matrix4x4>();
            float sepLen = SeparLength(streamer);
            foreach (var d in race.Decors)
            {
                if (!d.IsLine)
                {
                    var m = Matrix4x4.TRS(Ground(streamer, d.From), Quaternion.LookRotation(d.Forward, Vector3.up), Vector3.one);
                    (d.IsSeparator ? separA : cones).Add(m);
                    continue;
                }
                var a = d.From; var b = d.To;
                var dir = b - a; dir.y = 0f;
                float len = dir.magnitude;
                if (len < 0.05f) { (d.IsSeparator ? separA : cones).Add(Matrix4x4.TRS(Ground(streamer, a), Quaternion.identity, Vector3.one)); continue; }
                dir /= len;
                if (d.IsSeparator)
                {
                    // Absperrungen Stoß an Stoß quer zur Linie ausgerichtet entlang der Linie, Endstück B am Ende
                    int n = Mathf.Max(1, Mathf.RoundToInt(len / sepLen));
                    var rot = _lib != null && !_lib.SeparAlongX ? Quaternion.LookRotation(dir, Vector3.up) : Quaternion.LookRotation(new Vector3(-dir.z, 0f, dir.x), Vector3.up);
                    for (int i = 0; i <= n; i++)
                    {
                        var p = Vector3.Lerp(a, b, i / (float)n);
                        (i == n ? separB : separA).Add(Matrix4x4.TRS(Ground(streamer, p), rot, Vector3.one));
                    }
                }
                else
                {
                    int n = Mathf.Max(1, Mathf.RoundToInt(len / ConeSpacing));
                    var rot = Quaternion.LookRotation(dir, Vector3.up);
                    for (int i = 0; i <= n; i++) cones.Add(Matrix4x4.TRS(Ground(streamer, Vector3.Lerp(a, b, i / (float)n)), rot, Vector3.one));
                }
            }

            // Kegel an den Seiten der Kegel-Tore (CHK2-Art 1) und an den Ecken der Bremszone
            foreach (var cp in race.Checkpoints)
            {
                var rot = Quaternion.LookRotation(cp.Forward, Vector3.up);
                if (cp.GateKind == Ige2GateKind.Cones)
                {
                    cones.Add(Matrix4x4.TRS(Ground(streamer, cp.Position + cp.Right * (cp.Width * 0.5f)), rot, Vector3.one));
                    cones.Add(Matrix4x4.TRS(Ground(streamer, cp.Position - cp.Right * (cp.Width * 0.5f)), rot, Vector3.one));
                }
                else if (cp.IsBrakingZone)
                {
                    float hw = cp.Width * 0.5f, hd = cp.ZoneDepth * 0.5f;
                    foreach (var sx in new[] { -1f, 1f })
                        foreach (var sz in new[] { -1f, 1f })
                            cones.Add(Matrix4x4.TRS(Ground(streamer, cp.Position + cp.Right * (hw * sx) + cp.Forward * (hd * sz)), rot, Vector3.one));
                }
            }

            // Startplatz freihalten: keine Kegel/Absperrungen auf dem Auto (Startpunkt aus spotworld.bin, Auto ~4.5 x 2 m)
            int removed = ClearStart(race, cones) + ClearStart(race, separA) + ClearStart(race, separB);
            if (removed > 0) Debug.Log("[TDU2] Rennschule: " + removed + " Props am Startplatz entfernt");

            BuildSet(streamer, _lib?.Cone, cones, true, "Kegel");
            BuildSet(streamer, _lib?.SeparA, separA, false, "Absperrung");
            BuildSet(streamer, _lib?.SeparB ?? _lib?.SeparA, separB, false, "Absperrung");
            Source = _lib != null && (_lib.Cone != null || _lib.SeparA != null) ? "worldactors.bnk" : "Ersatzmodelle";

            // Tore (td_checkpoint-Optik) in Inselkoordinaten
            if (gateVisuals != null)
            {
                var gates = new GameObject("Gates");
                gates.transform.SetParent(Root.transform, false);
                var mat = gateMaterial != null ? gateMaterial : RaceGateVisual.SharedMaterial();
                foreach (var cp in race.Checkpoints)
                {
                    var model = BuildGateModel(streamer, gates.transform, cp, race);
                    if (model != null) { gateVisuals.Add(model); continue; }
                    if (cp.IsBrakingZone) { gateVisuals.Add(BuildZone(gates.transform, mat, cp, streamer)); continue; }
                    if (model != null) { gateVisuals.Add(model); continue; }
                    var vis = RaceGateVisual.CreatePosts(gates.transform, mat, cp, Mathf.Max(2f, cp.Width));
                    var p = cp.Position; p.y = Ground(streamer, cp.Position).y;
                    vis.transform.localPosition = p;
                    vis.transform.localRotation = Quaternion.LookRotation(cp.Forward, Vector3.up);
                    gateVisuals.Add(vis);
                }
                BuildTrajectory(streamer, race);
            }
        }

        public static float TrajectorySpacing = 2.6f;
        /// <summary>Größe der Pfeile (Modell 1 x 1 m, Spitze zeigt zu -Z bzw. V=0 der Textur).</summary>
        public static float TrajectoryScale = 2.4f;
        /// <summary>Lauflicht: Geschwindigkeit (m/s) und Wellenlänge (m) der Helligkeitswelle entlang der Route.</summary>
        public static float TrajectoryWaveSpeed = 18f, TrajectoryWaveLength = 30f;
        /// <summary>Drehung des Trajectory-Modells um Y (Grad); NaN = automatisch aus den Modellmaßen.</summary>
        public static float TrajectoryYaw = 180f;   // Pfeilspitze zeigt im Modell nach -Z (Textur V = 0) -> umdrehen
        public static Color TrajectoryTint = new Color(0.2f, 0.55f, 1f);

        /// <summary>
        /// Wegmarkierung auf der Fahrbahn: Actor "Trajectory" aus pack_cp.bnk entlang der (geglätteten) Route zwischen den Toren,
        /// alle <see cref="TrajectorySpacing"/> m, in Fahrtrichtung. [vermutet: Abstand]
        /// </summary>
        void BuildTrajectory(TDU2MapStreamer streamer, RaceDefinition race)
        {
            if (_lib == null || !_lib.Named.TryGetValue("Trajectory", out var set) || race.Checkpoints.Count < 2) return;
            // dichte Linie: Straßenroute (RaceRoadPath), sonst Catmull-Rom durch die Tore
            var dense = new List<Vector3>();
            if (race.RoadPath != null && race.RoadPath.Count > 1) dense.AddRange(race.RoadPath);
            else
            {
                var seq = race.TargetSequence();
                var pts = new List<Vector3> { race.StartPosition };
                foreach (int i in seq) pts.Add(race.Checkpoints[i].Position);
                dense.Add(pts[0]);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    Vector3 P(int k) => pts[Mathf.Clamp(k, 0, pts.Count - 1)];
                    Vector3 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
                    int sub = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(p1, p2) / 1f), 2, 400);
                    for (int k = 1; k <= sub; k++)
                    {
                        float u = k / (float)sub, u2 = u * u, u3 = u2 * u;
                        dense.Add(0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3));
                    }
                }
            }
            // Ausrichtung des Modells: Längsachse X -> um 90° drehen, damit der Streifen in Fahrtrichtung liegt
            float yaw = TrajectoryYaw;
            if (float.IsNaN(yaw))
            {
                var mn = new Vector3(float.MaxValue, 0, float.MaxValue); var mx = new Vector3(float.MinValue, 0, float.MinValue);
                foreach (var md in set.Meshes.Values)
                    if (md != null) foreach (var p in md.Positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                float ex = mx.x - mn.x, ez = mx.z - mn.z;
                yaw = ex > ez * 1.2f ? 90f : 0f;
                TDU2DebugLog.Event($"Trajectory-Modell: Ausdehnung X {ex:0.00} m, Z {ez:0.00} m -> Drehung {yaw}°");
            }
            var marks = new List<Matrix4x4>();
            float carry = TrajectorySpacing * 0.5f;
            for (int i = 1; i < dense.Count; i++)
            {
                var prev = dense[i - 1]; var q = dense[i];
                var d = q - prev; d.y = 0f;
                float len = d.magnitude;
                if (len < 1e-4f) continue;
                float walked = 0f;
                while (carry <= len - walked)
                {
                    walked += carry;
                    carry = TrajectorySpacing;
                    var at = prev + (q - prev) * (walked / len);
                    bool nearGate = false;
                    foreach (var cp in race.Checkpoints) { var g = cp.Position - at; g.y = 0f; if (g.sqrMagnitude < 9f) { nearGate = true; break; } }
                    if (!nearGate) marks.Add(Matrix4x4.TRS(Ground(streamer, at) + Vector3.up * 0.02f, Quaternion.LookRotation(d / len, Vector3.up) * Quaternion.Euler(0f, yaw, 0f), Vector3.one * TrajectoryScale));
                }
                carry -= len - walked;
            }
            if (marks.Count == 0) return;
            var holder = new GameObject("Trajectory");
            holder.transform.SetParent(Root.transform, false);
            try
            {
                var h = BuildFromLibrary(streamer, set, marks, holder.transform, "Trajectory");
                if (h != null)
                {
                    _scenes.Add(h);
                    if (h.Root != null)
                    {
                        foreach (var c in h.Root.GetComponentsInChildren<Collider>(true)) UnityUtil.SafeDestroy(c);
                        foreach (var t in h.Root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
                        ConvertMaterials(h.Root, TrajectoryTint);
                        var anim = h.Root.AddComponent<RaceTrajectoryWave>();
                        foreach (Transform c in h.Root.transform) anim.Marks.Add(c.GetComponentsInChildren<Renderer>(true));
                        anim.Spacing = TrajectorySpacing; anim.Speed = TrajectoryWaveSpeed; anim.WaveLength = TrajectoryWaveLength;
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Trajectory: " + ex.Message); }
        }

        public static float StartClearRadius = 3.5f;

        static int ClearStart(RaceDefinition race, List<Matrix4x4> list)
        {
            var sp = race.StartPosition;
            int n = list.RemoveAll(m =>
            {
                var p = (Vector3)m.GetColumn(3);
                float dx = p.x - sp.x, dz = p.z - sp.z;
                return dx * dx + dz * dz < StartClearRadius * StartClearRadius;
            });
            return n;
        }

        static readonly Dictionary<Texture, Material> _gateMats = new Dictionary<Texture, Material>();

        /// <summary>
        /// Original-Tor aus pack_cp.bnk (Entity__Actor__CheckPoint) an einem Checkpoint, ausgerichtet nach der CHK2-Matrix
        /// (Right/Up/Forward wie im Spiel, daher auch Schrift lesbar) und auf die Torbreite skaliert.
        /// Start: Start_line / Start_line_Off, Ziel: Finish_line / Finish_line_Off, Bremszone: BrakingZone_01,
        /// sonst Check_point (Dirt-Schule: CP_OffRoad). "_Off" = Variante, solange das Tor nicht das nächste ist.
        /// </summary>
        RaceGateVisual BuildGateModel(TDU2MapStreamer streamer, Transform parent, RaceCheckpoint cp, RaceDefinition race)
        {
            if (_lib == null || _lib.Named.Count == 0) return null;
            string on, off = null;
            if (cp.IsBrakingZone) on = "BrakingZone_01";
            else if (cp.IsStart && !race.IsCircuit) { on = "Start_line"; off = "Start_line_Off"; }
            else if (cp.IsFinish || (cp.IsStart && race.IsCircuit)) { on = "Finish_line"; off = "Finish_line_Off"; }
            else on = race.School == 'B' && _lib.Named.ContainsKey("CP_OffRoad") ? "CP_OffRoad" : "Check_point";
            if (!_lib.Named.TryGetValue(on, out var setOn)) return null;
            _lib.Named.TryGetValue(off ?? "", out var setOff);

            var holder = new GameObject($"Gate_{cp.Index}_{on}");
            holder.transform.SetParent(parent, false);
            var gp = Ground(streamer, cp.Position);
            var r = cp.Right.sqrMagnitude > 1e-6f ? cp.Right.normalized : Vector3.Cross(Vector3.up, cp.Forward).normalized;
            var f = cp.Forward.sqrMagnitude > 1e-6f ? cp.Forward.normalized : Vector3.forward;
            var u = cp.Up.sqrMagnitude > 1e-6f ? cp.Up.normalized : Vector3.up;
            var m = new Matrix4x4();
            m.SetColumn(0, r); m.SetColumn(1, u); m.SetColumn(2, f);
            m.SetColumn(3, new Vector4(cp.Position.x, gp.y, cp.Position.z, 1f));
            ObjectBuilder.ApplyMatrix(holder.transform, m);

            GameObject BuildOne(ObjectSet set, string label)
            {
                var sub = new GameObject(label);
                sub.transform.SetParent(holder.transform, false);
                try
                {
                    var h = BuildFromLibrary(streamer, set, new List<Matrix4x4> { Matrix4x4.identity }, sub.transform, label);
                    if (h == null || h.Root == null) { UnityUtil.SafeDestroy(sub); return null; }
                    _scenes.Add(h);
                    foreach (var c in h.Root.GetComponentsInChildren<Collider>(true)) UnityUtil.SafeDestroy(c);   // Tore sind nicht fest
                    foreach (var t in h.Root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
                    ConvertMaterials(h.Root);
                    return sub;
                }
                catch (Exception ex) { Debug.LogWarning("[TDU2] Tormodell " + label + ": " + ex.Message); UnityUtil.SafeDestroy(sub); return null; }
            }
            var onGo = BuildOne(setOn, on);
            if (onGo == null) { UnityUtil.SafeDestroy(holder); return null; }
            // Start_line/_Off und Finish_line/_Off verweisen auf dieselbe Szene (verschiedene Knoten) -> nur einmal bauen
            var offGo = setOff != null && setOff != setOn ? BuildOne(setOff, off) : null;
            if (_loggedNodes.Add(on)) Debug.Log("[TDU2] Tormodell " + on + " Knoten: " + NodeList(onGo.transform));

            // Breite (lokal X) messen und auf die Checkpoint-Breite skalieren
            if (!_lib.GateWidth.TryGetValue(setOn, out float w))
            {
                float ex = 0f;
                foreach (var rr in onGo.GetComponentsInChildren<Renderer>())
                    ex = Mathf.Max(ex, Mathf.Abs(holder.transform.InverseTransformVector(rr.bounds.size).x));
                w = ex > 0.2f ? ex : 0f;
                _lib.GateWidth[setOn] = w;
            }
            if (w > 0f && cp.Width > 0.5f)
            {
                var sc = holder.transform.localScale;
                float k = cp.Width / w;
                holder.transform.localScale = new Vector3(sc.x * k, sc.y, sc.z);
            }
            return RaceGateVisual.AttachOnOff(holder, cp, onGo, offGo);
        }

        static readonly HashSet<string> _loggedNodes = new HashSet<string>();

        static string NodeList(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            void Walk(Transform x, int d)
            {
                if (d > 6 || sb.Length > 1500) return;
                if (d > 0) sb.Append(new string('.', d)).Append(x.name).Append(x.GetComponent<Renderer>() != null ? "*" : "").Append(' ');
                foreach (Transform c in x) Walk(c, d + 1);
            }
            Walk(t, 0);
            return sb.ToString();
        }

        static readonly Dictionary<(Texture, string), Material> _gateMatsByName = new Dictionary<(Texture, string), Material>();

        /// <summary>Material-Name -> .uva (Datei heißt &lt;Material&gt;_&lt;Ebene&gt;_&lt;n&gt;.uva, §5.5/§6).</summary>
        RaceUva FindUva(string matName, string texName)
        {
            if (_lib == null || string.IsNullOrEmpty(matName)) return null;
            string m = matName.Replace(" (Instance)", "").Trim();
            if (_lib.UvaByMat.TryGetValue(m + "|" + (texName ?? ""), out var a)) return a;
            if (_lib.UvaByMat.TryGetValue(m, out a)) return a;
            foreach (var kv in _lib.Uva)
                if (kv.Key.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        RaceUvaAnimator Animator()
        {
            var a = Root.GetComponent<RaceUvaAnimator>();
            if (a == null) a = Root.AddComponent<RaceUvaAnimator>();
            return a;
        }

        /// <summary>
        /// Materialien auf TDU2/Checkpoint (td_checkpoint: Textur additiv, Tönung über __Instance_Color, UV-Animation .uva).
        /// Farbe des Originalmaterials bleibt als _Tint erhalten; fehlt sie (weiß) und ist defaultTint gesetzt, wird damit getönt.
        /// </summary>
        void ConvertMaterials(GameObject root, Color? defaultTint = null)
        {
            var sh = Shader.Find("TDU2/Checkpoint");
            if (sh == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    var tex = src != null ? src.mainTexture : null;
                    if (tex == null) continue;
                    string name = src.name ?? "";
                    var key = (tex, name);
                    if (!_gateMatsByName.TryGetValue(key, out var gm) || gm == null)
                    {
                        gm = new Material(sh) { name = "Checkpoint_" + name, mainTexture = tex };
                        gm.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        gm.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                        gm.SetFloat("_ZWrite", 0f);
                        gm.SetFloat("_Cull", 0f);
                        gm.renderQueue = 3000;
                        var tint = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
                        bool white = tint.r > 0.95f && tint.g > 0.95f && tint.b > 0.95f;
                        if (white && defaultTint.HasValue) tint = defaultTint.Value;
                        if (gm.HasProperty("_Tint")) gm.SetColor("_Tint", new Color(tint.r, tint.g, tint.b, 1f));
                        var uva = FindUva(name, tex.name);
                        if (uva != null) Animator().Items.Add((gm, uva));
                        _gateMatsByName[key] = gm;
                        TDU2DebugLog.Event("Checkpoint-Material " + name + " Textur " + tex.name + " Tönung " + tint + (uva != null ? " UVA " + uva.Name : " ohne UVA"));
                    }
                    else if (Root != null && FindUva(name, tex.name) is RaceUva uva2)
                    {
                        var an = Animator();
                        if (!an.Items.Exists(x => x.mat == gm)) an.Items.Add((gm, uva2));
                    }
                    mats[i] = gm;
                }
                r.sharedMaterials = mats;
            }
        }

        RaceGateVisual BuildZone(Transform parent, Material mat, RaceCheckpoint cp, TDU2MapStreamer streamer)
        {
            // Bremszone: flaches Feld auf dem Boden (gleiches Checkpoint-Material, liegt flach)
            var vis = RaceGateVisual.Create(parent, mat, cp, Vector3.zero, Quaternion.identity);
            var p = Ground(streamer, cp.Position);
            vis.transform.localPosition = new Vector3(cp.Position.x, p.y + 0.05f, cp.Position.z) - cp.Forward * (cp.ZoneDepth * 0.5f);
            vis.transform.localRotation = Quaternion.LookRotation(-Vector3.up, cp.Forward);
            vis.transform.localScale = new Vector3(cp.Width, cp.ZoneDepth, 1f);
            return vis;
        }

        float SeparLength(TDU2MapStreamer streamer)
        {
            var set = _lib?.SeparA;
            if (set == null || _lib.Model == null) return FallbackSeparLength;
            if (_lib.SeparMeasured) return _lib.SeparLength;
            _lib.SeparMeasured = true;
            // Länge der Absperrung messen: einmal an der Identität bauen, Render-Bounds im lokalen Raum
            var probe = new GameObject("probe");
            probe.transform.SetParent(Root.transform, false);
            var h = BuildFromLibrary(streamer, set, new List<Matrix4x4> { Matrix4x4.identity }, probe.transform, "probe");
            float ex = 0f, ez = 0f;
            foreach (var r in probe.GetComponentsInChildren<Renderer>())
            {
                var e = probe.transform.InverseTransformVector(r.bounds.size);
                ex = Mathf.Max(ex, Mathf.Abs(e.x)); ez = Mathf.Max(ez, Mathf.Abs(e.z));
            }
            float best = Mathf.Max(ex, ez);
            _lib.SeparAlongX = ex >= ez;
            if (h != null) { h.Res?.Destroy(); }
            UnityUtil.SafeDestroy(probe);
            _lib.SeparLength = best > 0.3f && best < 30f ? best : FallbackSeparLength;
            return _lib.SeparLength;
        }

        /// <summary>Ein Set der Bibliothek an den angegebenen Stellen bauen (über den Streamer: Materialien/Texturen wie die Map).</summary>
        TDU2MapStreamer.SceneHandle BuildFromLibrary(TDU2MapStreamer streamer, ObjectSet set, List<Matrix4x4> placements, Transform parent, string label)
        {
            var model = _lib.ModelOf.TryGetValue(set, out var om) ? om : _lib.Model;
            var keepObj = model.Objects; var keepExtra = new List<ObjectSet>(model.ExtraObjects);
            set.Placements = placements;
            model.Objects = set; model.ExtraObjects.Clear();
            try { return streamer.BuildScene(model, parent, label); }
            finally { model.Objects = keepObj; model.ExtraObjects.Clear(); model.ExtraObjects.AddRange(keepExtra); set.Placements = new List<Matrix4x4>(); }
        }

        void BuildSet(TDU2MapStreamer streamer, ObjectSet set, List<Matrix4x4> placements, bool dynamicBody, string label)
        {
            if (placements.Count == 0) return;
            var holder = new GameObject(label);
            holder.transform.SetParent(Root.transform, false);
            var items = new List<GameObject>();
            if (set != null && _lib?.Model != null)
            {
                try
                {
                    var h = BuildFromLibrary(streamer, set, placements, holder.transform, label);
                    if (h != null)
                    {
                        _scenes.Add(h);
                        if (h.Root != null) foreach (Transform t in h.Root.transform) items.Add(t.gameObject);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[TDU2] Prop-Aufbau " + label + ": " + ex.Message); items.Clear(); }
            }
            if (items.Count == 0)
            {
                foreach (var m in placements) items.Add(dynamicBody ? MakeCone(holder.transform, m) : MakeSepar(holder.transform, m));
            }
            foreach (var go in items) Physicalize(go, dynamicBody);
            Count += items.Count;
        }

        void Physicalize(GameObject go, bool dynamicBody)
        {
            // Collider aus den Render-Bounds (lokaler Raum des Halters)
            var rends = go.GetComponentsInChildren<Renderer>();
            Bounds? lb = null;
            foreach (var r in rends)
            {
                var wb = r.bounds;
                var c = go.transform.InverseTransformPoint(wb.center);
                var e = go.transform.InverseTransformVector(wb.extents);
                var b = new Bounds(c, new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)) * 2f);
                if (lb == null) lb = b; else { var x = lb.Value; x.Encapsulate(b); lb = x; }
            }
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;   // nicht von den Rad-Raycasts treffen
            if (go.GetComponent<Collider>() == null)
            {
                var box = go.AddComponent<BoxCollider>();
                var b = lb ?? new Bounds(new Vector3(0f, 0.4f, 0f), new Vector3(0.4f, 0.8f, 0.4f));
                box.center = b.center;
                box.size = new Vector3(Mathf.Max(0.1f, b.size.x), Mathf.Max(0.1f, b.size.y), Mathf.Max(0.1f, b.size.z));
            }
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = !dynamicBody;
            rb.mass = dynamicBody ? 4f : 60f;
            rb.interpolation = RigidbodyInterpolation.None;
            if (dynamicBody) rb.Sleep();
            var hit = go.AddComponent<RacePropHit>();
            hit.Owner = this;
            _owned.Add(go);
        }

        internal void ReportHit(GameObject prop, float speed) => Hit?.Invoke(prop, speed);

        static Vector3 Ground(TDU2MapStreamer streamer, Vector3 island)
        {
            var root = streamer.MapRoot;
            var w = root.TransformPoint(island);
            float y = island.y;
            if (Physics.Raycast(w + Vector3.up * 30f, Vector3.down, out var hit, 80f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                y = root.InverseTransformPoint(hit.point).y;
            else if (streamer.TryGetGroundHeight(w, out var gy))
                y = root.InverseTransformPoint(new Vector3(w.x, gy, w.z)).y;
            return new Vector3(island.x, y, island.z);
        }

        // ------------------------------------------------------------------ Ersatzmodelle

        static Material _coneMat, _sepMat;
        static Mesh _coneMesh;

        static Material MakeMat(Color c)
        {
            var sh = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
            var m = new Material(sh) { color = c };
            return m;
        }

        GameObject MakeCone(Transform parent, Matrix4x4 m)
        {
            if (_coneMat == null) _coneMat = MakeMat(new Color(1f, 0.42f, 0.05f));
            if (_coneMesh == null) _coneMesh = BuildConeMesh(0.17f, 0.72f, 12);
            var go = new GameObject("Kegel");
            go.transform.SetParent(parent, false);
            ObjectBuilder.ApplyMatrix(go.transform, m);
            go.AddComponent<MeshFilter>().sharedMesh = _coneMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _coneMat;
            return go;
        }

        GameObject MakeSepar(Transform parent, Matrix4x4 m)
        {
            if (_sepMat == null) _sepMat = MakeMat(new Color(0.85f, 0.1f, 0.08f));
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Absperrung";
            UnityUtil.SafeDestroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            ObjectBuilder.ApplyMatrix(go.transform, m);
            go.transform.localPosition += Vector3.up * 0.4f;
            go.transform.localScale = new Vector3(FallbackSeparLength * 0.98f, 0.8f, 0.45f);
            go.GetComponent<MeshRenderer>().sharedMaterial = _sepMat;
            return go;
        }

        static Mesh BuildConeMesh(float radius, float height, int seg)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            v.Add(new Vector3(0f, height, 0f));
            for (int i = 0; i <= seg; i++) { float a = i * Mathf.PI * 2f / seg; v.Add(new Vector3(Mathf.Cos(a) * radius, 0.03f, Mathf.Sin(a) * radius)); }
            for (int i = 1; i <= seg; i++) { t.Add(0); t.Add(i + 1); t.Add(i); }
            // Fuß (quadratische Platte)
            int b = v.Count; float s = radius * 1.35f;
            v.Add(new Vector3(-s, 0.03f, -s)); v.Add(new Vector3(s, 0.03f, -s)); v.Add(new Vector3(s, 0.03f, s)); v.Add(new Vector3(-s, 0.03f, s));
            v.Add(new Vector3(-s, 0f, -s)); v.Add(new Vector3(s, 0f, -s)); v.Add(new Vector3(s, 0f, s)); v.Add(new Vector3(-s, 0f, s));
            t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            for (int i = 0; i < 4; i++) { int a0 = b + i, a1 = b + (i + 1) % 4, c0 = b + 4 + i, c1 = b + 4 + (i + 1) % 4; t.AddRange(new[] { a0, a1, c1, a0, c1, c0 }); }
            var m = new Mesh { name = "TDU2_Cone" };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        public void Clear()
        {
            foreach (var g in _owned) if (g != null) UnityUtil.SafeDestroy(g);
            _owned.Clear();
            foreach (var h in _scenes) { if (h.Root != null) UnityUtil.SafeDestroy(h.Root); h.Res?.Destroy(); }
            _scenes.Clear();
            if (Root != null) UnityUtil.SafeDestroy(Root);
            Root = null; Count = 0;
        }
    }

    /// <summary>Meldet Treffer des Spielerautos (Layer 2, TDU2Vehicle) an <see cref="RaceProps"/>.</summary>
    public sealed class RacePropHit : MonoBehaviour
    {
        public RaceProps Owner;
        float _last = -10f;

        void OnCollisionEnter(Collision c)
        {
            if (Owner == null || c.rigidbody == null) return;
            if (c.rigidbody.GetComponent<Vehicles.TDU2Vehicle>() == null) return;
            if (Time.time - _last < 1f) return;
            _last = Time.time;
            Owner.ReportHit(gameObject, c.relativeVelocity.magnitude);
        }
    }

    /// <summary>
    /// Lauflicht der Fahrbahnpfeile (Trajectory): eine Helligkeitswelle läuft in Fahrtrichtung über die Pfeile
    /// (die Textur hat keine .uva – im Spiel wird die Instanzfarbe animiert). [vermutet: Geschwindigkeit/Abstand]
    /// </summary>
    public sealed class RaceTrajectoryWave : MonoBehaviour
    {
        public readonly List<Renderer[]> Marks = new List<Renderer[]>();
        public float Spacing = 5f, Speed = 18f, WaveLength = 30f;
        public Color Tint = new Color(1f, 1f, 1f, 1f);
        MaterialPropertyBlock _mpb;

        void Update()
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            float head = Time.time * Speed;
            for (int i = 0; i < Marks.Count; i++)
            {
                float x = (i * Spacing - head) / Mathf.Max(1f, WaveLength);
                float k = Mathf.Pow(0.5f + 0.5f * Mathf.Cos(x * Mathf.PI * 2f), 6f);
                _mpb.SetColor("_Color", new Color(1f, 1f, 1f, 0.3f + 0.7f * k));
                foreach (var r in Marks[i]) if (r != null) r.SetPropertyBlock(_mpb);
            }
        }
    }
}
