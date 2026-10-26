using System;
using System.Collections.Generic;
using MHK.Online;
using UnityEngine;

namespace MHK.Validation
{
    /// <summary>Checks a <see cref="KartValidator"/> runs.</summary>
    [Flags]
    public enum ValidationChecks
    {
        None = 0,
        Speed = 1,
        Acceleration = 2,
        Teleport = 4,
        WallClip = 8,
        Flying = 16,
        Laps = 32,
        Data = 64,
        Divergence = 128,
        /// <summary>Checks that need nothing but the samples themselves (any simulation version or content).</summary>
        Kinematic = Teleport | Data,
        All = Speed | Acceleration | Teleport | WallClip | Flying | Laps | Data | Divergence,
    }

    /// <summary>
    /// How close the samples came to each threshold so far, as a share of it (1 = at the threshold), for tuning and
    /// diagnostics. Only samples that were not flagged count.
    /// </summary>
    public struct ValidationMargins
    {
        public float speed, acceleration, teleport, data, flying, penetration, wallCrossing, lapError, divergence;
        public int windows, windowsTouched, windowsDiverged;

        public override string ToString() =>
            $"speed {speed:0.00}, accel {acceleration:0.00}, teleport {teleport:0.00}, data {data:0.00}, flying {flying:0.00}, " +
            $"penetration {penetration:0.00}, wall {wallCrossing:0.00}, laps {lapError:0.00}, divergence {divergence:0.00} " +
            $"({windowsDiverged}/{windows} windows diverged, {windowsTouched} touched walls)";
    }

    /// <summary>
    /// Plausibility engine for one kart's motion. Fed a stream of <see cref="KartSample"/>s in tick order (every tick from
    /// a ghost, every few ticks from network snapshots), it compares them with what the kart's physics, the track and the
    /// active effects allow and reports each <see cref="FlagReason"/> once, at the race time it happened. Re-simulation
    /// windows go to a <see cref="Resimulator"/>, which reports back through <see cref="ReportWindow"/>.
    /// Thresholds leave room for platform differences (desktop Mono vs Android IL2CPP, PhysX without enhanced
    /// determinism): nothing is ever compared for equality.
    /// </summary>
    public sealed class KartValidator
    {
        // Speed: the kart's flat top speed (redline speed after boosts) × this, plus what height loss explains.
        const float SpeedTolerance = 1.12f;
        const float UnknownEffectSpeed = 1.5f;
        const float SpeedExcessDecaySeconds = 4f;
        const float SpeedSustainSeconds = 0.25f;
        const float BoostAllowanceSeconds = 0.5f;
        // Acceleration: speed gained over a window against engine + gravity.
        const float AccelWindowSeconds = 0.2f;
        const float AccelFactor = 1.25f;
        const float AccelSlack = 3f;
        const float AccelSlackSpeed = 1f;
        // Teleport: displacement not explained by the velocity.
        const float TeleportSlack = 3f;
        const float TeleportReach = 2f;
        const float RespawnForwardSlack = 25f;
        // Data: recorded velocity vs positional delta.
        const int DataWindow = 30;
        const float DataMinError = 2f;
        const float DataRelativeError = 0.15f;
        const float DataShare = 0.8f;
        // Geometry queries run every few ticks.
        const int QueryStride = 3;
        // Flying: airborne (no static ground within this below the kart origin) and above the ballistic path.
        const float AirborneClearance = 1.5f;
        const float GroundProbeDepth = 60f;
        const float FlyingMinSeconds = 1f;
        const float FlyingSlack = 1f;
        const float FlyingGravityShare = 0.4f;
        // Wall clip: chassis inside static geometry, or behind a wall face it passed through.
        const float PenetrationDepth = 0.25f;
        const float PenetrationSeconds = 0.3f;
        const float WallMaxNormalY = 0.6f;
        const float BehindDepth = 0.3f;
        const float BehindRange = 15f;
        const float BehindSeconds = 0.3f;
        const float CrossingMaxStep = 20f;
        // Laps.
        const float LapTolerance = 0.05f;
        const float LapGrace = 1.5f;
        // Re-simulation.
        const int ResimWarmupTicks = 20;
        const int ResimTicks = 60;
        const float ResimMinSpeed = 3f;
        // A tick whose motion differs this much (m/s) from the observed velocity had a chassis contact (scraping a wall
        // gives 0.4 and more, free driving below 0.25): compare up to it only.
        const float ResimPush = 0.4f;
        const float DivergenceBase = 1.5f;
        const float DivergenceShareOfTravel = 0.05f;
        const int DivergedWindowsToFlag = 3;
        const float DivergedShareToFlag = 0.4f;
        // Influence of effects, respawns and track features lasts this long after them.
        const float ExemptSeconds = 0.3f;
        const int HistoryCapacity = 256;

