using System;
using System.Collections.Generic;

namespace MHK.Core
{
    /// <summary>Chance of one powerup in item boxes, in percent of all rolls. Field names are JSON keys: never rename them.</summary>
    [Serializable]
    public struct PowerupOdds
    {
        public string id;
        public float percent;

        public PowerupOdds(string id, float percent)
        {
            this.id = id;
            this.percent = percent;
        }
    }

    /// <summary>
    /// The player's own single race item odds for the tracks of one item family (part of <see cref="UserSettings"/>).
    /// Field names are JSON keys: never rename them.
    /// </summary>
    [Serializable]
    public class ItemOddsSet
    {
        /// <summary>Id of the family's top-level pack ("core", "tincan").</summary>
        public string family;
        /// <summary>Percent per powerup id. Ids of packs that are not loaded stay stored and are ignored.</summary>
        public List<PowerupOdds> odds = new();

        /// <summary>Sets the percentage of powerup <paramref name="id"/>, adding it when missing.</summary>
        public void Set(string id, float percent)
        {
            for (var i = 0; i < odds.Count; i++)
            {
                if (odds[i].id != id) continue;
                odds[i] = new PowerupOdds(id, percent);
                return;
            }
            odds.Add(new PowerupOdds(id, percent));
        }
    }
}
