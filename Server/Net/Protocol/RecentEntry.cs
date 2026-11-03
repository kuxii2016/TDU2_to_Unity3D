namespace TDU2Server.Net.Protocol;

/// <summary>Ein zuletzt gesehener Spieler in der Antwort von "recent".</summary>
public sealed class RecentEntry
{
    public string nick = "";
    public string lastSeen = "";   // ISO (UTC)
    public bool online;
    public int carId;
    public int color;
    public int nbCars;
    public long money;
    public int nationality;
}
