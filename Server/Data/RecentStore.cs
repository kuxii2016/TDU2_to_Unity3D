using System.Text.Json;

namespace TDU2Server.Data;

/// <summary>Eintrag "Zuletzt gesehen" (data/recent.json).</summary>
public sealed class RecentPlayer
{
    public string nick = "";
    public string lastSeen = "";   // ISO (UTC)
    public int carId;
    public int color;
    public int nbCars;
    public long money;
    public int nationality;
}

/// <summary>
/// Zuletzt gesehene Spieler des Servers (Pausemenü "Zuletzt gesehen", op "recent"): beim Abmelden/Trennen und alle paar
/// Minuten für die Online-Spieler aktualisiert, höchstens <see cref="Max"/> Einträge, neueste zuerst.
/// </summary>
public sealed class RecentStore
{
    public const int Max = 100;
    readonly string _file;
    readonly object _lock = new();
    readonly Dictionary<string, RecentPlayer> _byNick = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
    DateTime _lastSave = DateTime.MinValue;

    public RecentStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "recent.json");
        try
        {
            if (File.Exists(_file))
                foreach (var p in JsonSerializer.Deserialize<List<RecentPlayer>>(File.ReadAllText(_file), FileJson) ?? new List<RecentPlayer>())
                    if (!string.IsNullOrEmpty(p.nick)) _byNick[p.nick] = p;
        }
        catch (Exception) { }
    }

    public void Touch(string nick, int carId, int color, int nbCars, long money, int nationality, bool save)
    {
        if (string.IsNullOrEmpty(nick)) return;
        lock (_lock)
        {
            if (!_byNick.TryGetValue(nick, out var p)) _byNick[nick] = p = new RecentPlayer { nick = nick };
            p.lastSeen = DateTime.UtcNow.ToString("o");
            if (carId != 0) { p.carId = carId; p.color = color; }
            p.nbCars = nbCars; p.money = money; p.nationality = nationality;
            if (_byNick.Count > Max)
                foreach (var old in _byNick.Values.OrderBy(x => x.lastSeen, StringComparer.Ordinal).Take(_byNick.Count - Max).ToList())
                    _byNick.Remove(old.nick);
            if (save || (DateTime.UtcNow - _lastSave).TotalMinutes > 5) SaveLocked();
        }
    }

    /// <summary>Eintrag eines Spielers (Kopie) oder null.</summary>
    public RecentPlayer Find(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return null;
        lock (_lock)
            return _byNick.TryGetValue(nick, out var p)
                ? new RecentPlayer { nick = p.nick, lastSeen = p.lastSeen, carId = p.carId, color = p.color, nbCars = p.nbCars, money = p.money, nationality = p.nationality }
                : null;
    }

    /// <summary>Neueste zuerst (ohne <paramref name="except"/>).</summary>
    public List<RecentPlayer> Latest(string except, int count)
    {
        lock (_lock)
            return _byNick.Values.Where(p => !string.Equals(p.nick, except, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.lastSeen, StringComparer.Ordinal).Take(count)
                .Select(p => new RecentPlayer { nick = p.nick, lastSeen = p.lastSeen, carId = p.carId, color = p.color, nbCars = p.nbCars, money = p.money, nationality = p.nationality })
                .ToList();
    }

    void SaveLocked()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byNick.Values.ToList(), FileJson));
            File.Move(tmp, _file, true);
        }
        catch (Exception) { }
    }
}
