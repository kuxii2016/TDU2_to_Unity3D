using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Avatars;
using TDU2.Cinematics;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.Interior;
using TDU2.IO;
using TDU2.Vehicles;
using TDU2.World;
using UnityEngine;

namespace TDU2.Story
{
    /// <summary>
    /// Szene mit Echtzeit-Kinos einer Story-Bank (Unity-Port von OpenTDU2 IntroCinematic, docs/re/intro_spec.md):
    ///   - Bank (z.B. scenarics/cinematics/intro/new_intro.bnk, garage.bnk, meeting_tess.bnk) am Haus-/Spot-Platz der Insel
    ///     (Spot-Position + Drehung um Y); "Closed" = geschlossener Raum (Garage) hoch über der Insel, ohne Welt.
    ///   - entities.xmb: Entity__Cinematic (CinFile -> .cin über hashcodes.xmb, CinProps, DefaultShadowConfig), Entity__PNJ
    ///     (DBPNJ ConfigName), Entity__Camera (Laufmodus-Start), Entity__PosCam / Entity__CinCar (Auto), Volumes und
    ///     FpSelectables (Garage-Tor, Auto), Scenery/Actors (über BankModel wie die Interieurs).
    ///   - Ein Haupt-Kino (Kamera, Wetter, Flash-Nachrichten, Ende -> <see cref="TakeEndEvents"/>) und Hintergrund-Kinos
    ///     (CinProps Bit 4: keine Kamera, nur Darsteller), wie AVATAR_CHOICE / FIRST_WM.
    ///   - Darsteller: Position/Heading-Spuren, Animation-Spur (Schlüssel k: Clip = REFS[f0], Clipzeit = f2 - f7 + (t - T_k),
    ///     Überblendung f7 s vom vorigen Schlüssel; Clips laufen in Schleife).
    ///   - Flash: frontend/&lt;res&gt;/intro.bnk (Credits "flsh:", Avatar-Wahl, Laufmodus) und general.bnk (pop_goal, pop_tutorial).
    ///   - Töne: .cin-Fx-Ereignisse, .sce-Fx-Spuren (Stimmen), Entity__Fx-Emitter (snd:music, snd:timestop) über
    ///     <see cref="TDU2CinematicAudio"/> (Sprachbank &lt;bank&gt;__&lt;sprache&gt;.bnk).
    ///   - Requisiten (Entity__Actor): Platzierung x Wurzel^-1 (die Entität ersetzt den Wurzelknoten, wie OpenTDU2), von den
    ///     Kinos bewegt (gleiche Entität in der .sce); Entity__Actor__Mesh/__Interior im Bankrahmen.
    /// Nicht übernommen: FaceFX/Blickziele, Stil-Animationen (additionalsStyleAnims), Lichter.
    /// </summary>
    // nach allen anderen Kamera-Skripten (TDU2VehicleCamera u.a. schreiben ebenfalls in LateUpdate -> sonst Kamera-Zittern)
    [DefaultExecutionOrder(1000)]
    public sealed class TDU2StoryScene : MonoBehaviour
    {
        public sealed class Setup
        {
            public string Bank;              // relativ zu Bnk, z.B. "scenarics/cinematics/intro/new_intro.bnk"
            public string Spot;              // voller Spotname, z.B. "EPH_L3DI_5067I" (null = Inselursprung)
            public TDU2.Formats.Spot SpotRef;
            /// <summary>Freier Rahmen (Spielraum) statt Spot – Kino-Tester im Entwicklermenü ("hier abspielen").</summary>
            public bool UseFrame; public Vector3 FramePos; public float FrameHeading;          // genau dieser Spot (hat Vorrang vor Spot – z.B. Waschanlagen: mehrere Spots ECW_A)
            public bool Closed;              // geschlossener Raum: hoch über die Insel legen
            public float ClosedAltitude = 1500f;
            public bool LoadSets = true;     // Szenen-Modelle der Bank bauen
            public bool LoadUi = true;       // general.vmf (Ziel/Tipp) und, mit IntroUi, intro.vmf
            public bool IntroUi = true;      // intro.vmf (Credits, Avatar-Wahl, Laufmodus-Cursor)
            public string[] ExtraBanks;      // weitere Szenen-Banken im selben Rahmen (z.B. "interior/ich_ib.bnk" für Solar Crown innen)
            public bool HideSpotInterior;    // Interieur-Platzhalter (Low-Poly-LODs) des gestreamten Spot-Exteriors ausblenden (die Bank bringt das Interieur mit)
            /// <summary>Rahmen = Auto-Punkt des Spots (Position2/Heading2) statt Position/Richtung. Meeting Tess: mit Position/Richtung
            /// läge der Ankerpunkt der Darsteller (PLAYER_CAR/Avatar/Tess bei -8, -1, -36) hinter dem Hotel am Pool; mit Position2/Heading2
            /// steht das Auto 11 m vor dem Start der Mission tesseven.ig2 und schaut wie deren erster Checkpoint die Straße entlang.</summary>
            public bool ExitFrame;
        }

        public sealed class Definition
        {
            public string Name;
            public CinData Cin;
            public readonly List<CinScene> Scenes = new List<CinScene>();
            public uint CinProps;
            public string ShadowConfig;
            public bool Loop => (CinProps & 0x2) != 0;
            public bool NoCamera => (CinProps & 0x10) != 0;
        }

        sealed class Playing
        {
            public Definition Def;
            public float Time, Prev = -1f;
            public bool Loop, NoCamera, Ended;
            public readonly List<AudioSource> Voices = new List<AudioSource>();
        }

        /// <summary>Entity__Volume / FpSelectable (hauslokal).</summary>
        public sealed class Volume
        {
            public string Name = "";
            public int OfType, SelectableType = -1;
            public string SelectableString = "";
            public readonly float[,] M = new float[4, 4];   // volume_mat (D3D-Zeilen): hauslokal -> Einheitsvolumen
            public Vector3 Center;

            public Vector3 ToUnit(Vector3 p) => new Vector3(p.x * M[0, 0] + p.y * M[1, 0] + p.z * M[2, 0] + M[3, 0],
                                                             p.x * M[0, 1] + p.y * M[1, 1] + p.z * M[2, 1] + M[3, 1],
                                                             p.x * M[0, 2] + p.y * M[1, 2] + p.z * M[2, 2] + M[3, 2]);
            public Vector3 ToUnitDir(Vector3 d) => new Vector3(d.x * M[0, 0] + d.y * M[1, 0] + d.z * M[2, 0],
                                                                d.x * M[0, 1] + d.y * M[1, 1] + d.z * M[2, 1],
                                                                d.x * M[0, 2] + d.y * M[1, 2] + d.z * M[2, 2]);

            /// <summary>Punkt (hauslokal) im Volumen (FUN_00b40e50: 1 Kugel, 2 Box, 3 Zylinder).</summary>
            public bool Contains(Vector3 p)
            {
                var u = ToUnit(p);
                switch (OfType)
                {
                    case 1: return Vector3.Dot(u, u) <= 1f;
                    case 2: return Mathf.Abs(u.x) <= 0.5f && Mathf.Abs(u.y) <= 0.5f && Mathf.Abs(u.z) <= 0.5f;
                    case 3: return Mathf.Abs(u.y) <= 0.5f && u.x * u.x + u.z * u.z <= 1f;
                    default: return false;
                }
            }

            /// <summary>Strahl (hauslokal) gegen die Einheitsbox.</summary>
            public bool Raycast(Vector3 origin, Vector3 dir, float maxDist, out float tEnter, out float tExit)
            {
                var o = ToUnit(origin); var d = ToUnitDir(dir);
                float tMin = 0f, tMax = maxDist; tEnter = tExit = 0f;
                for (int a = 0; a < 3; a++)
                {
                    float oa = o[a], da = d[a];
                    if (Mathf.Abs(da) < 1e-8f) { if (oa < -0.5f || oa > 0.5f) return false; continue; }
                    float t0 = (-0.5f - oa) / da, t1 = (0.5f - oa) / da;
                    if (t0 > t1) { var x = t0; t0 = t1; t1 = x; }
                    tMin = Mathf.Max(tMin, t0); tMax = Mathf.Min(tMax, t1);
                    if (tMin > tMax) return false;
                }
                tEnter = tMin; tExit = tMax;
                return true;
            }
        }

        sealed class Pnj
        {
            public string Name, Config;
            public ulong Hc;
            public Transform Root;
            public TDU2Avatar Avatar;
            public float DiagT; public int DiagN;
        }

        // ------------------------------------------------------------------ Zustand
        public TDU2VehicleManager Manager { get; private set; }
        public Setup Config { get; private set; }
        public bool Loaded { get; private set; }
        public bool Failed { get; private set; }
        public string Error { get; private set; }
        /// <summary>Szenen-Ursprung (Haus): hauslokal -> Welt.</summary>
        public Transform Holder { get; private set; }
        public BnkArchive Bank { get; private set; }
        public VmfPlayer Ui { get; private set; }
        public VmfPlayer General { get; private set; }
        public IReadOnlyList<Volume> Selectables => _selectables;
        public IReadOnlyList<Volume> Volumes => _volumes;
        public Spot HouseSpot { get; private set; }

