using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TDU2.Flash;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Online
{
    /// <summary>Ein Spieler (online oder zuletzt gesehen) – Daten für Pausemenü-Listen, Spielerkarte und Karten-Blips.</summary>
    public sealed class PlayerInfo
    {
        public int Id;                  // Server-Spielernummer (0 = nicht online)
        public string Nick = "";
        public int CarId, Color;
        public long Money;
        public int NbCars;
        public int Nationality;          // Ländernummer der Flagge (24 = Deutschland)
        public bool Online, InCar;
        public bool HasPos;
        public int Island;              // 1 Ibiza, 2 Hawaii, 0 unbekannt
        public bool SameIsland => TDU2Multiplayer.SameIsland(Island);
        public Vector3 GamePos;         // Spielkoordinaten (Insel)
        public Vector3 GameForward = Vector3.forward;
        public float LastUpdate;        // Time.unscaledTime der letzten Position
        public int RideWith, Passenger; // Mitfahrt: fährt bei … mit / hat … als Beifahrer (Server-Spielernummern)
        public DateTime LastSeen;       // für "Zuletzt gesehen"
        public CarPhysics Car;          // aufgelöst aus der Datenbank (CarId)
        public string Avatar = "";      // Aussehen (AvatarConfig.Encode) aus der Server-Spielerliste
        public string Mood = "", MoodTxt = "";   // MyTDULife: Smiley + Stimmungstext (Spielerliste)
        public int PhotoVer;                     // MyTDULife: Version des Avatar-Fotos (0 = keins)
        public MyLifeData Life;                  // MyTDULife komplett (op "profile"), null = noch nicht geladen
        public Texture2D Photo;                  // Avatar-Foto (aus Life.photo)
        public int PhotoTexVer = -1;
        public string CarName => Car == null ? "" : ((Car.Brand ?? "") + " " + Car.ModelName).Trim();
    }

    /// <summary>
    /// Zentrale Datenhaltung für Online/Spieler/Profil – alle Flash-Filme lesen hieraus:
    ///   * Server-Adresse (Inspector oder Startparameter "-server host[:port]") – für Tests mit Freunden hier die IP eintragen.
    ///   * Online-Status und Spielerzahl: Minimap (/HUD/minimap/ic_status, nb_players) und in allen geladenen Filmen jeder Clip
    ///     "ic_status" mit den Labels connected/disconnected bzw. online/offline, dazu die Variablen /:IsOnline, /:NbPlayers.
    ///   * Eigene Daten: /:PlayerName, /:CurrentProfile, /:Money, /:money, /:VehicleName, /:player_car.
    ///   * Spielerlisten: Lobby (alle online), Zuletzt gesehen (gespeichert je Profil), Freunde/Club (noch leer).
    ///   * general.bnk (globales Overlay wie im Spiel): deco_pop = Verbindung verloren, online_evt = Spieler online/offline,
    ///     saving_logo = Speichern.
    /// </summary>
    [AddComponentMenu("TDU2/Online Manager (Server, Spieler, Flash-Daten)")]
    [DefaultExecutionOrder(-200)]
    public sealed class TDU2GameData : MonoBehaviour
    {
        public static TDU2GameData Instance { get; private set; }

        [Header("Server (für Tests mit Freunden: IP/Hostname des Rechners mit TDU2Server)")]
        public string ServerHost = "127.0.0.1";
        public int TcpPort = 7777;
        [Tooltip("Wird beim Anmelden vom Server übernommen (config.json udpPort)")]
        public int UdpPort = 7778;
        [Tooltip("Spielerliste/Spielerzahl abfragen (Sekunden)")]
        public float StatusSeconds = 3f;
        [Tooltip("Eigenes Land (Flagge im Ausweis/Spielerlisten): Nummer wie im Spiel (24 = Deutschland). 0 = aus den Windows-Regionseinstellungen")]
        public int Nationality = 0;
        /// <summary>Ländernummer für die Flaggen (Inspector oder System).</summary>
        public int NationalityId => Nationality > 0 ? Nationality : TDU2.TDU2CountryHelper.GetSystemCountryId();
        [Tooltip("Anmeldung mit Profilmenü (log_in2.bnk + main.bnk). Aus = Offline wie früher (kein Server, keine Mitspieler)")]
        public bool UseLogin = true;

        [Header("Spielstand")]
        [Tooltip("Spielstand automatisch auf dem Server speichern (Sekunden)")]
        public float AutosaveSeconds = 30f;

        [Header("Mitspieler-Synchronisation (UDP)")]
        [Tooltip("Zustandspakete pro Sekunde")]
        public float SendRate = 20f;
        [Tooltip("Darstellungsverzögerung für die Interpolation (Sekunden) – höher = ruhiger bei schlechter Verbindung")]
        public float InterpDelay = 0.12f;
        [Tooltip("Mitspieler ohne Pakete so lange behalten (Sekunden)")]
        public float Timeout = 4f;
        [Tooltip("Mitspieler-Autos höchstens so weit entfernt darstellen (m)")]
        public float MaxDistance = 3000f;
        [Tooltip("Namen über den Autos der Mitspieler")]
        public bool ShowNames = true;
        [Tooltip("Mitspieler-Autos sind fest (man prallt ab statt durchzufahren)")]
        public bool PlayerCollisions = true;
        [Tooltip("Motor- und Hupen-Sound der Mitspieler (3D)")]
        public bool PlayerSounds = true;
        [Tooltip("Objekt beim Szenenwechsel behalten")]
        public bool KeepBetweenScenes = true;

        [Header("Anzeige")]
        [Tooltip("general.bnk als Overlay laden (Verbindung verloren, Spieler online/offline, Speichern)")]
        public bool UseGeneralOverlay = true;
        [Tooltip("Meldung 'Spieler online/offline' einblenden (general.bnk online_evt)")]
        public bool ShowPlayerEvents = true;
        [Tooltip("Speicher-Symbol (saving_logo) mindestens so lange zeigen (Sekunden)")]
        public float SavingLogoMinSeconds = 2f;

        [Header("Chat (general.bnk pop_chat, Taste 'Chat' = T)")]
        public bool EnableChat = true;
        [Tooltip("Chatzeile so lange anzeigen (Sekunden)")]
        public float ChatLineSeconds = 20f;
        [Tooltip("Maximale Länge einer Nachricht")]
        public int ChatMaxLength = 100;

        // ---- Zustand (lesbar für alle)
        public bool Online { get; private set; }
        public int PlayerCount { get; private set; }
        public string PlayerName => TDU2Session.LoggedIn ? TDU2Session.Nick : "";
        public long Money => TDU2Session.Save?.money ?? 0;
        public int NbCars => TDU2Session.Save?.ownedCars?.Count ?? 0;
        public CarPhysics CurrentCar => _manager != null && _manager.Current != null ? _manager.Current.Car : null;
        public string CarName { get { var c = CurrentCar; return c == null ? "" : ((c.Brand ?? "") + " " + c.ModelName).Trim(); } }

        /// <summary>Spieler online (ohne einen selbst), nach Spielernummer.</summary>
        public readonly Dictionary<int, PlayerInfo> Players = new Dictionary<int, PlayerInfo>();
        public readonly List<PlayerInfo> Recent = new List<PlayerInfo>();
        public readonly List<PlayerInfo> Friends = new List<PlayerInfo>();
        public readonly List<PlayerInfo> Club = new List<PlayerInfo>();
        public event Action Changed;

        TDU2VehicleManager _manager;
        Task<NetResponse> _status;
        float _nextStatus, _nextPush;
        bool _wasOnline, _firstList = true;
        int _failCount;

        public static TDU2GameData Ensure()
        {
            if (Instance != null) return Instance;
#if UNITY_2023_1_OR_NEWER
            var found = FindFirstObjectByType<TDU2GameData>();
#else
            var found = FindObjectOfType<TDU2GameData>();
#endif
            if (found != null) { found.Awake(); return found; }
            var go = new GameObject("TDU2 Game Data");
            return go.AddComponent<TDU2GameData>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) return;
            if (KeepBetweenScenes && Application.isPlaying && transform.parent == null && GetComponents<Component>().Length > 2)
            {
                // Liegt die Komponente mit anderen (z.B. TDU2MapStreamer/TDU2Traffic auf "TDU2 Map") auf einem Objekt, würde
                // DontDestroyOnLoad das ganze Objekt behalten: beim Inselwechsel streamte die alte Karte weiter -> zwei Inseln.
                // Deshalb auf ein eigenes Objekt umziehen (Einstellungen werden übernommen).
                var json = JsonUtility.ToJson(this);
                var go = new GameObject("TDU2 Online Manager");
                go.SetActive(false);
                var moved = go.AddComponent<TDU2GameData>();
                JsonUtility.FromJsonOverwrite(json, moved);
                go.SetActive(true);   // Awake der Kopie: Instance + DontDestroyOnLoad
                Debug.LogWarning("[TDU2] TDU2GameData lag auf '" + name + "' zusammen mit anderen Komponenten – auf eigenes Objekt verschoben (bitte in der Szene auf ein eigenes GameObject legen).");
                Destroy(this);
                return;
            }
            Instance = this;
            if (KeepBetweenScenes && Application.isPlaying && transform.parent == null) DontDestroyOnLoad(gameObject);
            ApplyCommandLine();
            VmfPlayer.CommandKeyName = CommandKey;
        }

        /// <summary>"#command:NAME#" in den Texten -> Taste aus der Tastenbelegung (Optionen/Steuerung).</summary>
        static string CommandKey(string name)
        {
            string id;
            switch ((name ?? "").ToUpperInvariant())
            {
                case "CAR_ACCEL": id = "accel"; break;
                case "CAR_BRAKE": id = "brake"; break;
                case "CAR_HBRAKE": id = "handbrake"; break;
                case "CAR_GUP": id = "gearup"; break;
                case "CAR_GDOWN": id = "geardown"; break;
                case "CAR_HORN": id = "horn"; break;
                case "CAR_CAMERA": id = "camera"; break;
                case "CAR_BACKVIEW": id = "lookback"; break;
                case "CAR_LIGHT": id = "lights"; break;
                case "BACK_ON_TRACK": id = "reset"; break;
                case "MAP": id = "map"; break;
                case "MENU_PAUSE": return "Esc";
                case "MENU_VALIDATE": return "Enter";
                case "MENU_BACK": return "Rücktaste";
                default: return null;
            }
            var a = TDU2Controls.Get(id);
            return a != null ? TDU2Controls.BindText(a) : null;
        }

        /// <summary>Startparameter: -server 192.168.0.10[:7777]</summary>
        void ApplyCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (!string.Equals(args[i], "-server", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = args[i + 1];
                    int c = v.LastIndexOf(':');
                    if (c > 0 && int.TryParse(v.Substring(c + 1), out int port)) { ServerHost = v.Substring(0, c); TcpPort = port; }
                    else ServerHost = v;
                }
            }
            catch (Exception) { }
        }

        public void BindManager(TDU2VehicleManager m) { _manager = m; }

        // ------------------------------------------------------------------ MyTDULife anderer Spieler (Spielerkarte)
        readonly Dictionary<int, (Task<NetResponse> task, PlayerInfo p, List<Action<PlayerInfo>> done)> _lifeReq = new Dictionary<int, (Task<NetResponse>, PlayerInfo, List<Action<PlayerInfo>>)>();

        /// <summary>
        /// MyTDULife (Stimmung, Profil, Avatar-Foto) eines Spielers holen; done läuft im Hauptthread, sobald p.Life/p.Photo da sind
        /// (sofort, wenn schon aktuell). Offline-Spieler (Id 0) werden über den Namen gesucht.
        /// </summary>
        public void FetchLife(PlayerInfo p, Action<PlayerInfo> done)
        {
            if (p == null) return;
            if (p.Life != null && p.PhotoTexVer == p.PhotoVer && p.PhotoVer >= 0 && (p.PhotoVer == 0 || p.Photo != null)) { done?.Invoke(p); return; }
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null) return;
            int key = p.Id != 0 ? p.Id : -Math.Abs((p.Nick ?? "").GetHashCode() | 1);
            if (_lifeReq.TryGetValue(key, out var pend)) { if (done != null) pend.done.Add(done); return; }
            // nicht dauernd neu fragen (Spielerliste aktualisiert jede Sekunde): je Spieler und Foto-Version höchstens alle 15 s
            if (_lifeTried.TryGetValue(key, out var tried) && tried.ver == p.PhotoVer && Time.unscaledTime - tried.at < 15f) return;
            _lifeTried[key] = (p.PhotoVer, Time.unscaledTime);
            var t = TDU2Session.Client.Send(new NetRequest { op = "profile", session = TDU2Session.SessionId, target = p.Id, nick = p.Id == 0 ? p.Nick : "" });
            _lifeReq[key] = (t, p, done != null ? new List<Action<PlayerInfo>> { done } : new List<Action<PlayerInfo>>());
        }

        readonly Dictionary<int, (int ver, float at)> _lifeTried = new Dictionary<int, (int, float)>();

        void PollLife()
        {
            if (_lifeReq.Count == 0) return;
            List<int> fin = null;
            foreach (var kv in _lifeReq) if (kv.Value.task.IsCompleted) (fin ??= new List<int>()).Add(kv.Key);
            if (fin == null) return;
            foreach (int k in fin)
            {
                var (task, p, done) = _lifeReq[k];
                _lifeReq.Remove(k);
                var r = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (r == null || !r.ok || r.life == null) { Debug.Log("[TDU2] MyTDULife von " + p.Nick + ": " + (r?.error ?? "keine Antwort")); continue; }
                p.Life = r.life;
                p.Mood = r.life.mood ?? ""; p.MoodTxt = r.life.moodText ?? ""; p.PhotoVer = r.life.photoVer;
                if (p.Photo != null) { Destroy(p.Photo); p.Photo = null; }
                p.Photo = DecodePhoto(r.life.photo);
                p.PhotoTexVer = r.life.photoVer;
                foreach (var d in done) { try { d(p); } catch (Exception ex) { Debug.LogException(ex); } }
            }
        }

        /// <summary>Avatar-Foto (Base64-JPG) -> Textur; null bei leer/fehlerhaft.</summary>
        public static Texture2D DecodePhoto(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "MyTDULife Foto", wrapMode = TextureWrapMode.Clamp };
                if (tex.LoadImage(Convert.FromBase64String(b64))) return tex;
                Destroy(tex);
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Foto: " + ex.Message); }
            return null;
        }

        void Update()
        {
            PollLife();
            if (_manager == null)
            {
#if UNITY_2023_1_OR_NEWER
                _manager = FindFirstObjectByType<TDU2VehicleManager>();
#else
                _manager = FindObjectOfType<TDU2VehicleManager>();
#endif
            }
            PollStatus();
            UpdateGeneral();
            if (Time.unscaledTime >= _nextPush) { _nextPush = Time.unscaledTime + 0.5f; PushToFlash(); }
        }

        // ------------------------------------------------------------------ Spielerliste vom Server
        void PollStatus()
        {
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null)
            {
                if (Online) { Online = false; PlayerCount = 0; Changed?.Invoke(); }
                return;
            }
            if (_status == null && Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + Mathf.Max(1f, StatusSeconds);
                _status = TDU2Session.Client.Send(new NetRequest { op = "status", session = TDU2Session.SessionId });
            }
            if (_status == null || !_status.IsCompleted) return;
            var r = _status.Status == TaskStatus.RanToCompletion ? _status.Result : null;
            _status = null;
            bool ok = r != null && r.ok;
            if (ok && r.playerId == 0) { TDU2Session.SaveAsync(); ok = false; }   // Sitzung verloren (Server neu gestartet?) -> mit Token neu anmelden
            if (!ok) _failCount++; else _failCount = 0;
            bool online = ok || (_failCount < 2 && Online);   // einen Aussetzer tolerieren
            if (online && r != null && r.ok) { ApplyPlayers(r); TDU2Party.Ensure(gameObject).OnStatus(r); PlayerCount = IslandPlayerCount(r.online); TDU2Session.Online = r.online; TDU2Session.ApplyRadio(r); TDU2News.OnStatus(r.newsLatest); }
            if (online != Online)
            {
                Online = online;
                if (!online) { PlayerCount = 0; ShowDisconnected(); foreach (var p in Players.Values) { p.Online = false; Remember(p); } Players.Clear(); }
            }
            if (online) _wasOnline = true;
            Changed?.Invoke();
        }

        void ApplyPlayers(NetResponse r)
        {
            var db = _manager?.Database;
            var seen = new HashSet<int>();
            foreach (var e in r.players ?? new List<PlayerEntry>())
            {
                if (e.id == r.playerId || e.id == 0) continue;
                seen.Add(e.id);
                if (!Players.TryGetValue(e.id, out var p))
                {
                    p = new PlayerInfo { Id = e.id };
                    Players[e.id] = p;
                    if (!_firstList) PlayerEvent(e.nick, true);
                }
                else if (p.Nick.StartsWith("Spieler ") && !_firstList) PlayerEvent(e.nick, true);   // zuerst per UDP gesehen
                p.Nick = e.nick; p.Money = e.money; p.NbCars = e.nbCars; p.Online = true; p.Nationality = e.nationality;
                if (e.carId != 0) { p.CarId = e.carId; p.Color = e.color; }
                p.Island = e.island;
                p.RideWith = e.rideWith; p.Passenger = e.passenger;
                if (!string.IsNullOrEmpty(e.avatar)) p.Avatar = e.avatar;
                p.Mood = e.mood ?? ""; p.MoodTxt = e.moodTxt ?? ""; p.PhotoVer = e.photoVer;
                p.InCar = e.inCar && p.SameIsland;
                if (!p.SameIsland) p.HasPos = false;   // andere Insel: keine Position (Karte/Minimap)
                else if (!p.HasPos || Time.unscaledTime - p.LastUpdate > 3f) { p.GamePos = new Vector3(e.px, p.GamePos.y, e.pz); p.HasPos = e.inCar || p.HasPos; }
                p.Car = db != null && db.ById.TryGetValue(p.CarId, out var c) ? c : null;
                p.LastSeen = DateTime.Now;
                Remember(p);
            }
            var gone = new List<int>();
            foreach (var kv in Players) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                var p = Players[id];
                p.Online = false; p.LastSeen = DateTime.Now;
                Remember(p);
                Players.Remove(id);
                if (!p.Nick.StartsWith("Spieler ")) PlayerEvent(p.Nick, false);
            }
            if (gone.Count > 0 || _firstList) SaveRecent();
            _firstList = false;
        }

        /// <summary>Spielerzahl der eigenen Insel (inkl. einem selbst). Server ohne Inselangabe: alle Online-Spieler.</summary>
        int IslandPlayerCount(int serverOnline)
        {
            bool any = false; int n = 1;
            foreach (var p in Players.Values) { if (p.Island != 0) any = true; if (p.Online && p.SameIsland) n++; }
            return any ? n : Mathf.Max(1, serverOnline);
        }

        /// <summary>Spieler aus der Lobby (UDP) – Position/Auto aktualisieren; unbekannte Nummern bekommen einen Platzhalter.</summary>
        public PlayerInfo Player(int id)
        {
            if (Players.TryGetValue(id, out var p)) return p;
            p = new PlayerInfo { Id = id, Nick = "Spieler " + id, Online = true };
            Players[id] = p;
            return p;
        }

        // ------------------------------------------------------------------ Zuletzt gesehen (je Profil gespeichert)
        [Serializable] sealed class RecentEntry { public string nick = ""; public int carId; public int color; public long money; public int nbCars; public string seen = ""; }
        [Serializable] sealed class RecentList { public List<RecentEntry> list = new List<RecentEntry>(); }
        string _recentFor;

        string RecentKey => "TDU2.Recent." + (TDU2Session.Nick ?? "").ToLowerInvariant();

        void EnsureRecentLoaded()
        {
            string nick = TDU2Session.Nick ?? "";
            if (_recentFor == nick) return;
            _recentFor = nick;
            Recent.Clear();
            if (nick.Length == 0) return;
            try
            {
                var s = PlayerPrefs.GetString(RecentKey, "");
                var l = string.IsNullOrEmpty(s) ? null : JsonUtility.FromJson<RecentList>(s);
                if (l?.list != null)
                    foreach (var e in l.list)
                    {
                        DateTime.TryParse(e.seen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen);
                        Recent.Add(new PlayerInfo { Nick = e.nick, CarId = e.carId, Color = e.color, Money = e.money, NbCars = e.nbCars, LastSeen = seen });
                    }
            }
            catch (Exception) { }
        }

        void Remember(PlayerInfo p)
        {
            if (p == null || string.IsNullOrEmpty(p.Nick) || p.Nick.StartsWith("Spieler ")) return;
            EnsureRecentLoaded();
            int i = Recent.FindIndex(x => string.Equals(x.Nick, p.Nick, StringComparison.OrdinalIgnoreCase));
            var e = i >= 0 ? Recent[i] : new PlayerInfo { Nick = p.Nick };
            if (i >= 0) Recent.RemoveAt(i);
            e.CarId = p.CarId; e.Color = p.Color; e.Money = p.Money; e.NbCars = p.NbCars; e.Nationality = p.Nationality; e.LastSeen = DateTime.Now; e.Car = p.Car;
            Recent.Insert(0, e);
            if (Recent.Count > 50) Recent.RemoveRange(50, Recent.Count - 50);
        }

        void SaveRecent()
        {
            if (string.IsNullOrEmpty(TDU2Session.Nick)) return;
            try
            {
                var l = new RecentList();
                foreach (var p in Recent) l.list.Add(new RecentEntry { nick = p.Nick, carId = p.CarId, color = p.Color, money = p.Money, nbCars = p.NbCars, seen = p.LastSeen.ToString("o", CultureInfo.InvariantCulture) });
                PlayerPrefs.SetString(RecentKey, JsonUtility.ToJson(l));
            }
            catch (Exception) { }
        }

        /// <summary>Liste für das Pausemenü (Spielermenü): friends, club_members, current_players, recent_players, black_list.</summary>
        public List<PlayerInfo> ListFor(string listName)
        {
            switch ((listName ?? "").ToLowerInvariant())
            {
                case "current_players": return new List<PlayerInfo>(Players.Values);
                case "friends": return new List<PlayerInfo>(Friends);
                case "black_list": return new List<PlayerInfo>(Friends);
                case "recent_players":
                    {
                        EnsureRecentLoaded();
                        var db = _manager?.Database;
                        foreach (var p in Recent)
                        {
                            if (p.Car == null && db != null && db.ById.TryGetValue(p.CarId, out var c)) p.Car = c;
                            p.Online = false;
                            // gespeicherte Einträge haben keine Sitzungsnummer: online -> die aktuelle übernehmen (Einladen/Beitreten brauchen sie)
                            foreach (var o in Players.Values) if (string.Equals(o.Nick, p.Nick, StringComparison.OrdinalIgnoreCase)) { p.Online = true; p.Id = o.Id; }
                        }
                        return new List<PlayerInfo>(Recent);
                    }
                case "club_members": return new List<PlayerInfo>(Club);
            }
            return new List<PlayerInfo>();
        }

        void OnApplicationQuit() { SaveRecent(); }

        // ------------------------------------------------------------------ Daten in alle Flash-Filme schreiben
        readonly Dictionary<VmfPlayer, Dictionary<string, string>> _pushed = new Dictionary<VmfPlayer, Dictionary<string, string>>();

        void PushToFlash()
        {
            // Minimap (HUD) zeigt Symbol + Zahl über ihre eigenen Felder
            var mm = _manager != null ? _manager.Minimap : null;
            if (mm != null && TDU2Session.LoggedIn) { mm.OnlineStatus = Online; mm.PlayerCount = Mathf.Max(1, PlayerCount); }

            var vars = new Dictionary<string, string>
            {
                ["/:IsOnline"] = Online ? "1" : "0",
                ["/:NbPlayers"] = PlayerCount.ToString(),
                ["/:nb_players"] = PlayerCount.ToString(),
                ["/:nb_FRplayers"] = PlayerCount.ToString(),
            };
            if (TDU2Session.LoggedIn)
            {
                string money = Money.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " $";
                vars["/:PlayerName"] = PlayerName;
                vars["/:CurrentProfile"] = PlayerName;
                vars["/:Money"] = money;
                vars["/:money"] = money;
                vars["/:MyMoneyStr"] = money;
                vars["/:NbCars"] = NbCars.ToString();
            }
            string car = CarName;
            if (car.Length > 0) { vars["/:VehicleName"] = car; vars["/:player_car"] = car; }
            if (TDU2Session.LoggedIn) vars["/:AvatarName"] = PlayerName;
            // Zahlen (Skripte rechnen/vergleichen damit, gotoAndStop(_root.Nationality) braucht eine Zahl)
            var nums = new Dictionary<string, double>
            {
                ["/:state_online"] = Online ? 1 : 0,
                ["/:Nationality"] = NationalityId,
            };

#if UNITY_2023_1_OR_NEWER
            var films = FindObjectsByType<VmfPlayer>(FindObjectsSortMode.None);
#else
            var films = FindObjectsOfType<VmfPlayer>();
#endif
            var alive = new HashSet<VmfPlayer>(films);
            var dead = new List<VmfPlayer>();
            foreach (var k in _pushed.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (var k in dead) _pushed.Remove(k);

            foreach (var f in films)
            {
                if (f == null || f.Root == null) continue;
                if (!_pushed.TryGetValue(f, out var last)) { last = new Dictionary<string, string>(); _pushed[f] = last; }
                bool hud = mm != null && f.Find("/HUD/minimap") != null;   // Film der Minimap
                foreach (var kv in vars)
                {
                    if (hud && (kv.Key == "/:IsOnline" || kv.Key == "/:NbPlayers" || kv.Key == "/:nb_players" || kv.Key == "/:nb_FRplayers")) continue;   // macht die Minimap
                    if (last.TryGetValue(kv.Key, out var v) && v == kv.Value) continue;   // nur bei Änderung (Flash Debugger bleibt nutzbar)
                    f.SetVariable(kv.Key, kv.Value);
                    last[kv.Key] = kv.Value;
                }
                foreach (var kv in nums)
                {
                    string sv = "#" + kv.Value.ToString(CultureInfo.InvariantCulture);
                    if (last.TryGetValue(kv.Key, out var v) && v == sv) continue;
                    f.SetNumber(kv.Key, kv.Value);
                    last[kv.Key] = sv;
                }
                ApplyStatusIcons(f, f.Root, "");
            }
        }

        /// <summary>Jeder Clip "ic_status" mit connected/disconnected bzw. online/offline zeigt den eigenen Online-Status.</summary>
        void ApplyStatusIcons(VmfPlayer f, VmfPlayer.Instance inst, string path)
        {
            if (inst?.List == null) return;
            foreach (var d in inst.List.Values)
            {
                if (d.Sprite == null) continue;
                string p = path + "/" + (d.Name ?? "#" + d.Depth);
                if (d.Name == "player_card" || d.Name == "online_evt" || d.Name == "players_list" || d.Name == "minimap") continue;   // Status anderer Spieler / Minimap selbst
                if (d.Name == "ic_status")
                {
                    var lb = d.Sprite.Timeline?.Labels;
                    string want = null;
                    if (lb != null)
                    {
                        if (lb.ContainsKey("connected")) want = Online ? "connected" : "disconnected";
                        else if (lb.ContainsKey("online")) want = Online ? "online" : "offline";
                    }
                    if (want != null && lb.TryGetValue(want, out int fr) && d.Sprite.Frame != fr) f.GotoLabel(VmfPlayer.PathOf(d.Sprite), want, false);
                    continue;
                }
                ApplyStatusIcons(f, d.Sprite, p);
            }
        }

        // ------------------------------------------------------------------ general.bnk: Verbindung verloren / Spieler online / Speichern
        VmfPlayer _general;
        /// <summary>Globales Overlay general.bnk (Chat, Online-Meldungen, Spiel-Einladung pop_gameInvite); null = nicht geladen.</summary>
        public VmfPlayer General => _general;
        bool _generalTried;
        float _evtCloseAt = -1f, _saveCloseAt = -1f;
        bool _decoOpen;
        readonly Queue<(string nick, bool on)> _events = new Queue<(string, bool)>();

        void UpdateGeneral()
        {
            if (!UseGeneralOverlay) return;
            if (_general == null && !_generalTried && _manager != null && !string.IsNullOrEmpty(_manager.BnkDir))
            {
                _generalTried = true;
                string bank = null;
                foreach (var res in new[] { "hires", "medres", "lowres" })
                {
                    bank = CarDatabase.FindFile(Path.Combine(_manager.BnkDir, "frontend", res), "general.bnk");
                    if (bank != null) break;
                }
                if (bank != null)
                {
                    try
                    {
                        var go = new GameObject("TDU2 General (general.bnk)");
                        go.transform.SetParent(transform, false);
                        _general = go.AddComponent<VmfPlayer>();
                        _general.GuiDepth = -30;
                        _general.SetValue("/:WhatPlatform", 1);
                        _general.Load(bank);
                        _general.GotoLabel("/", "default", false);
                        // nur die benutzten Overlays sichtbar lassen
                        foreach (var d in _general.Root.List.Values) d.Visible = d.Name == "deco_pop" || d.Name == "online_evt" || d.Name == "saving_logo" || d.Name == "pop_chat";
                        _general.GotoLabel("/pop_chat", "default", false);
                        for (int i = 1; i <= ChatLines; i++) _general.GotoLabel("/pop_chat/log/line" + i, "default", false);
                        var pm = _manager.GetComponent<TDU2PauseMenu>();
                        if (pm?.Texts != null) FlashLocalization.Apply(_general, pm.Texts);
                        else _generalTexts = false;
                    }
                    catch (Exception ex) { Debug.LogWarning("[TDU2] general.bnk: " + ex.Message); _general = null; }
                }
            }
            if (_general == null) return;
            if (!_generalTexts)
            {
                var pm = _manager != null ? _manager.GetComponent<TDU2PauseMenu>() : null;
                if (pm?.Texts != null) { FlashLocalization.Apply(_general, pm.Texts); _generalTexts = true; }
            }

            // Spieler online/offline
            if (_evtCloseAt > 0f && Time.unscaledTime >= _evtCloseAt)
            {
                _evtCloseAt = -1f;
                _general.GotoLabel("/online_evt", "close", true);
                _evtFreeAt = Time.unscaledTime + 0.6f;
            }
            if (_evtCloseAt < 0f && Time.unscaledTime >= _evtFreeAt && _events.Count > 0)
            {
                var (nick, on) = _events.Dequeue();
                _general.SetVariable("/:OEType", on ? "player_on" : "player_off");
                _general.SetVariable("/:OEPlayerName", nick);
                _general.GotoLabel("/online_evt", "open", true);
                _evtCloseAt = Time.unscaledTime + 3.5f;
            }

            // Speichern: Symbol ab dem Start des Speicherns, mindestens SavingLogoMinSeconds (sonst nur ein Aufblitzen)
            if (_saveRequested)
            {
                _saveRequested = false;
                if (_saveCloseAt < 0f) _general.GotoLabel("/saving_logo", "open", true);
                _saveCloseAt = Time.unscaledTime + Mathf.Max(0.5f, SavingLogoMinSeconds);
            }
            if (_saveCloseAt > 0f && Time.unscaledTime >= _saveCloseAt && !TDU2Session.Saving) { _saveCloseAt = -1f; _general.GotoLabel("/saving_logo", "close", true); }

            UpdateChat();

            // Verbindung verloren: Enter bestätigt (validDeco)
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_decoOpen && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape)))
            {
                _decoOpen = false;
                if (!_general.CallClip("/deco_pop", "validDeco")) _general.GotoLabel("/deco_pop", "close", true);
            }
