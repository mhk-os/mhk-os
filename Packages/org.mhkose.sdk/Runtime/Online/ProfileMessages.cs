using System;
using System.Collections.Generic;

namespace MHK.Online
{
    /// <summary>Order of ListPlayers (u8).</summary>
    public enum PlayerSort : byte
    {
        /// <summary>Newest registration first.</summary>
        Newest = 0,
        /// <summary>Username A-Z.</summary>
        Name = 1,
    }

    /// <summary>
    /// An account's public summary: the name in lists with its rank (ListPlayers, GetProfiles, ScoreLeaderboard).
    /// <see cref="playtime"/> is not part of its own encoding: the message carrying summaries ends with the playtimes of
    /// all of them (<see cref="ReadPlaytimes"/>), which older servers do not send.
    /// </summary>
    public sealed class PlayerSummary
    {
        public const int MaxList = 100;
        // u64, two empty strings, u8, i64 i64, bool, u32 u32.
        internal const int MinBytes = 8 + 1 + 1 + 1 + 8 + 8 + 1 + 4 + 4;

        public long accountId;
        public string username = "";
        /// <summary>ISO 3166-1 alpha-2, empty = unknown.</summary>
        public string country = "";
        public AccountRole role;
        /// <summary>Registration (UTC).</summary>
        public DateTime created;
        /// <summary>Last login, resume or end of a logged-in connection (UTC), default = never.</summary>
        public DateTime lastActive;
        /// <summary>The account has a logged-in connection now.</summary>
        public bool online;
        /// <summary>Points of the online races so far.</summary>
        public int score;
        /// <summary>1 + accounts with a higher score, 0 = unranked (score 0 or disabled).</summary>
        public int rank;
        /// <summary>
        /// Seconds the account played (the game's time played counter, AchievementStats.PlaytimeSeconds, summed over
        /// every device by the server); 0 = none synced, -1 = unknown (a server without playtimes).
        /// </summary>
        public long playtime = -1;

        public bool IsModerator => role == AccountRole.Moderator;

        public void Write(WireWriter w)
        {
            w.I64(accountId);
            w.String(username, Account.MaxUsername);
            w.String(country, Account.MaxCountry);
            w.U8((byte)role);
            TimeTrialWire.Time(w, created);
            TimeTrialWire.Time(w, lastActive);
            w.Bool(online);
            TimeTrialWire.U32(w, score);
            TimeTrialWire.U32(w, rank);
        }

        public static PlayerSummary Read(WireReader r) => new()
        {
            accountId = r.I64(), username = r.String(Account.MaxUsername), country = r.String(Account.MaxCountry), role = (AccountRole)r.U8(),
            created = TimeTrialWire.Time(r), lastActive = TimeTrialWire.Time(r), online = r.Bool(), score = TimeTrialWire.U32(r),
            rank = TimeTrialWire.U32(r),
        };

        internal static void WriteList(WireWriter w, List<PlayerSummary> players)
        {
            var count = players?.Count ?? 0;
            w.ListCount(count, MaxList);
            for (var i = 0; i < count; i++) players[i].Write(w);
        }

        internal static List<PlayerSummary> ReadList(WireReader r)
        {
            var count = r.ListCount(MaxList, MinBytes);
            var list = new List<PlayerSummary>(count);
            for (var i = 0; i < count; i++) list.Add(Read(r));
            return list;
        }

        /// <summary>Answer of GetProfiles: the summaries and their playtimes.</summary>
        internal static List<PlayerSummary> ReadSummaries(WireReader r)
        {
            var list = ReadList(r);
            ReadPlaytimes(r, list, null);
            return list;
        }

