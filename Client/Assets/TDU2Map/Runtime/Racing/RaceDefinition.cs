using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>Testart einer Rennschul-Prüfung (ig2 "rsl2"). Reihenfolge = Zeigertabelle der EXE (0x1674d04),
    /// die Namen sind die Frame-Labels des Typ-Symbols in r_school.vmf (/tests/lineN/icons/type).</summary>
    public enum SchoolTestType { Turn = 0, Turn90, Turn180, Slalom, Overtaking, Drift, Acceleration, Braking, Mixed, Circuit, Manual, Obstacle, Jump }

    /// <summary>Ort der Prüfung (ig2 "rsl1"): Schulgelände oder offene Straße (Labels "r_school" / "road").</summary>
    public enum SchoolTestPlace { School = 0, Road = 1 }

    /// <summary>Wetter der Prüfung (ig2 "rswh"): Labels "sun" / "rain" / "night".</summary>
    public enum SchoolTestWeather { Sun = 0, Rain = 1, Night = 2 }

    /// <summary>Deko einer Prüfung: einzelner Kegel / Absperrung oder Linie von A nach B (Inselkoordinaten).</summary>
    public sealed class RaceDecor
    {
        public bool IsSeparator;   // false = Kegel, true = Absperrung ("Separ")
        public bool IsLine;
        public Vector3 From, To;   // bei Einzelobjekten From == To
        public Vector3 Forward;    // Ausrichtung des Einzelobjekts
    }

    /// <summary>Fahrzeug einer Prüfung (VEHI), z.B. die zu überholenden Autos.</summary>
    public sealed class RaceOpponentDef
    {
        public int CarId;           // DBCarPhysicsData._id_car
        public Vector3 Position;    // Inselkoordinaten
        public Vector3 Forward;
        public float MaxSpeed;      // m/s (Spmx / 1000)
        public int StartCheckpoint; // Stch (vermutet: fährt los, sobald der Spieler diesen Checkpoint erreicht)
        // --- Meisterschafts-Gegner (Startaufstellung)
        public string Name;         // Fahrername (DBPNJ)
        public float Accel = 4f;    // m/s²
        public float LatAccel;      // Querbeschleunigung in Kurven (m/s²); 0 = Kurven nicht beachten (Prüfungsfahrzeuge)
        public float LateralOffset; // seitlicher Abstand zur Ideallinie (m)
        public List<Vector3> PathOverride;   // feste Route (Inselkoordinaten), sonst durch die Tore
        public float StartDistance; // Weg bis zur Startlinie (m, negativ = dahinter) für die Platzierung
    }

    /// <summary>Spielart einer Challenge (ig2 "gmth"), nach den Meisterschafts-Dateien: 0 Zeitfahren (…T), 1 Radar (…A),
    /// 3 Rennen (…R), 10 Tempo (…S). [vermutet aus Namen/Daten]</summary>
    public enum RaceMode { TimeAttack = 0, SpeedTrap = 1, Race = 3, Speed = 10 }

    /// <summary>
    /// Eine fahrbare Prüfung/ein Rennen: Startpunkt, geordnete Checkpoints, Runden, Zeiten, Auto, Props.
    /// Zusammengesetzt aus DBLicense (Lizenz), spotworld.bin (Startpunkt) und der .ig2-Datei (Tore, Einstellungen).
    /// </summary>
    public sealed class RaceDefinition
    {
        public string Name;             // z.B. "RSA1T06"
        public ulong LicenseHash;
        public char School;             // 'A' Asphalt, 'B' Dirt, 'C' Classic (nur Rennschule)
        public int Island;
        public uint Mode;               // ig2 "gmth" (0 = Zeitfahren)
        public int Laps = 1;            // effektive Runden (nblp nur bei Rundkursen, sonst 1)
        public int LapsRaw;             // ig2 "nblp" wie gespeichert
        public bool IsCircuit;          // ig2 "#" = 1
        public float Length;            // ig2 "leme" (Meter)
        public Vector3 StartPosition;   // Startposition (Inselkoordinaten)
        public float StartHeading;      // Startrichtung aus spotworld.bin (Bogenmaß)
        public bool HasSpotStart;       // Startpunkt stammt aus spotworld.bin
        public int TargetTimeMs;        // Referenz-/Zielzeit "timG" (Gold); bestanden, wenn Zeit+Strafen <= Ziel
        public int SilverTimeMs, BronzeTimeMs;
        public bool HasTimeLimit;       // ig2 "ChCD" = 1: Countdown läuft, Ablauf = nicht bestanden
        public int TimeLimitMs;         // ig2 "CtDw" (bei ChCD = 0: Abbruch nach dieser Zeit)
        public int RewardMoney;         // "rewm"
        public int CarId;               // "VMod": Auto der Prüfung (DBCarPhysicsData._id_car)
        public int PenaltyLevel;        // "PeLv" (0..2) -> DBEnvironnement.PenaltyTimeLevel01..03
        public bool OffroadPenalty;     // "offR"
        public SchoolTestType TestType;
        public SchoolTestPlace Place;
        public SchoolTestWeather Weather;
        public bool IsGradedTest;       // "TEST"
        public ulong NameHc, DescHc;    // "dbna"/"dbd1": Codes der Textdatenbank (db_<sprache>.cpr)

        public readonly List<RaceCheckpoint> Checkpoints = new List<RaceCheckpoint>();
        public readonly List<RaceDecor> Decors = new List<RaceDecor>();
        public readonly List<RaceOpponentDef> Opponents = new List<RaceOpponentDef>();
        /// <summary>Route entlang der Straßen (Inselkoordinaten), siehe <see cref="RaceRoadPath"/>; null = nicht berechnet.</summary>
        public List<Vector3> RoadPath;
        /// <summary>Je Punkt von <see cref="RoadPath"/>: Checkpoint-Index des Abschnitts (für die Färbung).</summary>
        public List<int> RoadPathSeg;

        public RaceCheckpoint Start => Checkpoints.Count > 0 ? Checkpoints[0] : null;
        public RaceCheckpoint Finish => Checkpoints.Count > 0 ? Checkpoints[Checkpoints.Count - 1] : null;
        public bool EndsInBrakingZone => !IsCircuit && Finish != null && Finish.IsBrakingZone;

        /// <summary>Reihenfolge der anzufahrenden Checkpoints (Indizes in <see cref="Checkpoints"/>), ohne die Startlinie.
        /// Punkt-zu-Punkt: 1..n-1. Rundkurs: je Runde 1..n-1 und wieder 0 (Start-/Ziellinie).</summary>
        public List<int> TargetSequence()
        {
            var seq = new List<int>();
            int n = Checkpoints.Count;
            if (n < 2) return seq;
            int laps = IsCircuit ? Mathf.Max(1, Laps) : 1;
            for (int l = 0; l < laps; l++)
            {
                for (int i = 1; i < n; i++) seq.Add(i);
                if (IsCircuit) seq.Add(0);
            }
            return seq;
        }

        public int TargetCount => TargetSequence().Count;
        public int TargetsPerLap => Checkpoints.Count < 2 ? 0 : (IsCircuit ? Checkpoints.Count : Checkpoints.Count - 1);

        public string Title => string.IsNullOrEmpty(Name) ? Name : Name.ToUpperInvariant();
        public RaceMode Kind => (RaceMode)Mode;
        public int BotCount;            // ig2 "NbIA"
    }
}
