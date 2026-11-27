using MHK.Core;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Sends loose track props flying. A prop here is a movable (non-kinematic) body that is no kart, no item, no track
    /// hazard and no part of an animated prop. Karts that knock props out of their way use it, and so do shots that blast
    /// the props around where they explode.
    /// </summary>
    public static class PropKnock
    {
        // A knocked prop lifts a little as it flies off and tumbles at Spin rad/s. Props at the edge of a blast fly off at
        // EdgeShare of the blast speed.
        const float Lift = 0.35f, Spin = 6f, EdgeShare = 0.5f;
        static readonly Collider[] Buffer = new Collider[32];

        /// <summary>True for a loose prop a kart or a blast may send flying.</summary>
        public static bool IsKnockable(Rigidbody rb) =>
            rb != null && !rb.isKinematic && (GameLayers.KartAndItemMask & (1 << rb.gameObject.layer)) == 0 &&
            !rb.TryGetComponent<TrackHazard>(out _) && rb.GetComponentInParent<PropAnimator>() == null;

        /// <summary>
        /// Sends <paramref name="rb"/> off at <paramref name="speed"/> along <paramref name="away"/> (flattened, a little
        /// upwards) on top of <paramref name="baseVelocity"/>, tumbling.
        /// </summary>
        public static void Fling(Rigidbody rb, Vector3 baseVelocity, Vector3 away, float speed)
        {
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            rb.linearVelocity = baseVelocity + (away + Vector3.up * Lift).normalized * speed;
            rb.angularVelocity = Vector3.Cross(Vector3.up, away) * Spin;
            rb.WakeUp();
        }

        /// <summary>
        /// Blasts the props within <paramref name="radius"/> m of <paramref name="centre"/> away from it, at
        /// <paramref name="speed"/> close by down to half of it at the edge.
        /// </summary>
        public static void Blast(PhysicsScene scene, Vector3 centre, float radius, float speed)
        {
            if (radius <= 0f || speed <= 0f) return;
            var n = scene.OverlapSphere(centre, radius, Buffer, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var rb = Buffer[i].attachedRigidbody;
                if (!IsKnockable(rb)) continue;
                // A body with several colliders is already on its way after the first one sent it off.
                var away = rb.worldCenterOfMass - centre;
                var near = 1f - Mathf.Clamp01(away.magnitude / radius);
                var blast = speed * Mathf.Lerp(EdgeShare, 1f, near);
                if (Vector3.Dot(rb.linearVelocity, away.normalized) >= blast * EdgeShare) continue;
                Fling(rb, Vector3.zero, away, blast);
            }
        }
    }
}
