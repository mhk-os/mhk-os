using System.Collections.Generic;
using MHK.AI;
using MHK.Core;
using MHK.Karts;

namespace MHK.Race
{
    /// <summary>
    /// Power start: the first throttle press of each kart during the countdown, judged at GO against
    /// <see cref="GameConfig.powerStartWindow"/>.
    /// </summary>
    sealed class PowerStart
    {
        const float ThrottleThreshold = 0.1f;
        const int SkillHashPrime = 37;
        const float AiSkillShare = 0.8f;
        const float AiPressAt = 1.05f;
        const float FallbackBoost = 5f;

        readonly Dictionary<Kart, float> pressedAt = new();

        public void Clear() => pressedAt.Clear();

        /// <summary>Records the first press of every kart once the countdown is below the ignore threshold.</summary>
        public void Sample(IReadOnlyList<Kart> karts, float countdown, GameConfig cfg, DifficultySettings difficulty)
        {
            if (countdown > cfg.powerStartIgnoreAbove) return;
            foreach (var k in karts)
                if (!pressedAt.ContainsKey(k) && PeekThrottle(k, countdown, difficulty) > ThrottleThreshold)
                    pressedAt[k] = countdown;
        }

        static float PeekThrottle(Kart k, float countdown, DifficultySettings difficulty)
        {
            if (k.Driver == null) return 0f;
            if (k.Driver is AIKartDriver)
            {
                // The original never initialised the AI start mode, here better AIs nail the power start more often.
                var skilled = (k.RacerIndex * SkillHashPrime % 100) / 100f < difficulty.aiPrecision * AiSkillShare;
                return skilled && countdown <= AiPressAt ? 1f : 0f;
            }
            var input = default(KartInput);
            k.Driver.UpdateInput(k, ref input);
            return input.throttle;
        }

        /// <summary>Applies the power start or power fail effect of <paramref name="k"/>, if it pressed.</summary>
        public void Apply(Kart k, GameConfig cfg)
        {
            if (!pressedAt.TryGetValue(k, out var at)) return;
            var good = at >= cfg.powerStartWindow.x && at <= cfg.powerStartWindow.y;
            var effect = good ? cfg.powerStartEffect : cfg.powerFailEffect;
            if (effect != null) k.Effects.Apply(effect, k);
            else if (good) k.Physics.Body.linearVelocity += k.transform.forward * (FallbackBoost * k.Physics.SpeedScale);
        }
    }
}
