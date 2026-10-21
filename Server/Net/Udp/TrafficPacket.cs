using System.Net;
using TDU2Server.Core;

namespace TDU2Server.Net.Udp;

/// <summary>
/// Verkehr: Client [5][u16 playerId][u32 udpKey][Nutzdaten] -> an alle anderen derselben Insel [6][u16 playerId][Nutzdaten].
/// Die Clients simulieren die Verkehrsautos selbst (jedes Auto gehört einem Spieler); der Server reicht nur weiter (Format: Client TDU2Traffic).
/// </summary>
public sealed class TrafficPacket : UdpPacketHandler
{
    public const byte In = 5, Out = 6;
    const double RelayTimeout = 15;

    readonly byte[] _out = new byte[1500];

    public TrafficPacket(ServerContext ctx, UdpServer udp) : base(ctx, udp) { }

    public override byte Type => In;
    public override int MinLength => 10;
    public override int MaxLength => 1300;

    public override async Task HandleAsync(byte[] d, IPEndPoint from)
    {
        int pid = PlayerIdOf(d);
        var s = Ctx.Sessions.Authenticate(pid, KeyOf(d));
        if (s == null) return;
        s.UdpEndPoint = from;

        int payload = d.Length - HeaderSize;
        WriteHeader(_out, Out, pid);
        Buffer.BlockCopy(d, HeaderSize, _out, 3, payload);
        await Udp.RelayAsync(s, new ReadOnlyMemory<byte>(_out, 0, 3 + payload), RelayTimeout);
    }
}
