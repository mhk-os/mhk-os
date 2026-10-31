using System;

namespace MHK.Online
{
    /// <summary>Constants of the online protocol (docs/protocol.md in the server repository, the contract of both sides).</summary>
    public static class Protocol
    {
        /// <summary>Protocol revision this client speaks (Hello). Bumped for incompatible changes only.</summary>
        public const ushort Version = 2;
        /// <summary>First field of Hello: the bytes "MHKO" read as a little-endian u32.</summary>
        public const uint Magic = 0x4F4B484D;
        public const int DefaultPort = 7350;

        /// <summary>Frame header after the length prefix: kind u8, type u16, id u32.</summary>
        public const int HeaderSize = 7;
        /// <summary>Largest frame (after the length prefix) before login; the server closes on a longer one.</summary>
        public const int MaxFrameBeforeLogin = 64 * 1024;
        /// <summary>Largest frame after login; a longer one closes the connection.</summary>
        public const int MaxFrame = 16 * 1024 * 1024;

        /// <summary>Clients ping this often; the server closes a connection silent for <see cref="IdleTimeout"/>.</summary>
        public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    }

    /// <summary>First header byte of a frame.</summary>
    public enum FrameKind : byte
    {
        /// <summary>Client to server, answered by a <see cref="Response"/> with the same id.</summary>
        Request = 0,
        /// <summary>Server to client: status, then the payload (Ok) or a message.</summary>
        Response = 1,
        /// <summary>Server to client, unasked (id 0).</summary>
        Push = 2,
        /// <summary>Client to server, never answered (id 0).</summary>
        Notify = 3,
    }

    /// <summary>
    /// Request types. Ranges: 1-15 connection, 16-63 accounts, 64-127 moderation, 128-255 time trials, leaderboards and
    /// ghosts, 256-511 lobbies, 512-767 race control over TCP. A response carries the type of its request.
    /// </summary>
    public static class MessageType
    {
        public const ushort ConnectionFirst = 1, ConnectionLast = 15;
        public const ushort AccountsFirst = 16, AccountsLast = 63;
        public const ushort ModerationFirst = 64, ModerationLast = 127;
        public const ushort TimeTrialsFirst = 128, TimeTrialsLast = 255;
        public const ushort LobbiesFirst = 256, LobbiesLast = 511;
        public const ushort RaceControlFirst = 512, RaceControlLast = 767;

        /// <summary><see cref="HelloRequest"/> → <see cref="HelloOk"/>. First request of every connection.</summary>
        public const ushort Hello = 1;
        /// <summary><see cref="PingRequest"/> → <see cref="PingResponse"/>.</summary>
        public const ushort Ping = 2;
        /// <summary><see cref="CredentialsRequest"/> → <see cref="Session"/>.</summary>
        public const ushort Register = 16;
        /// <summary><see cref="CredentialsRequest"/> → <see cref="Session"/>.</summary>
        public const ushort Login = 17;
        /// <summary><see cref="ResumeRequest"/> → <see cref="Session"/>.</summary>
        public const ushort Resume = 18;
        /// <summary>Empty → empty. Revokes this connection's session, the connection stays open.</summary>
        public const ushort Logout = 19;
        /// <summary><see cref="SetCountryRequest"/> → <see cref="Account"/>.</summary>
        public const ushort SetCountry = 20;
        /// <summary>listed bool → empty. Needs login.</summary>
        public const ushort SetProfileListed = 21;
        /// <summary><see cref="PlayerListRequest"/> (with sort) → <see cref="PlayerPage"/> (listed accounts).</summary>
        public const ushort ListPlayers = 22;
        /// <summary><see cref="GetProfilesRequest"/> → list of <see cref="PlayerSummary"/>.</summary>
        public const ushort GetProfiles = 23;
        /// <summary>accountId u64 → <see cref="PlayerProfile"/>.</summary>
        public const ushort GetProfile = 24;
        /// <summary><see cref="PlayerListRequest"/> (no sort) → <see cref="PlayerPage"/> with the own row.</summary>
        public const ushort ScoreLeaderboard = 25;
        /// <summary><see cref="DeleteAccountRequest"/> → empty. Needs login; the connection stays open, logged out.</summary>
        public const ushort DeleteAccount = 26;
        /// <summary><see cref="SyncAchievementsRequest"/> → <see cref="AchievementTotals"/>. Needs login.</summary>
        public const ushort SyncAchievements = 27;
        /// <summary><see cref="AchievementSharesRequest"/> → <see cref="AchievementShareCounts"/>.</summary>
        public const ushort AchievementShares = 28;
        /// <summary><see cref="ChangeUsernameRequest"/> → <see cref="Account"/> (renamed). Needs login.</summary>
        public const ushort ChangeUsername = 29;

