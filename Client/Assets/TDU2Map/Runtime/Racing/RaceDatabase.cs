using System.Collections.Generic;
using System.IO;
using TDU2.Map.Formats;

namespace TDU2.Map.Racing
{
    /// <summary>Eine Rennserie aus DBLicense: entweder eine Rennschul-Lizenz oder eine Meisterschaft.</summary>
    public sealed class LicenseInfo
    {
        public ulong Hash;
        public bool IsChampionship;
        public int Island;                 // 0 Ibiza, 1 Hawaii
        public int Order;                  // Reihenfolge innerhalb der Schule (1-basiert)
        public int PlayerLevel;
        public int VehicleCategory1;       // Fahrzeugklasse (Gruppe 1 der Lizenz, z.B. 7 = A7)
        public int VehicleCategory2;       // Gruppe 2 (0 = keine)
        public ulong NameHc;               // Lokalisierungs-Hashcode des Namens
        public ulong DescHc;               // Beschreibung ("Fahren auf der Straße: Grundlagen")
        public int RaceMoneyReward, ChampMoneyReward;
        public int RetriesBeforeEasier;
        public readonly List<string> ChallengeFiles = new List<string>();
        /// <summary>Gegner der Meisterschaft: DBPNJ-IDs (_bot_1.._bot_7, 0 = keiner) mit Stärke (_difficulty_bot_n, 0..1).</summary>
        public readonly List<(int pnj, float difficulty, float aggressiveness)> Bots = new List<(int, float, float)>();
        public int Rival;                  // DBPNJ-ID des Rivalen

        /// <summary>Rennschul-Buchstabe aus dem ersten Prüfungsnamen (RSA..=A, RSB..=B, RSC..=C), sonst '\0'.</summary>
        public char School
        {
            get
            {
                foreach (var f in ChallengeFiles)
                    if (f != null && f.Length >= 3 && (f[0] == 'R' || f[0] == 'r') && (f[1] == 'S' || f[1] == 's')) return char.ToUpperInvariant(f[2]);
                return '\0';
            }
        }

        public bool IsRacingSchool => !IsChampionship && School != '\0';

        /// <summary>Gruppenname wie im Film ("A7", "B4", "C3"; "empty" ohne zweite Gruppe).</summary>
        public string Group1 => RaceDatabase.CategoryName(VehicleCategory1);
        public string Group2 => RaceDatabase.CategoryName(VehicleCategory2);
        /// <summary>Anzeigename "A7/A6" bzw. "C4".</summary>
        public string DisplayName => VehicleCategory2 > 0 ? Group1 + "/" + Group2 : Group1;
        /// <summary>Label im Lizenzrad von r_school.vmf ("A7_A6", "C4").</summary>
        public string FrameLabel => VehicleCategory2 > 0 ? Group1 + "_" + Group2 : Group1;

        /// <summary>Schultyp wie in der EXE (1 = Asphalt, 2 = Classic, 4 = Dirt).</summary>
        public int SchoolType => School == 'A' ? 1 : School == 'C' ? 2 : School == 'B' ? 4 : 0;

        public bool HasCategory(int cat) => cat > 0 && (VehicleCategory1 == cat || VehicleCategory2 == cat);
    }

    /// <summary>
    /// Renn-Datenbank (database/db_data.cpr): DBLicense = Rennschulen und Meisterschaften
    /// mit Insel, Reihenfolge, Fahrzeugklassen und der geordneten Liste der Challenge-Dateien.
    /// </summary>
    public sealed class RaceDatabase
    {
        public readonly List<LicenseInfo> Licenses = new List<LicenseInfo>();
        readonly Dictionary<ulong, LicenseInfo> _byHash = new Dictionary<ulong, LicenseInfo>();

        public LicenseInfo ByHash(ulong hash) => _byHash.TryGetValue(hash, out var l) ? l : null;

        /// <summary>DBPNJCar: Fahrer -> Autos (je Fahrzeugklasse eines), DBPNJ: Namen.</summary>
        public readonly Dictionary<int, List<int>> PnjCars = new Dictionary<int, List<int>>();
        public readonly Dictionary<int, string> PnjNames = new Dictionary<int, string>();

        /// <summary>Alle Meisterschaften einer Insel (Normal = Order 1, Elite = Order 2).</summary>
        public List<LicenseInfo> Championships(int island)
        {
            var list = new List<LicenseInfo>();
            foreach (var l in Licenses) if (l.IsChampionship && l.Island == island && l.ChallengeFiles.Count > 0) list.Add(l);
            list.Sort((a, b) => a.VehicleCategory1 != b.VehicleCategory1 ? a.VehicleCategory1.CompareTo(b.VehicleCategory1) : a.Order.CompareTo(b.Order));
            return list;
        }

