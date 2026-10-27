namespace MHK.Achievements
{
    /// <summary>
    /// The player's stats that achievements measure, and how game code and mods record them. Counters add up,
    /// maxima keep the largest value, sets collect item ids. Ids are 1-64 characters of A-Z, a-z, 0-9, '_', '.', '-'
    /// (set items and achievement ids up to 96); other ids are ignored with a warning. Recorded on this device and,
    /// once an account is logged in, synced to it (progress made while logged out goes to the next account that logs in).
    /// </summary>
    public static class AchievementStats
    {
        /// <summary>Counter: races finished (single races, championships, online; not time trials or battles).</summary>
        public const string RacesFinished = "races.finished";
        /// <summary>Counter: races finished in the top 3 (at least 4 racers).</summary>
        public const string RacesTop3 = "races.top3";
        /// <summary>Counter: races won (at least 2 racers).</summary>
        public const string RacesWon = "races.won";
        /// <summary>Counter: races and time trials finished in under 2 minutes.</summary>
        public const string RacesUnder2Min = "races.under2min";
        /// <summary>Counter: metres the local players drove in races, battles and time trials (not replays).</summary>
        public const string DistanceMetres = "distance.m";
        /// <summary>Counter: a local player's kart ran into a hazard it planted itself (once per use).</summary>
        public const string OwnTraps = "traps.own";
        /// <summary>Counter: championships with every race won.</summary>
        public const string PerfectChampionships = "championships.perfect";
        /// <summary>Counter: battles won (at least 2 karts).</summary>
        public const string BattlesWon = "battles.won";
        /// <summary>Counter: online races won against other players.</summary>
        public const string OnlineRacesWon = "races.online.won";
        /// <summary>Counter: time-trial runs uploaded.</summary>
        public const string TimeTrialsUploaded = "timetrials.uploaded";
        /// <summary>Set: ids of the tracks and arenas where a local player finished a race, time trial or battle.</summary>
        public const string TracksFinished = "tracks.finished";
        /// <summary>Set: ids of the championships won with every race won.</summary>
        public const string PerfectCups = "cups.perfect";

        /// <summary>Longest stat id.</summary>
        internal const int MaxStatLength = 64;
        /// <summary>Longest set item and achievement id.</summary>
        internal const int MaxItemLength = 96;

        /// <summary>Maximum: the most karts a single use of powerup <paramref name="powerupId"/> hit.</summary>
        public static string PowerupHits(string powerupId) => "use." + powerupId + ".hits";

        /// <summary>Adds <paramref name="delta"/> (positive) to the counter <paramref name="stat"/>.</summary>
        public static void Add(string stat, long delta = 1)
        {
            if (delta > 0 && Valid(stat, MaxStatLength)) Achievements.AddCounter(stat, delta);
        }

        /// <summary>Raises the maximum <paramref name="stat"/> to <paramref name="value"/> when it is larger.</summary>
        public static void Max(string stat, long value)
        {
            if (value > 0 && Valid(stat, MaxStatLength)) Achievements.RaiseMaximum(stat, value);
        }

        /// <summary>Adds <paramref name="item"/> (a content id) to the set <paramref name="stat"/>.</summary>
        public static void Collect(string stat, string item)
        {
            if (Valid(stat, MaxStatLength) && Valid(item, MaxItemLength)) Achievements.CollectItem(stat, item);
        }

        /// <summary>Whether <paramref name="id"/> is a valid stat, item or achievement id of at most <paramref name="max"/> characters.</summary>
        internal static bool IsValidId(string id, int max)
        {
            if (string.IsNullOrEmpty(id) || id.Length > max) return false;
            foreach (var c in id)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-')) return false;
            return true;
        }

        static bool Valid(string id, int max)
        {
            if (IsValidId(id, max)) return true;
            UnityEngine.Debug.LogWarning($"[Achievements] Ignored the invalid stat or item id '{id}' (1-{max} characters of A-Z, a-z, 0-9, '_', '.', '-').");
            return false;
        }
    }
}
