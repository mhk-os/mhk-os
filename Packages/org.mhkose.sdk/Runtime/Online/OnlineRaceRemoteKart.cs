using System;
using MHK.Ghosts;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Another online racer's kart: a real <see cref="Kart"/> (targeting, minimap, ranking and hits see it) whose
    /// kinematic body follows the received states about <see cref="DelayTicks"/> behind the newest one. Positions are
    /// Hermite-interpolated with the sent velocities, rotations slerped; a short gap is bridged by extrapolation, a cut
    /// (respawn, teleport) snaps. The render clock runs at the physics rate and is nudged towards
    /// newest tick − delay, so packet jitter never reaches the picture. Rigidbody interpolation smooths the frames
    /// between physics steps.
    /// </summary>
    [DefaultExecutionOrder(-80)]
    public sealed class RemoteKart : MonoBehaviour
    {
        /// <summary>Render delay behind the newest received state (100 ms at 60 ticks/s: three 30 Hz states).</summary>
        public const double DelayTicks = 6;
        const int BufferSize = 32;
        const double MaxExtrapolateTicks = 15;
        const double ResyncTicks = 30;
        const double ClockGain = 0.1, MaxClockSlew = 0.3;
        // Faster than any kart (m/s) between two states: a teleport whose cut flag was lost.
        const float ImplausibleSpeed = 120f;
        const float LeaveFadeSeconds = 0.8f;
        const double HermiteMaxTicks = 20;

        Kart kart;
        OnlineRace race;
        readonly KartState[] buffer = new KartState[BufferSize];
        readonly bool[] cutBefore = new bool[BufferSize];
        int start, count;
        double newestArrival = -1;
        double renderTick;
        bool clockStarted, applied, leaving;
        int lastSegment = -1;
        float leaveTimer;

        /// <summary>Grid slot of the racer.</summary>
        public int Slot { get; private set; }

        // Measurements (logged at the end of the race).
        int arrivals;
        double gapSum, gapMax, lastArrivalReal = -1;
        int lateGaps;
        double delaySum;
        int delaySamples, extrapolatedSteps, snaps, resyncs;
        // Drawn motion: change of the per-frame velocity of the rendered pose (jitter shows as spikes).
        Vector3 lastDrawn, lastDrawnVelocity;
        bool drawnValid;
        int frames, jerkyFrames;
        double frameAccelSum, frameAccelMax;
        const float JerkyFrameAccel = 60f;
        // Contacts with the local player's kart (bumping).
        int bumps;

        void OnCollisionEnter(Collision c)
        {
            var other = Kart.FromCollider(c.collider);
            if (other != null && !other.IsRemote && RaceManager.Current != null && other == RaceManager.Current.LocalPlayer) bumps++;
        }

        void LateUpdate()
        {
            var dt = Time.deltaTime;
            if (kart == null || dt <= 0f || leaving) return;
            var p = transform.position;
            var v = (p - lastDrawn) / dt;
            if (drawnValid && clockStarted && applied)
            {
                var a = (v - lastDrawnVelocity).magnitude / dt;
                if (a < 1000f)
                {
                    frames++;
                    frameAccelSum += a;
                    if (a > frameAccelMax) frameAccelMax = a;
                    if (a > JerkyFrameAccel) jerkyFrames++;
                }
            }
            drawnValid = true;
            lastDrawn = p;
            lastDrawnVelocity = v;
        }

        /// <summary>Makes <paramref name="k"/> the stand-in of grid slot <paramref name="slot"/>.</summary>
        public static RemoteKart Attach(Kart k, int slot, OnlineRace race)
        {
            k.BecomeRemote();
            k.Progress.ApplyRemote(k.Progress.Lap, k.Progress.RaceDistance);
            var r = k.gameObject.AddComponent<RemoteKart>();
            r.kart = k;
            r.Slot = slot;
            r.race = race;
            for (var i = 0; i < BufferSize; i++) r.buffer[i] = new KartState();
            return r;
        }

        KartState At(int i) => buffer[(start + i) % BufferSize];
        bool CutAt(int i) => cutBefore[(start + i) % BufferSize];

        /// <summary>
        /// A received state (copied). <paramref name="arrival"/> is the game time (Time.timeAsDouble domain) it arrived
        /// at, <paramref name="realMs"/> the receive thread's clock (gap statistics).
        /// </summary>
        public void Push(KartState s, double arrival, double realMs)
        {
            if (leaving) return;
            if (count > 0)
            {
                var newest = At(count - 1);
                if (s.tick < newest.tick || (s.tick == newest.tick && s.tick != 0)) return;
                if (s.tick == 0 && newest.tick == 0)
                {
                    newest.CopyFrom(s);
                    newestArrival = arrival;
                    lastArrivalReal = realMs;
                    return;
                }
            }
            if (lastArrivalReal >= 0)
            {
                var gap = realMs - lastArrivalReal;
                gapSum += gap;
                if (gap > gapMax) gapMax = gap;
                if (gap > 50) lateGaps++;
                arrivals++;
            }
            lastArrivalReal = realMs;
            var cut = s.Has(KartStateFlags.Cut);
            if (count > 0 && !cut)
            {
                var prev = At(count - 1);
                var dt = Math.Max(1, s.tick - prev.tick) / (double)RaceProtocol.TickRate;
                cut = (s.pos - prev.pos).magnitude / dt > ImplausibleSpeed;
            }
            if (count == BufferSize)
            {
                start = (start + 1) % BufferSize;
                count--;
                if (lastSegment >= 0) lastSegment--;
            }
            var slot = (start + count) % BufferSize;
            buffer[slot].CopyFrom(s);
            cutBefore[slot] = cut;
            count++;
            newestArrival = arrival;
        }

        /// <summary>The racer left: fades out, stops colliding and is removed.</summary>
        public void Leave()
        {
            if (leaving) return;
            leaving = true;
            kart.RemoteFade = 0f;
            kart.Physics.Body.detectCollisions = false;
            leaveTimer = LeaveFadeSeconds;
        }

        void Update()
        {
            if (!leaving) return;
            leaveTimer -= Time.deltaTime;
            if (leaveTimer > 0f) return;
            if (RaceManager.Current != null) RaceManager.Current.RemoveKart(kart);
            Destroy(kart.gameObject);
        }

        void FixedUpdate()
        {
            if (count == 0 || kart == null) return;
            var h = Time.fixedDeltaTime;
            var newest = At(count - 1);
            if (newest.tick == 0)
            {
                // Before GO every state is tick 0: show the newest (the racer stands on the grid).
                Apply(newest, newest.pos, newest.rot, newest.vel, newest.steer, newest.raceDistance, !applied, h);
                return;
            }
            AdvanceClock(newest);
            Sample(h);
        }

        void AdvanceClock(KartState newest)
        {
            var newestNow = newest.tick + (Time.fixedTimeAsDouble - newestArrival) * RaceProtocol.TickRate;
            var target = newestNow - DelayTicks;
            if (!clockStarted || Math.Abs(target - renderTick) > ResyncTicks)
            {
                if (clockStarted) resyncs++;
                renderTick = target;
                clockStarted = true;
                lastSegment = -1;
            }
            else renderTick += 1.0 + Math.Max(-MaxClockSlew, Math.Min(MaxClockSlew, (target - renderTick) * ClockGain));
            delaySum += newestNow - renderTick;
            delaySamples++;
        }

        void Sample(float h)
        {
            var oldest = At(0);
            var newest = At(count - 1);
            if (renderTick <= oldest.tick || count == 1)
            {
                var hold = renderTick <= oldest.tick ? oldest : newest;
                var ahead = renderTick <= oldest.tick ? 0.0 : Math.Min(renderTick - newest.tick, MaxExtrapolateTicks);
                Extrapolated(hold, Math.Max(0.0, ahead), h);
                return;
            }
            if (renderTick >= newest.tick)
            {
                extrapolatedSteps++;
                Extrapolated(newest, Math.Min(renderTick - newest.tick, MaxExtrapolateTicks), h, CutAt(count - 1) && lastSegment < count - 1);
                lastSegment = count - 1;
                return;
            }
            var i = count - 2;
            while (i > 0 && At(i).tick > renderTick) i--;
            var a = At(i);
            var b = At(i + 1);
            var crossedCut = false;
            for (var s = Math.Max(1, lastSegment + 1); s <= i && lastSegment >= 0; s++)
                if (CutAt(s)) crossedCut = true;
            lastSegment = i;
            if (CutAt(i + 1))
            {
                // A respawn between a and b: stay at a until b's tick, then appear at b.
                Apply(a, a.pos, a.rot, Vector3.zero, a.steer, a.raceDistance, crossedCut || !applied, h);
                return;
            }
            var span = (double)(b.tick - a.tick);
            var t = (float)((renderTick - a.tick) / span);
            var dt = (float)(span / RaceProtocol.TickRate);
            Vector3 pos;
            if (span <= HermiteMaxTicks)
            {
                var t2 = t * t;
                var t3 = t2 * t;
                pos = (2f * t3 - 3f * t2 + 1f) * a.pos + (t3 - 2f * t2 + t) * dt * a.vel + (-2f * t3 + 3f * t2) * b.pos + (t3 - t2) * dt * b.vel;
            }
            else pos = Vector3.Lerp(a.pos, b.pos, t);
            var near = t < 0.5f ? a : b;
            Apply(near, pos, Quaternion.Slerp(a.rot, b.rot, t), Vector3.Lerp(a.vel, b.vel, t), Mathf.Lerp(a.steer, b.steer, t),
                Mathf.Lerp(a.raceDistance, b.raceDistance, t), crossedCut || !applied, h);
        }

        void Extrapolated(KartState s, double ticks, float h, bool snap = false)
        {
            var seconds = (float)(ticks / RaceProtocol.TickRate);
            var moving = !s.Has(KartStateFlags.Finished) || s.vel.sqrMagnitude > 0.01f;
            var pos = moving ? s.pos + s.vel * seconds : s.pos;
            Apply(s, pos, s.rot, s.vel, s.steer, s.raceDistance, snap || !applied, h);
        }

        void Apply(KartState s, Vector3 pos, Quaternion rot, Vector3 vel, float steer, float raceDistance, bool snap, float h)
        {
            if (snap && applied) snaps++;
            applied = true;
            var input = InputOf(s, steer);
            kart.Physics.StepRemote(pos, rot, vel, steer, input.throttle > 0f, input.brake > 0f, s.Has(KartStateFlags.Airborne),
                s.Has(KartStateFlags.Intangible), snap, h);
            kart.SetRemoteInput(input);
            kart.ControlsEnabled = s.Has(KartStateFlags.ControlsEnabled) && !leaving;
            kart.Progress.ApplyRemote(s.lap, raceDistance);
            kart.SetRemoteHealth(s.health);
            race.ApplyRemoteEffects(kart, s);
        }

        static KartInput InputOf(KartState s, float steer)
        {
            var keys = (GhostInputKeys)s.keys;
            return new KartInput
            {
                steer = (keys & GhostInputKeys.SteerLeft) != 0 ? -1f : (keys & GhostInputKeys.SteerRight) != 0 ? 1f : steer,
                throttle = (keys & GhostInputKeys.Throttle) != 0 ? 1f : 0f,
                brake = (keys & GhostInputKeys.Brake) != 0 ? 1f : 0f,
                parked = (keys & GhostInputKeys.Parked) != 0,
                fire = (keys & GhostInputKeys.Fire) != 0,
                lookBack = (keys & GhostInputKeys.LookBack) != 0 || s.Has(KartStateFlags.LookBack),
                respawn = (keys & GhostInputKeys.Respawn) != 0,
            };
        }

        /// <summary>Snapshot gaps and interpolation figures of this racer, for the race's log.</summary>
        public string StatsReport() =>
            $"slot {Slot}: {arrivals} states, gap mean {(arrivals > 0 ? gapSum / arrivals : 0):0.0} ms max {gapMax:0} ms, " +
            $"{lateGaps} gaps > 50 ms, render delay {(delaySamples > 0 ? delaySum / delaySamples / RaceProtocol.TickRate * 1000 : 0):0} ms, " +
            $"{extrapolatedSteps} extrapolated steps, {snaps} snaps, {resyncs} clock resyncs; drawn {frames} frames, accel mean " +
            $"{(frames > 0 ? frameAccelSum / frames : 0):0.0} max {frameAccelMax:0} m/s², {jerkyFrames} frames > {JerkyFrameAccel:0} m/s²; " +
            $"{bumps} bumps by the local kart";
    }
}
