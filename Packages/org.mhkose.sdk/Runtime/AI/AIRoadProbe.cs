using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>Ground and wall probes that measure how far the drivable road reaches beside a track line.</summary>
    internal static class AIRoadProbe
    {
        const float ProbeStep = 0.5f;
        const float ProbeAbove = 3f, ProbeDepth = 20f;
        // Largest height step (m) between the centre ground and a probe point, plus this share of the lateral distance (banking).
        const float MaxStep = 0.6f, MaxBank = 0.35f;
        const float MinRayLength = 1e-3f;

        // Wall rays from the centre to a probe point run at kerb and kart height.
        [NoAutoStaticsCleanup] static readonly float[] WallRayHeights = { 0.15f, 0.6f };
        [NoAutoStaticsCleanup] static readonly RaycastHit[] Hits = new RaycastHit[16];
        [NoAutoStaticsCleanup] static readonly RaycastHit[] GroundHits = new RaycastHit[16];

        /// <summary>
        /// How far (m, up to <paramref name="max"/>) the road reaches from centre point <paramref name="c"/> in direction
        /// <paramref name="dir"/>: the last probe with drivable ground under it and no wall in between.
        /// </summary>
        public static float Reach(Vector3 c, Vector3 dir, float max, int mask)
        {
            if (!Ground(c, c.y, mask, out var floor) || !Drivable(floor)) return 0f;
            var reach = 0f;
            for (var o = ProbeStep; o <= max + 1e-3f; o += ProbeStep)
            {
                if (!Ground(c + dir * o, floor.point.y, mask, out var hit) || !Drivable(hit)) break;
                if (Mathf.Abs(hit.point.y - floor.point.y) > MaxStep + MaxBank * o) break;
                if (Blocked(floor.point, hit.point, mask)) break;
                reach = o;
            }
            return reach;
        }

        /// <summary>
        /// The ground under <paramref name="p"/>: of everything a ray down from above meets, the surface nearest to
        /// <paramref name="height"/> (a roof, arch or bridge over the road is not its ground, MHK3 Rome).
        /// </summary>
        public static bool Ground(Vector3 p, float height, int mask, out RaycastHit ground)
        {
            var origin = new Vector3(p.x, Mathf.Max(p.y, height) + ProbeAbove, p.z);
            var count = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, ProbeAbove + ProbeDepth + Mathf.Abs(p.y - height), mask,
                QueryTriggerInteraction.Ignore);
            ground = default;
            var best = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var gap = Mathf.Abs(GroundHits[i].point.y - height);
                if (gap >= best) continue;
                best = gap;
                ground = GroundHits[i];
            }
            return count > 0;
        }

        static bool Drivable(in RaycastHit hit)
        {
            if (hit.normal.y < AIObstacleClassifier.DrivableNormalY || hit.collider.attachedRigidbody != null) return false;
            var surface = TrackSurface.Of(hit.collider);
            return surface == null || (!surface.offroad && !surface.kind.IsUnsafeGround());
        }

        // A wall or steep static scenery between two ground points (kerbs and low walls a kart catches its wheels on).
        static bool Blocked(Vector3 from, Vector3 to, int mask)
        {
            foreach (var height in WallRayHeights)
            {
                var a = from + Vector3.up * height;
                var delta = to + Vector3.up * height - a;
                var distance = delta.magnitude;
                if (distance < MinRayLength) continue;
                var count = Physics.RaycastNonAlloc(a, delta / distance, Hits, distance, mask, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                {
                    var hit = Hits[i];
                    if (hit.collider.attachedRigidbody != null) continue;
                    var surface = TrackSurface.Of(hit.collider);
                    if (surface != null && surface.kind.IsWall()) return true;
                    if (Mathf.Abs(hit.normal.y) < AIObstacleClassifier.SteepNormalY) return true;
                }
            }
            return false;
        }
    }
}
