namespace TDU2.Online.Net
{
    /// <summary>Ping an den Server [7][u16 playerId][u32 udpKey][u32 Clientzeit ms] – Laufzeitmessung und NAT-Keepalive.</summary>
    public static class PingPacketOut
    {
        public const int Size = UdpPacketId.ClientHeader + 4;

        public static byte[] Build(int playerId, uint key, uint clientMs)
        {
            var b = new byte[Size];
            UdpPacketId.WriteClientHeader(b, UdpPacketId.PingIn, playerId, key);
            b[7] = (byte)clientMs; b[8] = (byte)(clientMs >> 8); b[9] = (byte)(clientMs >> 16); b[10] = (byte)(clientMs >> 24);
            return b;
        }
    }
}
