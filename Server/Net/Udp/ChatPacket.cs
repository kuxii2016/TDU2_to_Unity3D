using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TDU2Server.Core;

namespace TDU2Server.Net.Udp;

/// <summary>
/// Chat: Client [4][u16 playerId][u32 udpKey][u32 Nachrichtennr.][Text] -> an alle (auch den Absender) [3][u16 playerId][u32 Nr.][Name][0][Text].
/// Clients senden jede Nachricht doppelt; der Server verwirft die zweite Kopie und sendet selbst ebenfalls zweimal.
/// </summary>
public sealed class ChatPacket : UdpPacketHandler
{
    public const byte In = 4, Out = 3;
    const int MaxText = 120;
    const double ActiveTimeout = 30;

    readonly ConcurrentDictionary<int, uint> _lastMessage = new();

    public ChatPacket(ServerContext ctx, UdpServer udp) : base(ctx, udp) { }

    public override byte Type => In;
    public override int MinLength => 12;
    public override int MaxLength => 600;

    public override async Task HandleAsync(byte[] d, IPEndPoint from)
    {
        int pid = PlayerIdOf(d);
        var s = Ctx.Sessions.Authenticate(pid, KeyOf(d));
        if (s == null) return;
        uint msg = BitConverter.ToUInt32(d, 7);
        s.UdpEndPoint ??= from;
        if (_lastMessage.TryGetValue(pid, out var last) && last == msg) return;   // zweite Kopie
        _lastMessage[pid] = msg;

        string text = Encoding.UTF8.GetString(d, 11, d.Length - 11).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxText) text = text[..MaxText];
        Log.Info($"[Chat] {s.Nick}: {text}");
        Ctx.Chat.Add(pid, s.Nick, text);
        Ctx.Audit.Add("chat", s.Nick, text);

        var nick = Encoding.UTF8.GetBytes(s.Nick);
        var t = Encoding.UTF8.GetBytes(text);
        var o = new byte[7 + nick.Length + 1 + t.Length];
        WriteHeader(o, Out, pid);
        Buffer.BlockCopy(d, 7, o, 3, 4);
        Buffer.BlockCopy(nick, 0, o, 7, nick.Length);
        Buffer.BlockCopy(t, 0, o, 8 + nick.Length, t.Length);

        var now = DateTime.UtcNow;
        foreach (var x in Ctx.Sessions.Players)
        {
            var ep = x == s ? (x.UdpEndPoint ?? from) : x.UdpEndPoint;
            if (ep == null || (x != s && !x.UdpActive(now, ActiveTimeout))) continue;
            for (int i = 0; i < 2; i++) await Udp.SendAsync(o, ep);   // UDP kann Pakete verlieren; Clients verwerfen Doppelte
        }
    }
}
