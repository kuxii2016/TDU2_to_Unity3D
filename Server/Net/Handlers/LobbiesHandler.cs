using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"lobbies","session":..} -> alle Lobbys (Id, Insel, Spieler, Höchstzahl) und die eigene.</summary>
public sealed class LobbiesHandler : RequestHandler
{
    public LobbiesHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "lobbies";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        var resp = new NetResponse { ok = true, online = Online, lobby = me?.Lobby ?? 0, lobbyMax = Ctx.Config.maxPlayersPerLobby };
        foreach (var l in Ctx.Lobbies.All())
            resp.lobbies.Add(new LobbyEntry { id = l.Id, island = l.Island, count = l.Count, max = Ctx.Config.maxPlayersPerLobby });
        return resp;
    }
}
