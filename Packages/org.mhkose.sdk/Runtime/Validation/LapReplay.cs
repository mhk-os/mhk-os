using MHK.Race;
using UnityEngine;

namespace MHK.Validation
{
    /// <summary>What a <see cref="LapReplay.Step"/> saw at the finish line.</summary>
    public enum LapEventKind
    {
        None,
        /// <summary>First forward crossing: lap 1 starts.</summary>
        FirstCrossing,
        /// <summary>A lap was completed (<see cref="LapEvent.lap"/>).</summary>
        Lap,
        /// <summary>Crossed forwards without having passed every key checkpoint: no lap.</summary>
        Uncounted,
    }

    public readonly struct LapEvent
    {
        public readonly LapEventKind kind;
        /// <summary>Completed lap (1-based) for <see cref="LapEventKind.Lap"/>.</summary>
        public readonly int lap;
        /// <summary>Race time of the crossing (s from GO, interpolated on the line plane).</summary>
        public readonly float time;

        public LapEvent(LapEventKind kind, int lap, float time)
        {
            this.kind = kind;
            this.lap = lap;
            this.time = time;
        }
    }

    /// <summary>
    /// The lap logic of <see cref="RacerProgress"/> (projection onto the racing line and its routes, key checkpoints,
    /// finish-line crossings, safe point for respawns) replayed over observed kart states instead of a live kart. Kept
    /// step for step equal to RacerProgress, so a lap the race counted is counted here at the same crossing time.
    /// </summary>
    public sealed class LapReplay
    {
        const float LineZone = 25f;
        const float KeyHysteresis = 5f;
        const float RouteHysteresis = 1f;
        const float ReacquireInterval = 0.2f;
        const float ReacquireMinScore = 8f;
        const float ReacquireGain = 0.6f;
        const float BaseAllowance = 3f;
        const float AllowanceSpeedFactor = 3f;
        const float JunctionMargin = 0.5f;
        const float OffMainRoadMax = 4f;
        const float BranchBoundsMinWidth = 10f;
        const float BranchBoundsWidths = 2f;
        const float BranchTolerance = 30f;
        const float BranchToleranceSpanShare = 0.5f;
        const float LineNearShare = 0.25f;
        const float TangentSmoothing = 3f;
        const float WrongWayCos = -0.7071f;
        const float SafeLateralWidths = 1.5f;

        readonly TrackRuntime track;
        readonly int totalLaps;
        readonly float wrongWaySeconds;

        Vector3 pos, forward;
        float speed, dt;
        int segment = -1, branch = -1, branchSegment = -1;
        float branchDistance;
        int safeBranch = -1;
        float safeBranchDistance;
        float lapDistance, lineSide, reacquireTimer, wrongWayTimer;
        bool relocate, wrongWay;
        int crossings, lapsCounted, stage, undoStage;
        bool lastCrossForward;

        /// <summary>Main-line distance from node 0.</summary>
        public float PathDistance { get; private set; }
        public float LateralOffset { get; private set; }
        public float LastSafeDistance { get; private set; }
        public bool Finished { get; private set; }
        /// <summary>The track has a racing line to replay on.</summary>
        public bool Valid => track != null && track.HasMainPath;

        public LapReplay(TrackRuntime track, int laps, float wrongWaySeconds)
        {
            this.track = track;
            totalLaps = Mathf.Max(1, laps);
            this.wrongWaySeconds = wrongWaySeconds;
        }

        /// <summary>The kart's state at GO (RacerProgress.Begin on the grid).</summary>
        public void Begin(Vector3 position, Vector3 kartForward)
        {
            if (!Valid) return;
            pos = position;
            forward = kartForward;
            speed = 0f;
            dt = 0f;
            segment = branch = safeBranch = -1;
            relocate = false;
            Locate(true);
            var len = track.path.Length;
            lineSide = SideOfLine(PathDistance);
            lapDistance = DistanceInLap(PathDistance, lineSide);
            InitLapState(len);
            LastSafeDistance = PathDistance;
            Finished = false;
            wrongWay = false;
            wrongWayTimer = 0f;
        }

        void InitLapState(float len)
        {
            var path = track.path;
            if (lapDistance > len * 0.5f)
            {
                crossings = 0;
                stage = path.CheckpointCount;
            }
            else
            {
                crossings = 1;
                stage = 0;
                while (stage < path.CheckpointCount && lapDistance >= path.GetCheckpointDistance(stage)) stage++;
            }
            lapsCounted = crossings;
            undoStage = -1;
            lastCrossForward = false;
        }

