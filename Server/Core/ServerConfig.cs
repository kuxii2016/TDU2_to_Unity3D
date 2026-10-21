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
    public string motd = "Willkommen auf dem TDU2-Server";
    // Web-Moderationspanel (im selben Prozess). Abschalten mit webEnabled=false.
    public bool webEnabled = true;
    public string webHost = "0.0.0.0";
    public int webPort = 8080;

    static readonly JsonSerializerOptions FileOptions = new()
    {
        IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
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
