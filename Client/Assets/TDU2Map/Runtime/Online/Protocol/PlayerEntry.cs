using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    /// <summary>Eintrag der Spielerliste des Servers (op "status").</summary>
    [Serializable]
    public sealed class PlayerEntry
    {
        public int id;
        public string nick = "";
        public int carId;
        public int color;
        public long money;
        public int nbCars;
        public bool inCar;
        public float px, pz;
        public int nationality;
        public int island;          // 1 Ibiza, 2 Hawaii, 0 unbekannt (Server ohne Inseltrennung)
        public string avatar = "";  // Aussehen (AvatarConfig.Encode) – Mitspieler im Auto mit ihrer Kleidung
        public string mood = "";    // MyTDULife: Smiley, Stimmungstext, Foto-Version (Foto über op "profile")
        public string moodTxt = "";
        public int photoVer;
        public int rideWith;        // fährt als Beifahrer bei Spieler … mit (0 = nein)
        public int passenger;       // hat Spieler … als Beifahrer (0 = nein)
        /// <summary>P2P (nur Lobby-Mitspieler, wenn beide P2P können): öffentlicher/lokaler UDP-Endpunkt, Token; sonst leer/0.</summary>
        public string ep = "";
        public string lep = "";
        public long p2p;
    }
}
