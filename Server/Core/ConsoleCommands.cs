namespace TDU2Server.Core;

/// <summary>Konsolenbefehle: list, accounts, reload, say, quit.</summary>
public sealed class ConsoleCommands
{
    public const string Help = "list, accounts, reload, say <Text>, panel, quit";

    readonly ServerContext _ctx;
    readonly Action _stop;
    volatile bool _running = true;

    public ConsoleCommands(ServerContext ctx, Action stop)
    {
        _ctx = ctx;
        _stop = stop;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    async Task LoopAsync()
    {
        while (_running)
        {
            string line = await Task.Run(() => Console.ReadLine());
            if (line == null) { await Task.Delay(500); continue; }
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0].ToLowerInvariant())
            {
                case "list": ListOnline(); break;
                case "accounts": foreach (var n in _ctx.Accounts.Names()) Log.Info("  " + n); break;
                case "reload":
                    _ctx.Config = ServerConfig.Load(_ctx.ConfigFile);
                    Log.Info("Konfiguration neu geladen.");
                    break;
                case "quit":
                case "exit":
                    _running = false;
                    _stop();
                    break;
                case "say":
                    Say(line.Trim().Substring(p[0].Length).Trim());
                    break;
                case "panel":
                    Log.Info(_ctx.Config.webEnabled
                        ? $"Web-Panel: http://{_ctx.Config.webHost}:{_ctx.Config.webPort} (Anmeldung mit Konto, Rolle helper/mod/admin)"
                        : "Web-Panel ist in config.json abgeschaltet (webEnabled=false).");
                    break;
                default: Log.Info("Befehle: " + Help); break;
            }
        }
    }

    /// <summary>
    /// Meldung an alle angemeldeten Spieler: Status-Meldung "news:0:&lt;Text&gt;" (kommt mit der nächsten Status-Abfrage, ~1–2 s).
    /// Der Client zeigt sie im Flash-Fenster pop_news (TDU2UIManager.SendNews) und im Chat.
    /// </summary>
    void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { Log.Info("say <Text>  – Meldung an alle Spieler"); return; }
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        if (text.Length > 200) text = text.Substring(0, 200);
        int n = 0;
        foreach (var s in _ctx.Sessions.Players.ToList()) { s.Notice("news:0:" + text); n++; }
        Log.Info($"Meldung an {n} Spieler: {text}");
    }

    void ListOnline()
    {
        var all = _ctx.Sessions.All.ToList();
        Log.Info($"{all.Count} online");
        foreach (var s in all)
            Log.Info($"  #{s.PlayerId} {s.Nick}  ({s.Conn?.Remote}, UDP {s.UdpEndPoint?.ToString() ?? "-"}, Auto {s.CarId}, Insel {Islands.Name(s.Island)}, seit {s.Since:HH:mm:ss})");
    }
}
