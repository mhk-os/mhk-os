using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Something lying on the track that harms karts driving through it (planted traps, black holes, fields).
    /// Computer drivers steer around the ones they notice (<see cref="PlantedHazards.All"/>).
    /// </summary>
    public interface IPlantedHazard
    {
        Vector3 Position { get; }
        /// <summary>Distance (m) from <see cref="Position"/> a kart's centre must keep to stay unharmed. 0 while harmless (in flight, closing).</summary>
        float DangerRadius { get; }
        /// <summary>Disguised as an item box (fake item box): drivers that do not see through it drive into it on purpose.</summary>
        bool LooksLikeItemBox { get; }
    }

    /// <summary>Registry of the enabled <see cref="IPlantedHazard"/>s.</summary>
    public static partial class PlantedHazards
    {
        /// <summary><see cref="Modding.PowerupDefinition.family"/> of fake item boxes (MHK2 fake powerup).</summary>
        public const string DecoyFamily = "decoy";

        [AutoStaticsCleanup] static readonly List<IPlantedHazard> all = new();
        /// <summary>All planted hazards currently enabled in the scene.</summary>
        public static IReadOnlyList<IPlantedHazard> All => all;

        public static void Register(IPlantedHazard hazard) => all.Add(hazard);
        public static void Unregister(IPlantedHazard hazard) => all.Remove(hazard);
    }
}
