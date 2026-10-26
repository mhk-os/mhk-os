using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using MHK.Cameras;
using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Time-trial layer of a race: loads and spawns ghosts, records and stores the player's run, and raises lap splits.
    /// In a replay the watched ghost is the camera target.
    /// </summary>
    public class TimeTrialRun : MonoBehaviour
    {
        // Rigs spawned per frame while the race loads: as many as fit this time, so the loading screen keeps moving.
        const float SpawnBudgetSeconds = 0.008f;
        // A detailed ghost keeps its detail until another one is this much nearer (distance factor), so ghosts at the
        // edge of the nearest few do not swap every frame.
        const float DetailKeepFactor = 0.8f;

        RaceManager race;
        GhostRecorder recorder;
        readonly List<GhostPlayer> ghosts = new();
        int lapsSeen;
        float[] detailScores;
        bool[] detailed;

        /// <summary>Ghosts in this race: raced ones (reference first), or the watched one in a replay.</summary>
        public IReadOnlyList<GhostPlayer> Ghosts => ghosts;
        public bool IsReplay { get; private set; }
        /// <summary>The ghost watched in a replay, null otherwise.</summary>
        public GhostPlayer Spectated { get; private set; }
        /// <summary>The ghost split times compare with (the personal best when raced), null without ghosts.</summary>
        public GhostPlayer Reference { get; private set; }
        /// <summary>Fingerprint of the loaded track (<see cref="GhostFingerprint.TrackHash"/>).</summary>
        public string TrackHash { get; private set; }
        /// <summary>The run recorded in this race (null until the race ended with the player past the line).</summary>
        public GhostData Run { get; private set; }
        /// <summary>How the run was stored, null when it was not.</summary>
        public GhostSaveResult Saved { get; private set; }
        /// <summary>The player crossed a lap line: lap (1-based), delta to the reference ghost (s, NaN without one).</summary>
        public event Action<int, float> Split;
        /// <summary>Every ghost of this race is spawned (<see cref="Ghosts"/> is complete; also in replays).</summary>
        public bool GhostsLoaded { get; private set; }
        /// <summary>Raised once when <see cref="GhostsLoaded"/> becomes true.</summary>
        public event Action GhostsReady;
        /// <summary>Share of the ghosts loaded and spawned (0..1) until <see cref="GhostsLoaded"/>.</summary>
        public float LoadProgress { get; private set; }

        static IGhostStore Store => GhostStores.Current;

        /// <summary>
        /// While the race loads: fingerprint, recorder and the ghosts (replay: the watched ghost as the camera target).
        /// Raced ghosts load over the next frames: the race waits for <see cref="GhostsLoaded"/>.
        /// </summary>
        public void Load(RaceManager raceManager)
        {
            race = raceManager;
            IsReplay = race.Session.IsReplay;
            ComputeTrackHash();
            var root = new GameObject("Ghosts").transform;
            root.SetParent(race.KartsRoot != null ? race.KartsRoot : transform, false);
            if (IsReplay)
            {
                LoadReplay(root);
                MarkGhostsLoaded();
                return;
            }
            if (race.LocalPlayer != null) recorder = GhostRecorder.Attach(race.LocalPlayer, race);
            StartCoroutine(LoadGhosts(PickGhosts(), root));
        }

        void MarkGhostsLoaded()
        {
            LoadProgress = 1f;
            GhostsLoaded = true;
            GhostsReady?.Invoke();
        }

        void ComputeTrackHash()
        {
            try
            {
                TrackHash = GhostFingerprint.TrackHash(race.Track, race.TrackRuntime);
                Store.RememberTrackHash(race.Track.Id, TrackHash);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TimeTrial] Track fingerprint failed: {e.Message}");
            }
        }

        void LoadReplay(Transform root)
        {
            var id = race.Session.replayGhostId;
            var entry = Store.Find(id);
            var data = Store.Load(entry);
            if (data == null)
            {
                Debug.LogError($"[TimeTrial] Replay ghost '{id}' not found.");
                return;
            }
            Spectated = GhostPlayer.Spawn(data, entry, root, race, true);
            ghosts.Add(Spectated);
            var camera = race.ChaseCamera;
            if (camera == null) return;
            camera.Target = Spectated;
            camera.SnapToTarget();
        }

        // Own best first, then the session's ghosts in the order picked; a downloaded one counts like a stored one.
        List<GhostEntry> PickGhosts()
        {
            var session = race.Session;
            var max = Mathf.Max(1, GameRoot.Instance.Config.maxGhosts);
            var picked = new List<GhostEntry>();
            var ids = new HashSet<string>();
            if (session.raceOwnBest)
            {
                var best = Store.Best(race.Track.Id, session.difficulty);
                if (best != null && ids.Add(best.Id)) picked.Add(best);
            }
            foreach (var id in session.ghostIds)
            {
                if (picked.Count >= max) break;
                if (string.IsNullOrEmpty(id) || ids.Contains(id)) continue;
                var entry = Store.Find(id);
                if (entry != null && entry.header.trackId == race.Track.Id && ids.Add(entry.Id)) picked.Add(entry);
            }
            return picked;
        }

        // The files decode in parallel on worker threads (GhostFile uses no Unity API); the rigs spawn in pick order on
        // the main thread, spread over frames.
        IEnumerator LoadGhosts(List<GhostEntry> picked, Transform root)
        {
            var started = Time.realtimeSinceStartup;
            var store = Store;
            var loads = new Task<GhostData>[picked.Count];
            for (var i = 0; i < picked.Count; i++)
            {
                var entry = picked[i];
                loads[i] = Task.Run(() => store.Load(entry));
            }
            var frameStart = Time.realtimeSinceStartup;
            for (var i = 0; i < picked.Count; i++)
            {
                while (!loads[i].IsCompleted)
                {
                    yield return null;
                    frameStart = Time.realtimeSinceStartup;
                }
                if (loads[i].IsFaulted) Debug.LogWarning($"[TimeTrial] Could not load ghost {picked[i].Id}: {loads[i].Exception?.GetBaseException().Message}");
                var data = loads[i].Status == TaskStatus.RanToCompletion ? loads[i].Result : null;
                if (data != null) ghosts.Add(GhostPlayer.Spawn(data, picked[i], root, race, false));
                LoadProgress = (i + 1f) / picked.Count;
                if (i + 1 < picked.Count && Time.realtimeSinceStartup - frameStart > SpawnBudgetSeconds)
                {
                    yield return null;
                    frameStart = Time.realtimeSinceStartup;
                }
            }
            Reference = ghosts.Count > 0 ? ghosts[0] : null;
            Debug.Log($"[TimeTrial] {race.Track.Id}: {ghosts.Count} ghost(s) in {Time.realtimeSinceStartup - started:0.00} s, track hash {TrackHash}.");
            MarkGhostsLoaded();
        }

        /// <summary>At GO: the recording starts (tick 0).</summary>
        public void OnGo()
        {
            if (recorder != null) recorder.Begin();
        }

        void Update()
        {
            if (race == null) return;
            if (IsReplay) UpdateReplayCamera();
            else if (race.LocalPlayer != null) CheckSplit(race.LocalPlayer);
        }

        // After the ghosts moved: the nearest few to the camera (plus the reference and a watched ghost) are shown in full.
        void LateUpdate()
        {
            if (race == null || !GhostsLoaded || ghosts.Count == 0) return;
            var count = ghosts.Count;
            var budget = Mathf.Max(1, GameRoot.Instance.Config.ghostDetailCount);
            var cam = race.RaceCamera;
            if (detailed == null || detailed.Length != count)
            {
                detailed = new bool[count];
                detailScores = new float[count];
            }
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            for (var i = 0; i < count; i++)
            {
                var g = ghosts[i];
                detailed[i] = g == Reference || g == Spectated || cam == null;
                detailScores[i] = g.isActiveAndEnabled
                    ? (g.transform.position - eye).sqrMagnitude * (g.Detailed ? DetailKeepFactor * DetailKeepFactor : 1f)
                    : float.PositiveInfinity;
            }
            for (var n = 0; n < budget; n++)
            {
                var nearest = -1;
                for (var i = 0; i < count; i++)
                    if (!detailed[i] && detailScores[i] < float.PositiveInfinity && (nearest < 0 || detailScores[i] < detailScores[nearest]))
                        nearest = i;
                if (nearest < 0) break;
                detailed[nearest] = true;
            }
            for (var i = 0; i < count; i++) ghosts[i].SetDetailed(detailed[i]);
        }

        void UpdateReplayCamera()
        {
            var camera = race.ChaseCamera;
            if (Spectated == null || camera == null) return;
            if (Spectated.Finished && camera.CurrentMode == ChaseCamera.Mode.Chase) camera.CurrentMode = ChaseCamera.Mode.Finished;
            else if (!Spectated.Finished && camera.CurrentMode == ChaseCamera.Mode.Finished) camera.CurrentMode = ChaseCamera.Mode.Chase;
        }

        /// <summary>Recorded length of the watched ghost's run (s from GO), 0 outside replays.</summary>
        public float ReplayLength => Spectated != null ? Spectated.Data.Duration : 0f;

        /// <summary>
        /// Replays only, from GO: jumps the race clock to <paramref name="seconds"/> (clamped to the recording) and
        /// shows the watched ghost and the camera as they are at that time.
        /// </summary>
        public void Seek(float seconds)
        {
            if (!IsReplay || Spectated == null || !race.ClockRunning) return;
            var dt = (double)Time.fixedDeltaTime;
            var share = race.RenderSeconds / dt - race.RaceTicks;
            race.SeekReplayTicks((int)Math.Round(Mathf.Clamp(seconds, 0f, ReplayLength) / dt - share));
            Spectated.Resync(race.RenderSeconds);
            UpdateReplayCamera();
            if (race.ChaseCamera != null) race.ChaseCamera.SnapToTarget();
        }

        void CheckSplit(Kart player)
        {
            var laps = player.Progress.LapTimes.Count;
            if (laps <= lapsSeen) return;
            lapsSeen = laps;
            var reference = Reference != null ? Reference.Data.header : null;
            var delta = reference != null && reference.lapTimes.Length >= laps
                ? player.Progress.LapStartTime - reference.LapEndTime(laps)
                : float.NaN;
            Split?.Invoke(laps, delta);
        }

        /// <summary>The watched ghost's recording is over (<see cref="GameConfig.replayEndSeconds"/> after its last tick).</summary>
        public bool ReplayOver => Spectated == null || Spectated.ShownTime >= Spectated.Data.Duration + GameRoot.Instance.Config.replayEndSeconds;

        /// <summary>At the end of the race: stops the recording and stores the run when the player finished.</summary>
        public void Complete()
        {
            if (recorder == null) return;
            recorder.Stop();
            var player = race.LocalPlayer;
            if (player == null || !player.Progress.Finished || recorder.FrameCount < 2 || Run != null) return;
            var root = GameRoot.Instance;
            var hadId = !string.IsNullOrEmpty(root.Settings.playerId);
            var header = CreateRunHeader(player, root);
            if (!hadId) root.SaveSettings();
            Run = recorder.Build(header);
            StoreRun();
        }

        GhostHeader CreateRunHeader(Kart player, GameRoot root)
        {
            var settings = root.Settings;
            var track = race.Track;
            var session = race.Session;
            return new GhostHeader
            {
                id = Guid.NewGuid().ToString("N"),
                gameBuild = BuildInfo.Commit,
                simVersion = SimulationVersion.Current,
                // The account name when there is one: the server lists runs under it, the file should not disagree.
                playerName = settings.SignatureName,
                playerId = settings.EnsurePlayerId(),
                dateUtcTicks = DateTime.UtcNow.Ticks,
                trackId = track.Id,
                trackPackId = track.OwnerPackId,
                trackPackVersion = track.OwnerPackCommit,
                trackHash = TrackHash,
                kartId = player.KartDef != null ? player.KartDef.Id : "",
                characterId = player.Character != null ? player.Character.Id : "",
                engineClass = (int)session.difficulty,
                engineClassLabel = root.Config.GetDifficulty(session.difficulty).label,
                laps = race.Laps,
                setupHash = GhostFingerprint.SetupHash(session.difficulty, player.KartDef, race.Laps),
                tickSeconds = Time.fixedDeltaTime,
                speedScale = player.Physics.SpeedScale,
                finishTime = player.Progress.FinishTime,
                lapTimes = player.Progress.LapTimes.ToArray(),
            };
        }

        void StoreRun()
        {
            try
            {
                Saved = Store.SaveRun(Run);
                if (Saved.newRecord) MHK.Online.OnlineTimeTrials.AutoUploadBest(Saved.entry);
                Debug.Log($"[TimeTrial] Run {UI.UiText.Clock(Run.header.finishTime)} stored ({Saved.entry?.kind}, " +
                          $"new record: {Saved.newRecord}, {Run.frames.Length} ticks).");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TimeTrial] Could not store the run: {e.Message}");
            }
        }

        /// <summary>A session that replays <paramref name="entry"/> on its track and engine class.</summary>
        public static GameSession ReplaySession(GhostEntry entry)
        {
            var s = new GameSession
            {
                mode = GameMode.TimeTrial,
                difficulty = (Difficulty)entry.header.engineClass,
                powerupsEnabled = false,
                replayGhostId = entry.Id,
            };
            s.trackIds.Add(entry.header.trackId);
            return s;
        }
    }
}
