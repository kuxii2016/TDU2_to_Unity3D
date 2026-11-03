namespace TDU2Server.Sessions;

/// <summary>
/// Eine Lobby: Gruppe von Spielern derselben Insel, die sich gegenseitig sehen (UDP-Weiterleitung, Spielerliste).
/// Größe begrenzt durch config.json "maxPlayersPerLobby"; der Server verteilt die Spieler (LobbyManager).
/// </summary>
public sealed class Lobby
{
    public int Id;
    public int Island;                 // 1 Ibiza, 2 Hawaii, 0 unbekannt
    public readonly HashSet<int> Members = new();   // Spielernummern
    public DateTime Created = DateTime.UtcNow;

    public int Count { get { lock (Members) return Members.Count; } }
}
