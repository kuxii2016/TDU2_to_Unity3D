using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"hello"} -> Startkonfiguration (Startautos, Start-Spot, Startgeld), Spielerzahl, Radio-Zeitbasis.</summary>
public sealed class HelloHandler : RequestHandler
{
    public HelloHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "hello";

    public override NetResponse Handle(Connection conn, NetRequest r) => WithServerInfo(NetResponse.Ok());
}
