using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// MHK2 glibber ball (0x457550/0x457160): a thrown rigid sphere that bounces, settles and dissolves after
    /// <see cref="timeout"/>. It hits any kart it touches or sweeps past within <see cref="hitRadius"/>, and fall zones
    /// are not solid for it.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PhysicsBallPowerup : PowerupBehaviour, INetProjectile
    {
        const float OriginalDamping = 3.1f;
        const float ReleaseBackoff = 0.05f;
        const float TunnelSnapGap = 0.02f;
        const float GroundProbeExtra = 0.15f;
        const float MinGroundNormalY = 0.5f;
        const float BounceSoundInterval = 0.1f;
        const float SegmentEpsilon = 1e-8f;
        // Share of an online position correction applied per step.
        const float CorrectionShare = 0.2f;

        public float throwSpeed = 80f;
        [Tooltip("Gap (m) between the thrower's chassis and the ball's surface where it is released, in front (or behind " +
                 "when thrown backwards). MHK2 released it 3 m from the kart's centre, past a kart right behind.")]
        [SerializeField] float spawnGap = 0.1f;
        [SerializeField] float spawnHeight = 1f;
        public float moveTime = 5f;
        [Tooltip("Seconds of damping after moveTime before the ball is frozen. It only freezes once it rests on solid ground.")]
        [SerializeField] float settleTime = 3f;
        [SerializeField] float timeout = 20f;
        [Tooltip("Hits a kart whose centre the ball passes this close (m), besides any kart it touches.")]
        public float hitRadius = 1.3f;
        [Tooltip("Velocity kept when the anti-tunnelling guard bounces the ball off geometry it passed through.")]
        [SerializeField] float bounce = 0.8f;
        [SerializeField] StatusEffectDefinition hitEffect;
        [SerializeField] GameObject hitVfx;

        [Header("Sounds")]
        [Tooltip("Loop that follows the ball while it is thrown and rolling (stops when it comes to rest).")]
        [SerializeField] Audio.LoopSound flightLoop = new();
        [Tooltip("Loop while the ball lies at rest on the track.")]
        [SerializeField] Audio.LoopSound idleLoop = new();
        [Tooltip("One-shot when the ball bounces off the ground or a wall.")]
        [SerializeField] AudioClip bounceSound;
        [Tooltip("Bounces slower than this (m/s along the contact normal) are silent.")]
        [SerializeField] float bounceMinSpeed = 4f;
        [Tooltip("One-shot where it hits a kart (MHK2 PGB_hit).")]
        [SerializeField] AudioClip hitSound;
        [Tooltip("One-shot where it dissolves: timeout, or fallen below the track (MHK2 PGB_fail).")]
        [SerializeField] AudioClip failSound;

        Rigidbody body;
        Collider[] colliders;
        float timer;
        float radius = 0.5f;
        int mask;
        Vector3 lastPosition, sweptFrom;
        bool done;
        float lastBounceTime = -1f;
        Effects.EffectLoopSound loop;
        Vector3 bodyCorrection;

        public bool Flying => body != null && !done && !body.isKinematic;
        public Vector3 NetPosition => body != null ? body.position : transform.position;
        public Vector3 NetVelocity => body != null ? body.linearVelocity : Vector3.zero;

        public void Correct(Vector3 position, Vector3 velocity)
        {
            if (!Flying) return;
            bodyCorrection = position - body.position;
            body.linearVelocity = velocity;
        }

        // A rigidbody moves through the physics step: a remote use cannot be caught up step by step.
        protected internal override bool FastForwards => false;

        protected internal override float NetHitReach => hitRadius + radius;

        protected internal override void OnNetHit(Kart victim)
        {
            if (done) return;
            done = true;
            Despawn(hitVfx, NetPosition, hitSound);
        }

        protected override void OnLaunch()
        {
            body = GetComponent<Rigidbody>();
            colliders = GetComponentsInChildren<Collider>();
            if (TryGetComponent<SphereCollider>(out var sphere))
            {
                var s = transform.lossyScale;
                radius = sphere.radius * Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            }
            mask = ItemPhysics.MaskFor(gameObject.layer);
            IgnoreOwnerAndFallZones();

            var t = Owner.transform;
            var sign = Context.backwards ? -1f : 1f;
            var origin = t.position + Vector3.up * spawnHeight;
            transform.position = ReleasePoint(origin, t.forward * sign);
            body.position = transform.position;
            body.linearVelocity = t.rotation * new Vector3(0f, 1f, throwSpeed * sign);
            lastPosition = body.position;
            // The first hit test sweeps from the thrower's centre: a kart touching it is caught on the first frame.
            sweptFrom = origin;
            loop = Effects.EffectLoopSound.Play(gameObject, flightLoop);
        }

        void IgnoreOwnerAndFallZones()
        {
            foreach (var c in Owner.GetComponentsInChildren<Collider>())
                foreach (var mine in colliders)
                    Physics.IgnoreCollision(c, mine);
            ItemPhysics.IgnorePassThrough(colliders);
        }

        Vector3 ReleasePoint(Vector3 origin, Vector3 dir)
        {
            var reach = Mathf.Max(0f, OwnerChassisReach(dir)) + radius + spawnGap;
            var distance = reach;
            if (ItemPhysics.SphereCast(origin, radius, dir, reach, out var hit, mask))
                distance = Mathf.Max(0f, hit.distance - ReleaseBackoff);
            return origin + dir * distance;
        }

        protected override void Step(float dt)
        {
            if (body == null || done) return;
            timer += dt;
            if (timer > timeout || TrackRuntime.IsBelowKillHeight(body.position))
            {
                Dissolve();
                return;
            }
            CheckHits();
            if (done || body.isKinematic) return;
            ApplyBodyCorrection();
            PreventTunnelling();
            if (timer > moveTime) Damp();
            // The original freezes the body 3 s after the damping starts. Here only once it lies on the ground,
            // so a ball that is still falling or rolling over an edge never hangs in mid-air.
            if (timer > moveTime + settleTime && OnGround()) Freeze();
        }

        // 0.95 per frame at about 60 fps in the original.
        void Damp()
        {
            var k = Mathf.Exp(-OriginalDamping * Time.fixedDeltaTime);
            var v = body.linearVelocity;
            body.linearVelocity = new Vector3(v.x * k, v.y, v.z * k);
            body.angularVelocity *= k;
        }

        void Freeze()
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            Effects.EffectLoopSound.Stop(ref loop);
            loop = Effects.EffectLoopSound.Play(gameObject, idleLoop);
        }

        // Continuous collision misses thin two-sided walls at 80 m/s now and then. The centre can never legitimately
        // cross static geometry, so a crossing is undone and the ball bounced off the surface.
        void PreventTunnelling()
        {
            var p = body.position;
            if (ItemPhysics.Linecast(lastPosition, p, out var hit, mask))
            {
                body.position = hit.point + hit.normal * (radius + TunnelSnapGap);
                var v = body.linearVelocity;
                if (Vector3.Dot(v, hit.normal) < 0f)
                {
                    if (bounceSound != null && -Vector3.Dot(v, hit.normal) >= bounceMinSpeed) PlayBounce(hit.point);
                    body.linearVelocity = Vector3.Reflect(v, hit.normal) * bounce;
                }
            }
            lastPosition = body.position;
        }

        bool OnGround() =>
            ItemPhysics.Raycast(body.position, Vector3.down, radius + GroundProbeExtra, out var hit, mask) && hit.normal.y > MinGroundNormalY;

        // Along the path the ball moved since the last step, so a fast ball cannot skip past a kart.
        void CheckHits()
        {
            var p = body.position;
            var r2 = hitRadius * hitRadius;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (GeometryUtil.DistanceSqToSegment(k.transform.position, sweptFrom, p, SegmentEpsilon) > r2) continue;
                if (TryHit(k, p)) return;
            }
            sweptFrom = p;
        }

        void ApplyBodyCorrection()
        {
            if (bodyCorrection == Vector3.zero) return;
            var move = bodyCorrection * CorrectionShare;
            body.position += move;
            lastPosition += move;
            bodyCorrection -= move;
            if (bodyCorrection.sqrMagnitude < 1e-6f) bodyCorrection = Vector3.zero;
        }

        bool TryHit(Kart k, Vector3 at)
        {
            if (done || !IsTangible(k) || OwnerImmune(k)) return false;
            HitKart(k, hitEffect);
            done = true;
            Despawn(hitVfx, at, hitSound);
            return true;
        }

        void Dissolve()
        {
            done = true;
            Despawn(hitVfx, transform.position, failSound);
        }

        void OnCollisionEnter(Collision c)
        {
            if (done) return;
            // Bouncing off a kart's body is a hit too: the chassis keeps the ball further than hitRadius from its centre.
            if (c.rigidbody != null && c.rigidbody.TryGetComponent<Kart>(out var kart) && TryHit(kart, transform.position)) return;
            if (bounceSound == null || c.contactCount == 0) return;
            var contact = c.GetContact(0);
            if (Mathf.Abs(Vector3.Dot(c.relativeVelocity, contact.normal)) < bounceMinSpeed) return;
            PlayBounce(contact.point);
        }

        void PlayBounce(Vector3 at)
        {
            if (Time.time - lastBounceTime < BounceSoundInterval) return;
            lastBounceTime = Time.time;
            PlaySfx(bounceSound, at);
        }
    }
}
