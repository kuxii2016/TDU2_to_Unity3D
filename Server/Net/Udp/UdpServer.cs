using System.Net;
using System.Net.Sockets;
using TDU2Server.Core;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Udp;

/// <summary>UDP-Server (Standard-Port 7778): empfängt Pakete und verteilt sie nach Typ (Byte 0) an ihre Klasse.</summary>
public sealed class UdpServer
{
    const int SIO_UDP_CONNRESET = -1744830452;

    readonly ServerContext _ctx;
    readonly Dictionary<byte, UdpPacketHandler> _handlers = new();
    UdpClient _udp;
    volatile bool _running;

    public UdpServer(ServerContext ctx)
    {
        _ctx = ctx;
        Add(new PlayerStatePacket(ctx, this));
        Add(new ChatPacket(ctx, this));
        Add(new TrafficPacket(ctx, this));
    }

    void Add(UdpPacketHandler h) => _handlers[h.Type] = h;

    public void Start()
    {
        int port = _ctx.Config.udpPort;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }   // Windows: ICMP-Fehler ignorieren
            _running = true;
            _ = Task.Run(LoopAsync);
            Log.Info($"UDP-Synchronisation auf Port {port}");
        }
        catch (Exception ex) { Log.Info("UDP-Port " + port + " nicht nutzbar: " + ex.Message); }
    }

    public void Stop()
    {
        _running = false;
        try { _udp?.Dispose(); } catch { }
    }

    async Task LoopAsync()
    {
        while (_running)
        {
            UdpReceiveResult rr;
            try { rr = await _udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var d = rr.Buffer;
            if (d.Length == 0 || !_handlers.TryGetValue(d[0], out var h)) continue;
            if (d.Length < h.MinLength || d.Length > h.MaxLength) continue;
            try { await h.HandleAsync(d, rr.RemoteEndPoint); }
            catch (Exception ex) { Log.Info($"UDP {rr.RemoteEndPoint}: {ex.Message}"); }
        }
    }

    /// <summary>Ein Paket senden (Fehler werden ignoriert).</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint to)
    {
        try { await _udp.SendAsync(data, to); } catch (Exception) { }
    }

    /// <summary>An alle anderen aktiven Spieler derselben Insel weiterleiten.</summary>
    public async Task RelayAsync(Session sender, ReadOnlyMemory<byte> data, double activeSeconds)
    {
        var now = DateTime.UtcNow;
        foreach (var o in _ctx.Sessions.Players)
        {
            if (o == sender || o.UdpEndPoint == null || !o.UdpActive(now, activeSeconds) || !Islands.Same(sender.Island, o.Island)) continue;
            await SendAsync(data, o.UdpEndPoint);
        }
    }
}
