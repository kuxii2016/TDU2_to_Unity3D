using TDU2Server.Data;

namespace TDU2Server.Net.Protocol;

/// <summary>Anfrage des Clients (eine JSON-Zeile). Nicht jede Anfrage nutzt alle Felder.</summary>
public sealed class NetRequest
{
    public string op = "";
    public string nick = "";
    public string email = "";
    public string password = "";
    public string token = "";
    public string session = "";
    public int version;
    public int nationality;
    public SaveData save;
    public int target;          // invite/invreply/profile: Spielernummer
    public bool accept;         // invreply
    public string kind = "";    // invite: "codriving", "game|…", "room|…"
}
