using System.Text.Json;

namespace TDU2Server.Data;

/// <summary>
/// News auf der Platte: &lt;dataDir&gt;/news.json (atomar über .tmp geschrieben). Ids steigen nur (auch nach dem Löschen),
/// damit Clients mit "since" = höchste bekannte Id genau die neuen Meldungen abholen können.
/// </summary>
public sealed class NewsStore
{
    sealed class FileData { public int nextId = 1; public List<NewsItem> items = new(); }

    public const int MaxItems = 200;
    public const int MaxHeader = 80;
    public const int MaxMessage = 2000;
    public const int MaxIcon = 24;
    public const int MaxVersion = 24;

    /// <summary>Gültige Art: "update" (Updates-Kanal) oder "" (News-Kanal).</summary>
    public static readonly string[] Categories = { "gmodes", "vehicles", "avatar", "furnitures", "stickers" };

    /// <summary>Gültige Kategorie eines Updates (Reiter im Spiel), sonst "gmodes" (Packs); bei News leer.</summary>
    public static string Category(string kind, string category)
    {
        if (Kind(kind) != "update") return "";
        string c = (category ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(Categories, c) >= 0 ? c : "gmodes";
    }

    public static string Kind(string kind) => string.Equals((kind ?? "").Trim(), "update", StringComparison.OrdinalIgnoreCase) ? "update" : "";

    public const int MaxImageBytes = 400 * 1024;

    readonly string _file, _imgDir;
    readonly object _lock = new();
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
    FileData _data = new();

    /// <summary>Höchste vergebene Id (0 = keine). Ändert sich bei jeder neuen oder bearbeiteten Meldung (Bearbeiten vergibt eine neue Id).</summary>
    public int Latest { get { lock (_lock) return _data.nextId - 1; } }

    public NewsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "news.json");
        _imgDir = Path.Combine(dataDir, "news");
        Directory.CreateDirectory(_imgDir);
        try
        {
            if (File.Exists(_file)) _data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_file), FileJson) ?? new FileData();
        }
        catch (Exception ex) { Core.Log.Info("news.json fehlerhaft (" + ex.Message + ") – leer begonnen"); _data = new FileData(); }
        _data.items ??= new();
        int max = _data.items.Count > 0 ? _data.items.Max(n => n.id) : 0;
        if (_data.nextId <= max) _data.nextId = max + 1;
    }

    /// <summary>Alle Meldungen, neueste zuerst.</summary>
    public List<NewsItem> All()
    {
        lock (_lock) return _data.items.OrderByDescending(n => n.id).Select(Copy).ToList();
    }

    /// <summary>Meldungen mit Id &gt; since (neueste zuerst, höchstens max), optional nur für eine Insel (0 = alle).</summary>
    public List<NewsItem> Since(int since, int island, int max = 50)
    {
        lock (_lock)
            return _data.items.Where(n => n.id > since && (n.island == 0 || island == 0 || n.island == island))
                .OrderByDescending(n => n.id).Take(max).Select(Copy).ToList();
    }

    /// <summary>Ids aller vorhandenen Meldungen (Client entfernt gelöschte).</summary>
    public List<int> Ids() { lock (_lock) return _data.items.Select(n => n.id).ToList(); }

    // ---- Bilder (JPEG/PNG, data/news/<id>.jpg|png)

    /// <summary>Bild prüfen (JPEG/PNG, Größe). null = ungültig.</summary>
    public static byte[] CheckImage(string base64, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(base64)) { error = "kein Bild"; return null; }
        int comma = base64.IndexOf(',');
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) base64 = base64[(comma + 1)..];
        byte[] b;
        try { b = Convert.FromBase64String(base64.Trim()); } catch { error = "Bild nicht lesbar"; return null; }
        if (b.Length > MaxImageBytes) { error = $"Bild zu groß ({b.Length / 1024} KB, max. {MaxImageBytes / 1024} KB)"; return null; }
        bool jpg = b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8, png = b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
        if (!jpg && !png) { error = "nur JPEG oder PNG"; return null; }
        return b;
    }

    string ImageFile(int id, bool png) => Path.Combine(_imgDir, id + (png ? ".png" : ".jpg"));

    string FindImage(int id)
    {
        foreach (var f in new[] { ImageFile(id, false), ImageFile(id, true) }) if (File.Exists(f)) return f;
        return null;
    }

    void WriteImage(int id, byte[] b)
    {
        DeleteImage(id);
        bool png = b[0] == 0x89;
        File.WriteAllBytes(ImageFile(id, png), b);
    }

    void DeleteImage(int id) { var f = FindImage(id); if (f != null) File.Delete(f); }

    /// <summary>Bild einer Meldung als Base64 (null = keins).</summary>
    public string ImageBase64(int id)
    {
        lock (_lock)
        {
            var f = FindImage(id);
            return f != null ? Convert.ToBase64String(File.ReadAllBytes(f)) : null;
        }
    }

    public NewsItem Add(string header, string message, string icon, int island, string author, byte[] image = null, string kind = "", string version = "", string category = "")
    {
        var n = new NewsItem
        {
            header = Cut(header, MaxHeader), message = Cut(message, MaxMessage), icon = Cut(icon, MaxIcon),
            island = island, author = author ?? "", created = DateTime.UtcNow.ToString("o"),
            kind = Kind(kind), version = Cut(version, MaxVersion), category = Category(kind, category),
        };
        lock (_lock)
        {
            n.id = _data.nextId++;
            if (image != null) { WriteImage(n.id, image); n.hasImage = true; }
            _data.items.Add(n);
            while (_data.items.Count > MaxItems)
            {
                var old = _data.items.OrderBy(x => x.id).First();
                DeleteImage(old.id);
                _data.items.Remove(old);
            }
            Save();
        }
        return Copy(n);
    }

    /// <summary>
    /// Meldung ändern: bekommt eine neue Id, damit Clients sie als neu abholen. image = neues Bild (null = unverändert),
    /// removeImage = Bild entfernen. null = nicht gefunden.
    /// </summary>
    public NewsItem Update(int id, string header, string message, string icon, int island, byte[] image = null, bool removeImage = false,
        string kind = "", string version = "", string category = "")
    {
        lock (_lock)
        {
            var n = _data.items.FirstOrDefault(x => x.id == id);
            if (n == null) return null;
            n.header = Cut(header, MaxHeader); n.message = Cut(message, MaxMessage); n.icon = Cut(icon, MaxIcon); n.island = island;
            n.kind = Kind(kind); n.version = Cut(version, MaxVersion); n.category = Category(kind, category);
            n.updated = DateTime.UtcNow.ToString("o");
            int newId = _data.nextId++;
            var oldFile = FindImage(id);
            if (image != null) { DeleteImage(id); WriteImage(newId, image); n.hasImage = true; }
            else if (removeImage) { DeleteImage(id); n.hasImage = false; }
            else if (oldFile != null) { File.Move(oldFile, Path.Combine(_imgDir, newId + Path.GetExtension(oldFile)), true); }
            n.id = newId;
            Save();
            return Copy(n);
        }
    }

    public bool Delete(int id)
    {
        lock (_lock)
        {
            if (_data.items.RemoveAll(x => x.id == id) == 0) return false;
            DeleteImage(id);
            Save();
            return true;
        }
    }

    void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, FileJson));
        File.Move(tmp, _file, true);
    }

    static NewsItem Copy(NewsItem n) => new()
    {
        id = n.id, header = n.header, message = n.message, icon = n.icon, island = n.island, author = n.author, created = n.created, updated = n.updated,
        hasImage = n.hasImage, kind = n.kind ?? "", version = n.version ?? "", category = n.category ?? "",
    };

    static string Cut(string v, int max) { v = (v ?? "").Trim(); return v.Length > max ? v[..max] : v; }
}
