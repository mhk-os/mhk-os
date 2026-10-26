using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Single-instance hand-over of join links on desktop: the running game listens on a loopback port, a second
    /// instance started with a link sends it there (one line <c>MHKOS-LINK url</c>, answered <c>OK</c>) and quits.
    /// Only well-formed join links are taken; nothing else is read from the socket.
    /// </summary>
    public static class JoinLinkForwarder
    {
        const string Command = "MHKOS-LINK ";
        const string Answer = "OK";
        const int MaxLine = 512;
        const int ConnectTimeoutMs = 400;
        const int IoTimeoutMs = 1500;

        /// <summary>Sends <paramref name="url"/> to a game listening on <paramref name="port"/>: true when it took the link.</summary>
        public static bool TryForward(string url, int port)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(ConnectTimeoutMs) || !client.Connected) return false;
                client.EndConnect(connect);
                client.ReceiveTimeout = client.SendTimeout = IoTimeoutMs;
                var stream = client.GetStream();
                var line = Encoding.UTF8.GetBytes(Command + url + "\n");
                stream.Write(line, 0, line.Length);
                return ReadLine(stream) == Answer;
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Takes links of later instances on <paramref name="port"/> (loopback only) and passes each to
        /// <paramref name="onLink"/> on a background thread. Null when the port is taken (another game listens).
        /// </summary>
        public static Listener Listen(int port, Action<string> onLink)
        {
            try
            {
                var socket = new TcpListener(IPAddress.Loopback, port);
                socket.Start();
                return new Listener(socket, onLink);
            }
            catch (SocketException e)
            {
                Debug.Log($"[Online] Room links from other game instances are not taken (port {port}: {e.SocketErrorCode}).");
                return null;
            }
        }

        static string ReadLine(Stream stream)
        {
            var bytes = new byte[MaxLine];
            var count = 0;
            while (count < MaxLine)
            {
                var b = stream.ReadByte();
                if (b < 0 || b == '\n') break;
                bytes[count++] = (byte)b;
            }
            return count == MaxLine ? null : Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\r');
        }

        /// <summary>A running listener; dispose to stop it.</summary>
        public sealed class Listener : IDisposable
        {
            readonly TcpListener socket;
            readonly Action<string> onLink;
            readonly Thread thread;
            volatile bool stopped;

            internal Listener(TcpListener socket, Action<string> onLink)
            {
                this.socket = socket;
                this.onLink = onLink;
                thread = new Thread(Run) { IsBackground = true, Name = "MHK join links" };
                thread.Start();
            }

            void Run()
            {
                while (!stopped)
                {
                    try
                    {
                        using var client = socket.AcceptTcpClient();
                        client.ReceiveTimeout = client.SendTimeout = IoTimeoutMs;
                        var stream = client.GetStream();
                        var line = ReadLine(stream);
                        if (line == null || !line.StartsWith(Command, StringComparison.Ordinal)) continue;
                        var url = line.Substring(Command.Length);
                        if (!JoinLinks.TryParse(url, out _)) continue;
                        onLink(url);
                        var ok = Encoding.UTF8.GetBytes(Answer + "\n");
                        stream.Write(ok, 0, ok.Length);
                    }
                    catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        if (stopped) return;
                    }
                }
            }

            public void Dispose()
            {
                stopped = true;
                socket.Stop();
            }
        }
    }
}
