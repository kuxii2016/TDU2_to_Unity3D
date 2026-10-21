using System.Net;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Sessions;

/// <summary>Ein angemeldeter Spieler: Sitzung (TCP), UDP-Zugang, letzter bekannter Zustand, Einladungen und Mitfahrt.</summary>
public sealed class Session
{
    public string Id = "";
    public string Nick = "";
    public Connection Conn;
    public DateTime Since = DateTime.Now;

    // UDP
    public int PlayerId;
    public uint UdpKey;
    public IPEndPoint UdpEndPoint;
    public DateTime LastUdp = DateTime.MinValue;

    // Zustand (aus UDP bzw. Spielstand) für die Spielerliste
    public int CarId, Color;
    public bool InCar;
    public float Px, Pz;
    public long Money;
    public int NbCars;
    public int Nationality;
    public int Island;                       // 1 Ibiza, 2 Hawaii, 0 unbekannt
    public string Avatar = "";
    public string Mood = "", MoodTxt = "";   // MyTDULife
    public int PhotoVer;

    // Einladungen / Mitfahrt (Beifahrer)
    public readonly object Lock = new();
    public readonly List<Invite> Invites = new();
    public readonly List<string> Notices = new();
    public int RideWith;                     // Fahrer, bei dem ich mitfahre
    public int Passenger;                    // mein Beifahrer

    /// <summary>Meldung für den Spieler (kommt mit dem nächsten "status" an).</summary>
    public void Notice(string n)
    {
        lock (Lock)
        {
            Notices.Add(n);
            if (Notices.Count > 20) Notices.RemoveAt(0);
        }
    }

    /// <summary>Hat der Spieler in den letzten <paramref name="seconds"/> Sekunden UDP gesendet?</summary>
    public bool UdpActive(DateTime now, double seconds) => (now - LastUdp).TotalSeconds <= seconds;
}

/// <summary>Offene Einladung an einen Spieler.</summary>
public readonly record struct Invite(int From, string Nick, string Kind, DateTime At);
