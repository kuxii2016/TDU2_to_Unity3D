using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    /// <summary>Offene Einladung (op "status": invites).</summary>
    [Serializable]
    public sealed class InviteEntry
    {
        public int from;
        public string nick = "";
        public string kind = "";
    }
}
