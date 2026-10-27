using System.Collections.Generic;
using MHK.Audio;
using MHK.Karts;
using MHK.Modding;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Pool;

namespace MHK.Powerups
{
    public static partial class OnlineItems
    {
        /// <summary>Physics steps between two position reports of a live projectile (0.5 s).</summary>
        const int ReportTicks = RaceProtocol.TickRate / 2;
        /// <summary>A use is caught up at most this many steps (2 s): an older one starts where it is.</summary>
        const int MaxFastForwardTicks = 2 * RaceProtocol.TickRate;
        /// <summary>Content kind of powerups in pack manifests and UnknownContent.</summary>
        const int PowerupContentKind = 4;
        /// <summary>
        /// Extra distance (m) beyond a part's own hit reach within which it counts as the part that hit a remote racer:
        /// that kart is drawn ~100 ms behind its client, where the hit was decided.
        /// </summary>
        const float NetHitSlack = 4f;
        const float MinQuaternionLength = 1e-4f;

        /// <summary>Everything one use spawned, in spawn order (a burst's shots, scattered hazards).</summary>
        sealed class Use
        {
            public readonly List<PowerupBehaviour> parts = new();
            public bool local;
            public bool hadProjectile;
            public int nextReport;
        }

        [AutoStaticsCleanup] static readonly Dictionary<int, Use> uses = new();
        [AutoStaticsCleanup] static readonly List<int> endedUses = new();
        [AutoStaticsCleanup] static readonly HashSet<int> hitsTaken = new();
        [AutoStaticsCleanup] static readonly HashSet<long> hitsSeen = new();
        [AutoStaticsCleanup] static readonly HashSet<string> unknownPowerups = new();
        [AutoStaticsCleanup] static int usesMade;

        static void ResetUses()
        {
            uses.Clear();
            endedUses.Clear();
            hitsTaken.Clear();
            hitsSeen.Clear();
            unknownPowerups.Clear();
            usesMade = 0;
        }

        /// <summary>Id of the local racer's next use: (slot + 1) &lt;&lt; 24 | n, unique without a round trip.</summary>
        internal static int NextUseId() => ItemUse.UseId(race.LocalSlot, ++usesMade);

        /// <summary>Records a behaviour spawned for a use before it launches, so the use can be reported, corrected and caught up.</summary>
        internal static void Track(PowerupBehaviour b, in PowerupContext ctx)
        {
            if (!Active || ctx.usage == 0) return;
            if (!uses.TryGetValue(ctx.usage, out var use))
            {
                use = new Use { local = ctx.owner != null && !ctx.owner.IsRemote, nextReport = ctx.tick + ReportTicks };
                uses.Add(ctx.usage, use);
            }
            use.parts.Add(b);
            if (b is INetProjectile) use.hadProjectile = true;
        }

        /// <summary>
        /// Announces the local racer's use <paramref name="useId"/> of <paramref name="def"/> from the pose it was spawned
        /// at, with the target its behaviours chose (<see cref="PowerupBehaviour.ChosenTarget"/>).
        /// </summary>
        internal static void SendUse(int useId, Vector3 pos, Quaternion rot, bool backwards, GameObject spawned)
        {
            Kart target = null;
            if (spawned != null)
                using (ListPool<PowerupBehaviour>.Get(out var behaviours))
                {
                    spawned.GetComponents(behaviours);
                    foreach (var b in behaviours)
                        if (b.ChosenTarget != null)
                        {
                            target = b.ChosenTarget;
                            break;
                        }
                }
            race.Notify(new ItemUse
            {
                raceId = race.RaceId, useId = useId, tick = race.Tick, pos = pos, rot = rot, backwards = backwards,
                targetSlot = race.SlotOf(target),
            });
        }

