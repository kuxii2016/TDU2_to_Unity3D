using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TDU2.Map.Formats;
using TDU2.Map.Online;
using TDU2.Map.World;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// My House (Haus einrichten) wie im Spiel, mit den Original-Oberflächen aus home.vmf:
    ///  * Tagebuch -> "My House" (Label myhs_open, FSCommand myhs:open): Laufmodus mit Cursor; anvisierbar sind Möbel
    ///    (Entity__MyHouseObject der Szene) und Böden/Wände (Materialien "MAT_Floor&lt;n&gt;_&lt;id&gt;" / "MAT_Wall&lt;n&gt;_&lt;id&gt;").
    ///    Anvisieren: _root.SelItemType = "myhs_item", _root.myhsChangeType = "furniture"/"material", cursor "on";
    ///    A -> _root.myhsEditorOpen() -> myhs:furn:open bzw. myhs:mate:open.
    ///  * Möbel (myhs_editor/furniture): Modelle gleichen Typs (DBFurniture._type = Entity.Type + 1), _root.num_o/nb_o (Modell),
    ///    color_id/nb_colors (Farbvariante = letzter Buchstabe der Bank), name_o, CurPrice, IsItemBought, EnoughMoney; Lader
    ///    _root.furnitureLoaded. Befehle chsh (Modell), chco (Farbe), puok (gekauft/übernommen), clos (abgebrochen).
    ///    Möbel derselben Gruppe (Entity.Group, z.B. alle Stühle) wechseln gemeinsam.
    ///  * Böden/Tapeten (myhs_editor/material): DBMaterial (_type 1 = Boden ground_NN, 2 = Wand wall_NN), _root.id_m/nb_m/view_sq,
    ///    Vorschaubilder je Feld über myhs:mate:dima:&lt;id&gt; (Clip sqN/lure), chma (Vorschau), puok, clos; Lader _root.MaterialLoaded.
    ///  * Kauf: Bestätigung (myhs_purch_valid) wird beantwortet, puok bucht den Preis ab (schon gekaufte Stücke kostenlos).
    ///  * Gespeichert im Server-Spielstand (SaveData.houses je Haus-Spot, ownedFurniture/ownedMaterials); Gäste bekommen die
    ///    Einrichtung des Gastgebers mit der Einladung (GuestHouses) und sehen das Haus genauso.
    /// </summary>
    public sealed partial class TDU2Interiors
    {
        // ================================================================== Katalog (db_data.cpr)
        sealed class FurnItem { public int Id; public string Bank; public int Type; public int Price; public int Level; public ulong NameHc; public string Path; }
        sealed class MatItem { public int Id; public string Name; public int Type; public int Price; public ulong NameHc; public string Path; }
        static List<FurnItem> _furnCat;
        static List<MatItem> _matCat;
        static Task<(List<FurnItem>, List<MatItem>)> _catTask;

        void EnsureHouseCatalog()
        {
            if (_furnCat != null || _catTask != null || Manager == null || string.IsNullOrEmpty(Manager.BnkDir)) return;
            string db = CarDatabase.FindDatabase(Manager.BnkDir);
            string dir = Path.Combine(InteriorDir, "myhouse");
            if (db == null) { _furnCat = new List<FurnItem>(); _matCat = new List<MatItem>(); return; }
            _catTask = Task.Run(() =>
            {
                var root = XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(db)));
                var fl = new List<FurnItem>(); var ml = new List<MatItem>();
                var f = root.Get("DBFurniture");
                if (f != null)
                    foreach (var r in f.Items)
                    {
                        if (!r.B("_isvalid", true) || r.B("_iscasino") || r.B("_isdlc")) continue;
                        string bank = (r.S("_bank", "") ?? "").ToLowerInvariant();
                        if (bank.Length == 0) continue;
                        string p = CarDatabase.FindFile(dir, bank + ".bnk");
                        if (p == null) continue;
                        fl.Add(new FurnItem { Id = r.I("_hashcode"), Bank = bank, Type = r.I("_type"), Price = r.I("_price"), Level = r.I("_level"), NameHc = r.U("_name_hc"), Path = p });
                    }
                var m = root.Get("DBMaterial");
                var seen = new HashSet<string>();
                if (m != null)
                    foreach (var r in m.Items)
                    {
                        string n = (r.S("_materialname", "") ?? "").ToLowerInvariant();
                        if (n.Length == 0 || !seen.Add(n)) continue;
                        string p = CarDatabase.FindFile(dir, n + ".bnk");
                        if (p == null) continue;
                        ml.Add(new MatItem { Id = r.I("_hashcode"), Name = n, Type = r.I("_type"), Price = r.I("_price"), NameHc = r.U("_name_hc"), Path = p });
                    }
                ml.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return (fl, ml);
            });
        }

        bool HouseCatalogReady
        {
            get
            {
                if (_furnCat != null) return true;
                if (_catTask == null || !_catTask.IsCompleted) return false;
                if (_catTask.IsFaulted) { Debug.LogWarning("[TDU2] My House: Katalog: " + _catTask.Exception?.GetBaseException().Message); _furnCat = new List<FurnItem>(); _matCat = new List<MatItem>(); }
                else { _furnCat = _catTask.Result.Item1; _matCat = _catTask.Result.Item2; Debug.Log("[TDU2] My House: " + _furnCat.Count + " Möbel, " + _matCat.Count + " Böden/Tapeten"); }
                _catTask = null;
                return true;
            }
        }

        static string ItemName(ulong hc, string fallback)
        {
            string t = hc != 0 ? Racing.RaceTexts.Get(hc, "") : "";
            return string.IsNullOrEmpty(t) ? fallback : t;
        }

        /// <summary>
        /// Modell-Gruppe einer Bank. Nur die Serien-Möbel (T1…T8, S1…S6: "t2sofala".."t2sofale", gleiche Dateigröße) sind Farbvarianten
        /// eines Modells (letzter Buchstabe = Farbe). Die übrigen ("chaisala".."chaisalr", "sofa1p_a", "tabdinea" …) sind jeweils
        /// eigene Modelle – vorher wurden sie zu einem Modell mit vielen "Farben" zusammengefasst, darum gab es kaum Auswahl.
        /// </summary>
        static string ShapeOf(string bank)
        {
            if (string.IsNullOrEmpty(bank) || bank.Length < 4) return bank ?? "";
            string b = bank.ToLowerInvariant();
            bool series = (b[0] == 't' || b[0] == 's') && char.IsDigit(b[1]);
            return series ? b.Substring(0, b.Length - 1) : b;
        }

        // ================================================================== Einrichtung (gespeichert)
        /// <summary>Einrichtung des Gastgebers (mit der Einladung); null = eigene.</summary>
        public List<HouseLayout> GuestHouses;

        HouseLayout Layout(bool create)
        {
            if (_spot == null) return null;
            string key = _spot.Code ?? "";
            List<HouseLayout> list;
            if (GuestHost != 0) list = GuestHouses;
            else
            {
                var sv = TDU2Session.Save;
                if (sv == null) return null;
                sv.houses ??= new List<HouseLayout>();
                list = sv.houses;
            }
            if (list == null) return null;
            foreach (var h in list) if (h != null && string.Equals(h.spot, key, StringComparison.OrdinalIgnoreCase)) return h;
            if (!create || GuestHost != 0) return null;
            var nh = new HouseLayout { spot = key };
            list.Add(nh);
            return nh;
        }

        static string Lookup(List<HouseEntry> l, string k)
        {
            if (l == null) return null;
            foreach (var e in l) if (e != null && string.Equals(e.k, k, StringComparison.OrdinalIgnoreCase)) return e.v;
            return null;
        }

        static void Store(List<HouseEntry> l, string k, string v)
        {
            foreach (var e in l) if (e != null && string.Equals(e.k, k, StringComparison.OrdinalIgnoreCase)) { e.v = v; return; }
            l.Add(new HouseEntry { k = k, v = v });
        }

        /// <summary>Bank eines Möbelstücks (Einrichtung, sonst Standard der Szene).</summary>
        string HouseObjectBank(InteriorScene.Ent e, string defaultBank)
        {
            var l = Layout(false);
            string b = l != null ? Lookup(l.furn, e.Name ?? "") : null;
            return string.IsNullOrEmpty(b) ? defaultBank : b;
        }

        // ================================================================== Möbel der Szene
        sealed class HouseObj
        {
            public InteriorScene.Ent Ent; public int Type, Group;
            public string DefaultBank, Bank;
            public Transform Anchor; public TDU2MapStreamer.SceneHandle Handle;
            public Task<BankModel> Loading; public string LoadingBank;
        }
        readonly List<HouseObj> _houseObjs = new List<HouseObj>();
        readonly Dictionary<InteriorScene.Ent, string> _houseBankOf = new Dictionary<InteriorScene.Ent, string>();

        void RegisterHouseObject(InteriorScene.Ent e, Transform anchor, TDU2MapStreamer.SceneHandle h)
        {
            string def = Regex.Replace(e.Name ?? "", @"_\d+$", "").ToLowerInvariant();
            _houseObjs.Add(new HouseObj
            {
                Ent = e,
                Type = e.Node != null ? e.Node.I("Type") : 0,
                Group = e.Node != null ? e.Node.I("Group") : 0,
                DefaultBank = def,
                Bank = _houseBankOf.TryGetValue(e, out var b) ? b : def,
                Anchor = anchor,
                Handle = h,
            });
        }

        void ClearHouse()
        {
            foreach (var o in _houseObjs) o.Loading = null;
            _houseObjs.Clear();
            _pickMeshes.Clear(); _pickVerts.Clear(); _zoneParts = null;
            _houseBankOf.Clear();
            foreach (var d in _matDonors.Values) d?.Handle?.Destroy();   // fehlende Spender stehen als null drin
            _matDonors.Clear(); _donorTasks.Clear();
            _zoneOrig.Clear(); _zones = null; _zoneWanted.Clear();
            if (_myHouse != HouseMode.None) CasinoMenuOpen = false;
            _myHouse = HouseMode.None; _edit = null; _editZone = null; _housePreview = false; _houseRestoreAt = -1f;
        }

        /// <summary>Möbelstück (und seine Gruppe) gegen eine andere Bank tauschen (Vorschau oder endgültig).</summary>
        void SetFurnitureBank(HouseObj target, string bank)
        {
            foreach (var o in _houseObjs)
            {
                bool same = o == target || (target.Group != 0 && o.Group == target.Group && o.Type == target.Type);
                if (!same || string.Equals(o.LoadingBank ?? o.Bank, bank, StringComparison.OrdinalIgnoreCase)) continue;
                string path = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), bank + ".bnk");
                if (path == null) { Debug.LogWarning("[TDU2] My House: Bank fehlt: " + bank); continue; }
                var st = Manager.Streamer;
                o.LoadingBank = bank;
                o.Loading = Task.Run(() => st.LoadSceneModel(path));
            }
        }

        bool AnyFurnitureLoading { get { foreach (var o in _houseObjs) if (o.Loading != null) return true; return false; } }

        void PollHouseObjects()
        {
            foreach (var o in _houseObjs)
            {
                if (o.Loading == null || !o.Loading.IsCompleted) continue;
                var t = o.Loading; o.Loading = null;
                if (t.IsFaulted || t.Result == null) { Debug.LogWarning("[TDU2] My House: " + o.LoadingBank + ": " + t.Exception?.GetBaseException().Message); o.LoadingBank = null; continue; }
                if (o.Anchor == null) { t.Result.Release(); continue; }
                if (o.Handle != null) { _furniture.Remove(o.Handle); o.Handle.Destroy(); }
                bool glow = MaterialFactory.ShowGlowMaterials;
                MaterialFactory.ShowGlowMaterials = true;
                try { o.Handle = Manager.Streamer.BuildScene(t.Result, o.Anchor, o.Ent.Name); }
                finally { MaterialFactory.ShowGlowMaterials = glow; }
                InteriorFx.Apply(null, o.Anchor);
                PrepareStatic(o.Anchor);
                _furniture.Add(o.Handle);
                o.Bank = o.LoadingBank; o.LoadingBank = null;
            }
        }

        // ================================================================== Böden / Wände
        sealed class Zone { public string Key; public int Type; public string Default; public readonly List<Material> Mats = new List<Material>(); }
        Dictionary<string, Zone> _zones;
        static readonly Regex ZoneRx = new Regex(@"^MAT_(Floor|Wall)(\d+)_(\d+)", RegexOptions.IgnoreCase);
        readonly Dictionary<Material, Dictionary<string, (Texture tex, Vector2 sc, Vector2 of)>> _zoneOrig = new Dictionary<Material, Dictionary<string, (Texture, Vector2, Vector2)>>();

        Dictionary<string, Zone> Zones
        {
            get
            {
                if (_zones != null || _holder == null) return _zones;
                _zones = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _holder.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        var mt = ZoneRx.Match(m.name);
                        if (!mt.Success) continue;
                        bool floor = mt.Groups[1].Value.Equals("Floor", StringComparison.OrdinalIgnoreCase);
                        string key = (floor ? "Floor" : "Wall") + mt.Groups[2].Value;
                        if (!_zones.TryGetValue(key, out var z))
                            _zones[key] = z = new Zone { Key = key, Type = floor ? 1 : 2, Default = (floor ? "ground_" : "wall_") + int.Parse(mt.Groups[3].Value).ToString("00") };
                        if (!z.Mats.Contains(m)) z.Mats.Add(m);
                    }
                return _zones;
            }
        }

        string ZoneMaterial(Zone z)
        {
            var l = Layout(false);
            string v = l != null ? Lookup(l.mats, z.Key) : null;
            return string.IsNullOrEmpty(v) ? z.Default : v;
        }

        // Spender-Materialien (interior/myhouse/ground_NN / wall_NN.bnk: Vorschau-Fläche mit "_Ground_NN"/"_Wall_NN")
        sealed class Donor { public TDU2MapStreamer.SceneHandle Handle; public Material Mat; public Texture Thumb; }
        readonly Dictionary<string, Donor> _matDonors = new Dictionary<string, Donor>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Task<BankModel>> _donorTasks = new Dictionary<string, Task<BankModel>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _zoneWanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Transform _donorRoot;

        Donor GetDonor(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_matDonors.TryGetValue(name, out var d)) return d;
            if (!_donorTasks.ContainsKey(name))
            {
                string path = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), name + ".bnk");
                if (path == null) { _matDonors[name] = null; return null; }
                var st = Manager.Streamer;
                _donorTasks[name] = Task.Run(() => st.LoadSceneModel(path));
            }
            return null;
        }

        void PollDonors()
        {
            if (_donorTasks.Count == 0) return;
            List<string> done = null;
            foreach (var kv in _donorTasks)
            {
                if (!kv.Value.IsCompleted) continue;
                (done ??= new List<string>()).Add(kv.Key);
                if (kv.Value.IsFaulted || kv.Value.Result == null) { _matDonors[kv.Key] = null; continue; }
                if (_donorRoot == null)
                {
                    var go = new GameObject("TDU2 MyHouse Materials") { hideFlags = HideFlags.DontSave };
                    go.SetActive(false);
                    go.transform.SetParent(transform, false);
                    _donorRoot = go.transform;
                }
                var h = Manager.Streamer.BuildScene(kv.Value.Result, _donorRoot, kv.Key);
                Material mat = null;
                foreach (var r in h.Root.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials) if (m != null) { mat = m; break; }
                Texture thumb = null;
                if (mat != null)
                {
                    if (mat.HasProperty("_DetailTex")) thumb = mat.GetTexture("_DetailTex");
                    if (thumb == null && mat.HasProperty("_BaseMap")) thumb = mat.GetTexture("_BaseMap");
                    if (thumb == null) thumb = mat.mainTexture;
                }
                _matDonors[kv.Key] = new Donor { Handle = h, Mat = mat, Thumb = thumb };
            }
            if (done == null) return;
            foreach (var k in done) _donorTasks.Remove(k);
            // wartende Flächen / Vorschaubilder
            foreach (var kv in new List<KeyValuePair<string, string>>(_zoneWanted))
                if (_matDonors.ContainsKey(kv.Value)) { _zoneWanted.Remove(kv.Key); ApplyZone(kv.Key, kv.Value); }
            if (_myHouse == HouseMode.Material) RefreshMaterialThumbs();
        }

        static readonly string[] SurfaceProps = { "_DetailTex", "_GlossTex", "_BaseMap" };

        /// <summary>Fläche (Zone) mit dem Material eines Spenders belegen (Vorschau oder endgültig).</summary>
        void ApplyZone(string zoneKey, string matName)
        {
            var zones = Zones;
            if (zones == null || !zones.TryGetValue(zoneKey, out var z)) return;
            var d = GetDonor(matName);
            if (d == null) { if (!_matDonors.ContainsKey(matName)) _zoneWanted[zoneKey] = matName; return; }
            if (d.Mat == null) return;
            foreach (var m in z.Mats)
            {
                if (!_zoneOrig.ContainsKey(m))
                {
                    var o = new Dictionary<string, (Texture, Vector2, Vector2)>();
                    foreach (var p in SurfaceProps) if (m.HasProperty(p)) o[p] = (m.GetTexture(p), m.GetTextureScale(p), m.GetTextureOffset(p));
                    o["_MainTex"] = (m.mainTexture, m.mainTextureScale, m.mainTextureOffset);
                    _zoneOrig[m] = o;
                }
                bool copied = false;
                foreach (var p in SurfaceProps)
                {
                    if (!m.HasProperty(p) || !d.Mat.HasProperty(p)) continue;
                    var t = d.Mat.GetTexture(p);
                    if (t == null) continue;
                    m.SetTexture(p, t); m.SetTextureScale(p, d.Mat.GetTextureScale(p)); m.SetTextureOffset(p, d.Mat.GetTextureOffset(p));
                    copied = true;
                }
                if (!copied && d.Thumb != null)
                {
                    // Spender ohne Detail-Ebene: Textur dorthin, wo die Fläche ihre Oberfläche hat
                    if (m.HasProperty("_DetailTex") && m.GetTexture("_DetailTex") != null) m.SetTexture("_DetailTex", d.Thumb);
                    else m.mainTexture = d.Thumb;
                }
            }
        }

        /// <summary>Gespeicherte Böden/Wände anwenden (nach dem Aufbau der Szene).</summary>
        void ApplyHouseMaterials()
        {
            var zones = Zones;
            if (zones == null || zones.Count == 0) return;
            var l = Layout(false);
            if (l == null || l.mats == null) return;
            foreach (var e in l.mats) if (e != null && zones.ContainsKey(e.k) && !string.IsNullOrEmpty(e.v)) ApplyZone(e.k, e.v);
        }

        // ================================================================== Ablauf
        enum HouseMode { None, Walk, Furniture, Material }
        HouseMode _myHouse;
        bool _housePreview;
        float _houseModeAt, _houseRestoreAt = -1f;
        HouseObj _aimObj, _edit;
        string _aimZone, _editZone;
        // Möbel-Editor
        List<List<FurnItem>> _shapes; int _shape, _color; string _editOrigBank;
        // Material-Editor
        List<MatItem> _mats; int _matIdx; string _editOrigMat;

        public bool MyHouseActive => _myHouse != HouseMode.None;

        void StartMyHouse()
        {
            _diaryOpen = false;
            _myHouse = HouseMode.Walk; CasinoMenuOpen = true;   // Pausemenü bleibt gesperrt, ESC gehört dem Film (Verlassen-Dialog)
            _houseModeAt = Time.unscaledTime; _houseRestoreAt = -1f;
            _aimObj = null; _aimZone = null; _edit = null; _editZone = null;
            EnsureHouseCatalog();
            Racing.RaceTexts.Ensure(Manager.BnkDir, TDU2PauseMenu.SavedLanguage());   // Namen (_name_hc)
            if (_ui != null)
            {
                foreach (var n in new[] { "myhs_editor", "cursor", "help", "help_system", "title_bar", "cmon_loader", "cmon_loading" }) _ui.SetVisible("/" + n, true);
                // Tagebuch-Clips aus OpenDiary wieder weg: /diary zeigt auf "default" seine Seitengrafik (das Menü links)
                foreach (var n in new[] { "diary", "mylf", "mylf_book", "club_money" }) _ui.SetVisible("/" + n, false);
                _ui.SetValue("/:Input", true);
                SetMoneyVars();
            }
            LockCursor(true);
            if (_fp != null) _fp.LookEnabled = true;
            Debug.Log("[TDU2] My House: Einrichten (Zonen " + (Zones?.Count ?? 0) + ", Möbel " + _houseObjs.Count + ")");
        }

        void EndMyHouse()
        {
            if (_myHouse == HouseMode.None) return;
            RevertEdit();
            _myHouse = HouseMode.None; CasinoMenuOpen = false; _housePreview = false;
            Racing.RacingSchoolMenu.NoteUiClosed();
            _houseRestoreAt = Time.unscaledTime + 1.2f;   // Film spielt living_in -> danach die Laufmodus-Oberfläche wiederherstellen
            LockCursor(true);
            if (_fp != null) _fp.LookEnabled = true;
            Debug.Log("[TDU2] My House beendet");
        }

        void SetMoneyVars()
        {
            var sv = TDU2Session.Save;
            string money = sv != null ? sv.money.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $" : " ";
            _ui.SetVariable("/:CurMoney", money);
            _ui.SetVariable("/:MyMoneyStr", money);
        }

        static string PriceText(int price) => price > 0 ? price.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $" : "0 $";

        /// <summary>Jedes Bild im Haus (aus UpdateInside); true = Eingabe verbraucht.</summary>
        bool UpdateMyHouse()
        {
            if (_ui == null) { _myHouse = HouseMode.None; _houseRestoreAt = -1f; return false; }
            PollDonors();
            PollHouseObjects();
            if (_houseRestoreAt > 0f && Time.unscaledTime >= _houseRestoreAt)
            {
                _houseRestoreAt = -1f;
                if (_ui != null && _myHouse == HouseMode.None && !_diaryOpen)
                {
                    // Wohnbereich wie nach dem Tagebuch: Film auf living_stop, Eingabe frei, Laufmodus mit Maus-Umsicht
                    Debug.Log("[TDU2] My House verlassen: Wurzel " + _ui.Label("/") + ", Input " + _ui.GetVariable("/:Input") + ", Cursor " + _ui.Label("/cursor"));
                    _ui.GotoLabel("/", _kind == Kind.Garage ? "garage_stop" : "living_stop", false);
                    foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
                    _ui.GotoLabel("/myhs_editor", "default", false);
                    _ui.SetVisible("/cursor/low_bar", true);
                    _ui.SetVisible("/title_bar/ic", true);
                    _ui.GotoLabel("/cursor", "valid", true);
                    _ui.SetValue("/:Input", true);
                    _uiAim = null;
                }
                CasinoMenuOpen = false;
                LockCursor(true);
                if (_fp != null) _fp.LookEnabled = true;
            }
            if (_myHouse == HouseMode.None) return false;
            // Lader des Films: fertig, sobald nichts mehr lädt
            // (myhsEditorOpen setzt MaterialLoaded erst NACH myhs:mate:open auf false -> hier nachziehen)
            if (_myHouse == HouseMode.Furniture && !AnyFurnitureLoading && _ui.GetVariable("/:furnitureLoaded") != "true") _ui.SetValue("/:furnitureLoaded", true);
            if (_myHouse == HouseMode.Material && Time.unscaledTime - _houseModeAt > 0.1f && _ui.GetVariable("/:MaterialLoaded") != "true") _ui.SetValue("/:MaterialLoaded", true);
            // Hilfe-Leiste unten (Kaufen/Verlassen/Vorschau, SetHelp("myhs")) aus; im Editor auch die Ziel-Leiste des Cursors
            // ("Möbel ändern") und das Haus-Symbol der Titelleiste – dort zeigt der Editor seine Zähler (48/51)
            bool walk = _myHouse == HouseMode.Walk;
            _ui.SetVisible("/help", false);
            _ui.SetVisible("/cursor/low_bar", walk);
            _ui.SetVisible("/title_bar/ic", walk);
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_myHouse == HouseMode.Walk) UpdateHouseWalk();
            else
            {
                if (_fp != null) _fp.LookEnabled = _housePreview;
                if (Cursor.lockState == CursorLockMode.Locked && !_housePreview) LockCursor(false);
                if (Time.unscaledTime - _houseModeAt < 0.25f) return true;
                if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _ui.Input("Move", "left");
                if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _ui.Input("Move", "right");
                if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _ui.Input("Move", "up");
                if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _ui.Input("Move", "down");
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) _ui.Input("A");
                // ESC/Rücktaste: zentraler Menüablauf (TDU2MenuFlow.cs -> B des Editors, sonst ForceHouseWalk)
                if (Input.GetKeyDown(KeyCode.X) || Input.GetKeyDown(KeyCode.P)) _ui.Input("myhsPreview");   // Vorschau (frei umsehen)
            }
