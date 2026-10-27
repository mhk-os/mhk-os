using System;
using System.Collections.Generic;

namespace MHK.Online
{
    /// <summary>
    /// Request 27 (SyncAchievements): progress to merge into the logged-in account. Counters carry deltas, maxima and
    /// set items merge by maximum and union, unlocks keep the earliest time. Empty = only fetch the totals.
    /// </summary>
    public sealed class SyncAchievementsRequest
    {
        public const int MaxStat = 64, MaxId = 96, MaxItem = 96;
        /// <summary>Counters, and maxima, of one request.</summary>
        public const int MaxStats = 256;
        public const int MaxSets = 64, MaxSetItems = 2000, MaxUnlocked = 2000;

        public readonly List<(string stat, uint delta)> counters = new();
        public readonly List<(string stat, uint value)> maxima = new();
        /// <summary>Items to add to each set.</summary>
        public readonly List<(string stat, List<string> items)> sets = new();
        public readonly List<(string id, DateTimeOffset at)> unlocked = new();

        public bool IsEmpty => counters.Count == 0 && maxima.Count == 0 && sets.Count == 0 && unlocked.Count == 0;

        public void Write(WireWriter w)
        {
            AchievementWire.WriteValues(w, counters, MaxStats);
            AchievementWire.WriteValues(w, maxima, MaxStats);
            AchievementWire.WriteSets(w, sets, MaxSets, MaxSetItems);
            AchievementWire.WriteUnlocks(w, unlocked, MaxUnlocked);
        }

        public static SyncAchievementsRequest Read(WireReader r)
        {
            var m = new SyncAchievementsRequest();
            AchievementWire.ReadValues(r, m.counters, MaxStats);
            AchievementWire.ReadValues(r, m.maxima, MaxStats);
            AchievementWire.ReadSets(r, m.sets, MaxSets, MaxSetItems);
            AchievementWire.ReadUnlocks(r, m.unlocked, MaxUnlocked);
            return m;
        }
    }

    /// <summary>Response to SyncAchievements: the account's totals after the merge (whole sets, every unlock).</summary>
    public sealed class AchievementTotals
    {
        /// <summary>Counters, maxima and set stats an account keeps together.</summary>
        public const int MaxAccountStats = 500;
        /// <summary>Set items an account keeps over all its sets.</summary>
        public const int MaxAccountSetItems = 5000;
        public const int MaxAccountUnlocked = 2000;

        public readonly List<(string stat, ulong value)> counters = new();
        public readonly List<(string stat, uint value)> maxima = new();
        public readonly List<(string stat, List<string> items)> sets = new();
        public readonly List<(string id, DateTimeOffset at)> unlocked = new();

        public void Write(WireWriter w)
        {
            w.ListCount(counters.Count, MaxAccountStats);
            foreach (var (stat, value) in counters)
            {
                w.String(stat, SyncAchievementsRequest.MaxStat);
                w.U64(value);
            }
            AchievementWire.WriteValues(w, maxima, MaxAccountStats);
            AchievementWire.WriteSets(w, sets, MaxAccountStats, MaxAccountSetItems);
            AchievementWire.WriteUnlocks(w, unlocked, MaxAccountUnlocked);
        }

        public static AchievementTotals Read(WireReader r)
        {
            var m = new AchievementTotals();
            // String, u64.
            var count = r.ListCount(MaxAccountStats, 1 + 8);
            for (var i = 0; i < count; i++) m.counters.Add((r.String(SyncAchievementsRequest.MaxStat), r.U64()));
            AchievementWire.ReadValues(r, m.maxima, MaxAccountStats);
            AchievementWire.ReadSets(r, m.sets, MaxAccountStats, MaxAccountSetItems);
            AchievementWire.ReadUnlocks(r, m.unlocked, MaxAccountUnlocked);
            return m;
        }
    }

    /// <summary>Request 28 (AchievementShares, no login): how many players unlocked these achievements; empty = every one.</summary>
    public sealed class AchievementSharesRequest
    {
        public const int MaxIds = 2000;

