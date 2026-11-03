using System.Net;
using TDU2Server.Core;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Udp;

/// <summary>
/// Relay-Umschlag für P2P-Clients: [10][u16 playerId][u32 udpKey][u8 innerer Typ][u8 n][n × u16 Empfänger][Nutzdaten].
/// Innerer Typ 2 = Zustand, 6 = Verkehr. Der Server leitet [innerer Typ][u16 playerId][Nutzdaten] NUR an die genannten
/// Mitspieler weiter (die der Client nicht direkt erreicht) – plus an Mitspieler derselben Lobby ohne P2P (ältere Clients).
/// Mit n = 0 meldet der Client nur seinen Zustand (Position/Insel für Spielerliste und Lobby, ~2/s), ohne Weiterleitung.
/// So bezahlt der Server nur den Verkehr der Spieler, bei denen Hole-Punching nicht klappt.
/// </summary>
public sealed class RelayPacket : UdpPacketHandler
{
    const double RelayTimeout = 15;

    public RelayPacket(ServerContext ctx, UdpServer udp) : base(ctx, udp) { }

    public override byte Type => UdpPacketIds.RelayIn;
    public override int MinLength => HeaderSize + 2;
    public override int MaxLength => 1400;

    public override Task HandleAsync(byte[] d, IPEndPoint from)
    {
        int pid = PlayerIdOf(d);
        var s = Ctx.Sessions.Authenticate(pid, KeyOf(d));
        if (s == null) return Task.CompletedTask;
        s.UdpEndPoint = from;
        s.LastUdp = DateTime.UtcNow;
        s.P2P = Ctx.Config.p2pEnabled;

        byte inner = d[HeaderSize];
        int n = d[HeaderSize + 1];
        int list = HeaderSize + 2;
        int payload = list + 2 * n;
        if (payload > d.Length || (inner != UdpPacketIds.StateOut && inner != UdpPacketIds.TrafficOut)) return Task.CompletedTask;
        int plen = d.Length - payload;
        if (inner == UdpPacketIds.StateOut) { PlayerStatePacket.ApplyState(Ctx, s, d, payload, plen); s.RelayCount++; }

        var o = new byte[3 + plen];
        WriteHeader(o, inner, pid);
        Buffer.BlockCopy(d, payload, o, 3, plen);
        var now = DateTime.UtcNow;
        var sent = new HashSet<int>();
        // ausdrücklich genannte Empfänger (keine Direktverbindung)
        for (int i = 0; i < n; i++)
        {
            int t = d[list + 2 * i] | (d[list + 2 * i + 1] << 8);
            if (t == pid || !sent.Add(t) || !Ctx.Sessions.TryGetPlayer(t, out var r)) continue;
            if (r.UdpEndPoint == null || !r.UdpActive(now, RelayTimeout) || !LobbyManager.SameLobby(s, r)) continue;
            Udp.Send(o, r.UdpEndPoint);
            Udp.CountRelay(o.Length);
        }
        // Mitspieler ohne P2P (ältere Clients / P2P abgeschaltet) bekommen weiter alles über den Server
        float range = inner == UdpPacketIds.TrafficOut ? Ctx.Config.trafficRange : 0f;
        foreach (var r in Ctx.Sessions.Players)
        {
            if (r == s || r.P2P || sent.Contains(r.PlayerId) || r.UdpEndPoint == null || !r.UdpActive(now, RelayTimeout) || !LobbyManager.SameLobby(s, r)) continue;
            if (range > 0f && s.InCar && r.InCar)
            {
                float dx = r.Px - s.Px, dz = r.Pz - s.Pz;
                if (dx * dx + dz * dz > range * range) continue;
            }
            Udp.Send(o, r.UdpEndPoint);
            Udp.CountRelay(o.Length);
        }
        return Task.CompletedTask;
    }
}
