using System;
using System.Collections.Generic;
using System.IO;
using TDU2.Formats;

namespace TDU2.Vehicles
{
    /// <summary>Kategorie einer Radio-Nachricht (Feld <c>_type</c> in DBRadioMessage).</summary>
    public enum RadioMessageType
    {
        Comment = 0,
        BeforeAd = 1,
        BeforeMusic = 2,
        Short = 3,
        TimeMidday = 4,
        TimeNightfall = 5,
        TimeSunrise = 6,
        WeatherRain = 7,
        WeatherStorm = 8,
        WeatherSun = 9,
        Music = 10,
        Advertising = 11,
        Jingle = 13,
    }

    /// <summary>Ein Sender aus DBRadioStation (Hariba, RoadRock, Kalua ...).</summary>
    public sealed class RadioStationInfo
    {
        public int Hashcode;
        public string Name;            // _radiostation, z.B. "Hariba Radio"
        public string LogoLabel;       // _logoflashlabel, == RadioStation-Enum-Name ("hariba")
        public bool IsValid;
        public int IntervalMessages;   // _intervaltimemessages
        public float WeatherTime;      // _timeweather

        public override string ToString() => Name;
    }

    /// <summary>Ein Eintrag aus DBRadioMessage: ein Jingle, Werbespot, Kommentar oder Song.</summary>
    public sealed class RadioMessage
    {
        public ulong Hashcode;
        public int Station;            // _radio -> RadioStationInfo.Hashcode
        public RadioMessageType Type;
        public string File;            // _file -> Bankname (ohne .bnk)
        public string Artist;          // _artist (nur Musik gefüllt)
        public string Track;           // _trackname (nur Musik gefüllt)
        public float TimeBetweenTwo;   // _timebetweentwo

        public bool HasMetadata => !string.IsNullOrEmpty(Artist) || !string.IsNullOrEmpty(Track);

        public override string ToString() => $"{Type} {File}" + (HasMetadata ? $" ({Artist} - {Track})" : "");
    }

    /// <summary>Eintrag aus sound/radio/setup.xmb (ProjectExport.Tracks): Laufzeit und Marker eines Songs.</summary>
    public sealed class RadioTrackInfo
    {
        public string Name;            // z.B. "A Astronauts - Classic.wav"
        public ulong Hashcode;         // == DBRadioMessage._hashcode
        public float Duration;         // Sekunden
        public float LoopStart, LoopEnd;
    }

    /// <summary>
    /// Liest die Radio-Konfiguration: DBRadioStation + DBRadioMessage aus db_data.cpr, die Song-Laufzeiten
    /// aus sound/radio/setup.xmb und löst jede Nachricht auf ihre Bank in sound/radio/{music,jingle,comment,advertising} auf.
    /// Reine Datenhaltung ohne Unity-API -> im Hintergrund ladbar.
    /// </summary>
    public sealed class TDU2RadioData
    {
        public readonly Dictionary<int, RadioStationInfo> Stations = new Dictionary<int, RadioStationInfo>();
        public readonly List<RadioMessage> Messages = new List<RadioMessage>();
        public readonly Dictionary<string, RadioTrackInfo> Tracks = new Dictionary<string, RadioTrackInfo>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Track-Laufzeit über den Hashcode (== DBRadioMessage._hashcode) – Grundlage der synchronen Playlist.</summary>
        public readonly Dictionary<ulong, RadioTrackInfo> TracksByHashcode = new Dictionary<ulong, RadioTrackInfo>();
        /// <summary>Bankname (klein, ohne .bnk) -> voller Pfad. Aus dem tatsächlichen Ordnerinhalt gebaut.</summary>
        public readonly Dictionary<string, string> BankPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string BnkDir;
        public string RadioDir;
        public string Error;

        public static readonly string[] Categories = { "music", "jingle", "comment", "advertising" };

        /// <summary>Ordner einer Kategorie innerhalb von sound/radio.</summary>
        public static string CategoryOf(RadioMessageType t)
        {
            switch (t)
            {
                case RadioMessageType.Advertising: return "advertising";
                case RadioMessageType.Jingle: return "jingle";
                case RadioMessageType.Music: return "music";
                default: return "comment";
            }
        }

        public bool IsMusic(RadioMessage m) => m != null && m.Type == RadioMessageType.Music;

