using System;
using System.Collections.Generic;
using MHK.Core;

namespace MHK.Online
{
    /// <summary>Request types of content agreement and lobbies (docs/protocol.md, multiplayer). All need a login.</summary>
    public static class LobbyMessageType
    {
        /// <summary><see cref="ContentHelloRequest"/> → <see cref="ContentHelloAnswer"/>.</summary>
        public const ushort ContentHello = 256;
        /// <summary>list of <see cref="PackManifest"/> → empty: the manifests ContentHello asked for.</summary>
        public const ushort ContentManifests = 257;
        /// <summary>Empty → list of <see cref="LobbySummary"/> (public instances of every category).</summary>
        public const ushort ListLobbies = 258;
        /// <summary>lobbyId u64, code string → <see cref="LobbyState"/>.</summary>
        public const ushort JoinLobby = 259;
        /// <summary>category u8 → <see cref="LobbyState"/>.</summary>
        public const ushort QuickJoin = 260;
        /// <summary><see cref="RoomSettings"/> → <see cref="LobbyState"/>.</summary>
        public const ushort CreateRoom = 261;
        /// <summary><see cref="RoomSettings"/> → empty (owner, Waiting only).</summary>
        public const ushort UpdateRoom = 262;
        public const ushort LeaveLobby = 263;
        /// <summary>characterId, kartId → empty.</summary>
        public const ushort SetLoadout = 264;
        public const ushort SetReady = 265;
        /// <summary>trackId ("" = Random) → empty.</summary>
        public const ushort Vote = 266;
        /// <summary>trackId (owner pick; else "") → empty.</summary>
        public const ushort StartRace = 267;
        public const ushort EndRaceEarly = 268;
        /// <summary>accountId u64, reason string → empty.</summary>
        public const ushort KickMember = 269;
    }

    /// <summary>Push types of lobbies and the race results (their own numbering, see <see cref="PushType"/>).</summary>
    public static class LobbyPushType
    {
        /// <summary><see cref="LobbyState"/> after every change.</summary>
        public const ushort LobbyState = 256;
        /// <summary><see cref="LobbyLeftPush"/>: no longer in the lobby.</summary>
        public const ushort LobbyLeft = 257;
        /// <summary>username: someone waits while this player races alone against AI.</summary>
        public const ushort SoloRaceJoinWaiting = 258;
        /// <summary><see cref="RaceResults"/>.</summary>
        public const ushort RaceResults = 523;
    }

    /// <summary>Kind of a pack manifest item (u8).</summary>
    public enum ContentKind : byte
    {
        /// <summary>A track with the circuit layout (definition data; geometry is hashed at race load).</summary>
        RaceTrack = 1,
        Kart = 2,
        Character = 3,
        Powerup = 4,
        StatusEffect = 5,
        Cup = 6,
        /// <summary>A track with the arena layout.</summary>
        Arena = 7,
    }

    /// <summary>RoomSettings.mode and LobbySummary.mode (u8).</summary>
    public enum RoomMode : byte
    {
        RaceSet = 0,
        BattleSet = 1,
        /// <summary>The races of one cup.</summary>
        Championship = 2,
    }

    /// <summary>LobbySummary.phase (u8).</summary>
    public enum LobbyPhase : byte
    {
        Waiting = 0,
        Voting = 1,
        Loading = 2,
        Countdown = 3,
        Racing = 4,
        Results = 5,
        SetResults = 6,
    }

    /// <summary>How a racer's race ended (RaceResultRow.outcome, PlayerLeftRace.outcome; u8).</summary>
    public enum RaceOutcome : byte
    {
        Finished = 0,
        Dnf = 1,
        Left = 2,
        KickedCheating = 3,
        KickedModerator = 4,
        /// <summary>Content or race data differed from the other racers'.</summary>
        Desync = 5,
    }

