using MHK.Karts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Input
{
    /// <summary>Input injected by on-screen touch controls (Android) per local player.</summary>
    public static partial class VirtualKartInput
    {
        /// <summary>Local players with an input slot.</summary>
        public const int MaxLocalPlayers = 4;

        [AutoStaticsCleanup] static KartInput[] players = new KartInput[MaxLocalPlayers];

        public static ref KartInput ForPlayer(int index) => ref players[Mathf.Clamp(index, 0, players.Length - 1)];
    }
}
