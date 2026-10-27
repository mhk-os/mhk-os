using System;
using System.Collections.Generic;

namespace MHK.Online
{
    /// <summary>Why a client reported a run (ReportRun reason, u8): what the ghost validator detected.</summary>
    public enum FlagReason : byte
    {
        /// <summary>Above the kart's possible speed.</summary>
        Speed = 1,
        Acceleration = 2,
        /// <summary>Moved without a respawn cut.</summary>
        Teleport = 3,
        /// <summary>Inside static geometry.</summary>
        WallClip = 4,
        /// <summary>Airborne without cause.</summary>
        Flying = 5,
        /// <summary>Lap without all checkpoints, wrong finish.</summary>
        Checkpoints = 6,
        /// <summary>Lap times disagree with the frames.</summary>
        LapTimes = 7,
        /// <summary>Re-simulation from the recorded inputs ends elsewhere.</summary>
        Divergence = 8,
        /// <summary>Inconsistent data.</summary>
        Data = 9,
        Other = 255,
    }

    /// <summary>Encoding helpers of the phase-2 structures.</summary>
    static class TimeTrialWire
    {
        // Times that may be "none" are 0 on the wire and default(DateTime) here.
        public static void Time(WireWriter w, DateTime v) =>
            w.I64(v == default ? 0 : new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)).ToUnixTimeMilliseconds());

        public static DateTime Time(WireReader r)
        {
            var t = r.Time();
            return t.ToUnixTimeMilliseconds() == 0 ? default : t.UtcDateTime;
        }

        /// <summary>A u32 count or rank, which this side keeps as int.</summary>
        public static void U32(WireWriter w, int v)
        {
            if (v < 0) throw new ArgumentOutOfRangeException(nameof(v), "Negative u32.");
            w.U32((uint)v);
        }

        public static int U32(WireReader r)
        {
            var v = r.U32();
            if (v > int.MaxValue) throw new WireFormatException("u32 over 2^31-1.");
            return (int)v;
        }

        public static void U8(WireWriter w, int v)
        {
            if (v < 0 || v > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(v), "Not a u8.");
            w.U8((byte)v);
        }

        public static void Runs(WireWriter w, List<RunEntry> runs, int max)
        {
            var count = runs?.Count ?? 0;
            w.ListCount(count, max);
            for (var i = 0; i < count; i++) runs[i].Write(w);
        }

        public static List<RunEntry> Runs(WireReader r, int max)
        {
            var count = r.ListCount(max, RunEntry.MinBytes);
            var list = new List<RunEntry>(count);
            for (var i = 0; i < count; i++) list.Add(RunEntry.Read(r));
            return list;
        }
    }

    /// <summary>A leaderboard: runs of one track, engine class, lap count, content version and track hash.</summary>
    public sealed class BoardInfo
    {
        public const int MaxTrackId = 96, MaxTrackHash = 32, MaxList = 200;
        // u64 + 2 empty strings + u8 u8 i32 u32 i32 i64.
        internal const int MinBytes = 8 + 1 + 1 + 1 + 4 + 1 + 4 + 4 + 8;

        public long boardId;
        public string trackId = "";
        public int engineClass;
        public int laps;
        public int contentVersion;
        public string trackHash = "";
        /// <summary>Accounts with a visible run.</summary>
        public int players;
        /// <summary>Fastest visible finish, 0 when none.</summary>
        public int bestMs;
        /// <summary>UTC, default when none.</summary>
        public DateTime lastUpload;

        public void Write(WireWriter w)
        {
            w.I64(boardId);
            w.String(trackId, MaxTrackId);
            TimeTrialWire.U8(w, engineClass);
            TimeTrialWire.U8(w, laps);
            w.I32(contentVersion);
            w.String(trackHash, MaxTrackHash);
            TimeTrialWire.U32(w, players);
            w.I32(bestMs);
            TimeTrialWire.Time(w, lastUpload);
        }

        public static BoardInfo Read(WireReader r) => new()
        {
            boardId = r.I64(), trackId = r.String(MaxTrackId), engineClass = r.U8(), laps = r.U8(), contentVersion = r.I32(),
            trackHash = r.String(MaxTrackHash), players = TimeTrialWire.U32(r), bestMs = r.I32(), lastUpload = TimeTrialWire.Time(r),
        };
    }

    /// <summary>One uploaded run as a leaderboard row (or a run of MyRuns, an upload's answer, a flagged run).</summary>
    public sealed class RunEntry
    {
        public const int MaxUsername = 20, MaxCountry = 2, MaxId = 96, MaxGhostId = 32, MaxLaps = 99;
        // Fixed fields with empty strings and lists.
        internal const int MinBytes = 8 + 4 + 8 + 1 + 1 + 4 + 4 + 1 + 1 + 1 + 1 + 8 + 8 + 4 + 1 + 1 + 4 + 8 + 1 + 1;

        public long runId;
        /// <summary>Rank in the requested (filtered) list from 1, 0 when not ranked (hidden, or not the account's best).</summary>
        public int rank;
        public long accountId;
        public string username = "";
        public string country = "";
        public int finishMs;
        public int bestLapMs;
        public int[] lapMs = Array.Empty<int>();
        public string kartId = "";
        public string characterId = "";
        /// <summary>The ghost file's header id.</summary>
        public string ghostId = "";
        /// <summary>Header date (UTC).</summary>
        public DateTime recorded;
        public DateTime uploaded;
        /// <summary>Distinct accounts that reported the run.</summary>
        public int flags;
        /// <summary>Kart physics, engine class or laps differ from what most accounts used (display only).</summary>
        public bool setupMismatch;
        /// <summary>Hidden by a moderator; only moderators ever see it set.</summary>
        public bool hidden;
        /// <summary>Compressed bytes of the ghost file.</summary>
        public int fileSize;
        public long boardId;
        /// <summary>The board's track.</summary>
        public string trackId = "";
        /// <summary>The board's engine class (0-2).</summary>
        public int engineClass;

        public void Write(WireWriter w)
        {
            w.I64(runId);
            TimeTrialWire.U32(w, rank);
            w.I64(accountId);
            w.String(username, MaxUsername);
            w.String(country, MaxCountry);
            w.I32(finishMs);
            w.I32(bestLapMs);
            var laps = lapMs ?? Array.Empty<int>();
            w.ListCount(laps.Length, MaxLaps);
            foreach (var l in laps) w.I32(l);
            w.String(kartId, MaxId);
            w.String(characterId, MaxId);
            w.String(ghostId, MaxGhostId);
            TimeTrialWire.Time(w, recorded);
            TimeTrialWire.Time(w, uploaded);
            TimeTrialWire.U32(w, flags);
            w.Bool(setupMismatch);
            w.Bool(hidden);
            TimeTrialWire.U32(w, fileSize);
            w.I64(boardId);
            w.String(trackId, MaxId);
            TimeTrialWire.U8(w, engineClass);
        }

        public static RunEntry Read(WireReader r)
        {
            var e = new RunEntry
            {
                runId = r.I64(), rank = TimeTrialWire.U32(r), accountId = r.I64(), username = r.String(MaxUsername),
                country = r.String(MaxCountry), finishMs = r.I32(), bestLapMs = r.I32(),
            };
            var laps = new int[r.ListCount(MaxLaps, 4)];
            for (var i = 0; i < laps.Length; i++) laps[i] = r.I32();
            e.lapMs = laps;
            e.kartId = r.String(MaxId);
            e.characterId = r.String(MaxId);
            e.ghostId = r.String(MaxGhostId);
            e.recorded = TimeTrialWire.Time(r);
            e.uploaded = TimeTrialWire.Time(r);
            e.flags = TimeTrialWire.U32(r);
            e.setupMismatch = r.Bool();
            e.hidden = r.Bool();
            e.fileSize = TimeTrialWire.U32(r);
            e.boardId = r.I64();
            e.trackId = r.String(MaxId);
            e.engineClass = r.U8();
            return e;
        }
    }

    /// <summary>Which rows of a leaderboard to list; empty strings match everything.</summary>
    public sealed class LeaderboardFilter
    {
        public const int MaxContinent = 2;

        /// <summary>Case-insensitive substring of the account name.</summary>
        public string username = "";
        /// <summary>ISO 3166-1 alpha-2.</summary>
        public string country = "";
        /// <summary>AF AN AS EU NA OC SA.</summary>
        public string continent = "";
        public string kartId = "";
        public string characterId = "";
        /// <summary>Also hidden runs; honoured for moderators only.</summary>
        public bool includeHidden;

        public void Write(WireWriter w)
        {
            w.String(username, RunEntry.MaxUsername);
            w.String(country, RunEntry.MaxCountry);
            w.String(continent, MaxContinent);
            w.String(kartId, RunEntry.MaxId);
            w.String(characterId, RunEntry.MaxId);
            w.Bool(includeHidden);
        }

        public static LeaderboardFilter Read(WireReader r) => new()
        {
            username = r.String(RunEntry.MaxUsername), country = r.String(RunEntry.MaxCountry), continent = r.String(MaxContinent),
            kartId = r.String(RunEntry.MaxId), characterId = r.String(RunEntry.MaxId), includeHidden = r.Bool(),
        };
    }

    /// <summary>Answer of GetLeaderboard.</summary>
    public sealed class LeaderboardPage
    {
        public const int MaxLimit = 100;

        /// <summary>Rows in the filtered list.</summary>
        public int total;
        public List<RunEntry> entries = new();
        /// <summary>The logged-in account's best matching the filter, with its rank; null when none.</summary>
        public RunEntry mine;

        public void Write(WireWriter w)
        {
            TimeTrialWire.U32(w, total);
            TimeTrialWire.Runs(w, entries, MaxLimit);
            w.Bool(mine != null);
            mine?.Write(w);
        }

        public static LeaderboardPage Read(WireReader r)
        {
            var p = new LeaderboardPage { total = TimeTrialWire.U32(r), entries = TimeTrialWire.Runs(r, MaxLimit) };
            if (r.Bool()) p.mine = RunEntry.Read(r);
            return p;
        }
    }

    /// <summary>Answer of UploadRun.</summary>
    public sealed class UploadResult
    {
        /// <summary>Rank within the unfiltered board, 0 if not the account's best.</summary>
        public RunEntry run = new();
        /// <summary>The run is now the account's best on its board.</summary>
        public bool personalBest;
        /// <summary>This account had uploaded the file before; <see cref="run"/> is the existing run.</summary>
        public bool duplicate;

        public void Write(WireWriter w)
        {
            run.Write(w);
            w.Bool(personalBest);
            w.Bool(duplicate);
        }

        public static UploadResult Read(WireReader r) => new() { run = RunEntry.Read(r), personalBest = r.Bool(), duplicate = r.Bool() };
    }

    /// <summary>One player's report on a run (ModRunReports).</summary>
    public sealed class RunReport
    {
        public const int MaxDetail = 512, MaxList = 500;

        public string reporter = "";
        public FlagReason reason;
        /// <summary>Ghost time of the detection.</summary>
        public float atSeconds;
        public string detail = "";
        public DateTime created;
        /// <summary>False when the reporter's reports are ignored.</summary>
        public bool counted;

        public void Write(WireWriter w)
        {
            w.String(reporter, RunEntry.MaxUsername);
            w.U8((byte)reason);
            w.F32(atSeconds);
            w.String(detail, MaxDetail);
            TimeTrialWire.Time(w, created);
            w.Bool(counted);
        }

        public static RunReport Read(WireReader r) => new()
        {
            reporter = r.String(RunEntry.MaxUsername), reason = (FlagReason)r.U8(), atSeconds = r.F32(), detail = r.String(MaxDetail),
            created = TimeTrialWire.Time(r), counted = r.Bool(),
        };
    }

    /// <summary>An account as moderators see it (ModGetAccount).</summary>
    public sealed class ModAccountInfo
    {
        public const int MaxReason = 256;

        public Account account = new();
        public bool disabled;
        public string disabledReason = "";
        public int runs;
        public int hiddenRuns;
        /// <summary>Kicks from races for cheating (phase 3).</summary>
        public int cheatKicks;
        public DateTime created;
        public DateTime lastLogin;

        public void Write(WireWriter w)
        {
            account.Write(w);
            w.Bool(disabled);
            w.String(disabledReason, MaxReason);
            TimeTrialWire.U32(w, runs);
            TimeTrialWire.U32(w, hiddenRuns);
            TimeTrialWire.U32(w, cheatKicks);
            TimeTrialWire.Time(w, created);
            TimeTrialWire.Time(w, lastLogin);
        }

        public static ModAccountInfo Read(WireReader r) => new()
        {
            account = Account.Read(r), disabled = r.Bool(), disabledReason = r.String(MaxReason), runs = TimeTrialWire.U32(r),
            hiddenRuns = TimeTrialWire.U32(r), cheatKicks = TimeTrialWire.U32(r), created = TimeTrialWire.Time(r),
            lastLogin = TimeTrialWire.Time(r),
        };
    }

    /// <summary>Request 128.</summary>
    public sealed class ListBoardsRequest
    {
        /// <summary><see cref="engineClass"/> that matches every class.</summary>
        public const byte AnyEngineClass = 255;

        public string trackId = "";
        public byte engineClass = AnyEngineClass;
        /// <summary>0 = any.</summary>
        public int contentVersion;

        public void Write(WireWriter w)
        {
            w.String(trackId, BoardInfo.MaxTrackId);
            w.U8(engineClass);
            w.I32(contentVersion);
        }

        public static ListBoardsRequest Read(WireReader r) => new() { trackId = r.String(BoardInfo.MaxTrackId), engineClass = r.U8(), contentVersion = r.I32() };

        public static void WriteAnswer(WireWriter w, List<BoardInfo> boards)
        {
            var count = boards?.Count ?? 0;
            w.ListCount(count, BoardInfo.MaxList);
            for (var i = 0; i < count; i++) boards[i].Write(w);
        }

        public static List<BoardInfo> ReadAnswer(WireReader r)
        {
            var count = r.ListCount(BoardInfo.MaxList, BoardInfo.MinBytes);
            var list = new List<BoardInfo>(count);
            for (var i = 0; i < count; i++) list.Add(BoardInfo.Read(r));
            return list;
        }
    }

    /// <summary>Request 129 → <see cref="LeaderboardPage"/>.</summary>
    public sealed class GetLeaderboardRequest
    {
        public long boardId;
        public int offset;
        /// <summary>1 to <see cref="LeaderboardPage.MaxLimit"/>.</summary>
        public ushort limit;
        public LeaderboardFilter filter = new();

        public void Write(WireWriter w)
        {
            w.I64(boardId);
            TimeTrialWire.U32(w, offset);
            w.U16(limit);
            (filter ?? new LeaderboardFilter()).Write(w);
        }

        public static GetLeaderboardRequest Read(WireReader r) => new()
        {
            boardId = r.I64(), offset = TimeTrialWire.U32(r), limit = r.U16(), filter = LeaderboardFilter.Read(r),
        };
    }

    /// <summary>Request 130 → <see cref="UploadResult"/>: a whole <c>.mhkghost</c> file.</summary>
    public sealed class UploadRunRequest
    {
        public const int MaxFile = 8 * 1024 * 1024;

        public byte[] file = Array.Empty<byte>();

        public void Write(WireWriter w) => w.Bytes(file, MaxFile);

        public static UploadRunRequest Read(WireReader r) => new() { file = r.Bytes(MaxFile) };
    }

    /// <summary>Answer of DownloadRun (request 131, body: runId u64): the run and its ghost file.</summary>
    public sealed class DownloadRunResponse
    {
        public RunEntry run = new();
        public byte[] file = Array.Empty<byte>();

        public void Write(WireWriter w)
        {
            run.Write(w);
            w.Bytes(file, UploadRunRequest.MaxFile);
        }

        public static DownloadRunResponse Read(WireReader r) => new() { run = RunEntry.Read(r), file = r.Bytes(UploadRunRequest.MaxFile) };
    }

    /// <summary>Request 132 → flags u32 (the current count).</summary>
    public sealed class ReportRunRequest
    {
        public long runId;
        public FlagReason reason;
        public float atSeconds;
        public string detail = "";

        public void Write(WireWriter w)
        {
            w.I64(runId);
            w.U8((byte)reason);
            w.F32(atSeconds);
            w.String(detail, RunReport.MaxDetail);
        }

        public static ReportRunRequest Read(WireReader r) => new()
        {
            runId = r.I64(), reason = (FlagReason)r.U8(), atSeconds = r.F32(), detail = r.String(RunReport.MaxDetail),
        };
    }

    /// <summary>A request of one id (u64) and an optional reason: DownloadRun, ModHideRun, ModUnhideRun, ModDisableAccount, ModEnableAccount, ModRunReports, ModGetAccount.</summary>
    public sealed class IdRequest
    {
        public long id;
        /// <summary>Written only when not null (the Hide/Disable requests).</summary>
        public string reason;

        public void Write(WireWriter w)
        {
            w.I64(id);
            if (reason != null) w.String(reason, ModAccountInfo.MaxReason);
        }

        public static IdRequest Read(WireReader r, bool withReason) => new() { id = r.I64(), reason = withReason ? r.String(ModAccountInfo.MaxReason) : null };
    }

    /// <summary>Request 68 → total u32, list&lt;RunEntry&gt; ≤ 100.</summary>
    public sealed class ModListFlaggedRequest
    {
        public int offset;
        public ushort limit;
        public bool includeHidden;

        public void Write(WireWriter w)
        {
            TimeTrialWire.U32(w, offset);
            w.U16(limit);
            w.Bool(includeHidden);
        }

        public static ModListFlaggedRequest Read(WireReader r) => new() { offset = TimeTrialWire.U32(r), limit = r.U16(), includeHidden = r.Bool() };

        public static void WriteAnswer(WireWriter w, int total, List<RunEntry> runs)
        {
            TimeTrialWire.U32(w, total);
            TimeTrialWire.Runs(w, runs, LeaderboardPage.MaxLimit);
        }

        public static (int total, List<RunEntry> runs) ReadAnswer(WireReader r) => (TimeTrialWire.U32(r), TimeTrialWire.Runs(r, LeaderboardPage.MaxLimit));
    }

    /// <summary>List answers of MyRuns (≤ 500 runs) and ModRunReports (≤ 500 reports).</summary>
    public static class ListAnswers
    {
        public const int MaxMyRuns = 500;

        public static void WriteRuns(WireWriter w, List<RunEntry> runs) => TimeTrialWire.Runs(w, runs, MaxMyRuns);

        public static List<RunEntry> ReadRuns(WireReader r) => TimeTrialWire.Runs(r, MaxMyRuns);

        public static void WriteReports(WireWriter w, List<RunReport> reports)
        {
            var count = reports?.Count ?? 0;
            w.ListCount(count, RunReport.MaxList);
            for (var i = 0; i < count; i++) reports[i].Write(w);
        }

        public static List<RunReport> ReadReports(WireReader r)
        {
            // String, u8, f32, string, i64, bool.
            var count = r.ListCount(RunReport.MaxList, 1 + 1 + 4 + 1 + 8 + 1);
            var list = new List<RunReport>(count);
            for (var i = 0; i < count; i++) list.Add(RunReport.Read(r));
            return list;
        }
    }
}
