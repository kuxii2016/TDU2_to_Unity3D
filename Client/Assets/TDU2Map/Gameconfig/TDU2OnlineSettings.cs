namespace TDU2.Online
{
    /// <summary>
    /// Einstellungen für den Mehrspieler-Teil an einer Stelle: einfach die Werte hier ändern (true/false).
    /// Alle Felder sind static und können auch zur Laufzeit gesetzt werden (z.B. aus dem Entwicklermenü).
    /// </summary>
    public static class TDU2OnlineSettings
    {
        // ------------------------------------------------------------------ Mitspieler als Spielfigur in Interieurs
        /// <summary>Hauptschalter: Mitspieler in Interieurs als Spielfigur zeigen (und die eigene Figur senden).</summary>
        public static bool ShowPlayersInInteriors = true;

        public static bool ShowInClothesShops = true;     // EAS_*  Kleidungsläden
        public static bool ShowInCarDealers = true;       // ECD_* / ECDC_*  Autohändler
        public static bool ShowInBikeDealers = true;      // EBD_*  Motorradhändler
        public static bool ShowInClubs = true;            // ECH_*  Clubs / Community Racing Center
        public static bool ShowInCasino = true;           // Casino (Halle und Spielräume)
        public static bool ShowInGamblingHall = true;     // EGAM_* Spielhalle
        public static bool ShowInAirport = true;          // EAIR_* Flughafen
        public static bool ShowInHairdresser = true;      // EHD_*  Friseur
        public static bool ShowInSurgery = true;          // ESUR_* Schönheitsklinik
        public static bool ShowInRealtor = true;          // ERE_*  Makler
        public static bool ShowInTuner = false;            // ETUN_* Tuner
        public static bool ShowInStickerShop = false;      // ESTICK_* Lackiererei/Sticker
        public static bool ShowInRacingSchool = true;     // ERS_*  Fahrschule (Gebäude; die Prüfungen selbst siehe unten)
        public static bool ShowInPhotograph = true;       // EPHOT_* Fotograf
        public static bool ShowInAuctionHall = true;      // EUC_*  Auktionshaus
        public static bool ShowInStolenCarDealer = true;  // ESTOL_* Hehler
        public static bool ShowInOtherInteriors = true;   // alles andere
        /// <summary>Häuser (EPH_*): Mitspieler überhaupt zeigen.</summary>
        public static bool ShowInHouses = true;
        /// <summary>Häuser nur für Mitspieler aus der eigenen Gruppe (Einladung/Join), sonst für alle im selben Haus.</summary>
        public static bool HousesInviteOnly = true;

        // ------------------------------------------------------------------ Mitspieler-Autos
        /// <summary>Einzelspieler-Rennen/Cups (Meisterschaften): Mitspieler ausblenden.</summary>
        public static bool HidePlayersInCups = true;
        /// <summary>Fahrschul-Prüfungen: Mitspieler ausblenden.</summary>
        public static bool HidePlayersInRacingSchool = true;
        /// <summary>Lackiererei/Sticker-Shop: fremde Autos ausblenden (nur das eigene Auto steht im Raum).</summary>
        public static bool HideCarsInStickerShop = true;

        // ------------------------------------------------------------------ Synchronisation
        /// <summary>Verschmutzung (Schmutz/Kratzer/Nässe) mitsenden und bei Mitspielern anzeigen.</summary>
        public static bool SyncDirt = true;
        /// <summary>Lackierung/Beklebung aus dem Sticker-Shop mitsenden und anzeigen.</summary>
        public static bool SyncLiveries = true;

        /// <summary>Sieht man in diesem Interieur (Spot-Code, Casino) Mitspieler? Häuser: siehe <see cref="ShowInHouses"/>.</summary>
        public static bool InteriorShared(string spotCode, bool casino)
        {
            if (!ShowPlayersInInteriors) return false;
            if (casino) return ShowInCasino;
            string c = (spotCode ?? "").ToUpperInvariant();
            if (c.StartsWith("EPHOT")) return ShowInPhotograph;
            if (c.StartsWith("EPH")) return ShowInHouses;
            if (c.StartsWith("EAS")) return ShowInClothesShops;
            if (c.StartsWith("ECD")) return ShowInCarDealers;
            if (c.StartsWith("EBD")) return ShowInBikeDealers;
            if (c.StartsWith("ECH")) return ShowInClubs;
            if (c.StartsWith("ECAS")) return ShowInCasino;
            if (c.StartsWith("EGAM")) return ShowInGamblingHall;
            if (c.StartsWith("EAIR")) return ShowInAirport;
            if (c.StartsWith("EHD")) return ShowInHairdresser;
            if (c.StartsWith("ESUR")) return ShowInSurgery;
            if (c.StartsWith("ERE")) return ShowInRealtor;
            if (c.StartsWith("ETUN")) return ShowInTuner;
            if (c.StartsWith("ESTICK")) return ShowInStickerShop;
            if (c.StartsWith("ESTOL")) return ShowInStolenCarDealer;
            if (c.StartsWith("ERS")) return ShowInRacingSchool;
            if (c.StartsWith("EUC")) return ShowInAuctionHall;
            return ShowInOtherInteriors;
        }
    }
}
