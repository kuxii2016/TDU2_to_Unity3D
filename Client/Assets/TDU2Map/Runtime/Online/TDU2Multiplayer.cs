using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Online
{
    /// <summary>
    /// Spieler-Synchronisation über UDP (TDU2Server udpPort).
    ///   Senden (~20/s): Position/Richtung/Geschwindigkeit (Spielkoordinaten), Lenkung, Drehzahl/Motormoment, Auto (DB-Id) + Lack,
    ///   Lichter (Licht, Bremse, Rückwärts, Blinker, Warnblinker, Lichthupe), Hupe, Sendezeit.
    ///   Darstellung ohne Ruckeln:
    ///     * Zeitbasis des Senders (Sendezeit im Paket); Uhrversatz = kleinster (Ankunft − Sendezeit) der letzten Pakete,
    ///     * Puffer passt sich an (Grundverzögerung + 2,5 × Jitter, 50…400 ms),
    ///     * Hermite-Interpolation mit den Geschwindigkeiten (Kurven statt Ecken), kurze Extrapolation bei Paketverlust,
    ///     * Korrekturen werden weich ausgeglichen statt zu springen (Teleport > 25 m springt).
    ///   Mitspieler-Autos: Original-Modell + Lack, Räder lenken/drehen, Lichter, Motor-/Hupen-Sound (3D), Kollision
    ///   (kinematischer Körper – das eigene Auto prallt ab), Name über dem Auto.
    /// Paket (Client -> Server): [1][u16 Spieler][u32 Schlüssel][Zustand], Server -> Client: [2][u16 Spieler][Zustand].
    /// Zustand: +0 u32 seq, +4 i32 carId, +8 u8 Lack, +9 u8 Flags (1 im Auto, 2 Bremse, 4 rückwärts, 8 Blinker l, 16 Blinker r,
    /// 32 Warnblinker), +10 u8 Licht, +11 u8 Flags2 (1 Hupe, 2 Lichthupe), +12 Pos, +24 Vorne, +36 Oben, +48 Geschw. (je xyz),
    /// +60 Lenkwinkel, +64 km/h, +68 Drehzahl, +72 Motormoment, +76 u32 Sendezeit (ms), +80 u8 Insel.
    /// Erweiterung Lackierung/Beklebung (Sticker-Shop, ältere Clients ignorieren sie): +81 u8 Version (1), +82 u32 Hash der
    /// codierten <see cref="CarLivery"/> (0 = keine), +86 u16 Gesamtlänge, +88 u16 Versatz, +90 u8 Länge, +91 Daten (≤ 96 Byte).
    /// Jedes Paket trägt das nächste Stück (Karussell) – nach ~1 s haben alle Mitspieler die Beklebung, Paketverlust egal.
    /// </summary>
    public sealed class TDU2Multiplayer : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        /// <summary>
        /// Private Sitzung (z.B. Rennschul-Prüfung): man wird ohne Auto gemeldet (für die anderen unsichtbar) und sieht selbst
        /// keine Mitspieler. Chat und Verbindung bleiben bestehen.
        /// </summary>
        public static bool Hidden;
        /// <summary>Mitspieler nicht anzeigen (z.B. im Sticker-Shop: nur das eigene Auto), man selbst bleibt verbunden.</summary>
        public static bool HideRemotes;
        /// <summary>Eigene Insel als Code im Zustand (+80): 1 Ibiza, 2 Hawaii, 0 unbekannt.</summary>
        public static int LocalIsland { get; private set; }

        public static int IslandCode(string islandName)
        {
            if (string.IsNullOrEmpty(islandName)) return 0;
            if (islandName.StartsWith("i", StringComparison.OrdinalIgnoreCase)) return 1;
            if (islandName.StartsWith("h", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        /// <summary>Spieler auf derselben Insel (unbekannt zählt als gleich – alter Client/Server).</summary>
        public static bool SameIsland(int other) => other == 0 || LocalIsland == 0 || other == LocalIsland;

        // Einstellungen: TDU2GameData (Online Manager)
        static TDU2GameData GD => TDU2GameData.Instance;
        float SendRate => GD != null ? GD.SendRate : 20f;
        float BaseDelay => GD != null ? GD.InterpDelay : 0.12f;
        float Timeout => GD != null ? GD.Timeout : 4f;
        float MaxDistance => GD != null ? GD.MaxDistance : 3000f;
        bool Names => GD == null || GD.ShowNames;
        bool Collisions => GD == null || GD.PlayerCollisions;
        bool Sounds => GD == null || GD.PlayerSounds;

        const int StateSize = 81;      // +80: Insel (1 Ibiza, 2 Hawaii)
        const int StateSizeOld = 80;

        struct Snap { public double T; public uint Seq; public Vector3 P, F, U, V; public float Steer, Kmh, Rpm, Torque; public byte Flags, Head, Flags2; }

        sealed class Remote
        {
            public int Id;
            public int CarId = -1, Color = -1;
            public GameObject Go;
            public CarModel Model;
            public CarAssets Assets;
            public TDU2VehicleLights Lights;
            public TDU2VehicleAudio Audio;
            public Rigidbody Body;
            public Task<CarAssets> Load;
            public int LoadCarId, LoadColor;
            public readonly List<Snap> Snaps = new List<Snap>();
            public uint LastSeq;
            public float LastPacket;
            public float Spin;
            public (Transform stirrup, Quaternion sBase, float sign, Transform disc, Quaternion dBase, float radius, bool front)[] Wheels;
            // Zeitbasis
            public readonly Queue<double> Lag = new Queue<double>();   // Ankunft − Sendezeit
            public double Offset; public double Jitter; public bool HasOffset;
            // Darstellung
            public bool HasVis; public Vector3 VisPos, VisVel; public Quaternion VisRot = Quaternion.identity;
            public Vector3 TargetWorld; public Quaternion TargetRot = Quaternion.identity;
            public Snap Cur;
            // Lackierung/Beklebung (UDP-Karussell)
            public uint LiveryHash; public CarLivery Livery;
            public uint BufHash; public byte[] Buf; public bool[] Got; public int GotCount;
            public uint AppliedHash = uint.MaxValue;
        }

        readonly Dictionary<int, Remote> _remotes = new Dictionary<int, Remote>();
        readonly ConcurrentQueue<(byte[] data, double t)> _inbox = new ConcurrentQueue<(byte[], double)>();
        UdpClient _udp;
        Thread _recv;
        volatile bool _run;
        IPEndPoint _server;
        string _serverKey;
        float _nextSend;
        uint _seq;
        Vector3 _lastPos; float _lastPosT; bool _hasLast;
        const int LiveryChunk = 96, LiveryExt = 10;
        readonly byte[] _out = new byte[7 + StateSize + LiveryExt + LiveryChunk];
        int _livOffset;
        string _livCar; byte[] _livBlob; uint _livHash;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        double Now => _clock.Elapsed.TotalSeconds;

        void Update()
        {
            if (Manager == null) Manager = GetComponent<TDU2VehicleManager>();
            if (Manager == null) return;
            LocalIsland = IslandCode(Manager.Streamer != null && Manager.Streamer.Index != null ? Manager.Streamer.Index.IslandName : null);
            EnsureSocket();
            Receive();
            ReceiveChat();
            if (_udp != null && TDU2Session.LoggedIn && TDU2Session.PlayerId != 0 && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + 1f / Mathf.Max(2f, SendRate);
                SendState();
            }
            UpdateRemotes();
        }

        // ------------------------------------------------------------------ Netz
        void EnsureSocket()
        {
            var gd = GD;
            if (!TDU2Session.LoggedIn || gd == null) { CloseSocket(); return; }
            int port = TDU2Session.UdpPort > 0 ? TDU2Session.UdpPort : gd.UdpPort;
            string key = gd.ServerHost + ":" + port;
            if (key == _serverKey) return;
            CloseSocket();
            _serverKey = key;
            try
            {
                var addrs = Dns.GetHostAddresses(gd.ServerHost);
                IPAddress ip = null;
                foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                if (ip == null && addrs.Length > 0) ip = addrs[0];
                if (ip == null) return;
                _server = new IPEndPoint(ip, port);
                _udp = new UdpClient(ip.AddressFamily);
                try { _udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch (Exception) { }   // Windows: ICMP "Port nicht erreichbar" ignorieren
                _run = true;
                var udp = _udp;
                _recv = new Thread(() => ReceiveLoop(udp)) { IsBackground = true, Name = "TDU2 UDP" };
                _recv.Start();
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] UDP: " + ex.Message); _udp = null; }
        }

        void ReceiveLoop(UdpClient udp)
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_run)
            {
                try
                {
                    var d = udp.Receive(ref any);
                    if (d != null && d.Length >= 3 + StateSizeOld && d[0] == 2 && _inbox.Count < 4000) _inbox.Enqueue((d, Now));
                    else if (d != null && d.Length >= 8 && d[0] == 3 && _chatIn.Count < 200) _chatIn.Enqueue(d);
                    else if (d != null && d.Length >= 10 && d[0] == 6 && TrafficInbox.Count < 2000) TrafficInbox.Enqueue((d, Now));
                }
                catch (SocketException) { if (!_run) break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { if (!_run) break; }
            }
        }

        void CloseSocket()
        {
            _run = false;
            try { _udp?.Close(); } catch (Exception) { }
            _udp = null; _recv = null; _serverKey = null;
        }

        void OnDisable() { CloseSocket(); }
        void OnDestroy()
        {
            CloseSocket();
            foreach (var r in _remotes.Values) DestroyRemote(r);
            _remotes.Clear();
        }

        void SendState()
        {
            var root = Manager.Streamer?.MapRoot;
            if (root == null) return;
            var veh = Manager.Current;
            var lights = Manager.Lights;
            var inter = Manager.GetComponent<TDU2Interiors>();
            bool inCar = veh != null && veh.Car != null && (inter == null || !inter.Inside) && !Hidden;
            var b = _out;
            b[0] = 1;
            int pid = TDU2Session.PlayerId;
            b[1] = (byte)pid; b[2] = (byte)(pid >> 8);
            WriteU32(b, 3, TDU2Session.UdpKey);
            int o = 7;
            WriteU32(b, o, ++_seq);
            WriteI32(b, o + 4, inCar ? veh.Car.Id : 0);
            b[o + 8] = (byte)Mathf.Clamp(Manager.ColorIndex, 0, 255);
            byte flags = 0, flags2 = 0;
            if (inCar) flags |= 1;
            if (inCar && veh.Brake > 0.05f) flags |= 2;
            if (inCar && veh.Gear < 0) flags |= 4;
            if (lights != null && lights.IndicatorLeft) flags |= 8;
            if (lights != null && lights.IndicatorRight) flags |= 16;
            if (lights != null && lights.Hazard) flags |= 32;
            if (inCar && Manager.HornOn) flags2 |= 1;
            if (lights != null && lights.Flash) flags2 |= 2;
            b[o + 9] = flags;
            b[o + 10] = (byte)(lights != null ? Mathf.Clamp(lights.Headlights, 0, 2) : 0);
            b[o + 11] = flags2;
            Vector3 p = Vector3.zero, f = Vector3.forward, u = Vector3.up, v = Vector3.zero;
            float steer = 0f, kmh = 0f, rpm = 0f, torque = 0f;
            if (inCar)
            {
                var t = veh.transform;
                p = root.InverseTransformPoint(t.position);
                f = root.InverseTransformVector(t.forward).normalized;
                u = root.InverseTransformVector(t.up).normalized;
                float now = Time.time;
                if (_hasLast && now - _lastPosT > 1e-3f) v = (p - _lastPos) / (now - _lastPosT);
                if (v.sqrMagnitude > 200f * 200f) v = Vector3.zero;   // Teleport
                _lastPos = p; _lastPosT = now; _hasLast = true;
                steer = veh.SteeringAngle; kmh = veh.SpeedKmh; rpm = veh.Rpm;
                torque = veh.EngineTorque * (veh.Gear == 0 ? 0.3f : 1f);
            }
            else _hasLast = false;
            WriteV3(b, o + 12, p); WriteV3(b, o + 24, f); WriteV3(b, o + 36, u); WriteV3(b, o + 48, v);
            WriteF(b, o + 60, steer); WriteF(b, o + 64, kmh); WriteF(b, o + 68, rpm); WriteF(b, o + 72, torque);
            WriteU32(b, o + 76, (uint)(Now * 1000.0));
            b[o + 80] = (byte)LocalIsland;
            int len = o + StateSize;
            len = WriteLivery(b, len, inCar ? veh.Car.FileName : null);
            try { _udp.Send(b, len, _server); } catch (Exception) { }
        }

        /// <summary>Nächstes Stück der eigenen Lackierung anhängen (Karussell). Liefert die neue Paketlänge.</summary>
        int WriteLivery(byte[] b, int o, string carFile)
        {
            byte[] blob = carFile != null ? TDU2Liveries.Blob(carFile) : null;
            if (!string.Equals(carFile, _livCar, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(blob, _livBlob))
            {
                _livCar = carFile; _livBlob = blob; _livHash = CarLivery.Hash(blob); _livOffset = 0;
            }
            int total = blob != null ? blob.Length : 0;
            int off = total > 0 ? _livOffset % total : 0;
            int n = Math.Min(LiveryChunk, total - off);
            b[o] = 1;
            WriteU32(b, o + 1, _livHash);
            b[o + 5] = (byte)total; b[o + 6] = (byte)(total >> 8);
            b[o + 7] = (byte)off; b[o + 8] = (byte)(off >> 8);
            b[o + 9] = (byte)n;
            if (n > 0) Buffer.BlockCopy(blob, off, b, o + LiveryExt, n);
            _livOffset = total > 0 ? (off + n) % total : 0;
            return o + LiveryExt + n;
        }

        static void ReadLivery(Remote r, byte[] d, int o)
        {
            if (d.Length < o + LiveryExt || d[o] != 1) return;
            uint hash = BitConverter.ToUInt32(d, o + 1);
            int total = d[o + 5] | (d[o + 6] << 8), off = d[o + 7] | (d[o + 8] << 8), n = d[o + 9];
            if (hash == 0 || total == 0) { r.LiveryHash = 0; r.Livery = null; r.Buf = null; return; }
            if (hash == r.LiveryHash) return;   // schon vollständig
            if (r.BufHash != hash || r.Buf == null || r.Buf.Length != total) { r.BufHash = hash; r.Buf = new byte[total]; r.Got = new bool[total]; r.GotCount = 0; }
            if (n <= 0 || off + n > total || d.Length < o + LiveryExt + n) return;
            Buffer.BlockCopy(d, o + LiveryExt, r.Buf, off, n);
            for (int i = off; i < off + n; i++) if (!r.Got[i]) { r.Got[i] = true; r.GotCount++; }
            if (r.GotCount < total) return;
            if (CarLivery.Hash(r.Buf) != hash) { r.Buf = null; r.BufHash = 0; return; }
            r.Livery = CarLivery.Decode(r.Buf);
            r.LiveryHash = hash;
            r.Buf = null; r.Got = null;
        }

        void Receive()
        {
            var gd = GD;
            while (_inbox.TryDequeue(out var pk))
            {
                var d = pk.data;
                int id = d[1] | (d[2] << 8);
                if (id == TDU2Session.PlayerId) continue;
                int o = 3;
                var s = new Snap
                {
                    Seq = BitConverter.ToUInt32(d, o),
                    Flags = d[o + 9],
                    Head = d[o + 10],
                    Flags2 = d[o + 11],
                    P = ReadV3(d, o + 12),
                    F = ReadV3(d, o + 24),
                    U = ReadV3(d, o + 36),
                    V = ReadV3(d, o + 48),
                    Steer = BitConverter.ToSingle(d, o + 60),
                    Kmh = BitConverter.ToSingle(d, o + 64),
                    Rpm = BitConverter.ToSingle(d, o + 68),
                    Torque = BitConverter.ToSingle(d, o + 72),
                    T = BitConverter.ToUInt32(d, o + 76) / 1000.0,   // Sendezeit (Uhr des Senders)
                };
                int carId = BitConverter.ToInt32(d, o + 4), color = d[o + 8];
                int island = d.Length >= o + 81 ? d[o + 80] : 0;
                if (!_remotes.TryGetValue(id, out var r)) { r = new Remote { Id = id }; _remotes[id] = r; }
                if (s.Seq <= r.LastSeq && r.LastSeq - s.Seq < 1000) continue;   // alt/doppelt
                if (s.Seq < r.LastSeq) { r.Snaps.Clear(); r.Lag.Clear(); r.HasOffset = false; }   // Neustart des Senders
                r.LastSeq = s.Seq;
                r.LastPacket = Time.unscaledTime;

                // Uhrversatz/Jitter
                double lag = pk.t - s.T;
                r.Lag.Enqueue(lag);
                while (r.Lag.Count > 60) r.Lag.Dequeue();
                double min = double.MaxValue; foreach (var l in r.Lag) if (l < min) min = l;
                r.Offset = min; r.HasOffset = true;
                r.Jitter = r.Jitter * 0.9 + (lag - min) * 0.1;

                if (d.Length >= o + StateSize + LiveryExt) ReadLivery(r, d, o + StateSize);
                r.Snaps.Add(s);
                if (r.Snaps.Count > 40) r.Snaps.RemoveRange(0, r.Snaps.Count - 40);
                bool inCar = (s.Flags & 1) != 0 && carId != 0 && SameIsland(island);   // andere Insel: nicht zeigen
                if (inCar) { r.CarId = carId; r.Color = color; } else r.CarId = 0;

                if (gd != null)
                {
                    var p = gd.Player(id);
                    if (island != 0) p.Island = island;
                    p.InCar = inCar;
                    if (!SameIsland(island)) p.HasPos = false;
                    if (inCar) { p.CarId = carId; p.Color = color; p.GamePos = s.P; p.GameForward = s.F; p.HasPos = true; p.LastUpdate = Time.unscaledTime; }
                    var db = Manager.Database;
                    if (db != null && p.CarId != 0 && (p.Car == null || p.Car.Id != p.CarId)) p.Car = db.ById.TryGetValue(p.CarId, out var c) ? c : null;
                }
            }
        }

        // ------------------------------------------------------------------ Chat (UDP, über den Server an alle)
        // Client -> Server: [4][u16 Spieler][u32 Schlüssel][u32 Nachricht-Nr][UTF-8 Text]   (zweimal gesendet gegen Paketverlust)
        // Server -> Client: [3][u16 Spieler][u32 Nachricht-Nr][UTF-8 Name][0][UTF-8 Text]
        readonly ConcurrentQueue<byte[]> _chatIn = new ConcurrentQueue<byte[]>();
        readonly Queue<(int pid, uint msg)> _chatSeen = new Queue<(int, uint)>();
        uint _chatSeq, _chatBase;
        float _chatResendAt = -1f;
        byte[] _chatResend;
        /// <summary>Chatnachricht empfangen (Spieler-Nr., Name, Text) – auch die eigene (Bestätigung vom Server).</summary>
        public event Action<int, string, string> ChatReceived;

        public bool SendChat(string text)
        {
            if (_udp == null || _server == null || !TDU2Session.LoggedIn || TDU2Session.PlayerId == 0 || string.IsNullOrWhiteSpace(text)) return false;
            var t = System.Text.Encoding.UTF8.GetBytes(text.Trim());
            int len = Math.Min(t.Length, 300);
            var b = new byte[11 + len];
            b[0] = 4;
            int pid = TDU2Session.PlayerId;
            b[1] = (byte)pid; b[2] = (byte)(pid >> 8);
            WriteU32(b, 3, TDU2Session.UdpKey);
            if (_chatBase == 0) _chatBase = (uint)UnityEngine.Random.Range(1, 0xFFFF);
            WriteU32(b, 7, (_chatBase << 16) | (++_chatSeq & 0xFFFF));   // Nachricht-Nr. (Empfänger verwerfen Doppelte)
            Buffer.BlockCopy(t, 0, b, 11, len);
            try { _udp.Send(b, b.Length, _server); } catch (Exception) { return false; }
            _chatResend = b; _chatResendAt = Time.unscaledTime + 0.15f;
            return true;
        }

        void ReceiveChat()
        {
            if (_chatResend != null && _chatResendAt > 0f && Time.unscaledTime >= _chatResendAt)
            {
                try { _udp?.Send(_chatResend, _chatResend.Length, _server); } catch (Exception) { }
                _chatResend = null; _chatResendAt = -1f;
            }
            while (_chatIn.TryDequeue(out var d))
            {
                int pid = d[1] | (d[2] << 8);
                uint msg = BitConverter.ToUInt32(d, 3);
                bool dup = false;
                foreach (var x in _chatSeen) if (x.pid == pid && x.msg == msg) { dup = true; break; }
                if (dup) continue;
                _chatSeen.Enqueue((pid, msg));
                while (_chatSeen.Count > 128) _chatSeen.Dequeue();
                int z = Array.IndexOf(d, (byte)0, 7);
                if (z < 0) continue;
                string nick = System.Text.Encoding.UTF8.GetString(d, 7, z - 7);
                string text = System.Text.Encoding.UTF8.GetString(d, z + 1, d.Length - z - 1);
                try { ChatReceived?.Invoke(pid, nick, text); } catch (Exception ex) { Debug.LogWarning("[TDU2] Chat: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ Verkehr (TDU2Traffic)
        // Client -> Server: [5][u16 Spieler][u32 Schlüssel][Nutzdaten], Server -> Clients: [6][u16 Absender][Nutzdaten]
        /// <summary>Empfangene Verkehrspakete ([6][u16 Absender][Nutzdaten]) mit Ankunftszeit (<see cref="Clock"/>).</summary>
        public readonly ConcurrentQueue<(byte[] data, double t)> TrafficInbox = new ConcurrentQueue<(byte[], double)>();
        /// <summary>Uhr dieses Clients (Sekunden) – Zeitbasis der Pakete.</summary>
        public double Clock => Now;
        public bool CanSend => _udp != null && _server != null && TDU2Session.LoggedIn && TDU2Session.PlayerId != 0;

        public bool SendTraffic(byte[] payload, int len)
        {
            if (!CanSend || payload == null || len <= 0 || len > 1250) return false;
            var b = new byte[7 + len];
            b[0] = 5;
            int pid = TDU2Session.PlayerId;
            b[1] = (byte)pid; b[2] = (byte)(pid >> 8);
            WriteU32(b, 3, TDU2Session.UdpKey);
            Buffer.BlockCopy(payload, 0, b, 7, len);
            try { _udp.Send(b, b.Length, _server); return true; } catch (Exception) { return false; }
        }

        /// <summary>Mitspieler-Autos (Darstellung) in Spielkoordinaten: Id, Position, Geschwindigkeit, Richtung, halbe Länge – für die Verkehrs-KI.</summary>
        public void RemoteVehicles(List<(int id, Vector3 pos, Vector3 vel, Vector3 fwd, float halfLength)> outList)
        {
            outList.Clear();
            foreach (var r in _remotes.Values)
            {
                if (r.Go == null || !r.HasVis || r.CarId <= 0) continue;
                float hl = r.Model != null ? r.Model.BodyBounds.extents.z : 2.2f;
                outList.Add((r.Id, r.VisPos, r.VisVel, r.Cur.F, hl));
            }
        }

        // ------------------------------------------------------------------ Mitspieler-Autos
        void UpdateRemotes()
        {
            var root = Manager.Streamer?.MapRoot;
            var gd = GD;
            var dead = new List<int>();
            float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
            foreach (var r in _remotes.Values)
            {
                bool gone = Time.unscaledTime - r.LastPacket > Timeout || !TDU2Session.LoggedIn || (gd != null && !gd.Online);
                if (gone) { dead.Add(r.Id); continue; }
                if (root == null) continue;

                bool want = r.CarId > 0 && !Hidden && !HideRemotes;
                if (!want) { if (r.Go != null) DestroyModel(r); r.HasVis = false; continue; }
                bool modelOk = r.Go != null && r.Assets != null && r.Assets.Car.Id == r.CarId && r.LoadColor == r.Color;
                if (!modelOk && r.Load == null) StartLoad(r);
                PollLoad(r);
                if (r.Go == null) continue;
                // Lackierung/Beklebung des Mitspielers (sobald vollständig empfangen)
                if (r.Model != null && r.AppliedHash != r.LiveryHash)
                {
                    r.AppliedHash = r.LiveryHash;
                    if (r.Livery != null) { TDU2StickerLibrary.Ensure(Manager.BnkDir); TDU2LiveryView.For(r.Model)?.Show(r.Livery); }
                    else { var v = r.Model.Visual != null ? r.Model.Visual.GetComponent<TDU2LiveryView>() : null; if (v != null) v.Show(null); }
                }

                if (!Sample(r, out var s)) continue;
                r.Cur = s;

                // weiche Korrektur: Vorhersage aus der letzten Darstellung, Abweichung klingt ab
                if (!r.HasVis || (s.P - r.VisPos).sqrMagnitude > 25f * 25f) { r.VisPos = s.P; r.VisVel = s.V; r.HasVis = true; }
                else
                {
                    var predicted = r.VisPos + r.VisVel * dt;
                    var err = predicted - s.P;
                    var np = s.P + err * Mathf.Exp(-dt * 12f);
                    r.VisVel = (np - r.VisPos) / dt;
                    r.VisPos = np;
                }
                var pos = root.TransformPoint(r.VisPos);
                var fwd = root.TransformVector(s.F); var up = root.TransformVector(s.U);
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
                var rot = Quaternion.LookRotation(fwd.normalized, up.normalized);
                r.VisRot = Quaternion.Slerp(r.VisRot, rot, 1f - Mathf.Exp(-dt * 20f));
                if (Quaternion.Angle(r.VisRot, rot) > 60f) r.VisRot = rot;

                bool far = Camera.main != null && (Camera.main.transform.position - pos).sqrMagnitude > MaxDistance * MaxDistance;
                if (r.Go.activeSelf == far) r.Go.SetActive(!far);
                r.TargetWorld = pos; r.TargetRot = r.VisRot;
                r.Go.transform.SetPositionAndRotation(pos, r.VisRot);   // kinematischer Körper: Physik übernimmt die Lage
                if (r.Body != null && r.Body.detectCollisions != Collisions) r.Body.detectCollisions = Collisions;

                // Räder
                float speed = s.Kmh / 3.6f;
                if (Vector3.Dot(s.V, s.F) < 0f) speed = -speed;
                if (r.Wheels != null)
                    foreach (var w in r.Wheels)
                    {
                        if (w.stirrup != null && w.front) w.stirrup.localRotation = w.sBase * Quaternion.AngleAxis(s.Steer * w.sign, Vector3.up);
                        if (w.disc != null) w.disc.localRotation = w.dBase * Quaternion.AngleAxis(r.Spin * Mathf.Rad2Deg / Mathf.Max(0.2f, w.radius), Vector3.right);
                    }
                r.Spin += speed * dt;

                // Lichter
                if (r.Lights != null)
                {
                    r.Lights.Headlights = s.Head;
                    r.Lights.ExtBrake = (s.Flags & 2) != 0;
                    r.Lights.ExtReverse = (s.Flags & 4) != 0;
                    r.Lights.IndicatorLeft = (s.Flags & 8) != 0;
                    r.Lights.IndicatorRight = (s.Flags & 16) != 0;
                    r.Lights.Hazard = (s.Flags & 32) != 0;
                    r.Lights.Flash = (s.Flags2 & 2) != 0;
                    var mine = Manager.Lights;
                    if (mine != null) { r.Lights.Brightness = mine.Brightness; r.Lights.LensBrightness = mine.LensBrightness; }
                }
                // Sound
                if (r.Audio != null)
                {
                    bool audible = Sounds && !far && Time.timeScale > 0f;
                    r.Audio.enabled = audible;
                    r.Audio.ExtRpm = s.Rpm; r.Audio.ExtKmh = Mathf.Abs(s.Kmh); r.Audio.ExtTorque = s.Torque;
                    r.Audio.MasterVolume = Manager.Volume;
                    r.Audio.SetHorn(audible && (s.Flags2 & 1) != 0);
                    if (!audible) foreach (var src in r.Go.GetComponents<AudioSource>()) src.volume = 0f;
                }
            }
            foreach (var id in dead) { DestroyRemote(_remotes[id]); _remotes.Remove(id); }
        }

        /// <summary>Zustand zur Darstellungszeit (Senderuhr − adaptive Verzögerung): Hermite zwischen zwei Paketen, sonst kurz extrapolieren.</summary>
        bool Sample(Remote r, out Snap s)
        {
            s = default;
            if (r.Snaps.Count == 0 || !r.HasOffset) return false;
            double delay = Mathf.Clamp((float)(BaseDelay * 0.5 + r.Jitter * 2.5 + 1.0 / Mathf.Max(2f, SendRate)), 0.05f, 0.4f);
            double t = Now - r.Offset - delay;   // in Senderzeit
            var snaps = r.Snaps;
            if (t <= snaps[0].T) { s = snaps[0]; return true; }
            for (int i = snaps.Count - 1; i > 0; i--)
            {
                var a = snaps[i - 1]; var b = snaps[i];
                if (t < a.T || t > b.T) continue;
                double span = Math.Max(1e-4, b.T - a.T);
                float k = (float)((t - a.T) / span);
                s = b;
                if ((b.P - a.P).sqrMagnitude > 25f * 25f) { s.P = b.P; return true; }   // Teleport
                float h = (float)span;
                float k2 = k * k, k3 = k2 * k;
                // kubische Hermite-Kurve mit den Geschwindigkeiten
                s.P = (2 * k3 - 3 * k2 + 1) * a.P + (k3 - 2 * k2 + k) * h * a.V + (-2 * k3 + 3 * k2) * b.P + (k3 - k2) * h * b.V;
                s.V = Vector3.Lerp(a.V, b.V, k);
                s.F = Vector3.Slerp(a.F, b.F, k);
                s.U = Vector3.Slerp(a.U, b.U, k);
                s.Steer = Mathf.Lerp(a.Steer, b.Steer, k);
                s.Kmh = Mathf.Lerp(a.Kmh, b.Kmh, k);
                s.Rpm = Mathf.Lerp(a.Rpm, b.Rpm, k);
                s.Torque = Mathf.Lerp(a.Torque, b.Torque, k);
                return true;
            }
            // neuer als das letzte Paket: höchstens 0,5 s weiterrechnen
            s = snaps[snaps.Count - 1];
            float ex = Mathf.Clamp((float)(t - s.T), 0f, 0.5f);
            s.P += s.V * ex;
            return true;
        }

        void StartLoad(Remote r)
        {
            var db = Manager.Database;
            if (db == null || string.IsNullOrEmpty(Manager.BnkDir) || !db.ById.TryGetValue(r.CarId, out var car)) return;
            car = Manager.FindCar(car.FileName) ?? car;
            string bnk = Manager.BnkDir; int color = r.Color; bool snd = Sounds;
            r.LoadCarId = r.CarId; r.LoadColor = color;
            r.Load = Task.Run(() => CarAssets.Load(bnk, db, car, color, snd));
        }

        void PollLoad(Remote r)
        {
            if (r.Load == null || !r.Load.IsCompleted) return;
            var t = r.Load; r.Load = null;
            if (t.IsFaulted) { Debug.LogWarning("[TDU2] Mitspieler-Auto: " + t.Exception?.GetBaseException().Message); return; }
            if (r.LoadCarId != r.CarId || r.LoadColor != r.Color) { t.Result?.DestroyTextures(); return; }   // inzwischen gewechselt
            DestroyModel(r);
            try { Build(r, t.Result); }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Mitspieler-Auto aufbauen: " + ex.Message); t.Result?.DestroyTextures(); }
        }

        void Build(Remote r, CarAssets a)
        {
            var go = new GameObject("TDU2 Mitspieler #" + r.Id + " (" + a.Car.ModelName + ")");
            go.layer = 2;   // Ignore Raycast: Radstrahlen des eigenen Autos treffen es nicht, Kollision bleibt
            r.Go = go; r.Assets = a; r.HasVis = false;
            r.AppliedHash = uint.MaxValue;   // Lackierung neu auflegen
            r.Model = CarModel.Build(a, go.transform);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;

            // Kollision: kinematischer Körper mit der Karosserie-Box
            var bb = r.Model.BodyBounds;
            var box = go.AddComponent<BoxCollider>();
            box.center = bb.center; box.size = new Vector3(bb.size.x * 0.96f, Mathf.Max(0.4f, bb.size.y * 0.9f), bb.size.z * 0.98f);
            r.Body = go.AddComponent<Rigidbody>();
            r.Body.isKinematic = true;
            r.Body.interpolation = RigidbodyInterpolation.None;
            r.Body.detectCollisions = Collisions;

            // Räder wie TDU2Vehicle (STIRRUP_xx Lenkung, DISC_xx Drehung)
            var wheels = new List<(Transform, Quaternion, float, Transform, Quaternion, float, bool)>();
            foreach (var n in new[] { "FL", "FR", "RL", "RR" })
            {
                var st = r.Model.Find("STIRRUP_" + n); var disc = r.Model.Find("DISC_" + n);
                float sign = 1f;
                if (st != null && st.parent != null) sign = (go.transform.worldToLocalMatrix * st.parent.localToWorldMatrix).determinant < 0f ? -1f : 1f;
                if (st != null)
                {
                    var fwd = go.transform.forward; var upv = go.transform.up;
                    var q0 = st.rotation; var b0 = st.localRotation;
                    st.localRotation = b0 * Quaternion.AngleAxis(10f * sign, Vector3.up);
                    var v = (st.rotation * Quaternion.Inverse(q0)) * fwd;
                    st.localRotation = b0;
                    if (Vector3.SignedAngle(fwd, Vector3.ProjectOnPlane(v, upv), upv) < 0f) sign = -sign;
                }
                float radius = a.Rim != null ? a.Rim.TireRadius(n[0] == 'F') : 0.33f;
                wheels.Add((st, st != null ? st.localRotation : Quaternion.identity, sign, disc, disc != null ? disc.localRotation : Quaternion.identity, radius, n[0] == 'F'));
            }
            r.Wheels = wheels.ToArray();

            r.Lights = go.AddComponent<TDU2VehicleLights>();
            r.Lights.External = true;
            r.Lights.Setup(null, r.Model);

            if (a.Sound != null)
            {
                r.Audio = go.AddComponent<TDU2VehicleAudio>();
                r.Audio.External = true;
                r.Audio.MasterVolume = Manager.Volume;
                r.Audio.Setup(a);
            }
        }

        void DestroyModel(Remote r)
        {
            r.Model?.Destroy(); r.Model = null;
            r.Assets?.DestroyTextures(); r.Assets = null;
            if (r.Go != null) UnityUtil.SafeDestroy(r.Go);
            r.Go = null; r.Lights = null; r.Audio = null; r.Body = null; r.Wheels = null; r.HasVis = false;
        }

        void DestroyRemote(Remote r)
        {
            DestroyModel(r);
            if (r.Load != null) { var t = r.Load; r.Load = null; t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result?.DestroyTextures(); }); }
        }

        /// <summary>Mitspieler-Autos (für den Online Manager im Inspector): Id, geladen, Auto, Alter des letzten Pakets, Verzögerung.</summary>
        public IEnumerable<(int id, bool loaded, string car, float age)> RemoteInfo()
        {
            foreach (var r in _remotes.Values)
                yield return (r.Id, r.Go != null, (r.Assets?.Car?.ModelName ?? (r.Load != null ? "lädt …" : "-")) + $"  Jitter {r.Jitter * 1000:0} ms", Time.unscaledTime - r.LastPacket);
        }

        // ------------------------------------------------------------------ Namen über den Autos
        GUIStyle _tag, _tagShadow;

        void OnGUI()
        {
            if (!Names || Event.current.type != EventType.Repaint) return;
            var cam = Camera.main;
            var gd = GD;
            if (cam == null || gd == null || Time.timeScale <= 0f || TDU2BigMap.IsOpen) return;
            if (_tag == null)
            {
                _tag = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, fontStyle = FontStyle.Bold, richText = false };
                _tag.normal.textColor = Color.white;
                _tagShadow = new GUIStyle(_tag);
                _tagShadow.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
            }
            foreach (var r in _remotes.Values)
            {
                if (r.Go == null || !r.Go.activeSelf || r.Model == null) continue;
                var top = r.TargetWorld + r.Go.transform.up * (r.Model.BodyBounds.max.y + 0.6f);
                var sp = cam.WorldToScreenPoint(top);
                if (sp.z <= 0.5f || sp.z > 400f) continue;
                // verdeckt?
                var dir = top - cam.transform.position;
                if (UnityEngine.Physics.Raycast(cam.transform.position, dir.normalized, out var hit, dir.magnitude - 1f, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;
                string name = gd.Players.TryGetValue(r.Id, out var p) ? p.Nick : ("Spieler " + r.Id);
                int size = Mathf.RoundToInt(Mathf.Clamp(28f - sp.z * 0.06f, 12f, 26f) * Screen.height / 1080f);
                _tag.fontSize = size; _tagShadow.fontSize = size;
                float alpha = Mathf.Clamp01((400f - sp.z) / 100f);
                var rect = new Rect(sp.x - 200f, Screen.height - sp.y - size * 1.6f, 400f, size * 1.6f);
                var c = GUI.color; GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), name, _tagShadow);
                GUI.Label(rect, name, _tag);
                GUI.color = c;
            }
        }

        // ------------------------------------------------------------------ Binärhilfen
        static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static void WriteI32(byte[] b, int o, int v) => WriteU32(b, o, (uint)v);
        static void WriteF(byte[] b, int o, float f) { var t = BitConverter.GetBytes(f); Buffer.BlockCopy(t, 0, b, o, 4); }
        static void WriteV3(byte[] b, int o, Vector3 v) { WriteF(b, o, v.x); WriteF(b, o + 4, v.y); WriteF(b, o + 8, v.z); }
        static Vector3 ReadV3(byte[] d, int o) => new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
    }
}