        readonly List<Definition> _defs = new List<Definition>();
        /// <summary>Alle Kinos der Bank (Entity__Cinematic) – Kino-Tester.</summary>
        public IReadOnlyList<Definition> Definitions => _defs;
        /// <summary>Dauer des laufenden Haupt-Kinos (Kino-Tester; Name/Zeit: MainName/MainTime weiter unten).</summary>
        public float MainDuration => _main.Def?.Cin != null ? _main.Def.Cin.Duration : 0f;
        public int BackgroundCount => _backgrounds.Count;
        readonly Playing _main = new Playing();
        readonly List<Playing> _backgrounds = new List<Playing>();
        readonly List<string> _endEvents = new List<string>();
        readonly List<Volume> _volumes = new List<Volume>(), _selectables = new List<Volume>();
        readonly Dictionary<string, (Vector3 pos, Quaternion rot)> _cameras = new Dictionary<string, (Vector3, Quaternion)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (Vector3 pos, Quaternion rot)> _placements = new Dictionary<string, (Vector3, Quaternion)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ulong> _cinCarHc = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        readonly List<(string name, string cfg, ulong hc)> _pnjEntries = new List<(string, string, ulong)>();
        readonly List<Pnj> _pnjs = new List<Pnj>();
        Pnj _player;
        readonly Dictionary<string, AnmClip> _clips = new Dictionary<string, AnmClip>(StringComparer.OrdinalIgnoreCase);
        Task<(BankModel model, string error)> _task;
        readonly List<(string path, BankModel model)> _extraModels = new List<(string, BankModel)>();
        readonly List<TDU2MapStreamer.SceneHandle> _extraScenes = new List<TDU2MapStreamer.SceneHandle>();
        TDU2MapStreamer.SceneHandle _scene;
        TDU2WeatherData _weathers;
        string _weather = "", _modifier = "";
        Camera _cam;
        float _savedFov = -1f, _savedNear = -1f;
        bool _cameraOverride;
        GameObject _fade;
        Material _fadeMat;
        Color _ambientSaved; bool _ambientChanged;

        /// <summary>Auto-Modell der Szene (FIRST_WM: der California; Meeting Tess: CF_KCar) und seine Bindung.</summary>
        public Transform Car { get; private set; }
        string _carRest;
        ulong _carHc;
        string _carActorName;
        float _carGroundOffset, _carBottom; bool _carGrounded, _carGroundInit;

        /// <summary>Kino-Töne der Bank (null = keine).</summary>
        public TDU2CinematicAudio Audio { get; private set; }
        readonly List<AudioSource> _musicVoices = new List<AudioSource>();
        string _lang = "us";

        // Requisiten (Entity__Actor*): Bank-Eintrag der Szene, Platzierung, Entität, Array
        readonly List<(string dd, Matrix4x4 m, ulong hc, string array)> _propEntries = new List<(string, Matrix4x4, ulong, string)>();
        sealed class Prop { public Transform T; public ulong Hc; public Matrix4x4 Rest, RootInv; public bool Moved; }
        readonly List<Prop> _props = new List<Prop>();
        readonly List<GameObject> _hiddenExterior = new List<GameObject>();
        Bounds _sceneBounds; bool _haveSceneBounds;
        float _hideScanT = -1f, _hideScanUntil;

        // ------------------------------------------------------------------ Laden
        public static TDU2StoryScene Create(TDU2VehicleManager mgr, Setup setup)
        {
            var go = new GameObject("TDU2 Story " + Path.GetFileNameWithoutExtension(setup.Bank ?? "scene"));
            var s = go.AddComponent<TDU2StoryScene>();
            s.Manager = mgr; s.Config = setup;
            s.StartLoad();
            return s;
        }

        string BankPath(string rel) => string.IsNullOrEmpty(rel) ? null : Path.Combine(Manager.BnkDir, rel.Replace('\\', '/'));

        void StartLoad()
        {
            string path = BankPath(Config.Bank);
            if (path != null && !File.Exists(path)) { Fail("Bank fehlt: " + path); return; }   // ohne Bank: nur Rahmen + Oberfläche
            var st = Manager.Streamer;
            bool sets = Config.LoadSets;
            var extra = new List<string>();
            _lang = TDU2PauseMenu.SavedLanguage();
            string lang = _lang;
            if (Config.ExtraBanks != null) foreach (var e in Config.ExtraBanks) { var p = BankPath(e); if (p != null && File.Exists(p)) extra.Add(p); else DebugManager.Warning("[TDU2] Story: Bank fehlt: " + p); }
            _task = Task.Run(() =>
            {
                try
                {
                    BankModel model = null;
                    if (path != null)
                    {
                        var bank = BnkArchive.Open(path);
                        Bank = bank;
                        ParseEntities(bank);
                        LoadFaceSets(bank);
                        Audio = TDU2CinematicAudio.Load(path, bank, lang);
                    }
                    if (sets && path != null)
                    {
                        model = st.LoadSceneModel(path);
                        InteriorFx.Prepare(model.Bank);
                    }
                    foreach (var p in extra)
                    {
                        try { var m = st.LoadSceneModel(p); InteriorFx.Prepare(m.Bank); lock (_extraModels) _extraModels.Add((p, m)); }
                        catch (Exception ex) { DebugManager.Warning("[TDU2] Story: " + p + ": " + ex.Message); }
                    }
                    return (model, (string)null);
                }
                catch (Exception ex) { return ((BankModel)null, ex.Message); }
            });
        }

        void Fail(string msg)
        {
            Failed = true; Error = msg;
            DebugManager.Error("[TDU2] Story-Szene " + Config?.Bank + ": " + msg);
        }

        static Vector3 V3(XmbNode n, Vector3 def)
        {
            if (n == null) return def;
            return new Vector3(n.F("X", def.x), n.F("Y", def.y), n.F("Z", def.z));
        }

        /// <summary>orient_pos_scale: Position + Rotation (Unity: konjugiert, wie die .sce-Headings / BankModel).</summary>
        static (Vector3, Quaternion) Placement(XmbNode e)
        {
            var ops = e?.Get("orient_pos_scale");
            var p = V3(ops?.Get("pos"), Vector3.zero);
            var r = ops?.Get("rot");
            var q = r != null ? new Quaternion(r.F("X"), r.F("Y"), r.F("Z"), r.F("W", 1f)) : Quaternion.identity;
            return (p, CinIds.ToUnity(q));
        }

        void ParseEntities(BnkArchive bank)
        {
            var ent = bank.Find("entities.xmb");
            if (ent == null) throw new InvalidDataException("entities.xmb fehlt");
            var root = XmbFile.Parse(bank.Read(ent));
            // rsc-Hash -> Bankeintrag (hashcodes.xmb)
            var byRsc = new Dictionary<ulong, BnkEntry>();
            var hcEntry = bank.Find("hashcodes.xmb");
            if (hcEntry != null)
            {
                var hc = XmbFile.Parse(bank.Read(hcEntry));
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
            {
                foreach (var arr in et.Children)
                {
                    if (!arr.IsArray) continue;
                    bool actor = arr.Name.StartsWith("ArrayOf_Entity__Actor", StringComparison.Ordinal) && arr.Name.IndexOf("Avatar", StringComparison.OrdinalIgnoreCase) < 0;
                    foreach (var it in arr.Items)
                    {
                        string name = it.S("__Name", "");
                        if (actor)
                        {
                            // Szene (rscSubType 0) der Requisite -> gebaute Platzierung (BankModel: Holder "<3dd>#i")
                            var mesh = it.Get("mesh");
                            if (mesh != null)
                                foreach (var r in mesh.Items)
                                    if (r.I("rscSubType", -1) == 0 && byRsc.TryGetValue(r.U("rsc"), out var dd))
                                    {
                                        var ops = it.Get("orient_pos_scale");
                                        _propEntries.Add((dd.Name, ops != null ? BankModel.EntityMatrix(ops) : Matrix4x4.identity, it.U("__Hc"), arr.Name));
                                        break;
                                    }
                            continue;
                        }
                        switch (arr.Name)
                        {
                            case "ArrayOf_Entity__Cinematic": LoadDefinition(bank, byRsc, it); break;
                            case "ArrayOf_Entity__PNJ": _pnjEntries.Add((name, it.S("ConfigName", ""), it.U("__Hc"))); break;
                            case "ArrayOf_Entity__Camera": _cameras[name] = Placement(it); break;
                            case "ArrayOf_Entity__PosCam": _placements[name] = Placement(it); break;
                            case "ArrayOf_Entity__CinCar": _placements[name] = Placement(it); _cinCarHc[name] = it.U("__Hc"); break;
                            case "ArrayOf_Entity__Volume__FpSelectable":
                                {
                                    var v = ReadVolume(it);
                                    v.SelectableType = it.I("Type", -1);
                                    v.SelectableString = it.S("SelectableString", "");
                                    _selectables.Add(v);
                                    break;
                                }
                        }
                    }
                }
            }
            var vt = root.Get("ExportType_Volume")?.Get("ArrayOf_Entity__Volume");
            if (vt != null) foreach (var it in vt.Items) _volumes.Add(ReadVolume(it));
        }

        static Volume ReadVolume(XmbNode it)
        {
            var v = new Volume { Name = it.S("__Name", ""), OfType = it.I("OfType") };
            var m = it.Get("volume_mat");
            string[] rows = { "_0", "_1", "_2", "_3" }, cols = { "X", "Y", "Z", "W" };
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) v.M[r, c] = m?.Get(rows[r])?.F(cols[c]) ?? (r == c ? 1f : 0f);
            v.Center = V3(it.Get("orient_pos_scale")?.Get("pos"), Vector3.zero);
            return v;
        }

