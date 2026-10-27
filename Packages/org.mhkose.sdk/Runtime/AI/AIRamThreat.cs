using MHK.Core;
using MHK.Karts;
using MHK.Powerups;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Opponents ramming a computer driver: karts with an active effect whose aura is hostile (TINcan ram, Thunder panzer)
    /// on a course that brings them within the aura's reach of this kart in the next <see cref="LookSeconds"/>, both
    /// keeping their velocities. The driving steers out of the way of the soonest one (lane choice, battle headings).
    /// Each ram use is noticed with <see cref="DifficultySettings.aiAvoidRamChance"/>. A protected kart (shield, its own
    /// ram) ignores rammers.
    /// </summary>
    internal sealed class AIRamThreat
    {
        const float LookSeconds = 2f, Margin = 0.5f;
        // Headings are judged at least at this speed (m/s), so a slow kart still finds the way out.
        const float MinDodgeSpeed = 6f;

        readonly DifficultySettings difficulty;
        readonly int rollSeed;
        Vector3 rammerVelocity;

        public AIRamThreat(DifficultySettings difficulty, int rollSeed)
        {
            this.difficulty = difficulty;
            this.rollSeed = rollSeed;
        }

        /// <summary>The opponent that would ram this kart soonest, null for none.</summary>
        public Kart Rammer { get; private set; }
        /// <summary>Centre distance (m) to keep from <see cref="Rammer"/>: its aura's reach plus the kart's half width and a margin.</summary>
        public float Danger { get; private set; }
        /// <summary>Sideways offset (m, + = right of the kart) at which <see cref="Rammer"/> would meet the kart.</summary>
        public float Side { get; private set; }

        /// <summary>Finds this physics step's rammer. <paramref name="halfWidth"/> is the kart's half width.</summary>
        public void Tick(Kart kart, float halfWidth)
        {
            Rammer = null;
            if (difficulty.aiAvoidRamChance <= 0f || kart.Effects.IsProtected) return;
            var p = kart.transform.position;
            var v = kart.Physics.Body.linearVelocity;
            var soonest = LookSeconds;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o)) continue;
                var ram = RamOf(o, out var reach);
                var q = o.transform.position;
                if (ram == null || Mathf.Abs(q.y - p.y) > AILanes.SameLevelHeight) continue;
                var danger = reach + halfWidth + Margin;
                var w = OnlineItems.VelocityOf(o);
                var t = ContactTime(p, v, q, w, danger, soonest);
                if (t > soonest || AIRolls.Once(rollSeed ^ AIRolls.RamSalt, ram) >= difficulty.aiAvoidRamChance) continue;
                soonest = t;
                Rammer = o;
                Danger = danger;
                rammerVelocity = w;
                Side = Vector3.Dot(Flat(q - p + (w - v) * t), Flat(kart.transform.right).normalized);
            }
        }

        /// <summary>
        /// <paramref name="reach"/> (m) along the flat direction <paramref name="dir"/>, cut short where the kart at
        /// <paramref name="from"/>, driving that way at <paramref name="speed"/>, would meet <see cref="Rammer"/>.
        /// </summary>
        public float Along(Vector3 from, Vector3 dir, float speed, float reach)
        {
            if (Rammer == null) return reach;
            speed = Mathf.Max(speed, MinDodgeSpeed);
            var t = ContactTime(from, dir * speed, Rammer.transform.position, rammerVelocity, Danger, LookSeconds);
            return float.IsPositiveInfinity(t) ? reach : Mathf.Min(reach, speed * t);
        }

        // The ram effect running on o (an active effect with a hostile aura) and the aura's reach (m), null for none.
        static KartEffects.ActiveEffect RamOf(Kart o, out float reach)
        {
            reach = 0f;
            KartEffects.ActiveEffect ram = null;
            var active = o.Effects.Active;
            for (var i = 0; i < active.Count; i++)
            {
                var def = active[i].Definition;
                if (def.auraEffect == null || !def.auraEffect.hostile || def.auraRadius <= reach) continue;
                ram = active[i];
                reach = def.auraRadius;
            }
            return ram;
        }

        // Seconds (flat) until points at p and q moving at v and w come within danger of each other, infinity when not
        // within horizon. Already that close: 0 while still closing in, infinity while moving apart.
        static float ContactTime(Vector3 p, Vector3 v, Vector3 q, Vector3 w, float danger, float horizon)
        {
            var d = Flat(q - p);
            var dv = Flat(w - v);
            var b = Vector3.Dot(d, dv);
            var c = d.sqrMagnitude - danger * danger;
            if (c <= 0f) return b < 0f ? 0f : float.PositiveInfinity;
            var a = dv.sqrMagnitude;
            if (b >= 0f || a < 1e-4f) return float.PositiveInfinity;
            var disc = b * b - a * c;
            if (disc < 0f) return float.PositiveInfinity;
            var t = (-b - Mathf.Sqrt(disc)) / a;
            return t <= horizon ? t : float.PositiveInfinity;
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    }
}
