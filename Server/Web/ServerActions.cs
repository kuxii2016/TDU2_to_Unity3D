using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Sessions;

namespace TDU2Server.Web;

/// <summary>
/// Moderationsaktionen des Web-Panels auf dem Server (Kicken, Meldungen). Nutzt nur öffentliche
/// Dienste des ServerContext und ist damit unabhängig von TCP/UDP-Interna.
/// </summary>
public sealed class ServerActions
{
    readonly ServerContext _ctx;

    public ServerActions(ServerContext ctx) => _ctx = ctx;

    /// <summary>Alle Online-Spieler (Momentaufnahme).</summary>
    public List<Session> Players() => _ctx.Sessions.All.ToList();

    /// <summary>Spieler über die Spielernummer finden.</summary>
    public Session Find(int playerId) => _ctx.Sessions.TryGetPlayer(playerId, out var s) ? s : null;

    /// <summary>
    /// Meldung an alle oder an eine Insel (code) bzw. an einen einzelnen Spieler.
    /// Der Client zeigt sie als News-Fenster (pop_news) und im Chat – wie der Konsolenbefehl "say".
    /// </summary>
    public int Message(string text, int playerId = 0, int island = 0)
    {
        text = Normalize(text);
        if (text.Length == 0) return 0;
        int n = 0;
        foreach (var s in _ctx.Sessions.Players.ToList())
        {
            if (playerId != 0 && s.PlayerId != playerId) continue;
            if (island != 0 && s.Island != island) continue;
            s.Notice("news:0:" + text);
            n++;
        }
        return n;
    }

    /// <summary>Private Meldung an genau einen Spieler (erscheint als News + Chat mit Namen-Präfix).</summary>
    public bool DirectMessage(int playerId, string from, string text)
    {
        var s = Find(playerId);
        if (s == null) return false;
        text = Normalize(text);
        if (text.Length == 0) return false;
        s.Notice($"news:0:[{from}] {text}");
        return true;
    }

    /// <summary>Spieler trennen (TCP schließen; der Server räumt die Sitzung automatisch auf).</summary>
    public bool Kick(int playerId, string reason)
    {
        var s = Find(playerId);
        if (s == null) return false;
        try { s.Notice("news:0:Du wurdest vom Server getrennt:" + Normalize(reason)); } catch { }
        try { s.Conn?.Client?.Close(); } catch { }
        Log.Info($"[Mod] {s.Nick} (#{s.PlayerId}) gekickt: {reason}");
        return true;
    }

    /// <summary>Konto sperren (Ban) und den Spieler sofort trennen.</summary>
    public bool Ban(int playerId, string reason, TimeSpan duration)
    {
        var s = Find(playerId);
        if (s == null) return false;
        var a = _ctx.Accounts.Load(s.Nick);
        if (a == null) return false;
        a.bannedUntil = DateTime.UtcNow.Add(duration).ToString("o");
        a.banReason = Normalize(reason);
        _ctx.Accounts.Store(a);
        Log.Info($"[Mod] {s.Nick} für {duration.TotalHours:0.#}h gesperrt: {reason}");
        Kick(playerId, "gesperrt: " + reason);
        return true;
    }

    /// <summary>Konto entsperren.</summary>
    public bool Unban(string nick)
    {
        var a = _ctx.Accounts.Load(nick);
        if (a == null) return false;
        a.bannedUntil = "";
        a.banReason = "";
        _ctx.Accounts.Store(a);
        return true;
    }

    static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length > 200 ? text[..200] : text;
    }
}
