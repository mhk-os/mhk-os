using System.Collections.Generic;
using System.Text;
using MHK.Ghosts;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Online
{
    public sealed partial class OnlineRace
    {
        /// <summary>The local kart's state goes out every this many physics steps (30 Hz at 60 ticks/s).</summary>
        const int StateEveryTicks = 2;
        /// <summary>A teleport is flagged on this many consecutive states, so one lost datagram cannot hide it.</summary>
        const int CutRepeat = 3;
        // Moved further than this between two states (m, plus twice the distance its speed explains): a teleport.
        const float TeleportSlack = 3f;
        const double StallMs = 1000;
        /// <summary>The AI host's AiProgress goes out every this many physics steps (10 Hz).</summary>
        const int AiProgressEveryTicks = 6;

        readonly KartState outgoing = new();
        readonly List<ProjectileReport> reports = new();
        readonly List<AiProgress> aiProgress = new();
        readonly Dictionary<int, RemoteKart> remotes = new();
        readonly Dictionary<string, int> effectIndex = new();
        readonly List<StatusEffectDefinition> remoteEffects = new();
        Kart localKart;
        int fixedSteps;
        int cutPending;
        bool hasLastSent;
        Vector3 lastSentPos;
        float raceStartedAt = -1f;
        bool statsLogged;
        // Figures of stand-ins already removed (racers that left), kept for the race's log.
        readonly List<string> leftStats = new();
        // The same drawn-motion figure as the stand-ins' (RemoteKart.StatsReport) for the local kart: the baseline the
        // remote karts' smoothness compares against (frame pacing and rigidbody interpolation affect both).
        Vector3 localDrawn, localDrawnVelocity;
        bool localDrawnValid;
        int localFrames, localJerkyFrames;
        double localAccelSum, localAccelMax;

        void LateUpdate()
        {
            var dt = Time.deltaTime;
            if (localKart == null || dt <= 0f || !HasGo) return;
            var p = localKart.transform.position;
            var v = (p - localDrawn) / dt;
            if (localDrawnValid)
            {
                var a = (v - localDrawnVelocity).magnitude / dt;
                if (a < 1000f)
                {
                    localFrames++;
                    localAccelSum += a;
                    if (a > localAccelMax) localAccelMax = a;
                    if (a > 60f) localJerkyFrames++;
                }
            }
            localDrawnValid = true;
            localDrawn = p;
            localDrawnVelocity = v;
        }

        /// <summary>The receive clock of the realtime channel (Unix ms, monotonic).</summary>
        public double LocalNowMs => udp != null ? udp.LocalNowMs : Time.realtimeSinceStartupAsDouble * 1000.0;

        /// <summary>
        /// Other racers are in the race but nothing new comes from them for over a second (the HUD shows a connection
        /// icon): no World at all, or after GO, Worlds whose states all stopped advancing (the server repeats the
        /// latest state of a racer it hears nothing from).
        /// </summary>
        public bool ConnectionTrouble
        {
            get
            {
                if (udp == null || Loaded == null || !AnyRemoteRacing()) return false;
                var now = udp.LocalNowMs;
                var last = udp.LastWorldMs;
                if (last < 0 ? HasGo : now - last > StallMs) return true;
                return HasGo && SecondsToGo < -StallMs / 1000.0 && lastFreshMs >= 0 && now - lastFreshMs > StallMs;
            }
        }

        bool AnyRemoteRacing()
        {
            foreach (var slot in remotes.Keys)
                if (!leftSlots.Contains(slot)) return true;
            return false;
        }

        // Newest tick per other racer and when any of them last advanced (receive clock), for ConnectionTrouble.
        readonly Dictionary<int, int> newestTick = new();
        double lastFreshMs = -1;

        void NoteFresh(int slot, int tick, double receivedMs)
        {
            if (newestTick.TryGetValue(slot, out var known) && tick <= known) return;
            newestTick[slot] = tick;
            if (tick > 0) lastFreshMs = receivedMs;
        }

        /// <summary>
        /// Queues a live projectile's position for the next State datagrams (each live projectile every 0.5 s, sent
        /// with the next state that has room).
        /// </summary>
        public void QueueProjectileReport(ProjectileReport r) => reports.Add(r);

        void WatchLocalKart(Kart kart)
        {
            if (localKart != null) localKart.Respawned -= OnLocalRespawned;
            localKart = kart;
            kart.Respawned += OnLocalRespawned;
            hasLastSent = false;
            raceStartedAt = Time.realtimeSinceStartup;
        }

        void OnLocalRespawned(Kart k) => cutPending = CutRepeat;

        /// <summary>Called by the race for every spawned kart that is another racer's stand-in.</summary>
        void RegisterRemote(int slot, RemoteKart remote) => remotes[slot] = remote;

        // Game time (Time.timeAsDouble) minus the receive clock, sampled every frame: received states are stamped in
        // game time, the clock the stand-ins render by, whether they are taken in a physics step or a frame.
        double gameMinusUdpSeconds;
        bool gameClockKnown;

        void SyncGameClock()
        {
            if (udp == null) return;
            gameMinusUdpSeconds = Time.timeAsDouble - udp.LocalNowMs / 1000.0;
            gameClockKnown = true;
        }

        void PumpUdp()
        {
            if (udp == null) return;
            var frame = Time.timeAsDouble;
            var now = udp.LocalNowMs;
            while (udp.TryReceive(out var r))
            {
                if (r.slot != LocalSlot)
                {
                    rttBySlot[r.slot] = r.rttMs;
                    NoteFresh(r.slot, r.state.tick, r.receivedMs);
                    if (remotes.TryGetValue(r.slot, out var remote) && remote != null)
                        remote.Push(r.state, gameClockKnown ? r.receivedMs / 1000.0 + gameMinusUdpSeconds : frame - System.Math.Max(0.0, (now - r.receivedMs) / 1000.0),
                            r.receivedMs);
                    RemoteState?.Invoke(r.slot, r.state);
                }
                udp.Recycle(r);
            }
        }

        void SendLocalState()
        {
            if (udp == null || localKart == null || LocalLeft) return;
            if (++fixedSteps % StateEveryTicks != 0) return;
            Fill(outgoing, localKart);
            // A report with a non-finite value cannot be encoded: dropped, so it never blocks the states behind it.
            reports.RemoveAll(r => !Finite(r.pos) || !Finite(r.vel));
            var capacity = OnlineRaceUdp.ReportCapacity(outgoing);
            var n = Mathf.Min(reports.Count, Mathf.Min(capacity, byte.MaxValue));
            try
            {
                udp.SendState(outgoing, reports, 0, n);
            }
            catch (System.ArgumentException e)
            {
                Debug.LogException(e);
            }
            finally
            {
                reports.RemoveRange(0, n);
            }
        }

        // The AI host tells the server where its AI karts are (item rolls, DNF and battle ranking); nobody else needs it.
        void SendAiProgress()
        {
            if (udp == null || !IsAiHost || !HasGo || fixedSteps % AiProgressEveryTicks != 0) return;
            aiProgress.Clear();
            foreach (var pair in kartsBySlot)
            {
                var k = pair.Value;
                if (k == null || !aiSlots.Contains(pair.Key)) continue;
                aiProgress.Add(new AiProgress { slot = pair.Key, lap = k.Progress.Lap, raceDistance = k.Progress.RaceDistance, health = k.Health });
            }
            for (var start = 0; start < aiProgress.Count; start += OnlineRaceUdp.AiProgressCapacity)
                udp.SendAiProgress(aiProgress, start, Mathf.Min(OnlineRaceUdp.AiProgressCapacity, aiProgress.Count - start));
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        // The pose after the previous physics step is that of tick Tick - 1 (this runs before the karts step).
        void Fill(KartState s, Kart k)
        {
            var body = k.Physics.Body;
            var pos = body.position;
            var vel = body.linearVelocity;
            s.tick = Mathf.Max(0, Tick - 1);
            s.pos = pos;
            s.rot = body.rotation;
            s.vel = vel;
            var input = k.EffectiveInput;
            s.keys = (byte)KeysOf(input);
            if (hasLastSent)
            {
                var reach = vel.magnitude * Time.fixedDeltaTime * StateEveryTicks * 2f + TeleportSlack;
                if ((pos - lastSentPos).sqrMagnitude > reach * reach) cutPending = Mathf.Max(cutPending, CutRepeat);
            }
            hasLastSent = true;
            lastSentPos = pos;
            var flags = KartStateFlags.None;
            if (cutPending > 0)
            {
                flags |= KartStateFlags.Cut;
                cutPending--;
            }
            if (k.ControlsEnabled) flags |= KartStateFlags.ControlsEnabled;
            if (k.Progress.Finished) flags |= KartStateFlags.Finished;
            if (k.Eliminated) flags |= KartStateFlags.Eliminated;
            if (k.Physics.Intangible) flags |= KartStateFlags.Intangible;
            if (k.Physics.ContactCount == 0) flags |= KartStateFlags.Airborne;
            if (input.lookBack) flags |= KartStateFlags.LookBack;
            s.flags = flags;
            var max = k.Physics.Profile != null ? k.Physics.Profile.maxSteeringAngle : 0f;
            s.steer = max > 0f ? Mathf.Clamp(k.Physics.SteerAngle / max, -1f, 1f) : 0f;
            s.lap = k.Progress.Lap;
            s.raceDistance = k.Progress.RaceDistance;
            s.health = k.Health;
            s.effects.Clear();
            var active = k.Effects.Active;
            for (var i = 0; i < active.Count; i++)
                if (effectIndex.TryGetValue(active[i].Definition.Id, out var index) && !s.effects.Contains((ushort)index))
                    s.effects.Add((ushort)index);
        }

        static GhostInputKeys KeysOf(in KartInput i)
        {
            var k = GhostInputKeys.None;
            if (i.steer < -KartPhysics.SteerDeadzone) k |= GhostInputKeys.SteerLeft;
            else if (i.steer > KartPhysics.SteerDeadzone) k |= GhostInputKeys.SteerRight;
            if (i.throttle > 0f) k |= GhostInputKeys.Throttle;
            if (i.brake > 0f) k |= GhostInputKeys.Brake;
            if (i.parked) k |= GhostInputKeys.Parked;
            if (i.fire) k |= GhostInputKeys.Fire;
            if (i.lookBack) k |= GhostInputKeys.LookBack;
            if (i.respawn) k |= GhostInputKeys.Respawn;
            return k;
        }

        void IndexEffects()
        {
            effectIndex.Clear();
            for (var i = 0; i < effectIds.Count; i++) effectIndex[effectIds[i]] = i;
        }

        /// <summary>The status effects of a remote racer's state, as definitions (unknown indexes skipped).</summary>
        public IReadOnlyList<StatusEffectDefinition> EffectsOf(KartState s)
        {
            remoteEffects.Clear();
            var content = Core.GameRoot.Instance.Content;
            foreach (var index in s.effects)
            {
                if (index >= effectIds.Count) continue;
                var def = content.Get<StatusEffectDefinition>(effectIds[index]);
                if (def != null) remoteEffects.Add(def);
            }
            return remoteEffects;
        }

        /// <summary>Shows a remote racer's effects as its state lists them (called at render time by its stand-in).</summary>
        internal void ApplyRemoteEffects(Kart kart, KartState s) => kart.Effects.SyncRemote(EffectsOf(s));

        /// <summary>Bandwidth, clock and per-racer interpolation figures (logged once per race).</summary>
        public string StatsReport()
        {
            var b = new StringBuilder();
            var seconds = raceStartedAt >= 0f ? Mathf.Max(0.001f, Time.realtimeSinceStartup - raceStartedAt) : 0f;
            if (udp != null)
            {
                b.Append($"UDP up {udp.BytesUp / 1024f:0.0} KiB in {udp.DatagramsUp} datagrams, down {udp.BytesDown / 1024f:0.0} KiB in {udp.DatagramsDown}");
                if (seconds > 0f) b.Append($" ({udp.BytesUp / 1024f / seconds:0.00} / {udp.BytesDown / 1024f / seconds:0.00} KiB/s over {seconds:0} s)");
                b.Append($", {udp.Worlds} worlds, {udp.Dropped} dropped, RTT {udp.RttMs} ms");
                if (udp.Failure != null) b.Append($", socket failure: {udp.Failure}");
            }
            if (localFrames > 0)
                b.Append($"\n  local kart (baseline): drawn {localFrames} frames, accel mean {localAccelSum / localFrames:0.0} max {localAccelMax:0} m/s², " +
                         $"{localJerkyFrames} frames > 60 m/s²");
            foreach (var r in remotes.Values)
                if (r != null) b.Append("\n  ").Append(r.StatsReport());
            foreach (var s in leftStats) b.Append("\n  ").Append(s);
            return b.ToString();
        }

        /// <summary>Writes <see cref="StatsReport"/> to the log once (race end, or leaving).</summary>
        public void LogStats()
        {
            if (statsLogged) return;
            statsLogged = true;
            Debug.Log($"[OnlineRace] Race {RaceId}: {StatsReport()}");
        }
    }
}
