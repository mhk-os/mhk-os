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

            var steer = SteerKeyFor(Mathf.Clamp(input.steer, -1f, 1f), physics.SteerCommand01, steerKey);
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

        // +1 = right for both the wish and the commanded wheel position. Releasing centres the wheel fastest.
        static int SteerKeyFor(float wish, float wheel, int current)
        {
            if (wheel * wish < 0f && Mathf.Abs(wheel) > Tolerance) return 0;
            if (Mathf.Abs(wish) > Mathf.Abs(wheel) + Tolerance) return wish > 0f ? 1 : -1;
            if (Mathf.Abs(wish) < Mathf.Abs(wheel) - Tolerance) return 0;
            return current;
        }

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