        /// <summary>The end of a message carrying summaries: list&lt;u64&gt; with the playtime of each, <paramref name="players"/> first, then <paramref name="mine"/> when not null.</summary>
        internal static void WritePlaytimes(WireWriter w, List<PlayerSummary> players, PlayerSummary mine)
        {
            var count = players?.Count ?? 0;
            w.ListCount(count + (mine != null ? 1 : 0), MaxList + 1);
            for (var i = 0; i < count; i++) w.U64((ulong)Math.Max(0L, players[i].playtime));
            if (mine != null) w.U64((ulong)Math.Max(0L, mine.playtime));
        }

        /// <summary>
        /// Reads what <see cref="WritePlaytimes"/> wrote. A body that ends before it (an older server) leaves every
        /// <see cref="playtime"/> at -1; a list of another length than the summaries is malformed.
        /// </summary>
        internal static void ReadPlaytimes(WireReader r, List<PlayerSummary> players, PlayerSummary mine)
        {
            if (r.Remaining == 0) return;
            var count = players?.Count ?? 0;
            var expected = count + (mine != null ? 1 : 0);
            var got = r.ListCount(expected, 8);
            if (got != expected) throw new WireFormatException($"{got} playtimes for {expected} players.");
            for (var i = 0; i < count; i++) players[i].playtime = Seconds(r.U64());
            if (mine != null) mine.playtime = Seconds(r.U64());
        }

        static long Seconds(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;
    }

    /// <summary>An account's mini-profile (GetProfile). The moderation fields are set for moderators only.</summary>
    public sealed class PlayerProfile
    {
        public PlayerSummary player = new();
        /// <summary>Shown in ListPlayers.</summary>
        public bool listed;
        /// <summary>Online races raced.</summary>
        public int races;
        public int wins;
        public int podiums;
        /// <summary>Visible uploaded time-trial runs.</summary>
        public int ttRuns;
        /// <summary>Boards with a visible run.</summary>
        public int ttBoards;
        /// <summary>Best leaderboard rank on any board, 0 = none.</summary>
        public int ttBestRank;
        public bool disabled;
        public string disabledReason = "";
        public int cheatKicks;
        public int contentStrikes;
        public int hiddenRuns;
        /// <summary>Runs with at least one flag.</summary>
        public int flaggedRuns;

        public void Write(WireWriter w)
        {
            player.Write(w);
            w.Bool(listed);
            TimeTrialWire.U32(w, races);
            TimeTrialWire.U32(w, wins);
            TimeTrialWire.U32(w, podiums);
            TimeTrialWire.U32(w, ttRuns);
            TimeTrialWire.U32(w, ttBoards);
            TimeTrialWire.U32(w, ttBestRank);
            w.Bool(disabled);
            w.String(disabledReason, ModAccountInfo.MaxReason);
            TimeTrialWire.U32(w, cheatKicks);
            TimeTrialWire.U32(w, contentStrikes);
            TimeTrialWire.U32(w, hiddenRuns);
            TimeTrialWire.U32(w, flaggedRuns);
            PlayerSummary.WritePlaytimes(w, null, player);
        }

        public static PlayerProfile Read(WireReader r)
        {
            var p = new PlayerProfile
            {
                player = PlayerSummary.Read(r), listed = r.Bool(), races = TimeTrialWire.U32(r), wins = TimeTrialWire.U32(r),
                podiums = TimeTrialWire.U32(r), ttRuns = TimeTrialWire.U32(r), ttBoards = TimeTrialWire.U32(r), ttBestRank = TimeTrialWire.U32(r),
                disabled = r.Bool(), disabledReason = r.String(ModAccountInfo.MaxReason), cheatKicks = TimeTrialWire.U32(r),
                contentStrikes = TimeTrialWire.U32(r), hiddenRuns = TimeTrialWire.U32(r), flaggedRuns = TimeTrialWire.U32(r),
            };
            PlayerSummary.ReadPlaytimes(r, null, p.player);
            return p;
        }
    }