        /// <summary>
        /// Fahrzeugklasse -> Name (Tabelle der EXE bei 0x1488948: A1..A7 = 1..7, B1..B4 = 9..12, C1..C4 = 14..17,
        /// MA1/MA2 = 18/19, R1..R6 = 23..28). Unbekannt/0 = "empty" wie im Spiel.
        /// </summary>
        public static string CategoryName(int cat)
        {
            if (cat >= 1 && cat <= 7) return "A" + cat;
            if (cat >= 9 && cat <= 12) return "B" + (cat - 8);
            if (cat >= 14 && cat <= 17) return "C" + (cat - 13);
            if (cat == 18) return "MA1";
            if (cat == 19) return "MA2";
            if (cat >= 23 && cat <= 28) return "R" + (cat - 22);
            return "empty";
        }

        /// <summary>
        /// Voraussetzung einer Lizenz (Switch der EXE bei 0xb905e0, nach Gruppe 1): A1 braucht die Lizenz mit A2,
        /// A3 die mit A4, A5 die mit A6, B3 die mit B4, C3 die mit C4; A7, B4, C4 sind frei. Liefert 0 = frei, -1 = nie.
        /// </summary>
        public static int RequiredCategory(int cat1)
        {
            switch (cat1)
            {
                case 1: return 2;
                case 3: return 4;
                case 5: return 6;
                case 7: return 0;
                case 11: return 12;
                case 12: return 0;
                case 16: return 17;
                case 17: return 0;
                default: return -1;
            }
        }

        /// <summary>Lizenz (Rennschule), die eine Fahrzeugklasse enthält.</summary>
        public LicenseInfo SchoolLicenseWithCategory(int cat)
        {
            foreach (var l in Licenses) if (l.IsRacingSchool && l.HasCategory(cat)) return l;
            return null;
        }

        /// <summary>Alle Rennschul-Lizenzen einer Insel (optional nur eine Schule 'A'/'B'/'C'), nach Reihenfolge sortiert.</summary>
        public List<LicenseInfo> RacingSchools(int island, char school = '\0')
        {
            var list = new List<LicenseInfo>();
            foreach (var l in Licenses)
                if (l.IsRacingSchool && l.Island == island && (school == '\0' || l.School == school)) list.Add(l);
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return list;
        }

        public static RaceDatabase LoadFromRoot(XmbNode root)
        {
            var db = new RaceDatabase();
            var t = root?.Get("DBLicense");
            if (t == null) return db;
            foreach (var r in t.Items)
            {
                var l = new LicenseInfo
                {
                    Hash = r.U("_hashcode"),
                    IsChampionship = r.B("_isachampionship"),
                    Island = r.I("_island"),
                    Order = r.I("_order"),
                    PlayerLevel = r.I("_playerlevel"),
                    VehicleCategory1 = r.I("_vehiclecategory_1"),
                    VehicleCategory2 = r.I("_vehiclecategory_2"),
                    NameHc = r.U("_championshipname_hc"),
                    DescHc = r.U("_championshipdesc_hc"),
                    RaceMoneyReward = r.I("_racemoneyreward"),
                    ChampMoneyReward = r.I("_champmoneyreward"),
                    RetriesBeforeEasier = r.I("_retriesbeforeeasier"),
                };
                for (int i = 1; i <= 8; i++)
                {
                    string f = r.S("_challengefile_" + i, "");
                    if (!string.IsNullOrEmpty(f) && f != "##") l.ChallengeFiles.Add(f);
                }
                for (int i = 1; i <= 7; i++)
                {
                    int b = r.I("_bot_" + i);
                    if (b > 0) l.Bots.Add((b, r.F("_difficulty_bot_" + i, 0.5f), r.F("_aggressiveness_bot_" + i, 0.5f)));
                }
                l.Rival = r.I("_rival");
                db.Licenses.Add(l);
                if (!db._byHash.ContainsKey(l.Hash)) db._byHash[l.Hash] = l;
            }
            var pc = root.Get("DBPNJCar");
            if (pc != null)
                foreach (var r in pc.Items)
                {
                    int p = r.I("_pnj_id"), c = r.I("_car_id");
                    if (p <= 0 || c <= 0) continue;
                    if (!db.PnjCars.TryGetValue(p, out var l)) db.PnjCars[p] = l = new List<int>();
                    l.Add(c);
                }
            var pn = root.Get("DBPNJ");
            if (pn != null)
                foreach (var r in pn.Items) db.PnjNames[r.I("_hashcode")] = r.S("_pnj_name", "");
            return db;
        }

        public static RaceDatabase Load(string bnkDir)
        {
            string p = Path.Combine(bnkDir, "database", "db_data.cpr");
            if (!File.Exists(p)) return new RaceDatabase();
            return LoadFromRoot(XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(p))));
        }
    }
}
