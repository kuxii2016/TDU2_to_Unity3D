using System;
using System.Threading;

namespace TDU2.Online.Net
{
    /// <summary>
    /// Antwort auf <see cref="PingPacketOut"/>: [8][u16 playerId][u32 Clientzeit ms][u16 Lobby][u16 Spieler in der Lobby].
    /// Liefert die Laufzeit (RTT) zum Server; wird direkt im Empfangs-Thread ausgewertet.
    /// </summary>
    public sealed class PongPacketIn : IUdpPacketIn
    {
        readonly Func<double> _nowMs;
        double _rtt = -1, _smooth = -1;
        int _lobby, _lobbyCount;

        public PongPacketIn(Func<double> nowMs) { _nowMs = nowMs; }

        public byte Type => UdpPacketId.PongOut;
        public int MinLength => UdpPacketId.ServerHeader + 4;

        /// <summary>Letzte und geglättete Laufzeit (ms), -1 = noch keine Antwort.</summary>
        public double RttMs => Volatile.Read(ref _rtt);
        public double SmoothRttMs => Volatile.Read(ref _smooth);
        public int Lobby => Volatile.Read(ref _lobby);
        public int LobbyCount => Volatile.Read(ref _lobbyCount);

        public void Handle(byte[] d, double receivedAt)
        {
            uint sent = BitConverter.ToUInt32(d, 3);
            double now = _nowMs();
            double rtt = (uint)((uint)now - sent);
            if (rtt < 0 || rtt > 10000) return;
            Volatile.Write(ref _rtt, rtt);
            double s = Volatile.Read(ref _smooth);
            Volatile.Write(ref _smooth, s < 0 ? rtt : s + (rtt - s) * 0.2);
            if (d.Length >= 11)
            {
                Volatile.Write(ref _lobby, d[7] | (d[8] << 8));
                Volatile.Write(ref _lobbyCount, d[9] | (d[10] << 8));
            }
        }
    }
}
