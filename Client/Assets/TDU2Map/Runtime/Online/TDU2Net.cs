using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TDU2.Online
{
    // Datenklassen der Leitung (Feldnamen = JSON-Namen, identisch im Server): je Klasse eine Datei unter Online/Protocol/

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
        /// <summary>Kopie des Spielstands, die gerade gespeichert wird (Hauptthread; TDU2SaveGame schreibt sie zusätzlich als Original-DATA).</summary>
        public static event Action<string, SaveData> SaveCopied;

        /// <summary>Spielstand an den Server schicken (im Hintergrund). Ist die Sitzung weg (Verbindungsabbruch), erst mit dem Token neu anmelden.</summary>
        public static Task<NetResponse> SaveAsync()
        {
            if (!LoggedIn || Client == null) return Task.FromResult(NetResponse.Error("not_logged_in"));
            if (Saving) return _saving;
            int step = Story.TDU2StoryProgress.Get(Nick);
            if (step >= 0) Save.story = step;
            string json = JsonUtility.ToJson(Save);   // Kopie im Hauptthread ziehen
            var copy = JsonUtility.FromJson<SaveData>(json);
            var client = Client; string nick = Nick, token = Token, session = SessionId;
            try { SaveCopied?.Invoke(nick, JsonUtility.FromJson<SaveData>(json)); }
            catch (Exception e) { Debug.LogWarning("Savegame: " + e.Message); }
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