        void LoadDefinition(BnkArchive bank, Dictionary<ulong, BnkEntry> byRsc, XmbNode it)
        {
            var def = new Definition { Name = it.S("__Name", ""), ShadowConfig = it.S("DefaultShadowConfig", "") };
            var props = it.Get("CinProps");
            if (props != null && props.Items.Count > 0) def.CinProps = (uint)props.Items[0].AsInt();
            BnkEntry cin = null;
            var files = it.Get("CinFile");
            if (files != null)
                foreach (var f in files.Items)
                    if (f.I("rscSubType") == 16 && byRsc.TryGetValue(f.U("rsc"), out var e) && e.Extension == ".cin") { cin = e; break; }
            if (cin == null)
            {
                // Rückfall: Dateiname "..._M_<Name ohne C_>_..."
                string key = "_M_" + (def.Name.StartsWith("C_") ? def.Name.Substring(2) : def.Name) + "_";
                foreach (var e in bank.All(".cin")) if (e.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { cin = e; break; }
            }
            def.Cin = cin != null ? CinData.Parse(bank.Read(cin), cin.Name) : null;
            if (def.Cin == null) { DebugManager.Warning("[TDU2] Story: .cin von " + def.Name + " fehlt"); return; }
            for (int s = 0; s < def.Cin.SceneReferences.Count; s++)
            {
                string file = def.Cin.SceneFile(s);
                var se = file != null ? bank.Find(file + ".sce") : null;
                def.Scenes.Add(se != null ? CinScene.Parse(bank.Read(se), se.Name) : null);
            }
            _defs.Add(def);
        }

        void Update()
        {
            if (_task != null && _task.IsCompleted)
            {
                var t = _task; _task = null;
                if (t.IsFaulted || t.Result.error != null) { Fail(t.Exception?.GetBaseException().Message ?? t.Result.error); return; }
                try { Build(t.Result.model); }
                catch (Exception ex) { Fail("Aufbau: " + ex); }
            }
            if (!Loaded) return;
            float dt = Time.deltaTime;
            if (_main.Def != null && !_main.Ended) Advance(_main, dt, true);
            for (int i = _backgrounds.Count - 1; i >= 0; i--)
            {
                var b = _backgrounds[i];
                Advance(b, dt, false);
                if (b.Ended) { TDU2CinematicAudio.Stop(b.Voices); _backgrounds.RemoveAt(i); }
            }
            if (_goalTimer > 0f) { _goalTimer -= dt; if (_goalTimer <= 0f) CloseGoal(); }
            if (_hideScanT >= 0f)
            {
                _hideScanT -= dt;
                if (_hideScanT <= 0f)
                {
                    try { HideSpotInterior(); } catch (Exception ex) { DebugManager.Warning("[TDU2] Story: Exterior ausblenden: " + ex.Message); }
                    _hideScanT = Time.time < _hideScanUntil ? 2f : 3f;   // danach weiter (seltener): Streaming kann das Exterior neu bauen
                }
            }
        }

        void Build(BankModel model)
        {
            var st = Manager.Streamer;
            var root = st.MapRoot;
            Holder = new GameObject("Holder").transform;
            Holder.SetParent(root, false);
            Vector3 pos = Vector3.zero; float heading = 0f;
            if (!string.IsNullOrEmpty(Config.Spot) || Config.SpotRef != null)
            {
                HouseSpot = Config.SpotRef ?? TDU2GamePC.FindSpot(st.Spots?.Spots, Config.Spot);
                if (HouseSpot != null)
                {
                    bool exit = Config.ExitFrame && HouseSpot.Position2.sqrMagnitude > 1f;
                    pos = exit ? HouseSpot.Position2 : HouseSpot.Position;
                    heading = exit ? HouseSpot.Heading2 : HouseSpot.Value1;
                }
                else DebugManager.Warning("[TDU2] Story: Spot " + Config.Spot + " nicht gefunden");
            }
            if (Config.UseFrame) { pos = Config.FramePos; heading = Config.FrameHeading; }
            if (Config.Closed) pos += Vector3.up * Config.ClosedAltitude;
            Holder.localPosition = pos;
            Holder.localRotation = Quaternion.Euler(0f, heading * Mathf.Rad2Deg, 0f);
            // Ursprung der .sce ausgleichen: Meeting Tess ist mit Ursprung exportiert (Drehung = Spot-Richtung -2,44, Versatz
            // -6,18/-1,22/-13,03). Die Rohdaten liegen aber im Raum des Spot-Exteriors (Darsteller bei -13,7/0,2/19,0 = Vorplatz
            // zwischen Brunnen und Einfahrt, Bodenhöhe 0,07 wie e_hotela), ToGame() und die Platzierungen der Bank (D_CAR) tragen
            // den Ursprung -> Rahmen = Exterior (Position/Richtung des Spots) x Ursprung^-1. Alle anderen Szenen: Ursprung = Einheit.
            if (HouseSpot != null && !Config.Closed && SceneOrigin(out var om, out string omName))
            {
                var fm = Matrix4x4.TRS(Holder.localPosition, Holder.localRotation, Vector3.one) * om.inverse;
                Holder.localPosition = fm.GetColumn(3);
                Holder.localRotation = fm.rotation;
                TDU2Diag.Log("STORY", $"Szenen-Ursprung von {omName} ausgeglichen: Rahmen {TDU2Diag.F(Holder.localPosition)} {TDU2Diag.F(Holder.localRotation)}");
            }

            if (model != null)
            {
                bool glow = MaterialFactory.ShowGlowMaterials;
                MaterialFactory.ShowGlowMaterials = true;
                MaterialFactory.Interior = true;
                try { _scene = st.BuildScene(model, Holder, "Scene"); }
                finally { MaterialFactory.ShowGlowMaterials = glow; MaterialFactory.Interior = false; }
                if (_scene?.Root != null) FixProps(model, _scene.Root.transform);
                InteriorFx.Apply(BankPath(Config.Bank), Holder);
            }
            foreach (var (p, m) in _extraModels)
            {
                var anchor = new GameObject(Path.GetFileNameWithoutExtension(p)).transform;
                anchor.SetParent(Holder, false);
                bool glow = MaterialFactory.ShowGlowMaterials;
                MaterialFactory.ShowGlowMaterials = true; MaterialFactory.Interior = true;
                try { _extraScenes.Add(st.BuildScene(m, anchor, "Scene")); }
                finally { MaterialFactory.ShowGlowMaterials = glow; MaterialFactory.Interior = false; }
                InteriorFx.Apply(p, anchor);
            }
            if (model != null || _extraModels.Count > 0) AddColliders(Holder);
            if (Audio != null) Audio.Frame = Holder;
            if (!_probe) { _probe = true; TDU2SceneReflection.Acquire(); }   // Lack/Wasser spiegeln auch ohne Spieler-Auto
            if (Config.HideSpotInterior && !Config.Closed && HouseSpot != null && model != null) { _hideScanT = 0f; _hideScanUntil = Time.time + 45f; }
            if (Config.Closed)
            {
                _ambientSaved = RenderSettings.ambientLight; _ambientChanged = true;
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.58f);
            }
            BuildPnjs();
            LoadWeathers();
            if (Config.LoadUi) LoadFlash();
            _cam = Camera.main;
            if (_cam != null) { _savedFov = _cam.fieldOfView; _savedNear = _cam.nearClipPlane; _cam.nearClipPlane = Mathf.Min(_savedNear, 0.05f); }
            Loaded = true;
            DiagFrame();
            DebugManager.Info($"[TDU2] Story-Szene {Path.GetFileName(Config.Bank)}: {_defs.Count} Kinos, {_pnjs.Count} Figuren, {_selectables.Count} Auswahlpunkte, {_cameras.Count} Kameras" +
                              (HouseSpot != null ? ", Spot " + Config.Spot : ""));
        }

