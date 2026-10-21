using System.Net;
using TDU2Server.Core;

namespace TDU2Server.Net.Udp;

/// <summary>
/// Spielerzustand (~20/s): Client [1][u16 playerId][u32 udpKey][Zustand] -> an alle anderen derselben Insel [2][u16 playerId][Zustand].
/// Zustand: +0 u32 seq, +4 i32 carId, +8 u8 Farbe, +9 u8 Flags (Bit 0 = im Auto), +10 u8 Licht, +11 frei, +12.. Floats (Px +12, Pz +20),
/// +80 u8 Insel (1 Ibiza, 2 Hawaii, 0 unbekannt). Format: Client TDU2Multiplayer.
/// </summary>
public sealed class PlayerStatePacket : UdpPacketHandler
{
    public const byte In = 1, Out = 2;
    const int StateMin = 12;          // seq + carId + Farbe/Flags/Licht/frei
    const double RelayTimeout = 15;   // Empfänger ohne UDP seit … s bekommen nichts

    readonly byte[] _out = new byte[1500];

    public PlayerStatePacket(ServerContext ctx, UdpServer udp) : base(ctx, udp) { }

    public override byte Type => In;
    public override int MinLength => HeaderSize + StateMin;
    public override int MaxLength => 1200;

    public override async Task HandleAsync(byte[] d, IPEndPoint from)
    {
        int pid = PlayerIdOf(d);
        var s = Ctx.Sessions.Authenticate(pid, KeyOf(d));
        if (s == null) return;
        s.UdpEndPoint = from;
        s.LastUdp = DateTime.UtcNow;

        const int st = HeaderSize;
        s.CarId = BitConverter.ToInt32(d, st + 4);
        s.Color = d[st + 8];
        s.InCar = (d[st + 9] & 1) != 0;
        if (d.Length >= st + 24)
        {
            s.Px = BitConverter.ToSingle(d, st + 12);
            s.Pz = BitConverter.ToSingle(d, st + 20);
        }
        if (d.Length >= st + 81 && d[st + 80] != 0 && d[st + 80] != s.Island)
        {
            s.Island = d[st + 80];
            Log.Info($"{s.Nick}: Insel {Islands.Name(s.Island)}");
        }

        int len = 3 + (d.Length - st);
        WriteHeader(_out, Out, pid);
        Buffer.BlockCopy(d, st, _out, 3, d.Length - st);
        await Udp.RelayAsync(s, new ReadOnlyMemory<byte>(_out, 0, len), RelayTimeout);
    }
}
