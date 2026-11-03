namespace TDU2Server.Net.Udp;

/// <summary>
/// Pakettypen (Byte 0) der UDP-Synchronisation – identisch im Client (TDU2.Online.Net.UdpPacketId).
/// Client -&gt; Server: [Typ][u16 playerId][u32 udpKey][Nutzdaten]; Server -&gt; Client: [Typ][u16 playerId][Nutzdaten].
/// </summary>
public static class UdpPacketIds
{
    public const byte StateIn = 1, StateOut = 2;       // Spielerzustand (PlayerStatePacket)
    public const byte ChatOut = 3, ChatIn = 4;          // Chat (ChatPacket)
    public const byte TrafficIn = 5, TrafficOut = 6;    // Verkehr (TrafficPacket)
    public const byte PingIn = 7, PongOut = 8;          // Laufzeit/NAT-Keepalive (PingPacket)
    public const byte PeerPunch = 9;                    // nur Client &lt;-&gt; Client (Hole-Punching), nie über den Server
    public const byte RelayIn = 10;                     // Umschlag: Zustand/Verkehr nur an die genannten Spieler (RelayPacket)
}