        /// <summary><see cref="IdRequest"/> (run, reason) → empty. Moderators only, like every type up to 127.</summary>
        public const ushort ModHideRun = 64;
        /// <summary><see cref="IdRequest"/> (run) → empty.</summary>
        public const ushort ModUnhideRun = 65;
        /// <summary><see cref="IdRequest"/> (account, reason) → empty. Revokes the account's sessions.</summary>
        public const ushort ModDisableAccount = 66;
        /// <summary><see cref="IdRequest"/> (account) → empty.</summary>
        public const ushort ModEnableAccount = 67;
        /// <summary><see cref="ModListFlaggedRequest"/> → total, runs.</summary>
        public const ushort ModListFlagged = 68;
        /// <summary><see cref="IdRequest"/> (run) → list of <see cref="RunReport"/>.</summary>
        public const ushort ModRunReports = 69;
        /// <summary><see cref="IdRequest"/> (account) → <see cref="ModAccountInfo"/>.</summary>
        public const ushort ModGetAccount = 70;
        /// <summary><see cref="IdPageRequest"/> (board, 0 = all) → total, hidden runs.</summary>
        public const ushort ModListHidden = 71;

        /// <summary><see cref="ListBoardsRequest"/> → list of <see cref="BoardInfo"/>.</summary>
        public const ushort ListBoards = 128;
        /// <summary><see cref="GetLeaderboardRequest"/> → <see cref="LeaderboardPage"/>.</summary>
        public const ushort GetLeaderboard = 129;
        /// <summary><see cref="UploadRunRequest"/> → <see cref="UploadResult"/>.</summary>
        public const ushort UploadRun = 130;
        /// <summary><see cref="IdRequest"/> (run) → <see cref="DownloadRunResponse"/>.</summary>
        public const ushort DownloadRun = 131;
        /// <summary><see cref="ReportRunRequest"/> → flags u32.</summary>
        public const ushort ReportRun = 132;
        /// <summary>trackId string → list of <see cref="RunEntry"/> (the account's runs on the track).</summary>
        public const ushort MyRuns = 133;
        /// <summary><see cref="IdPageRequest"/> (account) → total, the account's runs on every board.</summary>
        public const ushort PlayerRuns = 134;
        /// <summary><see cref="IdRequest"/> (run) → <see cref="RunHiddenInfo"/>. Moderators, or the run's owner.</summary>
        public const ushort RunHiddenReason = 135;
    }

    /// <summary>Push types: their own numbering in the ranges of <see cref="MessageType"/>.</summary>
    public static class PushType
    {
        /// <summary><see cref="DisconnectPush"/>: the server is closing the connection.</summary>
        public const ushort Disconnect = 1;
        /// <summary><see cref="Account"/>: the logged-in account changed on another connection (renamed).</summary>
        public const ushort AccountUpdated = 16;
    }

    /// <summary>First field of every response. Anything but <see cref="Ok"/> is followed by a message for the player.</summary>
    public enum Status : ushort
    {
        Ok = 0,
        BadRequest = 1,
        NotLoggedIn = 2,
        InvalidCredentials = 3,
        UsernameTaken = 4,
        InvalidUsername = 5,
        InvalidPassword = 6,
        RateLimited = 7,
        /// <summary>Disabled by a moderator; the message holds the reason.</summary>
        AccountDisabled = 8,
        /// <summary>Protocol revision differs: the game needs an update.</summary>
        VersionMismatch = 9,
        NotFound = 10,
        /// <summary>Logged in but not allowed (not a moderator, ...).</summary>
        Forbidden = 11,
        TooLarge = 12,
        ServerError = 13,
        /// <summary>The session token is unknown, expired or revoked: log in again.</summary>
        SessionInvalid = 14,
        /// <summary>Unknown message type.</summary>
        Unsupported = 15,
    }

    /// <summary>Account role (u8).</summary>
    public enum AccountRole : byte
    {
        Player = 0,
        /// <summary>Moderates in game (phase 2 and 3). Server administration is the server's command line.</summary>
        Moderator = 1,
        /// <summary>Holds the anonymized runs of a deleted account ("anon-…", never logs in; rows say "Anonymous").</summary>
        Anonymous = 2,
    }
}
