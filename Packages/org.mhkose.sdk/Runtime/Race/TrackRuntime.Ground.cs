using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    public partial class TrackRuntime
    {
        const float GroundProbeAbove = 20f;
        const float GroundProbeBelow = 25f;
        const int GroundProbeMaxLayers = 12;
        const float GroundProbeStepBelow = 0.05f;
        const float AboveCostFactor = 1.5f;
        const float NoHeadroomCost = 100f;
        const float WaterCost = 20f;
        const float MinGroundNormalY = 0.6f;
        /// <summary>Approximate kart height (m) that must fit above a respawn spot on top of the drop height.</summary>
        const float KartHeight = 1.6f;
        const float RespawnLift = 0.6f;
        const float RespawnTangentSmoothing = 4f;
        /// <summary>Drop height when no GameConfig is loaded (editor tools), the GameConfig.respawnDropHeight default.</summary>
        const float FallbackDropHeight = 1.4f;
        const float HeadroomRadius = 0.3f;

        [NoAutoStaticsCleanup] static readonly Collider[] OverlapBuffer = new Collider[16];

        /// <summary>
        /// Safe pose on <paramref name="line"/> (main line or an alternative route). The ground is the drivable surface
        /// in a vertical column through the line closest to its height, surfaces above it count 1.5x, and it needs
        /// room for a dropped kart above it, so roofs and arches never win against the road under them.
        /// </summary>
        public Pose GetRespawnPose(TrackPath line, float distance, float lateralOffset = 0f)
        {
            var p = line.GetPoint(distance);
            var flatFwd = line.GetFlatTangent(distance, RespawnTangentSmoothing);
            var side = Vector3.Cross(Vector3.up, flatFwd);
            p += side * lateralOffset;
            var cfg = Core.GameRoot.Instance != null ? Core.GameRoot.Instance.Config : null;
            var clearance = (cfg != null ? cfg.respawnDropHeight : FallbackDropHeight) + RespawnLift + KartHeight;
            if (FindGround(p, clearance, out var ground)) p = ground.point;
            return new Pose(p + Vector3.up * RespawnLift, Quaternion.LookRotation(flatFwd, Vector3.up));
        }

        /// <summary>
        /// Drivable ground for a kart starting at <paramref name="slot"/> (a <see cref="GetGridPose"/> position), chosen
        /// as for respawns. Imported start slots sit inside or above the road, so karts go on this ground instead.
        /// </summary>
        public bool FindGridGround(Vector3 slot, out RaycastHit ground) => FindGround(slot, KartHeight, out ground);

        bool FindGround(Vector3 point, float clearance, out RaycastHit ground)
        {
            ground = default;
            var origin = point + Vector3.up * GroundProbeAbove;
            var bottom = point.y - GroundProbeBelow;
            var bestCost = float.MaxValue;
            var found = false;
            for (var k = 0; k < GroundProbeMaxLayers && origin.y > bottom; k++)
            {
                if (!GroundProbe.Down(origin, origin.y - bottom, groundMask, out var hit)) break;
                // Continue the column just below this surface (rays starting inside a solid skip its back faces).
                origin.y = hit.point.y - GroundProbeStepBelow;
                var penalty = GroundPenalty(hit);
                if (penalty < 0f) continue;
                var dy = hit.point.y - point.y;
                var cost = (dy > 0f ? dy * AboveCostFactor : -dy) + penalty;
                if (!HasHeadroom(hit.point, clearance)) cost += NoHeadroomCost;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    ground = hit;
                    found = true;
                }
            }
            return found;
        }

        // Negative = not a valid respawn ground.
        static float GroundPenalty(RaycastHit hit)
        {
            if (hit.normal.y < MinGroundNormalY) return -1f;
            if (hit.rigidbody != null && !hit.rigidbody.isKinematic) return -1f;
            var surface = TrackSurface.Of(hit.collider);
            if (surface == null) return 0f;
            if (surface.kind.IsUnsafeGround()) return -1f;
            return surface.kind == SurfaceKind.Water ? WaterCost : 0f;
        }

        bool HasHeadroom(Vector3 point, float height)
        {
            const float r = HeadroomRadius;
            var n = Physics.OverlapCapsuleNonAlloc(point + Vector3.up * (0.3f + r), point + Vector3.up * Mathf.Max(0.3f + r, height - r),
                r, OverlapBuffer, groundMask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var rb = OverlapBuffer[i].attachedRigidbody;
                if (rb == null || rb.isKinematic) return false;
            }
            return true;
        }
    }
}
