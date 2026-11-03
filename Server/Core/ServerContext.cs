using TDU2Server.Data;
using TDU2Server.Sessions;
using TDU2Server.Web;

namespace TDU2Server.Core;

/// <summary>Gemeinsame Dienste des Servers (für Anfrage- und Paket-Handler).</summary>
public sealed class ServerContext
{
    public ServerConfig Config { get; set; }
    public AccountStore Accounts { get; }
    public SessionManager Sessions { get; }
    public RadioClock Radio { get; } = new();
    public string ConfigFile { get; }
    public AuditLog Audit { get; } = new();
    public ChatLog Chat { get; } = new();
    /// <summary>News (data/news.json) – im Web-Panel angelegt, vom Client per "news" abgeholt.</summary>
    public NewsStore News { get; }
    /// <summary>Umfragen (data/surveys.json) – im Web-Panel angelegt, im Spiel im Umfrage-Kanal von TDU Media abgestimmt.</summary>
    public SurveyStore Surveys { get; }
    /// <summary>Lobbys (Spieler-Gruppen je Insel, Größe aus config.json).</summary>
    public LobbyManager Lobbies { get; }
    /// <summary>Zuletzt gesehene Spieler (data/recent.json, op "recent").</summary>
    public RecentStore Recent { get; }
    /// <summary>Freundeslisten mit Anfragen (data/friends.json, op "friend").</summary>
    public FriendStore Friends { get; }
    /// <summary>UDP-Server (Datenmengen-Statistik für Konsole/Panel).</summary>
    public TDU2Server.Net.Udp.UdpServer Udp { get; set; }

    /// <summary>Setzt eine Moderationsaktion um (Kick, Nachricht …). Wird vom Web-Panel belegt.</summary>
    public ServerActions Actions { get; set; }

    public ServerContext(string configFile)
    {
        ConfigFile = configFile;
        Config = ServerConfig.Load(configFile);
        Accounts = new AccountStore(Config.dataDir);
        News = new NewsStore(Config.dataDir);
        Surveys = new SurveyStore(Config.dataDir);
        Lobbies = new LobbyManager(() => Config);
        Recent = new RecentStore(Config.dataDir);
        Friends = new FriendStore(Config.dataDir);
        Sessions = new SessionManager(Lobbies, Recent);
        Actions = new ServerActions(this);
    }
}
