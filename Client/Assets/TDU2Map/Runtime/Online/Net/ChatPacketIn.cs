namespace TDU2.Online.Net
{
    /// <summary>Chatnachricht [3][u16 playerId][u32 Nr.][Name][0][Text] (Server sendet doppelt, der Client verwirft die Kopie).</summary>
    public sealed class ChatPacketIn : QueuedPacketIn
    {
        public override byte Type => UdpPacketId.ChatOut;
        public override int MinLength => 8;
        protected override int Capacity => 200;
    }
}
