using System.Net;
using TDU2Server.Core;

namespace TDU2Server.Net.Udp;

/// <summary>
/// Ping: Client [7][u16 playerId][u32 udpKey][u32 Clientzeit ms] -&gt; Antwort [8][u16 playerId][u32 Clientzeit][u16 Lobby][u16 Spieler in der Lobby].
/// Der Client misst damit die Laufzeit (RTT) und hält die NAT-Zuordnung offen, auch wenn er gerade keinen Zustand sendet
/// (Interieur, Menü). Der Server merkt sich dabei den aktuellen Absender (Portwechsel durch NAT/CGNAT).
/// </summary>
public sealed class PingPacket : UdpPacketHandler
{
    public PingPacket(ServerContext ctx, UdpServer udp) : base(ctx, udp) { }

    public override byte Type => UdpPacketIds.PingIn;
    public override int MinLength => HeaderSize + 4;
    public override int MaxLength => 64;

    public override Task HandleAsync(byte[] d, IPEndPoint from)
    {
        int pid = PlayerIdOf(d);
        var s = Ctx.Sessions.Authenticate(pid, KeyOf(d));
        if (s == null) return Task.CompletedTask;
        s.UdpEndPoint = from;
        s.LastUdp = DateTime.UtcNow;
        var lobby = Ctx.Lobbies.Get(s.Lobby);
        int n = lobby?.Count ?? 1;
        var o = new byte[3 + 4 + 4];
        WriteHeader(o, UdpPacketIds.PongOut, pid);
        Buffer.BlockCopy(d, HeaderSize, o, 3, 4);
        o[7] = (byte)s.Lobby; o[8] = (byte)(s.Lobby >> 8);
        o[9] = (byte)n; o[10] = (byte)(n >> 8);
        Udp.Send(o, from);
        return Task.CompletedTask;
    }
}
