using System.Text.Json;
using System.Text.RegularExpressions;

namespace TDU2Server.Data;

/// <summary>Konten auf der Platte: &lt;dataDir&gt;/accounts/&lt;nick&gt;.json (atomar über eine .tmp-Datei geschrieben).</summary>
public sealed class AccountStore
{
    readonly string _dir;
    readonly object _lock = new();
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
    static readonly Regex NickRx = new("^[A-Za-z0-9_.\\-]{3,16}$", RegexOptions.Compiled);

    public AccountStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "accounts");
        Directory.CreateDirectory(_dir);
    }

    public static bool ValidNick(string nick) => !string.IsNullOrEmpty(nick) && NickRx.IsMatch(nick);

    string FileOf(string nick) => Path.Combine(_dir, nick.ToLowerInvariant() + ".json");

    public Account Load(string nick)
    {
        if (!ValidNick(nick)) return null;
        lock (_lock)
        {
            var f = FileOf(nick);
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<Account>(File.ReadAllText(f), FileJson);
        }
    }

    public void Store(Account a)
    {
        lock (_lock)
        {
            var f = FileOf(a.nick);
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(a, FileJson));
            File.Move(tmp, f, true);
        }
    }

    /// <summary>Neues Konto anlegen; false, wenn der Name schon vergeben ist.</summary>
    public bool Create(Account a)
    {
        lock (_lock)
        {
            if (File.Exists(FileOf(a.nick))) return false;
            Store(a);
            return true;
        }
    }

    public int Count() { try { return Directory.GetFiles(_dir, "*.json").Length; } catch { return 0; } }

    public IEnumerable<string> Names() => Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension);

    /// <summary>Konto-Datei löschen; false, wenn es sie nicht gibt.</summary>
    public bool Delete(string nick)
    {
        if (!ValidNick(nick)) return false;
        lock (_lock)
        {
            var f = FileOf(nick);
            if (!File.Exists(f)) return false;
            File.Delete(f);
            return true;
        }
    }
}
