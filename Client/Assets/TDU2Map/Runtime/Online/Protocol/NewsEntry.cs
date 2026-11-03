using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    /// <summary>News-Meldung vom Server (op "news"; im Web-Panel angelegt). Feldnamen = Server Data/NewsItem.cs.</summary>
    [Serializable]
    public sealed class NewsEntry
    {
        public int id;
        public string header = "";
        public string message = "";
        public string icon = "";      // info, update, event, warning, weather, car, club (Anzeige später)
        public int island;            // 0 = alle, 1 Ibiza, 2 Hawaii
        public string author = "";
        public string created = "";   // ISO (UTC)
        public string updated = "";
        public bool hasImage;         // Bild per op "newsimg" (target = id) abholen
        public string kind = "";      // "" = News-Kanal, "update" = Updates-Kanal (Patch-Notes)
        public string version = "";   // Updates: Versionsnummer
        public string category = "";  // Updates: Reiter des DLC-Kanals – gmodes (Packs), vehicles, avatar, furnitures, stickers
    }
}
