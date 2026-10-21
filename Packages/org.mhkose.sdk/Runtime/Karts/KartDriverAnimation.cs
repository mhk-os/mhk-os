using MHK.Modding;
using UnityEngine;
using Random = UnityEngine.Random;

namespace MHK.Karts
{
    /// <summary>
    /// Driver animation logic after MHK2's car anim update (FUN_0042d6c0): locomotion role from speed and input, steering
    /// scrub, idle fidgets, glances and shouts. Computer drivers' turn poses follow their shown (smoothed) steering.
    /// </summary>
    public sealed class KartDriverAnimation
    {
        const CharacterAnimRole NoRole = (CharacterAnimRole)(-1);
        const float TurnPoseOn = 0.12f, TurnPoseOff = 0.06f;
        const float SteerKeyThreshold = 0.1f;
        const float DriveMinSpeed = 3f, ReverseMaxSpeed = -5f, AccelerateMaxSpeed = 5f, BrakeMinSpeed = 20f;
        const float PedalThreshold = 0.5f;
        const float LandingCooldownSeconds = 0.5f;
        const float IdleDelay = 4f, IdleExtraWaitMax = 6f;
        const float GlanceWaitMin = 1f, GlanceWaitRange = 2f;
        const float ShoutWaitMin = 2f, ShoutWaitRange = 2f;
        const float ShoutReachAlong = 2f, ShoutReachSide = 6f;
        const float DriveRateMin = 0.2f, DriveRateRange = 2f, DriveRatePerSpeed = 0.02f;

        enum TurnSide { Left = -1, None, Right }

        readonly KartVisual owner;
        readonly IKartView view;
        readonly Kart kart;
        readonly CharacterAnimator driver, kartModel;
        readonly KartAudio audio;

        CharacterAnimRole lastWant = CharacterAnimRole.Idle;
        TurnSide turnSide;
        float shownSteer;
        float idleTimer, glanceTimer = 2f, shoutTimer = 3f;
        float landingCooldown, collisionCooldown;

        /// <summary>Animation logic of <paramref name="owner"/>. <paramref name="kart"/> is null for a replayed ghost.</summary>
        public KartDriverAnimation(KartVisual owner, IKartView view, Kart kart, CharacterAnimator driver, CharacterAnimator kartModel)
        {
            this.owner = owner;
            this.view = view;
            this.kart = kart;
            this.driver = driver;
            this.kartModel = kartModel;
            if (kart != null) audio = kart.GetComponent<KartAudio>();
        }

        /// <summary>The driver if there is one, else the kart model's animator (null when neither exists).</summary>
        public CharacterAnimator Lead => driver != null ? driver : kartModel;

        /// <summary>True while a counted collision blocks the next collide reaction.</summary>
        public bool CollisionCoolingDown => collisionCooldown > 0f;

        /// <summary>Starts the collision cooldown after a collide reaction.</summary>
        public void StartCollisionCooldown() => collisionCooldown = KartImpact.CooldownSeconds;

        /// <summary>
        /// Advances the animation logic by <paramref name="dt"/> with the steering shown this frame (share of full lock).
        /// Landings, glances and shouts are a live kart's own, a ghost replays the ones it recorded.
        /// </summary>
        public void Update(float dt, float steerShown)
        {
            var lead = Lead;
            if (lead == null) return;
            shownSteer = steerShown;
            var input = view.CurrentInput;
            TriggerLanding(dt);
            var v = view.ForwardSpeedOriginal;
            var restart = false;
            var want = ChooseLocomotion(lead, input, v, dt, ref restart);
            if (want == NoRole) want = lastWant;
            lastWant = want;
            ApplyLocomotion(want, v, restart);
        }

        void TriggerLanding(float dt)
        {
            landingCooldown -= dt;
            collisionCooldown -= dt;
            if (kart == null || !view.JustLanded || landingCooldown > 0f) return;
            landingCooldown = LandingCooldownSeconds;
            owner.PlayAction(CharacterAnimRole.JumpLanding);
        }

        CharacterAnimRole ChooseLocomotion(CharacterAnimator lead, in KartInput input, float v, float dt, ref bool restart)
        {
            var want = NoRole;
            var finished = view.RaceOver;
            if (!view.ControlsEnabled && !finished)
            {
                // Before the start: humans play 'start', computer drivers idle now and then (MHK2 race state 0).
                if (view.IsHuman) return CharacterAnimRole.Start;
                IdleTimer(dt, ref want, ref restart);
                return want;
            }
            if (input.lookBack && !finished) return CharacterAnimRole.Backward;
            var playing = lead.LocomotionRole;
            want = DrivingRole(input, v);
            if (want == NoRole) IdleTimer(dt, ref want, ref restart);
            if (kart != null && !lead.ActionActive && want == playing) TryFidget(want, dt);
            return want;
        }

