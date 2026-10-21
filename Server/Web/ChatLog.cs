using System.Collections.Concurrent;

namespace TDU2Server.Web;

/// <summary>Chat-Verlauf im Speicher (neueste zuletzt). Wird vom ChatPacket gespeist.</summary>
public sealed class ChatLog
{
    public readonly record struct Line(DateTime At, int PlayerId, string Nick, string Text);

    const int Max = 5000;
    readonly object _lock = new();
    readonly LinkedList<Line> _lines = new();

    public void Add(int playerId, string nick, string text)
    {
        lock (_lock)
        {
            _lines.AddLast(new Line(DateTime.Now, playerId, nick, text));
            while (_lines.Count > Max) _lines.RemoveFirst();
        }
    }

    /// <summary>Neueste zuerst.</summary>
    public List<Line> Recent(int count)
    {
        lock (_lock) return _lines.Reverse().Take(count).ToList();
    }
}
