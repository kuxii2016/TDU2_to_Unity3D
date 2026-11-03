using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    // ------------------------------------------------------------------ Datenklassen (Feldnamen = JSON-Namen, identisch im Server TDU2Server/Program.cs)
    [Serializable]
    public sealed class OdoEntry
    {
        public int carId;
        public double meters;
    }
}
