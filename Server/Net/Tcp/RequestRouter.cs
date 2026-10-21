using TDU2Server.Core;
using TDU2Server.Net.Handlers;
using TDU2Server.Net.Protocol;

namespace TDU2Server.Net.Tcp;

/// <summary>Verteilt eine Anfrage anhand von "op" an ihren Handler.</summary>
public sealed class RequestRouter
{
    readonly Dictionary<string, RequestHandler> _handlers = new();
    readonly ServerContext _ctx;

    public RequestRouter(ServerContext ctx)
    {
        _ctx = ctx;
        Add(new HelloHandler(ctx));
        Add(new RegisterHandler(ctx));
        Add(new LoginHandler(ctx));
        Add(new SaveHandler(ctx));
        Add(new StatusHandler(ctx));
        Add(new LogoutHandler(ctx));
        Add(new InviteHandler(ctx));
        Add(new InviteReplyHandler(ctx));
        Add(new LeaveRideHandler(ctx));
        Add(new ProfileHandler(ctx));
        Add(new ForgetHandler(ctx));
    }

    void Add(RequestHandler h) => _handlers[h.Op] = h;

    public NetResponse Handle(Connection conn, NetRequest r)
    {
        if (_handlers.TryGetValue((r.op ?? "").ToLowerInvariant(), out var h)) return h.Handle(conn, r);
        return new NetResponse { ok = false, error = "unknown_op", online = _ctx.Sessions.Count };
    }
}
