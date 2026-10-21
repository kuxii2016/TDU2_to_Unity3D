using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"status","session":..} -> Spielerliste, Radio-Zeitbasis; für angemeldete Spieler zusätzlich
/// offene Einladungen, Meldungen ("accepted:", "declined:", "joined:", "rideend:") und die Mitfahrt.
/// </summary>
public sealed class StatusHandler : RequestHandler
{
    const double InviteTimeout = 60;   // Sekunden
    const double InCarTimeout = 10;    // ohne UDP gilt der Spieler als nicht im Auto

    public StatusHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "status";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        var resp = WithRadio(new NetResponse { ok = true, online = Online, nick = me?.Nick ?? "", playerId = me?.PlayerId ?? 0 });
        var now = DateTime.UtcNow;
        foreach (var s in Ctx.Sessions.All) resp.players.Add(Entry(s, now));
        if (me != null)
        {
            lock (me.Lock)
            {
                me.Invites.RemoveAll(i => (now - i.At).TotalSeconds > InviteTimeout || !Ctx.Sessions.IsOnline(i.From));
                foreach (var i in me.Invites) resp.invites.Add(new InviteEntry { from = i.From, nick = i.Nick, kind = i.Kind });
                resp.notices.AddRange(me.Notices);
                me.Notices.Clear();
            }
            resp.rideWith = me.RideWith;
            resp.passenger = me.Passenger;
        }
        return resp;
    }

    static PlayerEntry Entry(Session s, DateTime now) => new()
    {
        id = s.PlayerId, nick = s.Nick, carId = s.CarId, color = s.Color, money = s.Money, nbCars = s.NbCars,
        inCar = s.InCar && (now - s.LastUdp).TotalSeconds < InCarTimeout, px = s.Px, pz = s.Pz, nationality = s.Nationality,
        island = s.Island, avatar = s.Avatar ?? "", rideWith = s.RideWith, passenger = s.Passenger,
        mood = s.Mood ?? "", moodTxt = s.MoodTxt ?? "", photoVer = s.PhotoVer,
    };
}
