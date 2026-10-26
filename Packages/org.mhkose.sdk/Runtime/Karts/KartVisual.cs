using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Visual side of a kart, live or ghost (read through IKartView): poses wheels and body, drives the driver animation
    /// and shows effect visuals. Actions are reported through events so ghost recording can replay them.
    /// </summary>
    public class KartVisual : MonoBehaviour
    {
        const float SteerSmoothingSeconds = 0.15f;
        const float DropLevelRate = 5f;
        const float ScaleFollowRate = 8f;
        const float MinWheelRadius = 0.05f;
        const float FlattenTolerance = 0.01f;

        IKartView view;
        Kart kart;
        KartModel model;
        CharacterAnimator animator;
        CharacterAnimator kartAnimator;
        Renderer[] driverRenderers;
        readonly List<Renderer> visibleScratch = new();
        Quaternion[] frontRest, rearRest, pivotRest;
        KartSeeThrough seeThrough;
        KartWheelVisibility wheelVisibility;
        KartDriverAnimation driverAnimation;
        readonly KartBodyBob bodyBob = new();
        Transform body;
        float wheelRoll;
        float shownWheelAngleDeg, shownSteer;
        float dropTime;

        /// <summary>Wheels and seat of the kart model. Null without a kart prefab or KartModel.</summary>
        public KartModel Model => model;
        /// <summary>Animator of the driver, null without one.</summary>
        public CharacterAnimator DriverAnimator => animator;
        /// <summary>Animator of the kart model, null when it has no animations.</summary>
        public CharacterAnimator KartAnimator => kartAnimator;

        /// <summary>
        /// Child of this visual holding the kart model, the driver, held items and effect attachments, moved by the
        /// visual-only body bob. This visual itself has the pose without it, which is where gameplay reads mount points.
        /// </summary>
        public Transform Body => body;

        /// <summary>An action animation was started (role, hold): by the kart's own logic or <see cref="PlayRecordedAction"/>.</summary>
        public event Action<CharacterAnimRole, bool> ActionPlayed;
        /// <summary>A held action animation was released.</summary>
        public event Action<CharacterAnimRole> ActionReleased;
        /// <summary>Driver and kart model cut to their rest pose (respawn).</summary>
        public event Action PoseSnapped;

        /// <summary>Opacity drawn now (follows <see cref="IKartView.Opacity"/> at a fixed rate).</summary>
        public float ShownOpacity => seeThrough != null ? seeThrough.ShownOpacity : 1f;

        /// <summary>
        /// Top centre of the seated driver as it is drawn now (highest point of its mesh renderers' bounds, horizontally
        /// at their centre), in <see cref="Body"/> space, so items floating above the head fit small and big characters
        /// alike. False when the kart has no visible driver.
        /// </summary>
        public bool TryGetDriverTop(out Vector3 localTop)
        {
            localTop = default;
            if (animator == null) return false;
            driverRenderers ??= animator.GetComponentsInChildren<Renderer>(true);
            var found = false;
            var bounds = default(Bounds);
            foreach (var r in driverRenderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (found) bounds.Encapsulate(r.bounds);
                else bounds = r.bounds;
                found = true;
            }
            if (found) localTop = body.InverseTransformPoint(new Vector3(bounds.center.x, bounds.max.y, bounds.center.z));
            return found;
        }

        /// <summary>
        /// Shows or hides every renderer below this visual on an active GameObject (gathered at each call into a reused
        /// list, so attachments added since the last call are included).
        /// </summary>
        public void SetRenderersVisible(bool visible)
        {
            GetComponentsInChildren(false, visibleScratch);
            foreach (var r in visibleScratch) r.enabled = visible;
            visibleScratch.Clear();
        }

        /// <summary>Builds the visual of a live kart (child "Visual" of the kart).</summary>
        public static KartVisual Build(Kart kart, CharacterDefinition character, KartDefinition kartDef) =>
            Create(kart, kart, character, kartDef);

        /// <summary>
        /// Builds the visual of a replayed kart (a time-trial ghost) as child "Visual" of <paramref name="view"/>: it
        /// shows the recorded state and triggers no action by itself (they are replayed with <see cref="PlayRecordedAction"/>).
        /// </summary>
        public static KartVisual BuildReplay(IKartView view, CharacterDefinition character, KartDefinition kartDef) =>
            Create(view, null, character, kartDef);

        static KartVisual Create(IKartView view, Kart kart, CharacterDefinition character, KartDefinition kartDef)
        {
            var existing = view.transform.Find("Visual");
            if (existing != null) Destroy(existing.gameObject);
            var root = new GameObject("Visual");
            root.transform.SetParent(view.transform, false);
            var visual = root.AddComponent<KartVisual>();
            visual.view = view;
            visual.kart = kart;
            visual.Assemble(character, kartDef);
            return visual;
        }

        void Assemble(CharacterDefinition character, KartDefinition kartDef)
        {
            body = new GameObject("Body").transform;
            body.SetParent(transform, false);
            var rig = KartRigBuilder.Spawn(body, character, kartDef);
            model = rig.model;
            animator = rig.driver;
            kartAnimator = rig.kart;
            if (kart != null && Validation.UnknownContentGuard.IsPlaceholder(kart.Setup)) AddUnknownMarker();

            seeThrough = new KartSeeThrough(transform, view, GetComponentsInChildren<Renderer>());
            wheelVisibility = new KartWheelVisibility(model);
            driverAnimation = new KartDriverAnimation(this, view, kart, animator, kartAnimator, GameRoot.Instance.Config.kartTurnPose);
            if (model != null)
            {
                frontRest = KartRigBuilder.Rests(model.frontWheels);
                rearRest = KartRigBuilder.Rests(model.rearWheels);
                pivotRest = KartRigBuilder.Rests(model.steeringPivots);
            }

            if (kart == null) return;
            kart.Effects.Started += OnEffectApplied;
            kart.Effects.Ended += OnEffectEnded;
            kart.Respawned += OnRespawned;
            kart.Collided += OnCollided;
        }

        // Online: a stand-in for a character or kart this game does not have wears the '?' sign (fading with the kart).
        void AddUnknownMarker()
        {
            var prefab = GameRoot.Instance.Config.unknownContentMarker;
            if (prefab != null) Instantiate(prefab, body, false).name = "Unknown content";
        }

        internal bool PlayAction(CharacterAnimRole role, bool hold = false)
        {
            var a = animator != null && animator.PlayAction(role, hold);
            var b = kartAnimator != null && kartAnimator.PlayAction(role, hold);
            ActionPlayed?.Invoke(role, hold);
            return a || b;
        }

        void Release(CharacterAnimRole role)
        {
            if (animator != null) animator.ReleaseAction(role);
            if (kartAnimator != null) kartAnimator.ReleaseAction(role);
            ActionReleased?.Invoke(role);
        }

        void SnapPose()
        {
            if (animator != null) animator.Snap();
            if (kartAnimator != null) kartAnimator.Snap();
            dropTime = 0f;
            bodyBob.Reset();
            PoseSnapped?.Invoke();
        }

        /// <summary>Replays a recorded action (ghosts): as <see cref="ActionPlayed"/> reported it.</summary>
        public void PlayRecordedAction(CharacterAnimRole role, bool hold) => PlayAction(role, hold);

        /// <summary>Replays a recorded release (ghosts): as <see cref="ActionReleased"/> reported it.</summary>
        public void ReleaseRecordedAction(CharacterAnimRole role) => Release(role);

        /// <summary>Replays a recorded snap to the rest pose (ghosts): as <see cref="PoseSnapped"/> reported it.</summary>
        public void SnapRecordedPose() => SnapPose();

        /// <summary>Real-time length of an action on the driver and the kart model (the longer one), 0 if neither has a clip.</summary>
        public float ActionDuration(CharacterAnimRole role) =>
            Mathf.Max(animator != null ? animator.ActionDuration(role) : 0f, kartAnimator != null ? kartAnimator.ActionDuration(role) : 0f);

        /// <summary>Holds the win or lose pose after the finish.</summary>
        public void PlayWin(bool won) => PlayAction(won ? CharacterAnimRole.Win : CharacterAnimRole.Lose, true);

        /// <summary>
        /// Driver and kart model animate (default) or freeze in their pose, for a ghost too far away to watch
        /// (<see cref="CharacterAnimator.SetDormant"/>). Wheels, pose, opacity and tints stay live.
        /// </summary>
        public void SetAnimated(bool animated)
        {
            if (animator != null) animator.SetDormant(!animated);
            if (kartAnimator != null) kartAnimator.SetDormant(!animated);
        }

        void OnDestroy()
        {
            if (seeThrough != null) seeThrough.ReleaseFadedMaterials();
            if (kart == null) return;
            kart.Effects.Started -= OnEffectApplied;
            kart.Effects.Ended -= OnEffectEnded;
            kart.Respawned -= OnRespawned;
            kart.Collided -= OnCollided;
        }

        // Effect animations hold their last frame while the effect freezes or locks the kart and end with it, else they play once.
        void OnEffectApplied(StatusEffectDefinition def, Kart source)
        {
            if (def.useCharacterAnimation) PlayAction(def.characterAnimation, def.freezeKart || def.lockControls);
        }

        void OnEffectEnded(StatusEffectDefinition def)
        {
            if (def.useCharacterAnimation) Release(def.characterAnimation);
        }

        void OnRespawned(Kart k)
        {
            // Cut to the rest pose instead of blending out of the fall or shot-down pose.
            SnapPose();
            PlayAction(CharacterAnimRole.Respawn);
        }

        void OnCollided(Kart k, Collision c)
        {
            if (driverAnimation.CollisionCoolingDown || !kart.ControlsEnabled) return;
            if (!KartImpact.TryClassify(kart, c, out var impact)) return;
            driverAnimation.StartCollisionCooldown();
            PlayAction(impact.IsScratch ? CharacterAnimRole.Shake : impact.side);
        }

        void LateUpdate()
        {
            if (view == null) return;
            var dt = Time.deltaTime;
            UpdateShownSteering(dt);
            UpdateBodyPose(dt);
            PoseWheels(dt);
            seeThrough.UpdateTint(view.Burned, view.ModelTint, dt);
            seeThrough.UpdateFade(view.Opacity, dt);
            seeThrough.UpdateHidden(view.Hidden);
            wheelVisibility.Update(view.WheelsHidden && !view.RaceOver);
            driverAnimation.Update(dt, shownSteer);
        }

        void UpdateBodyPose(float dt)
        {
            var visualRotation = Quaternion.Euler(0f, view.SpinAngle, 0f);
            // MHK2 drop: the frozen kart's pitch/roll is levelled out over the anim (× (1 − 4·animTime), drop plays ×1.25).
            var lead = driverAnimation.Lead;
            if (lead != null && lead.ActionRole == CharacterAnimRole.Drop)
            {
                dropTime += dt;
                var level = Quaternion.FromToRotation(Vector3.up, view.transform.InverseTransformDirection(Vector3.up));
                visualRotation = Quaternion.Slerp(Quaternion.identity, level, Mathf.Clamp01(DropLevelRate * dropTime)) * visualRotation;
            }
            else dropTime = 0f;
            transform.localRotation = visualRotation;
            var targetScale = Vector3.one * view.ScaleMultiplier;
            targetScale.y *= view.VerticalScale;
            transform.localScale = Vector3.Lerp(transform.localScale, targetScale, Smoothing.Factor(ScaleFollowRate, dt));
            bodyBob.Update(dt, view.BodyBob, view.ForwardSpeed, HitAnimating());
            body.SetLocalPositionAndRotation(bodyBob.Offset, bodyBob.Rotation);
        }

        // The body bob rides over bumps and its own hit's shake, other hits (spins, flattening, shot down) fade it out.
        bool HitAnimating() =>
            view.SpinAngle != 0f || Mathf.Abs(view.VerticalScale - 1f) > FlattenTolerance || PlaysHit(animator) || PlaysHit(kartAnimator);

        static bool PlaysHit(CharacterAnimator a) =>
            a != null && a.ActionActive
                      && a.ActionRole is (CharacterAnimRole.ShotDown or CharacterAnimRole.Drop or CharacterAnimRole.Respawn or CharacterAnimRole.Burning
                          or CharacterAnimRole.Crushed or CharacterAnimRole.PowerFail or CharacterAnimRole.Win or CharacterAnimRole.Lose);

        // Computer drivers show their wheel smoothed so their steering taps do not flick the wheels and pose. Physics unchanged.
        void UpdateShownSteering(float dt)
        {
            var follow = view.IsHuman ? 1f : 1f - Mathf.Exp(-dt / SteerSmoothingSeconds);
            shownWheelAngleDeg = Mathf.Lerp(shownWheelAngleDeg, view.SteerAngle, follow);
            shownSteer = Mathf.Lerp(shownSteer, view.SteerCommand01, follow);
        }

        void PoseWheels(float dt)
        {
            if (model == null) return;
            var radius = Mathf.Max(MinWheelRadius, model.wheelRadius);
            wheelRoll += view.ForwardSpeed / radius * Mathf.Rad2Deg * dt;
            var spin = Quaternion.AngleAxis(wheelRoll, model.wheelSpinAxis);
            var steer = Quaternion.AngleAxis(shownWheelAngleDeg, Vector3.up);
            for (var i = 0; i < model.frontWheels.Length; i++)
                if (model.frontWheels[i] != null) model.frontWheels[i].localRotation = frontRest[i] * steer * spin;
            for (var i = 0; i < model.rearWheels.Length; i++)
                if (model.rearWheels[i] != null) model.rearWheels[i].localRotation = rearRest[i] * spin;
            var pivot = Quaternion.AngleAxis(shownWheelAngleDeg * model.steeringPivotScale, Vector3.up);
            for (var i = 0; i < model.steeringPivots.Length; i++)
                if (model.steeringPivots[i] != null) model.steeringPivots[i].localRotation = pivotRest[i] * pivot;
        }
    }
}
