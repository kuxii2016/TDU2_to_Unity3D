using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"invreply","session":..,"target":id,"accept":b} -> Einladung von id annehmen/ablehnen.
/// codriving = Beifahrer bei id; sonst Antwort mit inviteKind (+ bei Häusern "save" = Autos/Beklebungen/Einrichtung des Gastgebers),
/// der Gastgeber bekommt "joined:".
/// </summary>
public sealed class InviteReplyHandler : RequestHandler
{
    public InviteReplyHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "invreply";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");

        bool had;
        string kind = "codriving";
        lock (me.Lock)
        {
            foreach (var i in me.Invites) if (i.From == r.target) kind = i.Kind;
            had = me.Invites.RemoveAll(i => i.From == r.target) > 0;
        }
        if (!Ctx.Sessions.TryGetPlayer(r.target, out var host)) return Fail("unknown_player");
        if (!had) return Fail("no_invite");

        if (!r.accept)
        {
            host.Notice($"declined:{me.PlayerId}:{me.Nick}");
            Log.Info($"{me.Nick} lehnt die Einladung von {host.Nick} ab");
            return NetResponse.Ok();
        }
        return kind.StartsWith("codriving") ? AcceptRide(me, host) : AcceptGame(me, host, kind);
    }

    /// <summary>Spiel-Einladung: der Gast kommt zum Gastgeber (Straße bzw. dasselbe Interieur); bei Häusern die Garage mitgeben.</summary>
    NetResponse AcceptGame(Session me, Session host, string kind)
    {
        host.Notice($"joined:{me.PlayerId}:{me.Nick}");
        Log.Info($"{me.Nick} folgt der Einladung von {host.Nick} ({kind})");
        var resp = new NetResponse { ok = true, inviteKind = kind };
        var parts = kind.Split('|');
        if (parts.Length > 1 && parts[0] == "room" && parts[1].StartsWith("EPH", StringComparison.OrdinalIgnoreCase))
        {
            var hs = Ctx.Accounts.Load(host.Nick)?.save;
            if (hs != null)
                resp.save = new SaveData
                {
                    currentCar = hs.currentCar,
                    ownedCars = hs.ownedCars ?? new List<string>(),
                    liveries = hs.liveries ?? new List<LiveryEntry>(),
                    houses = hs.houses ?? new List<HouseLayout>(),
                };
        }
        return resp;
    }

    /// <summary>Mitfahrt: ich werde Beifahrer beim Gastgeber.</summary>
    NetResponse AcceptRide(Session me, Session host)
    {
        if (host.Passenger != 0 || host.RideWith != 0) return Fail("busy");
        Ctx.Sessions.EndRide(me);
        me.RideWith = host.PlayerId;
        host.Passenger = me.PlayerId;
        host.Notice($"accepted:{me.PlayerId}:{me.Nick}");
        Log.Info($"{me.Nick} fährt bei {host.Nick} mit");
        return new NetResponse { ok = true, rideWith = host.PlayerId };
    }
}
