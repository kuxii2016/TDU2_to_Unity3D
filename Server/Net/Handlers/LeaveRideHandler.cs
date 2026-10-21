using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"leaveride","session":..} -> Mitfahrt beenden (als Beifahrer oder Fahrer).</summary>
public sealed class LeaveRideHandler : RequestHandler
{
    public LeaveRideHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "leaveride";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        Ctx.Sessions.EndRide(me);
        return NetResponse.Ok();
    }
}
