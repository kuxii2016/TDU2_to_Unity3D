using System.Collections.Generic;

namespace TDU2.Online.Net
{
    /// <summary>Verteilt empfangene UDP-Pakete nach ihrem Typ (Byte 0) an die registrierte Paketklasse (thread-sicher lesend).</summary>
    public sealed class UdpDispatcher
    {
        readonly IUdpPacketIn[] _byType = new IUdpPacketIn[256];
        public long Received, Dropped;

        public UdpDispatcher Register(IUdpPacketIn p) { if (p != null) _byType[p.Type] = p; return this; }

        public void Dispatch(byte[] d, double t)
        {
            if (d == null || d.Length == 0) { Dropped++; return; }
            var p = _byType[d[0]];
            if (p == null || d.Length < p.MinLength) { Dropped++; return; }
            Received++;
            p.Handle(d, t);
        }
    }
}
