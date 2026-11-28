using System;
using UnityEngine;

namespace MHK.Cosmetics
{
    /// <summary>
    /// A cosmetic and its colour. Racers carry the ones they wear (<see cref="Core.RacerSetup.cosmetics"/>), the online
    /// race setup sends them to every racer, and the settings remember the colour the player picked for each cosmetic.
    /// Field names are JSON keys, never rename them.
    /// </summary>
    [Serializable]
    public struct CosmeticChoice
    {
        /// <summary>Id of the <see cref="Modding.CosmeticDefinition"/>.</summary>
        public string id;
        public Color32 color;

        public CosmeticChoice(string id, Color32 color)
        {
            this.id = id;
            this.color = color;
        }
    }
}
