using TDU2Server.Core;

namespace TDU2Server.Sessions;

/// <summary>
/// Verwaltet die Lobbys (Server entscheidet, Clients fragen per "status" nach den Spielern ihrer Lobby):
///  - Anmeldung / Inselwechsel: in die vollste Lobby der Insel mit freiem Platz, sonst eine neue.
///  - Einladung angenommen / Mitfahrt: der Gast kommt in die Lobby des Gastgebers (auch wenn sie dadurch voll ist,
///    damit Gruppen zusammenbleiben).
///  - Leere Lobbys werden entfernt.
/// maxPlayersPerLobby &lt;= 0 = eine Lobby je Insel (unbegrenzt).
/// </summary>
public sealed class LobbyManager
{
    readonly Func<ServerConfig> _config;
    readonly object _lock = new();
    readonly Dictionary<int, Lobby> _lobbies = new();
    int _nextId = 1;

    public LobbyManager(Func<ServerConfig> config) => _config = config;

    int Max => Math.Max(0, _config()?.maxPlayersPerLobby ?? 0);

    public List<Lobby> All() { lock (_lock) return _lobbies.Values.OrderBy(l => l.Id).ToList(); }

    public Lobby Get(int id) { lock (_lock) return _lobbies.TryGetValue(id, out var l) ? l : null; }

    /// <summary>Spieler in eine passende Lobby seiner Insel setzen (bleibt, wenn die aktuelle schon passt).</summary>
    public void Assign(Session s)
    {
        if (s == null) return;
        lock (_lock)
        {
            if (s.Lobby != 0 && _lobbies.TryGetValue(s.Lobby, out var cur) && cur.Island == s.Island) return;
            Lobby best = null;
            int max = Max;
            foreach (var l in _lobbies.Values)
            {
                if (l.Island != s.Island) continue;
                int n = l.Count;
                if (max > 0 && n >= max) continue;
                if (best == null || n > best.Count) best = l;   // vollste mit Platz: Spieler sehen sich eher
            }
            best ??= Create(s.Island);
            MoveLocked(s, best);
        }
    }

    /// <summary>Gast in die Lobby des Gastgebers holen (Einladung/Mitfahrt).</summary>
    public void JoinHost(Session guest, Session host)
    {
        if (guest == null || host == null || host.Lobby == 0) return;
        lock (_lock)
        {
            if (!_lobbies.TryGetValue(host.Lobby, out var l)) return;
            if (guest.Lobby == l.Id) return;
            MoveLocked(guest, l);
            Log.Info($"{guest.Nick}: Lobby {l.Id} (bei {host.Nick})");
        }
    }

    /// <summary>Lobby wechseln (op "lobbyjoin"); false, wenn sie voll ist, nicht existiert oder auf einer anderen Insel liegt.</summary>
    public bool Join(Session s, int lobbyId)
    {
        lock (_lock)
        {
            if (!_lobbies.TryGetValue(lobbyId, out var l)) return false;
            if (l.Island != s.Island && l.Island != Islands.Unknown && s.Island != Islands.Unknown) return false;
            int max = Max;
            if (max > 0 && l.Count >= max && s.Lobby != l.Id) return false;
            MoveLocked(s, l);
            return true;
        }
    }

    // ---- Web-Panel (Moderation) ----------------------------------------------------------

    /// <summary>Leere Lobby anlegen (Web-Panel). Sie bleibt bestehen, bis sie gelöscht wird oder ihr letzter Spieler sie verlässt.</summary>
    public Lobby CreateEmpty(int island)
    {
        lock (_lock) return Create(island);
    }

    /// <summary>
    /// Spieler in eine Lobby verschieben (Web-Panel). lobbyId 0 = in eine neue Lobby seiner Insel.
    /// Andere Insel ist nicht erlaubt (der Spieler sähe dort niemanden); eine volle Lobby nur mit force.
    /// </summary>
    public bool Move(Session s, int lobbyId, bool force, out string error, out int target)
    {
        error = ""; target = 0;
        if (s == null) { error = "Spieler nicht online"; return false; }
        lock (_lock)
        {
            Lobby l;
            if (lobbyId == 0) l = Create(s.Island);
            else if (!_lobbies.TryGetValue(lobbyId, out l)) { error = "Lobby existiert nicht"; return false; }
            if (l.Island != s.Island && l.Island != Islands.Unknown && s.Island != Islands.Unknown)
            { error = $"Lobby liegt auf {Islands.Name(l.Island)}, Spieler ist auf {Islands.Name(s.Island)}"; return false; }
            int max = Max;
            if (!force && max > 0 && l.Count >= max && s.Lobby != l.Id) { error = "Lobby ist voll"; return false; }
            target = l.Id;
            if (s.Lobby == l.Id) return true;
            MoveLocked(s, l);
            return true;
        }
    }

    /// <summary>Lobby löschen (Web-Panel): ihre Spieler werden auf die übrigen Lobbys ihrer Insel verteilt (bei Bedarf neue).</summary>
    public bool Delete(int lobbyId, Func<int, Session> findPlayer, out int moved)
    {
        moved = 0;
        lock (_lock)
        {
            if (!_lobbies.TryGetValue(lobbyId, out var l)) return false;
            List<int> ids;
            lock (l.Members) ids = l.Members.ToList();
            _lobbies.Remove(lobbyId);
            Log.Info($"Lobby {lobbyId} gelöscht (Web-Panel), {ids.Count} Spieler werden verteilt");
            foreach (int id in ids)
            {
                var s = findPlayer(id);
                if (s == null || s.Lobby != lobbyId) continue;
                s.Lobby = 0;
                Lobby best = null;
                int max = Max;
                foreach (var o in _lobbies.Values)
                {
                    if (o.Island != s.Island) continue;
                    int n = o.Count;
                    if (max > 0 && n >= max) continue;
                    if (best == null || n > best.Count) best = o;
                }
                best ??= Create(s.Island);
                lock (best.Members) best.Members.Add(s.PlayerId);
                s.Lobby = best.Id;
                moved++;
            }
            return true;
        }
    }

    /// <summary>Spieler verlässt den Server.</summary>
    public void Leave(Session s)
    {
        if (s == null) return;
        lock (_lock) RemoveLocked(s);
    }

    Lobby Create(int island)
    {
        var l = new Lobby { Id = _nextId++, Island = island };
        _lobbies[l.Id] = l;
        Log.Info($"Lobby {l.Id} ({Islands.Name(island)}) angelegt");
        return l;
    }

    void MoveLocked(Session s, Lobby to)
    {
        RemoveLocked(s);
        lock (to.Members) to.Members.Add(s.PlayerId);
        s.Lobby = to.Id;
    }

    void RemoveLocked(Session s)
    {
        if (s.Lobby == 0) return;
        if (_lobbies.TryGetValue(s.Lobby, out var old))
        {
            bool empty;
            lock (old.Members) { old.Members.Remove(s.PlayerId); empty = old.Members.Count == 0; }
            if (empty) { _lobbies.Remove(old.Id); Log.Info($"Lobby {old.Id} aufgelöst"); }
        }
        s.Lobby = 0;
    }

    /// <summary>Sehen sich zwei Spieler (gleiche Lobby, bzw. ohne Lobby gleiche Insel)?</summary>
    public static bool SameLobby(Session a, Session b)
    {
        if (a == null || b == null) return false;
        if (a.Lobby != 0 && b.Lobby != 0) return a.Lobby == b.Lobby;
        return Islands.Same(a.Island, b.Island);
    }
}
