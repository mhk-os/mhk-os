using System.Collections.Generic;
using MHK.Powerups;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Item box clusters along the main track line (boxes projected onto it), cached per track, for the computer
    /// drivers' rule to use an item before they reach the next cluster.
    /// </summary>
    internal static partial class AIItemBoxes
    {
        // A cluster starting closer than this ahead is the one the kart is collecting from (box radius plus margin).
        const float PickupSlack = 4f;

        [AutoStaticsCleanup] static readonly List<float> starts = new();
        [AutoStaticsCleanup] static readonly List<float> sorted = new();
        [AutoStaticsCleanup] static TrackPath cachePath;
        [AutoStaticsCleanup] static int cacheBoxes = -1;
        [AutoStaticsCleanup] static float cacheGap;

        /// <summary>
        /// Metres along <paramref name="path"/> from <paramref name="distance"/> to the start of the next item box cluster
        /// ahead, not counting the one the kart is in. Infinity when the track has no item boxes.
        /// </summary>
        public static float ToNextCluster(TrackPath path, float distance, float clusterGap)
        {
            Refresh(path, clusterGap);
            var best = float.PositiveInfinity;
            var length = path.Length;
            for (var i = 0; i < starts.Count; i++)
            {
                var ahead = Mathf.Repeat(starts[i] - distance, length);
                if (ahead > PickupSlack && ahead < best) best = ahead;
            }
            return best;
        }

        static void Refresh(TrackPath path, float clusterGap)
        {
            var boxes = ItemBox.Active;
            if (path == cachePath && boxes.Count == cacheBoxes && clusterGap == cacheGap) return;
            cachePath = path;
            cacheBoxes = boxes.Count;
            cacheGap = clusterGap;
            sorted.Clear();
            for (var i = 0; i < boxes.Count; i++)
                if (boxes[i].GivesPowerups()) sorted.Add(path.Project(boxes[i].transform.position));
            sorted.Sort();
            starts.Clear();
            for (var i = 0; i < sorted.Count; i++)
                if (i == 0 || sorted[i] - sorted[i - 1] > clusterGap) starts.Add(sorted[i]);
            // On a loop the last cluster may run on over the finish line into the first.
            if (path.closed && starts.Count > 1 && sorted[0] + path.Length - sorted[^1] <= clusterGap) starts.RemoveAt(0);
        }
    }
}
