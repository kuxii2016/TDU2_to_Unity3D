namespace TDU2Server.Net.Protocol;

/// <summary>Ein Freund (oder eine offene Anfrage) in der Antwort von "friend".</summary>
public sealed class FriendEntry
{
    public string nick = "";
    public bool online;
    public int id;              // Spielernummer, wenn online (sonst 0)
    public int nationality;
    public int carId;
    public int color;
    public long money;
    public int nbCars;
    public int photoVer;
    public string mood = "";
    public string lastSeen = "";   // ISO (UTC), aus "Zuletzt gesehen"
}