        /// <summary>
        /// One observed state, <paramref name="seconds"/> after the previous one, at race time <paramref name="raceClock"/>.
        /// <paramref name="cut"/>: the kart was respawned since (RacerProgress.ResetProjection).
        /// </summary>
        public LapEvent Step(Vector3 position, Vector3 kartForward, float kartSpeed, bool grounded, float raceClock, float seconds, bool cut)
        {
            if (!Valid) return default;
            if (cut) ResetProjection();
            pos = position;
            forward = kartForward;
            speed = kartSpeed;
            dt = seconds;
            var prevLap = lapDistance;
            var prevSide = lineSide;
            Locate(relocate);
            relocate = false;
            var e = UpdateLap(prevLap, prevSide, raceClock);
            var line = CurrentBranch;
            UpdateWrongWay(line);
            UpdateSafePoint(line, grounded);
            return e;
        }

        TrackPath CurrentBranch => branch >= 0 ? track.alternatePaths[branch] : null;

        void UpdateWrongWay(TrackPath line)
        {
            var tangent = line != null ? line.GetTangent(branchDistance, TangentSmoothing) : track.path.GetTangent(PathDistance, TangentSmoothing);
            if (Vector3.Dot(forward, tangent) < WrongWayCos) wrongWayTimer += dt;
            else wrongWayTimer = 0f;
            wrongWay = wrongWayTimer > wrongWaySeconds;
        }

        void UpdateSafePoint(TrackPath line, bool grounded)
        {
            var width = line != null ? line.GetWidth(branchDistance) : track.path.GetWidth(PathDistance);
            if (!grounded || !(LateralOffset < width * SafeLateralWidths) || wrongWay) return;
            LastSafeDistance = PathDistance;
            safeBranch = branch;
            safeBranchDistance = branchDistance;
        }

        void Locate(bool reset)
        {
            var path = track.path;
            var alts = track.alternatePaths;
            if (branch >= 0 && branch < alts.Count) segment = path.SegmentAt(track.MapToMain(branch, branchDistance));
            else branch = -1;
            var allowance = BaseAllowance + AllowanceSpeedFactor * speed * dt;
            var dMain = path.Track(pos, ref segment, reset ? float.NaN : PathDistance, allowance, out var latMain, out var scoreMain);
            ReacquireIfLost(path, reset, ref dMain, ref latMain, ref scoreMain);
            if (branch >= 0 && TryStayOnBranch(alts[branch], reset, allowance, scoreMain)) return;
            if (TryEnterBranch(path, reset, dMain, latMain, scoreMain)) return;
            SetProjection(dMain, latMain, scoreMain);
        }

        void ReacquireIfLost(TrackPath path, bool reset, ref float dMain, ref float latMain, ref float scoreMain)
        {
            reacquireTimer -= dt;
            if (!(reset || (branch < 0 && reacquireTimer <= 0f && scoreMain > Mathf.Max(ReacquireMinScore, path.GetWidth(dMain))))) return;
            reacquireTimer = ReacquireInterval;
            var g = -1;
            var dG = path.Project(pos, ref g, out var latG, out var scoreG);
            if (!(scoreG < scoreMain * ReacquireGain)) return;
            segment = g;
            dMain = dG;
            latMain = latG;
            scoreMain = scoreG;
        }

        bool TryStayOnBranch(TrackPath alt, bool reset, float allowance, float scoreMain)
        {
            var dA = alt.Track(pos, ref branchSegment, reset ? float.NaN : branchDistance, allowance, out var latA, out var scoreA);
            var atJunction = dA <= JunctionMargin || dA >= alt.Length - JunctionMargin;
            if ((atJunction && scoreMain <= scoreA + RouteHysteresis) || scoreMain + RouteHysteresis < scoreA)
            {
                branch = -1;
                return false;
            }
            branchDistance = dA;
            SetProjection(track.MapToMain(branch, dA), latA, scoreA);
            return true;
        }

