using System;
using System.Collections.Generic;
using System.IO;
using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>
    /// Lädt Renn-/Rennschul-Definitionen aus einem Bnk-Verzeichnis:
    /// database/db_data.cpr (DBLicense), scenarics/spotworld.bin (Startpunkte) und ig2/&lt;Name&gt;.ig2 (Tore, Props, Einstellungen).
    /// </summary>
    public sealed class RaceRepository
    {
        public RaceDatabase Database;
        public SpotWorldFile SpotWorld;
        public string BnkDir;

        readonly Dictionary<string, Ige2File> _ig2 = new Dictionary<string, Ige2File>(StringComparer.OrdinalIgnoreCase);

        public string Ige2Dir => Path.Combine(BnkDir, "ig2");

        public static RaceRepository Load(string bnkDir)
        {
            var r = new RaceRepository { BnkDir = bnkDir, Database = RaceDatabase.Load(bnkDir) };
            string sp = Path.Combine(bnkDir, "scenarics", "spotworld.bin");
            if (File.Exists(sp)) r.SpotWorld = SpotWorldFile.Parse(File.ReadAllBytes(sp));
            return r;
        }

        public Ige2File LoadIge2(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_ig2.TryGetValue(name, out var cached)) return cached;
            string path = Path.Combine(Ige2Dir, name.ToLowerInvariant() + ".ig2");
            Ige2File f = null;
            try { if (File.Exists(path)) f = Ige2File.Parse(File.ReadAllBytes(path)); }
            catch (Exception ex) { Debug.LogWarning("[TDU2] " + name + ".ig2: " + ex.Message); }
            _ig2[name] = f;
            return f;
        }

        /// <summary>Baut eine Renn-Definition aus Prüfungsname (.ig2) und zugehöriger Lizenz.</summary>
        public RaceDefinition Build(string challengeName, LicenseInfo license)
        {
            var ig2 = LoadIge2(challengeName);
            if (ig2 == null) return null;
            var def = new RaceDefinition
            {
                Name = challengeName,
                LicenseHash = license != null ? license.Hash : 0,
                School = license != null ? license.School : '\0',
                Island = ig2.Island,
                Mode = ig2.Mode,
                LapsRaw = ig2.Laps,
                IsCircuit = ig2.IsCircuit,
                Length = ig2.Length,
                TargetTimeMs = ig2.Int("timG"),
                SilverTimeMs = ig2.Int("timS"),
                BronzeTimeMs = ig2.Int("timB"),
                HasTimeLimit = ig2.Int("ChCD") == 1,
                TimeLimitMs = ig2.Int("CtDw"),
                RewardMoney = ig2.Int("rewm"),
                CarId = ig2.Int("VMod"),
                PenaltyLevel = ig2.Int("PeLv"),
                OffroadPenalty = ig2.Int("offR") != 0,
                TestType = (SchoolTestType)Mathf.Clamp(ig2.Int("rsl2"), 0, (int)SchoolTestType.Jump),
                Place = ig2.Int("rsl1") == 1 ? SchoolTestPlace.Road : SchoolTestPlace.School,
                Weather = (SchoolTestWeather)Mathf.Clamp(ig2.Int("rswh"), 0, 2),
                IsGradedTest = ig2.Int("TEST") != 0,
                NameHc = (ulong)(ig2.Setting("dbna")?.AsLong ?? 0),
                DescHc = (ulong)(ig2.Setting("dbd1")?.AsLong ?? 0),
                BotCount = ig2.Int("NbIA"),
            };
            def.Laps = def.IsCircuit ? Mathf.Max(1, def.LapsRaw) : 1;

            for (int i = 0; i < ig2.Checkpoints.Count; i++) def.Checkpoints.Add(RaceCheckpoint.From(ig2.Checkpoints[i], i));
            if (def.Checkpoints.Count > 0) def.Checkpoints[def.IsCircuit ? 0 : def.Checkpoints.Count - 1].IsFinish = true;

            BuildDecors(ig2, def);
            foreach (var v in ig2.Vehicles)
            {
                if (v.CarId <= 0) continue;
                var f = v.Forward; f.y = 0f; if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
                def.Opponents.Add(new RaceOpponentDef { CarId = v.CarId, Position = v.Position, Forward = f.normalized, MaxSpeed = v.MaxSpeed, StartCheckpoint = v.StartCheckpoint });
            }

            var entry = SpotWorld != null ? SpotWorld.Find(challengeName) : null;
            if (entry != null && entry.Position.sqrMagnitude > 1f)
            {
                def.StartPosition = entry.Position; def.StartHeading = entry.Heading; def.HasSpotStart = true;
            }
            else if (def.Start != null) def.StartPosition = def.Start.Position;
            return def;
        }

        /// <summary>DEC2: 0 Kegel, 1→2 Kegel-Linie, 3 Absperrung, 4→5 Absperrungs-Linie (siehe <see cref="Ige2Decor"/>).</summary>
        static void BuildDecors(Ige2File ig2, RaceDefinition def)
        {
            var d = ig2.Decors;
            for (int i = 0; i < d.Count; i++)
            {
                var a = d[i];
                var f = a.Forward; f.y = 0f; if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
                bool lineStart = a.Kind == 1 || a.Kind == 4;
                if (lineStart && i + 1 < d.Count && d[i + 1].Kind == a.Kind + 1)
                {
                    def.Decors.Add(new RaceDecor { IsSeparator = a.Kind == 4, IsLine = true, From = a.Position, To = d[i + 1].Position, Forward = f.normalized });
                    i++;
                    continue;
                }
                bool sep = a.Kind >= 3;
                def.Decors.Add(new RaceDecor { IsSeparator = sep, IsLine = false, From = a.Position, To = a.Position, Forward = f.normalized });
            }
        }

        /// <summary>Alle Prüfungen einer Lizenz in Reihenfolge.</summary>
        public List<RaceDefinition> BuildForLicense(LicenseInfo license)
        {
            var list = new List<RaceDefinition>();
            if (license == null) return list;
            foreach (var cf in license.ChallengeFiles)
            {
                var def = Build(cf, license);
                if (def != null) list.Add(def);
                else Debug.LogWarning("[TDU2] Rennschule: Prüfung " + cf + " (ig2) fehlt");
            }
            return list;
        }

        /// <summary>Alle Rennschul-Prüfungen einer Insel (optional auf einen Schul-Buchstaben 'A'/'B'/'C' gefiltert).</summary>
        public List<RaceDefinition> SchoolRaces(int island, char school = '\0')
        {
            var list = new List<RaceDefinition>();
            foreach (var lic in Database.RacingSchools(island, school)) list.AddRange(BuildForLicense(lic));
            return list;
        }
    }
}