        CharacterAnimRole DrivingRole(in KartInput input, float v)
        {
            var want = NoRole;
            if (v > DriveMinSpeed) want = TurnRole(CharacterAnimRole.TurnLeft, CharacterAnimRole.TurnRight, CharacterAnimRole.Drive);
            if (v < ReverseMaxSpeed)
            {
                want = CharacterAnimRole.Backward;
                if (lastWant is CharacterAnimRole.Backward or CharacterAnimRole.BackwardTurnLeft or CharacterAnimRole.BackwardTurnRight)
                    want = TurnRole(CharacterAnimRole.BackwardTurnLeft, CharacterAnimRole.BackwardTurnRight, want);
            }
            if (v < AccelerateMaxSpeed && input.throttle > PedalThreshold) want = CharacterAnimRole.Accelerate;
            if (v > BrakeMinSpeed && input.brake > PedalThreshold) want = CharacterAnimRole.Brake;
            return want;
        }

        void TryFidget(CharacterAnimRole want, float dt)
        {
            if (want == CharacterAnimRole.Drive && (glanceTimer -= dt) <= 0f)
            {
                glanceTimer = GlanceWaitMin + GlanceWaitRange * Random.value;
                owner.PlayAction(Random.value < 0.5f ? CharacterAnimRole.LookLeft : CharacterAnimRole.LookRight);
            }
            else if ((want is CharacterAnimRole.Drive or CharacterAnimRole.TurnLeft or CharacterAnimRole.TurnRight)
                     && (shoutTimer -= dt) <= 0f && TryShout())
                shoutTimer = ShoutWaitMin + ShoutWaitRange * Random.value;
        }

        void ApplyLocomotion(CharacterAnimRole want, float v, bool restart)
        {
            // MHK2: turn pose time = |steer angle| / max steer angle × length, drive speed 0.2 + 2·min(|v|·0.02, 1).
            var scrub = want is CharacterAnimRole.TurnLeft or CharacterAnimRole.BackwardTurnLeft ? Mathf.Max(0f, -shownSteer)
                : want is CharacterAnimRole.TurnRight or CharacterAnimRole.BackwardTurnRight ? Mathf.Max(0f, shownSteer) : 0f;
            var rate = DriveRateMin + DriveRateRange * Mathf.Min(Mathf.Abs(v) * DriveRatePerSpeed, 1f);
            if (driver != null) driver.SetLocomotion(want, scrub, rate, restart);
            if (kartModel != null) kartModel.SetLocomotion(want, scrub, rate, restart);
        }

        CharacterAnimRole TurnRole(CharacterAnimRole left, CharacterAnimRole right, CharacterAnimRole straight)
        {
            if (view.IsHuman)
            {
                var key = view.CurrentInput.steer;
                return key > SteerKeyThreshold ? right : key < -SteerKeyThreshold ? left : straight;
            }
            var side = shownSteer > 0f ? TurnSide.Right : TurnSide.Left;
            var size = Mathf.Abs(shownSteer);
            if (turnSide != TurnSide.None && (side != turnSide || size < TurnPoseOff)) turnSide = TurnSide.None;
            if (turnSide == TurnSide.None && size > TurnPoseOn) turnSide = side;
            return turnSide == TurnSide.Right ? right : turnSide == TurnSide.Left ? left : straight;
        }

        void IdleTimer(float dt, ref CharacterAnimRole want, ref bool restart)
        {
            idleTimer += dt;
            if (idleTimer <= IdleDelay) return;
            idleTimer = -IdleExtraWaitMax * Random.value;
            want = CharacterAnimRole.Idle;
            restart = lastWant == CharacterAnimRole.Idle;
        }

        bool TryShout()
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var other = karts[i];
                if (other == kart || other.Eliminated) continue;
                var local = kart.transform.InverseTransformPoint(other.transform.position);
                if (Mathf.Abs(local.z) >= ShoutReachAlong || Mathf.Abs(local.x) > ShoutReachSide) continue;
                if (owner.PlayAction(local.x > 0f ? CharacterAnimRole.ShoutRight : CharacterAnimRole.ShoutLeft) && audio != null)
                    audio.Voice(CharacterVoice.Shout);
                return true;
            }
            return false;
        }
    }
}
