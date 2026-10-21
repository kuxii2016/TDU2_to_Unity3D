using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"invite","session":..,"target":id,"kind":k} -> Einladung an Spieler id (kommt bei ihm über "status" an).
/// k = "codriving" (Beifahrer), "game|&lt;Ort&gt;" (zu mir auf die Straße), "room|&lt;SPOT&gt;|&lt;Bank&gt;|&lt;Ort&gt;" (zu mir ins Interieur/Haus).
/// </summary>
public sealed class InviteHandler : RequestHandler
{
    const int MaxKind = 200;

    public InviteHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "invite";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        if (!Ctx.Sessions.TryGetPlayer(r.target, out var t) || t == me) return Fail("unknown_player");
        if (!Islands.Same(me.Island, t.Island)) return Fail("other_island");

        string kind = string.IsNullOrEmpty(r.kind) ? "codriving" : r.kind;
        if (kind.Length > MaxKind) kind = kind[..MaxKind];
        bool ride = kind.StartsWith("codriving");
        if (ride && (t.RideWith != 0 || t.Passenger != 0)) return Fail("busy");
        if (me.RideWith != 0) return Fail("is_passenger");
        if (ride && me.Passenger != 0) return Fail("has_passenger");

        lock (t.Lock)
        {
            t.Invites.RemoveAll(i => i.From == me.PlayerId);
            t.Invites.Add(new Invite(me.PlayerId, me.Nick, kind, DateTime.UtcNow));
        }
        Log.Info($"{me.Nick} lädt {t.Nick} ein ({kind})");
        return new NetResponse { ok = true, online = Online };
    }
}
