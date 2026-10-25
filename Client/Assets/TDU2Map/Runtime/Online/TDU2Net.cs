using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TDU2.Online
{
    // ------------------------------------------------------------------ Datenklassen (Feldnamen = JSON-Namen, identisch im Server TDU2Server/Program.cs)
    [Serializable]
    public sealed class OdoEntry
    {
        public int carId;
        public double meters;
    }

    /// <summary>Spielstand – liegt auf dem Server (data/accounts/&lt;nick&gt;.json).</summary>
    [Serializable]
    public sealed class SaveData
    {
        public int version = 1;
        public string island = "";
        public bool hasPosition;
        public float px, py, pz, heading;   // Spielkoordinaten (Insel), Richtung in Grad um Y
        public string currentCar = "";
        public List<string> ownedCars = new List<string>();
        public long money;
        public bool starterChosen;
        public List<OdoEntry> odometers = new List<OdoEntry>();
        /// <summary>Lackierung/Beklebung je Auto (Sticker-Shop): Base64 von <see cref="TDU2.Map.Vehicles.CarLivery.Encode"/>.</summary>
        public List<LiveryEntry> liveries = new List<LiveryEntry>();
        /// <summary>Gekaufte Sticker-Planchen (DBStickersBanks._hashcode).</summary>
        public List<int> stickerBoards = new List<int>();
        /// <summary>"Meine Farben" des Sticker-Shops (6 Plätze, 0xRRGGBB, -1 = leer).</summary>
        public List<int> favoriteColors = new List<int>();
        /// <summary>Aussehen des Avatars (AvatarConfig.Encode: Geschlecht, Herkunft, Kleidung mit Farben).</summary>
        public string avatar = "";
        /// <summary>Gekaufte Kleidungsstücke (DBClothe._hashcode).</summary>
        public List<int> ownedClothes = new List<int>();
        /// <summary>Lesezeichen der großen Karte (je Insel, Spielkoordinaten).</summary>
        public List<BookmarkEntry> bookmarks = new List<BookmarkEntry>();
        /// <summary>Als Favorit markierte Spots der großen Karte (Spot-Code).</summary>
        public List<string> favoriteSpots = new List<string>();
        /// <summary>My House: Einrichtung je Haus (Spot-Code) – Möbel-Bank je Entity, Material je Fläche.</summary>
        public List<HouseLayout> houses = new List<HouseLayout>();
        /// <summary>Gekaufte Möbel (DBFurniture._hashcode) und Böden/Tapeten (DBMaterial._hashcode).</summary>
        public List<int> ownedFurniture = new List<int>();
        public List<int> ownedMaterials = new List<int>();
        /// <summary>MyTDULife (Tagebuch "My Life"): Stimmung, Profil, Kästen, Avatar-Foto.</summary>
        public MyLifeData myLife = new MyLifeData();
        /// <summary>Zuletzt betretenes Interieur ("SPOT|bank", leer = draußen) – beim Anmelden geht es dort weiter.</summary>
        public string interior = "";
        /// <summary>Ausstattung je Auto (Autohändler: Felgen, Lack, Innenraum).</summary>
        public List<CarConfigEntry> carConfigs = new List<CarConfigEntry>();
        public string savedAt = "";
    }

    /// <summary>Ausstattung eines Autos: Lack = Index in DBCarsAndColors des Autos, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode (0 = Standard).</summary>
    [Serializable]
    public sealed class CarConfigEntry
    {
        public string car = "";
        public int color;
        public int rim;
        public int interior;
    }

    /// <summary>MyTDULife – andere Spieler lesen es über op "profile" (Foto) bzw. die Spielerliste (Stimmung).</summary>
    [Serializable]
    public sealed class MyLifeData
    {
        public string mood = "neutral";                              // Smiley-Label (neutral, big_smile, tongue, wink, sad …)
        public string moodText = "";                                 // Stimmungstext
        public List<string> boxes = new List<string>();              // Kästen ed1..ed7
        public List<HouseEntry> fields = new List<HouseEntry>();     // Profilangaben: Liste -> Index ("attitude" -> "2")
        public int birthDay, birthMonth, birthYear;
        public string gender = "";
        public string photo = "";                                    // Avatar-Foto (JPG, Base64)
        public int photoVer;
    }

    [Serializable]
    public sealed class HouseLayout
    {
        public string spot = "";
        public List<HouseEntry> furn = new List<HouseEntry>();   // Entity-Name -> Bank (interior/myhouse)
        public List<HouseEntry> mats = new List<HouseEntry>();   // Fläche ("Floor1", "Wall2") -> Material (ground_NN / wall_NN)
    }

    [Serializable]
    public sealed class HouseEntry
    {
        public string k = "";
        public string v = "";
    }

    [Serializable]
    public sealed class BookmarkEntry
    {
        public int id;
        public string island = "";
        public float x, y, z;
    }

    [Serializable]
    public sealed class LiveryEntry
    {
        public string car = "";
        public string data = "";
    }

    [Serializable]
    public sealed class NetRequest
    {
        public string op = "";
        public string nick = "";
        public string email = "";
        public string password = "";
        public string token = "";
        public string session = "";
        public int version = 1;
        public int nationality;
        public SaveData save;
        public int target;          // invite/invreply: Spielernummer
        public bool accept;         // invreply
        public string kind = "";    // invite: "codriving"
    }

    /// <summary>Offene Einladung (op "status": invites).</summary>
    [Serializable]
    public sealed class InviteEntry
    {
        public int from;
        public string nick = "";
        public string kind = "";
    }

    /// <summary>Eintrag der Spielerliste des Servers (op "status").</summary>
    [Serializable]
    public sealed class PlayerEntry
    {
        public int id;
        public string nick = "";
        public int carId;
        public int color;
        public long money;
        public int nbCars;
        public bool inCar;
        public float px, pz;
        public int nationality;
        public int island;          // 1 Ibiza, 2 Hawaii, 0 unbekannt (Server ohne Inseltrennung)
        public string avatar = "";  // Aussehen (AvatarConfig.Encode) – Mitspieler im Auto mit ihrer Kleidung
        public string mood = "";    // MyTDULife: Smiley, Stimmungstext, Foto-Version (Foto über op "profile")
        public string moodTxt = "";
        public int photoVer;
        public int rideWith;        // fährt als Beifahrer bei Spieler … mit (0 = nein)
        public int passenger;       // hat Spieler … als Beifahrer (0 = nein)
    }

    [Serializable]
    public sealed class NetResponse
    {
        public bool ok;
        public string error = "";
        public string nick = "";
        public string token = "";
        public string session = "";
        public SaveData save;
        public List<string> starterCars = new List<string>();
        public string startSpot = "";
        public string startIsland = "";
        public long startMoney;
        public int online;
        public int udpPort;
        public string motd = "";
        public int playerId;
        public long udpKey;
        public List<PlayerEntry> players = new List<PlayerEntry>();
        /// <summary>Radio-Zeitbasis vom Server: Epoche (Serverstart, UTC-Millisekunden), Startwert und aktuelle Serverzeit.</summary>
        public long radioEpoch;
        public int radioSeed;
        public long serverTime;
        /// <summary>status: offene Einladungen an mich, Meldungen ("accepted:id:nick", "declined:…", "rideend:…"), Mitfahrt.</summary>
        public List<InviteEntry> invites = new List<InviteEntry>();
        public List<string> notices = new List<string>();
        public string inviteKind = "";   // invreply: Art der angenommenen Einladung ("game|…", "room|SPOT|Bank|Ort")
        public int rideWith;
        public int passenger;
        /// <summary>profile: MyTDULife des angefragten Spielers (mit Foto).</summary>
        public MyLifeData life;
        public int lifeOf;

        public static NetResponse Error(string e) => new NetResponse { ok = false, error = e };
    }

    /// <summary>
    /// TCP-Verbindung zum TDU2Server: eine JSON-Zeile je Anfrage/Antwort. Anfragen laufen nacheinander im Hintergrund
    /// (Send liefert einen Task); bei einem Verbindungsfehler wird beim nächsten Send neu verbunden (die Server-Sitzung
    /// ist dann weg – <see cref="TDU2Session"/> meldet sich mit dem Merk-Token neu an).
    /// </summary>
    public sealed class TDU2NetClient : IDisposable
    {
        public readonly string Host;
        public readonly int Port;
        public int TimeoutMs = 8000;
        readonly object _lock = new object();
        TcpClient _tcp;
        StreamReader _reader;
        StreamWriter _writer;
        int _generation;

        public TDU2NetClient(string host, int port) { Host = host; Port = port; }

        public bool Connected { get { lock (_lock) return _tcp != null && _tcp.Connected; } }
        /// <summary>Zählt jede neue Verbindung (Sitzung des Servers hängt an der Verbindung).</summary>
        public int Generation => _generation;

        public Task<NetResponse> Send(NetRequest req) => Task.Run(() => SendBlocking(req));

        public NetResponse SendBlocking(NetRequest req)
        {
            lock (_lock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureConnected();
                        _writer.Write(JsonUtility.ToJson(req));
                        _writer.Write('\n');
                        _writer.Flush();
                        string line = _reader.ReadLine();
                        if (line == null) throw new IOException("Verbindung vom Server getrennt");
                        var resp = JsonUtility.FromJson<NetResponse>(line) ?? NetResponse.Error("bad_response");
                        return resp;
                    }
                    catch (Exception ex)
                    {
                        CloseLocked();
                        // ein zweiter Versuch nur, wenn eine alte Verbindung kaputt war
                        if (attempt == 1 || !(ex is IOException || ex is SocketException || ex is ObjectDisposedException))
                            return NetResponse.Error("net:" + ex.Message);
                    }
                }
                return NetResponse.Error("net");
            }
        }

        bool EnsureConnected()
        {
            if (_tcp != null && _tcp.Connected) return false;
            CloseLocked();
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs };
            var t = tcp.ConnectAsync(Host, Port);
            if (!t.Wait(4000) || !tcp.Connected) { try { tcp.Close(); } catch (Exception) { } throw new SocketException((int)SocketError.TimedOut); }
            var s = tcp.GetStream();
            _tcp = tcp;
            _reader = new StreamReader(s, new UTF8Encoding(false));
            _writer = new StreamWriter(s, new UTF8Encoding(false));
            _generation++;
            return true;
        }

        void CloseLocked()
        {
            try { _reader?.Dispose(); } catch (Exception) { }
            try { _writer?.Dispose(); } catch (Exception) { }
            try { _tcp?.Close(); } catch (Exception) { }
            _reader = null; _writer = null; _tcp = null;
        }

        public void Dispose() { lock (_lock) CloseLocked(); }
    }

    /// <summary>
    /// Angemeldeter Spieler: Konto, Server-Sitzung und Spielstand. Wird vom Login (TDU2Login) gesetzt und von
    /// Händlern/Garage (eigene Autos), Pausemenü (Geld, Profilname) und dem Autosave benutzt.
    /// </summary>
    public static class TDU2Session
    {
        public static TDU2NetClient Client { get; private set; }
        public static string Nick { get; private set; }
        public static string Token { get; private set; }
        public static string SessionId { get; private set; }
        public static SaveData Save { get; private set; }
        /// <summary>Antwort des Servers beim Anmelden (Startautos, Start-Spot, Startgeld, MOTD).</summary>
        public static NetResponse Config { get; private set; }
        public static int Online { get; set; }
        /// <summary>Spieler-Nummer und Schlüssel für die UDP-Synchronisation (vom Server beim Anmelden).</summary>
        public static int PlayerId { get; private set; }
        public static uint UdpKey { get; private set; }
        public static int UdpPort { get; private set; }
        public static bool LoggedIn => Save != null && !string.IsNullOrEmpty(SessionId);
        public static event Action Changed;

        // ---- Radio-Zeitbasis (vom Server): gemeinsame Playlist aller Clients (Hariba/RoadRock)
        /// <summary>Nullpunkt der Radio-Zeitleiste (Serverstart, UTC-Millisekunden). 0 = unbekannt.</summary>
        public static long RadioEpochMs { get; private set; }
        /// <summary>Gemeinsamer Zufallsstartwert für die deterministische Radio-Playlist.</summary>
        public static int RadioSeed { get; private set; }
        static long _radioSkewMs;   // Serverzeit − lokale Uhr
        /// <summary>Ist die Radio-Zeitbasis bekannt? (nur dann lässt sich der Sender synchron abspielen)</summary>
        public static bool HasRadioClock => RadioEpochMs != 0;
        /// <summary>Sekunden seit dem Radio-Nullpunkt auf der Serveruhr – für alle Clients identisch.</summary>
        public static double RadioElapsedSeconds =>
            ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _radioSkewMs) - RadioEpochMs) / 1000.0;

        /// <summary>Serveruhr in Sekunden (UTC, um die gemessene Abweichung korrigiert) – gemeinsame Zeitbasis z.B. für die Ampeln.</summary>
        public static double ServerSeconds => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _radioSkewMs) / 1000.0;

        /// <summary>Radio-Zeitbasis aus einer Server-Antwort übernehmen (login/register/status).</summary>
        public static void ApplyRadio(NetResponse r)
        {
            if (r == null) return;
            if (r.radioEpoch != 0) { RadioEpochMs = r.radioEpoch; RadioSeed = r.radioSeed; }
            if (r.serverTime != 0) _radioSkewMs = r.serverTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static void Connect(string host, int port)
        {
            if (Client != null && Client.Host == host && Client.Port == port) return;
            Client?.Dispose();
            DebugManager.Info($"TDU2Session.Connect({host}:{port})");
            Client = new TDU2NetClient(host, port);
        }

        public static void Begin(NetResponse r)
        {
            Nick = r.nick; Token = r.token; SessionId = r.session;
            Save = r.save ?? new SaveData();
            if (Save.ownedCars == null) Save.ownedCars = new List<string>();
            if (Save.odometers == null) Save.odometers = new List<OdoEntry>();
            if (Save.liveries == null) Save.liveries = new List<LiveryEntry>();
            if (Save.stickerBoards == null) Save.stickerBoards = new List<int>();
            if (Save.favoriteColors == null) Save.favoriteColors = new List<int>();
            if (Save.bookmarks == null) Save.bookmarks = new List<BookmarkEntry>();
            if (Save.favoriteSpots == null) Save.favoriteSpots = new List<string>();
            if (Save.houses == null) Save.houses = new List<HouseLayout>();
            if (Save.ownedFurniture == null) Save.ownedFurniture = new List<int>();
            if (Save.ownedMaterials == null) Save.ownedMaterials = new List<int>();
            if (Save.myLife == null) Save.myLife = new MyLifeData();
            if (Save.carConfigs == null) Save.carConfigs = new List<CarConfigEntry>();
            if (Save.myLife.boxes == null) Save.myLife.boxes = new List<string>();
            if (Save.myLife.fields == null) Save.myLife.fields = new List<HouseEntry>();
            Config = r;
            Online = r.online;
            PlayerId = r.playerId; UdpKey = (uint)r.udpKey; UdpPort = r.udpPort;
            ApplyRadio(r);
            Changed?.Invoke();
        }

        public static void End()
        {
            Nick = null; Token = null; SessionId = null; Save = null; PlayerId = 0; UdpKey = 0;
            RadioEpochMs = 0; RadioSeed = 0; _radioSkewMs = 0;
            Changed?.Invoke();
        }

        // ---- eigene Autos
        public static IReadOnlyList<string> OwnedCars => Save != null ? (IReadOnlyList<string>)Save.ownedCars : Array.Empty<string>();

        public static void AddOwned(string carFile)
        {
            if (Save == null || string.IsNullOrEmpty(carFile)) return;
            if (!Save.ownedCars.Exists(c => string.Equals(c, carFile, StringComparison.OrdinalIgnoreCase))) Save.ownedCars.Add(carFile);
            Changed?.Invoke();
        }

        public static void SetCurrentCar(string carFile)
        {
            if (Save == null || string.IsNullOrEmpty(carFile)) return;
            Save.currentCar = carFile;
        }

        // ---- Speichern
        static Task<NetResponse> _saving;
        /// <summary>Speichern beginnt (für das Speicher-Symbol saving_logo).</summary>
        public static event Action SaveStarted;
        public static bool Saving => _saving != null && !_saving.IsCompleted;

        /// <summary>Spielstand an den Server schicken (im Hintergrund). Ist die Sitzung weg (Verbindungsabbruch), erst mit dem Token neu anmelden.</summary>
        public static Task<NetResponse> SaveAsync()
        {
            if (!LoggedIn || Client == null) return Task.FromResult(NetResponse.Error("not_logged_in"));
            if (Saving) return _saving;
            string json = JsonUtility.ToJson(Save);   // Kopie im Hauptthread ziehen
            var copy = JsonUtility.FromJson<SaveData>(json);
            var client = Client; string nick = Nick, token = Token, session = SessionId;
            SaveStarted?.Invoke();
            _saving = Task.Run(() =>
            {
                var r = client.SendBlocking(new NetRequest { op = "save", session = session, save = copy });
                if (!r.ok && (r.error == "not_logged_in" || r.error.StartsWith("net")))
                {
                    var l = client.SendBlocking(new NetRequest { op = "login", nick = nick, token = token });
                    if (!l.ok) return l;
                    SessionId = l.session;
                    PlayerId = l.playerId; UdpKey = (uint)l.udpKey;
                    r = client.SendBlocking(new NetRequest { op = "save", session = l.session, save = copy });
                }
                return r;
            });
            return _saving;
        }

        public static void Logout()
        {
            if (Client == null || string.IsNullOrEmpty(SessionId)) return;
            try { Client.SendBlocking(new NetRequest { op = "logout", session = SessionId }); } catch (Exception) { }
        }

        // ---- lokale Profile (Merk-Token je Konto, wie die Profilliste im Spiel)
        [Serializable] public sealed class LocalProfile { public string nick = ""; public string token = ""; }
        [Serializable] sealed class ProfileList { public List<LocalProfile> list = new List<LocalProfile>(); public string last = ""; }
        const string ProfilesKey = "TDU2.Profiles";

        public static List<LocalProfile> LoadProfiles(out string last)
        {
            last = "";
            try
            {
                var s = PlayerPrefs.GetString(ProfilesKey, "");
                if (!string.IsNullOrEmpty(s))
                {
                    var p = JsonUtility.FromJson<ProfileList>(s);
                    if (p != null && p.list != null) { last = p.last ?? ""; return p.list; }
                }
            }
            catch (Exception) { }
            return new List<LocalProfile>();
        }

        public static void StoreProfiles(List<LocalProfile> list, string last)
        {
            try
            {
                PlayerPrefs.SetString(ProfilesKey, JsonUtility.ToJson(new ProfileList { list = list, last = last ?? "" }));
                PlayerPrefs.Save();
            }
            catch (Exception) { }
        }

        /// <summary>Fehlercodes des Servers als Text für die Original-Oberfläche.</summary>
        public static string ErrorText(string error, bool german)
        {
            if (string.IsNullOrEmpty(error)) return "";
            if (error.StartsWith("net")) return german ? "Server nicht erreichbar" : "Server not reachable";
            switch (error)
            {
                case "invalid_nick": return german ? "Name: 3-16 Zeichen (A-Z, 0-9, _ - .)" : "Name: 3-16 characters (A-Z, 0-9, _ - .)";
                case "weak_password": return german ? "Passwort: mindestens 4 Zeichen" : "Password: at least 4 characters";
                case "nick_taken": return german ? "Dieser Name ist schon vergeben" : "This name is already taken";
                case "unknown_nick": return german ? "Konto nicht gefunden" : "Account not found";
                case "bad_password": return german ? "Falsches Passwort" : "Wrong password";
                case "bad_token": return german ? "Bitte Passwort eingeben" : "Please enter your password";
                case "server_full": return german ? "Server voll" : "Server full";
            }
            return error;
        }
    }
}