        readonly TrackContext track;
        readonly KartSpec spec;
        readonly EffectSet effects;
        readonly Resimulator resim;
        readonly ValidationChecks checks;
        readonly float tick;
        readonly float g;
        readonly LapReplay laps;
        readonly int lapCount;
        readonly float[] claimedLaps, recordedLaps;
        readonly KartSample[] history = new KartSample[HistoryCapacity];
        readonly bool[] dataBad = new bool[DataWindow];
        readonly List<Violation> violations = new();
        readonly HashSet<FlagReason> reported = new();
        int historyCount, historyHead;
        int dataCount, dataHead, dataBadCount;

        bool started;
        KartSample prev;
        EffectInfluence fx;
        TrackRegion region;
        int lastCut = int.MinValue / 2, lastEffect = int.MinValue / 2, lastEffectStart = int.MinValue / 2, lastBoost = int.MinValue / 2;
        int lastPush = int.MinValue / 2, lastMover = int.MinValue / 2, lastBridge = int.MinValue / 2, lastMovesKart = int.MinValue / 2;
        ulong prevEffects;
        float allowedSpeedSq, speedOver;
        // Geometry query state.
        int lastQueryTick = int.MinValue / 2;
        Vector3 lastQueryCentre;
        bool lastQueryValid;
        int airStart = -1;
        float airY, airVy;
        float penetrationTime;
        bool crossing;
        Vector3 crossPoint, crossNormal;
        int crossTick;
        // Laps: time of the latest crossing that counted no lap (checkpoints missing) since the last counted one.
        float lastUncounted = float.NaN;
        // Re-simulation.
        int cleanSince = int.MaxValue, nextWindowTick;
        int pendingWindows;
        ValidationMargins margins;

        /// <param name="track">The track the kart drives on.</param>
        /// <param name="spec">Its physics.</param>
        /// <param name="effects">Resolves the effect bits of the samples.</param>
        /// <param name="lapCount">Laps of the race.</param>
        /// <param name="tickSeconds">Length of a physics tick.</param>
        /// <param name="resimulator">Runs the re-simulation windows, null to skip <see cref="ValidationChecks.Divergence"/>.</param>
        public KartValidator(TrackContext track, KartSpec spec, EffectSet effects, int lapCount, float tickSeconds,
            ValidationChecks checks = ValidationChecks.All, Resimulator resimulator = null)
        {
            this.track = track;
            this.spec = spec;
            this.effects = effects;
            this.lapCount = Mathf.Max(1, lapCount);
            tick = tickSeconds;
            resim = resimulator;
            this.checks = resimulator != null ? checks : checks & ~ValidationChecks.Divergence;
            g = track.Gravity;
            var wrongWay = Core.GameRoot.Instance != null ? Core.GameRoot.Instance.Config.wrongWaySeconds : 2f;
            laps = new LapReplay(track.Track, this.lapCount, wrongWay);
            claimedLaps = new float[this.lapCount + 1];
            recordedLaps = new float[this.lapCount + 1];
            Array.Fill(claimedLaps, float.NaN);
            Array.Fill(recordedLaps, float.NaN);
        }

        /// <summary>Seconds of recorded time between two re-simulation windows (more ghosts, fewer windows each).</summary>
        public float ResimInterval { get; set; } = 3f;
        /// <summary>Every problem found, at most one per reason, in the order found.</summary>
        public IReadOnlyList<Violation> Violations => violations;
        /// <summary>Raised for each new entry of <see cref="Violations"/>.</summary>
        public event Action<Violation> Found;
        public ValidationMargins Margins => margins;
        /// <summary>Tick of the latest sample, -1 before the first.</summary>
        public int LastTick => started ? prev.tick : -1;
        /// <summary>Re-simulation windows handed out and not reported back yet.</summary>
        public int PendingWindows => pendingWindows;

