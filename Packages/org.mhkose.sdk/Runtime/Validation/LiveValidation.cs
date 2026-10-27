using System;
using System.Collections.Generic;
using System.Diagnostics;
using MHK.Core;
using MHK.Ghosts;
using MHK.Karts;
using MHK.Online;
using MHK.Race;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MHK.Validation
{
    /// <summary>
    /// Online races with anticheat: wires <see cref="RemoteKartChecks"/> to <see cref="OnlineRace"/>. Remote states go in
    /// as received, hits and item uses exempt their racers for a moment, finished, knocked-out and departed racers stop
    /// being checked, and reports go out as <see cref="CheatReport"/>s. Off in solo races and lobbies without anticheat.
    /// </summary>
    [AddComponentMenu("")]
    public class LiveValidation : MonoBehaviour
    {
        // Hits and item uses move karts in ways the validator does not model (impulses, tunnels, leashes).
        const float HitExemptBefore = 0.5f, HitExemptAfter = 2f;
        const float ItemExemptBefore = 0.25f, ItemExemptAfter = 1.5f;

        OnlineRace online;
        RaceManager race;
        RemoteKartChecks checks;

        void Start()
        {
            online = OnlineRace.Current;
            if (online == null || online.Solo || !online.Anticheat)
            {
                enabled = false;
                return;
            }
            online.RemoteState += OnRemoteState;
            online.Pushed += OnPushed;
            online.RacerLeft += OnRacerLeft;
        }

        void OnDestroy()
        {
            if (online != null)
            {
                online.RemoteState -= OnRemoteState;
                online.Pushed -= OnPushed;
                online.RacerLeft -= OnRacerLeft;
            }
            checks?.LogSummary();
            checks?.Dispose();
            checks = null;
        }

        void OnRemoteState(int slot, KartState state)
        {
            if (slot != online.LocalSlot && Ready()) checks.Receive(slot, state);
        }

        void OnPushed(IRacePush push)
        {
            if (push.RaceId != online.RaceId || !Ready()) return;
            switch (push)
            {
                case HitApplied hit:
                    if (hit.victimSlot != online.LocalSlot) checks.Exempt(hit.victimSlot, hit.tick, HitExemptBefore, HitExemptAfter);
                    break;
                case ItemUsed use:
                    if (use.slot != online.LocalSlot) checks.Exempt(use.slot, use.tick, ItemExemptBefore, ItemExemptAfter);
                    break;
                case Eliminated knockedOut:
                    checks.Stop(knockedOut.slot);
                    break;
                case PlayerFinished finished:
                    checks.Stop(finished.slot);
                    break;
            }
        }

        void OnRacerLeft(int slot)
        {
            if (Ready()) checks.Stop(slot);
        }

        void Update()
        {
            if (checks != null) checks.Process();
        }

        // The track copy is made once the track scene is there and the race's effect table is known (sent with
        // RaceLoaded); states before that (loading) prove nothing.
        bool Ready()
        {
            if (checks != null) return true;
            if (race == null) race = FindAnyObjectByType<RaceManager>();
            if (race == null || race.TrackRuntime == null || online.Loaded == null) return false;
            var cfg = GameRoot.Instance.Config;
            var template = cfg.kartRootPrefab != null ? cfg.kartRootPrefab.GetComponent<KartPhysics>() : null;
            if (template == null) return false;
            var started = Time.realtimeSinceStartup;
            var track = TrackContext.Create(race.TrackRuntime, template);
            checks = new RemoteKartChecks(track, online.EffectIds, race.Laps, 1f / RaceProtocol.TickRate)
            {
                Describe = Describe,
                Eligible = (slot, reason) => online.Anticheat && online.Eligible(online.LocalSlot, slot, reason),
                LocalPosition = LocalPosition,
            };
            checks.Report += OnReport;
            Debug.Log($"[LiveValidation] {race.Track.Id}: {track.ColliderCount} static colliders copied in {(Time.realtimeSinceStartup - started) * 1000f:0} ms.");
            return true;
        }

        // The kart's physics as the race spawned it, in the race's engine class.
        bool Describe(int slot, out RemoteKartChecks.Racer racer)
        {
            racer = default;
            var kart = online.KartOf(slot);
            if (kart == null) return false;
            var cfg = GameRoot.Instance.Config;
            var engineClass = online.Setup != null ? online.Setup.engineClass : (int)Difficulty.Medium;
            var profile = kart.KartDef != null && kart.KartDef.physics != null ? kart.KartDef.physics : cfg.defaultPhysics;
            racer = new RemoteKartChecks.Racer
            {
                name = NameOf(slot),
                spec = KartSpec.ForEngineClass(profile, engineClass, cfg.SpeedScale((Difficulty)engineClass)),
                standIn = UnknownContentGuard.IsPlaceholder(kart.Setup),
            };
            return true;
        }

        bool LocalPosition(out Vector3 position)
        {
            var local = online.KartOf(online.LocalSlot);
            position = local != null ? local.transform.position : default;
            return local != null;
        }

        void OnReport(int slot, Violation v) =>
            online.Notify(new CheatReport { raceId = online.RaceId, suspectSlot = slot, reason = v.reason, tick = v.tick, detail = v.detail });

        string NameOf(int slot)
        {
            var grid = online.Setup != null ? online.Setup.grid : null;
            if (grid != null)
                foreach (var r in grid)
                    if (r.slot == slot && !string.IsNullOrEmpty(r.username)) return r.username;
            return $"Racer {slot + 1}";
        }
    }

    /// <summary>
    /// Plausibility checks of remote karts in a live race: a <see cref="KartValidator"/> in <see cref="KartValidator.Live"/>
    /// mode per racer, fed with the states as received (30 Hz, never the interpolated poses the race shows). A detection
    /// is a strike when it counts (<see cref="Eligible"/>: the observer eligible, the suspect too unless the finding is a
    /// speed or acceleration magnitude) and nothing explains it: not
    /// around a hit or the suspect's own item use (<see cref="Exempt"/>), not in contact with another kart. Strikes of one
    /// reason at least <see cref="StrikeSpacingSeconds"/> apart are separate incidents; three within ten seconds raise
    /// <see cref="Report"/>, then that reason cools down for that racer.
    /// <para>
    /// Re-simulation (<see cref="ValidationChecks.Divergence"/>) runs on the received states too: they carry the keys of
    /// every second tick and the visual wheel angle instead of the steering command, which the window's warm-up absorbs
    /// (a recorded legit run streamed as 30 Hz states with 3 % loss diverged in 0 of 39 windows, at most 0.52 of the
    /// limit). Kart contacts the static copy cannot reproduce hide between samples (the interpolated ticks follow the
    /// observed velocities), so a diverged window is explained by any contact, hit or item use over its whole span.
    /// Lap checks stay off: they are one-off findings at the finish, never three strikes, and the server checks finish
    /// times and lap counts itself.
    /// </para>
    /// </summary>
    public sealed class RemoteKartChecks : IDisposable
    {
        const ValidationChecks Checks = ValidationChecks.Speed | ValidationChecks.Acceleration | ValidationChecks.Teleport
                                        | ValidationChecks.WallClip | ValidationChecks.Flying | ValidationChecks.Data
                                        | ValidationChecks.Divergence;
        /// <summary>Re-simulation work per <see cref="Process"/> (ms); windows wait in the queue beyond it.</summary>
        const double ResimBudgetMs = 0.6;
        /// <summary>Seconds between two windows of one racer, longer with more than this many racers (as for ghosts).</summary>
        const float BaseResimInterval = 3f;
        const int RacersPerResimInterval = 8;
        /// <summary>A re-simulation window's span (s): its warm-up and free run, which a contact anywhere in explains.</summary>
        const float ResimSpanSeconds = 1.5f;
        public const int StrikesToReport = 3;
        public const float StrikeWindowSeconds = 10f;
        /// <summary>A lasting problem (a speed hack over a whole straight) is one strike per this many seconds.</summary>
        public const float StrikeSpacingSeconds = 1f;
        /// <summary>After a report the same reason about the same racer waits this long (the server's kick window is 15 s).</summary>
        public const float CooldownSeconds = 15f;
        // Kart contacts: another kart this close (m) at about the same time explains a push. The local kart runs ahead of
        // received states by the latency: a wider radius.
        const float ContactRadius = 4f, LocalContactRadius = 7f;
        const float ContactExemptSeconds = 0.6f;
        /// <summary>Another remote's latest state counts for contacts when it is at most this much older (s).</summary>
        const float ContactStateAge = 0.1f;

        /// <summary>What a racer drives: its name for logs, its physics, and whether it is a stand-in (kinematic checks only).</summary>
        public struct Racer
        {
            public string name;
            public KartSpec spec;
            public bool standIn;
        }

        public delegate bool DescribeRacer(int slot, out Racer racer);
        public delegate bool PositionOf(out Vector3 position);

        sealed class Strikes
        {
            public readonly List<int> ticks = new();
            public int last = int.MinValue / 2, cooldownUntil = int.MinValue / 2;
        }

        sealed class Suspect
        {
            public int slot;
            public string name;
            public KartValidator validator;
            public readonly List<(int from, int to)> exempt = new();
            public readonly Dictionary<FlagReason, Strikes> strikes = new();
            public int lastTick = -1, lastNear = int.MinValue / 2;
            public Vector3 lastPosition;
            public bool done;
            public int samples, detections, struck, reports, maxGapTicks;
        }

        readonly struct Received
        {
            public readonly Suspect suspect;
            public readonly KartSample sample;
            public readonly bool last;

            public Received(Suspect suspect, in KartSample sample, bool last)
            {
                this.suspect = suspect;
                this.sample = sample;
                this.last = last;
            }
        }

        readonly TrackContext track;
        readonly Resimulator resim;
        readonly EffectSet effects = new(GhostEffectLookup.Find);
        readonly ulong[] effectBits;
        readonly ulong unknownEffectBit;
        readonly float tick;
        readonly int laps;
        readonly Dictionary<int, Suspect> suspects = new();
        readonly List<Received> queue = new();
        readonly Stopwatch watch = new();
        double costTotal, costMax;
        int costFrames;

        /// <summary>Physics and name of a racer, false while its kart is not spawned yet.</summary>
        public DescribeRacer Describe { get; set; }
        /// <summary>Whether a strike of this reason about this racer counts now (anticheat on, <see cref="OnlineRace.Eligible"/>).</summary>
        public Func<int, FlagReason, bool> Eligible { get; set; }
        /// <summary>Where the local kart is, for contacts; false without one.</summary>
        public PositionOf LocalPosition { get; set; }
        /// <summary>A racer reached <see cref="StrikesToReport"/> strikes of one reason within <see cref="StrikeWindowSeconds"/>.</summary>
        public event Action<int, Violation> Report;

        /// <summary>Validation work per <see cref="Process"/> call with work (ms): average and maximum so far.</summary>
        public double AverageCostMs => costFrames > 0 ? costTotal / costFrames : 0;
        public double MaxCostMs => costMax;
        public int CostFrames => costFrames;

        /// <param name="track">Owned from now on (disposed with this).</param>
        /// <param name="effectIds">The race's status effect table (<see cref="RaceLoaded.effects"/>): states name effects by index into it.</param>
        /// <param name="laps">Laps of the race (the lap replay behind the respawn check follows them).</param>
        public RemoteKartChecks(TrackContext track, IReadOnlyList<string> effectIds, int laps, float tickSeconds)
        {
            this.track = track;
            resim = new Resimulator(track);
            this.laps = Mathf.Max(1, laps);
            tick = tickSeconds;
            effectBits = new ulong[effectIds != null ? effectIds.Count : 0];
            for (var i = 0; i < effectBits.Length; i++) effectBits[i] = effects.Bit(effectIds[i]);
            // An index outside the table: an effect nothing is known about (the validator allows more for it).
            unknownEffectBit = effects.Bit("?");
        }

        public void Dispose() => track.Dispose();

        /// <summary>A state of racer <paramref name="slot"/> as received (copied at once: the caller may reuse it).</summary>
        public void Receive(int slot, KartState state)
        {
            var s = Get(slot);
            if (s.done || (s.lastTick >= 0 && state.tick <= s.lastTick)) return;
            if (s.lastTick >= 0) s.maxGapTicks = Mathf.Max(s.maxGapTicks, state.tick - s.lastTick);
            s.lastTick = state.tick;
            var sample = ToSample(state);
            if (Near(s, sample.position)) s.lastNear = state.tick;
            s.lastPosition = sample.position;
            queue.Add(new Received(s, sample, state.Has(KartStateFlags.Finished) || state.Has(KartStateFlags.Eliminated)));
        }

        /// <summary>Detections about racer <paramref name="slot"/> from <paramref name="before"/> s before to <paramref name="after"/> s after <paramref name="atTick"/> are no strikes.</summary>
        public void Exempt(int slot, int atTick, float before, float after)
        {
            if (slot < 0) return;
            Get(slot).exempt.Add((atTick - Ticks(before), atTick + Ticks(after)));
        }

        /// <summary>Racer <paramref name="slot"/> finished, is out or left: no more checks.</summary>
        public void Stop(int slot)
        {
            if (suspects.TryGetValue(slot, out var s)) s.done = true;
        }

        /// <summary>Checks the states received since the last call, then re-simulates within the budget.</summary>
        public void Process()
        {
            watch.Restart();
            var worked = queue.Count > 0;
            foreach (var r in queue)
            {
                var s = r.suspect;
                if (s.done || (s.validator == null && !Begin(s))) continue;
                s.samples++;
                s.validator.Push(r.sample);
                if (r.last) s.done = true;
            }
            queue.Clear();
            while (watch.Elapsed.TotalMilliseconds < ResimBudgetMs && resim.Step()) worked = true;
            if (!worked) return;
            var ms = watch.Elapsed.TotalMilliseconds;
            costTotal += ms;
            costMax = Math.Max(costMax, ms);
            costFrames++;
        }

        public void LogSummary()
        {
            if (costFrames == 0) return;
            Debug.Log($"[LiveValidation] {suspects.Count} remote kart(s), cost per frame {AverageCostMs:0.000} ms average, {costMax:0.000} ms max over {costFrames} frames.");
            foreach (var s in suspects.Values)
                if (s.validator != null)
                    Debug.Log($"[LiveValidation] {s.name} (slot {s.slot}): {s.samples} states, largest gap {s.maxGapTicks} ticks, {s.detections} detection(s), " +
                              $"{s.struck} strike(s), {s.reports} report(s); margins: {s.validator.Margins}");
        }

        Suspect Get(int slot)
        {
            if (!suspects.TryGetValue(slot, out var s)) suspects.Add(slot, s = new Suspect { slot = slot, name = $"Racer {slot + 1}" });
            return s;
        }

        KartSample ToSample(KartState state)
        {
            var flags = KartSampleFlags.None;
            if (state.Has(KartStateFlags.Cut)) flags |= KartSampleFlags.Cut;
            if (state.Has(KartStateFlags.ControlsEnabled)) flags |= KartSampleFlags.ControlsEnabled;
            if (state.Has(KartStateFlags.Finished)) flags |= KartSampleFlags.RaceOver;
            ulong mask = 0;
            foreach (var i in state.effects) mask |= i < effectBits.Length ? effectBits[i] : unknownEffectBit;
            return new KartSample
            {
                tick = state.tick,
                position = state.pos,
                rotation = state.rot,
                velocity = state.vel,
                contacts = state.Has(KartStateFlags.Airborne) ? 0 : -1,
                steerCommand = state.steer,
                keys = (KartKeys)(state.keys & (byte)KartKeys.All),
                flags = flags,
                effects = mask,
            };
        }

        // Another kart close by at about the same time: the latest states of the other remotes, the local kart where it is now.
        bool Near(Suspect s, Vector3 position)
        {
            var maxAge = Ticks(ContactStateAge);
            foreach (var o in suspects.Values)
                if (o != s && !o.done && o.lastTick >= 0 && Mathf.Abs(o.lastTick - s.lastTick) <= maxAge
                    && (o.lastPosition - position).sqrMagnitude < ContactRadius * ContactRadius) return true;
            return LocalPosition != null && LocalPosition(out var local) && (local - position).sqrMagnitude < LocalContactRadius * LocalContactRadius;
        }

        bool Begin(Suspect s)
        {
            if (Describe == null || !Describe(s.slot, out var racer)) return false;
            if (!string.IsNullOrEmpty(racer.name)) s.name = racer.name;
            // A stand-in's physics are not the racer's: only what needs no physics.
            var checks = racer.standIn ? Checks & ValidationChecks.Kinematic : Checks;
            if ((checks & ValidationChecks.Divergence) != 0) track.Prepare(racer.spec);
            s.validator = new KartValidator(track, racer.spec, effects, laps, tick, checks, resim)
            {
                Live = true,
                ResimInterval = BaseResimInterval * Mathf.Max(1f, suspects.Count / (float)RacersPerResimInterval),
            };
            s.validator.Found += v => OnFound(s, v);
            return true;
        }

        void OnFound(Suspect s, Violation v)
        {
            s.detections++;
            if (Eligible != null && !Eligible(s.slot, v.reason)) return;
            if (Explained(s, v)) return;
            if (!s.strikes.TryGetValue(v.reason, out var st)) s.strikes.Add(v.reason, st = new Strikes());
            if (v.tick < st.cooldownUntil || v.tick - st.last < Ticks(StrikeSpacingSeconds)) return;
            st.last = v.tick;
            var oldest = v.tick - Ticks(StrikeWindowSeconds);
            st.ticks.RemoveAll(t => t < oldest);
            st.ticks.Add(v.tick);
            s.struck++;
            Debug.Log($"[LiveValidation] strike {st.ticks.Count}/{StrikesToReport} on {s.name} (slot {s.slot}): {v}");
            if (st.ticks.Count < StrikesToReport) return;
            st.ticks.Clear();
            st.cooldownUntil = v.tick + Ticks(CooldownSeconds);
            s.reports++;
            Debug.Log($"[LiveValidation] reporting {s.name} (slot {s.slot}): {v}");
            Report?.Invoke(s.slot, v);
        }

        // A hit, an own item use or a kart contact around the detection (over the whole window of a re-simulation) explains it.
        bool Explained(Suspect s, in Violation v)
        {
            var from = v.tick - Ticks(v.reason == FlagReason.Divergence ? ResimSpanSeconds : ContactExemptSeconds);
            if (s.lastNear >= from) return true;
            var explained = false;
            var oldest = v.tick - Ticks(StrikeWindowSeconds);
            for (var i = s.exempt.Count - 1; i >= 0; i--)
            {
                var (start, end) = s.exempt[i];
                if (end < oldest) s.exempt.RemoveAt(i);
                else if (start <= v.tick && end >= from) explained = true;
            }
            return explained;
        }

        int Ticks(float seconds) => Mathf.RoundToInt(seconds / tick);
    }
}
