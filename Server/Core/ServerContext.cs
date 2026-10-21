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

    /// <summary>Setzt eine Moderationsaktion um (Kick, Nachricht …). Wird vom Web-Panel belegt.</summary>
    public ServerActions Actions { get; set; }

    public ServerContext(string configFile)
    {
        ConfigFile = configFile;
        Config = ServerConfig.Load(configFile);
        Accounts = new AccountStore(Config.dataDir);
        Sessions = new SessionManager();
        Actions = new ServerActions(this);
    }
}
