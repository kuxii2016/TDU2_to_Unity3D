using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"logout","session":..} -> abmelden.</summary>
public sealed class LogoutHandler : RequestHandler
{
    public LogoutHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "logout";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var s = SessionOf(conn, r);
        if (Ctx.Sessions.Remove(s))
        {
            conn.Session = null;
            Log.Info($"{s.Nick} abgemeldet ({Online} online)");
        }
        return new NetResponse { ok = true, online = Online };
    }
}