        /// <summary>
        /// The race says lap <paramref name="lap"/> ended at <paramref name="time"/> (s from GO); lap 0 = the first
        /// crossing that starts lap 1. Compared with the crossings the samples show.
        /// </summary>
        public void ClaimLap(int lap, float time)
        {
            if (lap < 0 || lap > lapCount) return;
            claimedLaps[lap] = time;
            CompareLap(lap);
        }

        /// <summary>The next observed state (ticks increasing).</summary>
        public void Push(in KartSample s)
        {
            if (!started)
            {
                Begin(s);
                return;
            }
            var ticks = s.tick - prev.tick;
            if (ticks <= 0) return;
            var dt = ticks * tick;
            var cut = s.Has(KartSampleFlags.Cut);
            UpdateContext(s, cut);
            if ((checks & ValidationChecks.Teleport) != 0 && !cut) CheckTeleport(s, dt);
            if ((checks & ValidationChecks.Data) != 0) CheckData(s, dt, cut);
            if ((checks & ValidationChecks.Speed) != 0) CheckSpeed(s, dt, cut);
            if ((checks & ValidationChecks.Acceleration) != 0) CheckAcceleration(s);
            if ((checks & (ValidationChecks.Flying | ValidationChecks.WallClip)) != 0 && (cut || s.tick - lastQueryTick >= QueryStride)) Query(s, cut);
            StepLaps(s, dt, cut);
            if ((checks & ValidationChecks.Divergence) != 0) ScheduleWindow(s);
            Remember(s);
            prev = s;
        }

        /// <summary>No more samples: laps the race claimed and the samples never showed are resolved.</summary>
        public void Finish()
        {
            if (!started) return;
            CheckOverdueLaps(float.PositiveInfinity);
        }

        void Begin(in KartSample s)
        {
            started = true;
            prev = s;
            prevEffects = s.effects;
            fx = effects.Describe(s.effects);
            allowedSpeedSq = Cap(s.tick) * Cap(s.tick);
            if (laps.Valid) laps.Begin(s.position, s.rotation * Vector3.forward);
            Remember(s);
        }

        void UpdateContext(in KartSample s, bool cut)
        {
            fx = effects.Describe(s.effects);
            region = track.Classify(s.position);
            if (cut) lastCut = s.tick;
            if (fx.any) lastEffect = s.tick;
            if ((s.effects & ~prevEffects) != 0) lastEffectStart = s.tick;
            if (fx.speedBoost || fx.unknown || (region & TrackRegion.Boost) != 0) lastBoost = s.tick;
            if ((region & TrackRegion.Push) != 0) lastPush = s.tick;
            if ((region & TrackRegion.Mover) != 0) lastMover = s.tick;
            if ((region & TrackRegion.Bridge) != 0) lastBridge = s.tick;
            if (fx.movesKart) lastMovesKart = s.tick;
            prevEffects = s.effects;
        }

        bool Recent(int since, int now, float seconds) => (now - since) * tick <= seconds;

        // Teleport: displacement beyond what the velocity explains, without a respawn cut.
        void CheckTeleport(in KartSample s, float dt)
        {
            var delta = s.position - prev.position;
            var err = Mathf.Min((delta - prev.velocity * dt).magnitude, (delta - 0.5f * (prev.velocity + s.velocity) * dt).magnitude);
            var speed = Mathf.Max(prev.velocity.magnitude, s.velocity.magnitude);
            var limit = TeleportSlack + TeleportReach * speed * dt;
            if (Recent(lastPush, s.tick, ExemptSeconds)) limit += track.PushSpeedAt(s.position) * dt;
            if (err > limit) Report(FlagReason.Teleport, s.tick, $"moved {delta.magnitude:0.0} m in {dt:0.000} s, {err:0.0} m beyond its velocity");
            else margins.teleport = Mathf.Max(margins.teleport, err / limit);
        }

