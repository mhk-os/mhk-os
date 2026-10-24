using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Gaps in the road along a track line: stretches where the ground under the centre line is a fall zone or
    /// missing (jumps). Sampled once per line and metre on first use.
    /// </summary>
    public static partial class TrackGaps
    {
        /// <summary>Sample spacing (m) along a line.</summary>
        public const float Step = 1f;
        const float ProbeAbove = 3f, ProbeDepth = 20f;

        [AutoStaticsCleanup] static readonly Dictionary<TrackPath, bool[]> Cache = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;

        /// <summary>True when the ground under <paramref name="line"/> at <paramref name="distance"/> is a gap.</summary>
        public static bool IsGap(TrackRuntime track, TrackPath line, float distance)
        {
            if (track == null || line == null || line.Count < 2) return false;
            var samples = Samples(track, line);
            var d = line.closed ? line.WrapDistance(distance) : Mathf.Clamp(distance, 0f, line.Length);
            return samples[Mathf.Clamp(Mathf.FloorToInt(d / Step), 0, samples.Length - 1)];
        }

        static bool[] Samples(TrackRuntime track, TrackPath line)
        {
            if (cacheTrack != track)
            {
                Cache.Clear();
                cacheTrack = track;
            }
            if (Cache.TryGetValue(line, out var samples)) return samples;
            samples = new bool[Mathf.Max(1, Mathf.CeilToInt(line.Length / Step) + 1)];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = IsGapAt(track, line.GetPoint(Mathf.Min(i * Step, line.Length)));
            Cache[line] = samples;
            return samples;
        }

        static bool IsGapAt(TrackRuntime track, Vector3 point)
        {
            if (!GroundProbe.Down(point, ProbeAbove, ProbeDepth, track.groundMask, out var hit)) return true;
            var surface = TrackSurface.Of(hit.collider);
            return surface != null && surface.kind.IsFall();
        }
    }
}
