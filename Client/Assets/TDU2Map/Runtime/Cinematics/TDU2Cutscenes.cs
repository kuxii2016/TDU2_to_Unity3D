using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Formats;
using TDU2.IO;
using TDU2.Story;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Cinematics
{
    // =====================================================================================================================
    /// <summary>Wo die Szene steht.</summary>
    public enum CutscenePlace
    {
        /// <summary>Exterior-Bank: nächster Spot mit diesem Code; Story-Bank: ihr Story-Spot; sonst vor dem Spieler.</summary>
        Auto,
        /// <summary>Am Spot <see cref="CutsceneOptions.Spot"/> (voller Spotname oder Spot-Code, nächster zum Spieler).</summary>
        Spot,
        /// <summary>Vor dem Spieler / der Kamera auf dem Boden.</summary>
        Here,
        /// <summary>Hoch über der Insel (geschlossener Raum), wie Garage/Interieur-Kinos.</summary>
        Sky,
    }

    /// <summary>Einstellungen für <see cref="TDU2Cutscenes.Play"/>.</summary>
    public sealed class CutsceneOptions
    {
        /// <summary>Das aktuelle Auto des Spielers als PLAYER_CAR (bzw. <see cref="CarEntity"/>) in die Szene setzen.</summary>
        public bool LoadPlayerCar = true;
        /// <summary>Den Avatar des Spielers (eigenes Aussehen) als "Avatar" in die Szene setzen.</summary>
        public bool LoadPlayerAvatar = true;
        /// <summary>Mitspieler während der Szene anzeigen.</summary>
        public bool ShowOtherPlayers = false;
        /// <summary>Verkehr während der Szene weiterlaufen lassen.</summary>
        public bool ShowTraffic = false;
        /// <summary>Spielerauto während der Szene entfernen (Steuerung aus) und danach an derselben Stelle wieder hinstellen.</summary>
        public bool ParkPlayer = true;
        /// <summary>Überspringen mit Enter/Leertaste/Escape (bzw. Spiel-Taste "Überspringen").</summary>
        public bool Skippable = true;
        /// <summary>Kino in Schleife (nur Ende über Skip/Stop).</summary>
        public bool Loop = false;
        /// <summary>Szenen-Modelle der Bank bauen (Kulisse). Aus = nur Darsteller/Kamera.</summary>
        public bool LoadSets = true;
        public CutscenePlace Place = CutscenePlace.Auto;
        /// <summary>Spot für <see cref="CutscenePlace.Spot"/> (oder Auto mit festem Spot).</summary>
        public string Spot;
        /// <summary>Entitätsname des Autos im Kino (Entity__CinCar).</summary>
        public string CarEntity = "PLAYER_CAR";
        /// <summary>Weitere Szenen-Banken im selben Rahmen (z.B. "interior/ich_ib.bnk").</summary>
        public string[] ExtraBanks;
        /// <summary>Am Ende aufgerufen: true = normal zu Ende gespielt, false = übersprungen/abgebrochen/Fehler.</summary>
        public Action<bool> OnFinished;
    }

    // =====================================================================================================================
    /// <summary>
    /// Kinos abspielen – von überall im Code:
    /// <code>
    ///   TDU2Cutscenes.Play("exterior/ecw_a:C_INTRO_E_1");                       // Bank + Kino
    ///   TDU2Cutscenes.Play("new_intro", new CutsceneOptions { LoadPlayerCar = false });   // Bank-Name: erstes Kino mit Kamera
    ///   TDU2Cutscenes.Play("C_MEETING_TESS", new CutsceneOptions { ShowOtherPlayers = true, OnFinished = ok => ... });
    /// </code>
    /// Kennung: "&lt;bank&gt;[:&lt;kino&gt;]" mit bank = Pfad relativ zu Bnk ("scenarics/cinematics/intro/new_intro.bnk"),
    /// "exterior/&lt;stem&gt;" (Exterior der aktuellen Insel) oder nur der Dateiname ("new_intro"); ohne Bank sucht ein Kinoname
    /// in allen Kino-Banken (.cin "..._M_&lt;Name&gt;_...") – Ergebnis wird gemerkt. Werte für alle Kinos zeigt der Editor
    /// "TDU2/Cutscene Browser".
    /// </summary>
    public static class TDU2Cutscenes
    {
        /// <summary>Laufender Player (oder null).</summary>
        public static TDU2CutscenePlayer Current { get; internal set; }
        public static bool IsPlaying => Current != null;

        static readonly Dictionary<string, string> _byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, (string bank, string cin)> _byCin = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        static string _indexedFor;

        /// <summary>Kino starten. Ein laufendes wird vorher beendet. null = Fahrzeug-Manager fehlt.</summary>
        public static TDU2CutscenePlayer Play(string id, CutsceneOptions options = null)
        {
#if UNITY_2023_1_OR_NEWER
            var mgr = UnityEngine.Object.FindFirstObjectByType<TDU2VehicleManager>();
#else
            var mgr = UnityEngine.Object.FindObjectOfType<TDU2VehicleManager>();
#endif
            return Play(mgr, id, options);
        }

        public static TDU2CutscenePlayer Play(TDU2VehicleManager mgr, string id, CutsceneOptions options = null)
        {
            if (mgr == null || string.IsNullOrEmpty(id)) { DebugManager.Warning("[TDU2] Cutscene: kein Fahrzeug-Manager / keine Kennung"); return null; }
            Stop();
            var go = new GameObject("TDU2 Cutscene " + id);
            var p = go.AddComponent<TDU2CutscenePlayer>();
            Current = p;
            p.Begin(mgr, id, options ?? new CutsceneOptions());
            return p;
        }

        /// <summary>Laufendes Kino abbrechen (OnFinished(false)).</summary>
        public static void Stop() { if (Current != null) Current.Finish(false); }

        /// <summary>Kennung in Bankpfad (voll) und Kinoname (null = erstes mit Kamera) zerlegen. Läuft auch in Threads.</summary>
        public static bool Resolve(string bnkDir, string exteriorDir, string id, out string bankPath, out string cin)
        {
            bankPath = null; cin = null;
            if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(id)) return false;
            string bankPart = id, cinPart = null;
            int c = id.LastIndexOf(':');
            if (c > 1) { bankPart = id.Substring(0, c); cinPart = id.Substring(c + 1); }   // c > 1: "E:\..." nicht zerlegen
            bankPath = FindBank(bnkDir, exteriorDir, bankPart);
            if (bankPath != null) { cin = string.IsNullOrEmpty(cinPart) ? null : cinPart; return true; }
            if (cinPart == null && FindCinematic(bnkDir, exteriorDir, id, out bankPath)) { cin = id; return true; }
            return false;
        }

        static string FindBank(string bnkDir, string exteriorDir, string s)
        {
            if (File.Exists(s)) return s;
            string rel = s.Replace('\\', '/');
            if (!rel.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) rel += ".bnk";
            var full = Path.Combine(bnkDir, rel);
            if (File.Exists(full)) return full;
            if (rel.StartsWith("exterior/", StringComparison.OrdinalIgnoreCase) && exteriorDir != null)
            {
                full = Path.Combine(exteriorDir, rel.Substring(9));
                if (File.Exists(full)) return full;
            }
            EnsureIndex(bnkDir, exteriorDir);
            lock (_byStem) return _byStem.TryGetValue(Path.GetFileNameWithoutExtension(rel), out var p) ? p : null;
        }

        static void EnsureIndex(string bnkDir, string exteriorDir)
        {
            string key = bnkDir + "|" + exteriorDir;
            lock (_byStem)
            {
                if (_indexedFor == key) return;
                _indexedFor = key; _byStem.Clear(); _byCin.Clear();
                foreach (var f in CinematicBankCandidates(bnkDir, exteriorDir))
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!_byStem.ContainsKey(stem)) _byStem[stem] = f;
                }
            }
        }

        /// <summary>Alle Banken, die Kinos enthalten können: scenarics/**, interior/*, Exteriors der Insel (ohne _low/__xx).</summary>
        public static List<string> CinematicBankCandidates(string bnkDir, string exteriorDir)
        {
            var files = new List<string>();
            try { var sc = Path.Combine(bnkDir, "scenarics"); if (Directory.Exists(sc)) files.AddRange(Directory.GetFiles(sc, "*.bnk", SearchOption.AllDirectories)); } catch (Exception) { }
            try { var it = Path.Combine(bnkDir, "interior"); if (Directory.Exists(it)) files.AddRange(Directory.GetFiles(it, "*.bnk")); } catch (Exception) { }
            try { if (exteriorDir != null && Directory.Exists(exteriorDir)) files.AddRange(Directory.GetFiles(exteriorDir, "*.bnk")); } catch (Exception) { }
            files.RemoveAll(f => { var n = Path.GetFileNameWithoutExtension(f); return n.Contains("__") || n.EndsWith("_low", StringComparison.OrdinalIgnoreCase); });
            return files;
        }

        /// <summary>Kinoname in allen Kino-Banken suchen (Dateiname der .cin: "..._M_&lt;Name ohne C_&gt;_...").</summary>
        static bool FindCinematic(string bnkDir, string exteriorDir, string name, out string bank)
        {
            EnsureIndex(bnkDir, exteriorDir);
            lock (_byStem) if (_byCin.TryGetValue(name, out var hit)) { bank = hit.bank; return true; }
            string key = "_M_" + (name.StartsWith("C_", StringComparison.OrdinalIgnoreCase) ? name.Substring(2) : name) + "_";
            foreach (var f in CinematicBankCandidates(bnkDir, exteriorDir))
            {
                try
                {
                    var b = BnkArchive.Open(f);
                    foreach (var e in b.Entries)
                        if (e.Extension == ".cin" && e.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            lock (_byStem) _byCin[name] = (f, name);
                            bank = f; return true;
                        }
                }
                catch (Exception) { }
            }
            bank = null;
            return false;
        }

        /// <summary>Story-Banken und der Spot, an dem das Spiel sie spielt (Ibiza).</summary>
        internal static readonly Dictionary<string, (string spot, bool closed, float alt, bool hide, string extra)> KnownSpots =
            new Dictionary<string, (string, bool, float, bool, string)>(StringComparer.OrdinalIgnoreCase)
        {
            { "new_intro", ("EPH_L3DI_5067I", false, 0f, true, null) },
            { "garage", ("EPH_L3DI_5067I", true, 1500f, false, null) },
            { "meeting_tess", ("E_HOTELA_1266I", false, 0f, false, null) },
            { "solar_crown_ext", ("ECH_IA_1243I", false, 0f, false, null) },
            { "solar_crown_int", ("ECH_IA_1243I", true, 0f, false, "interior/ich_ib.bnk") },
            { "pack_events13", ("ECH_IA_1243I", false, 0f, false, null) },
        };
    }

    // =====================================================================================================================
    /// <summary>
    /// Spielt ein Kino über <see cref="TDU2StoryScene"/>: Bank laden, Darsteller besetzen (Avatar, Spielerauto), Kino abspielen,
    /// am Ende alles aufräumen (Auto zurück, Mitspieler/Verkehr wie vorher) und <see cref="CutsceneOptions.OnFinished"/> melden.
    /// Angelegt von <see cref="TDU2Cutscenes.Play(string, CutsceneOptions)"/>.
    /// </summary>
    [DefaultExecutionOrder(1050)]
    public sealed class TDU2CutscenePlayer : MonoBehaviour
    {
        public string Id { get; private set; }
        public string BankPath { get; private set; }
        public string Cinematic { get; private set; }
        public CutsceneOptions Options { get; private set; }
        public TDU2StoryScene Scene { get; private set; }
        public string Status { get; private set; } = "";
        public float Time => Scene != null ? Scene.MainTime : 0f;
        public float Duration => Scene != null ? Scene.MainDuration : 0f;

        TDU2VehicleManager _mgr;
        Task<(string bank, string cin)> _resolve;
        Task<CarAssets> _carTask; CarPhysics _car; GameObject _carGo; CarModel _carModel;
        bool _parked, _hideBefore, _trafficBefore, _cast, _started, _done, _waitCar, _envSet;
        TDU2.Traffic.TDU2Traffic _traffic;
        float _startT;

        internal void Begin(TDU2VehicleManager mgr, string id, CutsceneOptions o)
        {
            _mgr = mgr; Id = id; Options = o;
            _car = mgr.Current != null ? mgr.Current.Car : null;
            if (_car == null)
            {
                var save = Online.TDU2Session.Save;
                if (save != null && !string.IsNullOrEmpty(save.currentCar)) _car = mgr.FindCar(save.currentCar);
            }
            string bnk = mgr.BnkDir, ext = mgr.Streamer?.Index?.ExteriorDir;
            Status = "suche " + id;
            _resolve = Task.Run(() => TDU2Cutscenes.Resolve(bnk, ext, id, out var b, out var c) ? (b, c) : ((string)null, (string)null));
        }

        void StartScene(string bank, string cin)
        {
            BankPath = bank; Cinematic = cin;
            var o = Options;
            // Umgebung
            _envSet = true;
            _hideBefore = Online.TDU2Multiplayer.HideRemotes;
            if (!o.ShowOtherPlayers) Online.TDU2Multiplayer.HideRemotes = true;
#if UNITY_2023_1_OR_NEWER
            _traffic = UnityEngine.Object.FindFirstObjectByType<TDU2.Traffic.TDU2Traffic>();
#else
            _traffic = UnityEngine.Object.FindObjectOfType<TDU2.Traffic.TDU2Traffic>();
#endif
            if (_traffic != null) { _trafficBefore = _traffic.EnableTraffic; if (!o.ShowTraffic) _traffic.EnableTraffic = false; }
            var pos = PlayerPos(out var fwd);
            if (o.ParkPlayer) { TDU2DebugStage.Enter(_mgr); _parked = true; }

            var setup = new TDU2StoryScene.Setup { Bank = bank, LoadUi = false, IntroUi = false, LoadSets = o.LoadSets, ExtraBanks = o.ExtraBanks };
            Place(setup, bank, pos, fwd);
            Scene = TDU2StoryScene.Create(_mgr, setup);
            _startT = UnityEngine.Time.unscaledTime;
            Status = "lade " + Path.GetFileNameWithoutExtension(bank);
            if (o.LoadPlayerCar && _car != null)
            {
                int color = 0, rim = 0, interior = 0;
                if (TDU2CarChoices.TryGet(_car.FileName, out var ch)) { color = ch.Color; rim = ch.Rim; interior = ch.Interior; }
                string bnk = _mgr.BnkDir; var db = _mgr.Database; var car = _car;
                _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, false, rim, interior));
                _waitCar = true;
            }
            DebugManager.Info($"[TDU2] Cutscene {Id}: Bank {TDU2DebugStage.Rel(_mgr.BnkDir, bank)}, Kino {cin ?? "(erstes mit Kamera)"}");
        }

        Vector3 PlayerPos(out Vector3 fwd)
        {
            Transform t = _mgr.Current != null ? _mgr.Current.transform : Camera.main != null ? Camera.main.transform : null;
            fwd = t != null ? t.forward : Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            return t != null ? t.position : Vector3.zero;
        }

        void Place(TDU2StoryScene.Setup setup, string bank, Vector3 pos, Vector3 fwd)
        {
            var o = Options; var st = _mgr.Streamer;
            string stem = Path.GetFileNameWithoutExtension(bank);
            var place = o.Place;
            if (place == CutscenePlace.Spot || (place == CutscenePlace.Auto && !string.IsNullOrEmpty(o.Spot)))
            {
                var s = FindSpot(o.Spot, pos);
                if (s != null) { setup.SpotRef = s; setup.Spot = s.Code; setup.HideSpotInterior = true; return; }
                place = CutscenePlace.Auto;
            }
            if (place == CutscenePlace.Auto)
            {
                bool exterior = st?.Index?.ExteriorDir != null && bank.Replace('\\', '/').IndexOf("/exterior/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (exterior) { var s = FindSpot(stem, pos); if (s != null) { setup.SpotRef = s; setup.Spot = s.Code; setup.HideSpotInterior = true; return; } }
                if (TDU2Cutscenes.KnownSpots.TryGetValue(stem, out var k) && TDU2GamePC.FindSpot(st?.Spots?.Spots, k.spot) != null)
                {
                    setup.Spot = k.spot; setup.Closed = k.closed; setup.ClosedAltitude = k.alt > 0f ? k.alt : setup.ClosedAltitude; setup.HideSpotInterior = k.hide;
                    if (k.extra != null && setup.ExtraBanks == null) setup.ExtraBanks = new[] { k.extra };
                    return;
                }
                place = CutscenePlace.Here;
            }
            if (place == CutscenePlace.Here && st?.MapRoot != null)
            {
                var p = pos + fwd * 8f; p.y = _mgr.GroundHeight(p);
                setup.UseFrame = true;
                setup.FramePos = st.MapRoot.InverseTransformPoint(p);
                var d = st.MapRoot.InverseTransformDirection(fwd);
                setup.FrameHeading = Mathf.Atan2(d.x, d.z);
                return;
            }
            setup.Closed = true; setup.ClosedAltitude = 1500f;
        }

        /// <summary>Spot nach vollem Namen (z.B. "EPH_L3DI_5067I") oder Code (nächster zum Spieler, z.B. "ECW_A").</summary>
        Spot FindSpot(string name, Vector3 worldPos)
        {
            var st = _mgr.Streamer;
            if (string.IsNullOrEmpty(name) || st?.Spots == null || st.MapRoot == null) return null;
            var exact = TDU2GamePC.FindSpot(st.Spots.Spots, name);
            if (exact != null) return exact;
            var g = st.MapRoot.InverseTransformPoint(worldPos);
            Spot best = null; float bd = float.MaxValue;
            foreach (var s in st.Spots.Spots)
            {
                if (!string.Equals(s.Code, name, StringComparison.OrdinalIgnoreCase)) continue;
                float d = (s.Position - g).sqrMagnitude;
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        void Update()
        {
            if (_done) return;
            if (_resolve != null)
            {
                if (!_resolve.IsCompleted) return;
                var r = _resolve; _resolve = null;
                if (r.IsFaulted || r.Result.bank == null) { Fail("nicht gefunden: " + Id); return; }
                try { StartScene(r.Result.bank, r.Result.cin); }
                catch (Exception ex) { Fail("Start: " + ex.Message); return; }
            }
            if (Scene == null) return;
            if (Scene.Failed) { Fail("Szene: " + Scene.Error); return; }
            if (_carTask != null && _carTask.IsCompleted)
            {
                var t = _carTask; _carTask = null; _waitCar = false;
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                {
                    try
                    {
                        _carGo = new GameObject("Cutscene Car " + _car.ModelName);
                        _carModel = CarModel.Build(t.Result, _carGo.transform);
                        TDU2WheelSpin.Attach(_carGo, _carModel, t.Result);
                        try { TDU2StickerLibrary.Ensure(_mgr.BnkDir); TDU2LiveryView.ApplyOwn(_carModel, _car.FileName); } catch (Exception) { }
                        if (_cast && Scene.Loaded) Scene.SetCar(_carGo.transform, null, Options.CarEntity);
                    }
                    catch (Exception ex) { DebugManager.Warning("[TDU2] Cutscene: Auto: " + ex.Message); }
                }
            }
            if (!Scene.Loaded) return;
            if (!_cast)
            {
                _cast = true;
                if (Options.LoadPlayerAvatar)
                {
                    string cfg = Avatars.AvatarConfigs.Local != null && Avatars.AvatarConfigs.Local.Sex == 1 ? "AVT_Female_Intro" : "AVT_Male_Intro";
                    Scene.SetPlayerAvatar(cfg, Avatars.AvatarConfigs.Local);
                }
                if (_carGo != null) Scene.SetCar(_carGo.transform, null, Options.CarEntity);
            }
            // Auto erst abwarten (sonst springt es mitten im Kino hinein), höchstens 10 s
            if (!_started && _waitCar && UnityEngine.Time.unscaledTime - _startT < 10f) { Status = "lade Auto …"; return; }
            if (!_started)
            {
                _started = true;
                string name = Cinematic;
                if (string.IsNullOrEmpty(name) || Scene.Find(name) == null)
                {
                    string wanted = name;
                    name = null;
                    if (!string.IsNullOrEmpty(wanted))
                        foreach (var d in Scene.Definitions) if (d.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) { name = d.Name; break; }
                    if (name == null && string.IsNullOrEmpty(wanted))
                        foreach (var d in Scene.Definitions) if (!d.NoCamera) { name = d.Name; break; }
                }
                if (name == null || !Scene.PlayMain(name))
                { Fail("Kino nicht in der Bank: " + (Cinematic ?? "(keins mit Kamera)")); return; }
                Cinematic = name;
                Status = "spielt " + name;
            }
            if (Options.Skippable && UnityEngine.Time.unscaledTime - _startT > 0.5f &&
                (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space) || TDU2Story.SkipPressed()))
            { Finish(false); return; }
            Status = $"spielt {Cinematic}  {Scene.MainTime:0.0} / {Scene.MainDuration:0.0} s";
            if (Scene.MainEnded)
            {
                if (Options.Loop) Scene.PlayMain(Cinematic);
                else Finish(true);
            }
        }

        void Fail(string why)
        {
            DebugManager.Warning("[TDU2] Cutscene " + Id + ": " + why);
            Status = why;
            Finish(false);
        }

        /// <summary>Beenden und aufräumen (true = normal zu Ende).</summary>
        public void Finish(bool completed)
        {
            if (_done) return;
            _done = true;
            try { if (Scene != null) Scene.Dispose(); } catch (Exception) { }
            Scene = null;
            if (_carGo != null) Destroy(_carGo); _carGo = null;
            try { _carModel?.Destroy(); } catch (Exception) { }
            _carModel = null; _carTask = null;
            if (_parked) { _parked = false; TDU2DebugStage.Exit(); }
            if (_envSet)
            {
                Online.TDU2Multiplayer.HideRemotes = _hideBefore;
                if (_traffic != null) _traffic.EnableTraffic = _trafficBefore;
            }
            if (TDU2Cutscenes.Current == this) TDU2Cutscenes.Current = null;
            var cb = Options?.OnFinished;
            try { cb?.Invoke(completed); } catch (Exception ex) { DebugManager.Error("[TDU2] Cutscene OnFinished: " + ex); }
            Destroy(gameObject);
        }

        void OnDestroy() { if (!_done) Finish(false); }
    }

    // =====================================================================================================================
    /// <summary>
    /// Kino-Infos einer Bank ohne sie zu bauen (Editor-Browser, Logs): Entity__Cinematic mit Dauer, Eigenschaften, Szenen,
    /// Shots/Kameras, Darstellern, Nachrichten und Effekt-/Musik-Ereignissen sowie die Figuren/Autos/Kameras der Bank.
    /// </summary>
    public sealed class TDU2CutsceneInfo
    {
        public sealed class Cin
        {
            public string Name, File, ShadowConfig;
            public uint Props;
            public float Duration;
            public bool Loop => (Props & 0x2) != 0;
            public bool NoCamera => (Props & 0x10) != 0;
            public int Shots;
            public readonly List<string> Scenes = new List<string>();
            public readonly List<string> Cameras = new List<string>();
            public readonly List<string> Actors = new List<string>();
            public readonly List<(float time, string text)> Messages = new List<(float, string)>();
            public readonly List<(float time, string text)> Fx = new List<(float, string)>();
            public bool UsesPlayerCar, UsesAvatar;
        }

        public string Bank;
        public readonly List<Cin> Cinematics = new List<Cin>();
        public readonly List<(string name, string config)> Pnjs = new List<(string, string)>();
        public readonly List<string> CinCars = new List<string>();
        public readonly List<string> CameraEntities = new List<string>();
        public readonly List<string> Volumes = new List<string>();
        public string Error;

        public static TDU2CutsceneInfo Read(string bankPath)
        {
            var info = new TDU2CutsceneInfo { Bank = bankPath };
            try
            {
                var bank = BnkArchive.Open(bankPath);
                var ent = bank.Find("entities.xmb");
                if (ent == null) { info.Error = "entities.xmb fehlt"; return info; }
                var root = XmbFile.Parse(bank.Read(ent));
                var byRsc = new Dictionary<ulong, BnkEntry>();
                var hcEntry = bank.Find("hashcodes.xmb");
                if (hcEntry != null)
                {
                    var groups = XmbFile.Parse(bank.Read(hcEntry)).Get("___CmdSaveFileInfoArray");
                    if (groups != null)
                        foreach (var g in groups.Items)
                        {
                            var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                            if (idx == null || refs == null) continue;
                            int n = Math.Min(idx.Items.Count, refs.Items.Count);
                            for (int i = 0; i < n; i++)
                            {
                                int fi = idx.Items[i].AsInt(-1);
                                if (fi < 0) continue;
                                BnkEntry be = null;
                                foreach (var x in bank.Entries) if (x.Index == fi) { be = x; break; }
                                if (be == null && fi < bank.Entries.Count) be = bank.Entries[fi];
                                if (be != null) byRsc[refs.Items[i].AsULong()] = be;
                            }
                        }
                }
                var et = root.Get("ExportType_Entity");
                if (et != null)
                    foreach (var arr in et.Children)
                    {
                        if (!arr.IsArray) continue;
                        foreach (var it in arr.Items)
                        {
                            string name = it.S("__Name", "");
                            switch (arr.Name)
                            {
                                case "ArrayOf_Entity__Cinematic": info.Cinematics.Add(ReadCin(bank, byRsc, it)); break;
                                case "ArrayOf_Entity__PNJ": info.Pnjs.Add((name, it.S("ConfigName", ""))); break;
                                case "ArrayOf_Entity__CinCar": info.CinCars.Add(name); break;
                                case "ArrayOf_Entity__Camera": info.CameraEntities.Add(name); break;
                            }
                        }
                    }
                var vt = root.Get("ExportType_Volume")?.Get("ArrayOf_Entity__Volume");
                if (vt != null) foreach (var it in vt.Items) info.Volumes.Add(it.S("__Name", ""));
                foreach (var c in info.Cinematics)
                {
                    foreach (var a in c.Actors)
                    {
                        if (string.Equals(a, "Avatar", StringComparison.OrdinalIgnoreCase)) c.UsesAvatar = true;
                        if (a.IndexOf("PLAYER_CAR", StringComparison.OrdinalIgnoreCase) >= 0) c.UsesPlayerCar = true;
                    }
                }
            }
            catch (Exception ex) { info.Error = ex.Message; }
            return info;
        }

        static Cin ReadCin(BnkArchive bank, Dictionary<ulong, BnkEntry> byRsc, XmbNode it)
        {
            var c = new Cin { Name = it.S("__Name", ""), ShadowConfig = it.S("DefaultShadowConfig", "") };
            var props = it.Get("CinProps");
            if (props != null && props.Items.Count > 0) c.Props = (uint)props.Items[0].AsInt();
            BnkEntry cin = null;
            var files = it.Get("CinFile");
            if (files != null)
                foreach (var f in files.Items)
                    if (f.I("rscSubType") == 16 && byRsc.TryGetValue(f.U("rsc"), out var e) && e.Extension == ".cin") { cin = e; break; }
            if (cin == null)
            {
                string key = "_M_" + (c.Name.StartsWith("C_") ? c.Name.Substring(2) : c.Name) + "_";
                foreach (var e in bank.All(".cin")) if (e.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { cin = e; break; }
            }
            if (cin == null) return c;
            c.File = cin.Name;
            var cd = CinData.Parse(bank.Read(cin), cin.Name);
            if (cd == null) return c;
            c.Duration = cd.Duration;
            c.Shots = cd.Shots.Count;
            foreach (var m in cd.Messages) { var t = cd.MessageText(m); if (!string.IsNullOrEmpty(t)) c.Messages.Add((m.Time, t)); }
            foreach (var m in cd.FxEvents) { var t = cd.MessageText(m); c.Fx.Add((m.Time, string.IsNullOrEmpty(t) ? "#" + m.Reference : t)); }
            var actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < cd.SceneReferences.Count; s++)
            {
                string file = cd.SceneFile(s);
                c.Scenes.Add(file ?? "?");
                var se = file != null ? bank.Find(file + ".sce") : null;
                var sc = se != null ? CinScene.Parse(bank.Read(se), se.Name) : null;
                if (sc == null) continue;
                foreach (var o in sc.Objects)
                {
                    if (string.IsNullOrEmpty(o.Name)) continue;
                    if (o.IsCamera) cams.Add(o.Name); else actors.Add(o.Name);
                }
                foreach (var r in sc.References) if (!string.IsNullOrEmpty(r.Name)) actors.Add(r.Name);
            }
            c.Actors.AddRange(actors); c.Actors.Sort(StringComparer.OrdinalIgnoreCase);
            c.Cameras.AddRange(cams); c.Cameras.Sort(StringComparer.OrdinalIgnoreCase);
            return c;
        }

        /// <summary>Lesbarer Bericht (Editor "Kopieren", Logs).</summary>
        public string Report(string bnkDir = null)
        {
            var sb = new System.Text.StringBuilder();
            string rel = bnkDir != null ? TDU2DebugStage.Rel(bnkDir, Bank) : Bank;
            sb.AppendLine("Bank: " + rel);
            if (Error != null) sb.AppendLine("Fehler: " + Error);
            foreach (var c in Cinematics)
            {
                sb.AppendLine($"- {c.Name}  {c.Duration:0.00} s  Shots {c.Shots}  Props 0x{c.Props:X} {(c.Loop ? "Loop " : "")}{(c.NoCamera ? "OhneKamera " : "")}  Datei {c.File}");
                if (c.Scenes.Count > 0) sb.AppendLine("    Szenen: " + string.Join(", ", c.Scenes));
                if (c.Cameras.Count > 0) sb.AppendLine("    Kameras: " + string.Join(", ", c.Cameras));
                if (c.Actors.Count > 0) sb.AppendLine("    Darsteller: " + string.Join(", ", c.Actors));
                foreach (var m in c.Messages) sb.AppendLine($"    Nachricht {m.time:0.00} s: {m.text}");
                foreach (var m in c.Fx) sb.AppendLine($"    Fx {m.time:0.00} s: {m.text}");
                sb.AppendLine($"    Aufruf: TDU2Cutscenes.Play(\"{Path.GetFileNameWithoutExtension(Bank)}:{c.Name}\", new CutsceneOptions {{ LoadPlayerCar = {(c.UsesPlayerCar ? "true" : "false")}, LoadPlayerAvatar = {(c.UsesAvatar ? "true" : "false")} }});");
            }
            if (Pnjs.Count > 0) { sb.Append("Figuren:"); foreach (var p in Pnjs) sb.Append($" {p.name}({p.config})"); sb.AppendLine(); }
            if (CinCars.Count > 0) sb.AppendLine("Kino-Autos: " + string.Join(", ", CinCars));
            if (CameraEntities.Count > 0) sb.AppendLine("Kamera-Entitäten: " + string.Join(", ", CameraEntities));
            if (Volumes.Count > 0) sb.AppendLine("Volumen: " + string.Join(", ", Volumes));
            return sb.ToString();
        }
    }
}
