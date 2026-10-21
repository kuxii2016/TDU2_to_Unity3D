using TDU2Server.Net.Tcp;
using TDU2Server.Net.Udp;
using TDU2Server.Web;

namespace TDU2Server.Core;

/// <summary>Startet TCP (Konten/Spielstände), UDP (Synchronisation), das Web-Panel und die Konsole.</summary>
public sealed class GameServer
{
    readonly ServerContext _ctx;
    readonly TcpServer _tcp;
    readonly UdpServer _udp;
    readonly ConsoleCommands _console;
    readonly WebPanel _web;

    public GameServer(string configFile)
    {
        _ctx = new ServerContext(configFile);
        _tcp = new TcpServer(_ctx);
        _udp = new UdpServer(_ctx);
        _web = new WebPanel(_ctx);
        _console = new ConsoleCommands(_ctx, Stop);
    }

    public async Task RunAsync()
    {
        var cfg = _ctx.Config;
        Log.Info($"Radio-Zeitbasis: Epoche {_ctx.Radio.Epoch}, Startwert {_ctx.Radio.Seed}");
        _tcp.Start();
        Log.Info($"TDU2Server läuft – TCP {cfg.tcpPort}, Daten in {Path.GetFullPath(cfg.dataDir)}, {_ctx.Accounts.Count()} Konten");
        Log.Info($"Start: Spot {cfg.startSpot} ({cfg.startIsland}), Startautos: {string.Join(", ", cfg.starterCars)}");
        _udp.Start();
        _web.Start();
        Log.Info("Befehle: " + ConsoleCommands.Help);
        _console.Start();
        await _tcp.AcceptLoopAsync();
        _udp.Stop();
        _web.Stop();
        Log.Info("Server beendet.");
    }

    void Stop() => _tcp.Stop();
}