    /// <summary>Answer of ListPlayers and ScoreLeaderboard: one page of players.</summary>
    public sealed class PlayerPage
    {
        /// <summary>Rows after the search.</summary>
        public int total;
        public List<PlayerSummary> players = new();
        /// <summary>ScoreLeaderboard: the logged-in account when ranked, else null.</summary>
        public PlayerSummary mine;

        /// <summary>ListPlayers: total, the page and its playtimes.</summary>
        public static PlayerPage Read(WireReader r)
        {
            var p = new PlayerPage { total = TimeTrialWire.U32(r), players = PlayerSummary.ReadList(r) };
            PlayerSummary.ReadPlaytimes(r, p.players, null);
            return p;
        }

        /// <summary>ScoreLeaderboard: total, the page, the own row and their playtimes.</summary>
        public static PlayerPage ReadRanked(WireReader r)
        {
            var p = new PlayerPage { total = TimeTrialWire.U32(r), players = PlayerSummary.ReadList(r) };
            if (r.Bool()) p.mine = PlayerSummary.Read(r);
            PlayerSummary.ReadPlaytimes(r, p.players, p.mine);
            return p;
        }

        public void Write(WireWriter w, bool ranked)
        {
            TimeTrialWire.U32(w, total);
            PlayerSummary.WriteList(w, players);
            if (ranked)
            {
                w.Bool(mine != null);
                mine?.Write(w);
            }
            PlayerSummary.WritePlaytimes(w, players, ranked ? mine : null);
        }
    }

    /// <summary>Requests 22 (ListPlayers: with <see cref="sort"/>) and 25 (ScoreLeaderboard: without) → <see cref="PlayerPage"/>.</summary>
    public sealed class PlayerListRequest
    {
        public int offset;
        /// <summary>1 to <see cref="PlayerSummary.MaxList"/>.</summary>
        public ushort limit;
        /// <summary>Case-insensitive username substring, empty = all.</summary>
        public string search = "";
        /// <summary>ListPlayers only.</summary>
        public PlayerSort? sort;

        public void Write(WireWriter w)
        {
            TimeTrialWire.U32(w, offset);
            w.U16(limit);
            w.String(search, Account.MaxUsername);
            if (sort.HasValue) w.U8((byte)sort.Value);
        }

        public static PlayerListRequest Read(WireReader r, bool withSort) => new()
        {
            offset = TimeTrialWire.U32(r), limit = r.U16(), search = r.String(Account.MaxUsername), sort = withSort ? (PlayerSort)r.U8() : (PlayerSort?)null,
        };
    }

    /// <summary>Request 23 → list of <see cref="PlayerSummary"/> (the ids that exist, any order).</summary>
    public sealed class GetProfilesRequest
    {
        public List<long> ids = new();

        public void Write(WireWriter w)
        {
            w.ListCount(ids.Count, PlayerSummary.MaxList);
            foreach (var id in ids) w.I64(id);
        }

        public static GetProfilesRequest Read(WireReader r)
        {
            var count = r.ListCount(PlayerSummary.MaxList, 8);
            var request = new GetProfilesRequest { ids = new List<long>(count) };
            for (var i = 0; i < count; i++) request.ids.Add(r.I64());
            return request;
        }
    }

    /// <summary>
    /// A page request about one id (u64), then offset u32 and limit u16: PlayerRuns (134, account) and ModListHidden
    /// (71, board, 0 = every board) → total u32, list&lt;RunEntry&gt; ≤ 100.
    /// </summary>
    public sealed class IdPageRequest
    {
        public long id;
        public int offset;
        /// <summary>1 to <see cref="LeaderboardPage.MaxLimit"/>.</summary>
        public ushort limit;

        public void Write(WireWriter w)
        {
            w.I64(id);
            TimeTrialWire.U32(w, offset);
            w.U16(limit);
        }

        public static IdPageRequest Read(WireReader r) => new() { id = r.I64(), offset = TimeTrialWire.U32(r), limit = r.U16() };
    }
}
