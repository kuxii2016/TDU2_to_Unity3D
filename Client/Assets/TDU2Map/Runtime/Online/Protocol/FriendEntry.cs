using System;

namespace TDU2.Online
{
    /// <summary>Freund bzw. offene Anfrage (op "friend"). Feldnamen = Server Net/Protocol/FriendEntry.cs.</summary>
    [Serializable]
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
        public string lastSeen = "";   // ISO (UTC)
    }
}