        bool TryEnterBranch(TrackPath path, bool reset, float dMain, float latMain, float scoreMain)
        {
            var alts = track.alternatePaths;
            var best = -1;
            var bestScore = scoreMain - RouteHysteresis;
            float bestD = 0f, bestLat = 0f;
            var bestSeg = -1;
            var offMainRoad = reset || latMain > Mathf.Min(0.5f * path.GetWidth(dMain), OffMainRoadMax);
            for (var i = 0; offMainRoad && i < alts.Count; i++)
            {
                var alt = alts[i];
                if (alt == null || alt.Count < 2) continue;
                var bounds = alt.WorldBounds;
                bounds.Expand(BranchBoundsWidths * Mathf.Max(BranchBoundsMinWidth, alt.GetNodeWidth(0)));
                if (!bounds.Contains(pos)) continue;
                var h = -1;
                var dA = alt.Project(pos, ref h, out var latA, out var scoreA);
                if (scoreA >= bestScore || dA <= JunctionMargin || dA >= alt.Length - JunctionMargin) continue;
                if (!reset && !CoversLapPosition(path, i, dA)) continue;
                best = i;
                bestScore = scoreA;
                bestD = dA;
                bestLat = latA;
                bestSeg = h;
            }
            if (best < 0) return false;
            branch = best;
            branchSegment = bestSeg;
            branchDistance = bestD;
            SetProjection(track.MapToMain(best, bestD), bestLat, bestScore);
            return true;
        }

        bool CoversLapPosition(TrackPath path, int route, float routeDistance)
        {
            var tolerance = BranchTolerance + BranchToleranceSpanShare * track.BranchSpan(route);
            return !(Mathf.Abs(path.Delta(PathDistance, track.MapToMain(route, routeDistance))) > tolerance);
        }

        void SetProjection(float pathDistance, float lateral, float score)
        {
            PathDistance = pathDistance;
            LateralOffset = lateral;
        }

        float SignedLapOffset(float lap)
        {
            var len = track.path.Length;
            return lap > len * 0.5f ? lap - len : lap;
        }

        float SideOfLine(float lap)
        {
            var s = SignedLapOffset(lap);
            return Mathf.Abs(s) < LineZone ? track.path.SignedDistanceToFinish(pos) : s;
        }

        float DistanceInLap(float lap, float side)
        {
            var len = track.path.Length;
            if (Mathf.Abs(SignedLapOffset(lap)) >= LineZone) return lap;
            return side >= 0f ? side : len + side;
        }

        LapEvent UpdateLap(float prevLap, float prevSide, float raceClock)
        {
            var len = track.path.Length;
            lineSide = SideOfLine(PathDistance);
            lapDistance = DistanceInLap(PathDistance, lineSide);
            if (Finished) return default;
            var near = Mathf.Abs(prevSide) < len * LineNearShare && Mathf.Abs(lineSide) < len * LineNearShare;
            if (near && prevSide < 0f && lineSide >= 0f)
            {
                var frac = prevSide / (prevSide - lineSide);
                return CrossForward(raceClock - dt * (1f - frac));
            }
            if (near && prevSide >= 0f && lineSide < 0f) CrossBackward();
            else UpdateKeys(prevLap, lapDistance, len);
            return default;
        }

        void UpdateKeys(float prevLap, float lap, float len)
        {
            if (Mathf.Abs(lap - prevLap) > len * 0.5f) return;
            var path = track.path;
            var n = path.CheckpointCount;
            while (stage < n && prevLap < path.GetCheckpointDistance(stage) && lap >= path.GetCheckpointDistance(stage)) stage++;
            while (stage > 0 && lap < path.GetCheckpointDistance(stage - 1) - KeyHysteresis) stage--;
        }

        LapEvent CrossForward(float crossTime)
        {
            lastCrossForward = true;
            if (stage < track.path.CheckpointCount)
            {
                undoStage = stage;
                stage = 0;
                return new LapEvent(LapEventKind.Uncounted, 0, crossTime);
            }
            stage = 0;
            undoStage = -1;
            crossings++;
            if (crossings <= lapsCounted) return default;
            lapsCounted = crossings;
            if (crossings == 1) return new LapEvent(LapEventKind.FirstCrossing, 0, crossTime);
            if (crossings - 1 >= totalLaps) Finished = true;
            return new LapEvent(LapEventKind.Lap, crossings - 1, crossTime);
        }

        void CrossBackward()
        {
            if (lastCrossForward && stage == 0)
            {
                if (undoStage < 0)
                {
                    crossings--;
                    stage = track.path.CheckpointCount;
                }
                else stage = undoStage;
            }
            else stage = 0;
            lastCrossForward = false;
        }

        void ResetProjection()
        {
            relocate = true;
            branch = safeBranch;
            branchDistance = safeBranchDistance;
            branchSegment = -1;
            segment = track.path.SegmentAt(LastSafeDistance);
        }
    }
}
