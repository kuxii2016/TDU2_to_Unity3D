// TDU2Server – kleiner Account-/Spielstand-Server für TDU2MapUnity
//
//   dotnet run                      startet mit config.json (wird beim ersten Start angelegt)
//
// Protokoll (TCP, Standard-Port 7777): eine JSON-Zeile (UTF-8, '\n') je Anfrage, eine JSON-Zeile je Antwort.
//   {"op":"hello"}                                         -> Startkonfiguration (Startautos, Start-Spot, Startgeld) + Spielerzahl
//   {"op":"register","nick":..,"password":..,"email":..}   -> neues Konto, gleich angemeldet (token, session, save)
//   {"op":"login","nick":..,"password":..}                 -> anmelden mit Passwort (liefert neuen Merk-Token)
//   {"op":"login","nick":..,"token":..}                    -> anmelden mit gemerktem Token (Profil auf dem PC)
//   {"op":"save","session":..,"save":{..}}                 -> Spielstand speichern
//   {"op":"status","session":..}                           -> Spielerzahl online
//   {"op":"logout","session":..}                           -> abmelden
//   {"op":"forget","nick":..,"token":..}                   -> Merk-Token löschen (Profil vom PC entfernt)
// Antwort: {"ok":true|false,"error":"..", ...}
//
// Radio-Synchronisation: hello/login/register/status liefern radioEpoch (Serverstart, Unix-Millisekunden),
//   radioSeed (gemeinsamer Zufallsstartwert) und serverTime (aktuelle Serverzeit). Jeder Client berechnet daraus
//   dieselbe Radio-Playlist und denselben Wiedergabezeitpunkt (nur Originalsender Hariba/RoadRock).
//
// Daten: <DataDir>/accounts/<nick>.json (Passwort als PBKDF2-SHA256, Tokens nur als SHA256-Hash).
// UDP (udpPort, Standard 7778): Spielersynchronisation. Jeder angemeldete Spieler hat playerId + udpKey (aus login/register).
//   Client -> Server: [1][u16 playerId][u32 udpKey][Zustand]     (~20/s: Position, Richtung, Auto, Farbe, Lichter …)
//   Server -> Clients: [2][u16 playerId][Zustand]                 (an alle anderen Spieler weitergeleitet)
//   Zustand: +0 u32 seq, +4 i32 carId, +8 u8 Farbe, +9 u8 Flags, +10 u8 Licht, +11 u8 frei, +12.. Floats (siehe Client TDU2Multiplayer)
//   Verkehr: [5][u16 playerId][u32 udpKey][Daten] -> [6][u16 playerId][Daten] an alle anderen (Format: Client TDU2Traffic)
//   Insel: Zustand +80 u8 (1 = Ibiza, 2 = Hawaii, 0 = unbekannt/alter Client). Zustände und Verkehr gehen nur an Spieler
//   derselben Insel (unbekannt = an alle); "status" liefert je Spieler "island".
// Spielerliste (Name, Auto, Geld, Autos) liefert "status" per TCP.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TDU2Server;

// ---------------------------------------------------------------- Datenklassen (Feldnamen = JSON-Namen, identisch im Unity-Client)
public sealed class ServerConfig
{
    public int tcpPort = 7777;
    public int udpPort = 7778;
    public string dataDir = "data";
    /// <summary>Startautos zur Auswahl beim Gebrauchtwagenhändler (Dateinamen wie in DBCarPhysicsData._car_file_name).</summary>
    public List<string> starterCars = new() { "Delta_Integrale_Evo_2", "Mustang_Fastback_68", "Esprit_S3" };
    /// <summary>Spot, an dem neue Spieler starten (Gebrauchtwagenhändler Ibiza = ESTOL_B, Bestand im Spiel: Mustang 68, Esprit S3, Delta Integrale).</summary>
    public string startSpot = "ESTOL_B";
    public string startIsland = "ibiza";
    public long startMoney = 25000;
    public int maxPlayers = 64;
    public string motd = "Willkommen auf dem TDU2-Server";
}

public sealed class OdoEntry
{
    public int carId;
    public double meters;
}

