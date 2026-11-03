using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"lobbyjoin","session":..,"target":lobbyId} -> in eine andere Lobby wechseln (falls Platz und gleiche Insel).</summary>
public sealed class LobbyJoinHandler : RequestHandler
{
    public LobbyJoinHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "lobbyjoin";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        if (!Ctx.Lobbies.Join(me, r.target)) return Fail("lobby_full_or_unknown");
        Log.Info($"{me.Nick}: wechselt in Lobby {me.Lobby}");
        return new NetResponse { ok = true, online = Online, lobby = me.Lobby, lobbyMax = Ctx.Config.maxPlayersPerLobby };
    }
}
