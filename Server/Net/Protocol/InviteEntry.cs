namespace TDU2Server.Net.Protocol;

/// <summary>Offene Einladung in der Antwort von "status".</summary>
public sealed class InviteEntry
{
    public int from;
    public string nick = "";
    public string kind = "";
}
