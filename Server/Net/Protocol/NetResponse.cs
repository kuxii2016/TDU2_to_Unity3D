using TDU2Server.Data;

namespace TDU2Server.Net.Protocol;

/// <summary>Antwort des Servers (eine JSON-Zeile). Nicht jede Antwort füllt alle Felder.</summary>
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
    public List<InviteEntry> invites = new();   // status: offene Einladungen an mich
    public List<string> notices = new();        // status: Meldungen seit der letzten Abfrage
    public int rideWith;                         // status: mein Fahrer (ich bin Beifahrer)
    public int passenger;                        // status: mein Beifahrer
    public string inviteKind = "";               // invreply: Art der angenommenen Einladung
    public MyLifeData life;                      // profile: MyTDULife des angefragten Spielers (mit Foto)
    public int lifeOf;                           // profile: Spielernummer dazu

    public static NetResponse Ok() => new() { ok = true };
}
