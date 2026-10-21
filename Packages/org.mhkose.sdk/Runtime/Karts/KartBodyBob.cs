using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Visual-only body bob of a kart, live or ghost (<see cref="StatusEffectDefinition.bodyBob"/>): the pose the
    /// <see cref="KartVisual.Body"/> gets on top of the turn look, faded in and out with the effect.
    /// </summary>
    public sealed class KartBodyBob
    {
        // MHK Extra shows its hop only while the kart drives forwards: standing, braking to a halt and reversing show none.
        const float MinForwardSpeed = 1f;

        BodyBob shown;
        float cycle, weight;

        /// <summary>Local offset of the body (m): the lift and the sideways rock.</summary>
        public Vector3 Offset { get; private set; }

        /// <summary>Local rotation of the body: the pitch and roll wobble.</summary>
        public Quaternion Rotation { get; private set; } = Quaternion.identity;

        /// <summary>
        /// Advances by <paramref name="dt"/> real seconds with the strongest bob of the active effects
        /// (<paramref name="active"/>, null when none). The bob fades out when it ends, at <paramref name="forwardSpeed"/>
        /// below walking pace and while <paramref name="hitAnimating"/> (another hit animates the chassis).
        /// </summary>
        public void Update(float dt, BodyBob active, float forwardSpeed, bool hitAnimating)
        {
            if (active != null) shown = active;
            if (shown == null || dt <= 0f) return;
            var target = active != null && forwardSpeed > MinForwardSpeed && !hitAnimating ? 1f : 0f;
            var fade = target > weight ? shown.fadeIn : shown.fadeOut;
            weight = fade > 0f ? Mathf.MoveTowards(weight, target, dt / fade) : target;
            if (weight <= 0f && active == null)
            {
                Reset();
                return;
            }
            cycle += dt * shown.frequency;
            cycle -= Mathf.Floor(cycle);
            Pose(Mathf.SmoothStep(0f, 1f, weight));
        }

        /// <summary>Drops the bob at once (respawns).</summary>
        public void Reset()
        {
            shown = null;
            cycle = weight = 0f;
            Offset = Vector3.zero;
            Rotation = Quaternion.identity;
        }

        void Pose(float w)
        {
            var angle = 2f * Mathf.PI * cycle;
            Offset = w * new Vector3(shown.sway * Mathf.Sin(angle), shown.height * shown.Lift(cycle), 0f);
            Rotation = Quaternion.Euler(w * shown.pitch * Mathf.Sin(angle), 0f, w * shown.roll * Mathf.Cos(angle));
        }
    }
}