        public RadioStationInfo StationByLabel(string label)
        {
            foreach (var s in Stations.Values)
                if (string.Equals(s.LogoLabel, label, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Alle Nachrichten eines Senders, optional nach Typ gefiltert.</summary>
        public List<RadioMessage> ForStation(int stationHashcode, params RadioMessageType[] types)
        {
            var list = new List<RadioMessage>();
            foreach (var m in Messages)
            {
                if (m.Station != stationHashcode) continue;
                if (types != null && types.Length > 0 && Array.IndexOf(types, m.Type) < 0) continue;
                list.Add(m);
            }
            return list;
        }

        /// <summary>Voller Bankpfad einer Nachricht oder null, wenn die Bank fehlt.</summary>
        public string BankPathFor(RadioMessage m)
        {
            if (m == null || string.IsNullOrEmpty(m.File)) return null;
            if (BankPaths.TryGetValue(m.File, out var path)) return path;
            return null;
        }

        public static TDU2RadioData Load(string bnkDir)
        {
            var d = new TDU2RadioData { BnkDir = bnkDir, RadioDir = Path.Combine(bnkDir, "sound", "radio") };
            try
            {
                d.LoadDatabase(Path.Combine(bnkDir, "database", "db_data.cpr"));
                d.IndexBanks();
                d.LoadSetup(Path.Combine(d.RadioDir, "setup.xmb"));
            }
            catch (Exception ex)
            {
                d.Error = ex.Message;
            }
            return d;
        }

        void LoadDatabase(string dbPath)
        {
            if (!File.Exists(dbPath)) { Error = "db_data.cpr fehlt: " + dbPath; return; }
            var root = XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(dbPath)));

            var st = root.Get("DBRadioStation");
            if (st != null)
                foreach (var r in st.Items)
                    Stations[r.I("_hashcode")] = new RadioStationInfo
                    {
                        Hashcode = r.I("_hashcode"),
                        Name = r.S("_radiostation"),
                        LogoLabel = r.S("_logoflashlabel"),
                        IsValid = r.B("_isvalid"),
                        IntervalMessages = r.I("_intervaltimemessages"),
                        WeatherTime = r.F("_timeweather"),
                    };

            var msg = root.Get("DBRadioMessage");
            if (msg != null)
                foreach (var r in msg.Items)
                    Messages.Add(new RadioMessage
                    {
                        Hashcode = r.U("_hashcode"),
                        Station = r.I("_radio"),
                        Type = (RadioMessageType)r.I("_type"),
                        File = r.S("_file"),
                        Artist = r.S("_artist"),
                        Track = r.S("_trackname"),
                        TimeBetweenTwo = r.F("_timebetweentwo"),
                    });
        }

        void IndexBanks()
        {
            if (!Directory.Exists(RadioDir)) return;
            foreach (var cat in Categories)
            {
                string dir = Path.Combine(RadioDir, cat);
                if (!Directory.Exists(dir)) continue;
                foreach (var p in Directory.GetFiles(dir, "*.bnk"))
                    BankPaths[Path.GetFileNameWithoutExtension(p)] = p;
            }
        }

        void LoadSetup(string path)
        {
            if (!File.Exists(path)) return;
            if (!XmbFile.IsXmb(File.ReadAllBytes(path))) { }
            var root = XmbFile.Parse(File.ReadAllBytes(path));
            var tracks = root.Get("Tracks") ?? root.Get("ProjectExport")?.Get("Tracks");
            if (tracks == null) return;
            foreach (var t in tracks.Items)
            {
                string name = t.S("Name");
                if (string.IsNullOrEmpty(name)) continue;
                var info = new RadioTrackInfo
                {
                    Name = name,
                    Hashcode = t.U("Hashcode"),
                    Duration = t.F("Duration"),
                    LoopStart = t.F("LoopStart"),
                    LoopEnd = t.F("LoopEnd"),
                };
                Tracks[name] = info;
                if (info.Hashcode != 0) TracksByHashcode[info.Hashcode] = info;
            }
        }

        /// <summary>Laufzeit (Sekunden) einer Nachricht oder 0, wenn unbekannt. Deterministisch – gleiche Dateien, gleiches Ergebnis.</summary>
        public float DurationOf(RadioMessage m)
        {
            if (m == null) return 0f;
            if (m.Hashcode != 0 && TracksByHashcode.TryGetValue(m.Hashcode, out var byHash) && byHash.Duration > 0f) return byHash.Duration;
            if (!string.IsNullOrEmpty(m.File) && Tracks.TryGetValue(m.File + ".wav", out var byName) && byName.Duration > 0f) return byName.Duration;
            return 0f;
        }

        // ------------------------------------------------------------------
        //  Deterministische Playlist: aus gemeinsamem Startwert + Sender rechnet jeder
        //  Client denselben Sendeablauf; die Position ergibt sich aus der Serverzeit.
        // ------------------------------------------------------------------

        /// <summary>Ein Segment des Sendeablaufs (Song, Jingle, Kommentar oder Werbung) mit Zeitfenster.</summary>
        public sealed class RadioSegment
        {
            public RadioMessage Message;
            public float Start;      // Sekunden ab Zyklusanfang
            public float Duration;
            public override string ToString() => $"{Start:F0}s +{Duration:F1}s {Message}";
        }

        /// <summary>Sich wiederholender Sendeablauf eines Senders.</summary>
        public sealed class RadioPlaylist
        {
            public RadioSegment[] Segments;
            public float Total;

            /// <summary>Segment zur Position (Sekunden im Zyklus) inkl. Offset innerhalb des Segments.</summary>
            public RadioSegment At(double pos, out float offset)
            {
                offset = 0f;
                if (Segments == null || Segments.Length == 0) return null;
                if (pos < 0) pos = 0;
                int lo = 0, hi = Segments.Length - 1, idx = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (Segments[mid].Start <= pos) { idx = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                offset = (float)(pos - Segments[idx].Start);
                return Segments[idx];
            }
        }

        /// <summary>Plattformunabhängiger Zufallsgenerator – garantiert auf allen Clients dieselbe Folge.</summary>
        struct DetRng
        {
            uint _s;
            public DetRng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }
            public uint Next() { uint x = _s; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _s = x; return x; }
            public double NextDouble() => (Next() >> 8) * (1.0 / 16777216.0);
            public int Range(int n) => n <= 1 ? 0 : (int)(Next() % (uint)n);
        }

        static uint Mix(uint a, uint b)
        {
            uint h = a * 0x9E3779B1u + b * 0x85EBCA77u;
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        /// <summary>
        /// Baut den Sendeablauf eines Senders: Songs deterministisch gemischt, dazwischen (nach jedem Song)
        /// höchstens ein Zwischenstück. Gleiche Eingaben ergeben auf jedem Client denselben Ablauf.
        /// </summary>
        public RadioPlaylist BuildPlaylist(int stationHashcode, int seed, float jingleChance, float commentChance, float adChance)
        {
            var songs = ForStation(stationHashcode, RadioMessageType.Music);
            var rng = new DetRng(Mix((uint)seed, (uint)stationHashcode));
            for (int i = songs.Count - 1; i > 0; i--)
            {
                int j = rng.Range(i + 1);
                var tmp = songs[i]; songs[i] = songs[j]; songs[j] = tmp;
            }

            var msgs = new List<RadioMessage>();
            for (int j = 0; j < songs.Count; j++)
            {
                msgs.Add(songs[j]);
                var inter = PickInterstitial(stationHashcode, seed, j, jingleChance, commentChance, adChance);
                if (inter != null) msgs.Add(inter);
            }

            var list = new List<RadioSegment>(msgs.Count);
            float t = 0f;
            foreach (var m in msgs)
            {
                if (m == null) continue;
                float d = DurationOf(m);
                if (d <= 0.2f) continue;   // ohne Laufzeit nicht einplanbar
                list.Add(new RadioSegment { Message = m, Start = t, Duration = d });
                t += d;
            }

            return new RadioPlaylist { Segments = list.ToArray(), Total = t };
        }

        RadioMessage PickInterstitial(int stationHashcode, int seed, int songIndex, float jingleChance, float commentChance, float adChance)
        {
            var rng = new DetRng(Mix(Mix((uint)seed, (uint)stationHashcode) ^ (uint)songIndex, 0x68BC21EBu));
            bool forceJingle = (songIndex % 4) == 3;   // regelmäßig ein Jingle
            double roll = rng.NextDouble();

            if (forceJingle || roll < jingleChance)
                return PickFrom(stationHashcode, rng, RadioMessageType.Jingle);
            if (roll < jingleChance + commentChance)
                return PickFrom(stationHashcode, rng,
                    RadioMessageType.BeforeAd, RadioMessageType.BeforeMusic, RadioMessageType.Short,
                    RadioMessageType.TimeMidday, RadioMessageType.TimeNightfall, RadioMessageType.TimeSunrise,
                    RadioMessageType.WeatherRain, RadioMessageType.WeatherStorm, RadioMessageType.WeatherSun);
            if (roll < jingleChance + commentChance + adChance)
                return PickFrom(stationHashcode, rng, RadioMessageType.Advertising);
            return null;
        }

        RadioMessage PickFrom(int stationHashcode, DetRng rng, params RadioMessageType[] types)
        {
            var list = ForStation(stationHashcode, types);
            return list.Count > 0 ? list[rng.Range(list.Count)] : null;
        }
    }
}
