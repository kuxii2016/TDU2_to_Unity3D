using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"status","session":..} -> Spielerliste, Radio-Zeitbasis; für angemeldete Spieler zusätzlich
/// offene Einladungen, Meldungen ("accepted:", "declined:", "joined:", "rideend:") und die Mitfahrt.
/// Lobbys: die Spielerliste enthält nur die eigene Lobby (plus Mitfahrt-Partner); dazu Lobby-Nummer, Anzahl, Höchstzahl und
/// der Abfrageabstand (config.json statusIntervalMs), damit Client und Server dieselben Einstellungen benutzen.
/// </summary>
public sealed class StatusHandler : RequestHandler
{
    const double InviteTimeout = 60;   // Sekunden
    const double InCarTimeout = 10;    // ohne UDP gilt der Spieler als nicht im Auto

    public StatusHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "status";

    static DateTime _lastRecent = DateTime.MinValue;

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        var resp = WithRadio(new NetResponse { ok = true, online = Online, nick = me?.Nick ?? "", playerId = me?.PlayerId ?? 0, newsLatest = Ctx.News.Latest });
        var now = DateTime.UtcNow;
        var cfg = Ctx.Config;
        resp.statusIntervalMs = cfg.statusIntervalMs;
        if (me != null)
        {
            me.LastStatus = now;
            me.P2P = cfg.p2pEnabled && r.p2p;
            if (r.localEp != null && r.localEp.Length <= 64) me.LocalEp = r.localEp;
            resp.p2pEnabled = me.P2P;
            resp.p2pToken = me.P2PToken;
        }
        resp.lobbyMax = cfg.maxPlayersPerLobby;
        foreach (var s in Ctx.Sessions.All)
        {
            // eigene Lobby (bzw. ohne Anmeldung alle); Mitfahrt-Partner immer
            bool visible = me == null || s == me || LobbyManager.SameLobby(me, s) || s.PlayerId == me.RideWith || s.PlayerId == me.Passenger;
            if (!visible) continue;
            var e = Entry(s, now);
            // P2P-Vermittlung: Endpunkte nur innerhalb der Lobby und nur, wenn beide P2P können
            if (me != null && s != me && me.P2P && s.P2P && s.UdpEndPoint != null && LobbyManager.SameLobby(me, s))
            {
                e.ep = s.UdpEndPoint.ToString();
                e.lep = s.LocalEp ?? "";
                e.p2p = s.P2PToken;
            }
            resp.players.Add(e);
        }
        if (me != null)
        {
            resp.lobby = me.Lobby;
            resp.lobbyCount = Ctx.Lobbies.Get(me.Lobby)?.Count ?? 1;
            if ((now - _lastRecent).TotalSeconds > 60)
            {
                _lastRecent = now;
                foreach (var s in Ctx.Sessions.All) Ctx.Recent.Touch(s.Nick, s.CarId, s.Color, s.NbCars, s.Money, s.Nationality, false);
            }
        }
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
