using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Where computer drivers may roam in a battle: drivable ground, offroad included, that is no wall, fall zone, deadly
    /// surface or water, lies above the kill height and outside kill zones, without a drop on the way and within a leash
    /// of the road edges of the racing line or of an alternative route.
    /// </summary>
    internal static partial class AIBattleArea
    {
        const float KillHeightMargin = 3f, LineFloorMargin = 1f, RouteBoundsMargin = 60f;
        // Largest drop (m) between two ground samples a few metres apart: deeper is a ledge or a cliff.
        const float MaxDrop = 2.5f;
        const float MinLineWidth = 2f;

        [AutoStaticsCleanup] static TrackRuntime floorTrack;
        [AutoStaticsCleanup] static float floor;

        /// <summary>
        /// Ground under <paramref name="p"/> nearest to <paramref name="fromY"/> (the previous ground height) is safe to
        /// drive onto. Offroad it must also be flatter than <paramref name="minNormalY"/> (hillsides karts cannot climb).
        /// <paramref name="hints"/> speed up the projections onto the lines between calls.
        /// </summary>
        public static bool Safe(TrackRuntime track, Vector3 p, float fromY, float leash, float minNormalY, Hints hints, out float groundY) =>
            Safe(track, p, fromY, leash, leash, minNormalY, hints, out groundY, out _);

        /// <summary>
        /// <see cref="Safe(TrackRuntime, Vector3, float, float, float, Hints, out float)"/>, where ground of a paved
        /// <see cref="TrackSurface"/> (one that is not offroad, such as a square beside the line) may lie up to
        /// <paramref name="pavedLeash"/> beyond the road edges. Also tells how far (m) the ground lies beyond them
        /// (<see cref="BeyondRoad"/>; +infinity when there is no ground).
        /// </summary>
        public static bool Safe(TrackRuntime track, Vector3 p, float fromY, float leash, float pavedLeash, float minNormalY, Hints hints,
            out float groundY, out float beyond)
        {
            groundY = fromY;
            beyond = float.PositiveInfinity;
            if (!AIRoadProbe.Ground(p, fromY, track.ObstacleMask, out var hit)) return false;
            groundY = hit.point.y;
            if (hit.normal.y < AIObstacleClassifier.SteepNormalY) return false;
            if (groundY < fromY - MaxDrop || groundY < Floor(track)) return false;
            if (track.fallDepth > 0f && groundY < fromY - track.fallDepth) return false;
            var surface = TrackSurface.Of(hit.collider);
            if (surface != null && (surface.kind.IsUnsafeGround() || surface.kind == SurfaceKind.Water)) return false;
            if (InKillZone(hit.point)) return false;
            beyond = BeyondRoad(track, hit.point, hints);
            var reach = surface != null && !surface.offroad ? Mathf.Max(leash, pavedLeash) : leash;
            return beyond <= reach && (beyond <= 0f || hit.normal.y >= minNormalY);
        }

        /// <summary>Lowest ground normal y offroad ground may have in a battle (<see cref="Core.GameConfig.aiBattleMaxSlope"/>).</summary>
        public static float MinNormalY(Core.GameConfig cfg) => Mathf.Cos(cfg.aiBattleMaxSlope * Mathf.Deg2Rad);

        /// <summary>
        /// How far (m) <paramref name="p"/> lies beyond the road edges of the main line or the nearest alternative route
        /// (negative on a road).
        /// </summary>
        public static float BeyondRoad(TrackRuntime track, Vector3 p, Hints hints)
        {
            var beyond = BeyondLine(track.path, p, ref hints.main, out _);
            var routes = track.alternatePaths;
            if (beyond <= 0f || routes.Count == 0) return beyond;
            hints.Fit(routes.Count);
            for (var i = 0; i < routes.Count; i++) beyond = Mathf.Min(beyond, BeyondRoute(routes[i], p, ref hints.routes[i], out _));
            return beyond;
        }

        /// <summary>
        /// Alternative route on whose road <paramref name="p"/> lies off the main line's road, -1 for none.
        /// <paramref name="distance"/> is the distance along that route.
        /// </summary>
        public static int RouteAt(TrackRuntime track, Vector3 p, Hints hints, out float distance)
        {
            distance = 0f;
            var routes = track.alternatePaths;
            if (routes.Count == 0 || BeyondLine(track.path, p, ref hints.main, out _) <= 0f) return -1;
            hints.Fit(routes.Count);
            var best = -1;
            var bestBeyond = 0f;
            for (var i = 0; i < routes.Count; i++)
            {
                var beyond = BeyondRoute(routes[i], p, ref hints.routes[i], out var d);
                if (beyond > bestBeyond) continue;
                best = i;
                bestBeyond = beyond;
                distance = d;
            }
            return best;
        }

        /// <summary>How far (m) <paramref name="p"/> lies beyond the road edges of alternative route <paramref name="route"/>.</summary>
        public static float BeyondRoute(TrackRuntime track, int route, Vector3 p, Hints hints, out float distance)
        {
            hints.Fit(track.alternatePaths.Count);
            return BeyondRoute(track.alternatePaths[route], p, ref hints.routes[route], out distance);
        }

        static float BeyondLine(TrackPath path, Vector3 p, ref int hint, out float distance)
        {
            distance = path.Project(p, ref hint, out var lateral);
            return lateral - 0.5f * Mathf.Max(MinLineWidth, path.GetWidth(distance));
        }

        // Far from a route's nodes the route is skipped and its hint dropped, so a later nearby search starts afresh.
        static float BeyondRoute(TrackPath route, Vector3 p, ref int hint, out float distance)
        {
            distance = 0f;
            if (route == null || route.Count < 2) return float.PositiveInfinity;
            var b = route.WorldBounds;
            if (p.x < b.min.x - RouteBoundsMargin || p.x > b.max.x + RouteBoundsMargin || p.z < b.min.z - RouteBoundsMargin || p.z > b.max.z + RouteBoundsMargin)
            {
                hint = -1;
                return float.PositiveInfinity;
            }
            return BeyondLine(route, p, ref hint, out distance);
        }

        // Ground below this is too close to the kill height: KillHeightMargin above it, but never above the lowest racing line
        // node less LineFloorMargin (tracks whose kill height lies just under the road). Cached per track.
        static float Floor(TrackRuntime track)
        {
            if (floorTrack == track) return floor;
            floorTrack = track;
            var lowest = float.PositiveInfinity;
            var path = track.path;
            for (var i = 0; i < path.Count; i++) lowest = Mathf.Min(lowest, path.GetNodePosition(i).y);
            floor = Mathf.Min(track.killHeight + KillHeightMargin, lowest - LineFloorMargin);
            return floor;
        }

        static bool InKillZone(Vector3 p)
        {
            var zones = TrackZone.KillZones;
            for (var i = 0; i < zones.Count; i++)
                if (zones[i].Contains(p))
                    return true;
            return false;
        }

        /// <summary>Projection hints of one caller onto the main line and each alternative route.</summary>
        public sealed class Hints
        {
            public int main = -1;
            public int[] routes = System.Array.Empty<int>();

            /// <summary>Forgets all hints (the next projections search the whole lines).</summary>
            public void Reset()
            {
                main = -1;
                for (var i = 0; i < routes.Length; i++) routes[i] = -1;
            }

            /// <summary>One route hint per alternative route (allocates only when the route count changes).</summary>
            public void Fit(int count)
            {
                if (routes.Length == count) return;
                routes = new int[count];
                for (var i = 0; i < count; i++) routes[i] = -1;
            }
        }
    }
}
