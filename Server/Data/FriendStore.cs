using System.Text.Json;

namespace TDU2Server.Data;

/// <summary>Freundesdaten eines Kontos (data/friends.json, Schlüssel = Name klein geschrieben).</summary>
public sealed class FriendRecord
{
    public List<string> friends = new();    // bestätigte Freunde
    public List<string> incoming = new();   // Anfragen an mich (warten auf meine Antwort)
    public List<string> outgoing = new();   // meine Anfragen (warten auf die Antwort des anderen)
}

/// <summary>
/// Freundesliste mit Anfrage/Antwort (wie im Spiel: CRC_E_FRIENDS_REQUEST / ANSWER / REVOKE). Anfragen bleiben gespeichert, bis der
/// Empfänger antwortet – auch wenn er beim Senden nicht online war. Freundschaft ist immer beidseitig.
/// </summary>
public sealed class FriendStore
{
    public const int MaxFriends = 200, MaxPending = 50;

    public enum RequestResult { Sent, AlreadyFriends, AlreadySent, BecameFriends, Self, Full }

    readonly string _file;
    readonly object _lock = new();
    readonly Dictionary<string, FriendRecord> _byNick = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };

    public FriendStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "friends.json");
        try
        {
            if (File.Exists(_file))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, FriendRecord>>(File.ReadAllText(_file), FileJson) ?? new())
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) _byNick[kv.Key] = kv.Value;
        }
        catch (Exception) { }
    }

    FriendRecord RecLocked(string nick)
    {
        if (!_byNick.TryGetValue(nick, out var r)) _byNick[nick] = r = new FriendRecord();
        return r;
    }

    static bool Has(List<string> l, string nick) => l.Exists(x => string.Equals(x, nick, StringComparison.OrdinalIgnoreCase));
    static void Del(List<string> l, string nick) => l.RemoveAll(x => string.Equals(x, nick, StringComparison.OrdinalIgnoreCase));

    /// <summary>Anfrage von <paramref name="from"/> an <paramref name="to"/>. Hatte <paramref name="to"/> selbst schon angefragt: sofort Freunde.</summary>
    public RequestResult Request(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return RequestResult.Self;
        lock (_lock)
        {
            var a = RecLocked(from); var b = RecLocked(to);
            if (Has(a.friends, to)) return RequestResult.AlreadyFriends;
            if (Has(a.incoming, to)) { MakeFriendsLocked(from, to); SaveLocked(); return RequestResult.BecameFriends; }
            if (Has(a.outgoing, to)) return RequestResult.AlreadySent;
            if (a.friends.Count >= MaxFriends || b.incoming.Count >= MaxPending || a.outgoing.Count >= MaxPending) return RequestResult.Full;
            a.outgoing.Add(to);
            b.incoming.Add(from);
            SaveLocked();
            return RequestResult.Sent;
        }
    }

    /// <summary>Anfrage von <paramref name="from"/> beantworten. false = es gab keine.</summary>
    public bool Answer(string me, string from, bool accept)
    {
        lock (_lock)
        {
            var a = RecLocked(me);
            if (!Has(a.incoming, from)) return false;
            if (accept) MakeFriendsLocked(me, from);
            else { Del(a.incoming, from); Del(RecLocked(from).outgoing, me); }
            SaveLocked();
            return true;
        }
    }

    /// <summary>Freundschaft beenden (beidseitig) bzw. eigene offene Anfrage zurückziehen.</summary>
    public bool Remove(string me, string other)
    {
        lock (_lock)
        {
            var a = RecLocked(me); var b = RecLocked(other);
            bool had = Has(a.friends, other) || Has(a.outgoing, other);
            Del(a.friends, other); Del(b.friends, me);
            Del(a.outgoing, other); Del(b.incoming, me);
            if (had) SaveLocked();
            return had;
        }
    }

    public FriendRecord Get(string nick)
    {
        lock (_lock)
        {
            if (!_byNick.TryGetValue(nick, out var r)) return new FriendRecord();
            return new FriendRecord { friends = new List<string>(r.friends), incoming = new List<string>(r.incoming), outgoing = new List<string>(r.outgoing) };
        }
    }

    void MakeFriendsLocked(string x, string y)
    {
        var a = RecLocked(x); var b = RecLocked(y);
        Del(a.incoming, y); Del(a.outgoing, y); Del(b.incoming, x); Del(b.outgoing, x);
        if (!Has(a.friends, y)) a.friends.Add(y);
        if (!Has(b.friends, x)) b.friends.Add(x);
    }

    void SaveLocked()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byNick, FileJson));
            File.Move(tmp, _file, true);
        }
        catch (Exception) { }
    }
}
