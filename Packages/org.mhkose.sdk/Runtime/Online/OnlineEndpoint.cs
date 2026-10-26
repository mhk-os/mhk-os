using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MHK.Core;

namespace MHK.Online
{
    /// <summary>
    /// Address of the game server: <c>tls://host:port</c>, <c>tcp://host:port</c> (plaintext, local network only) or a
    /// bare <c>host[:port]</c> (plaintext when the host qualifies, else TLS). The port defaults to
    /// <see cref="Protocol.DefaultPort"/>. Passwords never cross the internet unencrypted: plaintext is refused unless
    /// the host is localhost, a loopback, private or link-local address (names are resolved to check).
    /// </summary>
    public sealed class OnlineEndpoint : IEquatable<OnlineEndpoint>
    {
        public enum Scheme { Tls, Tcp, Auto }

        /// <summary>Release builds.</summary>
        public const string ReleaseServer = "tls://mhk-os.svit.ac:7350";
        /// <summary>Editor and unstamped builds (<see cref="BuildInfo.Number"/> 0).</summary>
        public const string DevServer = "tcp://localhost:7350";
        /// <summary>Command-line option naming the server; wins over <see cref="UserSettings.onlineServer"/>.</summary>
        public const string CommandLineOption = "-server";

        public Scheme Kind { get; }
        /// <summary>Host name or IP literal (IPv6 without brackets).</summary>
        public string Host { get; }
        public int Port { get; }

        /// <summary>Canonical text (<c>tls://host:port</c>, <c>tcp://host:port</c> or <c>host:port</c>); stored sessions are keyed by it.</summary>
        public string Text { get; }

        OnlineEndpoint(Scheme kind, string host, int port)
        {
            Kind = kind;
            Host = host;
            Port = port;
            var authority = (host.IndexOf(':') >= 0 ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);
            Text = kind switch { Scheme.Tls => "tls://" + authority, Scheme.Tcp => "tcp://" + authority, _ => authority };
        }

        public override string ToString() => Text;
        public bool Equals(OnlineEndpoint other) => other != null && other.Text == Text;
        public override bool Equals(object obj) => Equals(obj as OnlineEndpoint);
        public override int GetHashCode() => Text.GetHashCode();

        /// <summary>The built-in server of this build.</summary>
        public static string DefaultText(int buildNumber) => buildNumber == 0 ? DevServer : ReleaseServer;

        /// <summary>
        /// The server text in effect: the <c>-server</c> command-line option, else <paramref name="setting"/>
        /// (<see cref="UserSettings.onlineServer"/>), else <see cref="DefaultText"/>.
        /// </summary>
        public static string ConfiguredText(string setting, int buildNumber)
        {
            var option = CommandLine.Value(CommandLineOption);
            if (!string.IsNullOrWhiteSpace(option)) return option.Trim();
            if (!string.IsNullOrWhiteSpace(setting)) return setting.Trim();
            return DefaultText(buildNumber);
        }

        /// <summary>Whether the server is set on the command line (the setting then has no effect).</summary>
        public static bool FromCommandLine => !string.IsNullOrWhiteSpace(CommandLine.Value(CommandLineOption));

        /// <summary>Parses <paramref name="text"/>; on failure <paramref name="error"/> says why (for the player).</summary>
        public static bool TryParse(string text, out OnlineEndpoint endpoint, out string error)
        {
            endpoint = null;
            error = null;
            var s = (text ?? "").Trim();
            var kind = Scheme.Auto;
            if (s.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)) kind = Scheme.Tls;
            else if (s.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) kind = Scheme.Tcp;
            else if (s.Contains("://"))
            {
                error = "The server address must start with tls:// or tcp://, or be a plain host name.";
                return false;
            }
            if (kind != Scheme.Auto) s = s.Substring(6);
            if (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            if (!SplitAuthority(s, out var host, out var portText))
            {
                error = $"\"{text}\" is not a server address (host or host:port).";
                return false;
            }
            var port = Protocol.DefaultPort;
            if (portText != null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535))
            {
                error = $"\"{portText}\" is not a port number (1 to 65535).";
                return false;
            }
            if (!ValidHost(host))
            {
                error = $"\"{host}\" is not a host name or IP address.";
                return false;
            }
            endpoint = new OnlineEndpoint(kind, host.ToLowerInvariant(), port);
            return true;
        }

