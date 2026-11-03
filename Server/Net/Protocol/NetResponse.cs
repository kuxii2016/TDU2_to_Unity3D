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
    public string avatar = "";                   // profile: Aussehen (AvatarConfig.Encode) – Geschlecht für den Foto-Platzhalter
    public int newsLatest;                       // status/news: höchste News-Id (Client holt per "news" nach, wenn größer als bekannt)
    public List<NewsItem> news = new();          // news: Meldungen mit Id > since (neueste zuerst)
    public List<int> newsIds = new();            // news: alle vorhandenen Ids (gelöschte entfernt der Client)
    public string newsImage = "";                // newsimg: Bild der Meldung "target" (JPEG/PNG, Base64; leer = keins)
    public List<SurveyEntry> surveys = new();    // surveys/vote: Umfragen mit Summen und eigener Stimme
    public int surveyVersion;                    // surveys/vote: Änderungszähler der Umfragen (Client holt neu, wenn anders)
    // Lobbys (status/lobbies/lobbyjoin): eigene Lobby, Spieler darin, Höchstzahl (0 = unbegrenzt), Abfrageabstand
    public int lobby;
    public int lobbyCount;
    public int lobbyMax;
    public int statusIntervalMs;
    public List<LobbyEntry> lobbies = new();     // lobbies: alle Lobbys
    public List<RecentEntry> recent = new();     // recent: zuletzt gesehene Spieler
    public bool p2pEnabled;                      // status: Server erlaubt P2P
    public long p2pToken;                        // status: mein Token (steht in meinen Punch-Paketen)
    public List<FriendEntry> friends = new();    // friend: bestätigte Freunde
    public List<FriendEntry> friendRequests = new(); // friend: Anfragen an mich (warten auf meine Antwort)
    public List<string> friendPending = new();   // friend: meine offenen Anfragen
    public string friendResult = "";             // friend request: sent / already_friends / already_sent / became_friends

    public static NetResponse Ok() => new() { ok = true };
}
