using TDU2Server.Net.Tcp;
using TDU2Server.Net.Udp;
using TDU2Server.Web;

namespace TDU2Server.Core;

/// <summary>Startet TCP (Konten/Spielstände), UDP (Synchronisation), das Web-Panel, die Master-Anmeldung und die Konsole.</summary>
public sealed class GameServer
{
    readonly ServerContext _ctx;
    readonly TcpServer _tcp;
    readonly UdpServer _udp;
    readonly MasterAdvertiser _master;
    readonly ConsoleCommands _console;
    readonly WebPanel _web;

    public GameServer(string configFile)
    {
        _ctx = new ServerContext(configFile);
        _tcp = new TcpServer(_ctx);
        _udp = new UdpServer(_ctx);
        _ctx.Udp = _udp;
        _master = new MasterAdvertiser(_ctx);
        _ctx.Master = _master;
        _web = new WebPanel(_ctx);
        _console = new ConsoleCommands(_ctx, RequestStop);
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
        // Erst anmelden, wenn TCP/UDP stehen – der Launcher zeigt den Server sonst als erreichbar, obwohl er es nicht ist.
        _master.Start();
        Log.Info("Befehle: " + ConsoleCommands.Help);
        _console.Start();
        try
        {
            await _tcp.AcceptLoopAsync();
        }
        finally
        {
            _udp.Stop();
            _web.Stop();
            // Zuerst die eigenen Dienste schließen, dann beim Master abmelden (sauberer Eintrag in der Liste).
            await _master.StopAsync();
            Log.Info("Server beendet.");
        }
    }

    /// <summary>Server beenden (Konsole "quit", Ctrl+C, SIGTERM) – der Shutdown läuft danach sauber durch.</summary>
    public void RequestStop() => _tcp.Stop();
}
