using System;
using System.Collections.Generic;

namespace MHK.Achievements
{
    /// <summary>
    /// Stat values and unlocks: this device's totals, the progress not sent to an account yet, or the logged-in
    /// account's totals. Set items compare without case, like content ids.
    /// </summary>
    sealed class StatBook
    {
        public readonly Dictionary<string, long> counters = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> maxima = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> sets = new(StringComparer.Ordinal);
        /// <summary>Achievement id → Unix milliseconds of the unlock.</summary>
        public readonly Dictionary<string, long> unlocked = new(StringComparer.Ordinal);

        public bool IsEmpty => counters.Count == 0 && maxima.Count == 0 && sets.Count == 0 && unlocked.Count == 0;

        public long Counter(string stat) => counters.TryGetValue(stat, out var v) ? v : 0;

        public long Maximum(string stat) => maxima.TryGetValue(stat, out var v) ? v : 0;

        public bool Has(string stat, string item) => sets.TryGetValue(stat, out var s) && s.Contains(item);

        public void Add(string stat, long delta) => counters[stat] = Counter(stat) + delta;

        /// <summary>True when <paramref name="value"/> raised the maximum.</summary>
        public bool Raise(string stat, long value)
        {
            if (value <= Maximum(stat)) return false;
            maxima[stat] = value;
            return true;
        }

        /// <summary>True when the item is new to the set.</summary>
        public bool Collect(string stat, string item)
        {
            if (!sets.TryGetValue(stat, out var set)) sets[stat] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return set.Add(item);
        }

        /// <summary>Unlocks <paramref name="id"/> at <paramref name="at"/> (Unix ms), keeping an earlier time. True when it changed.</summary>
        public bool Unlock(string id, long at)
        {
            if (unlocked.TryGetValue(id, out var before) && before <= at) return false;
            unlocked[id] = at;
            return true;
        }

        public StatBookData ToData()
        {
            var data = new StatBookData();
            foreach (var (stat, value) in counters) data.counters.Add(new StatEntry { stat = stat, value = value });
            foreach (var (stat, value) in maxima) data.maxima.Add(new StatEntry { stat = stat, value = value });
            foreach (var (stat, items) in sets) data.sets.Add(new SetEntry { stat = stat, items = new List<string>(items) });
            foreach (var (id, at) in unlocked) data.unlocked.Add(new UnlockEntry { id = id, at = at });
            return data;
        }

        /// <summary>The book stored in <paramref name="data"/>; entries with invalid ids or values are left out.</summary>
        public static StatBook From(StatBookData data)
        {
            var book = new StatBook();
            if (data == null) return book;
            foreach (var e in data.counters)
                if (e != null && IsStat(e.stat) && e.value > 0) book.Add(e.stat, e.value);
            foreach (var e in data.maxima)
                if (e != null && IsStat(e.stat)) book.Raise(e.stat, e.value);
            foreach (var e in data.sets)
            {
                if (e?.items == null || !IsStat(e.stat)) continue;
                foreach (var item in e.items)
                    if (AchievementStats.IsValidId(item, AchievementStats.MaxItemLength)) book.Collect(e.stat, item);
            }
            foreach (var e in data.unlocked)
                if (e != null && AchievementStats.IsValidId(e.id, AchievementStats.MaxItemLength) && e.at > 0) book.Unlock(e.id, e.at);
            return book;
        }

        static bool IsStat(string stat) => AchievementStats.IsValidId(stat, AchievementStats.MaxStatLength);
    }

    /// <summary>One counter or maximum in achievements.json. Field names are JSON keys: never rename them.</summary>
    [Serializable]
    sealed class StatEntry
    {
        public string stat;
        public long value;
    }

    [Serializable]
    sealed class SetEntry
    {
        public string stat;
        public List<string> items = new();
    }

    [Serializable]
    sealed class UnlockEntry
    {
        public string id;
        /// <summary>Unix milliseconds.</summary>
        public long at;
    }

    [Serializable]
    sealed class StatBookData
    {
        public List<StatEntry> counters = new();
        public List<StatEntry> maxima = new();
        public List<SetEntry> sets = new();
        public List<UnlockEntry> unlocked = new();
    }

    /// <summary>The packs whose parts an achievement has had: it stays hidden while any of them is missing.</summary>
    [Serializable]
    sealed class PartPacks
    {
        public string achievement;
        public List<string> packs = new();
    }

    /// <summary>achievements.json: this device's totals, the progress no account has received yet, and the part packs seen.</summary>
    [Serializable]
    sealed class AchievementFile
    {
        public StatBookData local = new();
        public StatBookData pending = new();
        public List<PartPacks> partPacks = new();
    }
}
