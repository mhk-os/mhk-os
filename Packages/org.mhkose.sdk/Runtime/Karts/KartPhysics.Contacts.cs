using MHK.Race;
using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        const float ProbeAbove = 0.35f;
        const float ProbeSkin = 0.001f;
        const int ProbePasses = 3;

        /// <summary>Contact sphere of one wheel. Fields, not properties: the step works on it through <c>ref wheels[i]</c>.</summary>
        struct Wheel
        {
            /// <summary>Sphere centre in kart space.</summary>
            public Vector3 local;
            public float radius;
            public bool front;
            /// <summary>Ground within the contact margin (speculative contact).</summary>
            public bool contact;
            /// <summary>Actually penetrating at detection time.</summary>
            public bool touching;
            public Vector3 point, normal;
            public float depth;
            public TrackSurface surface;
            public Collider collider;
            /// <summary>Last wall collider the contact ray passed (cached surface lookup).</summary>
            public Collider wallCollider;
            /// <summary>TrackSurface.SlipAngleScaleFor of the contact (MHK2 road without a surface).</summary>
            public float slipScale;
            /// <summary>Normal load from the previous step.</summary>
            public float loadNewtons;
            public float spinDegPerSec;
            public float lambdaN, lambdaLat, lambdaLon;
            public Vector3 dirLat, dirLon;
            public float latLimit, lonLimit, restitutionTarget;
            /// <summary>Lateral slip compliance (impulse space), 0 = rigid.</summary>
            public float lateralCompliance;
            /// <summary>Carried load or penetrated in the last step.</summary>
            public bool onGround;
            /// <summary>|Lateral slip speed| at the contact (m/s).</summary>
            public float lateralSlipSpeed;
            /// <summary>|Lateral force| / lateral limit of the last step (0..1).</summary>
            public float gripUse;
        }

        void DetectContacts(Quaternion rot, Vector3 up)
        {
            Surface = SurfaceKind.Road;
            var origin = Body.position;
            for (var i = 0; i < WheelCount; i++) ProbeWheel(ref wheels[i], origin, rot, up);
        }

        // The ray continues through wall geometry, which never carries a wheel (MHK2 skips wheel contacts with
        // collision_objects), so karts can neither climb walls nor hang on their edges.
        void ProbeWheel(ref Wheel wh, Vector3 origin, Quaternion rot, Vector3 up)
        {
            wh.contact = false;
            wh.touching = false;
            var start = origin + rot * wh.local + up * ProbeAbove;
            var travelled = 0f;
            var reach = ProbeAbove + wh.radius + contactMargin;
            for (var pass = 0; pass < ProbePasses; pass++)
            {
                if (!physicsScene.Raycast(start, -up, out var hit, reach - travelled, groundMask, QueryTriggerInteraction.Ignore)) break;
                if (ShouldSkipHit(ref wh, hit))
                {
                    start -= up * (hit.distance + ProbeSkin);
                    travelled += hit.distance + ProbeSkin;
                    continue;
                }
                SetContact(ref wh, hit, travelled);
                break;
            }
            if (wh.contact) return;
            wh.collider = null;
            wh.surface = null;
        }

        bool ShouldSkipHit(ref Wheel wh, in RaycastHit hit)
        {
            if (hit.rigidbody == Body || hit.collider == wh.wallCollider || PassesThrough(hit)) return true;
            if (hit.collider == wh.collider) return false;
            var surface = TrackSurface.Of(hit.collider);
            if (surface != null && surface.kind == SurfaceKind.Wall)
            {
                wh.wallCollider = hit.collider;
                return true;
            }
            wh.collider = hit.collider;
            wh.surface = surface;
            wh.slipScale = TrackSurface.SlipAngleScaleFor(surface != null ? surface.lateralFriction : 1f);
            return false;
        }

        void SetContact(ref Wheel wh, in RaycastHit hit, float travelled)
        {
            wh.contact = true;
            wh.point = hit.point;
            wh.normal = hit.normal;
            wh.depth = ProbeAbove + wh.radius - (travelled + hit.distance);
            wh.touching = wh.depth >= 0f;
            // Last non-road wheel wins.
            if (wh.surface != null && wh.surface.kind != SurfaceKind.Road) Surface = wh.surface.kind;
        }
    }
}
