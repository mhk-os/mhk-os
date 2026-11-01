using System.Collections.Generic;
using MHK.Powerups;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;

namespace MHK.AI
{
    /// <summary>
    /// Which item boxes of the current track a computer driver in a battle can drive to: those with ground under them,
    /// cached per track. Battles have only boxes that give powerups (<see cref="ItemBox.FixedPickup"/> boxes are removed).
    /// </summary>
    internal static partial class AIBattleBoxes
    {
        const float GroundAbove = 0.5f, GroundBelow = 3f;

        [AutoStaticsCleanup] static readonly Dictionary<ItemBox, bool> reachable = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;

        /// <summary><paramref name="box"/> has ground under it (not outside the drivable area).</summary>
        public static bool Reachable(ItemBox box)
        {
            if (cacheTrack != TrackRuntime.Current)
            {
                reachable.Clear();
                cacheTrack = TrackRuntime.Current;
            }
            if (reachable.TryGetValue(box, out var r)) return r;
            var track = TrackRuntime.Current;
            r = track == null || GroundProbe.Down(box.transform.position, GroundAbove, GroundBelow, track.ObstacleMask, out _);
            reachable[box] = r;
            return r;
        }
    }
}
