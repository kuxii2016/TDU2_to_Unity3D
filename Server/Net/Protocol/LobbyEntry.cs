namespace TDU2Server.Net.Protocol;

/// <summary>Eine Lobby in der Antwort von "lobbies".</summary>
public sealed class LobbyEntry
{
    public int id;
    public int island;   // 1 Ibiza, 2 Hawaii, 0 unbekannt
    public int count;
    public int max;      // 0 = unbegrenzt
}
