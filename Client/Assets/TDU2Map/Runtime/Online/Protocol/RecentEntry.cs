using System;

namespace TDU2.Online
{
    /// <summary>Zuletzt gesehener Spieler (op "recent"). Feldnamen = Server Net/Protocol/RecentEntry.cs.</summary>
    [Serializable]
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
}
