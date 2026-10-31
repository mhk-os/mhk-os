using System;

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
}
