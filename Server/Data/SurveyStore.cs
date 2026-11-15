using System.Text.Json;

namespace TDU2Server.Data;

/// <summary>
/// Eine Umfrage (TDU Media, Umfrage-Kanal des Clients). Gespeichert in data/surveys.json; die Stimmen (votes: Nick -> Antwort)
/// verlassen den Server nie, die Clients bekommen nur die Summen (<see cref="SurveyEntry"/>).
/// </summary>
public sealed class SurveyItem
{
    public int id;
    public string question = "";                         // Frage (Liste und Detailansicht)
    public string text = "";                             // Beschreibung (optional)
    public List<string> answers = new();                 // 2..8 Antworten
    public bool open = true;                             // im Panel geschlossen = nur noch Ergebnis
    public int island;                                   // 0 = alle, 1 Ibiza, 2 Hawaii
    public string author = "";
    public string created = "";                          // ISO (UTC)
    public string closes = "";                           // ISO (UTC), leer = kein Ende
    public Dictionary<string, int> votes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offen: nicht im Panel geschlossen und Enddatum nicht erreicht.</summary>
    public bool IsOpen
    {
        get
        {
            if (!open) return false;
            if (!string.IsNullOrEmpty(closes) && DateTime.TryParse(closes, null, System.Globalization.DateTimeStyles.RoundtripKind, out var c)
                && DateTime.UtcNow >= c.ToUniversalTime()) return false;
            return true;
        }
    }
}

/// <summary>Umfrage für den Client (op "surveys"/"vote"): Summen je Antwort und die eigene Stimme. Gleiche Namen im Unity-Client (TDU2Net.cs).</summary>
public sealed class SurveyEntry
{
    public int id;
    public string question = "";
    public string text = "";
    public List<string> answers = new();
    public List<int> counts = new();
    public int total;
    public int myVote = -1;                              // -1 = noch nicht abgestimmt
    public bool open;
    public string created = "";
    public string closes = "";
}

/// <summary>Umfragen auf der Platte: &lt;dataDir&gt;/surveys.json (atomar über .tmp geschrieben). Ids steigen nur.</summary>
public sealed class SurveyStore
{
    sealed class FileData { public int nextId = 1; public List<SurveyItem> items = new(); }

    public const int MaxItems = 100;
    public const int MaxQuestion = 120;
    public const int MaxText = 1000;
    public const int MaxAnswer = 60;
    public const int MinAnswers = 2, MaxAnswers = 8;

    readonly string _file;
    readonly object _lock = new();
    static readonly JsonSerializerOptions FileJson = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
    FileData _data = new();

    /// <summary>Zähler für Änderungen (neue Umfrage, Stimme, Schließen …) – nur im Speicher.</summary>
    public int Version { get; private set; }

