using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TDU2.Formats;
using TDU2.IO;
using TDU2.Online;
using TDU2.World;
using UnityEngine;

namespace TDU2.Avatars
{
    /// <summary>DBClothe._parttype</summary>
    public enum ClothePart { None = 0, Bottom = 1, Glasses = 2, Hair = 3, Gloves = 4, Helmet = 5, Shoes = 6, Top = 7, Dress = 8, Beard = 9, Accessories = 10, Belt = 11 }

    /// <summary>Ein Kleidungsstück aus DBClothe (db_data.cpr).</summary>
    public sealed class ClotheInfo
    {
        public int Hash, IdNum, Sex, Price, NbColors, Brand, SubBrand, SortIndex, MinAreaLevel, NameHc, HairCutStyle, DrivingType;
        public ClothePart Part;
        public string File = "";
        public bool Valid, Dlc, PreOrder;
        /// <summary>Körperteile, die sichtbar bleiben (false = verdeckt). Namen wie die DBClothe-Felder ohne "_".</summary>
        public readonly Dictionary<string, bool> Visible = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public bool IsNothing => File.IndexOf("_NO_", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool ForMan => Sex != 1;
        public bool ForWoman => Sex != 2;
        public override string ToString() => $"{File} ({Part}, #{Hash})";
    }

    /// <summary>
    /// Aussehen eines Avatars: Geschlecht, Herkunft (Haut/Kopf) und je Teil ein DBClothe-Hashcode mit Farbe.
    /// Kompakt als Text kodierbar (Spielstand, Server-Spielerliste): "a1;Geschlecht;Herkunft;Hash:Farbe,Hash:Farbe,…".
    /// </summary>
    [Serializable]
    public sealed class AvatarConfig
    {
        public int Sex;        // 0 Mann, 1 Frau
        public int Ethnic;     // 0 occidental, 1 asiatisch, 2 afrikanisch (Kopf und Hautfarbe)
        public List<int> Items = new List<int>();
        public List<int> Colors = new List<int>();

