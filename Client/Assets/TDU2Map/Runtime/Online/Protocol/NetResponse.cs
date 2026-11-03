using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    [Serializable]
    public sealed class NetResponse
    {
        public bool ok;
        public string error = "";
        public string nick = "";
        public string token = "";
        public string session = "";
        public SaveData save;
        public List<string> starterCars = new List<string>();
        public string startSpot = "";
        public string startIsland = "";
        public long startMoney;
        public int online;
        public int udpPort;
        public string motd = "";
        public int playerId;
        public long udpKey;
        public List<PlayerEntry> players = new List<PlayerEntry>();
        /// <summary>Radio-Zeitbasis vom Server: Epoche (Serverstart, UTC-Millisekunden), Startwert und aktuelle Serverzeit.</summary>
        public long radioEpoch;
        public int radioSeed;
        public long serverTime;
        /// <summary>status: offene Einladungen an mich, Meldungen ("accepted:id:nick", "declined:…", "rideend:…"), Mitfahrt.</summary>
        public List<InviteEntry> invites = new List<InviteEntry>();
        public List<string> notices = new List<string>();
        public string inviteKind = "";   // invreply: Art der angenommenen Einladung ("game|…", "room|SPOT|Bank|Ort")
        public int rideWith;
        public int passenger;
        /// <summary>profile: MyTDULife des angefragten Spielers (mit Foto).</summary>
        public MyLifeData life;
        public int lifeOf;
        /// <summary>profile: Aussehen des Spielers (AvatarConfig.Encode) – Geschlecht für den Foto-Platzhalter.</summary>
        public string avatar = "";
        /// <summary>status/news: höchste News-Id auf dem Server; news: neue Meldungen (Id &gt; since) und alle vorhandenen Ids.</summary>
        public int newsLatest;
        public List<NewsEntry> news = new List<NewsEntry>();
        public List<int> newsIds = new List<int>();
        /// <summary>newsimg: Bild der Meldung (JPEG/PNG, Base64), lifeOf = deren Id.</summary>
        public string newsImage = "";
        /// <summary>surveys/vote: Umfragen mit Summen und eigener Stimme; Änderungszähler.</summary>
        public List<SurveyEntry> surveys = new List<SurveyEntry>();
        public int surveyVersion;
        /// <summary>status/lobbies: eigene Lobby, Spieler darin, Höchstzahl (0 = unbegrenzt), Abfrageabstand des Servers (ms).</summary>
        public int lobby;
        public int lobbyCount;
        public int lobbyMax;
        public int statusIntervalMs;
        /// <summary>lobbies: alle Lobbys; recent: zuletzt gesehene Spieler des Servers.</summary>
        public List<LobbyEntry> lobbies = new List<LobbyEntry>();
        public List<RecentEntry> recent = new List<RecentEntry>();
        /// <summary>status: Server erlaubt P2P; mein Token (steht in meinen Punch-Paketen, Mitspieler prüfen es).</summary>
        public bool p2pEnabled;
        public long p2pToken;
        /// <summary>friend: bestätigte Freunde, Anfragen an mich, meine offenen Anfragen, Ergebnis einer Anfrage
        /// (sent / already_friends / already_sent / became_friends).</summary>
        public List<FriendEntry> friends = new List<FriendEntry>();
        public List<FriendEntry> friendRequests = new List<FriendEntry>();
        public List<string> friendPending = new List<string>();
        public string friendResult = "";

        public static NetResponse Error(string e) => new NetResponse { ok = false, error = e };
    }
}
