using MHK.Core;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Lobs the item in a high arc to a spot on the track ahead along the racing line (MHK Extra water bomb). The faster
    /// the thrower drives, the farther it flies. On landing it shows <see cref="landedVisual"/> and/or
    /// <see cref="landedDecal"/> and enables <see cref="activateOnLanding"/> (e.g. the puddle's <see cref="HazardTrap"/>).
    /// </summary>
    public class TrackLobPowerup : PowerupBehaviour, ILandingPowerup
    {
        const float TangentSmoothing = 3f;
        const float FlatMinSqr = 1e-4f;
        const float GroundProbeUp = 6f;
        const float GroundProbeDown = 24f;
        const float MinDuration = 0.05f;

        [Header("Target")]
        [Tooltip("Distance ahead along the racing line where the item lands when thrown standing still (m).")]
        public float throwDistance = 20f;
        [Tooltip("Extra distance per m/s of the thrower's forward speed (s), e.g. 1.2 → +30 m at 25 m/s.")]
        public float distancePerSpeed = 1.2f;
        [Tooltip("Share of the thrower's sideways offset from the racing line kept at the landing spot.")]
        [SerializeField, Range(0f, 1f)] float keepLateralOffset = 0.5f;
        [Tooltip("The landing spot stays within this share of the half road width around the racing line.")]
        [SerializeField, Range(0f, 1f)] float maxLateralShare = 0.7f;
        [SerializeField] LayerMask groundMask = ~0;
        [SerializeField] float groundOffset = 0.03f;
        [SerializeField] bool alignToGround = true;

        [Header("Flight")]
        [Tooltip("Flight time (s) = flightTime + flightTimePerMetre × distance.")]
        [SerializeField] float flightTime = 0.8f;
        [SerializeField] float flightTimePerMetre = 0.01f;
        [Tooltip("Height of the arc's apex above the straight line between thrower and landing spot (m) = arcHeight + arcHeightPerMetre × distance.")]
        [SerializeField] float arcHeight = 3f;
        [SerializeField] float arcHeightPerMetre = 0.08f;
        [SerializeField] Vector3 launchOffset = new(0f, 1f, 1.2f);
        [Tooltip("Tumble of the flight visual (degrees per second).")]
        [SerializeField] Vector3 tumble = new(260f, 90f, 0f);

        [Header("Presentation")]
        [SerializeField] GameObject flightVisual;
        [SerializeField] GameObject landedVisual;
        [Tooltip("Stamped onto the ground on landing (projected onto slopes and bumps).")]
        [SerializeField] GroundDecal landedDecal;
        [SerializeField] float landedDecalRadius = 2f;
        [Tooltip("Disabled during the flight, enabled on landing.")]
        [SerializeField] Behaviour[] activateOnLanding;
        [SerializeField] GameObject landVfx;

        [Header("Sounds")]
        [Tooltip("One-shot where the item leaves the thrower's hand.")]
        [SerializeField] AudioClip launchSound;
        [Tooltip("Loop that follows the item along its arc (stops when it lands).")]
        [SerializeField] Audio.LoopSound flightLoop = new();
        [Tooltip("One-shot where the item lands.")]
        [SerializeField] AudioClip landSound;

        Vector3 start, target, targetNormal = Vector3.up;
        Quaternion landRotation = Quaternion.identity;
        float t, distance, duration, apex;
        bool flying;
        Effects.EffectLoopSound loop;

        public bool Landed { get; private set; }

        protected override void OnLaunch()
        {
            var o = Owner.transform;
            start = o.TransformPoint(launchOffset);
            ResolveTarget();
            duration = Mathf.Max(MinDuration, flightTime + flightTimePerMetre * distance);
            apex = arcHeight + arcHeightPerMetre * distance;
            transform.SetPositionAndRotation(start, Quaternion.Euler(0f, o.eulerAngles.y, 0f));
            SetEnabled(activateOnLanding, false);
            if (flightVisual != null) flightVisual.SetActive(true);
            if (landedVisual != null) landedVisual.SetActive(false);
            if (landedDecal != null) landedDecal.Radius = 0f;
            flying = true;
            PlaySfx(launchSound, start);
            loop = Effects.EffectLoopSound.Play(gameObject, flightLoop);
            InterpolatePose();
        }

        void ResolveTarget()
        {
            var flatForward = TrackRuntime.TryGetMainPath(out var path) ? TargetOnPath(path) : TargetStraightAhead();
            // Cast from just above the racing line so bridges resolve to the road level.
            if (ItemPhysics.TryGroundBelow(target, GroundProbeUp, GroundProbeDown, out var hit, groundMask))
            {
                target = hit.point;
                targetNormal = hit.normal;
            }
            var yaw = Quaternion.LookRotation(flatForward, Vector3.up);
            landRotation = alignToGround ? Quaternion.FromToRotation(Vector3.up, targetNormal) * yaw : yaw;
            distance = Vector3.Distance(start, target);
        }

        Vector3 TargetOnPath(TrackPath path)
        {
            var o = Owner.transform;
            var d0 = Owner.Progress.PathDistance;
            var lateral = SignedLateral(path, d0, o.position);
            var d1 = d0 + ThrowDistance();
            var flatForward = GeometryUtil.FlatNormalized(path.GetTangent(d1, TangentSmoothing), o.forward, FlatMinSqr);
            var side = Vector3.Cross(Vector3.up, flatForward);
            var halfWidth = path.GetWidth(d1) * 0.5f * maxLateralShare;
            target = path.GetPoint(d1) + side * Mathf.Clamp(lateral * keepLateralOffset, -halfWidth, halfWidth);
            return flatForward;
        }

        Vector3 TargetStraightAhead()
        {
            var o = Owner.transform;
            var flatForward = GeometryUtil.FlatNormalized(o.forward, Vector3.forward, FlatMinSqr);
            target = o.position + flatForward * ThrowDistance();
            return flatForward;
        }

        float ThrowDistance()
        {
            var speed = Owner.Physics != null ? Mathf.Max(0f, Owner.Physics.ForwardSpeed) : 0f;
            return throwDistance + distancePerSpeed * speed;
        }

        static float SignedLateral(TrackPath path, float d, Vector3 position)
        {
            var side = Vector3.Cross(Vector3.up, GeometryUtil.FlatNormalized(path.GetTangent(d, TangentSmoothing), Vector3.forward, FlatMinSqr));
            return Vector3.Dot(position - path.GetPoint(d), side);
        }

        protected override void Step(float dt)
        {
            if (!flying) return;
            t += dt;
            var s = Mathf.Clamp01(t / duration);
            transform.position = Vector3.Lerp(start, target, s) + Vector3.up * (apex * 4f * s * (1f - s));
            if (s >= 1f) Land();
        }

        // The tumble is visual only: it follows frame time.
        void Update()
        {
            if (flying && flightVisual != null) flightVisual.transform.Rotate(tumble * Time.deltaTime, Space.Self);
        }

        void Land()
        {
            flying = false;
            Landed = true;
            transform.SetPositionAndRotation(target + targetNormal * groundOffset, landRotation);
            if (flightVisual != null) flightVisual.SetActive(false);
            if (landedVisual != null) landedVisual.SetActive(true);
            if (landedDecal != null) landedDecal.Radius = landedDecalRadius;
            SetEnabled(activateOnLanding, true);
            Effects.EffectLoopSound.Stop(ref loop);
            PowerupFx.Spawn(landVfx, target, landSound, soundVolume);
        }
    }
}