#endif
            return true;
        }

        /// <summary>My House verlassen (= Ja im Dialog "My House verlassen?").</summary>
        void QuitMyHouse()
        {
            _ui.SetVariable("/:DgbxCase", "myhs_quit");
            _ui.SetVariable("/:PlayerAnswer", "a");
            if (!_ui.CallClip("/", "DialogBox") || _myHouse != HouseMode.None)
            {
                // Rückfall ohne Film-Dialog
                if (_myHouse != HouseMode.None) OnMyHouseCommand("myhs:clos");
                _ui.GotoLabel("/myhs_editor", "default", false);
                _ui.GotoLabel("/", "living_in", true);
            }
        }

        /// <summary>Editor ohne Film-Rückmeldung verlassen: Vorschau verwerfen, zurück in den Laufmodus.</summary>
        void ForceHouseWalk()
        {
            RevertEdit();
            _myHouse = HouseMode.Walk; _houseModeAt = Time.unscaledTime; _housePreview = false;
            foreach (var n in new[] { "furniture", "material", "simple_object", "frames" }) _ui.GotoLabel("/myhs_editor/" + n, "default", false);
            _ui.GotoLabel("/myhs_editor", "myhs_wm", false);
            _ui.SetValue("/:Input", true);
            LockCursor(true);
            Debug.Log("[TDU2] My House: Editor erzwungen geschlossen");
        }

        void UpdateHouseWalk()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_fp != null) _fp.LookEnabled = Cursor.lockState == CursorLockMode.Locked;
            if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0)) LockCursor(true);
            if (_fp == null || _holder == null) return;
            // Ziel unter dem Fadenkreuz: Möbel (Anker "Furniture …") oder Boden/Wand (MAT_Floor*/MAT_Wall*)
            HouseObj obj = null; string zone = null;
            var ray = _fp.AimRay;
            float maxD = InteractDistance + 4f, hitD = maxD;
            if (Physics.Raycast(ray, out var hit, maxD, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                hitD = hit.distance;
                foreach (var o in _houseObjs)
                    if (o.Anchor != null && hit.transform.IsChildOf(o.Anchor)) { obj = o; break; }
                if (obj == null) zone = ZoneAt(hit);
            }
            // Boden/Wand genau über die Dreiecke der Zonen-Materialien, wenn der Strahl zuerst etwas anderes trifft
            // (Sicherheitsboden, Teppich-/Leistenflächen, Kollisionsvolumen)
            if (obj == null && zone == null) zone = ZoneOnRay(ray, Mathf.Min(maxD, hitD + 0.3f));
            // Möbel genau über ihre Dreiecke suchen: Glasplatten (Esstisch) haben keinen Collider, der Strahl ginge sonst hindurch
            // auf Boden/Sofa; unsichtbare Kollisionsvolumen des Raums (weder Möbel noch Boden/Wand) dürfen etwas davor liegen.
            float tol = obj == null && zone == null ? 0.6f : 0.02f;
            var picked = HouseObjOnRay(ray, Mathf.Min(maxD, hitD + tol), out _);
            if (picked != null) { obj = picked; zone = null; }
            if (obj != _aimObj || zone != _aimZone)
            {
                _aimObj = obj; _aimZone = zone;
                if (obj != null || zone != null)
                {
                    string type = obj != null ? "furniture" : "material";
                    _ui.SetVariable("/:SelItemType", "myhs_item");
                    _ui.SetVariable("/:myhsChangeType", type);
                    _ui.SetVariable("/:t_wm_destination", obj != null ? "Möbel ändern" : (Zones[zone].Type == 1 ? "Boden ändern" : "Tapete ändern"));
                    _ui.GotoLabel("/cursor", "on", true);
                    _ui.GotoLabel("/cursor/low_bar", "open", true);
                }
                else
                {
                    _ui.GotoLabel("/cursor/low_bar", "close", true);
                    _ui.GotoLabel("/cursor", "off", true);
                }
            }
            if (Time.unscaledTime - _houseModeAt < 0.3f) return;
            bool use = Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || (Input.GetMouseButtonDown(0) && Cursor.lockState == CursorLockMode.Locked);
            if (use && (_aimObj != null || _aimZone != null))
            {
                if (!HouseCatalogReady) { ShowInfo("My House: Katalog wird geladen …"); return; }
                _edit = _aimObj; _editZone = _aimObj == null ? _aimZone : null;
                _ui.SetVariable("/:myhsChangeType", _edit != null ? "furniture" : "material");
                _ui.CallClip("/", "myhsEditorOpen");   // Film: myhs:furn:open bzw. myhs:mate:open
            }
            // ESC/Rücktaste: zentraler Menüablauf (TDU2MenuFlow.cs -> QuitMyHouse, wie "Ja" auf "My House verlassen?")
#endif
        }

        /// <summary>Nächstes Möbelstück auf dem Strahl (Dreieckstest je Mesh, Bounds nur als Vorfilter).</summary>
        HouseObj HouseObjOnRay(Ray ray, float maxDist, out float dist)
        {
            HouseObj best = null; float bestD = maxDist;
            foreach (var o in _houseObjs)
            {
                if (o.Anchor == null) continue;
                foreach (var mf in o.Anchor.GetComponentsInChildren<MeshFilter>())
                {
                    var r = mf.GetComponent<Renderer>();
                    if (r == null || !r.enabled || mf.sharedMesh == null) continue;
                    if (!r.bounds.IntersectRay(ray, out float bd) || bd > bestD) continue;
                    float d = RayMesh(ray, mf, bestD);
                    if (d < bestD) { bestD = d; best = o; }
                }
            }
            dist = bestD;
            return best;
        }

        List<(MeshFilter mf, int[] tris, string zone)> _zoneParts;

        string ZoneOnRay(Ray ray, float maxDist)
        {
            var zones = Zones;
            if (zones == null || zones.Count == 0 || _holder == null) return null;
            if (_zoneParts == null)
            {
                _zoneParts = new List<(MeshFilter, int[], string)>();
                foreach (var mf in _holder.GetComponentsInChildren<MeshFilter>(true))
                {
                    var r = mf.GetComponent<Renderer>(); var mesh = mf.sharedMesh;
                    if (r == null || mesh == null || !mesh.isReadable) continue;
                    bool furn = false;
                    foreach (var o in _houseObjs) if (o.Anchor != null && mf.transform.IsChildOf(o.Anchor)) { furn = true; break; }
                    if (furn) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                        foreach (var z in zones.Values)
                            if (z.Mats.Contains(mats[i])) { _zoneParts.Add((mf, mesh.GetTriangles(i), z.Key)); break; }
                }
            }
            string best = null; float bestD = maxDist;
            foreach (var (mf, tris, zone) in _zoneParts)
            {
                if (mf == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled || !r.bounds.IntersectRay(ray, out float bd) || bd > bestD) continue;
                float d = RayTris(ray, mf, tris, bestD);
                if (d < bestD) { bestD = d; best = zone; }
            }
            return best;
        }

        readonly Dictionary<Mesh, (Vector3[] v, int[] t)> _pickMeshes = new Dictionary<Mesh, (Vector3[], int[])>();
        readonly Dictionary<Mesh, Vector3[]> _pickVerts = new Dictionary<Mesh, Vector3[]>();

        /// <summary>Abstand des ersten Dreiecks-Treffers (Welt), float.MaxValue wenn keiner. Nicht lesbare Meshes: Bounds-Abstand.</summary>
        float RayMesh(Ray ray, MeshFilter mf, float maxDist)
        {
            var mesh = mf.sharedMesh;
            if (!mesh.isReadable) return mf.GetComponent<Renderer>().bounds.IntersectRay(ray, out float bd) ? bd : float.MaxValue;
            if (!_pickMeshes.TryGetValue(mesh, out var md)) _pickMeshes[mesh] = md = (mesh.vertices, mesh.triangles);
            return RayTris(ray, mf, md.t, maxDist);
        }

        float RayTris(Ray ray, MeshFilter mf, int[] t, float maxDist)
        {
            var mesh = mf.sharedMesh;
            if (!_pickVerts.TryGetValue(mesh, out var v)) _pickVerts[mesh] = v = mesh.vertices;
            var tr = mf.transform;
            Vector3 o = tr.InverseTransformPoint(ray.origin), dir = tr.InverseTransformVector(ray.direction);   // mit Skalierung, passend zu InverseTransformPoint
            float bestT = float.MaxValue;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], e1 = v[t[i + 1]] - a, e2 = v[t[i + 2]] - a;
                var p = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, p);
                if (det > -1e-7f && det < 1e-7f) continue;   // beidseitig (Möbel sind nicht immer geschlossen)
                float inv = 1f / det;
                var s = o - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(s, e1);
                float w = Vector3.Dot(dir, q) * inv;
                if (w < 0f || u + w > 1f) continue;
                float tt = Vector3.Dot(e2, q) * inv;
                if (tt > 0f && tt < bestT) bestT = tt;
            }
            if (bestT == float.MaxValue) return float.MaxValue;
            float d = Vector3.Distance(ray.origin, tr.TransformPoint(o + dir * bestT));
            return d <= maxDist ? d : float.MaxValue;
        }

        /// <summary>Zone (Boden/Wand) der getroffenen Fläche: Teilmesh am Dreieck.</summary>
        string ZoneAt(RaycastHit hit)
        {
            var zones = Zones;
            if (zones == null || zones.Count == 0) return null;
            var mc = hit.collider as MeshCollider;
            var r = hit.collider != null ? hit.collider.GetComponent<Renderer>() : null;
            if (mc == null || r == null || mc.sharedMesh == null) return null;
            var mesh = mc.sharedMesh;
            int sub = 0;
            if (mesh.subMeshCount > 1 && hit.triangleIndex >= 0)
            {
                int tri = hit.triangleIndex * 3;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var sm = mesh.GetSubMesh(i);
                    if (tri >= sm.indexStart && tri < sm.indexStart + sm.indexCount) { sub = i; break; }
                }
            }
            var mats = r.sharedMaterials;
            if (sub >= mats.Length || mats[sub] == null) return null;
            foreach (var z in zones.Values) if (z.Mats.Contains(mats[sub])) return z.Key;
            return null;
        }

        // ------------------------------------------------------------------ Befehle des Films (myhs:*)
        bool OnMyHouseCommand(string c)
        {
            switch (c)
            {
                case "myhs:open": StartMyHouse(); return true;
                case "myhs:clos": EndMyHouse(); return true;
                case "myhs:prin": _housePreview = true; LockCursor(true); return true;    // Vorschau: frei umsehen
                case "myhs:prou": _housePreview = false; LockCursor(false); return true;
                case "myhs:bdlc": return true;
                // Möbel
                case "myhs:furn:open": FurnitureOpen(); return true;
                case "myhs:furn:chsh": FurnitureShape(); return true;
                case "myhs:furn:chco": FurnitureColor(); return true;
                case "myhs:furn:scco": return true;
                case "myhs:furn:puok": Purchase(); return true;
                case "myhs:furn:clos": RevertEdit(); _myHouse = HouseMode.Walk; _houseModeAt = Time.unscaledTime; LockCursor(true); _ui.SetValue("/:Input", true); return true;
                // Böden / Wände
                case "myhs:mate:open": MaterialOpen(); return true;
                case "myhs:mate:chma": MaterialPreview(); return true;
                case "myhs:mate:scma": return true;
                case "myhs:mate:puok": Purchase(); return true;
                case "myhs:mate:clos": RevertEdit(); _myHouse = HouseMode.Walk; _houseModeAt = Time.unscaledTime; LockCursor(true); _ui.SetValue("/:Input", true); return true;
                case "myhs:sobj:open":
                case "myhs:fram:open":
                    ShowInfo("Dieses Objekt kann hier nicht geändert werden.");
                    _ui.CallClip("/myhs_editor", "BackToWM");
                    return true;
                case "wmon": case "wmof": case "camf": case "camn": case "dlci": return _myHouse != HouseMode.None;
            }
            if (c.StartsWith("myhs:mate:dima:")) { RefreshMaterialThumbs(); return true; }
            if (c.StartsWith("myhs:furn:dico:")) return true;
            return false;
        }

        // ------------------------------------------------------------------ Möbel-Editor
        void FurnitureOpen()
        {
            if (_edit == null) { _ui.CallClip("/myhs_editor", "BackToWM"); return; }
            _myHouse = HouseMode.Furniture; _houseModeAt = Time.unscaledTime; _housePreview = false;
            LockCursor(false);
            _editOrigBank = _edit.Bank;
            int dbType = _edit.Type + 1;   // DBFurniture._type = Entity.Type + 1
            var byShape = new Dictionary<string, List<FurnItem>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            // nur Stücke desselben Typs (Größe/Form passt zum Platz im Haus)
            foreach (var f in _furnCat)
            {
                if (f.Type != dbType) continue;
                string s = ShapeOf(f.Bank);
                if (!byShape.TryGetValue(s, out var l)) { byShape[s] = l = new List<FurnItem>(); order.Add(s); }
                l.Add(f);
            }
            // aktuelles Stück sicher in der Liste (auch wenn es nicht im Katalog steht)
            string curShape = ShapeOf(_edit.Bank);
            if (!byShape.ContainsKey(curShape))
            {
                string p = CarDatabase.FindFile(Path.Combine(InteriorDir, "myhouse"), _edit.Bank + ".bnk");
                byShape[curShape] = new List<FurnItem> { new FurnItem { Id = 0, Bank = _edit.Bank, Type = dbType, Path = p } };
                order.Insert(0, curShape);
            }
            _shapes = new List<List<FurnItem>>();
            foreach (var s in order) { var l = byShape[s]; l.Sort((a, b) => string.CompareOrdinal(a.Bank, b.Bank)); _shapes.Add(l); }
            _shape = Math.Max(0, order.IndexOf(curShape));
            _color = Math.Max(0, _shapes[_shape].FindIndex(f => string.Equals(f.Bank, _edit.Bank, StringComparison.OrdinalIgnoreCase)));
            _ui.SetValue("/:nb_o", Math.Max(0, _shapes.Count - 1));
            _ui.SetValue("/:num_o", _shape);
            _ui.SetValue("/:view_sq", 4);
            _ui.SetValue("/:furnitureLocked", false);
            _ui.SetValue("/:DLC_Locked", false);
            _ui.SetValue("/:IsDLC", false);
            UpdateFurnitureVars(false);
        }

        FurnItem CurFurn => _shapes != null && _shape >= 0 && _shape < _shapes.Count && _color >= 0 && _color < _shapes[_shape].Count ? _shapes[_shape][_color] : null;

        bool Owns(FurnItem f) => f != null && (f.Id == 0 || string.Equals(f.Bank, _editOrigBank, StringComparison.OrdinalIgnoreCase)
                                              || (TDU2Session.Save?.ownedFurniture?.Contains(f.Id) ?? false));

        void UpdateFurnitureVars(bool preview)
        {
            var f = CurFurn;
            if (f == null) return;
            var sv = TDU2Session.Save;
            bool owned = Owns(f);
            _ui.SetValue("/:nb_colors", Math.Max(0, _shapes[_shape].Count - 1));
            _ui.SetValue("/:color_id", _color);
            _ui.SetVariable("/:name_o", ItemName(f.NameHc, f.Bank.ToUpperInvariant()));
            _ui.SetVariable("/:CurPrice", owned ? " " : PriceText(f.Price));
            _ui.SetValue("/:IsItemBought", owned);
            _ui.SetValue("/:IsCurItem", string.Equals(f.Bank, _editOrigBank, StringComparison.OrdinalIgnoreCase));
            _ui.SetValue("/:EnoughMoney", owned || sv == null || sv.money >= f.Price);
            SetMoneyVars();
            if (preview) { _ui.SetValue("/:furnitureLoaded", false); SetFurnitureBank(_edit, f.Bank); }
            else if (!AnyFurnitureLoading) _ui.SetValue("/:furnitureLoaded", true);
        }

        void FurnitureShape()
        {
            if (_shapes == null) return;
            int n = Mathf.Clamp(ReadInt("/:num_o", _shape), 0, _shapes.Count - 1);
            if (n != _shape) { _shape = n; _color = 0; }
            UpdateFurnitureVars(true);
        }

        void FurnitureColor()
        {
            if (_shapes == null) return;
            int c = Mathf.Clamp(ReadInt("/:color_id", _color), 0, _shapes[_shape].Count - 1);
            if (c == _color) return;
            _color = c;
            UpdateFurnitureVars(true);
        }

        int ReadInt(string v, int def)
        {
            string s = _ui.GetVariable(v);
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : def;
        }

        // ------------------------------------------------------------------ Material-Editor
        void MaterialOpen()
        {
            var zones = Zones;
            if (_editZone == null || zones == null || !zones.TryGetValue(_editZone, out var z)) { _ui.CallClip("/myhs_editor", "BackToWM"); return; }
            _myHouse = HouseMode.Material; _houseModeAt = Time.unscaledTime; _housePreview = false;
            LockCursor(false);
            _editOrigMat = ZoneMaterial(z);
            _mats = new List<MatItem>();
            foreach (var m in _matCat) if (m.Type == z.Type) _mats.Add(m);
            if (!_mats.Exists(m => string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase)))
                _mats.Insert(0, new MatItem { Id = 0, Name = _editOrigMat, Type = z.Type });
            _matIdx = Math.Max(0, _mats.FindIndex(m => string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase)));
            _ui.SetValue("/:nb_m", Math.Max(0, _mats.Count - 1));
            _ui.SetValue("/:id_m", _matIdx);
            _ui.SetValue("/:view_sq", 4);
            _ui.SetValue("/:DLC_Locked", false);
            UpdateMaterialVars();
            foreach (var m in _mats) GetDonor(m.Name);   // Vorschaubilder laden
            RefreshMaterialThumbs();
            _ui.SetValue("/:MaterialLoaded", true);
        }

        bool OwnsMat(MatItem m) => m != null && (m.Id == 0 || string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase)
                                                 || (TDU2Session.Save?.ownedMaterials?.Contains(m.Id) ?? false));

        void UpdateMaterialVars()
        {
            if (_mats == null || _matIdx < 0 || _matIdx >= _mats.Count) return;
            var m = _mats[_matIdx];
            var sv = TDU2Session.Save;
            bool owned = OwnsMat(m);
            string name = ItemName(m.NameHc, m.Name.Replace('_', ' ').ToUpperInvariant());
            string price = owned ? " " : PriceText(m.Price);
            _ui.SetVariable("/:material_name", name); _ui.SetVariable("/myhs_editor/material:material_name", name);
            _ui.SetVariable("/:material_price", price); _ui.SetVariable("/myhs_editor/material:material_price", price);
            _ui.SetVariable("/:CurPrice", price);
            _ui.SetValue("/:IsItemBought", owned);
            _ui.SetValue("/:IsCurItem", string.Equals(m.Name, _editOrigMat, StringComparison.OrdinalIgnoreCase));
            _ui.SetValue("/:EnoughMoney", owned || sv == null || sv.money >= m.Price);
            SetMoneyVars();
        }

        void MaterialPreview()
        {
            if (_mats == null) return;
            _matIdx = Mathf.Clamp(ReadInt("/:id_m", _matIdx), 0, _mats.Count - 1);
            UpdateMaterialVars();
            ApplyZone(_editZone, _mats[_matIdx].Name);
        }

        /// <summary>Vorschaubilder der sichtbaren Felder (sqN/lure, Feld-Variable id = Index in der Liste).</summary>
        void RefreshMaterialThumbs()
        {
            if (_ui == null || _mats == null) return;
            for (int i = 0; i < 12; i++)
            {
                string sq = "/myhs_editor/material/sq" + i;
                if (_ui.Find(sq) == null) continue;
                int id = ReadInt(sq + ":id", -1);
                if (id < 0 || id >= _mats.Count) continue;
                var d = GetDonor(_mats[id].Name);
                if (d?.Thumb != null) _ui.SetClipTexture(sq + "/lure", d.Thumb);
            }
        }

        // ------------------------------------------------------------------ Kaufen / Abbrechen
        void Purchase()
        {
            var sv = TDU2Session.Save;
            if (_myHouse == HouseMode.Furniture && _edit != null)
            {
                var f = CurFurn;
                if (f != null)
                {
                    if (!Owns(f) && sv != null)
                    {
                        if (sv.money < f.Price) { ShowInfo("Nicht genug Geld."); RevertEdit(); _myHouse = HouseMode.Walk; return; }
                        sv.money -= f.Price;
                        sv.ownedFurniture ??= new List<int>();
                        if (f.Id != 0 && !sv.ownedFurniture.Contains(f.Id)) sv.ownedFurniture.Add(f.Id);
                    }
                    var l = Layout(true);
                    if (l != null)
                    {
                        l.furn ??= new List<HouseEntry>();
                        foreach (var o in _houseObjs)
                            if (o == _edit || (_edit.Group != 0 && o.Group == _edit.Group && o.Type == _edit.Type))
                                Store(l.furn, o.Ent.Name ?? "", f.Bank);
                    }
                    SetFurnitureBank(_edit, f.Bank);
                    Debug.Log("[TDU2] My House: " + _edit.Ent.Name + " -> " + f.Bank + " (" + f.Price + " $)");
                }
            }
            else if (_myHouse == HouseMode.Material && _editZone != null && _mats != null)
            {
                var m = _mats[_matIdx];
                if (!OwnsMat(m) && sv != null)
                {
                    if (sv.money < m.Price) { ShowInfo("Nicht genug Geld."); RevertEdit(); _myHouse = HouseMode.Walk; return; }
                    sv.money -= m.Price;
                    sv.ownedMaterials ??= new List<int>();
                    if (m.Id != 0 && !sv.ownedMaterials.Contains(m.Id)) sv.ownedMaterials.Add(m.Id);
                }
                var l = Layout(true);
                if (l != null) { l.mats ??= new List<HouseEntry>(); Store(l.mats, _editZone, m.Name); }
                ApplyZone(_editZone, m.Name);
                Debug.Log("[TDU2] My House: " + _editZone + " -> " + m.Name + " (" + m.Price + " $)");
            }
            _editOrigBank = null; _editOrigMat = null;
            _edit = null; _editZone = null;
            _myHouse = HouseMode.Walk; _houseModeAt = Time.unscaledTime;
            LockCursor(true);
            if (_ui != null) { SetMoneyVars(); _ui.SetValue("/:Input", true); }
            if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
        }

        /// <summary>Vorschau verwerfen: gespeicherten Stand wiederherstellen.</summary>
        void RevertEdit()
        {
            if (_edit != null && !string.IsNullOrEmpty(_editOrigBank)) SetFurnitureBank(_edit, _editOrigBank);
            if (_editZone != null && !string.IsNullOrEmpty(_editOrigMat)) ApplyZone(_editZone, _editOrigMat);
            _edit = null; _editZone = null; _editOrigBank = null; _editOrigMat = null;
            _shapes = null; _mats = null;
        }
    }
}
