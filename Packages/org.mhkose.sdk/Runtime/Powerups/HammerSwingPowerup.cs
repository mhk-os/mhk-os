using MHK.Karts;
using MHK.Modding;
using UnityEngine;
using UnityEngine.Serialization;

namespace MHK.Powerups
{
    /// <summary>
    /// Melee item (MHK Extra Holzhammer): the held hammer swings down, turning and reaching towards the nearest opponent
    /// within <see cref="radius"/>. At <see cref="strikeTime"/> the target and karts near the head are bashed. The root
    /// sits at the held mount, <see cref="model"/> is the animated model pivoting at the kart centre.
    /// </summary>
    public class HammerSwingPowerup : PowerupBehaviour
    {
        const float ReturnYawTolerance = 1f;
        const float ReturnReachTolerance = 0.02f;
        const float MaxOvertime = 1f;
        const float MinAimSqr = 0.01f;

        [Header("Model")]
        [SerializeField, FormerlySerializedAs("addOn")] HeldItemAnimation model;
        [Tooltip("Clip time at which the head hits the ground (s).")]
        [SerializeField] float strikeTime = 0.38f;
        [Tooltip("Speed of the whole swing: the clip, the strike time, the rest and the aim all run this many times faster.")]
        [SerializeField, Min(0.01f)] float playbackSpeed = 1f;
        [Tooltip("Head position at the strike in the model's space (m). Its horizontal distance is the unadjusted reach.")]
        [SerializeField] Vector3 strikePoint = new(0f, 0.3f, 1.2f);
        [Tooltip("How much closer than the unadjusted reach the hammer may pull in (m).")]
        [SerializeField] float minReachAdjust = -0.6f;
        [Tooltip("Turn rate of the aim (degrees per second). The model turns back to straight ahead after the strike.")]
        [SerializeField] float aimTurnRate = 1080f;
        [Tooltip("Speed at which the hammer reaches out / pulls in (m/s).")]
        [SerializeField] float reachSpeed = 12f;
        [Tooltip("The head stays where it struck this long before the hammer turns back (s).")]
        [SerializeField] float restAfterStrike = 0.12f;

        [Header("Hit")]
        [Tooltip("The nearest kart within this horizontal distance of the user is the target (Extra: 75 units = 3.2 m).")]
        public float radius = 3.2f;
        [Tooltip("Maximum height difference to a target (m).")]
        [SerializeField] float maxHeightDifference = 2.5f;
        [Tooltip("Other karts this close to the head at the strike are bashed too (m).")]
        [SerializeField] float splashRadius = 1f;
        [SerializeField] StatusEffectDefinition effect;
        [Tooltip("Played at the strike when at least one kart was bashed (the effect's applySound plays as well).")]
        [SerializeField] AudioClip hitSound;
        [Tooltip("Played at the strike when no kart was bashed, including when every kart in reach refused the effect " +
                 "(shielded, already flattened, shot down) (Extra [PowerUp_Hammer_Fail]).")]
        [SerializeField] AudioClip missSound;
        [Tooltip("Spawned where the head hits the ground.")]
        [SerializeField] GameObject impactVfx;

        Kart target;
        Vector3 basePosition;
        float yaw, reach, t;
        bool struck;

        protected override void OnLaunch()
        {
            transform.SetParent(KartMounts.Frame(Owner), false);
            transform.SetLocalPositionAndRotation(KartMounts.Point(Owner, HeldMountOr(HeldMount.Front)), Quaternion.identity);
            if (model != null)
            {
                basePosition = model.transform.localPosition;
                model.Sample(0f);
            }
            if (Owner.Inventory != null) Owner.Inventory.HideHeldVisual(model != null ? model.Length / playbackSpeed : 0f);
            target = Context.targetGiven ? Context.target : FindTarget();
            ChosenTarget = target;
        }

        Kart FindTarget()
        {
            Kart best = null;
            var bestDist = radius * radius;
            var origin = Owner.transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (k == null || k == Owner || k.Eliminated) continue;
                var d = k.transform.position - origin;
                if (Mathf.Abs(d.y) > maxHeightDifference) continue;
                d.y = 0f;
                if (d.sqrMagnitude > bestDist) continue;
                bestDist = d.sqrMagnitude;
                best = k;
            }
            return best;
        }

        protected override void Step(float dt)
        {
            if (Owner == null || model == null)
            {
                Remove();
                return;
            }
            dt *= playbackSpeed;
            t += dt;
            DesiredAim(out var desiredYaw, out var desiredReach);
            ApplyAim(desiredYaw, desiredReach, dt);
            if (!struck && t >= strikeTime)
            {
                model.Sample(t);
                Strike();
            }
            if (Returned || t > model.Length + MaxOvertime) Remove();
        }

        // The swing is drawn at the frame's time between steps, so the clip plays smoothly at any frame rate.
        void Update()
        {
            if (model != null) model.Sample(t + PoseInterpolation.StepShare * Time.fixedDeltaTime * playbackSpeed);
        }

        bool Returned => t >= model.Length && Mathf.Abs(yaw) < ReturnYawTolerance && Mathf.Abs(reach) < ReturnReachTolerance;

        // Aim at the target until the strike, keep the head on the spot while it rests, then turn back to the held pose.
        void DesiredAim(out float desiredYaw, out float desiredReach)
        {
            desiredYaw = 0f;
            desiredReach = 0f;
            if (struck && t < strikeTime + restAfterStrike)
            {
                desiredYaw = yaw;
                desiredReach = reach;
                return;
            }
            if (struck || target == null || !target.isActiveAndEnabled) return;
            var pivot = transform.TransformPoint(basePosition);
            var local = transform.InverseTransformVector(target.transform.position - pivot);
            local.y = 0f;
            if (local.sqrMagnitude <= MinAimSqr) return;
            desiredYaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            // Unadjusted reach of the (possibly scaled) model, in this root's space like `local`.
            var unadjusted = new Vector2(strikePoint.x, strikePoint.z).magnitude * model.transform.localScale.z;
            desiredReach = Mathf.Clamp(local.magnitude - unadjusted, minReachAdjust, Mathf.Max(0f, radius - unadjusted));
        }

        void ApplyAim(float desiredYaw, float desiredReach, float dt)
        {
            yaw = Mathf.MoveTowardsAngle(yaw, desiredYaw, aimTurnRate * dt);
            reach = Mathf.MoveTowards(reach, desiredReach, reachSpeed * dt);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            model.transform.SetLocalPositionAndRotation(basePosition + rot * new Vector3(0f, 0f, reach), rot);
        }

        void Strike()
        {
            struck = true;
            var head = model.transform.TransformPoint(strikePoint);
            var origin = Owner.transform.position;
            var hitAny = false;
            for (var i = Kart.All.Count - 1; i >= 0; i--)
            {
                var k = Kart.All[i];
                if (k == null || k == Owner || k.Eliminated) continue;
                var fromOwner = k.transform.position - origin;
                var fromHead = k.transform.position - head;
                fromOwner.y = 0f;
                fromHead.y = 0f;
                var isTarget = k == target && fromOwner.sqrMagnitude <= radius * radius;
                if (!isTarget && fromHead.sqrMagnitude > splashRadius * splashRadius) continue;
                if (Mathf.Abs(k.transform.position.y - head.y) > maxHeightDifference) continue;
                // A refused effect (shield, already flattened or shot down) counts as a miss.
                if (HitKart(k, effect)) hitAny = true;
            }
            PowerupFx.Spawn(impactVfx, head, hitAny ? hitSound : missSound, soundVolume);
        }
    }
}
