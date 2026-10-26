using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace MHK.Online
{
    /// <summary>
    /// The realtime UDP seat of one race (docs/protocol.md, "Realtime UDP"). A background thread resolves the server,
    /// pings it 4×/s, receives Pongs (clock offset from the minimum-RTT sample of the last <see cref="ClockSamples"/>,
    /// own RTT as an EWMA) and Worlds (decoded into pooled <see cref="Received"/> entries for the main thread). The
    /// main thread sends the local kart's States (<see cref="SendState"/>). Never touches the Unity API.
    /// </summary>
    internal sealed class OnlineRaceUdp : IDisposable
    {
        /// <summary>One other racer's state from a World datagram.</summary>
        public sealed class Received
        {
            public int slot;
            public int rttMs;
            /// <summary><see cref="LocalNowMs"/> when the datagram arrived.</summary>
            public double receivedMs;
            public readonly KartState state = new();
        }

        const int PingIntervalMs = 250;
        const int ClockSamples = 16;
        const int MaxSampleRttMs = 5000;
        const double RttSmoothing = 1.0 / 8.0;

        readonly string host;
        readonly int port;
        readonly ulong token;
        readonly byte[] key;
        readonly Stopwatch watch = Stopwatch.StartNew();
        readonly long epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        readonly ConcurrentQueue<Received> incoming = new();
        readonly ConcurrentQueue<Received> pool = new();
        readonly object clockLock = new();
        readonly Thread thread;
        readonly CancellationTokenSource stop = new();

        // Main thread: States.
        readonly WireWriter stateWriter = new(256);
        readonly HMACSHA256 stateMac;
        readonly byte[] stateHash = new byte[32];

        volatile Socket socket;
        volatile string failure;
        // Sequence counter (sendLock).
        uint seq;
        readonly object sendLock = new();

        // Clock (clockLock).
        readonly double[] sampleRtt = new double[ClockSamples];
        readonly double[] sampleOffset = new double[ClockSamples];
        int sampleCount, sampleNext;
        double offsetMs, rttMs;
        bool synced;
        long echoServerMs;
        double echoReceivedMs;

        long bytesUp, bytesDown, datagramsUp, datagramsDown, worlds, dropped;
        double lastWorldMs = -1;

        public OnlineRaceUdp(string host, int port, ulong token, byte[] key)
        {
            this.host = host;
            this.port = port;
            this.token = token;
            this.key = key ?? Array.Empty<byte>();
            stateMac = new HMACSHA256(this.key);
            thread = new Thread(Run) { IsBackground = true, Name = "MHK race UDP" };
            thread.Start();
        }

        /// <summary>Local monotonic clock as Unix milliseconds (wall clock at start + elapsed).</summary>
        public double LocalNowMs => epochMs + watch.Elapsed.TotalMilliseconds;

        /// <summary>Estimated server clock (Unix ms); the local clock until the first Pong.</summary>
        public double ServerNowMs
        {
            get
            {
                lock (clockLock) return LocalNowMs + offsetMs;
            }
        }

        /// <summary>At least one Pong arrived: <see cref="ServerNowMs"/> follows the server.</summary>
        public bool Synced
        {
            get
            {
                lock (clockLock) return synced;
            }
        }

        /// <summary>Own round-trip time to the server (ms, smoothed), 0 while unknown.</summary>
        public int RttMs
        {
            get
            {
                lock (clockLock) return (int)Math.Round(rttMs);
            }
        }

        /// <summary><see cref="LocalNowMs"/> of the newest World, -1 before the first.</summary>
        public double LastWorldMs => Interlocked.CompareExchange(ref lastWorldMs, 0, 0);

        /// <summary>Why the socket could not be opened (for the log), null while fine.</summary>
        public string Failure => failure;

        public long BytesUp => Interlocked.Read(ref bytesUp);
        public long BytesDown => Interlocked.Read(ref bytesDown);
        public long DatagramsUp => Interlocked.Read(ref datagramsUp);
        public long DatagramsDown => Interlocked.Read(ref datagramsDown);
        public long Worlds => Interlocked.Read(ref worlds);
        /// <summary>Datagrams dropped: bad MAC, other token, stale sequence, malformed.</summary>
        public long Dropped => Interlocked.Read(ref dropped);

        /// <summary>Next received state for the main thread; give it back with <see cref="Recycle"/>.</summary>
        public bool TryReceive(out Received r) => incoming.TryDequeue(out r);

        public void Recycle(Received r)
        {
            if (r != null) pool.Enqueue(r);
        }

        /// <summary>Sends a State datagram: <paramref name="state"/> and up to 30 piggybacked projectile reports (main thread).</summary>
        public void SendState(KartState state, List<ProjectileReport> reports, int reportStart, int reportCount)
        {
            var s = socket;
            if (s == null) return;
            var w = stateWriter;
            w.Clear();
            Header(w, RaceProtocol.UdpState);
            state.Write(w);
            w.U8((byte)reportCount);
            for (var i = 0; i < reportCount; i++) reports[reportStart + i].Write(w);
            Send(s, w, stateMac, stateHash);
        }

        /// <summary>Most reports one State can carry next to a full state (datagram limit).</summary>
        public static int ReportCapacity(KartState state) =>
            (RaceProtocol.UdpMaxDatagram - RaceProtocol.UdpHeaderSize - RaceProtocol.UdpMacSize - 1 - 48 - 2 * state.effects.Count) / ProjectileReport.Size;

        // The sequence number is written last, under sendLock together with the MAC and the send, so datagrams leave in
        // the order of their numbers whichever thread sends them (the server drops anything not newer than the newest).
        void Header(WireWriter w, byte type)
        {
            w.U8(type);
            w.U64(token);
            w.U32(0);
        }

        void Send(Socket s, WireWriter w, HMACSHA256 mac, byte[] hash)
        {
            lock (sendLock)
            {
                w.PatchU32(9, unchecked((uint)++seq));
                var length = w.Length;
                if (!mac.TryComputeHash(new ReadOnlySpan<byte>(w.RawBuffer, 0, length), hash, out _)) return;
                w.Raw(hash, 0, RaceProtocol.UdpMacSize);
                SendLocked(s, w);
            }
        }

        void SendLocked(Socket s, WireWriter w)
        {
            try
            {
                s.Send(w.RawBuffer, 0, w.Length, SocketFlags.None);
                Interlocked.Add(ref bytesUp, w.Length);
                Interlocked.Increment(ref datagramsUp);
            }
            catch (SocketException)
            {
                // Unreachable for a moment (network change): the next datagram tries again.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void Run()
        {
            Socket s;
            try
            {
                var address = Resolve(host);
                s = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(new IPEndPoint(address, port));
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                failure = e.Message;
                return;
            }
            socket = s;
            if (stop.IsCancellationRequested)
            {
                socket = null;
                s.Dispose();
                return;
            }
            using var mac = new HMACSHA256(key);
            var hash = new byte[32];
            var ping = new WireWriter(64);
            var buffer = new byte[2048];
            var nextPing = 0.0;
            while (!stop.IsCancellationRequested)
            {
                var now = LocalNowMs;
                if (now >= nextPing)
                {
                    SendPing(s, ping, mac, hash, now);
                    nextPing = now + PingIntervalMs;
                }
                try
                {
                    var waitUs = (int)Math.Max(1000, (nextPing - LocalNowMs) * 1000);
                    if (!s.Poll(waitUs, SelectMode.SelectRead)) continue;
                    var n = s.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                    Interlocked.Add(ref bytesDown, n);
                    Interlocked.Increment(ref datagramsDown);
                    Handle(buffer, n, mac, hash);
                }
                catch (SocketException)
                {
                    // ICMP port unreachable (server not listening yet) and the like: keep pinging.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        static IPAddress Resolve(string host)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentException("No server address.");
            if (IPAddress.TryParse(host, out var literal)) return literal;
            var addresses = Dns.GetHostAddresses(host);
            foreach (var a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            if (addresses.Length > 0) return addresses[0];
            throw new ArgumentException($"Cannot resolve {host}.");
        }

        void SendPing(Socket s, WireWriter w, HMACSHA256 mac, byte[] hash, double now)
        {
            long echo;
            double hold;
            lock (clockLock)
            {
                echo = echoServerMs;
                hold = echo != 0 ? now - echoReceivedMs : 0;
            }
            w.Clear();
            Header(w, RaceProtocol.UdpPing);
            w.I64((long)Math.Round(now));
            w.I64(echo);
            w.U16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, Math.Round(hold))));
            Send(s, w, mac, hash);
        }

        void Handle(byte[] data, int length, HMACSHA256 mac, byte[] hash)
        {
            if (length < RaceProtocol.UdpHeaderSize + RaceProtocol.UdpMacSize || length > RaceProtocol.UdpMaxDatagram)
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            var type = data[0];
            var datagramToken = BitConverter.ToUInt64(data, 1);
            var datagramSeq = BitConverter.ToUInt32(data, 9);
            var macAt = length - RaceProtocol.UdpMacSize;
            if (datagramToken != token || !mac.TryComputeHash(new ReadOnlySpan<byte>(data, 0, macAt), hash, out _) || !MacEquals(hash, data, macAt))
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            var r = new WireReader(data, RaceProtocol.UdpHeaderSize, macAt - RaceProtocol.UdpHeaderSize);
            try
            {
                if (type == RaceProtocol.UdpPong) Pong(r);
                else if (type == RaceProtocol.UdpWorld) World(r, datagramSeq);
            }
            catch (WireFormatException)
            {
                Interlocked.Increment(ref dropped);
            }
        }

        static bool MacEquals(byte[] hash, byte[] data, int at)
        {
            var diff = 0;
            for (var i = 0; i < RaceProtocol.UdpMacSize; i++) diff |= hash[i] ^ data[at + i];
            return diff == 0;
        }

        void Pong(WireReader r)
        {
            var clientMs = r.I64();
            var serverMs = r.I64();
            var now = LocalNowMs;
            var rtt = now - clientMs;
            lock (clockLock)
            {
                echoServerMs = serverMs;
                echoReceivedMs = now;
                if (rtt < 0 || rtt > MaxSampleRttMs) return;
                sampleRtt[sampleNext] = rtt;
                sampleOffset[sampleNext] = serverMs + rtt * 0.5 - now;
                sampleNext = (sampleNext + 1) % ClockSamples;
                if (sampleCount < ClockSamples) sampleCount++;
                var best = 0;
                for (var i = 1; i < sampleCount; i++)
                    if (sampleRtt[i] < sampleRtt[best]) best = i;
                offsetMs = sampleOffset[best];
                rttMs = synced ? rttMs + (rtt - rttMs) * RttSmoothing : rtt;
                synced = true;
            }
        }

        uint newestWorldSeq;
        bool worldSeen;

        void World(WireReader r, uint datagramSeq)
        {
            if (worldSeen && unchecked((int)(datagramSeq - newestWorldSeq)) <= 0)
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            worldSeen = true;
            newestWorldSeq = datagramSeq;
            r.I64(); // serverTime: the race clock comes from the ticks
            var n = r.U8();
            var now = LocalNowMs;
            for (var i = 0; i < n; i++)
            {
                if (!pool.TryDequeue(out var e)) e = new Received();
                try
                {
                    e.slot = r.U8();
                    e.rttMs = r.U16();
                    e.state.Read(r);
                }
                catch (WireFormatException)
                {
                    pool.Enqueue(e);
                    throw;
                }
                e.receivedMs = now;
                incoming.Enqueue(e);
            }
            Interlocked.Increment(ref worlds);
            Interlocked.Exchange(ref lastWorldMs, now);
        }

        public void Dispose()
        {
            if (stop.IsCancellationRequested) return;
            stop.Cancel();
            var s = socket;
            socket = null;
            try
            {
                s?.Dispose();
            }
            catch (SocketException)
            {
            }
            if (thread.IsAlive && !thread.Join(500)) failure ??= "UDP thread did not stop.";
            stateMac.Dispose();
            stop.Dispose();
        }
    }
}