        // Data: the recorded velocity must carry the kart to its next position. Collisions break single ticks, so only a
        // window where nearly every tick disagrees counts.
        void CheckData(in KartSample s, float dt, bool cut)
        {
            var exempt = cut || Recent(lastPush, s.tick, ExemptSeconds) || Recent(lastMover, s.tick, ExemptSeconds)
                         || Recent(lastMovesKart, s.tick, ExemptSeconds) || Recent(lastCut, prev.tick, 0f);
            var bad = false;
            if (!exempt)
            {
                var err = ((s.position - prev.position) / dt - prev.velocity).magnitude;
                bad = err > Mathf.Max(DataMinError, DataRelativeError * prev.velocity.magnitude);
            }
            if (dataCount == DataWindow)
            {
                if (dataBad[dataHead]) dataBadCount--;
            }
            else dataCount++;
            dataBad[dataHead] = bad;
            if (bad) dataBadCount++;
            dataHead = (dataHead + 1) % DataWindow;
            if (dataCount < DataWindow) return;
            var share = dataBadCount / (float)DataWindow;
            if (share >= DataShare)
                Report(FlagReason.Data, s.tick, $"velocity disagrees with the motion in {dataBadCount} of {DataWindow} samples");
            else margins.data = Mathf.Max(margins.data, share / DataShare);
        }

        float Cap(int now)
        {
            var cap = spec.flatTopSpeed * fx.maxSpeed;
            if (Recent(lastBoost, now, BoostAllowanceSeconds)) cap = Mathf.Max(cap, spec.redlineSpeed * fx.maxSpeed);
            if (fx.unknown) cap *= UnknownEffectSpeed;
            return cap * SpeedTolerance;
        }

        // Speed: above the top speed plus the energy of the height lost since (the excess fades like drag would).
        void CheckSpeed(in KartSample s, float dt, bool cut)
        {
            var cap = Cap(s.tick);
            var capSq = cap * cap;
            if (cut) allowedSpeedSq = capSq;
            else
            {
                var excess = Mathf.Max(0f, allowedSpeedSq - capSq) * Mathf.Exp(-dt / SpeedExcessDecaySeconds);
                var fall = 2f * g * Mathf.Max(1f, fx.gravity) * (prev.position.y - s.position.y);
                allowedSpeedSq = Mathf.Max(capSq, capSq + excess + fall);
            }
            var allowed = Mathf.Sqrt(allowedSpeedSq);
            var speed = s.velocity.magnitude;
            if (speed > allowed)
            {
                speedOver += dt;
                if (speedOver >= SpeedSustainSeconds)
                    Report(FlagReason.Speed, s.tick, $"{speed:0.0} m/s, at most {allowed:0.0} m/s possible");
                return;
            }
            speedOver = 0f;
            margins.speed = Mathf.Max(margins.speed, speed / allowed);
        }

        // Acceleration: speed gained over a short window against the engine's best plus gravity.
        void CheckAcceleration(in KartSample s)
        {
            if (!TryHistoryBefore(s.tick - Mathf.Max(1, Mathf.RoundToInt(AccelWindowSeconds / tick)), out var from)) return;
            var start = from.tick - Mathf.CeilToInt(ExemptSeconds / tick);
            if (lastCut >= start || lastEffect >= start || lastEffectStart >= start || lastBoost >= start || lastPush >= start
                || lastMover >= start || lastMovesKart >= start) return;
            var seconds = (s.tick - from.tick) * tick;
            var gain = s.velocity.magnitude - from.velocity.magnitude;
            var limit = ((spec.maxDriveAcceleration * fx.acceleration + g) * AccelFactor + AccelSlack) * seconds + AccelSlackSpeed;
            if (gain > limit) Report(FlagReason.Acceleration, s.tick, $"gained {gain:0.0} m/s in {seconds:0.00} s, at most {limit:0.0} m/s possible");
            else margins.acceleration = Mathf.Max(margins.acceleration, gain / limit);
        }

        void Query(in KartSample s, bool cut)
        {
            var dt = (s.tick - lastQueryTick) * tick;
            var centre = s.position + s.rotation * spec.chassisCentre;
            if ((checks & ValidationChecks.Flying) != 0) CheckFlying(s, cut);
            if ((checks & ValidationChecks.WallClip) != 0) CheckWalls(s, centre, cut, dt);
            lastQueryTick = s.tick;
            lastQueryCentre = centre;
            lastQueryValid = true;
        }