        public string Encode()
        {
            var sb = new StringBuilder("a1;");
            sb.Append(Sex).Append(';').Append(Ethnic).Append(';');
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Items[i].ToString(CultureInfo.InvariantCulture)).Append(':').Append((i < Colors.Count ? Colors[i] : 0).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static AvatarConfig Decode(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("a1;", StringComparison.Ordinal)) return null;
            var parts = s.Split(';');
            if (parts.Length < 3) return null;
            var c = new AvatarConfig();
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Sex);
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Ethnic);
            c.Sex = Mathf.Clamp(c.Sex, 0, 1); c.Ethnic = Mathf.Clamp(c.Ethnic, 0, 2);
            if (parts.Length > 3 && parts[3].Length > 0)
                foreach (var it in parts[3].Split(','))
                {
                    var kv = it.Split(':');
                    if (kv.Length < 1 || !int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) continue;
                    int col = 0;
                    if (kv.Length > 1) int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out col);
                    c.Items.Add(h); c.Colors.Add(col);
                }
            return c;
        }

        public AvatarConfig Clone() => Decode(Encode());

        /// <summary>Teil setzen (ersetzt ein vorhandenes Stück desselben Typs).</summary>
        public void Set(ClotheInfo item, int color)
        {
            if (item == null) return;
            for (int i = Items.Count - 1; i >= 0; i--)
                if (AvatarLibrary.Clothe(Items[i])?.Part == item.Part) { Items.RemoveAt(i); if (i < Colors.Count) Colors.RemoveAt(i); }
            Items.Add(item.Hash); Colors.Add(Mathf.Clamp(color, 0, Mathf.Max(0, item.NbColors - 1)));
        }

        public int ColorOf(int hash) { int i = Items.IndexOf(hash); return i >= 0 && i < Colors.Count ? Colors[i] : 0; }

        /// <summary>Standard-Aussehen (Streetwear wie ein neuer Spieler, Haare Brad bzw. Miami).</summary>
        public static AvatarConfig Default(int sex = 0)
        {
            var c = new AvatarConfig { Sex = sex, Ethnic = 0 };
            string[] files = sex == 0
                ? new[] { "M_HR_R_Brad", "M_SW_T_ShortSleeves_Long_01", "M_SW_B_Pant_00", "M_SW_S_Shoes_00" }
                : new[] { "W_HR_R_Miami", "W_SW_T_ShortSleeves_Long_01a", "W_SW_B_Pant_00", "W_SW_S_Shoes_Low_01" };
            foreach (var f in files) { var ci = AvatarLibrary.ClotheByFile(f); if (ci != null) { c.Items.Add(ci.Hash); c.Colors.Add(0); } }
            return c;
        }
    }

    /// <summary>CPU-Daten eines Avatar-Teils (Körper, Kopf, Kleidungsstück), im Hintergrund gelesen.</summary>
    public sealed class AvatarPartData
    {
        public string Name;
        public readonly List<SkinPrim> Prims = new List<SkinPrim>();
        public readonly List<MaterialDef> PrimMaterials = new List<MaterialDef>();
        public readonly LocalTextureSet Textures = new LocalTextureSet();
        public bool HasHairColor;
        public Color HairPrim, HairScd;
    }

    /// <summary>
    /// Zentrale Avatar-Daten: DBClothe, Bankindex avatar/clothes/&lt;Kollektion&gt;/&lt;datei&gt;.bnk, gemeinsame Texturen
    /// (common_bodies.bnk, clothes/common_clothes.bnk), Skelette und Animationen (scenarics/avatars/avatarsdriver.bnk).
    /// </summary>
    public static class AvatarLibrary
    {
        public static string BnkDir { get; private set; }
        public static bool Ready => _clothes != null;
        static Dictionary<int, ClotheInfo> _clothes;
        static readonly Dictionary<string, ClotheInfo> _byFile = new Dictionary<string, ClotheInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _bankByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new object();
        public static readonly GlobalTextureCache CommonTextures = new GlobalTextureCache();
        static readonly Dictionary<int, XsbSkeleton> _skel = new Dictionary<int, XsbSkeleton>();
        static readonly Dictionary<string, AnmClip> _clips = new Dictionary<string, AnmClip>(StringComparer.OrdinalIgnoreCase);
        static BnkArchive _bodies, _anims;
        static readonly Dictionary<string, (AvatarConfig cfg, string name)> _pnj = new Dictionary<string, (AvatarConfig, string)>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] PnjSlots = { "hair", "top", "bottom", "gloves", "shoes", "glasses", "helmet", "dress", "beard", "accessories" };

        /// <summary>Aussehen einer Figur (DBPNJ._configid, z.B. "SPT_Clothes_Seller04"); null = unbekannt.</summary>
        public static AvatarConfig PnjConfig(string configId) => configId != null && _pnj.TryGetValue(configId, out var p) ? p.cfg.Clone() : null;
        /// <summary>DBPNJ._pnj_name (z.B. "Clothes seller Highlife").</summary>
        public static string PnjName(string configId) => configId != null && _pnj.TryGetValue(configId, out var p) ? p.name : null;

        public static readonly string[] BodyFlags =
        {
            "abdomen", "ankles", "arms", "back", "calves", "cleavage", "cleavage_large", "cleavage_sides", "feet", "forearms", "hands",
            "knees", "lower_abdomen", "lower_back", "pelvis", "shoulders", "thighs", "upper_back_center", "upper_back_sides", "upper_chest", "base",
        };

        public static IEnumerable<ClotheInfo> AllClothes { get { if (_clothes != null) foreach (var c in _clothes.Values) yield return c; } }
        public static ClotheInfo Clothe(int hash) => _clothes != null && _clothes.TryGetValue(hash, out var c) ? c : null;
        public static ClotheInfo ClotheByFile(string file) => file != null && _byFile.TryGetValue(file, out var c) ? c : null;
        public static bool HasBank(ClotheInfo c) => c != null && _bankByStem.ContainsKey(c.File);

        /// <summary>Thread-sicher; einmalig. <paramref name="dbRoot"/> = Wurzel von db_data.cpr (CarDatabase.Root).</summary>
        public static void Init(string bnkDir, XmbNode dbRoot)
        {
            lock (_lock)
            {
                if (_clothes != null && BnkDir == bnkDir) return;
                BnkDir = bnkDir;
                var map = new Dictionary<int, ClotheInfo>();
                var rows = dbRoot?.Get("DBClothe");
                if (rows != null)
                    foreach (var r in rows.Items)
                    {
                        var c = new ClotheInfo
                        {
                            Hash = r.I("_hashcode"),
                            IdNum = r.I("_id_num"),
                            Sex = r.I("_sex"),
                            Price = r.I("_price"),
                            NbColors = r.I("_nb_colors"),
                            Brand = r.I("_brand"),
                            SubBrand = r.I("_subbrand"),
                            SortIndex = r.I("_sort_index"),
                            MinAreaLevel = r.I("_min_arealevel"),
                            NameHc = r.I("_clothename_hc"),
                            HairCutStyle = r.I("_haircutstyle"),
                            DrivingType = r.I("_drivingtype"),
                            Part = (ClothePart)r.I("_parttype"),
                            File = r.S("_file", "") ?? "",
                            Valid = r.B("_isvalid", true),
                            Dlc = r.B("_dlc"),
                            PreOrder = r.B("_ispreorder"),
                        };
                        foreach (var f in BodyFlags) c.Visible[f] = r.B("_" + f, true);
                        map[c.Hash] = c;
                        if (!_byFile.ContainsKey(c.File)) _byFile[c.File] = c;
                    }
                // Figuren (DBPNJ): Aussehen je _configid (Verkäufer, Friseur, Fahrer …)
                _pnj.Clear();
                var pnj = dbRoot?.Get("DBPNJ");
                if (pnj != null)
                    foreach (var r in pnj.Items)
                    {
                        string id = r.S("_configid", "");
                        if (string.IsNullOrEmpty(id)) continue;
                        var cfg = new AvatarConfig { Sex = r.I("_sex") == 1 ? 1 : 0, Ethnic = 0 };
                        foreach (var slot in PnjSlots)
                        {
                            int h = r.I("_clothe_" + slot);
                            if (h == 0) continue;
                            cfg.Items.Add(h); cfg.Colors.Add(r.I("_" + slot + "_color"));
                        }
                        _pnj[id] = (cfg, r.S("_pnj_name", ""));
                    }
                string clothesDir = Path.Combine(bnkDir ?? "", "avatar", "clothes");
                try
                {
                    if (Directory.Exists(clothesDir))
                        foreach (var f in Directory.GetFiles(clothesDir, "*.bnk", SearchOption.AllDirectories))
                        {
                            var stem = Path.GetFileNameWithoutExtension(f);
                            if (!_bankByStem.ContainsKey(stem)) _bankByStem[stem] = f;
                        }
                }
                catch (Exception ex) 
                { 
                    DebugManager.Error("Avatar clothing" + ex.Message);
                }
                foreach (var name in new[] { Path.Combine("avatar", "common_bodies.bnk"), Path.Combine("avatar", "clothes", "common_clothes.bnk") })
                {
                    var p = Path.Combine(bnkDir ?? "", name);
                    try 
                    { 
                        if (File.Exists(p)) CommonTextures.IndexBank(BnkArchive.Open(p)); 
                    } 
                    catch (Exception ex) 
                    { 
                        DebugManager.Error("Avatar: " + p + " - " + ex.Message);
                    }
                }
                _clothes = map;
                DebugManager.Info($"Avatars: {map.Count} clothing items, {_bankByStem.Count} clothing benches, {CommonTextures.Count} shared textures");
            }
        }

        static BnkArchive Bodies
        {
            get
            {
                if (_bodies == null)
                {
                    var p = Path.Combine(BnkDir ?? "", "avatar", "common_bodies.bnk");
                    if (File.Exists(p)) _bodies = BnkArchive.Open(p);
                }
                return _bodies;
            }
        }

        /// <summary>Skelett eines Geschlechts (alle Köpfe/Körper eines Geschlechts teilen dieselbe .xsb).</summary>
        public static XsbSkeleton Skeleton(int sex)
        {
            lock (_lock)
            {
                if (_skel.TryGetValue(sex, out var s)) return s;
                var b = Bodies;
                var e = b?.Find(sex == 1 ? "woman_body.xsb" : "man_body.xsb");
                s = e != null ? XsbSkeleton.Parse(b.Read(e)) : null;
                _skel[sex] = s;
                return s;
            }
        }

        /// <summary>Namen aller Clips in scenarics/avatars/avatarsdriver.bnk (Entwicklermenü).</summary>
        public static List<string> ClipNames()
        {
            var res = new List<string>();
            lock (_lock)
            {
                if (_anims == null)
                {
                    var p = Path.Combine(BnkDir ?? "", "scenarics", "avatars", "avatarsdriver.bnk");
                    if (File.Exists(p)) _anims = BnkArchive.Open(p);
                }
                if (_anims != null) foreach (var e in _anims.All(".anm")) res.Add(Path.GetFileNameWithoutExtension(e.Name));
            }
            res.Sort(StringComparer.OrdinalIgnoreCase);
            return res;
        }

        /// <summary>Clip aus scenarics/avatars/avatarsdriver.bnk (Name ohne Endung, z.B. "stance_turn").</summary>
        public static AnmClip Clip(string name)
        {
            lock (_lock)
            {
                if (_clips.TryGetValue(name, out var c)) return c;
                if (_anims == null)
                {
                    var p = Path.Combine(BnkDir ?? "", "scenarics", "avatars", "avatarsdriver.bnk");
                    if (File.Exists(p)) _anims = BnkArchive.Open(p);
                }
                var e = _anims?.Find(name + ".anm");
                c = e != null ? AnmClip.Parse(_anims.Read(e), name) : null;
                if (c == null) 
                { 
                    DebugManager.Warning("Avatar animation Missing for: " + name);
                }
                _clips[name] = c;
                return c;
            }
        }

        static readonly string[] EthSuffix = { "occ", "asia", "afrik" };

        /// <summary>Körper (mit Hautvariante) und Kopf eines Geschlechts/einer Herkunft.</summary>
        public static List<AvatarPartData> LoadBody(int sex, int ethnic)
        {
            var list = new List<AvatarPartData>();
            var b = Bodies;
            if (b == null) return list;
            string who = sex == 1 ? "woman" : "man";
            string eth = EthSuffix[Mathf.Clamp(ethnic, 0, 2)];
            string skin = ethnic == 0 ? who + "_body.2dm" : who + "_body_" + eth + ".2dm";
            list.Add(LoadPart(b, who + "_body", skin, null, 0, false));
            list.Add(LoadPart(b, who + "_head_" + eth, null, null, 0, false));
            list.RemoveAll(p => p == null);
            return list;
        }

        /// <summary>Kleidungsstück (avatar/clothes/Kollektion&gt/datei&gt.bnk), Farbe 0 = wie modelliert.</summary>
        public static AvatarPartData LoadClothe(ClotheInfo c, int color, bool helmetTwin = false)
        {
            if (c == null || c.IsNothing) return null;
            string file = c.File;
            // Frisur unter einem Hut: "<datei>_HELMET" (Haare bleiben nur am Nacken sichtbar)
            if (helmetTwin && c.Part == ClothePart.Hair && _bankByStem.ContainsKey(file + "_HELMET")) file += "_HELMET";
            if (!_bankByStem.TryGetValue(file, out var path)) return null;
            var bank = BnkArchive.Open(path);
            string stem = file.ToLowerInvariant();
            bool hair = c.Part == ClothePart.Hair || c.Part == ClothePart.Beard;
            var part = LoadPart(bank, stem, null, hair ? null : stem + "_mat.2dm", hair ? 0 : color, true);
            if (part != null && hair && color > 0 && HairColor(color, out var p, out var s)) { part.HasHairColor = true; part.HairPrim = p; part.HairScd = s; }
            return part;
        }

        static Dictionary<string, (Color prim, Color scd)> _hairMat;

        /// <summary>Haarfarbe c (1..14): Material "A"+(c-1) aus avatar/clothes/common_clothes.bnk hair_mat.2dm.</summary>
        public static bool HairColor(int c, out Color prim, out Color scd)
        {
            prim = Color.black; scd = Color.white;
            lock (_lock)
            {
                if (_hairMat == null)
                {
                    _hairMat = new Dictionary<string, (Color, Color)>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var p = Path.Combine(BnkDir ?? "", "avatar", "clothes", "common_clothes.bnk");
                        var b = File.Exists(p) ? BnkArchive.Open(p) : null;
                        var e = b?.Find("hair_mat.2dm");
                        if (e != null)
                            foreach (var m in Material2DM.Parse(b.Read(e)).Materials.Values)
                            {
                                m.TryGetParam("PRIMCLR", out var pc); m.TryGetParam("SCDCLR", out var sc);
                                _hairMat[m.Name ?? ""] = (new Color(pc.x, pc.y, pc.z, 1f), new Color(sc.x, sc.y, sc.z, 1f));
                            }
                    }
                    catch (Exception ex) 
                    { 
                        DebugManager.Error("hair_mat.2dm: " + ex.Message);
                    }
                }
                string letter = ((char)('A' + Mathf.Clamp(c - 1, 0, 25))).ToString();
                if (!_hairMat.TryGetValue(letter, out var v)) return false;
                prim = v.prim; scd = v.scd;
                return true;
            }
        }

        /// <summary>
        /// Teil aus einer Bank:.3dd (Instanz -> Objekt, Materialliste)
        /// in Namensreihenfolge statt der Materialien des Stücks, außer "UNCHANGEABLE").
        /// </summary>
        /// <param name="bank"></param>
        /// <param name="stem"></param>
        /// <param name="mat2dm"><paramref name="mat2dm"/></param>
        /// <param name="variants2dm"></param>
        /// <param name="color"></param>
        /// <param name="ownTextures"></param>
        /// <returns></returns>
        static AvatarPartData LoadPart(BnkArchive bank, string stem, string mat2dm, string variants2dm, int color, bool ownTextures)
        {
            var e3dd = bank.Find(stem + ".3dd");
            var e3dg = bank.Find(stem + ".3dg");
            var e2dm = bank.Find(mat2dm ?? (stem + ".2dm"));
            if (e3dd == null || e3dg == null) 
            { 
                DebugManager.Warning("Avatar part missing: " + stem);
                return null; 
            }
            var scene = Scene3DD.Parse(bank.Read(e3dd));
            var geo = Geometry3DG.Parse(bank.Read(e3dg));
            var mats = e2dm != null ? Material2DM.Parse(bank.Read(e2dm)) : null;
            MaterialDef variant = null;
            if (color > 0 && variants2dm != null)
            {
                var ev = bank.Find(variants2dm);
                if (ev != null)
                {
                    var vl = new List<MaterialDef>(Material2DM.Parse(bank.Read(ev)).Materials.Values);
                    vl.Sort((x, y) => { int r = string.CompareOrdinal(x.Name, y.Name); return r != 0 ? r : x.Key.CompareTo(y.Key); });
                    if (color - 1 < vl.Count) variant = vl[color - 1];
                }
            }
            var part = new AvatarPartData { Name = stem };
            if (ownTextures)
                foreach (var e in bank.All(".2db")) { try { part.Textures.Add(TextureDef.Parse(bank.Read(e), e.Name)); } catch (Exception) { } }
            ulong unchangeable = MaterialDef.KeyFromName("UNCHANGEABLE");
            foreach (var insOff in scene.InsOffsets)
            {
                var ins = scene.GetIns(insOff);
                if (ins.Lods == null || ins.Lods.Length == 0) continue;
                if (!geo.GeoHash.TryGetValue(ins.Lods[0].ObjKey, out int geom)) continue;
                var bankKeys = scene.ReadMaterialBank(ins.MaterialBankOffset);
                foreach (var pr in SkinnedGeometry.Read(geo, geom))
                {
                    ulong mk = pr.MaterialIndex >= 0 && pr.MaterialIndex < bankKeys.Length ? bankKeys[pr.MaterialIndex] : 0;
                    MaterialDef def = null;
                    if (mats != null && mk != 0) mats.Materials.TryGetValue(mk, out def);
                    if (variant != null && def != null && def.Key != unchangeable) def = variant;
                    part.Prims.Add(pr);
                    part.PrimMaterials.Add(def);
                }
            }
            return part;
        }
    }

    /// <summary>Aussehen des eigenen Spielers (Spielstand "avatar") und der Mitspieler (Server-Spielerliste).</summary>
    public static class AvatarConfigs
    {
        /// <summary>Eigener Avatar: aus dem Spielstand, sonst das Standard-Aussehen.</summary>
        public static AvatarConfig Local => AvatarConfig.Decode(LocalCode) ?? AvatarConfig.Default();
        static string _offline;
        /// <summary>Kodiertes eigenes Aussehen (Spielstand, ohne Anmeldung die Einstellung aus dem Entwicklermenü).</summary>
        public static string LocalCode => !string.IsNullOrEmpty(TDU2Session.Save?.avatar) ? TDU2Session.Save.avatar : _offline;

        /// <summary>Kodiertes Aussehen eines Mitspielers (leer = noch unbekannt).</summary>
        public static string RemoteCode(int playerId)
        {
            var gd = TDU2GameData.Instance;
            return gd != null && gd.Players.TryGetValue(playerId, out var p) ? p.Avatar ?? "" : "";
        }

        public static AvatarConfig ForPlayer(int playerId) => AvatarConfig.Decode(RemoteCode(playerId)) ?? AvatarConfig.Default();

        /// <summary>Eigenes Aussehen speichern (Spielstand, an den Server).</summary>
        public static void SetLocal(AvatarConfig cfg)
        {
            if (cfg == null) return;
            if (TDU2Session.Save == null) 
            { 
                _offline = cfg.Encode(); 
                LocalChanged?.Invoke(); 
                return; 
            }
            TDU2Session.Save.avatar = cfg.Encode();
            if (TDU2Session.LoggedIn) 
                TDU2Session.SaveAsync();
            LocalChanged?.Invoke();
        }

        public static event Action LocalChanged;
    }
}