public sealed class SaveData
{
    public int version = 1;
    public string island = "";
    public bool hasPosition;
    public float px, py, pz, heading;
    public string currentCar = "";
    public List<string> ownedCars = new();
    public long money;
    public bool starterChosen;
    public List<OdoEntry> odometers = new();
    public List<LiveryEntry> liveries = new();      // Sticker-Shop: Lackierung/Beklebung je Auto (Base64, Client CarLivery)
    public List<int> stickerBoards = new();         // gekaufte Sticker-Planchen (DBStickersBanks._hashcode)
    public List<int> favoriteColors = new();        // "Meine Farben" (0xRRGGBB, -1 = leer)
    public string savedAt = "";
}

public sealed class LiveryEntry
{
    public string car = "";
    public string data = "";
}

public sealed class NetRequest
{
    public string op = "";
    public string nick = "";
    public string email = "";
    public string password = "";
    public string token = "";
    public string session = "";
    public int version;
    public int nationality;
    public SaveData save;
}

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
    public int island;          // 1 Ibiza, 2 Hawaii, 0 unbekannt
}

public sealed class NetResponse
{
    public bool ok;
    public string error = "";
    public string nick = "";
    public string token = "";
    public string session = "";
    public SaveData save;
    public List<string> starterCars = new();
    public string startSpot = "";
    public string startIsland = "";
    public long startMoney;
    public int online;
    public int udpPort;
    public string motd = "";
    public int playerId;
    public long udpKey;
    public List<PlayerEntry> players = new();
    // Radio-Zeitbasis: alle Clients berechnen daraus denselben Sendungsausschnitt (Hariba/RoadRock).
    public long radioEpoch;   // Serverstart (UTC, Unix-Millisekunden) – Nullpunkt der Radio-Zeitleiste
    public int radioSeed;     // gemeinsamer Zufallsstartwert für die deterministische Playlist
    public long serverTime;   // aktuelle Serverzeit (UTC, Unix-Millisekunden) – für den Uhrversatz des Clients
}

public sealed class Account
{
    public string nick = "";
    public string email = "";
    public string passwordHash = "";
    public string salt = "";
    public List<string> tokenHashes = new();
    public string created = "";
    public string lastLogin = "";
    public int nationality;
    public SaveData save = new();
}

// ---------------------------------------------------------------- Konten auf der Platte
public sealed class AccountStore
{
    readonly string _dir;
    readonly object _lock = new();
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
    static readonly Regex NickRx = new("^[A-Za-z0-9_.\\-]{3,16}$", RegexOptions.Compiled);

    public AccountStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "accounts");
        Directory.CreateDirectory(_dir);
    }

    public static bool ValidNick(string nick) => !string.IsNullOrEmpty(nick) && NickRx.IsMatch(nick);
    string FileOf(string nick) => Path.Combine(_dir, nick.ToLowerInvariant() + ".json");

    public Account Load(string nick)
    {
        if (!ValidNick(nick)) return null;
        lock (_lock)
        {
            var f = FileOf(nick);
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<Account>(File.ReadAllText(f), FileJson);
        }
    }

    public void Store(Account a)
    {
        lock (_lock)
        {
            var f = FileOf(a.nick);
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(a, FileJson));
            File.Move(tmp, f, true);
        }
    }

    public bool Create(Account a)
    {
        lock (_lock)
        {
            if (File.Exists(FileOf(a.nick))) return false;
            Store(a);
            return true;
        }
    }

    public int Count() { try { return Directory.GetFiles(_dir, "*.json").Length; } catch { return 0; } }
    public IEnumerable<string> Names() => Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension);

    // ---- Passwörter / Tokens
    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    public static string HashPassword(string password, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32));
    public static bool CheckPassword(Account a, string password) =>
        !string.IsNullOrEmpty(password) && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(HashPassword(password, a.salt)), Convert.FromBase64String(a.passwordHash));
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""))).ToLowerInvariant();
}

