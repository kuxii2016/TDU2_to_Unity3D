using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"profile","session":..,"target":id | "nick":..} -> MyTDULife eines Spielers (Spielerkarte): Stimmung, Profil, Foto.</summary>
public sealed class ProfileHandler : RequestHandler
{
    public ProfileHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "profile";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        if (SessionOf(conn, r) == null) return Fail("not_logged_in");
        Account ta = null;
        if (r.target != 0 && Ctx.Sessions.TryGetPlayer(r.target, out var ts)) ta = Ctx.Accounts.Load(ts.Nick);
        else if (!string.IsNullOrEmpty(r.nick)) ta = Ctx.Accounts.Load(r.nick.Trim());
        if (ta == null) return Fail("unknown_player");
        return new NetResponse { ok = true, nick = ta.nick, life = ta.save?.myLife ?? new MyLifeData(), lifeOf = r.target, online = Online };
    }
}
