using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace MHK.Online
{
    /// <summary>
    /// One control connection to the game server (docs/protocol.md, frames): TCP, TLS when the endpoint says so. A reader
    /// thread takes frames in, a writer thread sends the queued ones in order and pings every
    /// <see cref="Protocol.PingInterval"/> of silence. Responses complete their request's task; pushes and the close are
    /// queued for the main thread (<see cref="TryDequeue"/>). Never touches the Unity API.
    /// </summary>
    public sealed class OnlineConnection : IDisposable
    {
        /// <summary>Answer to a request: <see cref="status"/>, then the payload (<see cref="body"/>, Ok) or <see cref="message"/>.</summary>
        public readonly struct Response
        {
            public readonly Status status;
            public readonly string message;
            /// <summary>Payload of an Ok response (empty reader otherwise).</summary>
            public readonly WireReader body;

            public Response(Status status, string message, WireReader body)
            {
                this.status = status;
                this.message = message ?? "";
                this.body = body;
            }

            public bool Ok => status == Status.Ok;
        }

        /// <summary>Something for the main thread: a push (<see cref="type"/>, <see cref="body"/>) or the end of the connection.</summary>
        public readonly struct Incoming
        {
            public readonly ushort type;
            public readonly WireReader body;
            /// <summary>The connection ended; <see cref="reason"/> says why (for the player). Always the last item.</summary>
            public readonly bool closed;
            public readonly string reason;

            Incoming(ushort type, WireReader body, bool closed, string reason)
            {
                this.type = type;
                this.body = body;
                this.closed = closed;
                this.reason = reason;
            }

            public static Incoming Push(ushort type, WireReader body) => new(type, body, false, null);
            public static Incoming Closed(string reason) => new(0, null, true, reason);
        }

        sealed class Pending
        {
            public TaskCompletionSource<Response> task;
            public CancellationTokenSource timeout;
        }

        static readonly WireReader Empty = new(Array.Empty<byte>());

        readonly Socket socket;
        readonly Stream stream;
        readonly ConcurrentDictionary<uint, Pending> pending = new();
        readonly ConcurrentQueue<Incoming> incoming = new();
        readonly BlockingCollection<byte[]> outgoing = new(new ConcurrentQueue<byte[]>());
        readonly Thread reader, writer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        int nextId;
        int ended;
        volatile bool disposed;
        volatile int maxFrame = Protocol.MaxFrameBeforeLogin;
        long lastSentMs, lastReceivedMs;
        string endReason = "Disconnected.";

        string EndReason => Volatile.Read(ref endReason);

        /// <summary>The endpoint text this connection was made to.</summary>
        public string Endpoint { get; }

        /// <summary>Whether the connection is encrypted.</summary>
        public bool Tls { get; }

        /// <summary>False once the connection ended (lost, closed by the server, or disposed).</summary>
        public bool IsOpen => Volatile.Read(ref ended) == 0;

        /// <summary>
        /// Raises the frame limit (both directions) from <see cref="Protocol.MaxFrameBeforeLogin"/> to
        /// <see cref="Protocol.MaxFrame"/> once an account is logged in, and back after logout.
        /// </summary>
        public bool LoggedIn
        {
            set => maxFrame = value ? Protocol.MaxFrame : Protocol.MaxFrameBeforeLogin;
        }

        OnlineConnection(string endpoint, bool tls, Socket socket, Stream stream)
        {
            Endpoint = endpoint;
            Tls = tls;
            this.socket = socket;
            this.stream = stream;
            lastSentMs = lastReceivedMs = clock.ElapsedMilliseconds;
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "MHK online reader" };
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "MHK online writer" };
            reader.Start();
            writer.Start();
        }

        /// <summary>
        /// Resolves <paramref name="endpoint"/>, connects (trying its addresses in order) and completes the TLS handshake
        /// within <paramref name="timeout"/>. Throws <see cref="OnlineException"/> with a message for the player.
        /// </summary>
        public static async Task<OnlineConnection> ConnectAsync(OnlineEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
        {
            var target = await endpoint.ResolveAsync().ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            Socket socket = null;
            Stream stream = null;
            try
            {
                socket = await OpenSocketAsync(target.addresses, endpoint, limit.Token).ConfigureAwait(false);
                stream = new NetworkStream(socket, true);
                if (target.tls)
                {
                    var tls = new SslStream(stream, false);
                    stream = tls;
                    using (limit.Token.Register(tls.Dispose))
                        await tls.AuthenticateAsClientAsync(endpoint.Host, null, SslProtocols.Tls12, false).ConfigureAwait(false);
                    limit.Token.ThrowIfCancellationRequested();
                }
                return new OnlineConnection(endpoint.Text, target.tls, socket, stream);
            }
            catch (Exception e)
            {
                stream?.Dispose();
                socket?.Dispose();
                ct.ThrowIfCancellationRequested();
                if (e is OnlineException) throw;
                if (limit.IsCancellationRequested) throw new OnlineException($"The server at {endpoint.Text} did not answer in time.");
                if (e is AuthenticationException)
                    throw new OnlineException($"Secure connection to {endpoint.Host} failed: its certificate is not trusted.");
                if (e is IOException && target.tls)
                    throw new OnlineException($"Secure connection to {endpoint.Host} failed. Is it a TLS server?");
                throw new OnlineException($"Could not connect to {endpoint.Text}: {e.Message}");
            }
        }

        static async Task<Socket> OpenSocketAsync(IPAddress[] addresses, OnlineEndpoint endpoint, CancellationToken ct)
        {
            SocketException last = null;
            foreach (var address in addresses)
            {
                ct.ThrowIfCancellationRequested();
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using (ct.Register(socket.Dispose))
                        await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return socket;
                }
                catch (SocketException e)
                {
                    socket.Dispose();
                    last = e;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            if (last != null && last.SocketErrorCode == SocketError.ConnectionRefused)
                throw new OnlineException($"No server is running at {endpoint.Text}.");
            throw new OnlineException($"The server at {endpoint.Text} could not be reached" + (last != null ? $" ({last.SocketErrorCode})." : "."));
        }

        /// <summary>
        /// Sends a request of <paramref name="type"/> whose body <paramref name="body"/> writes (null: empty) and waits for
        /// its response, at most <paramref name="timeout"/>. A lost connection or no answer in time throws
        /// <see cref="OnlineException"/>. Any thread; completions continue on the caller's context.
        /// </summary>
        public Task<Response> RequestAsync(ushort type, Action<WireWriter> body, TimeSpan timeout)
        {
            var id = NextId();
            byte[] frame;
            try
            {
                frame = BuildFrame(FrameKind.Request, type, id, body);
            }
            catch (Exception e)
            {
                return Task.FromException<Response>(e);
            }
            var p = new Pending { task = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously) };
            pending[id] = p;
            p.timeout = new CancellationTokenSource(timeout);
            p.timeout.Token.Register(() =>
            {
                if (pending.TryRemove(id, out var timedOut)) Finish(timedOut, null, new OnlineException("The server did not answer in time."));
            });
            if (!Enqueue(frame) && pending.TryRemove(id, out _)) Finish(p, null, new OnlineException(EndReason));
            return p.task.Task;
        }

        /// <summary>Sends a notify (never answered). False when the connection already ended.</summary>
        public bool Notify(ushort type, Action<WireWriter> body) => Enqueue(BuildFrame(FrameKind.Notify, type, 0, body));

        /// <summary>Next push or the close notice, for the main thread to handle.</summary>
        public bool TryDequeue(out Incoming item) => incoming.TryDequeue(out item);

        /// <summary>Closes the connection now (no close notice is queued). Pending requests fail.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            End("Disconnected.");
            // Both threads leave right away once the socket is closed and the queue completed.
            if (Thread.CurrentThread != reader) reader.Join(1000);
            if (Thread.CurrentThread != writer) writer.Join(1000);
            outgoing.Dispose();
        }

        uint NextId()
        {
            uint id;
            do id = unchecked((uint)Interlocked.Increment(ref nextId));
            while (id == 0);
            return id;
        }

        byte[] BuildFrame(FrameKind kind, ushort type, uint id, Action<WireWriter> body)
        {
            var w = new WireWriter();
            w.U32(0);
            w.U8((byte)kind);
            w.U16(type);
            w.U32(id);
            body?.Invoke(w);
            var size = w.Length - 4;
            if (size > maxFrame) throw new ArgumentException($"Frame of {size} bytes over the limit of {maxFrame}.");
            w.PatchU32(0, (uint)size);
            return w.ToArray();
        }

        bool Enqueue(byte[] frame)
        {
            if (!IsOpen) return false;
            try
            {
                outgoing.Add(frame);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Completed by End, or disposed (ObjectDisposedException derives from it).
                return false;
            }
        }

        // First caller wins: closes the transport, stops the writer, fails what is pending and (unless disposed by the
        // owner) tells the main thread why.
        void End(string reason)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            Volatile.Write(ref endReason, reason);
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                // Closing a broken TLS stream may throw; the socket below is what matters.
            }
            socket.Dispose();
            outgoing.CompleteAdding();
            foreach (var id in pending.Keys)
                if (pending.TryRemove(id, out var p)) Finish(p, null, new OnlineException(reason));
            if (!disposed) incoming.Enqueue(Incoming.Closed(reason));
        }

        static void Finish(Pending p, Response? response, Exception error)
        {
            p.timeout?.Dispose();
            if (p.task == null) return;
            if (error != null) p.task.TrySetException(error);
            else p.task.TrySetResult(response.GetValueOrDefault());
        }

        void ReadLoop()
        {
            var prefix = new byte[4];
            try
            {
                while (IsOpen)
                {
                    if (!ReadExactly(prefix, 4))
                    {
                        End("The server closed the connection.");
                        return;
                    }
                    var size = (int)Math.Min(int.MaxValue, prefix[0] | (uint)prefix[1] << 8 | (uint)prefix[2] << 16 | (uint)prefix[3] << 24);
                    if (size < Protocol.HeaderSize || size > maxFrame)
                    {
                        End($"The server sent an invalid frame ({size} bytes).");
                        return;
                    }
                    var frame = new byte[size];
                    if (!ReadExactly(frame, size))
                    {
                        End("The server closed the connection.");
                        return;
                    }
                    Interlocked.Exchange(ref lastReceivedMs, clock.ElapsedMilliseconds);
                    if (!Dispatch(frame)) return;
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
            {
                End("The connection to the server was lost.");
            }
        }

        bool ReadExactly(byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        bool Dispatch(byte[] frame)
        {
            var header = new WireReader(frame, 0, Protocol.HeaderSize);
            var kind = (FrameKind)header.U8();
            var type = header.U16();
            var id = header.U32();
            var body = new WireReader(frame, Protocol.HeaderSize, frame.Length - Protocol.HeaderSize);
            switch (kind)
            {
                case FrameKind.Response:
                    // A late answer to a request that timed out finds nothing here and is dropped.
                    if (pending.TryRemove(id, out var p)) Complete(p, body);
                    return true;
                case FrameKind.Push:
                    incoming.Enqueue(Incoming.Push(type, body));
                    return true;
                default:
                    End($"The server sent an unexpected frame (kind {(byte)kind}).");
                    return false;
            }
        }

        static void Complete(Pending p, WireReader body)
        {
            try
            {
                var status = (Status)body.U16();
                Finish(p, status == Status.Ok ? new Response(status, "", body) : new Response(status, body.String(), Empty), null);
            }
            catch (WireFormatException)
            {
                Finish(p, null, new OnlineException("The server sent a malformed answer."));
            }
        }

        void WriteLoop()
        {
            var pingMs = (long)Protocol.PingInterval.TotalMilliseconds;
            var idleMs = (long)Protocol.IdleTimeout.TotalMilliseconds;
            try
            {
                while (true)
                {
                    var now = clock.ElapsedMilliseconds;
                    var wait = Math.Max(0, Interlocked.Read(ref lastSentMs) + pingMs - now);
                    if (outgoing.TryTake(out var frame, (int)Math.Min(wait, int.MaxValue)))
                    {
                        stream.Write(frame, 0, frame.Length);
                        stream.Flush();
                        Interlocked.Exchange(ref lastSentMs, clock.ElapsedMilliseconds);
                        continue;
                    }
                    if (outgoing.IsAddingCompleted) return;
                    if (clock.ElapsedMilliseconds - Interlocked.Read(ref lastReceivedMs) > idleMs)
                    {
                        End("The server stopped answering.");
                        return;
                    }
                    SendPing();
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                End("The connection to the server was lost.");
            }
        }

        // Keeps the connection alive; the answer only refreshes the receive time (no one waits for it).
        void SendPing()
        {
            var id = NextId();
            pending[id] = new Pending();
            var ping = new PingRequest { clientTime = DateTimeOffset.UtcNow };
            if (!Enqueue(BuildFrame(FrameKind.Request, MessageType.Ping, id, ping.Write))) pending.TryRemove(id, out _);
        }
    }
}