    /// <summary>Why the player is no longer in a lobby (LobbyLeft.reason, u8).</summary>
    public enum LobbyLeftReason : byte
    {
        Left = 0,
        KickedByOwner = 1,
        KickedByModerator = 2,
        Closed = 3,
    }

    /// <summary>Limits of the lobby structures (bytes for strings, items for lists).</summary>
    public static class LobbyLimits
    {
        public const int PackId = 64, Commit = 40, Hash = 32, Fingerprint = 32, ContentId = 96, AnnouncedPacks = 64;
        public const int ManifestItems = 4096, StrikeMessage = 256, RoomName = 32, LobbyName = 64, RoomPacks = 32;
        public const int Lobbies = 500, Members = 10, Code = 8, RaceRows = 16, KickReason = 256, Message = 256;
        public const int ItemOdds = 64, Username = 20, Country = 2, Laps = 99;
        public const int MinPlayers = 2, MaxPlayers = 10, MaxRacesPerSet = 16;
    }

    /// <summary>Encoding helpers of the lobby structures.</summary>
    static class LobbyWire
    {
        /// <summary><paramref name="s"/> cut to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
        public static string Clip(string s, int maxBytes)
        {
            s ??= "";
            var bytes = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var pair = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                var size = pair ? 4 : s[i] < 0x80 ? 1 : s[i] < 0x800 ? 2 : 3;
                if (bytes + size > maxBytes) return s.Substring(0, i);
                bytes += size;
                if (pair) i++;
            }
            return s;
        }

        public static void Strings(WireWriter w, IReadOnlyList<string> list, int maxCount, int maxBytes)
        {
            var count = list?.Count ?? 0;
            w.ListCount(count, maxCount);
            for (var i = 0; i < count; i++) w.String(list[i], maxBytes);
        }

        public static List<string> Strings(WireReader r, int maxCount, int maxBytes)
        {
            var count = r.ListCount(maxCount);
            var list = new List<string>(count);
            for (var i = 0; i < count; i++) list.Add(r.String(maxBytes));
            return list;
        }

        public static void Odds(WireWriter w, IReadOnlyList<PowerupOdds> odds)
        {
            var count = odds?.Count ?? 0;
            w.ListCount(count, LobbyLimits.ItemOdds);
            for (var i = 0; i < count; i++)
            {
                w.String(odds[i].id, LobbyLimits.ContentId);
                w.F32(odds[i].percent);
            }
        }

        public static List<PowerupOdds> Odds(WireReader r)
        {
            var count = r.ListCount(LobbyLimits.ItemOdds, 1 + 4);
            var list = new List<PowerupOdds>(count);
            for (var i = 0; i < count; i++) list.Add(new PowerupOdds(r.String(LobbyLimits.ContentId), r.F32()));
            return list;
        }

        public static void Rows(WireWriter w, IReadOnlyList<RaceResultRow> rows, int max)
        {
            var count = rows?.Count ?? 0;
            w.ListCount(count, max);
            for (var i = 0; i < count; i++) rows[i].Write(w);
        }