        // Flying: no ground below for a while and higher than a ballistic arc from where it left the ground.
        void CheckFlying(in KartSample s, bool cut)
        {
            var exempt = cut || fx.any || Recent(lastMovesKart, s.tick, ExemptSeconds) || Recent(lastBoost, s.tick, ExemptSeconds)
                         || Recent(lastPush, s.tick, ExemptSeconds) || Recent(lastMover, s.tick, ExemptSeconds) || Recent(lastBridge, s.tick, ExemptSeconds);
            var grounded = track.GroundBelow(s.position + Vector3.up * 0.5f, GroundProbeDepth, out var distance) && distance - 0.5f <= AirborneClearance;
            if (grounded || exempt)
            {
                airStart = -1;
                return;
            }
            if (airStart < 0)
            {
                airStart = s.tick;
                airY = s.position.y;
                airVy = s.velocity.y;
                return;
            }
            var t = (s.tick - airStart) * tick;
            var fall = 0.5f * g * t * t;
            var dev = s.position.y - (airY + airVy * t - fall);
            var limit = FlyingSlack + FlyingGravityShare * fall;
            if (t < FlyingMinSeconds) return;
            if (dev > limit) Report(FlagReason.Flying, s.tick, $"{t:0.0} s in the air, {dev:0.0} m above a free fall");
            else margins.flying = Mathf.Max(margins.flying, dev / limit);
        }

        // Wall clip: the chassis deep inside static geometry for a while, or its centre passed a wall face and stayed behind it.
        void CheckWalls(in KartSample s, Vector3 centre, bool cut, float dt)
        {
            var exempt = cut || Recent(lastMover, s.tick, ExemptSeconds) || Recent(lastBridge, s.tick, ExemptSeconds) || Recent(lastCut, s.tick, ExemptSeconds);
            if (exempt)
            {
                penetrationTime = 0f;
                crossing = false;
                return;
            }
            var depth = track.ChassisPenetration(s.position, s.rotation, spec);
            if (depth > PenetrationDepth)
            {
                penetrationTime += dt;
                if (penetrationTime >= PenetrationSeconds) Report(FlagReason.WallClip, s.tick, $"chassis {depth:0.00} m inside the track geometry");
            }
            else penetrationTime = 0f;
            margins.penetration = Mathf.Max(margins.penetration, Mathf.Min(depth, PenetrationDepth) / PenetrationDepth);
            if (crossing)
            {
                var offset = centre - crossPoint;
                if (Vector3.Dot(offset, crossNormal) < -BehindDepth && offset.magnitude < BehindRange)
                {
                    var behind = (s.tick - crossTick) * tick;
                    if (behind >= BehindSeconds) Report(FlagReason.WallClip, s.tick, $"passed through a wall and stayed {behind:0.0} s behind it");
                    else margins.wallCrossing = Mathf.Max(margins.wallCrossing, behind / BehindSeconds);
                    return;
                }
                crossing = false;
            }
            if (!lastQueryValid || (centre - lastQueryCentre).sqrMagnitude > CrossingMaxStep * CrossingMaxStep) return;
            if (!track.WallBetween(lastQueryCentre, centre, WallMaxNormalY, out var hit)) return;
            crossing = true;
            crossPoint = hit.point;
            crossNormal = hit.normal;
            crossTick = s.tick;
        }

        void StepLaps(in KartSample s, float dt, bool cut)
        {
            if (!laps.Valid) return;
            var before = laps.PathDistance;
            var e = laps.Step(s.position, s.rotation * Vector3.forward, s.velocity.magnitude, s.contacts != 0, s.tick * tick, dt, cut);
            if (cut && (checks & ValidationChecks.Teleport) != 0)
            {
                var ahead = track.Track.path.Delta(before, laps.PathDistance);
                if (ahead > RespawnForwardSlack) Report(FlagReason.Teleport, s.tick, $"respawned {ahead:0} m ahead on the track");
            }
            if ((checks & ValidationChecks.Laps) == 0) return;
            switch (e.kind)
            {
                case LapEventKind.Uncounted:
                    lastUncounted = e.time;
                    break;
                case LapEventKind.FirstCrossing:
                    Record(0, e.time);
                    break;
                case LapEventKind.Lap:
                    Record(e.lap, e.time);
                    break;
            }
            CheckOverdueLaps(s.tick * tick);
        }

        void Record(int lap, float time)
        {
            if (lap > lapCount) return;
            recordedLaps[lap] = time;
            lastUncounted = float.NaN;
            CompareLap(lap);
        }