        // host, host:port, [v6], [v6]:port, or a bare IPv6 literal (several colons, no port).
        static bool SplitAuthority(string s, out string host, out string port)
        {
            host = port = null;
            if (s.Length == 0 || s.IndexOfAny(new[] { '/', '?', '#', '@', ' ' }) >= 0) return false;
            if (s[0] == '[')
            {
                var close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                var rest = s.Substring(close + 1);
                if (rest.Length == 0) return host.Length > 0;
                if (rest[0] != ':' || rest.Length == 1) return false;
                port = rest.Substring(1);
                return host.Length > 0;
            }
            var colon = s.IndexOf(':');
            if (colon < 0)
            {
                host = s;
                return true;
            }
            if (s.IndexOf(':', colon + 1) >= 0)
            {
                host = s;
                return true;
            }
            host = s.Substring(0, colon);
            port = s.Substring(colon + 1);
            return host.Length > 0 && port.Length > 0;
        }

        static bool ValidHost(string host)
        {
            if (host.Contains(":")) return IPAddress.TryParse(host, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
            // Digits and dots only: a full dotted IPv4 address (IPAddress also takes "10" or "127.1").
            if (host.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.').Length == 0)
                return host.Split('.').Length == 4 && IPAddress.TryParse(host, out _);
            if (host.Length > 253) return false;
            foreach (var label in host.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                foreach (var c in label)
                    if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-')) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether plaintext may be used with <paramref name="address"/>: loopback, 10/8, 172.16/12, 192.168/16,
        /// 169.254/16, fc00::/7, fe80::/10 (IPv4-mapped IPv6 by its IPv4 address).
        /// </summary>
        public static bool IsLocal(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv4MappedToIPv6) return IsLocal(address.MapToIPv4());
                var b6 = address.GetAddressBytes();
                return (b6[0] & 0xFE) == 0xFC || b6[0] == 0xFE && (b6[1] & 0xC0) == 0x80;
            }
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }

        /// <summary>The addresses to connect to and whether the connection is TLS (a bare host: plaintext only when local).</summary>
        public readonly struct Resolved
        {
            public readonly IPAddress[] addresses;
            public readonly bool tls;

            public Resolved(IPAddress[] addresses, bool tls)
            {
                this.addresses = addresses;
                this.tls = tls;
            }
        }

        /// <summary>
        /// Resolves the host (off the main thread) and applies the plaintext rule: every address of a plaintext host must
        /// be local. Throws <see cref="OnlineException"/> with a message for the player.
        /// </summary>
        public async Task<Resolved> ResolveAsync()
        {
            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(Host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(Host).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                throw new OnlineException($"The server {Host} was not found. Check the internet connection.");
            }
            if (addresses == null || addresses.Length == 0) throw new OnlineException($"The server {Host} was not found.");
            var local = Host == "localhost" || Array.TrueForAll(addresses, IsLocal);
            if (Kind == Scheme.Tcp && !local)
                throw new OnlineException("Unencrypted connections (tcp://) are only allowed to this computer or the local network. " +
                                          $"Use tls://{Text.Substring(6)}.", true);
            return new Resolved(addresses, Kind == Scheme.Tls || Kind == Scheme.Auto && !local);
        }
    }

    /// <summary>A connection or request failed without an answer from the server; the message is for the player.</summary>
    public sealed class OnlineException : Exception
    {
        /// <summary>Trying again cannot help (the address is refused by policy): no automatic reconnect.</summary>
        public bool Permanent { get; }

        public OnlineException(string message, bool permanent = false) : base(message) => Permanent = permanent;
    }
}