        public static List<RaceResultRow> Rows(WireReader r, int max)
        {
            var count = r.ListCount(max, RaceResultRow.MinBytes);
            var list = new List<RaceResultRow>(count);
            for (var i = 0; i < count; i++) list.Add(RaceResultRow.Read(r));
            return list;
        }
    }

    /// <summary>One announced pack of ContentHello.</summary>
    public sealed class PackRef
    {
        public string packId = "";
        /// <summary><c>ContentPack.version</c>.</summary>
        public string commit = "";
        public string manifestHash = "";

        public void Write(WireWriter w)
        {
            w.String(packId, LobbyLimits.PackId);
            w.String(commit, LobbyLimits.Commit);
            w.String(manifestHash, LobbyLimits.Hash);
        }

        public static PackRef Read(WireReader r) => new()
        {
            packId = r.String(LobbyLimits.PackId), commit = r.String(LobbyLimits.Commit), manifestHash = r.String(LobbyLimits.Hash),
        };
    }

    /// <summary>Request 256: the gameplay content of every enabled pack, as manifest hashes.</summary>
    public sealed class ContentHelloRequest
    {
        public int simVersion;
        public List<PackRef> packs = new();

        public void Write(WireWriter w)
        {
            w.I32(simVersion);
            w.ListCount(packs.Count, LobbyLimits.AnnouncedPacks);
            foreach (var p in packs) p.Write(w);
        }
    }

    /// <summary>A pack whose copy differs from the catalog majority (ContentHello answer).</summary>
    public sealed class ContentStrike
    {
        public string packId = "";
        /// <summary>For the player.</summary>
        public string message = "";
    }

    /// <summary>Answer of ContentHello.</summary>
    public sealed class ContentHelloAnswer
    {
        /// <summary>Manifest hashes the server wants (ContentManifests).</summary>
        public List<string> missing = new();
        public List<ContentStrike> strikes = new();

        public static ContentHelloAnswer Read(WireReader r)
        {
            var a = new ContentHelloAnswer { missing = LobbyWire.Strings(r, LobbyLimits.AnnouncedPacks, LobbyLimits.Hash) };
            var count = r.ListCount(LobbyLimits.AnnouncedPacks, 2);
            for (var i = 0; i < count; i++)
                a.strikes.Add(new ContentStrike { packId = r.String(LobbyLimits.PackId), message = r.String(LobbyLimits.StrikeMessage) });
            return a;
        }
    }

    /// <summary>One item of a pack manifest: a content definition the pack registers and its gameplay fingerprint.</summary>
    public readonly struct ManifestItem
    {
        public readonly ContentKind kind;
        public readonly string id;
        public readonly string fingerprint;

        public ManifestItem(ContentKind kind, string id, string fingerprint)
        {
            this.kind = kind;
            this.id = id;
            this.fingerprint = fingerprint;
        }
    }

    /// <summary>The gameplay content of one pack version (ContentManifests).</summary>
    public sealed class PackManifest
    {
        public string packId = "";
        public string commit = "";
        public int simVersion;
        public string manifestHash = "";
        public List<ManifestItem> items = new();

        public void Write(WireWriter w)
        {
            w.String(packId, LobbyLimits.PackId);
            w.String(commit, LobbyLimits.Commit);
            w.I32(simVersion);
            w.String(manifestHash, LobbyLimits.Hash);
            w.ListCount(items.Count, LobbyLimits.ManifestItems);
            foreach (var item in items)
            {
                w.U8((byte)item.kind);
                w.String(item.id, LobbyLimits.ContentId);
                w.String(item.fingerprint, LobbyLimits.Fingerprint);
            }
        }

        public static void WriteList(WireWriter w, IReadOnlyList<PackManifest> manifests)
        {
            w.ListCount(manifests.Count, LobbyLimits.AnnouncedPacks);
            for (var i = 0; i < manifests.Count; i++) manifests[i].Write(w);
        }
    }

    /// <summary>
    /// Settings of a private room (also the effective settings of a public lobby). Serializable: the Create room screen
    /// keeps the last ones in the user settings. Field names are JSON keys: never rename them.
    /// </summary>
    [Serializable]
    public sealed class RoomSettings
    {
        public string name = "";
        public RoomMode mode = RoomMode.RaceSet;
        public List<string> packs = new();
        public bool anticheat = true;
        /// <summary>0-2 (<see cref="Difficulty"/>).</summary>
        public int engineClass = (int)Difficulty.Medium;
        /// <summary>0 = each track's default, else 1-99.</summary>
        public int laps;
        public bool items = true;
        /// <summary>Empty = default odds.</summary>
        public List<PowerupOdds> itemOdds = new();
        public bool aiWhenAlone = true;
        public int maxPlayers = LobbyLimits.MaxPlayers;
        /// <summary>0 owner picks, 1 vote.</summary>
        public int trackPick = 1;
        public int racesPerSet = 4;
        /// <summary>Championship only.</summary>
        public string cupId = "";

        public bool OwnerPicks => trackPick == 0;

        public RoomSettings Clone()
        {
            var c = (RoomSettings)MemberwiseClone();
            c.packs = new List<string>(packs ?? new List<string>());
            c.itemOdds = new List<PowerupOdds>(itemOdds ?? new List<PowerupOdds>());
            return c;
        }

        public void Write(WireWriter w)
        {
            w.String(name, LobbyLimits.RoomName);
            w.U8((byte)mode);
            LobbyWire.Strings(w, packs, LobbyLimits.RoomPacks, LobbyLimits.PackId);
            w.Bool(anticheat);
            TimeTrialWire.U8(w, engineClass);
            TimeTrialWire.U8(w, laps);
            w.Bool(items);
            LobbyWire.Odds(w, itemOdds);
            w.Bool(aiWhenAlone);
            TimeTrialWire.U8(w, maxPlayers);
            TimeTrialWire.U8(w, trackPick);
            TimeTrialWire.U8(w, racesPerSet);
            w.String(cupId, LobbyLimits.ContentId);
        }

        public static RoomSettings Read(WireReader r) => new()
        {
            name = r.String(LobbyLimits.RoomName), mode = (RoomMode)r.U8(), packs = LobbyWire.Strings(r, LobbyLimits.RoomPacks, LobbyLimits.PackId),
            anticheat = r.Bool(), engineClass = r.U8(), laps = r.U8(), items = r.Bool(), itemOdds = LobbyWire.Odds(r),
            aiWhenAlone = r.Bool(), maxPlayers = r.U8(), trackPick = r.U8(), racesPerSet = r.U8(), cupId = r.String(LobbyLimits.ContentId),
        };
    }

    /// <summary>A lobby as listed (public instances) and in <see cref="LobbyState"/>.</summary>
    public sealed class LobbySummary
    {
        // u64 u8 u8 u16, empty name, u8 bool, empty packs, u8 u8 u8 i32.
        internal const int MinBytes = 8 + 1 + 1 + 2 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 4;

        public long lobbyId;
        /// <summary>False: a private room.</summary>
        public bool isPublic;
        /// <summary>Public category (1-10), 0 for private rooms.</summary>
        public int category;
        /// <summary>Number within the category (#1, #2, ...).</summary>
        public int instance;
        public string name = "";
        public RoomMode mode;
        public bool anticheat;
        public List<string> packs = new();
        public int players;
        public int maxPlayers;
        public LobbyPhase phase;
        public int simVersion;

        public void Write(WireWriter w)
        {
            w.I64(lobbyId);
            w.U8(isPublic ? (byte)0 : (byte)1);
            TimeTrialWire.U8(w, category);
            w.U16((ushort)instance);
            w.String(name, LobbyLimits.LobbyName);
            w.U8((byte)mode);
            w.Bool(anticheat);
            LobbyWire.Strings(w, packs, LobbyLimits.RoomPacks, LobbyLimits.PackId);
            TimeTrialWire.U8(w, players);
            TimeTrialWire.U8(w, maxPlayers);
            w.U8((byte)phase);
            w.I32(simVersion);
        }

        public static LobbySummary Read(WireReader r) => new()
        {
            lobbyId = r.I64(), isPublic = r.U8() == 0, category = r.U8(), instance = r.U16(), name = r.String(LobbyLimits.LobbyName),
            mode = (RoomMode)r.U8(), anticheat = r.Bool(), packs = LobbyWire.Strings(r, LobbyLimits.RoomPacks, LobbyLimits.PackId),
            players = r.U8(), maxPlayers = r.U8(), phase = (LobbyPhase)r.U8(), simVersion = r.I32(),
        };

        public static List<LobbySummary> ReadList(WireReader r)
        {
            var count = r.ListCount(LobbyLimits.Lobbies, MinBytes);
            var list = new List<LobbySummary>(count);
            for (var i = 0; i < count; i++) list.Add(Read(r));
            return list;
        }
    }

    /// <summary>A player in a lobby.</summary>
    public sealed class LobbyMember
    {
        // u64, 4 empty strings, 5 bools, u16 i32 i64.
        internal const int MinBytes = 8 + 4 + 5 + 2 + 4 + 8;

        public long accountId;
        public string username = "";
        public string country = "";
        /// <summary>"" = not chosen.</summary>
        public string characterId = "";
        public string kartId = "";
        /// <summary>Private rooms.</summary>
        public bool ready;
        public bool owner;
        public bool moderator;
        /// <summary>Racing in the current race (false while a race runs: waiting for the next one).</summary>
        public bool inRace;
        /// <summary>0 = unknown.</summary>
        public int pingMs;
        public int setPoints;
        /// <summary>Unix milliseconds.</summary>
        public long joined;

        public bool HasLoadout => !string.IsNullOrEmpty(characterId) && !string.IsNullOrEmpty(kartId);

        public void Write(WireWriter w)
        {
            w.I64(accountId);
            w.String(username, LobbyLimits.Username);
            w.String(country, LobbyLimits.Country);
            w.String(characterId, LobbyLimits.ContentId);
            w.String(kartId, LobbyLimits.ContentId);
            w.Bool(ready);
            w.Bool(owner);
            w.Bool(moderator);
            w.Bool(inRace);
            w.U16((ushort)pingMs);
            w.I32(setPoints);
            w.I64(joined);
        }

        public static LobbyMember Read(WireReader r) => new()
        {
            accountId = r.I64(), username = r.String(LobbyLimits.Username), country = r.String(LobbyLimits.Country),
            characterId = r.String(LobbyLimits.ContentId), kartId = r.String(LobbyLimits.ContentId), ready = r.Bool(), owner = r.Bool(),
            moderator = r.Bool(), inRace = r.Bool(), pingMs = r.U16(), setPoints = r.I32(), joined = r.I64(),
        };
    }

    /// <summary>One racer's line of a race result.</summary>
    public sealed class RaceResultRow
    {
        // u8 u64, empty name, u8 i32 i32 i32 u8 bool.
        internal const int MinBytes = 1 + 8 + 1 + 1 + 4 + 4 + 4 + 1 + 1;

        public int slot;
        public long accountId;
        public string username = "";
        /// <summary>1-based.</summary>
        public int position;
        /// <summary>0 = DNF.</summary>
        public int finishMs;
        public int points;
        public int setPoints;
        public RaceOutcome outcome;
        public bool ai;

        public void Write(WireWriter w)
        {
            TimeTrialWire.U8(w, slot);
            w.I64(accountId);
            w.String(username, LobbyLimits.Username);
            TimeTrialWire.U8(w, position);
            w.I32(finishMs);
            w.I32(points);
            w.I32(setPoints);
            w.U8((byte)outcome);
            w.Bool(ai);
        }

        public static RaceResultRow Read(WireReader r) => new()
        {
            slot = r.U8(), accountId = r.I64(), username = r.String(LobbyLimits.Username), position = r.U8(), finishMs = r.I32(),
            points = r.I32(), setPoints = r.I32(), outcome = (RaceOutcome)r.U8(), ai = r.Bool(),
        };
    }

    /// <summary>A member's track vote ("" = Random).</summary>
    public readonly struct LobbyVote
    {
        public readonly long accountId;
        public readonly string trackId;

        public LobbyVote(long accountId, string trackId)
        {
            this.accountId = accountId;
            this.trackId = trackId ?? "";
        }
    }

    /// <summary>Everything about the lobby the player is in (push 256 and the answer of joins).</summary>
    public sealed class LobbyState
    {
        public LobbySummary summary = new();
        /// <summary>Private rooms (members only), else "".</summary>
        public string code = "";
        /// <summary>Effective settings (public lobbies too).</summary>
        public RoomSettings settings = new();
        public List<LobbyMember> members = new();
        /// <summary>Server clock Unix milliseconds the phase ends at, 0 = no timer.</summary>
        public long phaseEnds;
        /// <summary>1-based race number in the set, 0 between sets.</summary>
        public int setRace;
        public int setSize;
        public List<LobbyVote> votes = new();
        /// <summary>Drawn or current track, "" before the draw.</summary>
        public string trackId = "";
        /// <summary>Current race, 0 = none.</summary>
        public long raceId;
        /// <summary>Of the previous race, for the results phases.</summary>
        public List<RaceResultRow> lastResults = new();

        public LobbyPhase Phase => summary.phase;
        public bool IsPrivate => !summary.isPublic;

        public LobbyMember Member(long accountId) => members.Find(m => m.accountId == accountId);

        /// <summary>The vote of <paramref name="accountId"/>, null when it did not vote ("" = Random).</summary>
        public string VoteOf(long accountId)
        {
            foreach (var v in votes)
                if (v.accountId == accountId) return v.trackId;
            return null;
        }

        public void Write(WireWriter w)
        {
            summary.Write(w);
            w.String(code, LobbyLimits.Code);
            settings.Write(w);
            w.ListCount(members.Count, LobbyLimits.Members);
            foreach (var m in members) m.Write(w);
            w.I64(phaseEnds);
            TimeTrialWire.U8(w, setRace);
            TimeTrialWire.U8(w, setSize);
            w.ListCount(votes.Count, LobbyLimits.Members);
            foreach (var v in votes)
            {
                w.I64(v.accountId);
                w.String(v.trackId, LobbyLimits.ContentId);
            }
            w.String(trackId, LobbyLimits.ContentId);
            w.I64(raceId);
            LobbyWire.Rows(w, lastResults, LobbyLimits.Members);
        }

        public static LobbyState Read(WireReader r)
        {
            var s = new LobbyState { summary = LobbySummary.Read(r), code = r.String(LobbyLimits.Code), settings = RoomSettings.Read(r) };
            var count = r.ListCount(LobbyLimits.Members, LobbyMember.MinBytes);
            for (var i = 0; i < count; i++) s.members.Add(LobbyMember.Read(r));
            s.phaseEnds = r.I64();
            s.setRace = r.U8();
            s.setSize = r.U8();
            count = r.ListCount(LobbyLimits.Members, 8 + 1);
            for (var i = 0; i < count; i++) s.votes.Add(new LobbyVote(r.I64(), r.String(LobbyLimits.ContentId)));
            s.trackId = r.String(LobbyLimits.ContentId);
            s.raceId = r.I64();
            s.lastResults = LobbyWire.Rows(r, LobbyLimits.Members);
            return s;
        }
    }

    /// <summary>Push 257: the player is no longer in the lobby.</summary>
    public sealed class LobbyLeftPush
    {
        public LobbyLeftReason reason;
        public string message = "";

        public static LobbyLeftPush Read(WireReader r) => new() { reason = (LobbyLeftReason)r.U8(), message = r.String(LobbyLimits.Message) };
    }

    /// <summary>Push 523: the result of a race (every racer, AI rows of solo races excluded) and whether the set is over.</summary>
    public sealed class RaceResults
    {
        public long raceId;
        public List<RaceResultRow> rows = new();
        public bool setDone;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            LobbyWire.Rows(w, rows, LobbyLimits.RaceRows);
            w.Bool(setDone);
        }

        public static RaceResults Read(WireReader r) => new() { raceId = r.I64(), rows = LobbyWire.Rows(r, LobbyLimits.RaceRows), setDone = r.Bool() };
    }
}
