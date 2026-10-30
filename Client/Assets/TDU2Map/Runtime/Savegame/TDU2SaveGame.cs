using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using EdenSavegame;
using TDU2.Avatars;
using TDU2.Formats;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Online
{
    /// <summary>
    /// Spielstand zusätzlich im Original-Format von TDU2 (PLAYERSAVE/DATA: XMBF, DES-CBC wie das Spiel, Schlüssel aus dem Profilnamen),
    /// abgelegt unter Dokumente/KuxiiSoft/TDU2toUnity3D/savegame/&lt;Profil&gt;/PLAYERSAVE/DATA (nicht im Eden-Games-Ordner).
    ///  - Quelle bleibt der Server: bei jedem Speichern (TDU2Session.SaveAsync) wird die Kopie im Hintergrund auch lokal geschrieben,
    ///    beim Anmelden wird eine fehlende Datei aus dem Server-Spielstand neu gebaut.
    ///  - Ist der Server-Spielstand leer, die lokale Datei hat aber Fortschritt, wird sie übernommen (TDU2Login).
    /// Abgebildet: Fahrer (Name, Geld, Kilometer, aktuelles Auto, Haus, Position, Story-Schritt), Garage (Autos mit Lack/Felge/Innenraum
    /// je Hausstellplatz), Häuser (aktuell gehören alle), Lesezeichen, eigene Farben, Avatar. Was das Original nicht kennt
    /// (Insel, Interieur, Story-Schritt genau) steht zusätzlich in CEnvironment.Variables ("U3D_*").
    /// Die Vorlage (TDU2SaveTemplate) ist ein frisches Original-Profil; nicht abgebildete Teile bleiben wie dort.
    /// </summary>
    public static class TDU2SaveGame
    {
        public const string FileName = "DATA";
        const string Container = "PLAYERSAVE";
        const ulong NoSpot = 0xFFFFFFFFFFFFFFFFUL;
        const int OutfitSlots = 11;
        /// <summary>Reihenfolge der 11 Kleidungsplätze in AvatarSave.Config.ClotheIDArray (zweimal: normal, fahren).</summary>
        static readonly ClothePart[] SlotParts =
        {
            ClothePart.Hair, ClothePart.Top, ClothePart.Bottom, ClothePart.Gloves, ClothePart.Shoes, ClothePart.Glasses,
            ClothePart.Helmet, ClothePart.Dress, ClothePart.Beard, ClothePart.Accessories, ClothePart.Belt,
        };

        static readonly object _lock = new object();
        static string _docs;
        static bool _warnedNoDb;

        /// <summary>Fahrzeugdatenbank (TDU2Login setzt sie beim Anmelden).</summary>
        public static CarDatabase Database { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            try { _docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch (Exception) { _docs = null; }
            TDU2Session.SaveCopied -= OnSaveCopied;
            TDU2Session.SaveCopied += OnSaveCopied;
        }

        static string Docs
        {
            get
            {
                if (string.IsNullOrEmpty(_docs))
                    try { _docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch (Exception) { }
                return string.IsNullOrEmpty(_docs) ? Application.persistentDataPath : _docs;
            }
        }

        public static string Folder => Path.Combine(Docs, "KuxiiSoft", "TDU2toUnity3D", "savegame");

        public static string PathFor(string nick)
        {
            string n = string.IsNullOrEmpty(nick) ? "_offline" : nick;
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return Path.Combine(Folder, n, Container, FileName);
        }

        public static bool Exists(string nick)
        {
            try { return File.Exists(PathFor(nick)); } catch (Exception) { return false; }
        }

        static CarDatabase Db()
        {
            if (Database != null) return Database;
            var vm = UnityEngine.Object.FindObjectOfType<TDU2VehicleManager>();
            if (vm != null && vm.Database != null) Database = vm.Database;
            return Database;
        }

        /// <summary>Aus TDU2Session.SaveAsync (Hauptthread, Kopie des Spielstands).</summary>
        static void OnSaveCopied(string nick, SaveData copy)
        {
            var db = Db();
            if (db == null)
            {
                if (!_warnedNoDb) Debug.LogWarning("[TDU2] Savegame: Fahrzeugdatenbank noch nicht geladen – DATA nicht geschrieben");
                _warnedNoDb = true;
                return;
            }
            Task.Run(() => Write(nick, copy, db, false));
        }

        /// <summary>Lokale DATA aus einem Spielstand schreiben (Kopie wird im Hauptthread gezogen, Schreiben im Hintergrund).</summary>
        public static Task WriteAsync(string nick, SaveData save, CarDatabase db, string reason)
        {
            if (save == null || string.IsNullOrEmpty(nick)) return Task.CompletedTask;
            db = db ?? Db();
            if (db == null) { Debug.LogWarning("[TDU2] Savegame: Fahrzeugdatenbank fehlt – DATA nicht geschrieben"); return Task.CompletedTask; }
            var copy = JsonUtility.FromJson<SaveData>(JsonUtility.ToJson(save));
            int st = Story.TDU2StoryProgress.Get(nick);
            if (st >= 0) copy.story = st;
            if (!string.IsNullOrEmpty(reason)) Debug.Log("[TDU2] Savegame: " + reason + " -> " + PathFor(nick));
            return Task.Run(() => Write(nick, copy, db, true));
        }

        // ------------------------------------------------------------------ Schreiben

        static bool Write(string nick, SaveData s, CarDatabase db, bool verbose)
        {
            try
            {
                byte[] xmbf = Build(nick, s, db, out string info);
                byte[] file = SaveCrypto.Encrypt(xmbf, nick, FileName, null, Container);
                string path = PathFor(nick);
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, file);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                if (verbose) Debug.Log($"[TDU2] Savegame geschrieben: {path} ({file.Length} Bytes) – {info}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TDU2] Savegame: DATA schreiben fehlgeschlagen: " + e.Message);
                return false;
            }
        }

        static byte[] Template()
        {
            byte[] gz = Convert.FromBase64String(TDU2SaveTemplate.GzipBase64);
            using (var src = new MemoryStream(gz))
            using (var z = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream(TDU2SaveTemplate.Length))
            {
                z.CopyTo(dst);
                return dst.ToArray();
            }
        }

        /// <summary>Spielstand -> XMBF (unverschlüsselt) auf Basis der Vorlage.</summary>
        public static byte[] Build(string nick, SaveData s, CarDatabase db, out string info)
        {
            var doc = Xmbf.Decode(Template());
            var root = doc.Root;
            var ic = CultureInfo.InvariantCulture;
            string island = IslandKey(s.island);

            // ---- Häuser: aktuell gehören alle (Kaufen kommt später); Club-Häuser (ECH_*) nicht
            var houses = new List<HouseInfo>();
            foreach (var h in db.Houses)
                if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && h.CarSlots > 0) houses.Add(h);

            string[] ip = (s.interior ?? "").Split('|');
            string inHouse = ip.Length > 2 && IsHouse(db, ip[2]) ? ip[2] : (ip[0].Length > 0 ? UniqueHouseOfCode(db, ip[0]) : null);
            string home = inHouse ?? DefaultHouse(houses, island);
            // Garage des aktuellen Hauses zuerst, dann die übrigen wie in DBHouse
            houses.Sort((a, b) => (Same(a.SpotName, home) ? 0 : 1).CompareTo(Same(b.SpotName, home) ? 0 : 1));

            // ---- Garage
            var gArr = (XArray)root["Garage"];
            var emptySlot = gArr.Items.Count > 0 ? (XObject)gArr.Items[0] : null;
            if (emptySlot == null) throw new InvalidDataException("Vorlage ohne Garage");
            var cars = new List<CarPhysics>();
            var seenCars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in s.ownedCars ?? new List<string>())
            {
                if (string.IsNullOrEmpty(f) || !seenCars.Add(f)) continue;
                var c = FindCar(db, f);
                if (c != null) cars.Add(c);
                else Debug.LogWarning("[TDU2] Savegame: Auto " + f + " nicht in DBCarPhysicsData – nicht in der Garage");
            }
            var garage = new XArray { ElementType = XType.Object };
            int ci = 0, current = -1, idx = 0, dropped = 0;
            foreach (var h in houses)
            {
                ulong hc = HouseHash(h.SpotName);
                for (int k = 0; k < h.CarSlots; k++, idx++)
                {
                    var e = (XObject)Clone(emptySlot);
                    Set(e, "IndexInGarage", idx);
                    Set(e, "House", hc);
                    Set(e, "XTradeID", 0);
                    if (ci < cars.Count)
                    {
                        var car = cars[ci];
                        FillCar((XObject)e["Car"], car, ci + 1, s, db);
                        if (current < 0 && string.Equals(car.FileName, s.currentCar, StringComparison.OrdinalIgnoreCase)) current = idx;
                        ci++;
                    }
                    garage.Items.Add(e);
                }
            }
            dropped = cars.Count - ci;
            root["Garage"] = garage;

            // ---- Fahrer
            var drv = (XObject)root["Driver"];
            Set(drv, "Name", nick ?? "");
            Set(drv, "Money", Math.Max(0L, s.money));
            double km = 0;
            foreach (var o in s.odometers ?? new List<OdoEntry>()) km += Math.Max(0, o.meters) / 1000.0;
            Set(drv, "OdometerKms", km);
            Set(drv, "CurrentCar", current >= 0 ? current : 0);
            Set(drv, "CurrentHouse", home != null ? HouseHash(home) : 0UL);
            Set(drv, "LastSpotWorld", inHouse != null ? HouseHash(inHouse) : NoSpot);
            int step = Mathf.Clamp(s.story, 0, Story.TDU2StoryProgress.Implemented);
            Set(drv, "BeginGameState", step);
            var pos = (XArray)drv["LastPosWorld"];
            if (pos != null && pos.Items.Count == 16)
            {
                float yaw = s.hasPosition ? s.heading * Mathf.Deg2Rad : 0f;
                float cs = Mathf.Cos(yaw), sn = Mathf.Sin(yaw);
                // Zeilenweise 4x4, Drehung um Y (Vorwärts = Spalte 3 = (sin, 0, cos)), Verschiebung = Spielkoordinaten
                float[] m = s.hasPosition
                    ? new[] { cs, 0f, sn, s.px, 0f, 1f, 0f, s.py, -sn, 0f, cs, s.pz, 0f, 0f, 0f, 1f }
                    : new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
                for (int i = 0; i < 16; i++) ((XPrim)pos.Items[i]).Value = (double)m[i];
            }

            // ---- Häuser (My House)
            var my = (XObject)root["MyHouse"];
            var hArr = new XArray { ElementType = XType.Object };
            foreach (var h in houses)
            {
                var e = new XObject();
                e["HouseHC"] = new XPrim(XType.UInt64, HouseHash(h.SpotName));
                e["LivingMaterials"] = PrimArray(XType.UInt32, 0UL, 0UL);
                e["GarageMaterials"] = PrimArray(XType.UInt32, 0UL, 0UL);
                var frames = new XArray { ElementType = XType.Object };
                for (int i = 0; i < 5; i++) { var fr = new XObject(); fr["FrameFilename"] = new XPrim(XType.String, ""); frames.Items.Add(fr); }
                e["HouseFrames"] = frames;
                hArr.Items.Add(e);
            }
            my["House"] = hArr;
            var stats = root["StatisticsData"] as XObject;
            if (stats != null) Set(stats, "NbHouseBought", houses.Count);

            // ---- Lesezeichen je Insel
            var bm = (XObject)root["Bookmarks"];
            var bI = new XArray { ElementType = XType.Object }; var bH = new XArray { ElementType = XType.Object }; var bC = new XArray { ElementType = XType.Object };
            foreach (var b in s.bookmarks ?? new List<BookmarkEntry>())
            {
                var e = new XObject();
                e["x"] = new XPrim(XType.Float, (double)b.x); e["y"] = new XPrim(XType.Float, (double)b.y);
                e["z"] = new XPrim(XType.Float, (double)b.z); e["w"] = new XPrim(XType.Float, 1.0);
                string k = IslandKey(b.island);
                (k == "hawai" ? bH : k == "casino" ? bC : bI).Items.Add(e);
            }
            bm["Bookmarks_IBIZA"] = bI; bm["Bookmarks_HAWAI"] = bH; bm["Bookmarks_CASINO"] = bC;

            // ---- eigene Farben (Sticker-Shop)
            var fav = (root["Stickers"] as XObject)?["FavoriteColors"] as XArray;
            if (fav != null)
                for (int i = 0; i < fav.Items.Count; i++)
                {
                    var e = (XObject)fav.Items[i];
                    int col = s.favoriteColors != null && i < s.favoriteColors.Count ? s.favoriteColors[i] : -1;
                    Set(e, "Color", col < 0 ? 0xFFFFFFFFUL : 0xFF000000UL | (ulong)(uint)(col & 0xFFFFFF));
                    Set(e, "IsUsed", col >= 0);
                }

            // ---- Avatar
            string av = WriteAvatar(root, s.avatar);

            // ---- Profil
            var lv = root["PlayerLevels"] as XObject;
            if (lv != null) { Set(lv, "PlayedTheGame", true); Set(lv, "TutoSeen", step >= Story.TDU2StoryProgress.Implemented); }
            var life = root["MyLife"] as XObject;
            if (life != null && s.myLife != null && !string.IsNullOrEmpty(s.myLife.moodText)) Set(life, "PersonalQuote", s.myLife.moodText);

            // ---- Zusatz für den Unity-Port (Original kennt keine Insel/Interieur im DATA-Fahrerteil)
            var env = (root["CEnvironment"] as XObject)?["Variables"] as XArray;
            if (env != null)
            {
                SetVar(env, "U3D_Island", island);
                SetVar(env, "U3D_Story", s.story.ToString(ic));
                SetVar(env, "U3D_Pos", s.hasPosition ? "1" : "0");
                SetVar(env, "U3D_Heading", s.heading.ToString("R", ic));
                SetVar(env, "U3D_Interior", s.interior ?? "");
                SetVar(env, "U3D_Car", s.currentCar ?? "");
                SetVar(env, "U3D_Saved", DateTime.UtcNow.ToString("o", ic));
            }

            info = $"Insel {island}, {ci} Autos in {houses.Count} Häusern ({idx} Stellplätze{(dropped > 0 ? ", " + dropped + " ohne Platz" : "")}), "
                 + $"aktuelles Auto {(current >= 0 ? "#" + current : "-")}, Haus {home ?? "-"}{(inHouse != null ? " (drin)" : "")}, Geld {s.money}, "
                 + $"Story {s.story}, Avatar {av}";
            return Xmbf.Encode(doc);
        }

        static void FillCar(XObject c, CarPhysics car, int id, SaveData s, CarDatabase db)
        {
            CarConfigEntry cfg = null;
            if (s.carConfigs != null)
                foreach (var e in s.carConfigs) if (e != null && string.Equals(e.car, car.FileName, StringComparison.OrdinalIgnoreCase)) cfg = e;
            int paint = cfg != null ? Math.Max(0, cfg.color) : 0;
            int rim = 0, interior = 0;
            if (cfg != null && cfg.rim != 0 && db.CarRims.TryGetValue(car.Id, out var rims)) rim = Math.Max(0, rims.IndexOf(cfg.rim));
            if (cfg != null && cfg.interior != 0 && db.Colors.TryGetValue(car.Id, out var cols) && paint < cols.Count)
                interior = Math.Max(0, Array.IndexOf(cols[paint].Interiors, cfg.interior));
            double km = 0;
            if (s.odometers != null) foreach (var o in s.odometers) if (o != null && o.carId == car.Id) km = o.meters / 1000.0;
            bool sticker = false;
            if (s.liveries != null) foreach (var l in s.liveries) if (l != null && string.Equals(l.car, car.FileName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(l.data)) sticker = true;

            Set(c, "Status", 0);
            Set(c, "ID", (ulong)id);
            Set(c, "Archetype", car.Id);
            Set(c, "VIN", 0x6700000000000000UL | ((ulong)(uint)car.Id << 16) | (ulong)(id & 0xFFFF));
            Set(c, "PaintIndice", Math.Min(paint, 254));
            Set(c, "RimIndice", Math.Min(rim, 254));
            Set(c, "InteriorIndice", Math.Min(interior, 254));
            Set(c, "OdometerKms", km);
            Set(c, "HasSticker", sticker);
        }

        static string WriteAvatar(XObject root, string code)
        {
            var cfg = AvatarConfig.Decode(code);
            var conf = (root["AvatarSave"] as XObject)?["Config"] as XObject;
            if (cfg == null || conf == null) return "Vorlage";
            if (!HasClothes()) return "Vorlage (Kleidung nicht geladen)";
            Set(conf, "BodyID", cfg.Ethnic * 2 + cfg.Sex);
            if (cfg.EyeId >= 0) Set(conf, "EyeID", cfg.EyeId);
            if (cfg.BrowId >= 0) Set(conf, "EyebrowsID", cfg.BrowId);
            if (cfg.HasBrowColor)
            {
                Set(conf, "EyebrowsColor_R", Mathf.RoundToInt(Mathf.Clamp01(cfg.BrowColor.r) * 255f));
                Set(conf, "EyebrowsColor_G", Mathf.RoundToInt(Mathf.Clamp01(cfg.BrowColor.g) * 255f));
                Set(conf, "EyebrowsColor_B", Mathf.RoundToInt(Mathf.Clamp01(cfg.BrowColor.b) * 255f));
            }
            var ids = conf["ClotheIDArray"] as XArray; var cols = conf["ColorIDArray"] as XArray;
            if (ids == null || cols == null || ids.Items.Count < OutfitSlots * 2 || cols.Items.Count < OutfitSlots * 2) return "Vorlage (Feld fehlt)";
            int dbSex = cfg.Sex == 1 ? 1 : 2;   // DBClothe._sex: 1 Frau, 2 Mann
            int set = 0;
            for (int slot = 0; slot < OutfitSlots; slot++)
            {
                var part = SlotParts[slot];
                ClotheInfo item = null; int color = 0;
                for (int i = 0; i < cfg.Items.Count; i++)
                {
                    var c = AvatarLibrary.Clothe(cfg.Items[i]);
                    if (c != null && c.Part == part) { item = c; color = i < cfg.Colors.Count ? cfg.Colors[i] : 0; }
                }
                if (item == null) item = Nothing(part, dbSex);
                else set++;
                ulong id = item != null ? (ulong)(uint)item.IdNum : 65535UL;
                // erste 11 = normale Kleidung, zweite 11 = beim Fahren (hier gleich)
                ((XPrim)ids.Items[slot]).Value = id; ((XPrim)ids.Items[slot + OutfitSlots]).Value = id;
                ((XPrim)cols.Items[slot]).Value = (long)color; ((XPrim)cols.Items[slot + OutfitSlots]).Value = (long)color;
            }
            return $"{(cfg.Sex == 1 ? "Frau" : "Mann")}, {set} Teile";
        }

        static bool HasClothes() { foreach (var _ in AvatarLibrary.AllClothes) return true; return false; }

        static ClotheInfo Nothing(ClothePart part, int dbSex)
        {
            ClotheInfo any = null;
            foreach (var c in AvatarLibrary.AllClothes)
                if (c.Part == part && c.IsNothing)
                {
                    if (c.Sex == dbSex) return c;
                    if (any == null && (c.Sex == 0 || c.Sex == 3)) any = c;
                }
            return any;
        }

        // ------------------------------------------------------------------ Lesen (lokale DATA -> Spielstand)

        /// <summary>Lokale DATA lesen und in einen Spielstand umsetzen. Hauptthread (Avatar-/Fahrzeugdaten), Dauer wenige ms.</summary>
        public static bool TryRead(string nick, CarDatabase db, out SaveData s, out string why)
        {
            s = null; why = "";
            db = db ?? Db();
            string path = PathFor(nick);
            try
            {
                if (!File.Exists(path)) { why = "keine Datei"; return false; }
                if (db == null) { why = "Fahrzeugdatenbank fehlt"; return false; }
                byte[] file;
                lock (_lock) file = File.ReadAllBytes(path);
                var dec = SaveCrypto.Decrypt(file, nick, FileName, Container);
                var doc = Xmbf.Decode(dec.Xmbf);
                s = Map(doc.Root, db, out why);
                return true;
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        static SaveData Map(XObject root, CarDatabase db, out string info)
        {
            var ic = CultureInfo.InvariantCulture;
            var s = new SaveData();
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            var env = (root["CEnvironment"] as XObject)?["Variables"] as XArray;
            if (env != null)
                foreach (var v in env.Items)
                    if (v is XObject o) { string n = Str(o, "Name"); if (!string.IsNullOrEmpty(n)) vars[n] = Str(o, "Value") ?? ""; }

            var drv = (XObject)root["Driver"];
            s.money = Math.Max(0L, Long(drv, "Money"));
            ulong curHouse = ULong(drv, "CurrentHouse"), lastSpot = ULong(drv, "LastSpotWorld");
            string curHouseName = HouseName(db, curHouse), lastSpotName = HouseName(db, lastSpot);

            // Garage -> eigene Autos, Ausstattung, Kilometer
            var garage = root["Garage"] as XArray;
            int curIdx = (int)Long(drv, "CurrentCar");
            if (garage != null)
                for (int gi = 0; gi < garage.Items.Count; gi++)
                {
                    var c = (garage.Items[gi] as XObject)?["Car"] as XObject;
                    if (c == null || Long(c, "Status") != 0) continue;
                    int arch = (int)Long(c, "Archetype");
                    if (!db.ById.TryGetValue(arch, out var car) || string.IsNullOrEmpty(car.FileName)) continue;
                    if (s.ownedCars.Exists(f => string.Equals(f, car.FileName, StringComparison.OrdinalIgnoreCase))) continue;
                    s.ownedCars.Add(car.FileName);
                    if (gi == curIdx) s.currentCar = car.FileName;
                    int paint = (int)Long(c, "PaintIndice"), rimI = (int)Long(c, "RimIndice"), intI = (int)Long(c, "InteriorIndice");
                    if (paint >= 255) paint = 0;
                    var cfg = new CarConfigEntry { car = car.FileName, color = paint };
                    if (rimI < 255 && db.CarRims.TryGetValue(arch, out var rims) && rimI < rims.Count && rimI > 0) cfg.rim = rims[rimI];
                    if (intI < 255 && intI > 0 && db.Colors.TryGetValue(arch, out var cols) && paint < cols.Count && intI < cols[paint].Interiors.Length)
                        cfg.interior = cols[paint].Interiors[intI];
                    if (cfg.color != 0 || cfg.rim != 0 || cfg.interior != 0) s.carConfigs.Add(cfg);
                    double km = Double(c, "OdometerKms");
                    if (km > 0) s.odometers.Add(new OdoEntry { carId = arch, meters = km * 1000.0 });
                }
            if (vars.TryGetValue("U3D_Car", out string vc) && s.ownedCars.Exists(f => string.Equals(f, vc, StringComparison.OrdinalIgnoreCase))) s.currentCar = vc;
            if (string.IsNullOrEmpty(s.currentCar) && s.ownedCars.Count > 0) s.currentCar = s.ownedCars[0];
            s.starterChosen = s.ownedCars.Count > 0;

            // Insel, Position, Interieur, Story
            s.island = vars.TryGetValue("U3D_Island", out string vi) && vi.Length > 0 ? vi
                     : (lastSpotName ?? curHouseName ?? "").EndsWith("I", StringComparison.Ordinal) ? "ibiza" : "hawai";
            var pos = drv["LastPosWorld"] as XArray;
            if (pos != null && pos.Items.Count == 16)
            {
                double D(int i) => (pos.Items[i] as XPrim)?.AsDouble() ?? 0;
                float px = (float)D(3), py = (float)D(7), pz = (float)D(11);
                bool has = vars.TryGetValue("U3D_Pos", out string vp) ? vp == "1" : (Math.Abs(px) > 1f || Math.Abs(pz) > 1f);
                if (has && Finite(px) && Finite(py) && Finite(pz))
                {
                    s.hasPosition = true; s.px = px; s.py = py; s.pz = pz;
                    s.heading = vars.TryGetValue("U3D_Heading", out string vh) && float.TryParse(vh, NumberStyles.Float, ic, out float hd)
                        ? hd : (float)(Math.Atan2(D(2), D(10)) * 180.0 / Math.PI);
                }
            }
            if (vars.TryGetValue("U3D_Interior", out string vin)) s.interior = vin;
            else if (lastSpotName != null) s.interior = Code(lastSpotName) + "||" + lastSpotName;
            int bgs = (int)Long(drv, "BeginGameState");
            s.story = vars.TryGetValue("U3D_Story", out string vs) && int.TryParse(vs, NumberStyles.Integer, ic, out int st) ? st : bgs;

            // Lesezeichen
            var bm = root["Bookmarks"] as XObject;
            int bid = 1;
            if (bm != null)
                foreach (var (key, isl) in new[] { ("Bookmarks_IBIZA", "ibiza"), ("Bookmarks_HAWAI", "hawai"), ("Bookmarks_CASINO", "casino") })
                    if (bm[key] is XArray a)
                        foreach (var it in a.Items)
                            if (it is XObject b)
                                s.bookmarks.Add(new BookmarkEntry { id = bid++, island = isl, x = (float)Double(b, "x"), y = (float)Double(b, "y"), z = (float)Double(b, "z") });

            // eigene Farben
            if ((root["Stickers"] as XObject)?["FavoriteColors"] is XArray fav)
                foreach (var it in fav.Items)
                {
                    var e = it as XObject;
                    bool used = e != null && Long(e, "IsUsed") != 0;
                    s.favoriteColors.Add(used ? (int)(ULong(e, "Color") & 0xFFFFFF) : -1);
                }

            // Avatar
            string av = ReadAvatar(root, out string avInfo);
            if (av != null) s.avatar = av;

            // Profil
            var life = root["MyLife"] as XObject;
            string quote = life != null ? Str(life, "PersonalQuote") : null;
            if (!string.IsNullOrEmpty(quote) && quote != "Test Drive Unlimited 2") s.myLife.moodText = quote.Length > 60 ? quote.Substring(0, 60) : quote;

            info = $"{s.ownedCars.Count} Autos, aktuell {s.currentCar}, Insel {s.island}, Position={s.hasPosition}, Interieur '{s.interior}', Geld {s.money}, Story {s.story}, Avatar {avInfo}";
            return s;
        }

        static string ReadAvatar(XObject root, out string info)
        {
            info = "-";
            var conf = (root["AvatarSave"] as XObject)?["Config"] as XObject;
            if (conf == null || !HasClothes()) { info = "nicht gelesen"; return null; }
            var byNum = new Dictionary<int, ClotheInfo>();
            foreach (var c in AvatarLibrary.AllClothes) if (!byNum.ContainsKey(c.IdNum)) byNum[c.IdNum] = c;
            int body = (int)Long(conf, "BodyID");
            var cfg = new AvatarConfig { Sex = body & 1, Ethnic = Mathf.Clamp(body >> 1, 0, 2) };
            var ids = conf["ClotheIDArray"] as XArray; var cols = conf["ColorIDArray"] as XArray;
            if (ids != null)
                for (int i = 0; i < Math.Min(OutfitSlots, ids.Items.Count); i++)
                {
                    int num = (int)((ids.Items[i] as XPrim)?.AsLong() ?? 65535);
                    if (num == 65535 || !byNum.TryGetValue(num, out var c) || c.IsNothing) continue;
                    int col = cols != null && i < cols.Items.Count ? (int)((cols.Items[i] as XPrim)?.AsLong() ?? 0) : 0;
                    cfg.Items.Add(c.Hash); cfg.Colors.Add(Math.Max(0, col));
                }
            cfg.EyeId = (int)Long(conf, "EyeID");
            cfg.BrowId = (int)Long(conf, "EyebrowsID");
            cfg.BrowColor = new Color(Long(conf, "EyebrowsColor_R") / 255f, Long(conf, "EyebrowsColor_G") / 255f, Long(conf, "EyebrowsColor_B") / 255f, 1f);
            info = $"{(cfg.Sex == 1 ? "Frau" : "Mann")}, {cfg.Items.Count} Teile";
            return cfg.Encode();
        }

        /// <summary>Übernimmt aus dem (leeren) Server-Spielstand, was die DATA nicht enthält (MyTDULife, Lieblings-Spots, Sticker, Kleidung …).</summary>
        public static void KeepServerOnly(SaveData local, SaveData server)
        {
            if (local == null || server == null) return;
            if (server.myLife != null)
            {
                string quote = local.myLife?.moodText;
                local.myLife = server.myLife;
                if (string.IsNullOrEmpty(local.myLife.moodText) && !string.IsNullOrEmpty(quote)) local.myLife.moodText = quote;
            }
            if (server.favoriteSpots != null && server.favoriteSpots.Count > 0) local.favoriteSpots = server.favoriteSpots;
            if (server.liveries != null && server.liveries.Count > 0) local.liveries = server.liveries;
            if (server.stickerBoards != null && server.stickerBoards.Count > 0) local.stickerBoards = server.stickerBoards;
            if (server.ownedClothes != null && server.ownedClothes.Count > 0) local.ownedClothes = server.ownedClothes;
            if (server.houses != null && server.houses.Count > 0) local.houses = server.houses;
            if (server.ownedFurniture != null && server.ownedFurniture.Count > 0) local.ownedFurniture = server.ownedFurniture;
            if (server.ownedMaterials != null && server.ownedMaterials.Count > 0) local.ownedMaterials = server.ownedMaterials;
            if (string.IsNullOrEmpty(local.avatar)) local.avatar = server.avatar ?? "";
            if (local.bookmarks.Count == 0 && server.bookmarks != null) local.bookmarks = server.bookmarks;
        }

        // ------------------------------------------------------------------ Häuser / Spots

        /// <summary>Hashcode eines Spots wie im Spielstand (Garage.House, Driver.CurrentHouse/LastSpotWorld): Zeichen des vollen
        /// Namens auf 8 Bytes aufsummiert (Byte k = Summe der Zeichen k, k+8, …).</summary>
        public static ulong HouseHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            var sum = new int[8];
            string up = name.ToUpperInvariant();
            for (int i = 0; i < up.Length; i++) sum[i & 7] += up[i];
            ulong k = 0;
            for (int b = 0; b < 8; b++) k |= (ulong)(byte)sum[b] << (8 * b);
            return k;
        }

        static string HouseName(CarDatabase db, ulong hc)
        {
            if (hc == 0 || hc == NoSpot) return null;
            foreach (var h in db.Houses) if (h.SpotName != null && HouseHash(h.SpotName) == hc) return h.SpotName;
            return null;
        }

        static bool IsHouse(CarDatabase db, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var h in db.Houses) if (Same(h.SpotName, name)) return h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        /// <summary>Haus (DBHouse-Name) zu einem Spot der Insel, sonst null (Läden, Hotels …).</summary>
        public static string HouseOfSpot(CarDatabase db, Spot spot)
        {
            if (db == null || spot == null || string.IsNullOrEmpty(spot.Code) || !spot.Code.StartsWith("EPH", StringComparison.OrdinalIgnoreCase)) return null;
            var one = new List<Spot> { spot };
            foreach (var h in db.Houses)
                if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && string.Equals(Code(h.SpotName), spot.Code, StringComparison.OrdinalIgnoreCase)
                    && TDU2GamePC.FindSpot(one, h.SpotName) == spot)
                    return h.SpotName;
            return null;
        }

        static string UniqueHouseOfCode(CarDatabase db, string code)
        {
            string found = null;
            foreach (var h in db.Houses)
                if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && string.Equals(Code(h.SpotName), code, StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null) return null;   // mehrdeutig (mehrere Häuser mit gleichem Code)
                    found = h.SpotName;
                }
            return found;
        }

        /// <summary>Standard-Haus der Insel (Wohnwagen Ibiza EPH_L0C_1254I, Hawaii EPH_L0A_1276), sonst das erste der Insel.</summary>
        static string DefaultHouse(List<HouseInfo> houses, string island)
        {
            string want = island == "hawai" ? "EPH_L0A_1276" : "EPH_L0C_1254I";
            foreach (var h in houses) if (Same(h.SpotName, want)) return h.SpotName;
            bool ibiza = island != "hawai";
            foreach (var h in houses) if (h.SpotName.EndsWith("I", StringComparison.Ordinal) == ibiza) return h.SpotName;
            return houses.Count > 0 ? houses[0].SpotName : null;
        }

        static string Code(string name) { int us = name.LastIndexOf('_'); return us > 0 ? name.Substring(0, us) : name; }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static string IslandKey(string island)
        {
            if (string.IsNullOrEmpty(island)) return "ibiza";
            if (island.StartsWith("h", StringComparison.OrdinalIgnoreCase)) return "hawai";
            if (island.StartsWith("cas", StringComparison.OrdinalIgnoreCase)) return "casino";
            return "ibiza";
        }

        static CarPhysics FindCar(CarDatabase db, string file)
        {
            foreach (var c in db.Cars) if (string.Equals(c.FileName, file, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // ------------------------------------------------------------------ XMBF-Helfer

        static XValue Clone(XValue v)
        {
            switch (v)
            {
                case XObject o:
                    var no = new XObject();
                    foreach (var kv in o.Items) no[kv.Key] = Clone(kv.Value);
                    return no;
                case XArray a:
                    var na = new XArray { ElementType = a.ElementType };
                    foreach (var it in a.Items) na.Items.Add(Clone(it));
                    return na;
                case XPrim p:
                    return new XPrim(p.Type, p.Value);
                default:
                    return v;
            }
        }

        static XArray PrimArray(XType t, params object[] values)
        {
            var a = new XArray { ElementType = t };
            foreach (var v in values) a.Items.Add(new XPrim(t, v));
            return a;
        }

        /// <summary>Wert eines vorhandenen Feldes setzen (Typ bleibt der der Vorlage; Encode wandelt passend).</summary>
        static void Set(XObject o, string key, object value)
        {
            if (!(o[key] is XPrim p)) throw new InvalidDataException("Feld " + key + " fehlt in der Vorlage");
            switch (value)
            {
                case bool b: p.Value = p.Type == XType.Bool ? (object)b : (b ? 1L : 0L); break;
                case int i: p.Value = (long)i; break;
                case uint u: p.Value = (ulong)u; break;
                case long l: p.Value = l; break;
                case ulong ul: p.Value = ul; break;
                case float f: p.Value = (double)f; break;
                case double d: p.Value = d; break;
                case string s: p.Value = s; break;
                default: p.Value = value; break;
            }
        }

        static void SetVar(XArray vars, string name, string value)
        {
            foreach (var it in vars.Items)
                if (it is XObject o && Str(o, "Name") == name) { o["Value"] = new XPrim(XType.String, value ?? ""); return; }
            var e = new XObject();
            e["Name"] = new XPrim(XType.String, name);
            e["Value"] = new XPrim(XType.String, value ?? "");
            vars.Items.Add(e);
        }

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        static string Str(XObject o, string k) => (o[k] as XPrim)?.AsString();
        static long Long(XObject o, string k) => (o[k] as XPrim)?.AsLong() ?? 0;
        static ulong ULong(XObject o, string k) => (o[k] as XPrim)?.AsULong() ?? 0;
        static double Double(XObject o, string k) => (o[k] as XPrim)?.AsDouble() ?? 0;
    }
}