        public readonly List<string> ids = new();

        public void Write(WireWriter w)
        {
            w.ListCount(ids.Count, MaxIds);
            foreach (var id in ids) w.String(id, SyncAchievementsRequest.MaxId);
        }

        public static AchievementSharesRequest Read(WireReader r)
        {
            var m = new AchievementSharesRequest();
            var count = r.ListCount(MaxIds, 1);
            for (var i = 0; i < count; i++) m.ids.Add(r.String(SyncAchievementsRequest.MaxId));
            return m;
        }
    }

    /// <summary>
    /// Response to AchievementShares: accounts that synced any achievement data (<see cref="players"/>) and how many of
    /// them unlocked each achievement (ids nobody unlocked may be left out).
    /// </summary>
    public sealed class AchievementShareCounts
    {
        public uint players;
        public readonly List<(string id, uint unlocked)> shares = new();

        public void Write(WireWriter w)
        {
            w.U32(players);
            w.ListCount(shares.Count, AchievementSharesRequest.MaxIds);
            foreach (var (id, unlocked) in shares)
            {
                w.String(id, SyncAchievementsRequest.MaxId);
                w.U32(unlocked);
            }
        }

        public static AchievementShareCounts Read(WireReader r)
        {
            var m = new AchievementShareCounts { players = r.U32() };
            // String, u32.
            var count = r.ListCount(AchievementSharesRequest.MaxIds, 1 + 4);
            for (var i = 0; i < count; i++) m.shares.Add((r.String(SyncAchievementsRequest.MaxId), r.U32()));
            return m;
        }
    }

    /// <summary>List encodings shared by the achievement messages.</summary>
    static class AchievementWire
    {
        public static void WriteValues(WireWriter w, List<(string stat, uint value)> list, int max)
        {
            w.ListCount(list.Count, max);
            foreach (var (stat, value) in list)
            {
                w.String(stat, SyncAchievementsRequest.MaxStat);
                w.U32(value);
            }
        }

        public static void ReadValues(WireReader r, List<(string stat, uint value)> list, int max)
        {
            // String, u32.
            var count = r.ListCount(max, 1 + 4);
            for (var i = 0; i < count; i++) list.Add((r.String(SyncAchievementsRequest.MaxStat), r.U32()));
        }

        public static void WriteSets(WireWriter w, List<(string stat, List<string> items)> list, int maxSets, int maxItems)
        {
            w.ListCount(list.Count, maxSets);
            foreach (var (stat, items) in list)
            {
                w.String(stat, SyncAchievementsRequest.MaxStat);
                w.ListCount(items.Count, maxItems);
                foreach (var item in items) w.String(item, SyncAchievementsRequest.MaxItem);
            }
        }

        public static void ReadSets(WireReader r, List<(string stat, List<string> items)> list, int maxSets, int maxItems)
        {
            // String, item count.
            var count = r.ListCount(maxSets, 1 + 1);
            for (var i = 0; i < count; i++)
            {
                var stat = r.String(SyncAchievementsRequest.MaxStat);
                var n = r.ListCount(maxItems, 1);
                var items = new List<string>(n);
                for (var j = 0; j < n; j++) items.Add(r.String(SyncAchievementsRequest.MaxItem));
                list.Add((stat, items));
            }
        }

        public static void WriteUnlocks(WireWriter w, List<(string id, DateTimeOffset at)> list, int max)
        {
            w.ListCount(list.Count, max);
            foreach (var (id, at) in list)
            {
                w.String(id, SyncAchievementsRequest.MaxId);
                w.Time(at);
            }
        }

        public static void ReadUnlocks(WireReader r, List<(string id, DateTimeOffset at)> list, int max)
        {
            // String, i64.
            var count = r.ListCount(max, 1 + 8);
            for (var i = 0; i < count; i++) list.Add((r.String(SyncAchievementsRequest.MaxId), r.Time()));
        }
    }
}
