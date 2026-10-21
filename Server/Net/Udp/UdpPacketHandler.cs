using System.Net;
using TDU2Server.Core;

namespace TDU2Server.Net.Udp;

/// <summary>Basis für einen UDP-Pakettyp (erstes Byte). Jeder Typ hat seine eigene Klasse.</summary>
public abstract class UdpPacketHandler
{
    protected readonly ServerContext Ctx;
    protected readonly UdpServer Udp;

    protected UdpPacketHandler(ServerContext ctx, UdpServer udp)
    {
        Ctx = ctx;
        Udp = udp;
    }

    /// <summary>Pakettyp (Byte 0).</summary>
    public abstract byte Type { get; }
    public abstract int MinLength { get; }
    public abstract int MaxLength { get; }

    public abstract Task HandleAsync(byte[] d, IPEndPoint from);

    /// <summary>Kopf aller Client-Pakete: [Typ][u16 playerId][u32 udpKey] (7 Byte).</summary>
    protected const int HeaderSize = 7;

    protected static int PlayerIdOf(byte[] d) => d[1] | (d[2] << 8);
    protected static uint KeyOf(byte[] d) => BitConverter.ToUInt32(d, 3);

    /// <summary>Kopf der Server-Pakete: [Typ][u16 playerId] (3 Byte).</summary>
    protected static void WriteHeader(byte[] o, byte type, int pid)
    {
        o[0] = type;
        o[1] = (byte)pid;
        o[2] = (byte)(pid >> 8);
    }
}
