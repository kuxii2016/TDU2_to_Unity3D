using TDU2Server.Core;

namespace TDU2Server.Web;

/// <summary>Ringspeicher für Ereignisse, die im Panel angezeigt werden (Anmeldungen, Moderationsaktionen).</summary>
public sealed class AuditLog
{
    public readonly record struct Event(DateTime At, string Kind, string Nick, string Text);

    const int Max = 1000;
    readonly object _lock = new();
    readonly LinkedList<Event> _events = new();

    public void Add(string kind, string nick, string text)
    {
        lock (_lock)
        {
            _events.AddLast(new Event(DateTime.Now, kind, nick, text));
            while (_events.Count > Max) _events.RemoveFirst();
        }
    }

    /// <summary>Neueste zuerst, optional gefiltert nach Art ("login", "mod", "chat", "kick" …).</summary>
    public List<Event> Recent(int count, string kind = null)
    {
        lock (_lock)
        {
            IEnumerable<Event> q = _events;
            if (!string.IsNullOrEmpty(kind)) q = q.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
            return q.Reverse().Take(count).ToList();
        }
    }
}
