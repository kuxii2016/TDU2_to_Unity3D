using System.Collections.Concurrent;
using System.Security.Cryptography;
using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Sessions;

/// <summary>Alle angemeldeten Spieler (nach Sitzungs-ID und Spielernummer).</summary>
public sealed class SessionManager
{
    readonly ConcurrentDictionary<string, Session> _byId = new();
    readonly ConcurrentDictionary<int, Session> _byPlayerId = new();
    int _nextPlayerId = 1;

    public int Count => _byId.Count;
    public IEnumerable<Session> All => _byId.Values;
    /// <summary>Für UDP-Verteilung (nach Spielernummer registriert).</summary>
    public IEnumerable<Session> Players => _byPlayerId.Values;

    public bool TryGetPlayer(int playerId, out Session s) => _byPlayerId.TryGetValue(playerId, out s);
    public bool IsOnline(int playerId) => _byPlayerId.ContainsKey(playerId);

    /// <summary>Spieler zu UDP-Nummer + Schlüssel (null, wenn unbekannt oder Schlüssel falsch).</summary>
    public Session Authenticate(int playerId, uint udpKey) =>
        _byPlayerId.TryGetValue(playerId, out var s) && s.UdpKey == udpKey ? s : null;

    /// <summary>Sitzung zur ID – nur, wenn sie zu dieser Verbindung gehört.</summary>
    public Session Of(Connection conn, string id)
    {
        if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var s)) return null;
        return s.Conn == conn ? s : null;
    }

    /// <summary>Neue Sitzung für ein Konto; eine alte Sitzung desselben Kontos (z.B. nach Absturz) wird beendet.</summary>
    public Session Start(Connection conn, Account a)
    {
        foreach (var old in _byId.Values.Where(x => string.Equals(x.Nick, a.nick, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _byId.TryRemove(old.Id, out _);
            _byPlayerId.TryRemove(old.PlayerId, out _);
            EndRide(old);
            if (old.Conn != conn) { try { old.Conn?.Client?.Close(); } catch { } }
            Log.Info($"{old.Nick}: alte Sitzung beendet");
        }
        if (conn.Session != null)
        {
            _byId.TryRemove(conn.Session.Id, out _);
            _byPlayerId.TryRemove(conn.Session.PlayerId, out _);
        }

        var sv = a.save;
        var s = new Session
        {
            Id = Credentials.NewToken(), Nick = a.nick, Conn = conn,
            PlayerId = NewPlayerId(), UdpKey = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)),
            Money = sv?.money ?? 0, NbCars = sv?.ownedCars?.Count ?? 0, Nationality = a.nationality,
            Island = Islands.Code(sv?.island), Avatar = sv?.avatar ?? "",
            Mood = sv?.myLife?.mood ?? "", MoodTxt = sv?.myLife?.moodText ?? "", PhotoVer = sv?.myLife?.photoVer ?? 0,
        };
        conn.Session = s;
        _byId[s.Id] = s;
        _byPlayerId[s.PlayerId] = s;
        return s;
    }

    /// <summary>Sitzung entfernen (Abmelden/Trennen) und eine Mitfahrt beenden; false, wenn sie schon weg war.</summary>
    public bool Remove(Session s)
    {
        if (s == null || !_byId.TryRemove(s.Id, out _)) return false;
        _byPlayerId.TryRemove(s.PlayerId, out _);
        EndRide(s);
        return true;
    }

    /// <summary>Mitfahrt beenden (Beifahrer steigt aus bzw. Fahrer geht offline) – der Partner bekommt "rideend".</summary>
    public void EndRide(Session s)
    {
        if (s.RideWith != 0 && _byPlayerId.TryGetValue(s.RideWith, out var host) && host.Passenger == s.PlayerId)
        {
            host.Passenger = 0;
            host.Notice($"rideend:{s.PlayerId}:{s.Nick}");
        }
        if (s.Passenger != 0 && _byPlayerId.TryGetValue(s.Passenger, out var pas) && pas.RideWith == s.PlayerId)
        {
            pas.RideWith = 0;
            pas.Notice($"rideend:{s.PlayerId}:{s.Nick}");
        }
        if (s.RideWith != 0 || s.Passenger != 0) Log.Info($"{s.Nick}: Mitfahrt beendet");
        s.RideWith = 0;
        s.Passenger = 0;
    }

    int NewPlayerId()
    {
        for (int i = 0; i < 65535; i++)
        {
            int id = Interlocked.Increment(ref _nextPlayerId) & 0xFFFF;
            if (id != 0 && !_byPlayerId.ContainsKey(id)) return id;
        }
        return 1;
    }
}