        void CompareLap(int lap)
        {
            if ((checks & ValidationChecks.Laps) == 0 || float.IsNaN(claimedLaps[lap]) || float.IsNaN(recordedLaps[lap])) return;
            var err = Mathf.Abs(recordedLaps[lap] - claimedLaps[lap]);
            if (err > LapTolerance)
                Report(FlagReason.LapTimes, TickAt(recordedLaps[lap]), lap == 0
                    ? $"crossed the start line at {recordedLaps[lap]:0.000} s, claims {claimedLaps[lap]:0.000} s"
                    : $"lap {lap} ended at {recordedLaps[lap]:0.000} s, claims {claimedLaps[lap]:0.000} s");
            else margins.lapError = Mathf.Max(margins.lapError, err / LapTolerance);
        }

        // A claimed crossing the samples did not show in time: after a crossing without every checkpoint it is a skipped
        // checkpoint, otherwise the lap times are wrong.
        void CheckOverdueLaps(float now)
        {
            if ((checks & ValidationChecks.Laps) == 0) return;
            for (var lap = 0; lap <= lapCount; lap++)
            {
                var claim = claimedLaps[lap];
                if (float.IsNaN(claim) || !float.IsNaN(recordedLaps[lap]) || now < claim + LapGrace) continue;
                if (!float.IsNaN(lastUncounted))
                    Report(FlagReason.Checkpoints, TickAt(lastUncounted), $"crossed the line at {lastUncounted:0.000} s without passing every checkpoint");
                else
                    Report(FlagReason.LapTimes, TickAt(claim), lap == 0 ? "never crossed the start line" : $"lap {lap} not completed by {claim:0.000} s");
                return;
            }
        }

        int TickAt(float time) => Mathf.RoundToInt(time / tick);

        // A window opens when the samples have been free of anything the stripped kart does not simulate for long enough.
        void ScheduleWindow(in KartSample s)
        {
            var clean = !s.Has(KartSampleFlags.Cut) && !fx.any && s.Has(KartSampleFlags.ControlsEnabled)
                        && (s.keys & KartKeys.Parked) == 0 && region == TrackRegion.None
                        && Vector3.Dot(s.velocity, s.rotation * Vector3.forward) > ResimMinSpeed
                        && !Recent(lastEffect, s.tick, 1f) && !Recent(lastBoost, s.tick, 1f) && !Recent(lastMovesKart, s.tick, 1f);
            if (!clean)
            {
                cleanSince = int.MaxValue;
                return;
            }
            if (cleanSince == int.MaxValue) cleanSince = s.tick;
            var span = ResimWarmupTicks + ResimTicks + 1;
            if (s.tick - cleanSince < span || s.tick < nextWindowTick) return;
            var window = BuildWindow(s, s.tick - span);
            if (window == null) return;
            nextWindowTick = s.tick + Mathf.Max(span, Mathf.RoundToInt(ResimInterval / tick));
            pendingWindows++;
            resim.Enqueue(window);
        }

        ResimWindow BuildWindow(in KartSample last, int firstTick)
        {
            var count = last.tick - firstTick + 1;
            var w = new ResimWindow
            {
                owner = this,
                spec = spec,
                tickSeconds = tick,
                firstTick = firstTick,
                warmup = ResimWarmupTicks,
                position = new Vector3[count],
                velocity = new Vector3[count],
                angularVelocity = new Vector3[count],
                rotation = new Quaternion[count],
                keys = new KartKeys[count],
                steerCommand = new float[count],
            };
            // Samples from the newest backwards; `last` is not in the history yet.
            var b = last;
            var i = 0;
            for (var t = last.tick; t >= firstTick; t--)
            {
                while (b.tick > t)
                {
                    if (i >= historyCount) return null;
                    var a = HistoryAt(i++);
                    if (a.tick <= t)
                    {
                        FillBetween(w, a, b, firstTick, t);
                        t = a.tick;
                    }
                    b = a;
                }
                if (b.tick == t && t >= firstTick) Fill(w, t - firstTick, b);
            }
            // Backwards, so the observed velocity of tick j - 1 is still there to compare with the motion out of it.
            w.compareIndex = count - 1;
            for (var j = count - 1; j >= 1; j--)
            {
                // The velocity a tick starts with is the one the physics moved the kart with since the tick before.
                var moved = (w.position[j] - w.position[j - 1]) / tick;
                if (j - 1 > ResimWarmupTicks && (moved - w.velocity[j - 1]).magnitude > ResimPush) w.compareIndex = j - 1;
                w.velocity[j] = moved;
                var dq = w.rotation[j] * Quaternion.Inverse(w.rotation[j - 1]);
                dq.ToAngleAxis(out var angle, out var axis);
                if (angle > 180f) angle -= 360f;
                w.angularVelocity[j] = float.IsFinite(axis.x) ? axis * (angle * Mathf.Deg2Rad / tick) : Vector3.zero;
            }
            return w;
        }

