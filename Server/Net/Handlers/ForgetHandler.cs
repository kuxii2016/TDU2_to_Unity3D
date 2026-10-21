using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"forget","nick":..,"token":..} -> Merk-Token löschen (Profil vom PC entfernt).</summary>
public sealed class ForgetHandler : RequestHandler
{
    public ForgetHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "forget";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var a = Ctx.Accounts.Load((r.nick ?? "").Trim());
        if (a != null && !string.IsNullOrEmpty(r.token) && a.tokenHashes.Remove(Credentials.HashToken(r.token))) Ctx.Accounts.Store(a);
        return NetResponse.Ok();
    }
}
