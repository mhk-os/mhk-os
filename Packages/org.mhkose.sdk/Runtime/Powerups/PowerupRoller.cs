using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>Weighted random powerup draws for item boxes, from the race's item pool (<see cref="ItemOdds"/>).</summary>
    public static partial class PowerupRoller
    {
        [AutoStaticsCleanup] static readonly List<ItemPoolEntry> pool = new();
        [AutoStaticsCleanup] static readonly List<PowerupDefinition> poolPowerups = new();
        [AutoStaticsCleanup] static GameSession poolSession;
        [AutoStaticsCleanup] static int poolVersion;

        /// <summary>
        /// Session of the running race (a menu background race has its own), else <see cref="GameRoot.Session"/>.
        /// </summary>
        public static GameSession RaceSession
        {
            get
            {
                var race = Race.RaceManager.Current;
                return race != null && race.Session != null ? race.Session : GameRoot.Instance.Session;
            }
        }

        /// <summary>Item boxes give powerups in the running race (off in time trials and when every odd is 0 %).</summary>
        public static bool ItemsEnabled
        {
            get
            {
                var session = RaceSession;
                return session == null || session.powerupsEnabled;
            }
        }

        /// <summary>Powerups the random item boxes of the current race can give (the HUD roulette cycles them).</summary>
        public static IReadOnlyList<PowerupDefinition> Pool
        {
            get
            {
                Prepare();
                return poolPowerups;
            }
        }

        /// <summary>
        /// Weighted random powerup for a kart from the race's item pool, using its race position as 0 (leader) to 1
        /// (last). Null when item boxes give nothing.
        /// </summary>
        public static PowerupDefinition Roll(Kart kart)
        {
            var session = RaceSession;
            if (session != null && !session.powerupsEnabled) return null;
            var racers = Mathf.Max(1, Kart.All.Count - 1);
            // Battles have no meaningful race position: use the mid-field weights (Thunder Last Man Standing: equal table).
            var battle = session != null && session.mode == GameMode.Battle;
            var fraction = Kart.All.Count <= 1 || battle ? 0.5f : (kart.Progress.Position - 1) / (float)racers;
            Prepare();
            var total = TotalWeight(pool, fraction);
            return total > 0f ? Select(pool, fraction, Random.value * total) : null;
        }

        /// <summary>Sum of the pick weights of <paramref name="entries"/> at race position <paramref name="fraction"/>.</summary>
        public static float TotalWeight(IReadOnlyList<ItemPoolEntry> entries, float fraction)
        {
            var total = 0f;
            for (var i = 0; i < entries.Count; i++) total += Mathf.Max(0f, entries[i].WeightAt(fraction));
            return total;
        }

        /// <summary>The powerup at <paramref name="point"/> (0 to <see cref="TotalWeight"/>) along the weights of <paramref name="entries"/>.</summary>
        public static PowerupDefinition Select(IReadOnlyList<ItemPoolEntry> entries, float fraction, float point)
        {
            PowerupDefinition last = null;
            for (var i = 0; i < entries.Count; i++)
            {
                var w = entries[i].WeightAt(fraction);
                if (w <= 0f) continue;
                last = entries[i].powerup;
                point -= w;
                if (point <= 0f) return last;
            }
            return last;
        }

        // Rebuilt when the session or the loaded content changes.
        static void Prepare()
        {
            var root = GameRoot.Instance;
            var session = RaceSession;
            if (session == poolSession && root.Content.Version == poolVersion && poolVersion != 0) return;
            poolSession = session;
            poolVersion = root.Content.Version;
            ItemOdds.BuildPool(root.Content, session, pool);
            poolPowerups.Clear();
            for (var i = 0; i < pool.Count; i++) poolPowerups.Add(pool[i].powerup);
        }
    }
}
