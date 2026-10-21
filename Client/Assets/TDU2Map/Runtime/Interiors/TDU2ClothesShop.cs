using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TDU2.Map.Avatars;
using TDU2.Map.Flash;
using TDU2.Map.Online;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Kleidungsladen und Garderobe mit den Original-Filmen:
    ///  * Laden (Spots EAS_A..E, Interieurs ias_a..e, Verkäuferin = FpSelectable "Seller_area" Typ 22): frontend/&lt;res&gt;/shopsavatar.bnk,
    ///    Label clothes_shop -> cl_in/cl_stop (menu_square, _root.SelItemType "goodies") -> item_selection_in/_stop (menu_item: kaufen).
    ///  * Garderobe (Haus, FpSelectable "Dressing" Typ 10 -> Interieur idr_*): home.bnk, Label dressing_in -> root_menu_stop
    ///    (Kleidung/Motorrad) -> square_in/_stop -> item_selection_in/_stop (menu_item, WhatPage "dressing_ic": A = "seit" anziehen).
    /// FSCommands: came (Kamera /:WhatCam = Kategorie), prit/neit (/:Num_Item), buit (kaufen), seit (anziehen), moup (Geld), nsit
    /// (Auswahl verlassen), dbxo (Dialog), csou/mcou/wmon/gofr (Laden verlassen), avou/liin (Garderobe verlassen), sndp:* (Klänge).
    /// Platz des Avatars, Kameras und Posen kommen aus den Cinematic-Szenen der Interieur-Bank (C_main, C_hats, C_head_d, C_tops,
    /// C_bottoms, C_shoes, C_bracelets: Objekt "Avatar" mit Clip dr_*), die Verkäuferin steht dabei an ihrem M_LOOP-Platz.
    /// Gekaufte Stücke landen in SaveData.ownedClothes und werden sofort angezogen (SaveData.avatar -> Server -> Mitspieler).
    /// </summary>
    public sealed class TDU2ClothesShop : MonoBehaviour
    {
        public enum ShopMode { Shop, Dressing }

        public TDU2Interiors Interiors;

        static TDU2ClothesShop _inst;
        public static bool IsOpen => _inst != null && _inst._open;
        static int _closedFrame = -10;
        /// <summary>Offen oder gerade geschlossen (die schließende Taste gehört noch dem Laden).</summary>
        public static bool Busy => IsOpen || Time.frameCount - _closedFrame <= 1;

        /// <summary>Garderobe verlassen (zurück ins Wohnzimmer).</summary>
        public event Action Closed;

        // DBClothe._brand der Kollektionen
        const int BrandHighlife = 57, BrandStreetwear = 85, BrandSportswear = 84, BrandFashion = 83, BrandEden = 44;
        const int BrandCasino = 87, BrandPnj = 73, BrandDefault = 54, BrandHair = 55;

        sealed class Entry
        {
            public ClotheInfo Item;     // null = "nichts" (Teil ablegen)
            public ClothePart Part;
            public int Color;
            public bool Owned;
            public int Price => Item != null ? Item.Price : 0;
        }

        ShopMode _mode;
        TDU2InteriorNpcs _npcs;
        TDU2InteriorNpcs.Npc _seller;
        readonly Dictionary<string, VmfPlayer> _films = new Dictionary<string, VmfPlayer>(StringComparer.OrdinalIgnoreCase);
        VmfPlayer _ui;
        bool _open, _inItems;
        float _cinAt = -1f, _reopenAt = -1f, _lastEsc = -10f;
        float _catInputAt = -1f, _muteBackUntil = -1f;   // nach dem Kauf: zurück in die Kategorien
        int _brand;
        string _cat = "C_main";
        readonly List<Entry> _items = new List<Entry>();
        int _index;
        AvatarConfig _base, _preview;
        string _previewCode;

        // Anprobe
        Transform _holder;
        TDU2Avatar _avatar, _pending;
        AnmClip _clip;
        float _poseT, _turn;
        Vector3 _avatarPos; Quaternion _avatarRot = Quaternion.identity; bool _hasPlace;

        // Kamera
        /// <summary>Faktor für die Fov der Nahaufnahme-Kameras (C_hats, C_head_d, C_shoes …).</summary>
        public static float CloseUpFov = 1.45f;
        Vector3 _savedCamPos; Quaternion _savedCamRot; float _savedFov, _savedNear;
        Vector3 _camPos; Quaternion _camRot = Quaternion.identity; float _camFov = 40f;
        bool _camInit;

        void Awake() { _inst = this; }
        void OnDestroy() { if (_inst == this) _inst = null; if (_open) Close(); foreach (var f in _films.Values) if (f != null) Destroy(f.gameObject); }

        // ================================================================== Öffnen / Schließen
        /// <summary>
        /// Laden bzw. Garderobe öffnen. <paramref name="npcs"/>: Figuren und Cinematic-Szenen der Interieur-Bank (Platz des Avatars,
        /// Kameras, Verkäuferin); <paramref name="spotCategory"/> = spots.bin TypeA high byte (Ersatz für die Kollektion).
        /// </summary>
        public bool Open(ShopMode mode, string frontendBank, TDU2InteriorNpcs npcs, int spotCategory = 0)
        {
            if (_open) return true;
            if (string.IsNullOrEmpty(frontendBank) || !File.Exists(frontendBank)) return false;
            if (!AvatarLibrary.Ready) { Debug.LogWarning("[TDU2] Kleidung: Avatar-Daten noch nicht geladen"); return false; }
            _mode = mode;
            _npcs = npcs;
            string bnk = Interiors != null && Interiors.Manager != null ? Interiors.Manager.BnkDir : null;
            Racing.RaceTexts.Ensure(bnk, TDU2PauseMenu.SavedLanguage());   // Namen der Stücke (_clothename_hc)
            _base = AvatarConfigs.Local.Clone() ?? AvatarConfig.Default();
            _preview = _base.Clone();
            _previewCode = null;
            _seller = mode == ShopMode.Shop && npcs != null ? npcs.First("Seller") ?? npcs.First(null) : null;
            _brand = mode == ShopMode.Shop ? BrandFromSeller(_seller) : 0;
            if (mode == ShopMode.Shop && _brand == 0) _brand = BrandFor(spotCategory);

            _ui = Film(frontendBank, bnk);
            if (_ui == null) return false;

            var cam = Camera.main;
            if (cam != null)
            {
                _savedCamPos = cam.transform.position;
                _savedCamRot = cam.transform.rotation;
                _savedFov = cam.fieldOfView;
                _savedNear = cam.nearClipPlane;
                cam.nearClipPlane = 0.03f;
            }
            Interiors?.SetFirstPerson(false);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            _cat = "C_main";
            _turn = 0f;
            PlaceHolder(cam);
            ApplyCategoryPose();
            ShowPreview(_preview, true);
            _camInit = false;

            // Verkäuferin hinter die Theke (M_LOOP)
            if (_seller != null)
            {
                var loop = npcs.FindActor(_seller.Hc, "LOOP");
                if (loop != null) npcs.SetPose(_seller.ConfigName, loop);
            }

            _open = true;
            _inItems = false;
            _ui.gameObject.SetActive(true);
            _ui.MouseEnabled = true;
            _ui.SetValue("/:fake", false);
            _ui.SetValue("/:IsTutorial", 0);
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.SetValue("/:EnableItemBuyCin", false);
            _ui.SetValue("/:DLC_Ok", 1);
            _ui.SetVariable("/:ClubMoney", " ");
            SetCounts();
            UpdateMoney();
            if (mode == ShopMode.Shop)
            {
                _ui.GotoLabel("/", "clothes_shop", true);
                _cinAt = Time.unscaledTime + 0.6f;   // Init-Skripte von clothes_shop/clothes_WM, dann ins Menü (cl_in)
            }
            else
            {
                // Garderobe: dressing_in setzt SelItemType "dressing", root_menu_stop zeigt Kleidung/Motorrad
                _ui.GotoLabel("/", "dressing_in", true);
                _ui.SetValue("/:Input", true);
            }
            Debug.Log("[TDU2] " + (mode == ShopMode.Shop ? "Kleidungsladen" : "Garderobe") + " geöffnet: Kollektion " + _brand
                + (_seller != null ? " (" + AvatarLibrary.PnjName(_seller.ConfigName) + ")" : "") + ", " + (_base.Sex == 1 ? "Frau" : "Mann")
                + ", Avatar-Platz " + (_hasPlace ? "aus der Szene" : "vor der Kamera"));
            return true;
        }

        VmfPlayer Film(string bank, string bnkDir)
        {
            if (_films.TryGetValue(bank, out var f) && f != null) return f;
            try
            {
                var go = new GameObject("TDU2 " + Path.GetFileNameWithoutExtension(bank) + " UI");
                go.transform.SetParent(transform, false);
                f = go.AddComponent<VmfPlayer>();
                f.GuiDepth = -2;
                f.Load(bank);
                f.FSCommand += OnCommand;
                FlashLocalization.Apply(f, FlashLocalization.Load(bnkDir, TDU2PauseMenu.SavedLanguage()));
                go.SetActive(false);
                _films[bank] = f;
                return f;
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Kleidung: " + Path.GetFileName(bank) + " – " + ex.Message); return null; }
        }

        public void Close()
        {
            if (!_open) return;
            _open = false; _inItems = false; _cinAt = _reopenAt = _catInputAt = -1f;
            if (_avatar != null) Destroy(_avatar.gameObject);
            if (_pending != null) Destroy(_pending.gameObject);
            _avatar = _pending = null;
            if (_holder != null) Destroy(_holder.gameObject);
            _holder = null;
            if (_seller != null && _npcs != null) _npcs.ResetPose(_seller);
            _seller = null;
            var cam = Camera.main;
            if (cam != null) { cam.transform.SetPositionAndRotation(_savedCamPos, _savedCamRot); cam.fieldOfView = _savedFov; if (_savedNear > 0f) cam.nearClipPlane = _savedNear; }
            if (_ui != null) _ui.gameObject.SetActive(false);
            Interiors?.SetFirstPerson(true);
            _closedFrame = Time.frameCount;
            Debug.Log("[TDU2] " + (_mode == ShopMode.Shop ? "Kleidungsladen" : "Garderobe") + " geschlossen");
            try { Closed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>Kollektion aus der Verkäuferin (DBPNJ "Clothes seller Highlife" …).</summary>
        static int BrandFromSeller(TDU2InteriorNpcs.Npc s)
        {
            string n = s != null ? (AvatarLibrary.PnjName(s.ConfigName) ?? "") : "";
            if (n.IndexOf("Highlife", StringComparison.OrdinalIgnoreCase) >= 0) return BrandHighlife;
            if (n.IndexOf("Streetwear", StringComparison.OrdinalIgnoreCase) >= 0) return BrandStreetwear;
            if (n.IndexOf("Sportswear", StringComparison.OrdinalIgnoreCase) >= 0) return BrandSportswear;
            if (n.IndexOf("Fashion", StringComparison.OrdinalIgnoreCase) >= 0) return BrandFashion;
            if (n.IndexOf("DLC", StringComparison.OrdinalIgnoreCase) >= 0) return BrandEden;
            return 0;
        }

        /// <summary>Ersatz ohne Verkäuferin: Spot-Kategorie 15 Highlife, 16 Streetwear, 17 Sportswear, 18 Fashion.</summary>
        static int BrandFor(int cat)
        {
            switch (cat)
            {
                case 15: return BrandHighlife;
                case 16: return BrandStreetwear;
                case 17: return BrandSportswear;
                case 18: return BrandFashion;
                case 19: return BrandEden;
                default: return 0;
            }
        }

        // ================================================================== Sortiment
        static ClothePart[] PartsFor(string cam, int sex)
        {
            switch ((cam ?? "").ToLowerInvariant())
            {
                case "c_hats": return new[] { ClothePart.Helmet };
                case "c_head_d": return new[] { ClothePart.Glasses };
                case "c_tops": return sex == 1 ? new[] { ClothePart.Top, ClothePart.Dress } : new[] { ClothePart.Top };
                case "c_bottoms": return new[] { ClothePart.Bottom };
                case "c_shoes": return new[] { ClothePart.Shoes };
                case "c_bracelets": return new[] { ClothePart.Gloves };
                default: return null;
            }
        }

        static bool Removable(ClothePart p) => p == ClothePart.Helmet || p == ClothePart.Glasses || p == ClothePart.Gloves;

        bool Sells(ClotheInfo c, int sex, bool anyBrand)
        {
            if (c == null || !c.Valid || c.IsNothing) return false;
            if (sex == 1 ? !c.ForWoman : !c.ForMan) return false;
            if (!AvatarLibrary.HasBank(c)) return false;
            if (c.Brand == BrandPnj || c.Brand == BrandDefault || c.Brand == BrandHair || c.Brand == BrandCasino) return false;
            return anyBrand || c.Brand == _brand;
        }

        static int Order(ClotheInfo a, ClotheInfo b)
        {
            int r = a.Part.CompareTo(b.Part); if (r != 0) return r;
            r = a.SortIndex.CompareTo(b.SortIndex); if (r != 0) return r;
            r = a.Price.CompareTo(b.Price); if (r != 0) return r;
            return string.CompareOrdinal(a.File, b.File);
        }

        List<Entry> BuildItems(string cam)
        {
            var list = new List<Entry>();
            int sex = _base.Sex;
            var parts = PartsFor(cam, sex);
            if (parts == null) return list;
            var owned = OwnedSet();
            if (_mode == ShopMode.Dressing)
            {
                // Garderobe: gekaufte + getragene Stücke + das Standard-Aussehen, jeweils alle Farben
                var have = new HashSet<int>(owned);
                foreach (int h in _base.Items) have.Add(h);
                foreach (int h in AvatarConfig.Default(sex).Items) have.Add(h);
                var found = new List<ClotheInfo>();
                foreach (int h in have)
                {
                    var c = AvatarLibrary.Clothe(h);
                    if (c != null && !c.IsNothing && Array.IndexOf(parts, c.Part) >= 0 && (sex == 1 ? c.ForWoman : c.ForMan) && AvatarLibrary.HasBank(c)) found.Add(c);
                }
                found.Sort(Order);
                if (Removable(parts[0])) list.Add(new Entry { Item = null, Part = parts[0], Owned = true });
                foreach (var c in found)
                    for (int col = 0; col < Mathf.Max(1, c.NbColors); col++) list.Add(new Entry { Item = c, Part = c.Part, Color = col, Owned = true });
                return list;
            }
            foreach (bool anyBrand in new[] { false, true })
            {
                if (_brand == 0 && !anyBrand) continue;
                var found = new List<ClotheInfo>();
                foreach (var c in AvatarLibrary.AllClothes)
                    if (Array.IndexOf(parts, c.Part) >= 0 && Sells(c, sex, anyBrand)) found.Add(c);
                if (found.Count == 0) continue;   // Kollektion führt nichts davon -> ganzes Sortiment
                found.Sort(Order);
                if (Removable(parts[0])) list.Add(new Entry { Item = null, Part = parts[0], Owned = true });
                foreach (var c in found)
                {
                    int n = Mathf.Max(1, c.NbColors);
                    for (int col = 0; col < n; col++)
                        list.Add(new Entry { Item = c, Part = c.Part, Color = col, Owned = owned.Contains(c.Hash) || Wears(_base, c) });
                }
                break;
            }
            return list;
        }

        static bool Wears(AvatarConfig cfg, ClotheInfo c) => cfg != null && c != null && cfg.Items.Contains(c.Hash);

        static HashSet<int> OwnedSet()
        {
            var s = new HashSet<int>();
            var sv = TDU2Session.Save;
            if (sv?.ownedClothes != null) foreach (int h in sv.ownedClothes) s.Add(h);
            return s;
        }

        void SetCounts()
        {
            int Count(string cam) => BuildItems(cam).Count;
            _ui.SetValue("/:Nb_Hats", Count("C_hats"));
            _ui.SetValue("/:Nb_Acc", Count("C_head_d"));
            _ui.SetValue("/:Nb_Tops", Count("C_tops"));
            _ui.SetValue("/:Nb_Bottoms", Count("C_bottoms"));
            _ui.SetValue("/:Nb_Shoes", Count("C_shoes"));
            _ui.SetValue("/:Nb_Hands", Count("C_bracelets"));
            foreach (var n in new[] { "Nb_Helmets", "Nb_BikeTops", "Nb_BikeBottoms", "Nb_Watches", "Nb_Rings", "Nb_Earrings", "Nb_Chokers", "Nb_Bracelets" }) _ui.SetValue("/:" + n, 0);
            foreach (var l in new[] { "LockHats", "LockGlasses", "LockTops", "LockBottoms", "LockShoes", "LockGloves" }) _ui.SetValue("/:" + l, false);
            foreach (var l in new[] { "LockHelmets", "LockBikeTops", "LockBikeBottoms" }) _ui.SetValue("/:" + l, true);
            bool dress = false;
            foreach (int h in _base.Items) if (AvatarLibrary.Clothe(h)?.Part == ClothePart.Dress) dress = true;
            _ui.SetValue("/:IsWearingDress", dress ? 1 : 0);
        }

        // ================================================================== Befehle des Films
        void OnCommand(string cmd, string args)
        {
            if (!_open) return;
            string lc = (cmd ?? "").Trim().ToLowerInvariant();
            if (lc == "sndp:back" && Time.unscaledTime < _muteBackUntil) return;   // eigenes Schließen nach dem Kauf: kein Zurück-Ton
            if (TDU2FrontendSounds.Command(lc)) return;
            switch (lc)
            {
                case "came": OnCamera(_ui.GetVariable("/:WhatCam")); return;
                case "prit": case "neit": SelectFromFilm(); return;
                case "seit": WearCurrent(); return;
                case "nsit": _inItems = false; ShowPreview(_base, false); return;
                case "buit": Buy(); return;
                case "moup": UpdateMoney(); return;
                case "dbxo": AnswerDialog(); return;
                case "drin": case "clin": SetCounts(); return;
                case "csou":
                case "mcou":
                case "wmon":
                case "gofr":
                case "shko":
                case "avou":
                case "liin":
                    if (_mode == ShopMode.Dressing && lc == "mcou") return;   // dressing_in meldet mcou beim Öffnen
                    Close(); return;
            }
            Debug.Log("[TDU2] Kleidung: " + lc + (string.IsNullOrEmpty(args) ? "" : " " + args));
        }

        void OnCamera(string cam)
        {
            _cat = string.IsNullOrEmpty(cam) ? "C_main" : cam;
            ApplyCategoryPose();
            var parts = PartsFor(_cat, _base.Sex);
            if (parts == null)
            {
                // Hauptmenü: Anprobe zurück auf das Getragene
                _inItems = false;
                ShowPreview(_base, false);
                return;
            }
            _items.Clear();
            _items.AddRange(BuildItems(_cat));
            _inItems = true;
            // mit dem getragenen Stück der Kategorie beginnen
            _index = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                if (e.Item != null && Wears(_base, e.Item) && _base.ColorOf(e.Item.Hash) == e.Color) { _index = i; break; }
            }
            _ui.SetValue("/:Nb_Items", _items.Count);
            _ui.SetValue("/:Num_Item", _index + 1);
            UpdateItem();
        }

        void SelectFromFilm()
        {
            if (!_inItems || _items.Count == 0) return;
            int n = 1;
            int.TryParse(_ui.GetVariable("/:Num_Item") ?? "1", NumberStyles.Float, CultureInfo.InvariantCulture, out n);
            _index = Mathf.Clamp(n - 1, 0, _items.Count - 1);
            UpdateItem();
        }

        Entry Current => _inItems && _index >= 0 && _index < _items.Count ? _items[_index] : null;

        void UpdateItem()
        {
            var e = Current;
            if (e == null) return;
            long money = TDU2Session.Save != null ? TDU2Session.Save.money : 0;
            _ui.SetVariable("/:ItemName", ItemName(e));
            _ui.SetVariable("/:Item_brand", BrandLabel(e));
            _ui.SetValue("/:Solded", _mode == ShopMode.Shop && e.Owned);
            _ui.SetValue("/:IsEnoughMoney", e.Owned || TDU2Session.Save == null || money >= e.Price);
            _ui.SetValue("/:DLC_Locked", false);
            string price = e.Owned || _mode == ShopMode.Dressing ? " " : Money(e.Price);
            _ui.SetVariable("/:CurrentPrice", price);
            _ui.SetVariable("CurrentPrice", price);
            foreach (var o in _ui.FunctionOwners("updateBrandLabel")) _ui.CallClip(o, "updateBrandLabel");
            ShowPreview(Apply(_base, e), false);
        }

        string ItemName(Entry e)
        {
            if (e.Item == null) return PartName(e.Part) + " –";
            string n = e.Item.NameHc != 0 ? Racing.RaceTexts.Get((ulong)(uint)e.Item.NameHc, null) : null;
            if (string.IsNullOrEmpty(n)) n = Pretty(e.Item.File);
            int colors = Mathf.Max(1, e.Item.NbColors);
            return colors > 1 ? n + "  (" + (e.Color + 1) + "/" + colors + ")" : n;
        }

        static string PartName(ClothePart p)
        {
            switch (p)
            {
                case ClothePart.Helmet: return "Hut";
                case ClothePart.Glasses: return "Brille";
                case ClothePart.Gloves: return "Handschuhe";
                default: return p.ToString();
            }
        }

        /// <summary>"M_SW_T_ShortSleeves_Long_01" -> "ShortSleeves Long 01".</summary>
        static string Pretty(string file)
        {
            var parts = (file ?? "").Split('_');
            int start = parts.Length > 3 ? 3 : 0;
            return string.Join(" ", parts, start, parts.Length - start);
        }

        /// <summary>Logo-Etikett im Film (menu_item/labels/etiquette: brand00..brand12, none) – über DBClothe._subbrand.</summary>
        static string BrandLabel(Entry e)
        {
            int sb = e.Item != null ? e.Item.SubBrand : -1;
            return sb >= 0 && sb <= 12 ? "brand" + sb.ToString("00", CultureInfo.InvariantCulture) : "none";
        }

        /// <summary>Aussehen mit dem Stück (Kleid ersetzt Ober- und Unterteil und umgekehrt; "nichts" legt das Teil ab).</summary>
        static AvatarConfig Apply(AvatarConfig cfg, Entry e)
        {
            var c = cfg.Clone();
            if (e == null) return c;
            void Remove(ClothePart p)
            {
                for (int i = c.Items.Count - 1; i >= 0; i--)
                    if (AvatarLibrary.Clothe(c.Items[i])?.Part == p) { c.Items.RemoveAt(i); if (i < c.Colors.Count) c.Colors.RemoveAt(i); }
            }
            if (e.Item == null) { Remove(e.Part); return c; }
            if (e.Part == ClothePart.Dress) { Remove(ClothePart.Top); Remove(ClothePart.Bottom); }
            if (e.Part == ClothePart.Top || e.Part == ClothePart.Bottom)
            {
                bool hadDress = false;
                foreach (int h in c.Items) if (AvatarLibrary.Clothe(h)?.Part == ClothePart.Dress) hadDress = true;
                Remove(ClothePart.Dress);
                // Kleid ausgezogen: das fehlende Gegenstück aus dem Standard-Aussehen ergänzen
                if (hadDress)
                {
                    var def = AvatarConfig.Default(c.Sex);
                    foreach (int h in def.Items)
                    {
                        var ci = AvatarLibrary.Clothe(h);
                        if (ci != null && (ci.Part == ClothePart.Top || ci.Part == ClothePart.Bottom) && ci.Part != e.Part) c.Set(ci, 0);
                    }
                }
            }
            c.Set(e.Item, e.Color);
            return c;
        }

        void Buy()
        {
            var e = Current;
            // Der Film hat die Eingabe schon gesperrt (ValidateItemBuy: Input = false, Hilfe zu) -> in jedem Fall wieder herausführen
            _ui.SetValue("/:EnableItemBuyCin", false);
            _reopenAt = Time.unscaledTime + 0.9f;
            if (e == null) return;
            var sv = TDU2Session.Save;
            if (!e.Owned && sv != null)
            {
                if (sv.money < e.Price) { Debug.Log("[TDU2] Kleidungsladen: zu wenig Geld"); return; }
                sv.money = Math.Max(0, sv.money - e.Price);
            }
            if (e.Item != null && sv != null)
            {
                sv.ownedClothes ??= new List<int>();
                if (!sv.ownedClothes.Contains(e.Item.Hash)) sv.ownedClothes.Add(e.Item.Hash);
            }
            Wear(e);
            Debug.Log("[TDU2] Kleidungsladen: gekauft " + (e.Item != null ? e.Item.File + " Farbe " + e.Color : "(ablegen)") + " für $" + (e.Owned ? 0 : e.Price));
            foreach (var x in _items) if (x.Item != null && e.Item != null && x.Item.Hash == e.Item.Hash) x.Owned = true;
            // Kauf-Kinematik (M_BUY_ITEM) gibt es hier nicht: nach der Bestätigungs-Animation das Stückmenü sauber schließen (Update)
        }

        /// <summary>
        /// Nach dem Kauf: Stückmenü wie mit B schließen (menu_item.B: close-Animation, nsit; Bild 38 springt bei WhatPage
        /// "clothes_ic" nach cl_in = Kategorien). Früher wurde item_selection_stop erneut angesprungen – das öffnete menu_item
        /// ein zweites Mal über die laufende "valid"-Animation, ein Untermenü blieb offen und die Eingabe hing.
        /// </summary>
        void CloseItemsAfterBuy()
        {
            _ui.SetValue("/:Input", true);   // B prüft Input
            string owner = null;
            foreach (var o in _ui.FunctionOwners("B"))
                if (o.IndexOf("menu_item", StringComparison.OrdinalIgnoreCase) >= 0 && (owner == null || o.Length < owner.Length)) owner = o;
            _muteBackUntil = Time.unscaledTime + 0.3f;
            if (owner == null || !_ui.CallClip(owner, "B"))
            {
                _ui.GotoLabel("/menu_item", "close", true);
                _inItems = false; ShowPreview(_base, false);
            }
            _inItems = false;
            _inputAt = -1f;
            _catInputAt = Time.unscaledTime + 0.8f;   // Kategorien: Eingabe frei, falls cl_in sie nicht selbst freigibt
            UpdateMoney();
        }

        /// <summary>Gekauftes bzw. bereits besessenes Stück anziehen (Spielstand + Server, Mitspieler).</summary>
        void Wear(Entry e)
        {
            _base = Apply(_base, e);
            AvatarConfigs.SetLocal(_base);
            ShowPreview(_base, false);
            UpdateMoney();
        }

        void WearCurrent()
        {
            var e = Current;
            if (e != null && e.Owned) { Wear(e); TDU2FrontendSounds.Command("sndp:valid"); UpdateItem(); }
        }

        void UpdateMoney()
        {
            if (_ui == null) return;
            var sv = TDU2Session.Save;
            _ui.SetVariable("/:MyMoneyStr", sv != null ? Money(sv.money) : "");
        }

        static string Money(long v) => "   " + v.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + "     ";

        void AnswerDialog()
        {
            string dc = _ui.GetVariable("/:DgbxCase") ?? "";
            Debug.Log("[TDU2] Kleidung: Dialog '" + dc + "' -> ja");
            _ui.SetVariable("/:PlayerAnswer", "a");
            var owners = _ui.FunctionOwners("DialogBox");
            if (owners.Count > 0) _ui.CallClip(owners[owners.Count - 1], "DialogBox");
            else _ui.CallClip("/", "DialogBox");
        }

        // ================================================================== Anprobe-Avatar (Platz aus den Cinematic-Szenen)
        CineScene.Obj SceneObject(string cat, bool camera)
        {
            if (_npcs == null) return null;
            foreach (var kv in _npcs.Cines)
            {
                var sc = kv.Value;
                if (sc.Find(cat) == null) continue;   // Szene mit der Kamera dieser Kategorie
                if (camera) return sc.Find(cat);
                foreach (var o in sc.Objects) if (!o.IsCamera && o.HasPosition && o.Name.StartsWith("Avatar", StringComparison.OrdinalIgnoreCase)) return o;
            }
            return null;
        }

        void PlaceHolder(Camera cam)
        {
            var scene = _npcs != null ? _npcs.Scene : null;
            var go = new GameObject("TDU2 Anprobe");
            _holder = go.transform;
            var place = SceneObject("C_main", false);
            _hasPlace = place != null && scene != null;
            if (_hasPlace)
            {
                // im Spielraum der Szene (Elternteil = Interieur, gespiegelt wie die Szene)
                _holder.SetParent(scene, false);
                _avatarPos = place.Position; _avatarRot = place.Rotation;
                _holder.localPosition = _avatarPos; _holder.localRotation = _avatarRot;
                return;
            }
            // Ersatz: vor der Kamera, mit Blick zu ihr
            Vector3 origin = cam != null ? cam.transform.position : transform.position;
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
            fwd.y = 0f; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();
            Vector3 p = origin + fwd * 2.4f;
            float floor = origin.y - 1.7f;
            if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var hit, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore)) floor = hit.point.y;
            p.y = floor;
            if (scene != null)
            {
                _holder.SetParent(scene, false);
                _holder.position = p;
                var f = scene.InverseTransformVector(-fwd); f.y = 0f;
                _holder.localRotation = f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
            }
            else { _holder.position = p; _holder.rotation = Quaternion.LookRotation(-fwd, Vector3.up); _holder.localScale = new Vector3(-1f, 1f, 1f); }
            _avatarPos = _holder.localPosition; _avatarRot = _holder.localRotation;
        }

        /// <summary>Pose der Kategorie (C_hats -> dr_hat-m …) aus der Szene; Mann/Frau passend getauscht.</summary>
        void ApplyCategoryPose()
        {
            var act = SceneObject(_cat, false) ?? SceneObject("C_main", false);
            string clip = act?.Clip;
            if (string.IsNullOrEmpty(clip)) clip = _mode == ShopMode.Shop ? "clot_standloop-m" : "dr_standdressing-m";
            bool woman = _base != null && _base.Sex == 1;
            if (woman && clip.EndsWith("-m")) clip = clip.Substring(0, clip.Length - 2) + "-w";
            else if (!woman && clip.EndsWith("-w")) clip = clip.Substring(0, clip.Length - 2) + "-m";
            var c = _npcs != null ? _npcs.Clip(clip) : AvatarLibrary.Clip(clip);
            if (c == null) c = AvatarLibrary.Clip(woman ? "co_stand-w" : "co_stand-m");
            if (c != _clip) { _clip = c; _poseT = 0f; }
            if (act != null && _holder != null && _hasPlace) { _avatarPos = act.Position; _avatarRot = act.Rotation; }
        }

        void ShowPreview(AvatarConfig cfg, bool force)
        {
            if (cfg == null || _holder == null) return;
            string code = cfg.Encode();
            if (!force && code == _previewCode) return;
            _previewCode = code;
            _preview = cfg;
            if (_pending != null) Destroy(_pending.gameObject);
            _pending = TDU2Avatar.Create(_holder, cfg, "TDU2 Anprobe-Avatar");
            _pending.InCar = false;
            _pending.Built += a =>
            {
                if (a != _pending) return;
                if (_avatar != null) Destroy(_avatar.gameObject);
                _avatar = a; _pending = null;
                Pose(a);
            };
        }

        void Pose(TDU2Avatar a)
        {
            if (a == null || !a.Ready) return;
            a.transform.localPosition = Vector3.zero;
            a.transform.localRotation = Quaternion.identity;
            a.BeginPose();
            if (_clip != null) a.ApplyClip(_clip, Mathf.Repeat(_poseT, _clip.Length > 0.01f ? _clip.Length : 1f), 1f, true);
            a.CommitPose();
        }

        // ================================================================== Eingabe + Kamera
        void Update()
        {
            if (!_open || _ui == null) return;
            if (_cinAt > 0f && Time.unscaledTime >= _cinAt)
            {
                _cinAt = -1f;
                // Laufmodus überspringen: was clothes_WM per Taste setzen würde
                _ui.SetVariable("/:SelItemType", "goodies");
                _ui.GotoLabel("/", "cl_in", true);
                _ui.SetValue("/:Input", true);
            }
            // Scroll sperrt die Eingabe bis zum Ende der Blätter-Animation; falls die nicht zurückschaltet, nach kurzer Zeit wieder frei
            if (_inputAt > 0f && Time.unscaledTime >= _inputAt) { _inputAt = -1f; if (_inItems) _ui.SetValue("/:Input", true); }
            if (_reopenAt > 0f && Time.unscaledTime >= _reopenAt) { _reopenAt = -1f; CloseItemsAfterBuy(); }
            if (_catInputAt > 0f && Time.unscaledTime >= _catInputAt) { _catInputAt = -1f; if (!_inItems) _ui.SetValue("/:Input", true); }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (TDU2GameData.ChatTyping) return;
            // In der Stückauswahl (menu_item) nur links/rechts: hoch/runter erreichte sonst das Kategorie-Menü dahinter
            if (!_inItems)
            {
                if (Input.GetKeyDown(KeyCode.UpArrow)) _ui.Input("Move", "up");
                if (Input.GetKeyDown(KeyCode.DownArrow)) _ui.Input("Move", "down");
            }
            // links/rechts in der Stückauswahl direkt an menu_item (sonst fing menu_square dahinter die Eingabe und öffnete andere Menüs)
            if (Input.GetKeyDown(KeyCode.LeftArrow)) MenuInput("Move", "left");
            if (Input.GetKeyDown(KeyCode.RightArrow)) MenuInput("Move", "right");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space))
            {
                // Laden: bereits gekauft -> der Film macht nichts ("Solded"), direkt anziehen
                var e = Current;
                if (_mode == ShopMode.Shop && e != null && e.Owned && _reopenAt < 0f) WearCurrent();
                else MenuInput("A");
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                // doppeltes ESC: sicher verlassen (falls der Film hängt)
                if (Time.unscaledTime - _lastEsc < 1.2f) { Close(); return; }
                _lastEsc = Time.unscaledTime;
                if (!MenuInput("B")) Close();
            }
            // Avatar drehen: Q/E bzw. rechte Maustaste
            if (Input.GetKey(KeyCode.Q)) _turn -= 90f * Time.unscaledDeltaTime;
            if (Input.GetKey(KeyCode.E)) _turn += 90f * Time.unscaledDeltaTime;
            if (Input.GetMouseButton(1)) _turn += Input.GetAxis("Mouse X") * 4f;
