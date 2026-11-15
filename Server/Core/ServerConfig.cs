using System.Text.Json;

namespace TDU2Server.Core;

/// <summary>config.json (Feldnamen = JSON-Namen).</summary>
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
    // Lobbys: der Server verteilt die Spieler einer Insel auf Lobbys (Spieler sehen nur ihre Lobby). 0 = eine Lobby je Insel.
    public int maxPlayersPerLobby = 16;
    /// <summary>Abstand der "status"-Abfragen der Clients (ms) – Spielerliste/Lobby, Einladungen.</summary>
    public int statusIntervalMs = 2000;
    // UDP-Weiterleitung (Server = Relay, Clients brauchen keine offenen Ports, NAT/CGNAT egal):
    /// <summary>Spieler weiter weg als … m bekommen nur jedes <see cref="farRelayDivider"/>-te Zustandspaket (0 = immer alle).</summary>
    public float relayRange = 1500f;
    public int farRelayDivider = 4;
    /// <summary>Verkehrspakete nur an Spieler innerhalb … m (0 = alle der Lobby).</summary>
    public float trafficRange = 1200f;
    /// <summary>Socket-Puffer der UDP-Verbindung (Bytes) – zu kleine Puffer verlieren bei vielen Spielern Pakete.</summary>
    public int udpBufferBytes = 4 * 1024 * 1024;
    // P2P: Spieler einer Lobby senden Zustand/Verkehr direkt zueinander (UDP-Hole-Punching), der Server vermittelt nur.
    // Wer keine Direktverbindung bekommt (symmetrisches NAT/CGNAT, Firewall), läuft automatisch über das Relay des Servers.
    public bool p2pEnabled = true;
    public string motd = "Willkommen auf dem TDU2-Server";
    // Web-Moderationspanel (im selben Prozess). Abschalten mit webEnabled=false.
    public bool webEnabled = true;
    public string webHost = "0.0.0.0";
    public int webPort = 8080;
    // MasterServer (Serverliste im Launcher): der Server meldet sich an, hält den Eintrag per Heartbeat
    // aktuell und meldet sich beim Beenden wieder ab. masterEnabled=false schaltet alles ab.
    public bool masterEnabled = true;
    /// <summary>Basis-Adresse des MasterServers, z. B. http://localhost:8080.</summary>
    public string masterUrl = "http://localhost:8080";
    /// <summary>Stabile Server-ID; leer = wird beim ersten Start erzeugt und gespeichert.</summary>
    public string masterId = "";
    /// <summary>Name in der Serverliste (leer = "TDU2-Server").</summary>
    public string masterName = "";
    /// <summary>Öffentliche Adresse für Clients; leer = MasterServer nimmt die Absender-IP (NAT-freundlich).</summary>
    public string masterHost = "";
    /// <summary>Port in der Serverliste; 0 = tcpPort.</summary>
    public int masterPort = 0;
    /// <summary>Mod-Nachricht in der Serverliste; leer = motd.</summary>
    public string masterModMessage = "";
    public string masterVersion = "TDU2MapUnity";
    public bool masterLocked = false;
    /// <summary>Heartbeat-Abstand (ms). Der MasterServer wirft Server nach 30 s ohne Heartbeat raus.</summary>
    public int masterHeartbeatMs = 10000;
    /// <summary>Wartezeit (ms) nach Fehlern/abgelehnter Anmeldung, bevor es erneut versucht wird.</summary>
    public int masterRetryMs = 15000;

    static readonly JsonSerializerOptions FileOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Konfiguration lesen; fehlt die Datei, wird sie mit Standardwerten angelegt.</summary>
    public static ServerConfig Load(string file)
    {
        ServerConfig cfg;
        try
        {
            if (File.Exists(file)) cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(file), FileOptions) ?? new ServerConfig();
            else
            {
                cfg = new ServerConfig();
                File.WriteAllText(file, JsonSerializer.Serialize(cfg, FileOptions));
                Log.Info("config.json angelegt – Startautos/Start-Spot dort einstellbar.");
            }
        }
        catch (Exception ex)
        {
            Log.Info("config.json fehlerhaft (" + ex.Message + ") – Standardwerte.");
            cfg = new ServerConfig();
        }
        if (cfg.starterCars == null || cfg.starterCars.Count == 0) cfg.starterCars = new ServerConfig().starterCars;
        return cfg;
    }

    /// <summary>Konfiguration schreiben (Web-Editor).</summary>
    public static void Save(string file, ServerConfig cfg) =>
        File.WriteAllText(file, JsonSerializer.Serialize(cfg, FileOptions));
}
