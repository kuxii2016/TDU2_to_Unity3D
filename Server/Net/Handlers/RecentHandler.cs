using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"recent","session":..} -> zuletzt gesehene Spieler des Servers (neueste zuerst, mit Online-Status).</summary>
public sealed class RecentHandler : RequestHandler
{
    const int Count = 50;

    public RecentHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "recent";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        var online = new HashSet<string>(Ctx.Sessions.All.Select(s => s.Nick), StringComparer.OrdinalIgnoreCase);
        var resp = new NetResponse { ok = true, online = Online };
        foreach (var p in Ctx.Recent.Latest(me.Nick, Count))
            resp.recent.Add(new RecentEntry
            {
                nick = p.nick, lastSeen = p.lastSeen, online = online.Contains(p.nick), carId = p.carId, color = p.color,
                nbCars = p.nbCars, money = p.money, nationality = p.nationality,
            });
        return resp;
    }
}
