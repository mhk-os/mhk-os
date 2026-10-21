using System.Collections.Generic;
using MHK.Karts;
using MHK.Powerups;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>Read-only questions a computer driver asks about the other karts and the road around it, without allocating.</summary>
    internal static partial class AIKartQueries
    {
        const int LineOfSightMask = Physics.DefaultRaycastLayers;
        const float EyeHeight = 0.8f;
        const float MinLaneWidth = 2f;
        const float AroundHeight = 2.5f, MinAhead = 2f;
        // Narrow: road below this share of the track's median width.
        const float NarrowShare = 0.7f;
        const float DefaultMedianWidth = 10f, WallReachMargin = 0.5f;
        const float StraightMaxSteer = 0.3f, StraightFrom = 2f, StraightTo = 40f, StraightSmoothing = 4f, StraightMaxAngle = 12f;

        [AutoStaticsCleanup] static readonly Dictionary<TrackPath, float> medianWidths = new();
        [AutoStaticsCleanup] static TrackRuntime medianTrack;
        [NoAutoStaticsCleanup] static readonly List<float> widthScratch = new();

        public static TrackPath MainPath() => TrackRuntime.TryGetMainPath(out var path) ? path : null;

        public static bool IsOpponent(Kart kart, Kart o) => o != kart && o.isActiveAndEnabled && !o.Eliminated;

        public static bool IsLeading(Kart kart) => kart.Progress.Position == 1;

        /// <summary>Last of the karts still racing (nobody behind to look for).</summary>
        public static bool IsLast(Kart kart)
        {
            var racing = 0;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
                if (karts[i].isActiveAndEnabled && !karts[i].Eliminated) racing++;
            return racing > 1 && kart.Progress.Position >= racing;
        }

        public static float Distance(Kart a, Kart b) => Vector3.Distance(a.transform.position, b.transform.position);

        public static bool LineOfSight(Kart kart, Kart o) =>
            !ItemPhysics.Linecast(kart.transform.position + Vector3.up * EyeHeight, o.transform.position + Vector3.up * EyeHeight, out _, LineOfSightMask);

        /// <summary>Race distance of <paramref name="kart"/> ahead of the leading human (negative behind), NaN without humans.</summary>
        public static float DistanceToHuman(Kart kart)
        {
            var best = float.NaN;
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var k = all[i];
                if (!k.IsHuman || k == kart) continue;
                var gap = kart.Progress.RaceDistance - k.Progress.RaceDistance;
                if (float.IsNaN(best) || gap < best) best = gap;
            }
            return best;
        }

        /// <summary>Road lane (0 = left edge, 1 = right edge) of <paramref name="o"/> on the main line.</summary>
        public static float RoadLaneOf(Kart o)
        {
            var path = MainPath();
            if (path == null) return AILanes.Centre;
            var d = o.Progress.PathDistance;
            return Mathf.Clamp01(path.GetRoadLane(o.transform.position, d, Mathf.Max(MinLaneWidth, path.GetWidth(d))));
        }

        /// <summary>Nearest opponent behind in the race within <paramref name="reach"/> metres along the track.</summary>
        public static Kart ClosestBehind(Kart kart, float reach)
        {
            var path = MainPath();
            Kart best = null;
            var bestGap = reach;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!IsOpponent(kart, o) || o.Progress.Position <= kart.Progress.Position) continue;
                var gap = path != null
                    ? path.Delta(o.Progress.PathDistance, kart.Progress.PathDistance)
                    : -kart.transform.InverseTransformPoint(o.transform.position).z;
                if (gap < 0f || gap > bestGap) continue;
                best = o;
                bestGap = gap;
            }
            return best;
        }

        /// <summary>Unprotected opponents within <paramref name="radius"/> on the same level.</summary>
        public static int CountAround(Kart kart, float radius)
        {
            var count = 0;
            var p = kart.transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!IsOpponent(kart, o) || o.Effects.IsProtected) continue;
                var d = o.transform.position - p;
                if (Mathf.Abs(d.y) > AroundHeight) continue;
                d.y = 0f;
                if (d.sqrMagnitude < radius * radius) count++;
            }
            return count;
        }

        /// <summary>Unprotected opponents ahead within <paramref name="range"/> and <paramref name="halfWidth"/> of the kart's line.</summary>
        public static int CountAhead(Kart kart, float range, float halfWidth)
        {
            var count = 0;
            var tf = kart.transform;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!IsOpponent(kart, o) || o.Effects.IsProtected) continue;
                var l = tf.InverseTransformPoint(o.transform.position);
                if (l.z > MinAhead && l.z < range && Mathf.Abs(l.x) < halfWidth) count++;
            }
            return count;
        }

        /// <summary>
        /// The kart a selection-based item (<see cref="PowerupTargeting.Select"/>) goes for first. Unlike the item it does
        /// not skip ghosted karts. <paramref name="maxDistance"/> 0 = unlimited.
        /// </summary>
        public static Kart FirstTarget(Kart kart, TargetSelection selection, float maxDistance)
        {
            Kart best = null;
            var bestScore = float.MaxValue;
            var myPosition = kart.Progress.Position;
            var p = kart.transform.position;
            var forward = kart.transform.forward;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!IsOpponent(kart, o)) continue;
                var to = o.transform.position - p;
                var distance = to.magnitude;
                if (maxDistance > 0f && distance > maxDistance) continue;
                if (!TryScore(selection, o.Progress.Position, myPosition, forward, to, distance, out var score) || !(score < bestScore)) continue;
                best = o;
                bestScore = score;
            }
            return best;
        }

        /// <summary>Unprotected karts a multi-target item would hit.</summary>
        public static int CountTargets(Kart kart, TargetSelection selection, float maxDistance)
        {
            var count = 0;
            var myPosition = kart.Progress.Position;
            var p = kart.transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!IsOpponent(kart, o) || o.Effects.IsProtected) continue;
                if (maxDistance > 0f && (o.transform.position - p).sqrMagnitude > maxDistance * maxDistance) continue;
                if (selection == TargetSelection.AllAhead && o.Progress.Position >= myPosition) continue;
                count++;
            }
            return count;
        }

        public static bool LeaderProtected(Kart kart)
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (o != kart && o.isActiveAndEnabled && !o.Eliminated && o.Progress.Position == 1) return o.Effects.IsProtected;
            }
            return false;
        }

        /// <summary>Grounded with the wheel nearly straight and the main line straight for the next 40 m.</summary>
        public static bool OnStraight(Kart kart, float steer)
        {
            if (!kart.Physics.IsGrounded || Mathf.Abs(steer) > StraightMaxSteer) return false;
            var path = MainPath();
            if (path == null) return true;
            var d = kart.Progress.PathDistance;
            var a = path.GetTangent(d + StraightFrom, StraightSmoothing);
            var b = path.GetTangent(d + StraightTo, StraightSmoothing);
            return Vector3.Angle(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z)) < StraightMaxAngle;
        }

        /// <summary>
        /// The main line is narrow at <paramref name="distance"/> (only checked off routes), or walls on both sides of the
        /// kart are closer together than a narrow road.
        /// </summary>
        public static bool NarrowRoad(Kart kart, TrackPath route, float distance)
        {
            var path = MainPath();
            if (path != null && route == null && kart.Progress.CurrentBranch == null
                && path.GetWidth(distance) <= NarrowShare * MedianWidth(path))
                return true;
            return WallsClose(kart, path);
        }

        static bool TryScore(TargetSelection selection, int position, int myPosition, Vector3 forward, Vector3 to, float distance, out float score)
        {
            score = distance;
            switch (selection)
            {
                case TargetSelection.NextAhead:
                    score = -position;
                    return position < myPosition;
                case TargetSelection.Leader:
                    score = position;
                    return true;
                case TargetSelection.AllAhead:
                    return position < myPosition;
                case TargetSelection.NearestInFront:
                    return !(Vector3.Angle(forward, to) >= PowerupTargeting.FrontConeHalfAngle || distance >= PowerupTargeting.DefaultMaxDistance);
                default:
                    return true;
            }
        }

        static bool WallsClose(Kart kart, TrackPath path)
        {
            var median = path != null ? MedianWidth(path) : DefaultMedianWidth;
            var reach = NarrowShare * median * 0.5f + WallReachMargin;
            var tf = kart.transform;
            var right = tf.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-4f) return false;
            right.Normalize();
            var origin = tf.position + Vector3.up * EyeHeight;
            return ItemPhysics.Raycast(origin, right, reach, out _, LineOfSightMask)
                && ItemPhysics.Raycast(origin, -right, reach, out _, LineOfSightMask);
        }

        // Median node width of a line, cached per line of the current track.
        static float MedianWidth(TrackPath path)
        {
            if (medianTrack != TrackRuntime.Current)
            {
                medianWidths.Clear();
                medianTrack = TrackRuntime.Current;
            }
            if (medianWidths.TryGetValue(path, out var w)) return w;
            widthScratch.Clear();
            for (var i = 0; i < path.Count; i++) widthScratch.Add(path.GetNodeWidth(i));
            widthScratch.Sort();
            w = widthScratch.Count > 0 ? widthScratch[widthScratch.Count / 2] : DefaultMedianWidth;
            medianWidths[path] = w;
            return w;
        }
    }
}