        /// <summary>
        /// Gemeinsamer Szenen-Ursprung (.sce) als Spaltenmatrix (Szenenraum -> Spielraum) – nur wenn ALLE Szenen der Bank denselben
        /// Ursprung haben, der keine Einheitsmatrix ist (Meeting Tess: Rohdaten liegen im Exterior-Raum, der Ursprung ist doppelt).
        /// Gemischte Banken (Waschanlage: C_BABE_xx mit Ursprung am Auto, Einfahrt/Waschen mit Einheitsmatrix) tragen echte
        /// Ursprünge je Szene: dort nichts ausgleichen (sonst war der ganze Rahmen um 90° gedreht und 18 m versetzt -> Kamera
        /// im Boden, Figuren in Wänden).
        /// </summary>
        bool SceneOrigin(out Matrix4x4 m, out string name)
        {
            m = Matrix4x4.identity; name = null;
            float[] first = null; int count = 0;
            foreach (var d in _defs)
                foreach (var sc in d.Scenes)
                {
                    if (sc == null) continue;
                    var o = sc.Origin;
                    bool ident = true;
                    for (int i = 0; i < 16 && ident; i++) ident = Mathf.Abs(o[i] - ((i % 5) == 0 ? 1f : 0f)) < 1e-3f;
                    if (ident) { TDU2Diag.Log("STORY", "Szenen-Ursprung: " + sc.Name + " ohne Ursprung – kein Ausgleich"); return false; }
                    if (first == null) { first = o; name = sc.Name; count++; continue; }
                    for (int i = 0; i < 16; i++)
                        if (Mathf.Abs(o[i] - first[i]) > 1e-2f) { TDU2Diag.Log("STORY", $"Szenen-Ursprung: {name} und {sc.Name} verschieden – kein Ausgleich"); return false; }
                    count++;
                }
            if (first == null) return false;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) m[r, c] = first[c * 4 + r];
                m[r, 3] = first[12 + r];
            }
            return true;
        }

        static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-3f) return false;
            return true;
        }

        /// <summary>
        /// Requisiten wie OpenTDU2 (IntroCinematic::loadBankModels): Entity__Actor an Platzierung x Wurzel^-1 (die Entität ersetzt den
        /// Wurzelknoten der Szene, sonst stünden LKW/Möbel doppelt verdreht/versetzt), Entity__Actor__Mesh/__Interior im Bankrahmen.
        /// </summary>
        void FixProps(BankModel model, Transform sceneRoot)
        {
            var used = new bool[_propEntries.Count];
            int fixedCount = 0;
            foreach (var set in model.AllObjectSets())
            {
                if (set?.Placements == null || set.Scene == null) continue;
                var rootInv = Matrix4x4.identity;
                // Wurzelknoten wie im C++ (IntroCinematic): nur der erste Hierarchie-Knoten ohne Eltern; ohne Hierarchie = Identität
                foreach (var h in set.Scene.Hierarchy)
                    if (h.ParentOffset == 0) { rootInv = set.Scene.GetIns(h.InsOffset).Matrix.inverse; break; }
                for (int i = 0; i < set.Placements.Count; i++)
                {
                    var pm = set.Placements[i];
                    if (pm.isIdentity) continue;
                    var t = sceneRoot.Find(set.Name + "#" + i);
                    if (t == null) continue;
                    int k = -1;
                    for (int j = 0; j < _propEntries.Count; j++)
                        if (!used[j] && string.Equals(_propEntries[j].dd, set.Name, StringComparison.OrdinalIgnoreCase) && Near(_propEntries[j].m, pm)) { k = j; break; }
                    if (k < 0) continue;
                    used[k] = true;
                    string arr = _propEntries[k].array;
                    if (arr == "ArrayOf_Entity__Actor__Mesh" || arr == "ArrayOf_Entity__Actor__Interior")
                    {
                        ObjectBuilder.ApplyMatrix(t, Matrix4x4.identity);
                        fixedCount++;
                        continue;
                    }
                    var rest = pm * rootInv;
                    ObjectBuilder.ApplyMatrix(t, rest);
                    _props.Add(new Prop { T = t, Hc = _propEntries[k].hc, Rest = rest, RootInv = rootInv });
                    fixedCount++;
                }
            }
            if (fixedCount > 0) DebugManager.Debug($"[TDU2] Story: {fixedCount} Requisiten ausgerichtet ({_props.Count} beweglich)");
        }

        /// <summary>Requisiten, die ein laufendes Kino bewegt (gleiche Entität mit Positionsspur in der .sce).</summary>
        void AnimateProps()
        {
            foreach (var p in _props)
            {
                if (p.T == null) continue;
                if (p.Hc != 0 && FindActor(p.Hc, null, out var sc, out var obj, out float t) && obj.Find(CinIds.TrackPosition) != null)
                {
                    var local = Matrix4x4.TRS(sc.ToGame(obj.SamplePosition(t)), sc.OriginRotation * obj.SampleRotation(t), Vector3.one);
                    ObjectBuilder.ApplyMatrix(p.T, local * p.RootInv);
                    p.Moved = true;
                }
                else if (p.Moved) { ObjectBuilder.ApplyMatrix(p.T, p.Rest); p.Moved = false; }
            }
        }

        /// <summary>
        /// Im gestreamten Exterior des Spots (Spot_&lt;Code&gt;, Spot_&lt;Code&gt;_low) den Interieur-Platzhalter (Low-Poly-LODs, Knoten
        /// "INTERIOR") und Teile, die ganz in der Szene liegen, ausblenden (wie TDU2Interiors / OpenTDU2: die Bank bringt das Interieur).
        /// Läuft einige Sekunden wiederholt, weil das Exterior auch nach der Szene gestreamt werden kann.
        /// </summary>
        void HideSpotInterior()
        {
            var root = Manager.Streamer?.MapRoot;
            if (root == null || HouseSpot == null) return;
            string n1 = "Spot_" + HouseSpot.Code, n2 = n1 + "_low";
            var center = root.TransformPoint(HouseSpot.Position);
            // Ausdehnung der Szene (jedes Mal neu: Floating Origin verschiebt die Welt)
            _haveSceneBounds = false;
            if (_scene?.Root != null)
                foreach (var r in _scene.Root.GetComponentsInChildren<Renderer>(false))
                {
                    if (!_haveSceneBounds) { _sceneBounds = r.bounds; _haveSceneBounds = true; } else _sceneBounds.Encapsulate(r.bounds);
                }
            if (_haveSceneBounds) _sceneBounds.Expand(1f);
            var names = new List<string>();
            if (_scene?.Model != null)
                foreach (var set in _scene.Model.AllObjectSets())
                    if (!string.IsNullOrEmpty(set.Name)) names.Add(Path.GetFileNameWithoutExtension(set.Name));
            // Entity__Actor__Interior des Spot-Exteriors (islands/<insel>/exterior/<code>.bnk): die Low-Poly-Innenräume, die OpenTDU2
            // beim Laden des Exteriors auslässt, wenn das echte Interieur geladen ist (sonst flackern Low- und High-Mesh übereinander)
            if (_extInteriorSets == null) _extInteriorSets = ExteriorInteriorSets(HouseSpot.Code);
            // Spot-Wurzeln suchen (ganze Welt) nur alle 10 s, dazwischen nur die bekannten prüfen (Streaming baut sie ggf. neu)
            _spotRoots.RemoveAll(x => x == null);
            if (_spotRoots.Count == 0 || Time.time >= _spotRootsSearch)
            {
                _spotRootsSearch = Time.time + 10f;
                _spotRoots.Clear();
                foreach (var t in root.GetComponentsInChildren<Transform>(false))
                    if ((t.name == n1 || t.name == n2) && (t.position - center).sqrMagnitude <= 60f * 60f) _spotRoots.Add(t);
            }
            foreach (var t in _spotRoots)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (!c.gameObject.activeSelf || _hiddenRoots.Contains(c)) continue;
                    bool hide = string.Equals(c.name, "INTERIOR", StringComparison.OrdinalIgnoreCase) || IsExteriorInterior(c.name);
                    if (!hide) foreach (var n in names) if (c.name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) { hide = true; break; }
                    if (!hide && _haveSceneBounds)
                    {
                        bool any = false; var b = new Bounds();
                        foreach (var r in c.GetComponentsInChildren<Renderer>(false)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                        hide = any && _sceneBounds.Contains(b.min) && _sceneBounds.Contains(b.max);
                    }
                    if (hide) { HideRenderers(c); continue; }
                    foreach (var d in c.GetComponentsInChildren<Transform>(false))
                        if (d != c && !_hiddenRoots.Contains(d) && (string.Equals(d.name, "INTERIOR", StringComparison.OrdinalIgnoreCase) || IsExteriorInterior(d.name)))
                            HideRenderers(d);
                }
            }
        }

        List<string> _extInteriorSets;
        readonly HashSet<Transform> _hiddenRoots = new HashSet<Transform>();
        readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

        /// <summary>Nur unsichtbar machen: die Collider bleiben (darauf läuft man im Laufmodus, die Szene selbst hat nicht überall Boden-Collider).</summary>
        void HideRenderers(Transform t)
        {
            _hiddenRoots.Add(t);
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }
        }
        readonly List<Transform> _spotRoots = new List<Transform>();
        float _spotRootsSearch;

        bool IsExteriorInterior(string name)
        {
            if (_extInteriorSets == null || string.IsNullOrEmpty(name)) return false;
            foreach (var s in _extInteriorSets) if (name.StartsWith(s + "#", StringComparison.OrdinalIgnoreCase) || string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Szenen (.3dd-Name) der Entity__Actor__Interior im Exterior-Bank des Spots.</summary>
        List<string> ExteriorInteriorSets(string code)
        {
            var list = new List<string>();
            try
            {
                string island = Manager.Streamer?.Index?.IslandName ?? "ibiza";
                string path = Path.Combine(Manager.BnkDir, "islands", island, "exterior", (code ?? "").ToLowerInvariant() + ".bnk");
                if (!File.Exists(path)) return list;
                var bank = BnkArchive.Open(path);
                var ent = bank.Find("entities.xmb"); var hc = bank.Find("hashcodes.xmb");
                if (ent == null || hc == null) return list;
                var byRsc = new Dictionary<ulong, BnkEntry>();
                var groups = XmbFile.Parse(bank.Read(hc))?.Get("___CmdSaveFileInfoArray");
                if (groups != null)
                    foreach (var g in groups.Items)
                    {
                        var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                        if (idx == null || refs == null) continue;
                        int n = Math.Min(idx.Items.Count, refs.Items.Count);
                        for (int i = 0; i < n; i++)
                        {
                            int fi = idx.Items[i].AsInt(-1);
                            BnkEntry be = null;
                            foreach (var x in bank.Entries) if (x.Index == fi) { be = x; break; }
                            if (be == null && fi >= 0 && fi < bank.Entries.Count) be = bank.Entries[fi];
                            if (be != null) byRsc[refs.Items[i].AsULong()] = be;
                        }
                    }
                var arr = XmbFile.Parse(bank.Read(ent))?.Get("ExportType_Entity")?.Get("ArrayOf_Entity__Actor__Interior");
                if (arr != null)
                    foreach (var it in arr.Items)
                    {
                        var mesh = it.Get("mesh");
                        if (mesh == null) continue;
                        foreach (var r in mesh.Items)
                            if (r.I("rscSubType", -1) == 0 && byRsc.TryGetValue(r.U("rsc"), out var dd)) { if (!list.Contains(dd.Name)) list.Add(dd.Name); break; }
                    }
                DebugManager.Debug("[TDU2] Story: Interieur-LODs im Exterior " + Path.GetFileName(path) + ": " + string.Join(", ", list));
            }
            catch (Exception ex) { DebugManager.Warning("[TDU2] Story: Exterior-Interieur: " + ex.Message); }
            return list;
        }

        static void AddColliders(Transform root)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!UnityUtil.IsCollidable(mf.sharedMesh)) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr != null && (!mr.enabled || (mr.sharedMaterial != null && mr.sharedMaterial.renderQueue >= 2900))) continue;
                if (mf.GetComponent<Collider>() == null) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }
        }

        void BuildPnjs()
        {
            if (!AvatarLibrary.Ready) { DebugManager.Warning("[TDU2] Story: Avatar-Bibliothek nicht bereit – keine Figuren"); return; }
            // nur Figuren, die eine Szene eines Kinos spielt
            var used = new HashSet<ulong>();
            foreach (var d in _defs) foreach (var sc in d.Scenes) if (sc != null) foreach (var o in sc.Objects) used.Add(o.EntityHashcode);
            foreach (var (name, cfgName, hc) in _pnjEntries)
            {
                if (!used.Contains(hc)) continue;
                var cfg = AvatarLibrary.PnjConfig(cfgName);
                if (cfg == null) { DebugManager.Debug("[TDU2] Story-Figur ohne DBPNJ: " + name + " (" + cfgName + ")"); continue; }
                var go = new GameObject("Figur " + name);
                go.transform.SetParent(Holder, false);
                var p = new Pnj { Name = name, Config = cfgName, Hc = hc, Root = go.transform };
                p.Avatar = TDU2Avatar.Create(go.transform, cfg, "TDU2 " + (AvatarLibrary.PnjName(cfgName) ?? name));
                _pnjs.Add(p);
            }
        }

        /// <summary>Spieler-Avatar (Darsteller "Avatar" der Szenen) aus einer DBPNJ-Konfiguration (AVT_Male_Intro …) oder einer Konfiguration.</summary>
        public bool SetPlayerAvatar(string configId, AvatarConfig cfg = null)
        {
            if (_player != null && _player.Root != null) Destroy(_player.Root.gameObject);
            _player = null;
            cfg = cfg ?? AvatarLibrary.PnjConfig(configId);
            if (cfg == null || Holder == null) return false;
            var go = new GameObject("Spieler-Avatar");
            go.transform.SetParent(Holder, false);
            _player = new Pnj { Name = "Avatar", Config = configId, Root = go.transform };
            _player.Avatar = TDU2Avatar.Create(go.transform, cfg, "TDU2 Spieler");
            return true;
        }

        public AvatarConfig PlayerConfig => _player?.Avatar?.Config;

        void LoadWeathers()
        {
            var idx = Manager.Streamer?.Index;
            if (idx == null) return;
            string path = Path.Combine(idx.FxDir, "cinematic_weathers.xmb");
            try { if (File.Exists(path)) _weathers = TDU2WeatherData.Load(path); }
            catch (Exception ex) { DebugManager.Warning("[TDU2] cinematic_weathers.xmb: " + ex.Message); }
            // Modifikatoren der Schnitte ("wmod:Meeting_Tess_02" ...): Sonnenrichtung/Licht je Einstellung
            try { if (_weathers != null) _weathers.LoadModifiers(Path.Combine(idx.FxDir, "cinematic_modifiers.xmb")); }
            catch (Exception ex) { DebugManager.Warning("[TDU2] cinematic_modifiers.xmb: " + ex.Message); }
        }

        string FrontendBank(string name)
        {
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                var f = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), name);
                if (f != null) return f;
            }
            return null;
        }

        void LoadFlash()
        {
            var texts = FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage());
            string intro = Config.IntroUi ? FrontendBank("intro.bnk") : null;
            if (intro != null)
            {
                var go = new GameObject("TDU2 Story UI (intro)");
                go.transform.SetParent(transform, false);
                Ui = go.AddComponent<VmfPlayer>();
                Ui.GuiDepth = -2;
                Ui.Load(intro);
                Ui.FSCommand += (c, a) =>
                {
                    if (c == "sbdd") { FlashLocalization.Apply(Ui, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); return; }
                    Command?.Invoke(c, a);
                };
                FlashLocalization.Apply(Ui, texts);
                Ui.SetValue("/:IsCasinoSaveData", false);
                Ui.SetValue("/:WhatPlatform", 1);
                Ui.SetValue("/:IsTransition", 1);
                Ui.MouseEnabled = true;
            }
            string general = FrontendBank("general.bnk");
            if (general != null)
            {
                var go = new GameObject("TDU2 Story UI (general)");
                go.transform.SetParent(transform, false);
                General = go.AddComponent<VmfPlayer>();
                General.GuiDepth = -3;
                General.Load(general);
                FlashLocalization.Apply(General, texts);
                General.FSCommand += (c, a) => { if (c == "sbdd") FlashLocalization.Apply(General, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); };
                General.SetValue("/:WhatPlatform", 1);
                General.SetValue("/:IsWalkmode", false);
                // nur Ziel- und Tipp-Boxen zeigen
                foreach (var d in General.Root.List.Values) d.Visible = d.Name == "pop_goal" || d.Name == "pop_tutorial";
            }
        }

        /// <summary>FSCommands des INTRO-Films (sbdd wird hier behandelt).</summary>
        public event Action<string, string> Command;

        // ------------------------------------------------------------------ Kinos
        public Definition Find(string name)
        {
            foreach (var d in _defs) if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        public string MainName => _main.Def?.Name ?? "";
        public float MainTime => _main.Time;
        public bool MainEnded => _main.Def == null || _main.Ended;

        public bool PlayMain(string name)
        {
            var d = Find(name);
            if (d == null) { DebugManager.Warning("[TDU2] Story: Kino " + name + " fehlt"); return false; }
            Launch(_main, d, d.Loop, d.NoCamera);
            DebugManager.Debug("[TDU2] Story: Kino " + name + $" ({d.Cin.Duration:0.0} s)");
            TDU2Diag.TraceCamera(240, "Story-Kino " + name);
            DiagFrame();
            return true;
        }

        /// <summary>Figur (Entity__PNJ, z.B. "Babe_01_radio") ein-/ausblenden – Figuren, die erst ein Kino auftreten lässt.</summary>
        public bool SetPnjVisible(string name, bool visible)
        {
            bool any = false;
            foreach (var p in _pnjs)
                if (p.Root != null && (name == null || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                { if (p.Root.gameObject.activeSelf != visible) p.Root.gameObject.SetActive(visible); any = true; }
            return any;
        }

        public bool PlayBackground(string name, bool loop = true)
        {
            var d = Find(name);
            if (d == null) { DebugManager.Warning("[TDU2] Story: Kino " + name + " fehlt"); return false; }
            StopBackground(name);
            var p = new Playing();
            Launch(p, d, loop, true);
            _backgrounds.Add(p);
            return true;
        }

        public void StopMain() { TDU2CinematicAudio.Stop(_main.Voices); _main.Def = null; _main.Ended = false; _main.Time = 0f; _main.Prev = -1f; }
        public void StopBackground() { foreach (var b in _backgrounds) TDU2CinematicAudio.Stop(b.Voices); _backgrounds.Clear(); }
        public void StopBackground(string name) => _backgrounds.RemoveAll(b =>
        {
            if (b.Def == null || !string.Equals(b.Def.Name, name, StringComparison.OrdinalIgnoreCase)) return false;
            TDU2CinematicAudio.Stop(b.Voices);
            return true;
        });

        /// <summary>Entity__Fx der Bank abspielen (z.B. "C_OUTRO", "S_TimeStart").</summary>
        public bool PlayEmitter(string name) => Audio != null && Audio.PlayEmitter(name) != null;

        /// <summary>Fx__Root nach Name abspielen (2D, z.B. Stimmen "EVTE_to_player_01"). Rückgabe: Dauer in s (0 = nicht gefunden).</summary>
        public float PlayFx(string rootName)
        {
            if (Audio == null) return 0f;
            ulong hc = Audio.FindRootByName(rootName);
            if (hc == 0) return 0f;
            float len = 0f;
            foreach (var s in Audio.PlayRoot(hc, 0f, false, null, TDU2CinematicAudio.Flag2D)) if (s != null && s.clip != null) len = Mathf.Max(len, s.clip.length / Mathf.Max(0.1f, s.pitch));
            return len;
        }

        public void StopMusic() => TDU2CinematicAudio.Stop(_musicVoices);
        public bool IsBackgroundPlaying(string name) => _backgrounds.Exists(b => b.Def != null && string.Equals(b.Def.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Haupt-Kino überspringen: Endpose, das Ende kommt mit dem nächsten Bild (Ereignis 7).</summary>
        public void SkipMain()
        {
            if (_main.Def == null) return;
            _main.Time = _main.Prev = Mathf.Max(_main.Def.Cin.Duration - 0.001f, 0f);
            _main.Loop = false;
        }

        public List<string> TakeEndEvents()
        {
            var r = new List<string>(_endEvents);
            _endEvents.Clear();
            return r;
        }

        void Launch(Playing p, Definition d, bool loop, bool noCamera)
        {
            TDU2CinematicAudio.Stop(p.Voices);
            p.Def = d; p.Loop = loop; p.NoCamera = noCamera; p.Ended = false;
            p.Time = 0f;
            ProcessMessages(p, -1f, 0f, p == _main);
            UpdateFx(p, -1f, 0f);
            p.Prev = 0f;
        }

        void Advance(Playing p, float dt, bool main)
        {
            if (p.Def == null) return;
            float dur = p.Def.Cin.Duration;
            if (dur <= 0f) return;
            float from = p.Prev, t = p.Time + dt;
            if (t > dur - 0.001f)
            {
                if (!p.Loop)
                {
                    t = dur - 0.001f;
                    ProcessMessages(p, from, t, main);
                    UpdateFx(p, from, t);
                    p.Time = p.Prev = t;
                    TDU2CinematicAudio.Stop(p.Voices);
                    p.Ended = true;
                    _endEvents.Add(p.Def.Name);
                    return;
                }
                ProcessMessages(p, from, dur, main);
                UpdateFx(p, from, dur);
                t = t <= dur ? 0.001f : t - dur;
                // Neustart (FUN_00b402f0): die Töne des Kinos beginnen von vorn
                TDU2CinematicAudio.Stop(p.Voices);
                ProcessMessages(p, -1f, t, main);
                UpdateFx(p, -1f, t);
            }
            else { ProcessMessages(p, from, t, main); UpdateFx(p, from, t); }
            p.Time = p.Prev = t;
        }

        /// <summary>Töne: Fx-Ereignisse des .cin und Fx-Spuren der Darsteller der aktuellen Szene (OpenTDU2 IntroCinematic::updateFx).</summary>
        void UpdateFx(Playing p, float from, float to)
        {
            if (Audio == null || !Audio.HasSounds || p.Def?.Cin == null) return;
            var cin = p.Def.Cin;
            try
            {
                Audio.UpdateCin(cin, from, to, p.Voices);
                var shot = cin.ShotAt(Mathf.Max(to, 0f));
                if (shot == null || shot.Scene < 0 || shot.Scene >= p.Def.Scenes.Count || p.Def.Scenes[shot.Scene] == null) return;
                var prevShot = from >= 0f ? cin.ShotAt(from) : null;
                float st = shot.SceneTime(to);
                float prev = prevShot == shot && from >= 0f ? shot.SceneTime(from) : shot.CameraStart - 1e-4f;
                Audio.UpdateScene(p.Def.Scenes[shot.Scene], prev, st, p.Voices);
            }
            catch (Exception ex) { DebugManager.Warning("[TDU2] Kino-Töne " + p.Def.Name + ": " + ex.Message); }
        }

        /// <summary>Nachrichten des Haupt-Kinos: flsh:&lt;Label&gt; (INTRO-Film), weat:/wmod: (Kino-Wetter), shad: (Schatten).</summary>
        void ProcessMessages(Playing p, float from, float to, bool main)
        {
            if (!main || p.Def == null) return;
            var cin = p.Def.Cin;
            foreach (var e in cin.Messages)
            {
                bool hit = (e.Time > from && e.Time <= to) || (from < 0f && e.Time <= 0f && to >= 0f);
                if (!hit) continue;
                string m = cin.MessageText(e);
                if (string.IsNullOrEmpty(m)) continue;
                if (m.StartsWith("flsh:", StringComparison.Ordinal))
                {
                    string label = m.Substring(5);
                    if (label == "generik5" && Ui != null) Ui.SetVariable("/:generik_date", FlashLocalization.Text(Manager.BnkDir, TDU2PauseMenu.SavedLanguage(), 0x139b) ?? "");
                    Ui?.GotoLabel("/", label, true);
                    Message?.Invoke(m);
                }
                else if (m.StartsWith("weat:", StringComparison.Ordinal)) { _weather = m.Substring(5); _modifier = ""; ApplyWeather(); }
                else if (m.StartsWith("wmod:", StringComparison.Ordinal)) { _modifier = m.Substring(5); ApplyWeather(); }
                else if (m == "snd:timestop" && Audio != null) { Audio.PlayEmitter("S_TimeStop", Mathf.Max(to - e.Time, 0f)); Message?.Invoke(m); }
                else if (m == "snd:music" && Audio != null)
                {
                    // die 4 Party-Musik-Emitter (Schleife)
                    StopMusic();
                    foreach (var n in new[] { "S_WALKMODE_Music", "S_WALKMODE_Music_01", "S_WALKMODE_Music_02", "S_WALKMODE_Music_03" })
                    {
                        var v = Audio.PlayEmitter(n, Mathf.Max(to - e.Time, 0f), true, true);
                        if (v != null) _musicVoices.AddRange(v);
                    }
                    Message?.Invoke(m);
                }
                else Message?.Invoke(m);
            }
        }

        /// <summary>Übrige Kino-Nachrichten (snd:, shad:, avm4: …) für die Spielmodi.</summary>
        public event Action<string> Message;

        void ApplyWeather()
        {
            if (_weathers == null || string.IsNullOrEmpty(_weather)) { TDU2Weather.Override = null; return; }
            var k = _weathers.Find(_weather, _modifier);
            TDU2Weather.Override = k;
            TDU2Diag.Log("WETTER", $"Kino-Wetter weat:{_weather} wmod:{_modifier} -> {k?.Name ?? "nicht gefunden"}");
        }

        /// <summary>Rahmen der Szene und Weltlage der Platzierungen ins Sitzungs-Log.</summary>
        void DiagFrame()
        {
            if (Holder == null) return;
            var sb = new System.Text.StringBuilder();
            sb.Append($"Bank={Config.Bank} Spot={Config.Spot} ExitFrame={Config.ExitFrame} Closed={Config.Closed}");
            if (HouseSpot != null) sb.Append($" spotPos={TDU2Diag.F(HouseSpot.Position)} h={HouseSpot.Value1:0.000} pos2={TDU2Diag.F(HouseSpot.Position2)} h2={HouseSpot.Heading2:0.000}");
            sb.Append($" HolderLocal={TDU2Diag.F(Holder.localPosition)} {TDU2Diag.F(Holder.localRotation)} HolderWelt={TDU2Diag.F(Holder.position)}");
            foreach (var kv in _placements)
                if (PlacementWorld(kv.Key, out var wp, out var wr)) sb.Append("\n    ").Append(kv.Key).Append(" bank=").Append(TDU2Diag.F(kv.Value.pos)).Append(" welt=").Append(TDU2Diag.F(wp)).Append(' ').Append(TDU2Diag.F(wr));
            if (Car != null) sb.Append("\n    Auto welt=").Append(TDU2Diag.F(Car.position)).Append(' ').Append(TDU2Diag.F(Car.rotation));
            // Gebäude des Spots (vom Streamer gebaut, "Spot_<Code>") gegen den Rahmen: gleiche Lage/Drehung erwartet
            if (HouseSpot != null && !string.IsNullOrEmpty(HouseSpot.Code))
            {
                string want = "Spot_" + HouseSpot.Code;
                foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (t == null || !t.name.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                    float d = Vector3.Distance(t.position, Holder.position);
                    if (d > 400f) continue;
                    sb.Append("\n    Gebäude ").Append(t.name).Append(" aktiv=").Append(t.gameObject.activeInHierarchy).Append(" welt=").Append(TDU2Diag.F(t.position))
                      .Append(' ').Append(TDU2Diag.F(t.rotation)).Append(" skala=").Append(TDU2Diag.F(t.lossyScale)).Append(" Abstand=").Append(d.ToString("0.00"))
                      .Append(" Rahmen=").Append(TDU2Diag.F(Holder.rotation)).Append(" skala=").Append(TDU2Diag.F(Holder.lossyScale));
                }
            }
            TDU2Diag.Log("STORY", sb.ToString());
        }

        /// <summary>Figuren: Materialien einmal, Gesichtsknochen ein paar Mal (1 s nach dem ersten Bild, dann alle 10 s, 3x).</summary>
        void DiagPnj(Pnj p)
        {
            if (p.Avatar == null || !p.Avatar.Ready || !p.Avatar.Visible) return;
            if (p.DiagN == 0 && p.DiagT <= 0f) { p.DiagT = Time.time + 1f; p.Avatar.DiagMaterials(p.Name + " (" + p.Config + ")"); }
            if (p.DiagN < 3 && Time.time >= p.DiagT)
            {
                p.DiagN++; p.DiagT = Time.time + 10f;
                p.Avatar.DiagFace($"{p.Name} t={_main.Time:0.00} {_main.Def?.Name}");
                // Gesichtsbild je Konfiguration einmal (Docs/Logs/face_<Konfig>.png)
                if (p.DiagN == 1) p.Avatar.DiagSnapshot(p.Config ?? p.Name, $"{p.Name} ({p.Config}) {_main.Def?.Name}");
            }
        }

        // ------------------------------------------------------------------ Kamera
        /// <summary>Kamera gehört dem Laufmodus (true) bzw. dem Kino (false).</summary>
        public bool CameraOverride { get => _cameraOverride; set => _cameraOverride = value; }

        /// <summary>Entity__Camera (z.B. cam_entree_01) in Weltkoordinaten.</summary>
        public bool EntityCamera(string name, out Vector3 worldPos, out Vector3 worldForward)
        {
            worldPos = Vector3.zero; worldForward = Vector3.forward;
            if (Holder == null || !_cameras.TryGetValue(name, out var c)) return false;
            worldPos = Holder.TransformPoint(c.pos);
            worldForward = Holder.TransformVector(c.rot * Vector3.forward).normalized;
            return true;
        }

        /// <summary>Platzierung (Entity__PosCam / Entity__CinCar) in Weltkoordinaten.</summary>
        public bool PlacementWorld(string name, out Vector3 worldPos, out Quaternion worldRot)
        {
            worldPos = Vector3.zero; worldRot = Quaternion.identity;
            if (Holder == null || !_placements.TryGetValue(name, out var c)) return false;
            worldPos = Holder.TransformPoint(c.pos);
            worldRot = CinIds.FrameRotation(Holder, c.rot);
            return true;
        }

        /// <summary>
        /// Rahmen in der Höhe auf den Boden der gestreamten Welt setzen: Platzierung <paramref name="anchor"/> (z.B. "D_CAR") liegt danach
        /// auf der ersten Fläche unter/über ihr, die nicht zur Szene selbst gehört. false = kein Boden gefunden (Rahmen unverändert).
        /// </summary>
        public bool GroundTo(string anchor)
        {
            if (Holder == null || !PlacementWorld(anchor, out var p, out _)) return false;
            var hits = Physics.RaycastAll(p + Vector3.up * 30f, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, y = 0f;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(Holder)) continue;
                float d = Mathf.Abs(h.point.y - p.y);
                if (d < best) { best = d; y = h.point.y; }
            }
            if (best > 20f) { TDU2Diag.Log("STORY", $"GroundTo {anchor}: kein Boden unter {TDU2Diag.F(p)} ({hits.Length} Treffer)"); return false; }
            TDU2Diag.Log("STORY", $"GroundTo {anchor}: Anker {TDU2Diag.F(p)} -> Boden y={y:0.000} ({hits.Length} Treffer)");
            Holder.position += Vector3.up * (y - p.y);
            DebugManager.Info($"[TDU2] Story-Szene: Rahmen auf Boden gesetzt ({anchor}, {y - p.y:+0.00;-0.00} m)");
            return true;
        }

        public Vector3 ToLocal(Vector3 world) => Holder != null ? Holder.InverseTransformPoint(world) : world;
        public Vector3 ToLocalDir(Vector3 world) => Holder != null ? Holder.InverseTransformVector(world) : world;

        public Volume FindVolume(string name)
        {
            foreach (var v in _volumes) if (v.Name == name) return v;
            foreach (var v in _selectables) if (v.Name == name) return v;
            return null;
        }

        /// <summary>Aktuelle Kino-Kamera (Welt), falls das Haupt-Kino eine hat.</summary>
        public bool CinematicCamera(out Vector3 pos, out Quaternion rot, out float fov)
        {
            pos = Vector3.zero; rot = Quaternion.identity; fov = 60f;
            if (_main.Def == null || _main.NoCamera || Holder == null) return false;
            if (!_main.Def.Cin.Camera(_main.Def.Scenes, _main.Time, out var gp, out var gr, out fov)) return false;
            pos = Holder.TransformPoint(gp);
            rot = CinIds.FrameRotation(Holder, gr);
            return true;
        }

        void LateUpdate()
        {
            if (!Loaded) return;
            if (_cam == null) _cam = Camera.main;
            if (!_cameraOverride && _cam != null && CinematicCamera(out var cp, out var cr, out float fov))
            {
                _cam.transform.SetPositionAndRotation(cp, cr);
                if (fov > 1f && fov < 170f) _cam.fieldOfView = fov;
                TDU2Diag.CameraSet(_cam, "Story " + (_main.Def?.Name ?? "?"), _main.Time);
            }
            foreach (var p in _pnjs) Animate(p, p.Hc, null);
            if (_player != null) Animate(_player, 0, "Avatar");
            AnimateProps();
            UpdateCar();
            UpdateFade();
            DiagGround();
        }

        string _groundDiagFor;
        float _loadedAt = -1f;

        /// <summary>
        /// DIAG: 0,5 s nach Kinostart (bzw. 4 s nach dem Laden einer Deko-Szene ohne Hauptkino) je Figur/Auto/Requisite Weltlage und
        /// Boden darunter (Δ) ins Log – zeigt verschobene Szenen (schwebende LKW, Figuren in Wänden).
        /// </summary>
        void DiagGround()
        {
            if (Holder == null || !Loaded) return;
            if (_loadedAt < 0f) _loadedAt = Time.time;
            string key = _main.Def != null ? _main.Def.Name : "(Deko)";
            if (_groundDiagFor == key) return;
            if (_main.Def != null ? _main.Time < 0.5f : Time.time - _loadedAt < 4f) return;
            _groundDiagFor = key;
            try
            {
                var root = Manager != null && Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
                var sb = new System.Text.StringBuilder();
                sb.Append("Boden-Check ").Append(key).Append(" (Holder spiel=")
                  .Append(TDU2Diag.F(root != null ? root.InverseTransformPoint(Holder.position) : Holder.position)).Append(')');
                void One(string name, Transform t)
                {
                    if (t == null) return;
                    var p = t.position;
                    float best = float.MaxValue, gy = float.NaN; string what = "-";
                    foreach (var h in Physics.RaycastAll(p + Vector3.up * 30f, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider == null || h.collider.transform.IsChildOf(Holder) || h.collider.transform.IsChildOf(t)) continue;
                        float d = Mathf.Abs(h.point.y - p.y);
                        if (d < best) { best = d; gy = h.point.y; what = h.collider.name; }
                    }
                    var g = root != null ? root.InverseTransformPoint(p) : p;
                    sb.Append("\n    ").Append(name).Append(" spiel=").Append(TDU2Diag.F(g)).Append(" Boden ")
                      .Append(float.IsNaN(gy) ? "keiner" : $"{gy:0.00} (Δ {p.y - gy:+0.00;-0.00} m, {what})");
                }
                foreach (var pn in _pnjs) One(pn.Name, pn.Root);
                if (_player != null) One("Avatar", _player.Root);
                if (Car != null) One("PLAYER_CAR", Car);
                // Requisiten der Bank (LKW, Teppich, Kabel …): Unterkante der Renderer gegen den Boden
                int n = 0;
                foreach (Transform ch in Holder)
                {
                    if (n >= 40) break;
                    var rs = ch.GetComponentsInChildren<Renderer>(false);
                    if (rs.Length == 0) continue;
                    var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                    float best = float.MaxValue, gy = float.NaN;
                    foreach (var h in Physics.RaycastAll(new Vector3(b.center.x, b.max.y + 30f, b.center.z), Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider == null || h.collider.transform.IsChildOf(Holder)) continue;
                        float d = Mathf.Abs(h.point.y - b.min.y);
                        if (d < best) { best = d; gy = h.point.y; }
                    }
                    var g = root != null ? root.InverseTransformPoint(b.center) : b.center;
                    sb.Append("\n    Requisite ").Append(ch.name).Append(" mitte spiel=").Append(TDU2Diag.F(g)).Append(" unten=").Append(b.min.y.ToString("0.00"))
                      .Append(float.IsNaN(gy) ? " Boden keiner" : $" Boden {gy:0.00} (Δ {b.min.y - gy:+0.00;-0.00} m)");
                    n++;
                }
                TDU2Diag.Log("STORY", sb.ToString());
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Boden-Check: " + ex.Message); }
        }

        IEnumerable<Playing> Order()
        {
            if (_main.Def != null) yield return _main;
            for (int i = _backgrounds.Count - 1; i >= 0; i--) yield return _backgrounds[i];
        }

        /// <summary>Darsteller (Entität oder Name) in der aktuellen Szene eines laufenden Kinos.</summary>
        bool FindActor(ulong hc, string name, out CinScene scene, out CinObject obj, out float sceneTime)
        {
            scene = null; obj = null; sceneTime = 0f;
            foreach (var p in Order())
            {
                if (!p.Def.Cin.SceneTime(p.Def.Scenes, p.Time, out var sc, out float st)) continue;
                foreach (var o in sc.Objects)
                {
                    if (o.IsCamera) continue;
                    bool match = name != null ? string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) : o.EntityHashcode == hc;
                    if (!match) continue;
                    scene = sc; obj = o; sceneTime = st;
                    return true;
                }
            }
            return false;
        }

        AnmClip Clip(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "none") return null;
            if (_clips.TryGetValue(name, out var c)) return c;
            c = CineScene.LoadClip(Bank, name) ?? AvatarLibrary.Clip(name);
            _clips[name] = c;
            return c;
        }

        void Animate(Pnj p, ulong hc, string name)
        {
            var a = p.Avatar;
            if (a == null || p.Root == null) return;
            if (!FindActor(hc, name, out var sc, out var obj, out float t) || !obj.VisibleAt(t))
            {
                if (a.Ready) a.SetVisible(false);
                return;
            }
            p.Root.localPosition = sc.ToGame(obj.SamplePosition(t));
            p.Root.localRotation = sc.OriginRotation * obj.SampleRotation(t);
            if (!a.Ready) return;
            a.SetVisible(true);
            a.BeginPose();
            var tr = obj.Find(CinIds.TrackAnimation);
            if (tr != null && tr.Components >= 8 && tr.Times.Length > 0)
            {
                int k = 0;
                for (int i = 0; i < tr.Times.Length; i++) if (tr.Times[i] <= t) k = i;
                float blendIn = tr.Values[k * tr.Components + 7];
                float since = t - tr.Times[k];
                if (k > 0 && blendIn > 0f && since < blendIn)
                {
                    ApplyKey(a, sc, tr, k - 1, t, 1f);
                    ApplyKey(a, sc, tr, k, t, Mathf.Clamp01(since / blendIn));
                }
                else ApplyKey(a, sc, tr, k, t, 1f);
            }
            ApplyFaceFx(a, sc, obj, t);
            a.CommitPose();
            DiagPnj(p);
        }

        // ------------------------------------------------------------------ FaceFX (Lippen-Sync, Mimik)
        readonly List<FaceFxAnimSet> _faceSets = new List<FaceFxAnimSet>();
        readonly Dictionary<string, FaceFxAnim> _faceAnims = new Dictionary<string, FaceFxAnim>(StringComparer.OrdinalIgnoreCase);
        float[] _faceVals, _facePrev;
        const float FaceFxFade = 0.2f;   // DAT_00ede428 (FUN_00b77600 / FUN_00b77b20)

        /// <summary>FxAnimSets (.fxe) der Kino-Bank; die Darsteller (female/male.fxa) gleich mitladen (Hintergrund-Thread).</summary>
        void LoadFaceSets(BnkArchive bank)
        {
            if (bank == null) return;
            int n = 0;
            foreach (var e in bank.All(".fxe"))
            {
                try { var s = FaceFxAnimSet.Parse(bank.Read(e)); lock (_faceSets) _faceSets.Add(s); n += s.Anims.Count; }
                catch (Exception ex) { DebugManager.Warning("[TDU2] FaceFX " + e.Name + ": " + ex.Message); }
            }
            if (n > 0) { FaceFxLibrary.Actor(true); FaceFxLibrary.Actor(false); DebugManager.Info($"[TDU2] Story: {n} FaceFX-Animationen"); }
        }

        FaceFxAnim FaceAnim(CinScene sc, CinTrack tr, int k, bool woman)
        {
            int refIdx = (int)tr.Raw[k * tr.Components];
            if (refIdx < 0 || refIdx >= sc.References.Count) return null;
            var rf = sc.References[refIdx];
            if (rf.Type != 0x0306 || string.IsNullOrEmpty(rf.Name)) return null;
            int c = rf.Name.LastIndexOf(':');
            string name = c >= 0 ? rf.Name.Substring(c + 1) : rf.Name;
            string cacheKey = (woman ? "w:" : "m:") + name;
            if (_faceAnims.TryGetValue(cacheKey, out var a)) return a;
            lock (_faceSets)
            {
                foreach (var s in _faceSets) if (s.IsFemale == woman && s.Anims.TryGetValue(name, out a)) break;
                if (a == null) foreach (var s in _faceSets) if (s.Anims.TryGetValue(name, out a)) break;
            }
            _faceAnims[cacheKey] = a;
            return a;
        }

        static float FaceWeight(FaceFxAnim anim, float animTime, out float evalTime)
        {
            evalTime = Mathf.Min(animTime, anim.End);
            if (animTime < 0f) return 0f;
            float w = Mathf.Min(animTime / FaceFxFade, 1f);
            if (animTime > anim.End) w = Mathf.Min(w, 1f - (animTime - anim.End) / FaceFxFade);
            return Mathf.Max(w, 0f);
        }

        /// <summary>
        /// "Morph"-Spur des Darstellers: der letzte Schlüssel bis zur Szenenzeit spielt seine FaceFX-Animation (Zeit ab dem Schlüssel),
        /// 0,2 s Ein-/Ausblenden, die vorige wird währenddessen weiter überblendet (OpenTDU2 IntroCinematic::applyFaceFx).
        /// </summary>
        void ApplyFaceFx(TDU2Avatar a, CinScene sc, CinObject obj, float t)
        {
            if (_faceSets.Count == 0) return;
            var tr = obj.Find(CinIds.TrackMorph);
            if (tr == null || tr.Components < 1 || tr.Times.Length == 0) return;
            bool woman = a.Config != null && a.Config.Sex == 1;
            var actor = FaceFxLibrary.Actor(woman);
            if (actor == null) return;
            int k = -1;
            for (int i = 0; i < tr.Times.Length; i++) if (tr.Times[i] <= t) k = i;
            if (k < 0) return;
            var anim = FaceAnim(sc, tr, k, woman);
            if (anim == null) return;
            float w = FaceWeight(anim, t - tr.Times[k], out float at);
            if (w <= 0f) return;
            if (_faceVals == null || _faceVals.Length != actor.Nodes.Length) { _faceVals = new float[actor.Nodes.Length]; _facePrev = new float[actor.Nodes.Length]; }
            if (w < 1f && k > 0)
            {
                var prev = FaceAnim(sc, tr, k - 1, woman);
                if (prev != null)
                {
                    float pw = FaceWeight(prev, t - tr.Times[k - 1], out float pat);
                    if (pw > 0f) { actor.Evaluate(prev, pat, _facePrev); ApplyFacePose(a, actor, _facePrev, pw); }
                }
            }
            actor.Evaluate(anim, at, _faceVals);
            ApplyFacePose(a, actor, _faceVals, w);
        }

        static void ApplyFacePose(TDU2Avatar a, FaceFxActor actor, float[] vals, float w)
        {
            for (int b = 0; b < actor.Bones.Length; b++)
            {
                int i = a.Bone(actor.Bones[b].Name);
                if (i < 0) continue;
                actor.Pose(b, vals, out var p, out var r, out var s);
                a.BlendLocal(i, p, r, s, w);
            }
        }

        void ApplyKey(TDU2Avatar a, CinScene sc, CinTrack tr, int k, float t, float w)
        {
            int refIdx = (int)tr.Raw[k * tr.Components];
            var clip = Clip(sc.ReferenceName(refIdx));
            if (clip == null) return;
            float ct = tr.Values[k * tr.Components + 2] - tr.Values[k * tr.Components + 7] + (t - tr.Times[k]);
            a.ApplyClip(clip, Mathf.Repeat(ct, Mathf.Max(0.05f, clip.Length)), w, true);
        }

        // ------------------------------------------------------------------ Auto der Szene
        /// <summary>
        /// Auto der Szene: ruht an einer Platzierung (Entity__PosCam, z.B. D_CAR); spielt ein Kino den Darsteller einer
        /// Entity__CinCar (bzw. mit diesem Namen, z.B. CIN_CAR / PLAYER_CAR), folgt es dessen Spuren.
        /// </summary>
        public void SetCar(Transform car, string restEntity, string cinCarEntity)
        {
            Car = car; _carRest = restEntity; _carActorName = cinCarEntity;
            _carHc = cinCarEntity != null && _cinCarHc.TryGetValue(cinCarEntity, out var h) ? h : 0;
            _carGrounded = false;
            // Auto-Modelle sind selbst gespiegelt (CarModel.Visual): in Weltkoordinaten lassen, Lage aus dem Haus-Rahmen
        }

        void PoseCar(Vector3 local, Quaternion localRot) => Car.SetPositionAndRotation(Holder.TransformPoint(local), CinIds.FrameRotation(Holder, localRot));

        void UpdateCar()
        {
            if (Car == null || Holder == null) return;
            bool posed = false;
            if (_carHc != 0 && FindActor(_carHc, null, out var sc, out var obj, out float t) && obj.Find(CinIds.TrackPosition) != null)
            { PoseCar(sc.ToGame(obj.SamplePosition(t)), sc.OriginRotation * obj.SampleRotation(t)); posed = true; }
            else if (_carActorName != null && FindActor(0, _carActorName, out sc, out obj, out t) && obj.Find(CinIds.TrackPosition) != null)
            { PoseCar(sc.ToGame(obj.SamplePosition(t)), sc.OriginRotation * obj.SampleRotation(t)); posed = true; }
            if (!posed && _carRest != null && _placements.TryGetValue(_carRest, out var rest)) PoseCar(rest.pos, rest.rot);
            // Reifen auf den Boden der Szene: Abstand Radunterkante – Autoursprung einmal, der Boden darunter jedes Bild
            // (vorher nur einmal am Start: bei Einfahrten liegen Straße draußen und Hallenboden drinnen verschieden hoch ->
            // Auto schwebte z.B. in der Waschanlage). Treffer über der Radunterkante (+0,6 m: Dach, Portal, Bürsten) zählen nicht.
            if (!_carGrounded)
            {
                var rs = Car.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) return;
                float minY = float.MaxValue;
                foreach (var r in rs) if (r.enabled && r.bounds.size.y < 4f) minY = Mathf.Min(minY, r.bounds.min.y);
                if (minY == float.MaxValue) return;
                _carBottom = minY - Car.position.y;
                _carGrounded = true; _carGroundInit = false;
            }
            float bottom = Car.position.y + _carBottom;
            float best = float.NegativeInfinity;
            foreach (var hit in UnityEngine.Physics.RaycastAll(Car.position + Vector3.up * 2f, Vector3.down, 8f))
                if (!hit.transform.IsChildOf(Car) && hit.point.y <= bottom + 0.6f) best = Mathf.Max(best, hit.point.y);
            float target = best > float.NegativeInfinity && Mathf.Abs(best - bottom) < 2f ? best - bottom : 0f;
            _carGroundOffset = _carGroundInit ? Mathf.Lerp(_carGroundOffset, target, 1f - Mathf.Exp(-Time.deltaTime * 12f)) : target;
            _carGroundInit = true;
            Car.position += Vector3.up * _carGroundOffset;
        }

        // ------------------------------------------------------------------ Einblenden (Shot-Übergang Typ 1)
        void UpdateFade()
        {
            float a = 0f; Color c = Color.black;
            if (_main.Def != null && !_main.NoCamera && !_cameraOverride)
            {
                var shot = _main.Def.Cin.ShotAt(_main.Time);
                if (shot != null && shot.FadeType == 1 && shot.FadeDuration > 0f)
                {
                    float el = _main.Time - shot.Start;
                    if (el >= 0f && el < shot.FadeDuration) { a = 1f - el / shot.FadeDuration; c = shot.FadeColor; }
                }
            }
            if (_extraFade > 0f) { a = Mathf.Max(a, _extraFade); c = _extraFadeColor; }
            if (a <= 0.001f) { if (_fade != null) _fade.SetActive(false); return; }
            if (_cam == null) return;
            if (_fade == null)
            {
                _fade = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _fade.name = "TDU2 Story Fade";
                var col = _fade.GetComponent<Collider>(); if (col != null) Destroy(col);
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
                _fadeMat = new Material(sh) { renderQueue = 5000 };
                _fade.GetComponent<Renderer>().sharedMaterial = _fadeMat;
            }
            _fade.SetActive(true);
            var ct = _cam.transform;
            float d = _cam.nearClipPlane + 0.01f;
            _fade.transform.SetPositionAndRotation(ct.position + ct.forward * d, ct.rotation);
            float h = 2f * d * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
            _fade.transform.localScale = new Vector3(h * _cam.aspect, h, 1f);
            c.a = a;
            _fadeMat.color = c;
        }

        float _extraFade; Color _extraFadeColor = Color.black;
        /// <summary>Zusätzliche Blende (0 = aus, 1 = voll) in einer Farbe, z.B. Übergänge zwischen Spielmodi.</summary>
        public void SetFade(float alpha, Color color) { _extraFade = Mathf.Clamp01(alpha); _extraFadeColor = color; }

        // ------------------------------------------------------------------ GENERAL: Ziel- und Tipp-Box
        float _goalTimer = -1f;

        /// <summary>pop_goal (FUN_00d61790): /:GoalTxt, Label "open", 30 s.</summary>
        public void ShowGoal(string text)
        {
            TDU2Story.CurrentObjective = text;
            if (General == null) return;
            General.SetVariable("/:GoalTxt", text ?? "");
            General.SetValue("/:IsWalkmode", false);
            General.GotoLabel("/pop_goal", "open", true);
            _goalTimer = 30f;
        }

        public void CloseGoal()
        {
            _goalTimer = -1f;
            if (General != null && !General.Call("/pop_goal", "close_popup")) General.GotoLabel("/pop_goal", "close", true);
        }

        /// <summary>pop_tutorial ("Tipp"): /:TutText, Label "open_tuto".</summary>
        public void ShowTutorial(string text)
        {
            TDU2Story.CurrentObjective = text;
            if (General == null) return;
            General.SetVariable("/:TutText", text ?? "");
            General.SetValue("/:IsWalkmode", false);
            General.GotoLabel("/pop_tutorial", "open_tuto", true);
        }

        public void CloseTutorial()
        {
            if (General != null && !General.Call("/pop_tutorial", "close_popup")) General.GotoLabel("/pop_tutorial", "close", true);
        }

        /// <summary>Lokalisierter Text (db_&lt;lang&gt;, HashCode/Zeile).</summary>
        public string Loc(int id) => FlashLocalization.Text(Manager.BnkDir, TDU2PauseMenu.SavedLanguage(), id) ?? "";

        // ------------------------------------------------------------------ Aufräumen
        bool _probe;

        public void Dispose()
        {
            if (_probe) { _probe = false; TDU2SceneReflection.Release(); }
            StopMain(); StopBackground(); StopMusic();
            Audio?.Dispose(); Audio = null;
            foreach (var go in _hiddenExterior) if (go != null) go.SetActive(true);
            _hiddenExterior.Clear(); _hideScanT = -1f;
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenRenderers.Clear(); _hiddenRoots.Clear();
            _props.Clear();
            TDU2Weather.Override = null;
            if (_cam != null)
            {
                if (_savedFov > 0f) _cam.fieldOfView = _savedFov;
                if (_savedNear > 0f) _cam.nearClipPlane = _savedNear;
            }
            if (_ambientChanged) { RenderSettings.ambientLight = _ambientSaved; _ambientChanged = false; }
            _scene?.Destroy(); _scene = null;
            foreach (var h in _extraScenes) h?.Destroy();
            _extraScenes.Clear();
            if (Holder != null) Destroy(Holder.gameObject);
            Holder = null; Car = null;
            if (_fade != null) Destroy(_fade);
            if (_fadeMat != null) Destroy(_fadeMat);
            _pnjs.Clear(); _player = null; _clips.Clear();
            Loaded = false;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (Loaded) { TDU2Weather.Override = null; if (Holder != null) Destroy(Holder.gameObject); _scene?.Destroy(); }
            Audio?.Dispose(); Audio = null;
            foreach (var go in _hiddenExterior) if (go != null) go.SetActive(true);
            _hiddenExterior.Clear();
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenRenderers.Clear();
            if (_probe) { _probe = false; TDU2SceneReflection.Release(); }
        }
    }
}