// ---------------------------------------------------------------- Server
public sealed class Session
{
    public string Id = "";
    public string Nick = "";
    public Connection Conn;
    public DateTime Since = DateTime.Now;
    public int PlayerId;
    public uint UdpKey;
    public IPEndPoint UdpEndPoint;
    public DateTime LastUdp = DateTime.MinValue;
    public int CarId, Color;
    public bool InCar;
    public float Px, Pz;
    public long Money;
    public int NbCars;
    public int Nationality;
    public int Island;          // 1 Ibiza, 2 Hawaii, 0 unbekannt
}

public sealed class Connection
{
    public TcpClient Client;
    public string Remote = "";
    public Session Session;
}

public static class Program
{
    static ServerConfig _cfg = new();
    static AccountStore _store;
    static readonly ConcurrentDictionary<string, Session> _sessions = new();
    static readonly JsonSerializerOptions WireJson = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
    static TcpListener _listener;
    static UdpClient _udp;
    static volatile bool _running = true;
    static int _nextPlayerId = 1;
    static readonly ConcurrentDictionary<int, Session> _byPlayerId = new();
    // Radio-Zeitbasis (siehe NetResponse): beim Start festgelegt, damit alle Clients dieselbe Playlist hören.
    static long _radioEpoch;
    static int _radioSeed;

    static long ServerNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string cfgFile = args.Length > 0 ? args[0] : "config.json";
        LoadConfig(cfgFile);
        _store = new AccountStore(_cfg.dataDir);
        _radioEpoch = ServerNowMs();
        _radioSeed = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
        Log($"Radio-Zeitbasis: Epoche {_radioEpoch}, Startwert {_radioSeed}");

