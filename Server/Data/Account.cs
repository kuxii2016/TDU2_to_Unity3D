namespace TDU2Server.Data;

/// <summary>Konto (data/accounts/&lt;nick&gt;.json).</summary>
public sealed class Account
{
    public string nick = "";
    public string email = "";
    public string passwordHash = "";
    public string salt = "";
    public List<string> tokenHashes = new();
    public string created = "";
    public string lastLogin = "";
    public int nationality;
    /// <summary>Rolle für das Web-Panel: "player" (Standard), "helper", "mod", "admin".</summary>
    public string permission = "player";
    public string bannedUntil = "";   // leere Zeichenkette = nicht gesperrt, sonst ISO-Zeit
    public string banReason = "";
    public SaveData save = new();

    /// <summary>Höchstens so viele gemerkte Anmeldungen (PCs) je Konto.</summary>
    public const int MaxTokens = 8;

    /// <summary>Rollen mit Zugriff auf das Web-Panel (mindestens "helper").</summary>
    public static bool IsStaff(string permission) =>
        permission is "admin" or "dev" or "mod" or "helper";

    /// <summary>Rang, großes Feld = mehr Rechte (0 = Spieler, 3 = admin/dev).</summary>
    public static int Rank(string permission) => permission switch
    {
        "admin" or "dev" => 3,
        "mod" => 2,
        "helper" => 1,
        _ => 0,
    };

    /// <summary>Aktuell gesperrt?</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBanned => !string.IsNullOrEmpty(bannedUntil)
        && DateTime.TryParse(bannedUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out var until)
        && until > DateTime.UtcNow;
}
