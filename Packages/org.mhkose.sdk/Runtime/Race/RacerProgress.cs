using System.Collections.Generic;
using MHK.Karts;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Tracks a kart's progress on the racing line: race distance, laps and lap times, wrong-way state and the last
    /// safe respawn point. A lap counts when the kart crosses the finish plane forwards after passing every key
    /// checkpoint in order.
    /// </summary>
    [RequireComponent(typeof(Kart))]
    public class RacerProgress : MonoBehaviour
    {
        /// <summary>Distance driven since the start, continuous across laps (negative behind the line at the start).</summary>
        public float RaceDistance { get; private set; }
        /// <summary>Current lap, 1..number of laps (0 before the first crossing of the line).</summary>
        public int Lap { get; private set; }
        public bool Finished { get; private set; }
        public float FinishTime { get; private set; }
        /// <summary>Race position, 1 = leading (written by the race ranking).</summary>
        public int Position { get; internal set; } = 1;
        public bool WrongWay { get; private set; }
        /// <summary>Main-line distance of the last point where the kart was safely on the track.</summary>
        public float LastSafeDistance { get; private set; }
        /// <summary>Main-line distance from node 0 (mapped onto the main line while on an alternative route).</summary>
        public float PathDistance { get; private set; }
        /// <summary>Horizontal distance from the line the kart is following (main line or alternative route).</summary>
        public float LateralOffset { get; private set; }
        /// <summary>
        /// How far the kart is outside the road of the line it follows (height-weighted distance to the main line or
        /// alternative route minus half the road width, 0 on the road).
        /// </summary>
        public float OffRouteDistance { get; private set; }
        /// <summary>Main-line segment of the projection.</summary>
        public int PathSegment => segment;
        /// <summary>Alternative route the kart is on, null on the main line.</summary>
        public TrackPath CurrentBranch => branch >= 0 && track != null ? track.Routes[branch] : null;
        /// <summary>Distance along <see cref="CurrentBranch"/>.</summary>
        public float BranchDistance => branchDistance;
        public List<float> LapTimes { get; } = new();
        public float BestLap { get; private set; } = -1f;
        /// <summary>Explicit respawn pose from a respawn zone, used until the kart is back on the racing line.</summary>
        public Pose? RespawnOverride { get; private set; }
        /// <summary>Race clock of this racer: physics steps since GO × the step.</summary>
        public float RaceClock => raceClock;
        /// <summary>Race clock at the line crossing that started the current lap (the end of the last completed lap).</summary>
        public float LapStartTime => lapStartTime;

        public void SetRespawnOverride(Pose pose) => RespawnOverride = pose;

        /// <summary>
        /// An online remote racer: lap and race distance come from its own client (<see cref="ApplyRemote"/>) and its
        /// finish from the server; the projection still runs for the minimap, items and targeting.
        /// </summary>
        public bool Remote { get; private set; }

        /// <summary>Takes a remote racer's lap and race distance as its client reports them (ranking).</summary>
        public void ApplyRemote(int lap, float raceDistance)
        {
            Remote = true;
            if (Finished) return;
            Lap = Mathf.Clamp(lap, 0, totalLaps);
            RaceDistance = raceDistance;
        }

        /// <summary>Records lap times a remote racer reported with its finish (seconds).</summary>
        public void SetRemoteLapTimes(IReadOnlyList<float> laps)
        {
            LapTimes.Clear();
            BestLap = -1f;
            for (var i = 0; i < laps.Count; i++)
            {
                LapTimes.Add(laps[i]);
                if (BestLap < 0f || laps[i] < BestLap) BestLap = laps[i];
            }
        }

        public event System.Action<RacerProgress> FinishedRace;

        /// <summary>Near the finish line (m of path distance) crossings are taken from the real line plane.</summary>
        const float LineZone = 25f;
        /// <summary>A passed key checkpoint is only taken back when the kart is this far behind it (m).</summary>
        const float KeyHysteresis = 5f;
        /// <summary>Height-weighted distance (m) by which a route must be closer before the kart switches to it.</summary>
        const float RouteHysteresis = 1f;
        const float ReacquireInterval = 0.2f;
        const float ReacquireMinScore = 8f;
        const float ReacquireGain = 0.6f;
        /// <summary>Along-line movement (m) allowed per step before candidates are penalised, plus a share of the speed.</summary>
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
        /// <summary>cos 135°: facing further than this away from the track direction is wrong way (MHK2).</summary>
        const float WrongWayCos = -0.7071f;
        /// <summary>MHK2: a respawn position stays valid within one road width of either edge (u in [-1, 2]).</summary>
        const float SafeLateralWidths = 1.5f;

        Kart kart;
        TrackRuntime track;
        int segment = -1;
        int branch = -1;
        int branchSegment = -1;
        float branchDistance;
        int safeBranch = -1;
        float safeBranchDistance;
        float lapDistance;
        float lineSide;
        float reacquireTimer;
        bool relocate;
        float routeScore;

        // Lap state: crossings = forward finish-line crossings that counted (1 during lap 1), stage = key
        // checkpoints passed since. undoStage: stage to restore when the kart backs over the line again right
        // after crossing it (-1 = that crossing counted a lap and is undone).
        int crossings;
        int lapsCounted;
        int stage;
        int undoStage;
        bool lastCrossForward;

        int totalLaps = 3;
        float raceClock;
        // Physics steps since GO: the clock is ticks × dt, not a running float sum (which drifts by tens of ms per race).
        int clockTicks;
        float lapStartTime;
        float wrongWayTimer;
        bool running;

        void Awake() => kart = GetComponent<Kart>();

        /// <summary>Places the racer on <paramref name="t"/> before the start: lap state from its grid position.</summary>
        public void Begin(TrackRuntime t, int laps)
        {
            track = t;
            totalLaps = laps;
            segment = -1;
            branch = -1;
            safeBranch = -1;
            relocate = false;
            Locate(true);
            var len = track.MainPath.Length;
            var rawLap = PathDistance;
            lineSide = SideOfLine(rawLap);
            lapDistance = DistanceInLap(rawLap, lineSide);
            InitLapState(len);
            Lap = Mathf.Min(crossings, totalLaps);
            RaceDistance = (crossings - 1) * len + lapDistance;
            LastSafeDistance = PathDistance;
            Finished = false;
            FinishTime = 0f;
            WrongWay = false;
            wrongWayTimer = 0f;
            LapTimes.Clear();
            BestLap = -1f;
        }

        void InitLapState(float len)
        {
            var path = track.MainPath;
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

        public void StartClock()
        {
            running = true;
            raceClock = 0;
            clockTicks = 0;
            lapStartTime = 0;
        }

        public void StopClock() => running = false;

        void FixedUpdate()
        {
            if (track == null || !track.HasMainPath) return;
            var dt = Time.fixedDeltaTime;
            if (running) raceClock = (float)(++clockTicks * (double)dt);

            var prevLap = lapDistance;
            var prevSide = lineSide;
            Locate(relocate);
            relocate = false;
            UpdateLap(prevLap, prevSide, dt);
            var line = CurrentBranch;
            UpdateWrongWay(line, dt);
            UpdateSafePoint(line);
        }

        void UpdateWrongWay(TrackPath line, float dt)
        {
            var tangent = line != null ? line.GetTangent(branchDistance, TangentSmoothing) : track.MainPath.GetTangent(PathDistance, TangentSmoothing);
            var facing = Vector3.Dot(transform.forward, tangent);
            if (facing < WrongWayCos) wrongWayTimer += dt;
            else wrongWayTimer = 0f;
            WrongWay = wrongWayTimer > Core.GameRoot.Instance.Config.wrongWaySeconds;
        }

        static bool InBattle => RaceManager.Current != null && RaceManager.Current.IsBattle;

        void UpdateSafePoint(TrackPath line)
        {
            var width = line != null ? line.GetWidth(branchDistance) : track.MainPath.GetWidth(PathDistance);
            OffRouteDistance = Mathf.Max(0f, routeScore - 0.5f * width);
            // Battles have no wrong way (karts drive both directions): respawns there come back where the kart last was.
            if (kart == null || !kart.Physics.IsGrounded || !(LateralOffset < width * SafeLateralWidths) || (WrongWay && !InBattle)) return;
            LastSafeDistance = PathDistance;
            safeBranch = branch;
            safeBranchDistance = branchDistance;
            RespawnOverride = null;
        }

        void Locate(bool reset)
        {
            var path = track.MainPath;
            var pos = transform.position;
            var alts = track.Routes;
            if (branch >= 0 && branch < alts.Count) segment = path.SegmentAt(track.MapToMain(branch, branchDistance));
            else branch = -1;
            var allowance = ProjectionAllowance();
            var dMain = path.Track(pos, ref segment, reset ? float.NaN : PathDistance, allowance, out var latMain, out var scoreMain);
            ReacquireIfLost(path, pos, reset, ref dMain, ref latMain, ref scoreMain);
            if (branch >= 0 && TryStayOnBranch(alts[branch], pos, reset, allowance, scoreMain)) return;
            if (TryEnterBranch(path, pos, reset, dMain, latMain, scoreMain)) return;
            SetProjection(dMain, latMain, scoreMain);
        }

        float ProjectionAllowance()
        {
            var speed = kart != null && kart.Physics != null && kart.Physics.Body != null ? kart.Physics.Body.linearVelocity.magnitude : 0f;
            return BaseAllowance + AllowanceSpeedFactor * speed * Time.fixedDeltaTime;
        }

        // Lost the local window (cut across an unmapped shortcut, pushed, teleported): global search, taken only when
        // clearly better so parallel sections never steal a kart that is on its road.
        void ReacquireIfLost(TrackPath path, Vector3 pos, bool reset, ref float dMain, ref float latMain, ref float scoreMain)
        {
            reacquireTimer -= Time.fixedDeltaTime;
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

        bool TryStayOnBranch(TrackPath alt, Vector3 pos, bool reset, float allowance, float scoreMain)
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

        bool TryEnterBranch(TrackPath path, Vector3 pos, bool reset, float dMain, float latMain, float scoreMain)
        {
            var alts = track.Routes;
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
            routeScore = score;
        }

        float SignedLapOffset(float lap)
        {
            var len = track.MainPath.Length;
            return lap > len * 0.5f ? lap - len : lap;
        }

        float SideOfLine(float lap)
        {
            var s = SignedLapOffset(lap);
            return Mathf.Abs(s) < LineZone ? track.MainPath.SignedDistanceToFinish(transform.position) : s;
        }

        float DistanceInLap(float lap, float side)
        {
            var len = track.MainPath.Length;
            if (Mathf.Abs(SignedLapOffset(lap)) >= LineZone) return lap;
            return side >= 0f ? side : len + side;
        }

        void UpdateLap(float prevLap, float prevSide, float dt)
        {
            var len = track.MainPath.Length;
            var rawLap = PathDistance;
            lineSide = SideOfLine(rawLap);
            // Near the line the lap distance follows the line plane, so it wraps exactly at the crossing.
            lapDistance = DistanceInLap(rawLap, lineSide);
            if (Finished || Remote) return;

            var near = Mathf.Abs(prevSide) < len * LineNearShare && Mathf.Abs(lineSide) < len * LineNearShare;
            if (near && prevSide < 0f && lineSide >= 0f)
            {
                var frac = prevSide / (prevSide - lineSide);
                CrossForward(raceClock - dt * (1f - frac));
            }
            else if (near && prevSide >= 0f && lineSide < 0f) CrossBackward();
            else UpdateKeys(prevLap, lapDistance, len);
            if (!Finished) RaceDistance = (crossings - 1) * len + lapDistance;
        }

        void UpdateKeys(float prevLap, float lap, float len)
        {
            if (Mathf.Abs(lap - prevLap) > len * 0.5f) return;
            var path = track.MainPath;
            var n = path.CheckpointCount;
            while (stage < n && prevLap < path.GetCheckpointDistance(stage) && lap >= path.GetCheckpointDistance(stage)) stage++;
            while (stage > 0 && lap < path.GetCheckpointDistance(stage - 1) - KeyHysteresis) stage--;
        }

        void CrossForward(float crossTime)
        {
            lastCrossForward = true;
            if (stage < track.MainPath.CheckpointCount)
            {
                undoStage = stage;
                stage = 0;
                return;
            }
            stage = 0;
            undoStage = -1;
            crossings++;
            Lap = Mathf.Min(crossings, totalLaps);
            if (crossings <= lapsCounted) return; // re-crossing after backing over the line
            lapsCounted = crossings;
            if (crossings == 1)
            {
                lapStartTime = crossTime;
                return;
            }
            if (running) RecordLap(crossTime);
            if (crossings - 1 >= totalLaps) Finish(crossTime);
        }

        void RecordLap(float crossTime)
        {
            var lapTime = crossTime - lapStartTime;
            LapTimes.Add(lapTime);
            if (BestLap < 0 || lapTime < BestLap) BestLap = lapTime;
            lapStartTime = crossTime;
        }

        void Finish(float crossTime)
        {
            Finished = true;
            FinishTime = crossTime;
            RaceDistance = totalLaps * track.MainPath.Length;
            FinishedRace?.Invoke(this);
        }

        void CrossBackward()
        {
            if (lastCrossForward && stage == 0)
            {
                if (undoStage < 0)
                {
                    crossings--;
                    stage = track.MainPath.CheckpointCount;
                }
                else stage = undoStage;
            }
            else stage = 0;
            lastCrossForward = false;
            Lap = Mathf.Clamp(crossings, 0, totalLaps);
        }

        /// <summary>
        /// Respawn pose for a main-line distance relative to <see cref="LastSafeDistance"/> (e.g.
        /// <c>LastSafeDistance - 15</c>): on the alternative route the kart was last safe on, else on the main line.
        /// </summary>
        public Pose GetRespawnPose(float distance, float lateralOffset = 0f)
        {
            if (track == null) return new Pose(transform.position, transform.rotation);
            ResolveSafeLine(distance, out var line, out var d);
            return track.GetRespawnPose(line, d, lateralOffset);
        }

        /// <summary>
        /// <paramref name="distance"/> (as for <see cref="GetRespawnPose"/>) moved back so at least
        /// <paramref name="runUp"/> m of road lie between the respawn point and the next gap ahead (<see cref="TrackGaps"/>):
        /// from a standstill right before a jump a kart cannot reach its take-off speed.
        /// </summary>
        public float BeforeGaps(float distance, float runUp)
        {
            if (track == null) return distance;
            for (var ahead = 0f; ahead < runUp; ahead += TrackGaps.Step)
                if (IsGap(distance + ahead)) return distance - (runUp - ahead);
            return distance;
        }

        bool IsGap(float distance)
        {
            ResolveSafeLine(distance, out var line, out var d);
            return TrackGaps.IsGap(track, line, d);
        }

        void ResolveSafeLine(float distance, out TrackPath line, out float lineDistance)
        {
            var alts = track.Routes;
            line = track.MainPath;
            lineDistance = distance;
            if (safeBranch < 0 || safeBranch >= alts.Count || alts[safeBranch] == null) return;
            var d = safeBranchDistance + (distance - LastSafeDistance);
            // A collapsed bridge ahead on the route: the main-line equivalent instead.
            if (d >= 0f && track.IsRouteCut(safeBranch, d)) return;
            if (d >= 0f) line = alts[safeBranch];
            lineDistance = d >= 0f ? d : track.BranchJoinStart(safeBranch) + d;
        }

        /// <summary>Call after teleporting (respawn): the next step re-locates the kart from scratch.</summary>
        public void ResetProjection()
        {
            if (track == null) return;
            relocate = true;
            branch = safeBranch;
            branchDistance = safeBranchDistance;
            branchSegment = -1;
            segment = track.MainPath.SegmentAt(LastSafeDistance);
        }

        /// <summary>Ends the race for this racer at <paramref name="estimatedTime"/> (it did not cross the line).</summary>
        public void ForceFinish(float estimatedTime)
        {
            if (Finished) return;
            Finished = true;
            FinishTime = estimatedTime;
            FinishedRace?.Invoke(this);
        }
    }
}