        _listener = new TcpListener(IPAddress.Any, _cfg.tcpPort);
        _listener.Start();
        Log($"TDU2Server läuft – TCP {_cfg.tcpPort}, Daten in {Path.GetFullPath(_cfg.dataDir)}, {_store.Count()} Konten");
        Log($"Start: Spot {_cfg.startSpot} ({_cfg.startIsland}), Startautos: {string.Join(", ", _cfg.starterCars)}");
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _cfg.udpPort));
            try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }   // Windows: ICMP-Fehler ignorieren
            _ = Task.Run(UdpLoop);
            Log($"UDP-Synchronisation auf Port {_cfg.udpPort}");
        }
        catch (Exception ex) { Log("UDP-Port " + _cfg.udpPort + " nicht nutzbar: " + ex.Message); }
        Log("Befehle: list, accounts, reload, quit");

        _ = Task.Run(ConsoleLoop);
        while (_running)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); }
            catch (Exception) when (!_running) { break; }
            catch (Exception ex) { Log("Accept: " + ex.Message); continue; }
            _ = Task.Run(() => HandleClient(c));
        }
        Log("Server beendet.");
    }

    static void LoadConfig(string file)
    {
        var opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        try
        {
            if (File.Exists(file)) _cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(file), opts) ?? new ServerConfig();
            else
            {
                _cfg = new ServerConfig();
                File.WriteAllText(file, JsonSerializer.Serialize(_cfg, opts));
                Log("config.json angelegt – Startautos/Start-Spot dort einstellbar.");
            }
        }
        catch (Exception ex) { Log("config.json fehlerhaft (" + ex.Message + ") – Standardwerte."); _cfg = new ServerConfig(); }
        if (_cfg.starterCars == null || _cfg.starterCars.Count == 0) _cfg.starterCars = new ServerConfig().starterCars;
    }

    static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    static async Task ConsoleLoop()
    {
        while (_running)
        {
            string line = await Task.Run(() => Console.ReadLine());
            if (line == null) { await Task.Delay(500); continue; }
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0].ToLowerInvariant())
            {
                case "list":
                    Log($"{_sessions.Count} online");
                    foreach (var s in _sessions.Values) Log($"  #{s.PlayerId} {s.Nick}  ({s.Conn?.Remote}, UDP {s.UdpEndPoint?.ToString() ?? "-"}, Auto {s.CarId}, Insel {(s.Island == 1 ? "Ibiza" : s.Island == 2 ? "Hawaii" : "?")}, seit {s.Since:HH:mm:ss})");
                    break;
                case "accounts":
                    foreach (var n in _store.Names()) Log("  " + n);
                    break;
                case "reload":
                    LoadConfig("config.json");
                    Log("Konfiguration neu geladen.");
                    break;
                case "quit":
                case "exit":
                    _running = false;
                    try { _listener.Stop(); } catch { }
                    break;
                default:
                    Log("Befehle: list, accounts, reload, quit");
                    break;
            }
        }
    }

    static async Task HandleClient(TcpClient client)
    {
        var conn = new Connection { Client = client, Remote = client.Client.RemoteEndPoint?.ToString() ?? "?" };
        client.NoDelay = true;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            while (_running)
            {
                string line = await reader.ReadLineAsync();
                if (line == null) break;
                if (line.Length > 1 << 20) break;   // 1 MB Schutz
                NetResponse resp;
                try
                {
                    var req = JsonSerializer.Deserialize<NetRequest>(line, WireJson) ?? new NetRequest();
                    resp = Handle(conn, req);
                }
                catch (Exception ex)
                {
                    resp = new NetResponse { ok = false, error = "bad_request" };
                    Log($"{conn.Remote}: Anfrage fehlerhaft: {ex.Message}");
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, WireJson));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log($"{conn.Remote}: {ex.Message}"); }
        finally
        {
            if (conn.Session != null && _sessions.TryRemove(conn.Session.Id, out var s))
            {
                _byPlayerId.TryRemove(s.PlayerId, out _);
                Log($"{s.Nick} getrennt ({_sessions.Count} online)");
            }
            try { client.Close(); } catch { }
        }
    }

    static NetResponse Fail(string error) => new() { ok = false, error = error, online = _sessions.Count };

    static NetResponse Handle(Connection conn, NetRequest r)
    {
        switch ((r.op ?? "").ToLowerInvariant())
        {
            case "hello":
                return new NetResponse
                {
                    ok = true, starterCars = _cfg.starterCars, startSpot = _cfg.startSpot, startIsland = _cfg.startIsland,
                    startMoney = _cfg.startMoney, online = _sessions.Count, udpPort = _cfg.udpPort, motd = _cfg.motd,
                    radioEpoch = _radioEpoch, radioSeed = _radioSeed, serverTime = ServerNowMs(),
                };

            case "register":
            {
                string nick = (r.nick ?? "").Trim();
                if (!AccountStore.ValidNick(nick)) return Fail("invalid_nick");
                if (string.IsNullOrEmpty(r.password) || r.password.Length < 4) return Fail("weak_password");
                if (_sessions.Count >= _cfg.maxPlayers) return Fail("server_full");
                string salt = AccountStore.NewSalt();
                var a = new Account
                {
                    nick = nick, email = (r.email ?? "").Trim(), salt = salt, passwordHash = AccountStore.HashPassword(r.password, salt),
                    created = DateTime.UtcNow.ToString("o"),
                    save = new SaveData { island = _cfg.startIsland, money = _cfg.startMoney, starterChosen = false },
                };
                string token = AccountStore.NewToken();
                a.tokenHashes.Add(AccountStore.HashToken(token));
                if (!_store.Create(a)) return Fail("nick_taken");
                Log($"Neues Konto: {nick} ({conn.Remote})");
                a.nationality = r.nationality;
                return StartSession(conn, a, token);
            }

            case "login":
            {
                var a = _store.Load((r.nick ?? "").Trim());
                if (a == null) return Fail("unknown_nick");
                string token;
                if (!string.IsNullOrEmpty(r.password))
                {
                    if (!AccountStore.CheckPassword(a, r.password)) { Log($"Falsches Passwort für {a.nick} ({conn.Remote})"); return Fail("bad_password"); }
                    token = AccountStore.NewToken();
                    a.tokenHashes.Add(AccountStore.HashToken(token));
                    while (a.tokenHashes.Count > 8) a.tokenHashes.RemoveAt(0);
                }
                else
                {
                    if (string.IsNullOrEmpty(r.token) || !a.tokenHashes.Contains(AccountStore.HashToken(r.token))) return Fail("bad_token");
                    token = r.token;
                }
                if (_sessions.Count >= _cfg.maxPlayers) return Fail("server_full");
                if (r.nationality > 0) a.nationality = r.nationality;
                return StartSession(conn, a, token);
            }

            case "save":
            {
                var s = SessionOf(conn, r.session);
                if (s == null) return Fail("not_logged_in");
                if (r.save == null) return Fail("no_save");
                var a = _store.Load(s.Nick);
                if (a == null) return Fail("unknown_nick");
                var sv = r.save;
                if (sv.money < 0) sv.money = 0;
                sv.ownedCars ??= new List<string>();
                sv.odometers ??= new List<OdoEntry>();
                if (sv.ownedCars.Count > 500) sv.ownedCars = sv.ownedCars.Take(500).ToList();
                sv.liveries ??= new List<LiveryEntry>();
                sv.stickerBoards ??= new List<int>();
                sv.favoriteColors ??= new List<int>();
                sv.liveries = sv.liveries.Where(l => l != null && !string.IsNullOrEmpty(l.car) && (l.data ?? "").Length <= 8192).Take(500).ToList();
                if (sv.stickerBoards.Count > 200) sv.stickerBoards = sv.stickerBoards.Take(200).ToList();
                if (sv.favoriteColors.Count > 16) sv.favoriteColors = sv.favoriteColors.Take(16).ToList();
                sv.savedAt = DateTime.UtcNow.ToString("o");
                a.save = sv;
                _store.Store(a);
                s.Money = sv.money; s.NbCars = sv.ownedCars.Count;
                int isl = IslandCode(sv.island);
                if (isl != 0 && isl != s.Island) { Log($"{s.Nick}: Insel {sv.island}"); s.Island = isl; }
                return new NetResponse { ok = true, nick = a.nick, online = _sessions.Count };
            }

            case "status":
            {
                var me = SessionOf(conn, r.session);
                var resp = new NetResponse { ok = true, online = _sessions.Count, nick = me?.Nick ?? "", playerId = me?.PlayerId ?? 0,
                    radioEpoch = _radioEpoch, radioSeed = _radioSeed, serverTime = ServerNowMs() };
                foreach (var s in _sessions.Values)
                    resp.players.Add(new PlayerEntry
                    {
                        id = s.PlayerId, nick = s.Nick, carId = s.CarId, color = s.Color, money = s.Money, nbCars = s.NbCars,
                        inCar = s.InCar && (DateTime.UtcNow - s.LastUdp).TotalSeconds < 10, px = s.Px, pz = s.Pz, nationality = s.Nationality,
                        island = s.Island,
                    });
                return resp;
            }

            case "logout":
            {
                var s = SessionOf(conn, r.session);
                if (s != null && _sessions.TryRemove(s.Id, out _)) { _byPlayerId.TryRemove(s.PlayerId, out _); conn.Session = null; Log($"{s.Nick} abgemeldet ({_sessions.Count} online)"); }
                return new NetResponse { ok = true, online = _sessions.Count };
            }

            case "forget":
            {
                var a = _store.Load((r.nick ?? "").Trim());
                if (a != null && !string.IsNullOrEmpty(r.token) && a.tokenHashes.Remove(AccountStore.HashToken(r.token))) _store.Store(a);
                return new NetResponse { ok = true };
            }
        }
        return Fail("unknown_op");
    }

    static Session SessionOf(Connection conn, string id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var s)) return null;
        return s.Conn == conn ? s : null;   // Sitzung gehört zu dieser Verbindung
    }

    static NetResponse StartSession(Connection conn, Account a, string token)
    {
        // gleiches Konto schon online (z.B. nach Absturz): alte Sitzung beenden
        foreach (var old in _sessions.Values.Where(x => string.Equals(x.Nick, a.nick, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _sessions.TryRemove(old.Id, out _);
            _byPlayerId.TryRemove(old.PlayerId, out _);
            if (old.Conn != conn) { try { old.Conn?.Client?.Close(); } catch { } }
            Log($"{old.Nick}: alte Sitzung beendet");
        }
        if (conn.Session != null) { _sessions.TryRemove(conn.Session.Id, out _); _byPlayerId.TryRemove(conn.Session.PlayerId, out _); }
        var s = new Session
        {
            Id = AccountStore.NewToken(), Nick = a.nick, Conn = conn,
            PlayerId = NewPlayerId(), UdpKey = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)),
            Money = a.save?.money ?? 0, NbCars = a.save?.ownedCars?.Count ?? 0, Nationality = a.nationality,
            Island = IslandCode(a.save?.island),
        };
        conn.Session = s;
        _sessions[s.Id] = s;
        _byPlayerId[s.PlayerId] = s;
        a.lastLogin = DateTime.UtcNow.ToString("o");
        _store.Store(a);
        Log($"{a.nick} angemeldet ({conn.Remote}, {_sessions.Count} online)");
        return new NetResponse
        {
            ok = true, nick = a.nick, token = token, session = s.Id, save = a.save,
            starterCars = _cfg.starterCars, startSpot = _cfg.startSpot, startIsland = _cfg.startIsland, startMoney = _cfg.startMoney,
            online = _sessions.Count, udpPort = _cfg.udpPort, motd = _cfg.motd,
            playerId = s.PlayerId, udpKey = s.UdpKey,
            radioEpoch = _radioEpoch, radioSeed = _radioSeed, serverTime = ServerNowMs(),
        };
    }

    /// <summary>Inselname (Spielstand) -> Code des UDP-Zustands: 1 Ibiza, 2 Hawaii, 0 unbekannt.</summary>
    static int IslandCode(string island)
    {
        if (string.IsNullOrEmpty(island)) return 0;
        if (island.StartsWith("i", StringComparison.OrdinalIgnoreCase)) return 1;
        if (island.StartsWith("h", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    /// <summary>Sehen sich zwei Spieler (gleiche Insel; unbekannt sieht alle)?</summary>
    static bool SameIsland(Session a, Session b) => a.Island == 0 || b.Island == 0 || a.Island == b.Island;

    static int NewPlayerId()
    {
        for (int i = 0; i < 65535; i++)
        {
            int id = Interlocked.Increment(ref _nextPlayerId) & 0xFFFF;
            if (id != 0 && !_byPlayerId.ContainsKey(id)) return id;
        }
        return 1;
    }

    // ---------------------------------------------------------------- UDP: Zustände der Spieler an alle anderen weiterreichen
    const int StateMin = 12;   // seq + carId + Farbe/Flags/Licht/frei

    // ---------------------------------------------------------------- Chat: [4][u16 Spieler][u32 Schlüssel][u32 Nr][Text] -> an alle [3][u16][u32 Nr][Name][0][Text]
    static readonly ConcurrentDictionary<int, uint> _lastChat = new();

    static async Task HandleChat(byte[] d, IPEndPoint from)
    {
        int pid = d[1] | (d[2] << 8);
        uint key = BitConverter.ToUInt32(d, 3);
        if (!_byPlayerId.TryGetValue(pid, out var s) || s.UdpKey != key) return;
        uint msg = BitConverter.ToUInt32(d, 7);
        s.UdpEndPoint ??= from;
        if (_lastChat.TryGetValue(pid, out var last) && last == msg) return;   // zweite Kopie
        _lastChat[pid] = msg;
        string text = Encoding.UTF8.GetString(d, 11, d.Length - 11).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0) return;
        if (text.Length > 120) text = text.Substring(0, 120);
        Log($"[Chat] {s.Nick}: {text}");
        var nick = Encoding.UTF8.GetBytes(s.Nick);
        var t = Encoding.UTF8.GetBytes(text);
        var o = new byte[7 + nick.Length + 1 + t.Length];
        o[0] = 3; o[1] = (byte)pid; o[2] = (byte)(pid >> 8);
        Buffer.BlockCopy(d, 7, o, 3, 4);
        Buffer.BlockCopy(nick, 0, o, 7, nick.Length);
        Buffer.BlockCopy(t, 0, o, 8 + nick.Length, t.Length);
        var now = DateTime.UtcNow;
        foreach (var x in _byPlayerId.Values)
        {
            var ep = x == s ? (x.UdpEndPoint ?? from) : x.UdpEndPoint;
            if (ep == null || (x != s && (now - x.LastUdp).TotalSeconds > 30)) continue;
            // zweimal senden (UDP kann Pakete verlieren; Clients verwerfen Doppelte)
            for (int i = 0; i < 2; i++) { try { await _udp.SendAsync(o, o.Length, ep); } catch (Exception) { } }
        }
    }

    // ---------------------------------------------------------------- Verkehr: [5][u16 Spieler][u32 Schlüssel][Nutzdaten] -> an alle anderen [6][u16 Spieler][Nutzdaten]
    // Die Clients simulieren die Verkehrsautos selbst (jedes Auto gehört einem Spieler); der Server reicht nur weiter.
    static readonly byte[] _trafficBuf = new byte[1500];

    static async Task HandleTraffic(byte[] d, IPEndPoint from)
    {
        int pid = d[1] | (d[2] << 8);
        uint key = BitConverter.ToUInt32(d, 3);
        if (!_byPlayerId.TryGetValue(pid, out var s) || s.UdpKey != key) return;
        s.UdpEndPoint = from;
        int len = 3 + (d.Length - 7);
        var o = _trafficBuf;
        o[0] = 6; o[1] = (byte)pid; o[2] = (byte)(pid >> 8);
        Buffer.BlockCopy(d, 7, o, 3, d.Length - 7);
        var now = DateTime.UtcNow;
        foreach (var x in _byPlayerId.Values)
        {
            if (x == s || x.UdpEndPoint == null || (now - x.LastUdp).TotalSeconds > 15 || !SameIsland(s, x)) continue;
            try { await _udp.SendAsync(new ReadOnlyMemory<byte>(o, 0, len), x.UdpEndPoint); } catch (Exception) { }
        }
    }

    static async Task UdpLoop()
    {
        var outBuf = new byte[1500];
        while (_running)
        {
            UdpReceiveResult rr;
            try { rr = await _udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var d = rr.Buffer;
            if (d.Length >= 12 && d[0] == 4 && d.Length <= 600) { await HandleChat(d, rr.RemoteEndPoint); continue; }
            if (d.Length >= 10 && d[0] == 5 && d.Length <= 1300) { await HandleTraffic(d, rr.RemoteEndPoint); continue; }
            if (d.Length < 7 + StateMin || d[0] != 1 || d.Length > 1200) continue;
            int pid = d[1] | (d[2] << 8);
            uint key = BitConverter.ToUInt32(d, 3);
            if (!_byPlayerId.TryGetValue(pid, out var s) || s.UdpKey != key) continue;
            s.UdpEndPoint = rr.RemoteEndPoint;
            s.LastUdp = DateTime.UtcNow;
            int st = 7;
            s.CarId = BitConverter.ToInt32(d, st + 4);
            s.Color = d[st + 8];
            s.InCar = (d[st + 9] & 1) != 0;
            if (d.Length >= st + 24) { s.Px = BitConverter.ToSingle(d, st + 12); s.Pz = BitConverter.ToSingle(d, st + 20); }
            if (d.Length >= st + 81 && d[st + 80] != 0 && d[st + 80] != s.Island) { s.Island = d[st + 80]; Log($"{s.Nick}: Insel {(s.Island == 1 ? "Ibiza" : s.Island == 2 ? "Hawaii" : s.Island.ToString())}"); }

            // weiterleiten: [2][u16 playerId][Zustand]
            int len = 3 + (d.Length - st);
            outBuf[0] = 2; outBuf[1] = (byte)pid; outBuf[2] = (byte)(pid >> 8);
            Buffer.BlockCopy(d, st, outBuf, 3, d.Length - st);
            var now = DateTime.UtcNow;
            foreach (var o in _byPlayerId.Values)
            {
                if (o == s || o.UdpEndPoint == null || (now - o.LastUdp).TotalSeconds > 15 || !SameIsland(s, o)) continue;
                try { await _udp.SendAsync(new ReadOnlyMemory<byte>(outBuf, 0, len), o.UdpEndPoint); }
                catch (Exception) { }
            }
        }
    }
}
