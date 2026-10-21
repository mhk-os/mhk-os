using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// MHK2 homing missile (activate 0x4578a0, update 0x457b30): flies along the racing line towards the current race
    /// leader, locks on once armed and close, then accelerates and hits. Like the original it only ever hits the locked
    /// leader and flies through other karts.
    /// </summary>
    public partial class TrackHomingMissile : PowerupBehaviour
    {
        const float LaunchHeight = 1f;
        const float VerticalWeight = 0.25f;
        const float ArriveDistance = 1f;
        const float HitDistance = 2f;
        const float MinTravelSqr = 1e-4f;

        [SerializeField] float speed = 70f;
        [SerializeField] float lockedSpeed = 90f;
        [SerializeField] float armDelay = 2f;
        [SerializeField] float lifetime = 15f;
        [SerializeField] float activationDistance = 100f;
        [SerializeField] float lockDistance = 20f;
        [SerializeField] float cruiseLookAhead = 10f;
        [SerializeField] float cruiseHeight = 2f;
        [SerializeField] float lockedHeight = 1.25f;
        [SerializeField] StatusEffectDefinition hitEffect;
        [SerializeField] GameObject impactVfx;
        [Tooltip("Loop that follows the missile while it flies (MHK2 PSR_move).")]
        [SerializeField] Audio.LoopSound flightLoop = new();
        [Tooltip("One-shot when it locks on its target (MHK2 PSR_targetlocked).")]
        [SerializeField] AudioClip lockSound;
        [Tooltip("One-shot where it hits its target (MHK2 PSR_hit).")]
        [SerializeField] AudioClip hitSound;
        [Tooltip("One-shot where it ends without a hit: lifetime over or target missed (MHK2 PSR_fail).")]
        [SerializeField] AudioClip failSound;
        [Tooltip("How far the hit and fail one-shots carry (a floor volume above 0 makes them heard anywhere on the track).")]
        [SerializeField] Audio.SoundRange endSoundRange = new();
        [Tooltip("Spin of the model around its flight axis (deg/s).")]
        [SerializeField] float rollSpeed = 180f;
        [SerializeField] Transform model;

        float life, arm, currentSpeed;
        int dir = 1;
        int segment = -1;
        Kart target;
        Vector3 travel;

        [AutoStaticsCleanup] static readonly List<TrackHomingMissile> active = new();
        /// <summary>Missiles in flight (AI threat detection).</summary>
        public static IReadOnlyList<TrackHomingMissile> Active => active;
        /// <summary>The kart the missile goes for: its locked target, or before lock-on the current race leader.</summary>
        public Kart Hunted { get; private set; }
        /// <summary>Locked on <see cref="Hunted"/>: it will hit that kart whatever the race order does now.</summary>
        public bool Locked => target != null;
        /// <summary>The arm delay has passed, so it can lock on (before that it flies past karts).</summary>
        public bool Armed => arm <= 0f;

        void OnEnable() => active.Add(this);
        void OnDisable() => active.Remove(this);

        protected override void OnLaunch()
        {
            // Original world speeds, lifetime and arm delay (not scaled with the karts).
            life = lifetime;
            arm = armDelay;
            currentSpeed = speed;
            travel = Owner.transform.forward;
            transform.SetPositionAndRotation(Owner.transform.position + Vector3.up * LaunchHeight, Quaternion.LookRotation(travel, Vector3.up));
            Hunted = Leader();
            Effects.EffectLoopSound.Play(gameObject, flightLoop);
        }

        // Unlike TargetSelection.Leader this includes the owner (MHK2).
        static Kart Leader()
        {
            Kart best = null;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!k.isActiveAndEnabled || !IsTangible(k)) continue;
                if (best == null || k.Progress.Position < best.Progress.Position) best = k;
            }
            return best;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            life -= dt;
            var track = TrackRuntime.Current;
            if (life < 0f || track == null || track.path == null)
            {
                Finish(null);
                return;
            }
            var path = track.path;
            var myD = path.Project(transform.position, ref segment, out _);
            // A target that turned into a ghost is lost: the missile goes for the leader among the tangible karts.
            if (target != null && target.Effects.Ghost) target = null;
            Vector3 aim;
            if (target == null) aim = CruiseAim(path, myD, dt);
            else if (TryStrikeLocked(out aim)) return;
            Fly(aim, dt);
        }

        Vector3 CruiseAim(TrackPath path, float myD, float dt)
        {
            var leader = Leader();
            Hunted = leader;
            if (leader != null)
            {
                var delta = path.Delta(myD, leader.Progress.PathDistance);
                if (delta < -activationDistance) dir = -1;
                else if (delta > activationDistance) dir = 1;
                arm -= dt;
                var ahead = delta * dir;
                if (arm <= 0f && ahead >= 0f && ahead < lockDistance) LockOn(leader);
            }
            return path.GetPoint(myD + dir * cruiseLookAhead) + Vector3.up * cruiseHeight;
        }

        void LockOn(Kart leader)
        {
            target = leader;
            currentSpeed = lockedSpeed;
            PlaySfx(lockSound, transform.position);
        }

        bool TryStrikeLocked(out Vector3 aim)
        {
            Hunted = target;
            var tp = target.transform.position;
            aim = new Vector3(tp.x, tp.y + lockedHeight, tp.z);
            var v = aim - transform.position;
            v.y *= VerticalWeight;
            if (v.magnitude >= ArriveDistance) return false;
            var w = tp - transform.position;
            w.y *= VerticalWeight;
            Finish(w.magnitude < HitDistance ? target : null);
            return true;
        }

        void Fly(Vector3 aim, float dt)
        {
            var to = aim - transform.position;
            if (to.sqrMagnitude > MinTravelSqr) travel = to.normalized;
            transform.SetPositionAndRotation(transform.position + travel * (currentSpeed * dt), Quaternion.LookRotation(travel, Vector3.up));
            if (model != null) model.Rotate(0f, 0f, rollSpeed * dt, Space.Self);
        }

        void Finish(Kart victim)
        {
            if (victim != null) HitKart(victim, hitEffect);
            Despawn(impactVfx, transform.position, victim != null ? hitSound : failSound, endSoundRange);
        }
    }
}
