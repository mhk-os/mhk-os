using MHK.Core;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Places the powerup object in the world: thrown forward in a ballistic arc (<see cref="throwForward"/>) or dropped
    /// behind the kart and stuck straight onto the solid ground below (MHK2: no fall). With no ground it is lost.
    /// Combine with <see cref="HazardTrap"/>, <see cref="AreaEffectPowerup"/> or <see cref="MagnetPowerup"/>.
    /// </summary>
    public class PlacementPowerup : PowerupBehaviour, ILandingPowerup
    {
        /// <summary>Throw origin ahead of the kart (m).</summary>
        public const float ThrowOriginForward = 1.5f;
        /// <summary>Throw origin above the kart (m).</summary>
        public const float ThrowOriginUp = 0.5f;
        const float DiscardSlopeLimit = 45f;
        const float WallRestitution = 0.3f;
        const float BounceSoundInterval = 0.15f;
        const float ContactLift = 0.3f;
        const float ContactDrop = 0.1f;
        const float MaxFlightProbeUp = 1f;
        const float MaxFlightProbeDown = 29f;

        [Tooltip("Dropped items: distance (m) from the placing kart's centre to the item's (MHK2 / MHK3 data), unless " +
                 "dropClearOfChassis.")]
        [SerializeField] float dropDistance = 2.2f;
        [Tooltip("Dropped items are placed just clear of the placing kart instead: their meshes start dropGap behind the " +
                 "back of its chassis, whatever the kart's size, so a kart right behind drives into them (x-field plate).")]
        [SerializeField] bool dropClearOfChassis;
        [Tooltip("Gap (m) between the placing kart's chassis and the dropped item's meshes (dropClearOfChassis).")]
        [SerializeField, Min(0f)] float dropGap = 0.1f;
        [Tooltip("When fired forwards, throw instead of dropping behind.")]
        public bool throwForward = true;
        [Tooltip("When true the item always goes forward (e.g. thrown black hole).")]
        public bool alwaysForward;
        public float throwSpeed = 25f;
        public float throwUpSpeed = 6f;
        [Tooltip("Share of the kart's velocity added to the throw (MHK2 projectiles: 0).")]
        [SerializeField] float inheritVelocity = 0.8f;
        [SerializeField] float groundOffset = 0.05f;
        [SerializeField] LayerMask groundMask = ~0;
        public float gravity = 20f;
        [Tooltip("Landing on ground steeper than this discards the item (MHK2 black hole: normal.y < 0.85).")]
        [SerializeField] float maxGroundSlope = 60f;
        [Tooltip("Thrown items open where they are after this flight time (MHK2 black hole 'throwingTimer').")]
        public float maxFlightTime = 5f;
        [Tooltip("Orient to the ground normal when landing.")]
        [SerializeField] bool alignToGround = true;
        [Tooltip("Dropped items: the ground is searched from this far above the drop point...")]
        [SerializeField] float dropProbeUp = 1f;
        [Tooltip("...down to this far below it (MHK2: 14).")]
        [SerializeField] float dropProbeDown = 14f;

        [Header("Sounds")]
        [Tooltip("Loop that follows a thrown item while it flies (stops when it lands).")]
        [SerializeField] Audio.LoopSound flightLoop = new();
        [Tooltip("One-shot when a thrown item glances off a wall and keeps flying.")]
        [SerializeField] AudioClip bounceSound;
        [Tooltip("One-shot when a thrown item lands (items dropped behind stick at once and stay silent: use the definition's useSound).")]
        [SerializeField] AudioClip landSound;
        [Tooltip("One-shot where the item is lost: dropped or thrown over a chasm, or onto ground too steep to hold it.")]
        [SerializeField] AudioClip failSound;

        Vector3 velocity;
        bool flying;
        float flightTime, lastBounce = -1f;
        Effects.EffectLoopSound loop;

        public bool Landed { get; private set; }

        bool DiscardsOnSteepGround => maxGroundSlope < DiscardSlopeLimit;

        protected override void OnLaunch()
        {
            transform.rotation = Quaternion.Euler(0, Owner.transform.eulerAngles.y, 0);
            if (!alwaysForward && (!throwForward || Context.backwards)) Drop();
            else Throw();
        }

        void Drop()
        {
            var back = transform.rotation * Vector3.back;
            var distance = dropClearOfChassis ? Mathf.Max(0f, OwnerChassisReach(back)) + dropGap + MeshReach(-back) : dropDistance;
            var point = Owner.transform.position + back * distance;
            if (!ItemPhysics.TryGroundBelow(point, dropProbeUp, dropProbeDown, out var hit, groundMask)
                || (Vector3.Angle(hit.normal, Vector3.up) > maxGroundSlope && DiscardsOnSteepGround))
            {
                Lose();
                return;
            }
            Land(hit.point, hit.normal, false);
        }

        void Throw()
        {
            var t = Owner.transform;
            // Landing distance as in the original: the kart velocity share is taken in original units (÷ SpeedScale).
            var kartVel = Owner.Physics.Body.linearVelocity * (inheritVelocity / SpeedScale);
            transform.position = t.position + t.forward * ThrowOriginForward + Vector3.up * ThrowOriginUp;
            velocity = t.forward * throwSpeed + Vector3.up * throwUpSpeed + kartVel;
            flying = true;
            loop = Effects.EffectLoopSound.Play(gameObject, flightLoop);
        }

        // Corners of each mesh's local bounds, so a rotated plate is not oversized like a world AABB.
        float MeshReach(Vector3 direction)
        {
            var origin = transform.position;
            var reach = 0f;
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (!BoundsUtil.ModelSpace(r, out var space) || !r.enabled) continue;
                var b = r.localBounds;
                for (var i = 0; i < 8; i++)
                    reach = Mathf.Max(reach, Vector3.Dot(space.MultiplyPoint3x4(BoundsUtil.Corner(b, i)) - origin, direction));
            }
            return reach;
        }

        void Update()
        {
            if (!flying) return;
            var dt = Time.deltaTime;
            flightTime += dt;
            velocity += Vector3.down * (gravity * dt);
            var next = transform.position + velocity * dt;
            if (TryResolveContact(next)) return;
            transform.position = next;
            var track = Race.TrackRuntime.Current;
            if (track != null && next.y < track.killHeight)
            {
                Lose();
                return;
            }
            if (flightTime > maxFlightTime) LandAfterMaxFlight();
        }

        // Fall zones are not solid for items (ItemPhysics): a trap thrown over a chasm falls into it.
        bool TryResolveContact(Vector3 next)
        {
            if (!ItemPhysics.Linecast(transform.position + Vector3.up * ContactLift, next + Vector3.down * ContactDrop, out var hit, groundMask))
                return false;
            if (Vector3.Angle(hit.normal, Vector3.up) <= maxGroundSlope) Land(hit.point, hit.normal, true);
            else if (DiscardsOnSteepGround) Lose();
            else Bounce(hit);
            return true;
        }

        // A grazing item can touch the same wall on consecutive frames: one bounce sound per interval.
        void Bounce(RaycastHit hit)
        {
            velocity = Vector3.Reflect(velocity, hit.normal) * WallRestitution;
            if (flightTime - lastBounce > BounceSoundInterval) PlaySfx(bounceSound, hit.point);
            lastBounce = flightTime;
        }

        void LandAfterMaxFlight()
        {
            if (ItemPhysics.TryGroundBelow(transform.position, MaxFlightProbeUp, MaxFlightProbeDown, out var g, groundMask))
                Land(g.point, g.normal, true);
            else Land(transform.position, Vector3.up, true);
        }

        void Land(Vector3 point, Vector3 normal, bool thrown)
        {
            transform.position = point + normal * groundOffset;
            var yaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
            transform.rotation = alignToGround ? Quaternion.FromToRotation(Vector3.up, normal) * yaw : yaw;
            flying = false;
            Landed = true;
            Effects.EffectLoopSound.Stop(ref loop);
            if (thrown) PlaySfx(landSound, transform.position);
        }

        void Lose()
        {
            flying = false;
            PlaySfx(failSound, transform.position);
            Destroy(gameObject);
        }
    }
}
