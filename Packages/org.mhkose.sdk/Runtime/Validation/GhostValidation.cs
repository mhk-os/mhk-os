using System;
using System.Collections.Generic;
using System.Diagnostics;
using MHK.Core;
using MHK.Ghosts;
using MHK.Karts;
using MHK.Modding;
using MHK.Online;
using MHK.Race;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MHK.Validation
{
    /// <summary>
    /// Time trials: validates every ghost of the race or replay (<see cref="TimeTrialRun.Ghosts"/>) with a
    /// <see cref="KartValidator"/>, a few seconds ahead of its playback and within a per-frame time budget, flags each
    /// problem on the ghost (<see cref="GhostPlayer.Flag"/>) when the playback reaches it, and reports downloaded runs of
    /// other players to the server once per reason.
    /// </summary>
    [AddComponentMenu("")]
    public class GhostValidation : MonoBehaviour
    {
        /// <summary>Validation work per frame (ms); one item (a few samples or one re-simulated tick) may run over it.</summary>
        const double BudgetMs = 1.2;
        /// <summary>Validation work done at once while the race loads (ms).</summary>
        const double LoadBudgetMs = 50;
        /// <summary>Validation tries to stay this far ahead of each ghost's playback (s).</summary>
        const float LeadSeconds = 4f;
        const float BaseResimInterval = 3f;
        const int GhostsPerResimInterval = 8;

        sealed class Entry
        {
            public GhostPlayer ghost;
            public GhostData data;
            public KartValidator validator;
            public int next, nextEvent;
            public readonly int[] effectCounts = new int[64];
            public ulong effects;
            public readonly List<Violation> due = new();
            public long runId;
            public string uploader;
            public bool done, logged;
            public float Validated => next * data.header.tickSeconds;
        }

        RaceManager race;
        TimeTrialRun run;
        TrackContext track;
        Resimulator resim;
        EffectSet effects;
        readonly List<Entry> entries = new();
        readonly Stopwatch watch = new();
        double costTotal, costMax;
        int costFrames;

        void Start()
        {
            race = GetComponent<RaceManager>();
            run = race != null ? race.TimeTrial : null;
            if (run == null)
            {
                enabled = false;
                return;
            }
            if (run.GhostsLoaded) Begin();
            else run.GhostsReady += Begin;
        }

        void OnDestroy()
        {
            if (run != null) run.GhostsReady -= Begin;
            if (costFrames > 0)
                Debug.Log($"[Validation] {entries.Count} ghost(s), cost per frame {costTotal / costFrames:0.000} ms average, {costMax:0.000} ms max over {costFrames} frames.");
            track?.Dispose();
            track = null;
        }

        void Begin()
        {
            run.GhostsReady -= Begin;
            var ghosts = run.Ghosts;
            if (ghosts.Count == 0 || race.TrackRuntime == null) return;
            var root = GameRoot.Instance;
            var template = root.Config.kartRootPrefab != null ? root.Config.kartRootPrefab.GetComponent<KartPhysics>() : null;
            if (template == null) return;
            var started = Time.realtimeSinceStartup;
            track = TrackContext.Create(race.TrackRuntime, template);
            resim = new Resimulator(track);
            effects = new EffectSet(GhostEffectLookup.Find);
            var interval = BaseResimInterval * Mathf.Max(1f, ghosts.Count / (float)GhostsPerResimInterval);
            foreach (var ghost in ghosts)
                if (ghost != null && ghost.Data != null && ghost.Data.frames.Length > 1) Add(ghost, interval);
            // A head start while the race still loads, which also compiles every check before the first raced frame.
            if (entries.Count > 0)
            {
                watch.Restart();
                Work(LoadBudgetMs);
            }
            Debug.Log($"[Validation] {race.Track.Id}: {entries.Count} ghost(s), {track.ColliderCount} static colliders copied, " +
                      $"ready in {(Time.realtimeSinceStartup - started) * 1000f:0} ms.");
        }

        void Add(GhostPlayer ghost, float resimInterval)
        {
            var data = ghost.Data;
            var h = data.header;
            var checks = GhostCompatibilityRules.Evaluate(h, run.TrackHash) == GhostCompatibility.Current ? ValidationChecks.All : ValidationChecks.Kinematic;
            if (!Mathf.Approximately(h.tickSeconds, Time.fixedDeltaTime)) checks &= ~ValidationChecks.Divergence;
            var spec = KartSpec.ForEngineClass(ProfileOf(h), h.engineClass, h.speedScale);
            if ((checks & ValidationChecks.Divergence) != 0) track.Prepare(spec);
            var validator = new KartValidator(track, spec, effects, h.laps, h.tickSeconds, checks, resim) { ResimInterval = resimInterval };
            if (h.lapTimes.Length == h.laps)
                for (var lap = 0; lap <= h.laps; lap++) validator.ClaimLap(lap, h.LapEndTime(lap));
            var e = new Entry { ghost = ghost, data = data, validator = validator };
            // Downloaded ghosts carry a run id; local ones are never reported.
            if (OnlineGhostCache.TryGet(h.id, out var info))
            {
                e.runId = info.runId;
                e.uploader = info.username;
            }
            validator.Found += v => OnFound(e, v);
            entries.Add(e);
        }

        static KartPhysicsProfile ProfileOf(GhostHeader h)
        {
            var root = GameRoot.Instance;
            var character = root.Content.Get<CharacterDefinition>(h.characterId);
            if (character == null)
            {
                var all = root.Content.All<CharacterDefinition>();
                character = all.Count > 0 ? all[0] : null;
            }
            var kart = root.Content.ResolveKart(h.kartId, character);
            return kart != null && kart.physics != null ? kart.physics : root.Config.defaultPhysics;
        }

        // The player's own uploads are never reported.
        void OnFound(Entry e, Violation v)
        {
            Debug.Log($"[Validation] {e.data.header.playerName} ({e.data.header.id}): {v}");
            e.due.Add(v);
            var me = OnlineService.Account;
            if (e.runId != 0 && me != null && !string.Equals(me.username, e.uploader, StringComparison.OrdinalIgnoreCase))
                _ = OnlineTimeTrials.ReportRun(e.runId, v.reason, v.time, v.detail);
        }

        void Update()
        {
            if (entries.Count == 0) return;
            watch.Restart();
            Work(BudgetMs);
            var ms = watch.Elapsed.TotalMilliseconds;
            costTotal += ms;
            costMax = Math.Max(costMax, ms);
            costFrames++;
            ShowDue();
        }

        // Ghosts that fall behind their lead first, then re-simulation, then more lead for everyone.
        void Work(double budgetMs)
        {
            while (watch.Elapsed.TotalMilliseconds < budgetMs)
            {
                var e = Laggard();
                if (e != null && e.Validated - e.ghost.ShownTime < LeadSeconds)
                {
                    Advance(e);
                    continue;
                }
                if (resim.Step()) continue;
                if (e == null) return;
                Advance(e);
            }
        }

        Entry Laggard()
        {
            Entry best = null;
            var bestLead = float.MaxValue;
            foreach (var e in entries)
            {
                if (e.done || e.ghost == null) continue;
                var lead = e.Validated - e.ghost.ShownTime;
                if (lead < bestLead)
                {
                    bestLead = lead;
                    best = e;
                }
            }
            return best;
        }

        // A few ticks at a time, so the budget check stays fine-grained.
        void Advance(Entry e)
        {
            const int Chunk = 8;
            var frames = e.data.frames;
            for (var n = 0; n < Chunk && e.next < frames.Length; n++, e.next++) e.validator.Push(Sample(e, e.next));
            if (e.next < frames.Length) return;
            e.validator.Finish();
            e.done = true;
        }

        KartSample Sample(Entry e, int i)
        {
            var f = e.data.frames[i];
            var time = i * e.data.header.tickSeconds;
            var events = e.data.events;
            while (e.nextEvent < events.Count && events[e.nextEvent].time <= time + 1e-4f) Apply(e, events[e.nextEvent++]);
            var flags = KartSampleFlags.None;
            if (f.Has(GhostFrameFlags.Cut)) flags |= KartSampleFlags.Cut;
            if (f.Has(GhostFrameFlags.ControlsEnabled)) flags |= KartSampleFlags.ControlsEnabled;
            if (f.Has(GhostFrameFlags.RaceOver)) flags |= KartSampleFlags.RaceOver;
            return new KartSample
            {
                tick = i,
                position = f.position,
                rotation = f.rotation,
                velocity = f.velocity,
                contacts = f.contacts,
                steerCommand = f.steerCommand,
                keys = (KartKeys)((int)f.keys & (int)KartKeys.All),
                flags = flags,
                effects = e.effects,
            };
        }

        // Effects as the ghost replays them: a refreshing effect counts once, stacking ones per application.
        void Apply(Entry e, in GhostEvent ev)
        {
            if (ev.kind is not (GhostEventKind.EffectApplied or GhostEventKind.EffectEnded)) return;
            var bit = effects.Bit(ev.effectId);
            if (bit == 0) return;
            var index = 0;
            while (bit >> (index + 1) != 0) index++;
            if (ev.kind == GhostEventKind.EffectEnded) e.effectCounts[index] = Math.Max(0, e.effectCounts[index] - 1);
            else
            {
                var def = GhostEffectLookup.Find(ev.effectId);
                if (def == null || !def.refreshOnReapply || e.effectCounts[index] == 0) e.effectCounts[index]++;
            }
            if (e.effectCounts[index] > 0) e.effects |= bit;
            else e.effects &= ~bit;
        }

        void ShowDue()
        {
            foreach (var e in entries)
            {
                if (e.ghost == null) continue;
                for (var i = 0; i < e.due.Count; i++)
                {
                    if (e.due[i].time > e.ghost.ShownTime) continue;
                    e.ghost.Flag(Message(e.due[i].reason));
                    e.due.RemoveAt(i--);
                }
                if (!e.done || e.logged || e.validator.PendingWindows > 0) continue;
                e.logged = true;
                Debug.Log($"[Validation] {e.data.header.playerName} ({e.data.header.id}) done, {e.validator.Violations.Count} problem(s); margins: {e.validator.Margins}");
            }
        }

        static string Message(FlagReason reason) => reason switch
        {
            FlagReason.Speed => "Impossible speed",
            FlagReason.Acceleration => "Impossible acceleration",
            FlagReason.Teleport => "Teleported",
            FlagReason.WallClip => "Drove through a wall",
            FlagReason.Flying => "Flew through the air",
            FlagReason.Checkpoints => "Skipped a checkpoint",
            FlagReason.LapTimes => "Lap times don't match",
            FlagReason.Divergence => "Inputs don't match the run",
            FlagReason.Data => "Broken recording",
            _ => "Suspicious run",
        };
    }
}
