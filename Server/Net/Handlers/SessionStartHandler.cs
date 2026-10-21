using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>Gemeinsame Basis für register/login: Sitzung starten und die Anmelde-Antwort bauen.</summary>
public abstract class SessionStartHandler : RequestHandler
{
    protected SessionStartHandler(ServerContext ctx) : base(ctx) { }

    protected bool ServerFull => Online >= Ctx.Config.maxPlayers;

    /// <summary>Sitzung anlegen (alte Sitzung desselben Kontos endet), Konto speichern, Antwort mit token/session/save/UDP-Zugang.</summary>
    protected NetResponse StartSession(Connection conn, Account a, string token)
    {
        var s = Ctx.Sessions.Start(conn, a);
        a.lastLogin = DateTime.UtcNow.ToString("o");
        Ctx.Accounts.Store(a);
        Log.Info($"{a.nick} angemeldet ({conn.Remote}, {Online} online)");
        Ctx.Audit.Add("login", a.nick, $"angemeldet von {conn.Remote}");
        var resp = WithServerInfo(NetResponse.Ok());
        resp.nick = a.nick;
        resp.token = token;
        resp.session = s.Id;
        resp.save = a.save;
        resp.playerId = s.PlayerId;
        resp.udpKey = s.UdpKey;
        return resp;
    }
}
