using System;

namespace MHK.Online
{
    /// <summary>Request 1: the first request of a connection, naming the game build and its protocol.</summary>
    public sealed class HelloRequest
    {
        public const int MaxCommit = 40, MaxPlatform = 32;

        public ushort protocol = Protocol.Version;
        public string gameCommit = "";
        public int gameBuild;
        public int contentVersion;
        public int modApi;
        public string platform = "";

        public void Write(WireWriter w)
        {
            w.U32(Protocol.Magic);
            w.U16(protocol);
            w.String(Clip(gameCommit, MaxCommit), MaxCommit);
            w.I32(gameBuild);
            w.I32(contentVersion);
            w.I32(modApi);
            w.String(Clip(platform, MaxPlatform), MaxPlatform);
        }

        public static HelloRequest Read(WireReader r)
        {
            if (r.U32() != Protocol.Magic) throw new WireFormatException("Bad magic.");
            return new HelloRequest
            {
                protocol = r.U16(), gameCommit = r.String(MaxCommit), gameBuild = r.I32(), contentVersion = r.I32(),
                modApi = r.I32(), platform = r.String(MaxPlatform),
            };
        }

        // Build labels are ASCII; a longer one is cut rather than failing the connection.
        static string Clip(string s, int max) => s == null ? "" : s.Length > max ? s.Substring(0, max) : s;
    }

    /// <summary>Response to Hello.</summary>
    public sealed class HelloOk
    {
        public const int MaxServerVersion = 64;

        public ushort protocol;
        public string serverVersion = "";
        /// <summary>Realtime UDP port, 0 = none.</summary>
        public ushort udpPort;
        public DateTimeOffset serverTime;

        public void Write(WireWriter w)
        {
            w.U16(protocol);
            w.String(serverVersion, MaxServerVersion);
            w.U16(udpPort);
            w.Time(serverTime);
        }

        public static HelloOk Read(WireReader r) => new()
        {
            protocol = r.U16(), serverVersion = r.String(MaxServerVersion), udpPort = r.U16(), serverTime = r.Time(),
        };
    }

    /// <summary>Request 2.</summary>
    public sealed class PingRequest
    {
        public DateTimeOffset clientTime;

        public void Write(WireWriter w) => w.Time(clientTime);

        public static PingRequest Read(WireReader r) => new() { clientTime = r.Time() };
    }

    /// <summary>Response to Ping: the client's time echoed and the server's.</summary>
    public sealed class PingResponse
    {
        public DateTimeOffset clientTime, serverTime;

        public void Write(WireWriter w)
        {
            w.Time(clientTime);
            w.Time(serverTime);
        }

        public static PingResponse Read(WireReader r) => new() { clientTime = r.Time(), serverTime = r.Time() };
    }

    /// <summary>Requests 16 (Register) and 17 (Login).</summary>
    public sealed class CredentialsRequest
    {
        public const int MaxUsername = 64, MaxPassword = 512;

        public string username = "";
        public string password = "";

        public void Write(WireWriter w)
        {
            w.String(username, MaxUsername);
            w.String(password, MaxPassword);
        }

        public static CredentialsRequest Read(WireReader r) => new() { username = r.String(MaxUsername), password = r.String(MaxPassword) };
    }

    /// <summary>Request 18.</summary>
    public sealed class ResumeRequest
    {
        public string token = "";

        public void Write(WireWriter w) => w.String(token, Session.MaxToken);

        public static ResumeRequest Read(WireReader r) => new() { token = r.String(Session.MaxToken) };
    }

    /// <summary>Request 20. Empty returns to the country detected from the registration IP.</summary>
    public sealed class SetCountryRequest
    {
        public string country = "";

        public void Write(WireWriter w) => w.String(country, Account.MaxCountry);

        public static SetCountryRequest Read(WireReader r) => new() { country = r.String(Account.MaxCountry) };
    }

    /// <summary>Request 26: the account's password, and whether its time-trial runs are hidden too (they are anonymized either way).</summary>
    public sealed class DeleteAccountRequest
    {
        public string password = "";
        public bool deleteRuns;

        public void Write(WireWriter w)
        {
            w.String(password, CredentialsRequest.MaxPassword);
            w.Bool(deleteRuns);
        }

        public static DeleteAccountRequest Read(WireReader r) => new() { password = r.String(CredentialsRequest.MaxPassword), deleteRuns = r.Bool() };
    }

    /// <summary>Request 29: the account's new username (same rules as Register; a case-only change of its own name is allowed).</summary>
    public sealed class ChangeUsernameRequest
    {
        public string username = "";

        public void Write(WireWriter w) => w.String(username, CredentialsRequest.MaxUsername);

        public static ChangeUsernameRequest Read(WireReader r) => new() { username = r.String(CredentialsRequest.MaxUsername) };
    }

    /// <summary>An account as the server describes it.</summary>
    public sealed class Account
    {
        public const int MaxUsername = 20, MaxCountry = 2;

        public ulong id;
        /// <summary>The case it registered with.</summary>
        public string username = "";
        /// <summary>Effective ISO 3166-1 alpha-2 country, empty = unknown.</summary>
        public string country = "";
        /// <summary>True while <see cref="country"/> is the one detected from the registration IP.</summary>
        public bool countryFromIp;
        public AccountRole role;
        public DateTimeOffset created;

        public bool IsModerator => role == AccountRole.Moderator;

        public void Write(WireWriter w)
        {
            w.U64(id);
            w.String(username, MaxUsername);
            w.String(country, MaxCountry);
            w.Bool(countryFromIp);
            w.U8((byte)role);
            w.Time(created);
        }

        public static Account Read(WireReader r) => new()
        {
            id = r.U64(), username = r.String(MaxUsername), country = r.String(MaxCountry), countryFromIp = r.Bool(),
            role = (AccountRole)r.U8(), created = r.Time(),
        };
    }

    /// <summary>Answer of Register, Login and Resume.</summary>
    public sealed class Session
    {
        public const int MaxToken = 128;

        /// <summary>Opaque; stored by the client and sent with Resume.</summary>
        public string token = "";
        /// <summary>Sliding: every Resume extends it.</summary>
        public DateTimeOffset expires;
        public Account account = new();

        public void Write(WireWriter w)
        {
            w.String(token, MaxToken);
            w.Time(expires);
            account.Write(w);
        }

        public static Session Read(WireReader r) => new() { token = r.String(MaxToken), expires = r.Time(), account = Account.Read(r) };
    }

    /// <summary>Push 1: the server closes the connection (account disabled, session revoked, shutting down).</summary>
    public sealed class DisconnectPush
    {
        public Status status;
        public string message = "";

        public void Write(WireWriter w)
        {
            w.U16((ushort)status);
            w.String(message);
        }

        public static DisconnectPush Read(WireReader r) => new() { status = (Status)r.U16(), message = r.String() };
    }
}
