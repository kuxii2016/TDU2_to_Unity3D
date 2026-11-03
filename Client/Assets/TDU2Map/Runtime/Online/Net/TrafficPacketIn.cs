namespace TDU2.Online.Net
{
    /// <summary>Verkehrsautos eines anderen Spielers [6][u16 playerId][Nutzdaten] (Format: TDU2Traffic).</summary>
    public sealed class TrafficPacketIn : QueuedPacketIn
    {
        public override byte Type => UdpPacketId.TrafficOut;
        public override int MinLength => 10;
        protected override int Capacity => 2000;
    }
}
