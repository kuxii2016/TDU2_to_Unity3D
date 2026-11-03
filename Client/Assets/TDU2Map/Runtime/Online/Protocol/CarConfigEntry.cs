using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    /// <summary>Ausstattung eines Autos: Lack = Index in DBCarsAndColors des Autos, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode (0 = Standard).</summary>
    [Serializable]
    public sealed class CarConfigEntry
    {
        public string car = "";
        public int color;
        public int rim;
        public int interior;
    }
}