    public SurveyStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "surveys.json");
        try
        {
            if (File.Exists(_file)) _data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_file), FileJson) ?? new FileData();
        }
        catch (Exception ex) { Core.Log.Info("surveys.json fehlerhaft (" + ex.Message + ") – leer begonnen"); _data = new FileData(); }
        _data.items ??= new();
        foreach (var s in _data.items)
        {
            s.answers ??= new();
            s.votes = s.votes != null ? new Dictionary<string, int>(s.votes, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        int max = _data.items.Count > 0 ? _data.items.Max(n => n.id) : 0;
        if (_data.nextId <= max) _data.nextId = max + 1;
    }

    /// <summary>Höchste Id einer Umfrage (0 = keine).</summary>
    public int Latest { get { lock (_lock) return _data.items.Count > 0 ? _data.items.Max(s => s.id) : 0; } }

    /// <summary>Umfragen für einen Spieler (neueste zuerst; offene vor geschlossenen), mit Summen und seiner Stimme.</summary>
    public List<SurveyEntry> ForPlayer(string nick, int island)
    {
        lock (_lock)
            return _data.items.Where(s => s.island == 0 || island == 0 || s.island == island)
                .OrderByDescending(s => s.IsOpen).ThenByDescending(s => s.id).Select(s => Entry(s, nick)).ToList();
    }

    /// <summary>Alle Umfragen mit Summen (Web-Panel).</summary>
    public List<SurveyEntry> All(out Dictionary<int, SurveyItem> raw)
    {
        lock (_lock)
        {
            raw = _data.items.ToDictionary(s => s.id, s => s);
            return _data.items.OrderByDescending(s => s.id).Select(s => Entry(s, null)).ToList();
        }
    }

    static SurveyEntry Entry(SurveyItem s, string nick)
    {
        var e = new SurveyEntry
        {
            id = s.id,
            question = s.question,
            text = s.text,
            answers = s.answers.ToList(),
            open = s.IsOpen,
            created = s.created,
            closes = s.closes,
            counts = Enumerable.Repeat(0, s.answers.Count).ToList(),
        };
        foreach (var v in s.votes.Values) if (v >= 0 && v < e.counts.Count) { e.counts[v]++; e.total++; }
        if (!string.IsNullOrEmpty(nick) && s.votes.TryGetValue(nick, out var mine)) e.myVote = mine;
        return e;
    }

    /// <summary>Neue Umfrage. null + error bei ungültigen Angaben.</summary>
    public SurveyItem Add(string question, string text, IEnumerable<string> answers, int island, string closes, string author, out string error)
    {
        error = "";
        var ans = (answers ?? Enumerable.Empty<string>()).Select(a => Cut(a, MaxAnswer)).Where(a => a.Length > 0).Take(MaxAnswers).ToList();
        question = Cut(question, MaxQuestion);
        if (question.Length == 0) { error = "Frage fehlt"; return null; }
        if (ans.Count < MinAnswers) { error = $"mindestens {MinAnswers} Antworten"; return null; }
        string closesIso = "";
        if (!string.IsNullOrWhiteSpace(closes))
        {
            if (!DateTime.TryParse(closes, null, System.Globalization.DateTimeStyles.RoundtripKind, out var c)) { error = "Enddatum ungültig"; return null; }
            closesIso = c.ToUniversalTime().ToString("o");
        }
        var s = new SurveyItem
        {
            question = question,
            text = Cut(text, MaxText),
            answers = ans,
            island = island,
            closes = closesIso,
            author = author ?? "",
            created = DateTime.UtcNow.ToString("o"),
        };
        lock (_lock)
        {
            s.id = _data.nextId++;
            _data.items.Add(s);
            while (_data.items.Count > MaxItems) _data.items.Remove(_data.items.OrderBy(x => x.id).First());
            Save();
        }
        return s;
    }

    /// <summary>Stimme abgeben bzw. ändern (solange offen). Fehlercode oder "" bei Erfolg.</summary>
    public string Vote(int id, string nick, int choice)
    {
        if (string.IsNullOrWhiteSpace(nick)) return "not_logged_in";
        lock (_lock)
        {
            var s = _data.items.FirstOrDefault(x => x.id == id);
            if (s == null) return "not_found";
            if (!s.IsOpen) return "closed";
            if (choice < 0 || choice >= s.answers.Count) return "bad_choice";
            s.votes[nick] = choice;
            Save();
            return "";
        }
    }

    public bool SetOpen(int id, bool open)
    {
        lock (_lock)
        {
            var s = _data.items.FirstOrDefault(x => x.id == id);
            if (s == null) return false;
            s.open = open;
            if (open && !s.IsOpen) s.closes = "";   // wieder öffnen nach Ablauf: Enddatum entfernen
            Save();
            return true;
        }
    }

    public bool ResetVotes(int id)
    {
        lock (_lock)
        {
            var s = _data.items.FirstOrDefault(x => x.id == id);
            if (s == null) return false;
            s.votes.Clear();
            Save();
            return true;
        }
    }

    public bool Delete(int id)
    {
        lock (_lock)
        {
            if (_data.items.RemoveAll(x => x.id == id) == 0) return false;
            Save();
            return true;
        }
    }

    void Save()
    {
        Version++;
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, FileJson));
        File.Move(tmp, _file, true);
    }

    static string Cut(string v, int max) { v = (v ?? "").Trim(); return v.Length > max ? v[..max] : v; }
}
