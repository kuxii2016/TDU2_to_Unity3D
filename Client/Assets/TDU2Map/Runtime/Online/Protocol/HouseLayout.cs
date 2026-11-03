using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    [Serializable]
    public sealed class HouseLayout
    {
        public string spot = "";
        public List<HouseEntry> furn = new List<HouseEntry>();   // Entity-Name -> Bank (interior/myhouse)
        public List<HouseEntry> mats = new List<HouseEntry>();   // Fläche ("Floor1", "Wall2") -> Material (ground_NN / wall_NN)
    }
}
