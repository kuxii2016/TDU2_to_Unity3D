using System.Collections.Concurrent;

namespace TDU2.Online.Net
{
    /// <summary>Basis: Paket mit Empfangszeit in eine Warteschlange legen (begrenzt), der Hauptthread holt sie ab.</summary>
    public abstract class QueuedPacketIn : IUdpPacketIn
    {
        public readonly ConcurrentQueue<(byte[] data, double t)> Queue = new ConcurrentQueue<(byte[], double)>();
        public abstract byte Type { get; }
        public abstract int MinLength { get; }
        protected abstract int Capacity { get; }

        public void Handle(byte[] data, double receivedAt)
        {
            if (Queue.Count < Capacity) Queue.Enqueue((data, receivedAt));
        }
    }
}