#endif
            if (_decoOpen && Online) { _decoOpen = false; _general.GotoLabel("/deco_pop", "close", true); }
        }

        bool _generalTexts = true;
        volatile bool _saveRequested;
        void OnEnable() { TDU2Session.SaveStarted += OnSaveStarted; }
        void OnDisable() { TDU2Session.SaveStarted -= OnSaveStarted; TDU2Controls.Blocked = false; }
        void OnSaveStarted() { _saveRequested = true; }

        // ------------------------------------------------------------------ Chat (general.bnk /pop_chat)
        // Öffnen: Label "open" (Bild 3 setzt _root.TextToFocus = "/:TxtInput" + "tfoc"), Schließen: "close" (Bild 4 meldet "chva").
        // Verlauf: /pop_chat/log/line1..4 (line4 unten = neueste), Text in lineN/color:txt, Farbe = Bild von lineN/color,
        // Zeile ein-/ausblenden über die Labels open/close der Zeile. Position des Verlaufs: log-Labels ingame/menus/walk_mode.
        const int ChatLines = 4;
        public readonly List<(DateTime time, string nick, string text)> ChatLog = new List<(DateTime, string, string)>();
        readonly (string text, int color, float until)[] _chatLine = new (string, int, float)[ChatLines];
        bool _chatOpen;
        string _chatInput = "";
        int _chatBlockFrame = -1;
        TDU2Multiplayer _mp;
        string _logMode;

        /// <summary>true, solange der Chat die Tastatur hat (andere Komponenten ignorieren dann Esc/Enter usw.).</summary>
        /// <summary>Ein Film-Textfeld hat die Tastatur (z.B. MyTDULife-Stimmungstext): der Chat öffnet sich dann nicht.</summary>
        public static bool ExternalTextInput;
        public static bool ChatTyping => Instance != null && (Instance._chatOpen || Time.frameCount <= Instance._chatBlockFrame);

        void UpdateChat()
        {
            if (_mp == null && _manager != null) { _mp = _manager.GetComponent<TDU2Multiplayer>(); if (_mp != null) _mp.ChatReceived += OnChat; }

            // Position des Verlaufs je nach Lage
            var pm = _manager != null ? _manager.GetComponent<TDU2PauseMenu>() : null;
            var inter = _manager != null ? _manager.GetComponent<TDU2Interiors>() : null;
            string mode = pm != null && pm.IsOpen ? "menus" : inter != null && inter.Inside ? "walk_mode" : "ingame";
            if (mode != _logMode) { _logMode = mode; _general.GotoLabel("/pop_chat/log", mode, false); }

            // Zeilen ausblenden, wenn abgelaufen
            for (int i = 0; i < ChatLines; i++)
                if (_chatLine[i].text != null && Time.unscaledTime >= _chatLine[i].until)
                {
                    _chatLine[i].text = null;
                    _general.GotoLabel("/pop_chat/log/line" + (i + 1), "close", true);
                }

#if ENABLE_LEGACY_INPUT_MANAGER
            if (!_chatOpen)
            {
                bool free = EnableChat && !ExternalTextInput && TDU2Session.LoggedIn && !TDU2Login.Active && !TDU2BigMap.IsOpen && (pm == null || !pm.IsOpen) && Time.frameCount > _chatBlockFrame;
                if (free && TDU2Controls.Pressed("chat")) OpenChat();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { CloseChat(false); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { CloseChat(true); return; }
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b') { if (_chatInput.Length > 0) _chatInput = _chatInput.Substring(0, _chatInput.Length - 1); }
                else if (ch == '\n' || ch == '\r' || ch == '\t') continue;
                else if (!char.IsControl(ch) && _chatInput.Length < Mathf.Max(10, ChatMaxLength)) _chatInput += ch;
            }
            bool blink = (Time.unscaledTime % 1f) < 0.55f;
            _general.SetVariable("/:TxtInput", _chatInput + (blink ? "_" : ""));
#endif
        }

        void OpenChat()
        {
            _chatOpen = true;
            _chatInput = "";
            _chatBlockFrame = Time.frameCount + 1;   // das 'T' selbst nicht als Text übernehmen
            TDU2Controls.Blocked = true;
            _general.SetVariable("/:TxtInput", "");
            _general.SetVariable("/:m_Chat", "Chat:    ");
            _general.GotoLabel("/pop_chat", "open", true);
        }

        void CloseChat(bool send)
        {
            _chatOpen = false;
            _chatBlockFrame = Time.frameCount + 1;   // Esc/Enter nicht noch ans Pausemenü/Interieur weitergeben
            TDU2Controls.Blocked = false;
            string text = _chatInput.Trim();
            _chatInput = "";
            _general.SetVariable("/:TxtInput", "");
            //_general.SetVariable("/:m_Chat", "");
            _general.GotoLabel("/pop_chat", "close", true);
            if (send && text.Length > 0)
            {
                if (_mp == null || !_mp.SendChat(text)) AddChatLine(TDU2Session.Nick ?? "", text, 0);   // ohne Verbindung wenigstens lokal zeigen
            }
        }

        void OnChat(int pid, string nick, string text)
        {
            int color = pid == TDU2Session.PlayerId ? 0 : 1 + pid % 15;
            AddChatLine(nick, text, color);
        }

        /// <summary>Neue Zeile unten (line4), ältere rutschen nach oben.</summary>
        public void AddChatLine(string nick, string text, int color)
        {
            ChatLog.Add((DateTime.Now, nick, text));
            if (ChatLog.Count > 200) ChatLog.RemoveAt(0);
            if (_general == null) return;
            for (int i = 0; i < ChatLines - 1; i++) _chatLine[i] = _chatLine[i + 1];
            _chatLine[ChatLines - 1] = (nick + ": " + text, color, Time.unscaledTime + Mathf.Max(3f, ChatLineSeconds));
            for (int i = 0; i < ChatLines; i++)
            {
                string line = "/pop_chat/log/line" + (i + 1);
                var l = _chatLine[i];
                if (l.text == null) { _general.GotoLabel(line, "default", false); continue; }
                _general.SetVariable(line + "/color:txt", l.text);
                _general.GotoFrameNumber(line + "/color", Mathf.Clamp(l.color, 0, 15));
                if (i == ChatLines - 1) _general.GotoLabel(line, "open", true);   // neue Zeile blendet ein
                else { var d = _general.Find(line); if (d?.Sprite != null && d.Sprite.Frame < 10) _general.GotoFrameNumber(line, 10); }   // sichtbar stehen lassen
            }
        }
        float _evtFreeAt;

        void PlayerEvent(string nick, bool online)
        {
            if (!ShowPlayerEvents || string.IsNullOrEmpty(nick)) return;
            if (_events.Count < 8) _events.Enqueue((nick, online));
        }

        void ShowDisconnected()
        {
            if (!_wasOnline || _general == null) return;
            _decoOpen = true;
            _general.GotoLabel("/deco_pop", "open", true);
        }
    }
}
