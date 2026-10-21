using System;
using System.Collections.Generic;
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
        RaceManager race;
        GhostRecorder recorder;
        readonly List<GhostPlayer> ghosts = new();
        int lapsSeen;

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

        static IGhostStore Store => GhostStores.Current;

        /// <summary>While the race loads: fingerprint, ghosts, recorder (replay: the watched ghost as the camera target).</summary>
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
                return;
            }
            SpawnGhosts(PickGhosts(), root);
            Reference = ghosts.Count > 0 ? ghosts[0] : null;
            if (race.LocalPlayer != null) recorder = GhostRecorder.Attach(race.LocalPlayer, race);
            Debug.Log($"[TimeTrial] {race.Track.Id}: {ghosts.Count} ghost(s), track hash {TrackHash}.");
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

        List<GhostEntry> PickGhosts()
        {
            var session = race.Session;
            var max = Mathf.Max(1, GameRoot.Instance.Config.maxGhosts);
            var picked = new List<GhostEntry>();
            if (session.raceOwnBest)
            {
                var best = Store.Best(race.Track.Id, session.difficulty);
                if (best != null) picked.Add(best);
            }
            foreach (var id in session.ghostIds)
            {
                if (picked.Count >= max) break;
                if (picked.Exists(e => e.Id == id)) continue;
                var entry = Store.Find(id);
                if (entry != null && entry.header.trackId == race.Track.Id) picked.Add(entry);
            }
            return picked;
        }

        void SpawnGhosts(List<GhostEntry> picked, Transform root)
        {
            foreach (var entry in picked)
            {
                var data = Store.Load(entry);
                if (data != null) ghosts.Add(GhostPlayer.Spawn(data, entry, root, race, false));
            }
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

        void UpdateReplayCamera()
        {
            var camera = race.ChaseCamera;
            if (Spectated != null && Spectated.Finished && camera != null && camera.CurrentMode == ChaseCamera.Mode.Chase)
                camera.CurrentMode = ChaseCamera.Mode.Finished;
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
