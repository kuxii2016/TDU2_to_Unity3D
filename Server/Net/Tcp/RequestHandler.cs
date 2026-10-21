using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Tcp;

/// <summary>Basis für eine Anfrage ("op"). Jede Anfrage hat ihre eigene Klasse unter Net/Handlers.</summary>
public abstract class RequestHandler
{
    protected readonly ServerContext Ctx;

    protected RequestHandler(ServerContext ctx) => Ctx = ctx;

    /// <summary>Name der Anfrage (Feld "op", klein geschrieben).</summary>
    public abstract string Op { get; }

    public abstract NetResponse Handle(Connection conn, NetRequest r);

    // ---- Hilfen für die Handler

    protected int Online => Ctx.Sessions.Count;

    protected NetResponse Fail(string error) => new() { ok = false, error = error, online = Online };

    protected Session SessionOf(Connection conn, NetRequest r) => Ctx.Sessions.Of(conn, r.session);

    /// <summary>Startkonfiguration (Startautos, Spot, Geld, UDP-Port, Begrüßung) + Radio-Zeitbasis eintragen.</summary>
    protected NetResponse WithServerInfo(NetResponse resp)
    {
        var cfg = Ctx.Config;
        resp.starterCars = cfg.starterCars;
        resp.startSpot = cfg.startSpot;
        resp.startIsland = cfg.startIsland;
        resp.startMoney = cfg.startMoney;
        resp.udpPort = cfg.udpPort;
        resp.motd = cfg.motd;
        resp.online = Online;
        return WithRadio(resp);
    }

    /// <summary>Radio-Zeitbasis (Epoche, Startwert, Serverzeit) eintragen.</summary>
    protected NetResponse WithRadio(NetResponse resp)
    {
        resp.radioEpoch = Ctx.Radio.Epoch;
        resp.radioSeed = Ctx.Radio.Seed;
        resp.serverTime = RadioClock.NowMs();
        return resp;
    }
}
