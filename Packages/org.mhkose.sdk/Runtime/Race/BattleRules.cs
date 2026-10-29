using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Powerups;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Battle rules (MHK Thunder mode 3, Last Man Standing there): ramming costs health, karts at 0 health are
    /// eliminated, the round ends when all humans are out or one kart is left. Online each racer's client decides its
    /// own health and elimination (<see cref="OnlineItems"/>): rams only cost the local kart health here, and other
    /// racers are eliminated when the server says so.
    /// </summary>
    public class BattleRules : MonoBehaviour
    {
        RaceManager race;
        // Race tick until which a kart takes no ram damage again.
        readonly Dictionary<Kart, int> ramCooldownUntil = new();
        float roundEndTimer = -1f;

        /// <summary>Karts not eliminated yet.</summary>
        public int AliveCount
        {
            get
            {
                var n = 0;
                if (race != null)
                    for (var i = 0; i < race.Karts.Count; i++)
                        if (!race.Karts[i].Eliminated) n++;
                return n;
            }
        }
        public bool RoundOver { get; private set; }

        public event Action<Kart> KartEliminated;

        public void Begin(RaceManager raceManager)
        {
            race = raceManager;
            foreach (var k in race.Karts) k.Collided += OnCollided;
        }

        void OnDestroy()
        {
            if (race == null) return;
            foreach (var k in race.Karts)
                if (k != null) k.Collided -= OnCollided;
        }

        // Thunder 0x4bf590: A rams B when B lies within the cone in front of a moving A.
        void OnCollided(Kart a, Collision c)
        {
            if (race.Phase != RacePhase.Racing || a.Eliminated) return;
            var b = Kart.FromCollider(c.collider);
            if (b == null || b == a || b.Eliminated || b.IsRemote) return;
            var cfg = GameRoot.Instance.Config;
            if (OnlineItems.VelocityOf(a).magnitude <= cfg.ramMinSpeed * a.Physics.SpeedScale) return;
            var toB = b.transform.position - a.transform.position;
            if (Vector3.Angle(a.transform.forward, toB) > cfg.ramConeDegrees) return;
            var now = RaceClock.Ticks;
            if (ramCooldownUntil.TryGetValue(b, out var until) && now < until) return;
            if (b.Effects.IsProtected) return;
            ramCooldownUntil[b] = now + Mathf.CeilToInt(cfg.ramCooldown / RaceClock.Step);
            b.Effects.LastAttacker = a;
            b.TakeDamage(cfg.ramDamage, a);
        }

        void FixedUpdate()
        {
            if (race == null || race.Phase != RacePhase.Racing || RoundOver) return;
            var karts = race.Karts;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                // Remote racers and mirrored AI karts are out when the server says so (Eliminated).
                if (k.Eliminated || k.Health >= 1 || k.IsRemote || OnlineItems.IsMirror(k)) continue;
                Eliminate(k);
                OnlineItems.SendEliminatedSelf(k);
            }

            int alive = 0, humans = 0, humansOut = 0;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!k.Eliminated) alive++;
                if (!k.IsHuman) continue;
                humans++;
                if (k.Eliminated) humansOut++;
            }
            RoundOver = alive <= 1 || (humans > 0 && humansOut == humans);
        }

        void Eliminate(Kart k)
        {
            k.Eliminate();
            k.Progress.ForceFinish(k.Progress.RaceClock);
            KartEliminated?.Invoke(k);
        }

        /// <summary>Online: the server says <paramref name="k"/> is out (its client reported it, or the server decided).</summary>
        public void EliminateShared(Kart k)
        {
            if (k != null && !k.Eliminated) Eliminate(k);
        }

        /// <summary>
        /// Runs the elimination grace once the round is over. Returns true when it has elapsed and the survivors are
        /// finished, so the race can end.
        /// </summary>
        public bool TickRoundEnd(float dt)
        {
            if (!RoundOver) return false;
            if (roundEndTimer < 0f) roundEndTimer = GameRoot.Instance.Config.eliminationGraceSeconds;
            roundEndTimer -= dt;
            if (roundEndTimer > 0f) return false;
            FinishRound();
            return true;
        }

        // Thunder 0x485f20: survivors finish one second after the round ends.
        void FinishRound()
        {
            foreach (var k in race.Karts)
                if (!k.Eliminated) k.Progress.ForceFinish(k.Progress.RaceClock + 1f);
        }

        /// <summary>Survivors first (most health first), then the eliminated in reverse order of elimination.</summary>
        public int Compare(Kart a, Kart b)
        {
            if (a.Eliminated != b.Eliminated) return a.Eliminated ? 1 : -1;
            if (!a.Eliminated)
            {
                var byHealth = b.Health.CompareTo(a.Health);
                return byHealth != 0 ? byHealth : b.Progress.RaceDistance.CompareTo(a.Progress.RaceDistance);
            }
            return b.Progress.FinishTime.CompareTo(a.Progress.FinishTime);
        }
    }
}
