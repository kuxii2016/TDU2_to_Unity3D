using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace TDU2.Online.Net
{
    /// <summary>
    /// P2P zwischen den Spielern einer Lobby (der Server vermittelt nur, Verkehr kostet ihn dann nichts):
    ///
    ///  1. Vermittlung: "status" liefert je Lobby-Mitspieler seinen öffentlichen UDP-Endpunkt (wie ihn der Server sieht),
    ///     den lokalen Endpunkt (gleiches LAN / gleicher Router) und sein Token.
    ///  2. Hole-Punching: beide Seiten senden ~6 s lang alle 0,25 s Punch-Pakete an beide Endpunkte
    ///     [9][u16 eigene Nummer][u32 eigenes Token][u8 1 = Antwort]. Kommt eins an (Token passt), steht der direkte Weg –
    ///     auf genau dem Endpunkt, von dem es kam. Klappt es nicht, alle 20 s ein neuer Versuch.
    ///  3. Senden: Mitspieler mit direktem Weg bekommen Zustand/Verkehr direkt ([2]/[6][u16 Nummer][Daten], wie vom Server),
    ///     alle anderen über den Relay-Umschlag [10] des Servers (nur an sie). So gibt es keine Lücke: bis der direkte Weg
    ///     steht bzw. wenn er abreißt (3 s nichts gehört), läuft automatisch das Relay.
    ///  4. Empfang: direkte Pakete werden nur von bekannten Endpunkten mit passender Spielernummer angenommen.
    /// Symmetrisches NAT/CGNAT auf BEIDEN Seiten oder blockierende Firewalls: dann bleibt dieser Mitspieler auf dem Relay.
    /// </summary>
    public sealed class TDU2PeerMesh
    {
        public const float DirectTimeout = 3f;      // so lange ohne Paket vom Peer -> wieder Relay
        const float PunchDuration = 6f, PunchInterval = 0.25f, RetryInterval = 20f, KeepAlive = 1.5f;

        sealed class Peer
        {
            public int Id;
            public uint Token;
            public IPEndPoint Public, Local;
            public IPEndPoint Direct;               // bestätigter Weg (Empfangs-Thread setzt ihn)
            public double LastHeard = -100;         // Zeit des letzten Pakets vom Peer
            public double PunchUntil, NextPunch, NextRetry, LastSent;
            public readonly object Lock = new object();
        }

        readonly Func<double> _now;
        readonly ConcurrentDictionary<int, Peer> _peers = new ConcurrentDictionary<int, Peer>();
        readonly ConcurrentDictionary<IPEndPoint, int> _byEp = new ConcurrentDictionary<IPEndPoint, int>();
        UdpClient _udp;
        int _myId; uint _myToken;
        readonly byte[] _punch = new byte[8];

        public TDU2PeerMesh(Func<double> now) { _now = now; }

        /// <summary>Server erlaubt P2P und dieser Client nutzt es.</summary>
        public bool Active { get; private set; }
        public int PeerCount => _peers.Count;
        public int DirectCount { get { int n = 0; double t = _now(); foreach (var p in _peers.Values) if (IsDirect(p, t)) n++; return n; } }

        public void Bind(UdpClient udp) { _udp = udp; _byEp.Clear(); foreach (var p in _peers.Values) lock (p.Lock) { p.Direct = null; p.LastHeard = -100; } }

        /// <summary>Stand aus "status" übernehmen (Hauptthread): Peers anlegen/aktualisieren/entfernen.</summary>
        public void Update(bool serverAllows, int myId, long myToken, List<PlayerEntry> players)
        {
            Active = serverAllows && myId != 0;
            _myId = myId; _myToken = (uint)myToken;
            var seen = new HashSet<int>();
            if (Active && players != null)
                foreach (var e in players)
                {
                    if (e == null || e.id == 0 || e.id == myId || e.p2p == 0 || string.IsNullOrEmpty(e.ep)) continue;
                    var pub = ParseEp(e.ep);
                    if (pub == null) continue;
                    seen.Add(e.id);
                    var p = _peers.GetOrAdd(e.id, id => new Peer { Id = id });
                    lock (p.Lock)
                    {
                        bool changed = p.Public == null || !p.Public.Equals(pub) || p.Token != (uint)e.p2p;
                        p.Token = (uint)e.p2p;
                        p.Public = pub;
                        p.Local = ParseEp(e.lep);
                        if (changed) { p.PunchUntil = 0; p.NextRetry = 0; }   // neuer Endpunkt: gleich punchen
                    }
                }
            foreach (var id in new List<int>(_peers.Keys))
                if (!seen.Contains(id) && _peers.TryRemove(id, out var gone))
                    lock (gone.Lock) { if (gone.Direct != null) _byEp.TryRemove(gone.Direct, out _); }
        }

        static bool IsDirect(Peer p, double now) => p.Direct != null && now - p.LastHeard < DirectTimeout;

        public bool IsDirect(int id) => _peers.TryGetValue(id, out var p) && IsDirect(p, _now());

        /// <summary>Hauptthread, jedes Bild: Punch-Pakete senden, direkte Wege mit Keepalive offen halten.</summary>
        public void Tick()
        {
            if (!Active || _udp == null) return;
            double t = _now();
            foreach (var p in _peers.Values)
            {
                lock (p.Lock)
                {
                    if (IsDirect(p, t))
                    {
                        // NAT-Zuordnung offen halten, auch wenn gerade keine Daten fließen (Interieur, Menü)
                        if (t - p.LastSent > KeepAlive) { SendPunch(p.Direct, false); p.LastSent = t; }
                        continue;
                    }
                    if (t >= p.NextRetry) { p.PunchUntil = t + PunchDuration; p.NextRetry = t + RetryInterval; }
                    if (t < p.PunchUntil && t >= p.NextPunch)
                    {
                        p.NextPunch = t + PunchInterval;
                        if (p.Public != null) SendPunch(p.Public, false);
                        if (p.Local != null && !p.Local.Equals(p.Public)) SendPunch(p.Local, false);
                    }
                }
            }
        }

        void SendPunch(IPEndPoint to, bool reply)
        {
            if (to == null || _udp == null) return;
            var b = reply ? new byte[8] : _punch;   // Antworten kommen aus dem Empfangs-Thread: eigener Puffer
            b[0] = UdpPacketId.PeerPunch;
            b[1] = (byte)_myId; b[2] = (byte)(_myId >> 8);
            b[3] = (byte)_myToken; b[4] = (byte)(_myToken >> 8); b[5] = (byte)(_myToken >> 16); b[6] = (byte)(_myToken >> 24);
            b[7] = (byte)(reply ? 1 : 0);
            try { _udp.Send(b, 8, to); } catch (Exception) { }
        }

        /// <summary>Empfangs-Thread: Punch-Paket eines Peers. Token passt -&gt; direkter Weg über den Absender-Endpunkt.</summary>
        public void OnPunch(byte[] d, IPEndPoint from)
        {
            if (!Active || d == null || d.Length < 8 || from == null) return;
            int id = d[1] | (d[2] << 8);
            uint token = (uint)(d[3] | (d[4] << 8) | (d[5] << 16) | (d[6] << 24));
            if (!_peers.TryGetValue(id, out var p)) return;
            lock (p.Lock)
            {
                if (p.Token == 0 || token != p.Token) return;
                var ep = new IPEndPoint(from.Address, from.Port);
                if (p.Direct == null || !p.Direct.Equals(ep))
                {
                    if (p.Direct != null) _byEp.TryRemove(p.Direct, out _);
                    p.Direct = ep;
                    _byEp[ep] = id;
                }
                p.LastHeard = _now();
            }
            if (d[7] == 0) SendPunch(from, true);   // Anfrage beantworten: die Gegenseite weiß dann auch Bescheid
        }

        /// <summary>Empfangs-Thread: Datenpaket direkt von einem Peer? Nur bekannter Endpunkt mit passender Nummer.</summary>
        public bool AcceptDirect(IPEndPoint from, int senderId)
        {
            if (from == null || !_byEp.TryGetValue(from, out var id) || id != senderId || !_peers.TryGetValue(id, out var p)) return false;
            lock (p.Lock) p.LastHeard = _now();
            return true;
        }

        /// <summary>
        /// Paket an die Lobby senden: direkt an Peers mit Weg, der Rest über den Relay-Umschlag des Servers.
        /// peerPacket = [Typ][u16 eigene Nummer][Daten] (wie vom Server), payload = Daten (ab peerPacket[3]).
        /// include(id) = false: diesem Peer nichts senden (z.B. weit weg, nur jedes n-te Paket).
        /// forceServer: Umschlag auch ohne Relay-Empfänger senden (Zustand für Spielerliste/Lobby des Servers).
        /// </summary>
        public void Send(byte inner, byte[] peerPacket, int len, IPEndPoint server, int myId, uint udpKey, Func<int, bool> include, bool forceServer)
        {
            if (_udp == null) return;
            double t = _now();
            var relay = new List<int>();
            foreach (var p in _peers.Values)
            {
                if (include != null && !include(p.Id)) continue;
                IPEndPoint direct = null;
                lock (p.Lock) if (IsDirect(p, t)) { direct = p.Direct; p.LastSent = t; }
                if (direct != null) { try { _udp.Send(peerPacket, len, direct); } catch (Exception) { relay.Add(p.Id); } }
                else relay.Add(p.Id);
            }
            if (relay.Count == 0 && !forceServer) return;
            int n = Math.Min(relay.Count, 255);
            int plen = len - UdpPacketId.ServerHeader;
            var b = new byte[UdpPacketId.ClientHeader + 2 + 2 * n + plen];
            UdpPacketId.WriteClientHeader(b, UdpPacketId.RelayIn, myId, udpKey);
            b[7] = inner; b[8] = (byte)n;
            for (int i = 0; i < n; i++) { b[9 + 2 * i] = (byte)relay[i]; b[10 + 2 * i] = (byte)(relay[i] >> 8); }
            Buffer.BlockCopy(peerPacket, UdpPacketId.ServerHeader, b, 9 + 2 * n, plen);
            try { _udp.Send(b, b.Length, server); } catch (Exception) { }
        }

        public static IPEndPoint ParseEp(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int c = s.LastIndexOf(':');
            if (c <= 0) return null;
            string host = s.Substring(0, c).Trim('[', ']');
            if (!IPAddress.TryParse(host, out var ip) || !int.TryParse(s.Substring(c + 1), out int port) || port <= 0 || port > 65535) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return new IPEndPoint(ip, port);
        }
    }
}