        static void Fill(ResimWindow w, int j, in KartSample s)
        {
            w.position[j] = s.position;
            w.rotation[j] = s.rotation;
            w.velocity[j] = s.velocity;
            w.keys[j] = s.keys;
            w.steerCommand[j] = s.steerCommand;
        }

        // Ticks strictly between samples a and b (b newer), down to firstTick: Hermite position, slerped rotation, a's keys.
        void FillBetween(ResimWindow w, in KartSample a, in KartSample b, int firstTick, int fromTick)
        {
            var span = b.tick - a.tick;
            var dt = span * tick;
            for (var t = Mathf.Min(fromTick, b.tick - 1); t > a.tick && t >= firstTick; t--)
            {
                var u = (t - a.tick) / (float)span;
                var u2 = u * u;
                var u3 = u2 * u;
                var j = t - firstTick;
                w.position[j] = (2f * u3 - 3f * u2 + 1f) * a.position + (u3 - 2f * u2 + u) * dt * a.velocity
                                + (-2f * u3 + 3f * u2) * b.position + (u3 - u2) * dt * b.velocity;
                w.rotation[j] = Quaternion.Slerp(a.rotation, b.rotation, u);
                w.velocity[j] = Vector3.Lerp(a.velocity, b.velocity, u);
                w.keys[j] = a.keys;
                w.steerCommand[j] = Mathf.Lerp(a.steerCommand, b.steerCommand, u);
            }
            if (a.tick >= firstTick) Fill(w, a.tick - firstTick, a);
        }

        /// <summary>Result of a window this validator handed to the <see cref="Resimulator"/>.</summary>
        public void ReportWindow(ResimWindow w, ResimOutcome outcome)
        {
            pendingWindows = Mathf.Max(0, pendingWindows - 1);
            margins.windows++;
            var limit = DivergenceBase + DivergenceShareOfTravel * w.TravelledTo(outcome.index);
            var ratio = outcome.error / limit;
            // Still on track when it hit a wall: the rest of the window is no evidence either way.
            if (outcome.touched && ratio <= 1f)
            {
                margins.windowsTouched++;
                margins.divergence = Mathf.Max(margins.divergence, ratio);
                return;
            }
            var evaluated = margins.windows - margins.windowsTouched;
            if (ratio > 1f)
            {
                margins.windowsDiverged++;
                if (margins.windowsDiverged >= DivergedWindowsToFlag && margins.windowsDiverged >= DivergedShareToFlag * evaluated)
                    Report(FlagReason.Divergence, w.firstTick + outcome.index,
                        $"re-simulation {outcome.error:0.0} m off (limit {limit:0.0} m) in {margins.windowsDiverged} of {evaluated} windows");
                return;
            }
            margins.divergence = Mathf.Max(margins.divergence, ratio);
        }

        void Remember(in KartSample s)
        {
            historyHead = (historyHead + 1) % HistoryCapacity;
            history[historyHead] = s;
            historyCount = Mathf.Min(historyCount + 1, HistoryCapacity);
        }

        // i = 0 is the newest remembered sample.
        KartSample HistoryAt(int i) => history[(historyHead - i + HistoryCapacity) % HistoryCapacity];

        bool TryHistoryBefore(int maxTick, out KartSample sample)
        {
            for (var i = 0; i < historyCount; i++)
            {
                sample = HistoryAt(i);
                if (sample.tick <= maxTick) return true;
            }
            sample = default;
            return false;
        }

        void Report(FlagReason reason, int atTick, string detail)
        {
            if (!reported.Add(reason)) return;
            var v = new Violation(reason, atTick, atTick * tick, detail);
            violations.Add(v);
            Found?.Invoke(v);
        }
    }
}