        // Another racer's use: spawned from the pose it was used at (the stand-in is drawn ~100 ms behind) with the
        // sender's target, then simulated up to now.
        static void SpawnRemote(ItemUsed m)
        {
            var owner = race.KartOf(m.slot);
            var def = Content.Get<PowerupDefinition>(m.powerupId);
            if (def == null && unknownPowerups.Add(m.powerupId))
                race.Notify(new UnknownContent { raceId = race.RaceId, slot = m.slot, kind = PowerupContentKind, id = m.powerupId });
            if (owner == null || def == null || def.usePrefab == null || uses.ContainsKey(m.useId)) return;
            // A malformed pose (zero or non-finite quaternion, non-finite position) spawns nothing.
            if (!Finite(m.pos) || !TryNormalize(m.rot, out var rot)) return;
            var target = m.targetSlot >= 0 ? race.KartOf(m.targetSlot) : null;
            var ctx = new PowerupContext(owner, def, m.backwards, m.useId, m.tick, target, true);
            // Launches read the owner's transform: it stands at the use pose while the use spawns.
            var t = owner.transform;
            t.GetPositionAndRotation(out var shownPosition, out var shownRotation);
            t.SetPositionAndRotation(m.pos, rot);
            try
            {
                PowerupFx.SpawnAndLaunch(def.usePrefab, m.pos, rot, ctx);
            }
            finally
            {
                t.SetPositionAndRotation(shownPosition, shownRotation);
            }
            AudioManager.PlaySfx(def.useSound, t);
            FastForward(m.useId, race.Tick - m.tick);
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        static bool TryNormalize(Quaternion q, out Quaternion unit)
        {
            unit = Quaternion.identity;
            if (!float.IsFinite(q.x) || !float.IsFinite(q.y) || !float.IsFinite(q.z) || !float.IsFinite(q.w)) return false;
            var length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (length < MinQuaternionLength) return false;
            unit = new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
            return true;
        }

        // Steps every part of the use once per missed tick. Parts spawned meanwhile (burst shots) join from the next one.
        static void FastForward(int useId, int ticks)
        {
            if (ticks <= 0 || !uses.TryGetValue(useId, out var use)) return;
            ticks = Mathf.Min(ticks, MaxFastForwardTicks);
            for (var t = 0; t < ticks; t++)
            {
                var n = use.parts.Count;
                for (var i = 0; i < n; i++)
                {
                    var b = use.parts[i];
                    if (b != null && b.isActiveAndEnabled && b.FastForwards) b.StepOnce();
                }
            }
        }

        // The server refused the use (not held, cooldown): whatever it spawned or applied goes.
        static void RemoveUse(int useId)
        {
            if (uses.TryGetValue(useId, out var use))
            {
                foreach (var b in use.parts)
                    if (b != null) b.Discard();
                uses.Remove(useId);
            }
            var k = LocalKart;
            if (k != null) k.Effects.RemoveUsage(useId);
        }

        static INetProjectile FirstFlying(Use use)
        {
            foreach (var b in use.parts)
                if (Alive(b) && b is INetProjectile p && p.Flying) return p;
            return null;
        }

        static bool Alive(PowerupBehaviour b) => b != null && b.gameObject.activeSelf;

        static void Correct(ProjectileCorrect m)
        {
            if (!uses.TryGetValue(m.useId, out var use) || !Finite(m.pos) || !Finite(m.vel)) return;
            var p = FirstFlying(use);
            if (p == null) return;
            var ahead = Mathf.Max(0, race.Tick - m.tick) * Time.fixedDeltaTime;
            var position = m.pos + m.vel * ahead;
            if (Finite(position)) p.Correct(position, m.vel);
        }

        /// <summary>Reports live projectiles every 0.5 s and announces the end of the local racer's projectiles. Once per step.</summary>
        internal static void Tick()
        {
            if (!Active) return;
            var now = race.Tick;
            endedUses.Clear();
            foreach (var pair in uses)
            {
                var use = pair.Value;
                for (var i = use.parts.Count - 1; i >= 0; i--)
                    if (!Alive(use.parts[i])) use.parts.RemoveAt(i);
                if (use.parts.Count == 0)
                {
                    if (use.local && use.hadProjectile) race.Notify(new ProjectileGone { raceId = race.RaceId, useId = pair.Key, tick = now });
                    endedUses.Add(pair.Key);
                    continue;
                }
                if (now < use.nextReport) continue;
                use.nextReport = now + ReportTicks;
                var p = FirstFlying(use);
                if (p == null) continue;
                var pos = p.NetPosition;
                var vel = p.NetVelocity;
                if (Finite(pos) && Finite(vel)) race.QueueProjectileReport(new ProjectileReport { useId = pair.Key, tick = now, pos = pos, vel = vel });
            }
            foreach (var id in endedUses) uses.Remove(id);
        }

        // ---- Hits -------------------------------------------------------------------------------------------------

        /// <summary>
        /// A hostile effect landed on <paramref name="victim"/> here. On the local racer's kart: reported to the server
        /// (HitTaken), once per use, hazards once per new effect.
        /// </summary>
        internal static void OnLocalHit(Kart victim, StatusEffectDefinition def, Kart source, int usage, bool started)
        {
            if (!IsLocal(victim)) return;
            if (usage != 0 ? !hitsTaken.Add(usage) : !started) return;
            SendHitTaken(AttackerSlot(victim, source, usage), usage, def.Id);
        }

        // A use's attacker is its owner, also when it hits the owner's own kart; hazards and falls (use 0) have none.
        static int AttackerSlot(Kart victim, Kart source, int usage) =>
            source != null && (usage != 0 || source != victim) ? race.SlotOf(source) : RaceProtocol.NoSlot;

        static void SendHitTaken(int attackerSlot, int usage, string effectId) =>
            race.Notify(new HitTaken { raceId = race.RaceId, attackerSlot = attackerSlot, useId = usage, effectId = effectId, tick = race.Tick });

        /// <summary>A use hit a remote racer here: reported as seen (HitSeen) once per victim and use; its client decides.</summary>
        internal static void OnRemoteHit(Kart victim, StatusEffectDefinition def, Kart source, int usage)
        {
            if (!Active || usage == 0) return;
            var slot = race.SlotOf(victim);
            if (slot < 0 || !hitsSeen.Add((long)slot << 32 | (uint)usage)) return;
            race.Notify(new HitSeen
            {
                raceId = race.RaceId, victimSlot = slot, attackerSlot = AttackerSlot(victim, source, usage), useId = usage, effectId = def.Id, tick = race.Tick,
            });
        }

        // Another racer took a hit: its reaction shows, and the one part of the use that can have made it (the nearest
        // within its reach of the victim) is spent here too; the other bananas of a rain or bubbles of a burst stay.
        static void OnHitApplied(HitApplied m)
        {
            var victim = race.KartOf(m.victimSlot);
            if (victim == null || !victim.IsRemote) return;
            var def = Content.Get<StatusEffectDefinition>(m.effectId);
            if (def != null) victim.Effects.ShowRemote(def, race.KartOf(m.attackerSlot), -1f, m.useId);
            if (m.useId == 0 || !uses.TryGetValue(m.useId, out var use)) return;
            var at = victim.transform.position;
            PowerupBehaviour hitter = null;
            var best = float.PositiveInfinity;
            foreach (var b in use.parts)
            {
                if (!Alive(b)) continue;
                var reach = b.NetHitReach + NetHitSlack;
                var d = (b.NetHitPosition - at).sqrMagnitude;
                if (b.NetHitReach <= 0f || d > reach * reach || d >= best) continue;
                best = d;
                hitter = b;
            }
            if (hitter != null) hitter.OnNetHit(victim);
        }

        // Enough observers saw a hit on the local kart that it did not report: apply it and confirm.
        static void OnHitForce(HitForce m)
        {
            var k = LocalKart;
            if (k == null) return;
            if (m.useId != 0) hitsTaken.Add(m.useId);
            var def = Content.Get<StatusEffectDefinition>(m.effectId);
            if (def != null) KartReactions.Apply(k, def, race.KartOf(m.attackerSlot), -1f, m.useId);
            SendHitTaken(m.attackerSlot, m.useId, m.effectId);
        }
    }
}
