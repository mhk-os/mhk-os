using MHK.Karts;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Turns a computer driver's steering, throttle and brake wishes into key presses, tapping keys no faster than
    /// <see cref="Core.GameConfig.aiKeyMinHold"/> so the AI drives with the player's input model.
    /// </summary>
    internal sealed class AIKeys
    {
        // Dead band (share of full lock, of full throttle or brake) within which a key keeps its state.
        const float Tolerance = 0.04f;

        int steerKey;
        bool throttleKey, brakeKey;
        float steerHeld, throttleHeld, brakeHeld;

        /// <summary>Replaces the wished steer, throttle and brake of <paramref name="input"/> with key presses (0 or ±1).</summary>
        public void Press(KartPhysics physics, ref KartInput input, float dt, float minHold)
        {
            steerHeld += dt;
            throttleHeld += dt;
            brakeHeld += dt;

            var steer = SteerKeyFor(Mathf.Clamp(input.steer, -1f, 1f), physics.SteerCommand01, steerKey, ReleaseStep(physics.Profile, minHold));
            if (steer != steerKey && steerHeld >= minHold)
            {
                steerKey = steer;
                steerHeld = 0f;
            }

            // Brake wins over throttle. Each key taps towards the wished amount.
            var brakeWish = Mathf.Clamp01(input.brake);
            var throttleWish = brakeWish > 0f ? 0f : Mathf.Clamp01(input.throttle);
            Tap(ref brakeKey, ref brakeHeld, brakeWish, physics.Reversing ? physics.Throttle : physics.BrakeAmount, minHold);
            Tap(ref throttleKey, ref throttleHeld, throttleWish, physics.Reversing ? physics.BrakeAmount : physics.Throttle, minHold);

            input.steer = steerKey;
            input.brake = brakeKey ? 1f : 0f;
            input.throttle = throttleKey && !brakeKey ? 1f : 0f;
        }

        // +1 = right for both the wish and the commanded wheel position. Releasing centres the wheel fastest, but within one
        // key hold it swings back by releaseStep (share of full lock): smaller cuts tap the other key, as a player holds a
        // partial lock.
        static int SteerKeyFor(float wish, float wheel, int current, float releaseStep)
        {
            if (wheel * wish < 0f && Mathf.Abs(wheel) > Tolerance) return 0;
            if (Mathf.Abs(wish) > Mathf.Abs(wheel) + Tolerance) return wish > 0f ? 1 : -1;
            if (Mathf.Abs(wish) < Mathf.Abs(wheel) - Tolerance)
                return Mathf.Abs(wish) <= Tolerance || Mathf.Abs(wheel) - Mathf.Abs(wish) >= releaseStep ? 0 : wheel > 0f ? -1 : 1;
            return current;
        }

        static float ReleaseStep(KartPhysicsProfile p, float minHold) =>
            p != null && p.maxSteeringAngle > 0f ? p.steeringRestSpeed * minHold / p.maxSteeringAngle : 1f;

        static void Tap(ref bool key, ref float held, float wish, float current, float minHold)
        {
            bool want;
            if (wish <= 0f) want = false;
            else if (wish >= 1f - Tolerance) want = true;
            else if (current < wish - Tolerance) want = true;
            else if (current > wish + Tolerance) want = false;
            else want = key;
            if (want == key || held < minHold) return;
            key = want;
            held = 0f;
        }
    }
}
