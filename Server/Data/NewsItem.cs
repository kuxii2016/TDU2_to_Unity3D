namespace TDU2Server.Data;

/// <summary>
/// Eine News-Meldung (TDU Media, News-Kanal des Clients). Feldnamen = JSON-Namen (Datei data/news.json und Leitung,
/// identisch im Unity-Client TDU2Net.cs NewsEntry).
/// </summary>
public sealed class NewsItem
{
    public int id;
    public string header = "";       // Überschrift (Liste und Detailansicht)
    public string message = "";      // Text
    public string icon = "";         // Symbol (z.B. "info", "update", "event", "warning", "weather", "car", "club") – Client später
    public int island;               // 0 = alle, sonst Islands-Code (nur auf dieser Insel anzeigen)
    public string author = "";       // Panel-Konto, das sie angelegt hat
    public string created = "";      // ISO-Zeit (UTC)
    public string updated = "";      // ISO-Zeit (UTC) der letzten Änderung, leer = nie
    public bool hasImage;            // Bild vorhanden (data/news/&lt;id&gt;.jpg|png, Abruf per op "newsimg")
    public string kind = "";         // "" = News-Kanal, "update" = Updates-Kanal (Patch-Notes) von TDU Media
    public string version = "";      // Updates: Versionsnummer (z.B. "0.9.3"), in der Liste statt des Datums
    public string category = "";     // Updates: Reiter – gmodes (Packs), vehicles, avatar, furnitures, stickers
}
