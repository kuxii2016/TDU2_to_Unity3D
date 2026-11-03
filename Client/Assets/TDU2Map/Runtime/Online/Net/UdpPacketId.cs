namespace TDU2.Online.Net
{
    /// <summary>
    /// Pakettypen (Byte 0) der UDP-Synchronisation – identisch im Server (TDU2Server.Net.Udp.UdpPacketIds).
    /// Client -&gt; Server: [Typ][u16 playerId][u32 udpKey][Nutzdaten]; Server -&gt; Client: [Typ][u16 playerId][Nutzdaten].
    /// Der Server ist das Relay (kein P2P): der Client sendet nur an den Server, funktioniert daher hinter NAT/CGNAT.
    /// </summary>
    public static class UdpPacketId
    {
        public const byte StateIn = 1, StateOut = 2;       // Spielerzustand
        public const byte ChatOut = 3, ChatIn = 4;          // Chat
        public const byte TrafficIn = 5, TrafficOut = 6;    // Verkehr
        public const byte PingIn = 7, PongOut = 8;          // Laufzeit / NAT-Keepalive
        public const byte PeerPunch = 9;                    // P2P-Hole-Punching (nur Client <-> Client)
        public const byte RelayIn = 10;                     // Relay-Umschlag an den Server (nur an Mitspieler ohne Direktweg)

        /// <summary>Kopf der Client-Pakete (Typ, Spielernummer, Schlüssel).</summary>
        public const int ClientHeader = 7;
        /// <summary>Kopf der Server-Pakete (Typ, Spielernummer).</summary>
        public const int ServerHeader = 3;

        public static void WriteClientHeader(byte[] b, byte type, int playerId, uint key)
        {
            b[0] = type;
            b[1] = (byte)playerId; b[2] = (byte)(playerId >> 8);
            b[3] = (byte)key; b[4] = (byte)(key >> 8); b[5] = (byte)(key >> 16); b[6] = (byte)(key >> 24);
        }
    }
}