#endif
        }

        /// <summary>Eingabe an das geöffnete Menü: in der Stückauswahl an den Clip "menu_item", sonst wie üblich (Fokus-Menü).</summary>
        float _inputAt = -1f;

        bool MenuInput(string fn, params object[] args)
        {
            if (_inItems)
            {
                // menu_item kennt kein Move: das Spiel ruft für links/rechts menu_item.Scroll("left"/"right") auf
                // (blättert Num_Item, FSCommand prit/neit, Animation change_left/right)
                if (fn == "Move" && args != null && args.Length > 0 && (args[0] as string == "left" || args[0] as string == "right"))
                {
                    string sc = null;
                    foreach (var o in _ui.FunctionOwners("Scroll"))
                        if (o.IndexOf("menu_item", StringComparison.OrdinalIgnoreCase) >= 0 && (sc == null || o.Length < sc.Length)) sc = o;
                    if (sc != null) { _ui.CallClip(sc, "Scroll", args[0]); _inputAt = Time.unscaledTime + 0.7f; return true; }
                    return false;   // nie an menu_square (würde die Kategorie wechseln)
                }
                string best = null;
                foreach (var o in _ui.FunctionOwners(fn))
                    if (o.IndexOf("menu_item", StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || o.Length < best.Length)) best = o;
                if (best != null && _ui.CallClip(best, fn, args)) return true;
            }
            return _ui.Input(fn, args);
        }

        void LateUpdate()
        {
            if (!_open) return;
            _poseT += Time.deltaTime;
            if (_holder != null && _holder.parent != null)
            {
                _holder.localPosition = _avatarPos;
                _holder.localRotation = Quaternion.AngleAxis(_turn, Vector3.up) * _avatarRot;
            }
            if (_avatar != null) Pose(_avatar);
            UpdateCamera();
        }

        /// <summary>Kamera der Kategorie aus der Szene (C_main, C_hats …: Position, Blick, Fov); ohne Szene um den Avatar.</summary>
        void UpdateCamera()
        {
            var cam = Camera.main;
            if (cam == null || _holder == null) return;
            Vector3 pos; Quaternion rot; float fov;
            var sc = _npcs != null && _npcs.Scene != null ? (SceneObject(_cat, true) ?? SceneObject("C_main", true)) : null;
            if (sc != null && sc.HasPosition)
            {
                CineScene.ToWorld(_npcs.Scene, sc, out pos, out rot);
                fov = Mathf.Clamp(sc.Fov, 10f, 90f);
                // Nahaufnahmen (Hüte, Brillen/Accessoires, Schuhe …): das Original zeigt sie in 4:3 – hier etwas weiter, damit Kopf
                // bzw. Füße ganz im Bild sind (Seitenverhältnis und Kamera-Nähe)
                if (fov < 30f) fov = Mathf.Min(fov * CloseUpFov, 40f);
            }
            else
            {
                var a = _avatar;
                Vector3 look = _holder.position + Vector3.up * 1.0f;
                if (a != null && a.Ready && a.Bones != null) { int b = a.Bone("SPINE1"); if (b >= 0 && a.Bones[b] != null) look = a.Bones[b].position; }
                Vector3 front = _savedCamPos - _holder.position; front.y = 0f;
                if (front.sqrMagnitude < 1e-4f) front = Vector3.forward;
                pos = look + front.normalized * 2.8f + Vector3.up * 0.3f;
                rot = Quaternion.LookRotation(look - pos, Vector3.up);
                fov = 45f;
            }
            if (!_camInit) { _camPos = cam.transform.position; _camRot = cam.transform.rotation; _camFov = cam.fieldOfView; _camInit = true; }
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 4f);
            _camPos = Vector3.Lerp(_camPos, pos, k);
            _camRot = Quaternion.Slerp(_camRot, rot, k);
            _camFov = Mathf.Lerp(_camFov, fov, k);
            cam.transform.SetPositionAndRotation(_camPos, _camRot);
            cam.fieldOfView = _camFov;
        }
    }
}
