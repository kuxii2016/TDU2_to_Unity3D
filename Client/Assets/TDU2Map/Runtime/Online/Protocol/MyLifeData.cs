using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    /// <summary>MyTDULife – andere Spieler lesen es über op "profile" (Foto) bzw. die Spielerliste (Stimmung).</summary>
    [Serializable]
    public sealed class MyLifeData
    {
        public string mood = "neutral";                              // Smiley-Label (neutral, big_smile, tongue, wink, sad …)
        public string moodText = "";                                 // Stimmungstext
        public List<string> boxes = new List<string>();              // Kästen ed1..ed7
        public List<HouseEntry> fields = new List<HouseEntry>();     // Profilangaben: Liste -> Index ("attitude" -> "2")
        public int birthDay, birthMonth, birthYear;
        public string gender = "";
        public string photo = "";                                    // Avatar-Foto (JPG, Base64)
        public int photoVer;
    }
}
