using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MHK.AI;
using MHK.Audio;
using MHK.Cameras;
using MHK.Core;
using MHK.Ghosts;
using MHK.Input;
using MHK.Karts;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MHK.Race
{
    /// <summary>
    /// Builds and runs one race from the current <see cref="GameSession"/>: loads the track, spawns karts, runs the
    /// phases and publishes results. Execution order -100 so <see cref="RaceTime"/> already counts the current physics
    /// step for karts and ghost recorders.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public partial class RaceManager : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("Input actions asset the player drivers read (Kart map).")]
        [SerializeField] InputActionAsset controls;
        [Tooltip("Race camera following the local player.")]
        [SerializeField] ChaseCamera chaseCamera;
        [Tooltip("Spawned karts are parented here.")]
        [SerializeField] Transform kartsRoot;

        [Header("Debug")]
        [Tooltip("Track id used when the Race scene is played without a session.")]
        [SerializeField] string debugTrackId;
        [Tooltip("Computer opponents when the Race scene is played without a session.")]
        [SerializeField] int debugOpponents = 5;

        [AutoStaticsCleanup] public static RaceManager Current { get; private set; }

        public RacePhase Phase { get; private set; } = RacePhase.Loading;
        public TrackDefinition Track { get; private set; }
        public TrackRuntime TrackRuntime { get; private set; }
        public GameSession Session { get; private set; }
        public IReadOnlyList<Kart> Karts => karts;
        public Kart LocalPlayer { get; private set; }
        public int Laps { get; private set; }
        public List<RaceResult> Results { get; } = new();
        public bool Paused { get; private set; }
        /// <summary>Battle rules, null in circuit races.</summary>
        public BattleRules Battle { get; private set; }
        public bool IsBattle => Session != null && Session.mode == GameMode.Battle;
        /// <summary>Loading progress 0..1 during <see cref="RacePhase.Loading"/>: the track scene, then the karts.</summary>
        public float LoadProgress { get; private set; }
        /// <summary>The race could not be built (track missing or broken), it stays in <see cref="RacePhase.Loading"/>.</summary>
        public bool LoadFailed { get; private set; }
        /// <summary>Time trial (ghosts, recording, replay), null in the other modes.</summary>
        public TimeTrialRun TimeTrial { get; private set; }
        /// <summary>The race clock runs (from GO).</summary>
        public bool ClockRunning { get; private set; }
        /// <summary>Physics steps since GO.</summary>
        public int RaceTicks { get; private set; }
        /// <summary>
        /// Seconds left until GO while the intro and the countdown run (scaled frame time, so only visuals may follow
        /// it), held at their whole length while the track loads, 0 from GO. The <see cref="RaceClock"/> before GO.
        /// </summary>
        public float SecondsToGo { get; private set; }
        public ChaseCamera ChaseCamera => chaseCamera;
        public Transform KartsRoot => kartsRoot;
        /// <summary>Menu background race (<see cref="RunAsAttract"/>): no music, no intro, nobody to pause it.</summary>
        public bool IsAttract { get; private set; }
        /// <summary>The loaded track scene, invalid until it loaded.</summary>
        public UnityEngine.SceneManagement.Scene TrackScene { get; private set; }

        /// <summary>Camera the race is seen through: the chase camera's, else the main camera.</summary>
        public Camera RaceCamera
        {
            get
            {
                if (raceCamera == null) raceCamera = chaseCamera != null ? chaseCamera.GetComponent<Camera>() : Camera.main;
                return raceCamera;
            }
        }

        /// <summary>
        /// Race clock: seconds from GO, <see cref="RaceTicks"/> × the physics step (the same value as each racer's
        /// <see cref="RacerProgress.RaceClock"/>), 0 before GO.
        /// </summary>
        public float RaceTime => (float)(RaceTicks * (double)Time.fixedDeltaTime);

        /// <summary>
        /// Race time of the picture drawn now: <see cref="RaceTicks"/> plus the frame's share of the next physics step
        /// (rigidbody interpolation), exactly <see cref="RaceTime"/> inside a physics step. Double precision, so a
        /// ghost samples its ticks exactly however long the race.
        /// </summary>
        public double RenderSeconds
        {
            get
            {
                var dt = (double)Time.fixedDeltaTime;
                if (!ClockRunning || dt <= 0.0) return RaceTime;
                var share = Math.Min(Math.Max((Time.timeAsDouble - Time.fixedTimeAsDouble) / dt, 0.0), 1.0);
                return (RaceTicks + share) * dt;
            }
        }
        /// <summary><see cref="RenderSeconds"/> as a float (event stamps, displays).</summary>
        public float RenderTime => (float)RenderSeconds;

        public event Action<int> CountdownTick;
        public event Action<Kart> KartFinished;
        public event Action ResultsReady;
        public event Action<bool> PauseChanged;

        /// <summary>Share of <see cref="LoadProgress"/> taken by the track scene, spawning the karts (and ghosts) takes the rest.</summary>
        const float TrackLoadShare = 0.9f;
        /// <summary>Share of the rest taken by the karts in a time trial, loading the ghosts takes what is left.</summary>
        const float TimeTrialKartShare = 0.2f;
        const float IntroOrbitSeconds = 1.5f;
        const float MinFarClip = 10f;
        const float NearClip = 0.1f;
        const int MaxCountdownTick = 3;

        readonly List<Kart> karts = new();
        readonly List<PlayerKartDriver> humanDrivers = new();
        readonly RaceRanking ranking = new();
        readonly PowerStart powerStart = new();
        float finishTimer = -1f;
        int finishCounter;
        Vector3 defaultGravity;
        Camera raceCamera;
        float playbackSpeed = 1f;

        DifficultySettings Difficulty => GameRoot.Instance.Config.GetDifficulty(Session.difficulty);

        void Awake()
        {
            Current = this;
            defaultGravity = Physics.gravity;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            Physics.gravity = defaultGravity;
            foreach (var d in humanDrivers) d.Dispose();
            Time.timeScale = 1f;
        }

        IEnumerator Start()
        {
            if (!IsAttract) EnsureSession();
            if (!BindOnline())
            {
                // Not an error: the online race ended (left the lobby, connection lost) before this scene started.
                Debug.LogWarning("[Race] The online race ended before it loaded; back to the online menu.");
                LoadFailed = true;
                PrepareOnlineMenu();
                yield break;
            }
            Track = GameRoot.Instance.Content.Get<TrackDefinition>(Session.CurrentTrackId);
            if (Track == null || !Track.scene.IsSet)
            {
                FailLoad($"[Race] Track '{Session.CurrentTrackId}' not found or has no scene.");
                yield break;
            }
            Laps = LapsFor(Session, Track);
            SecondsToGo = IntroSeconds + GameRoot.Instance.Config.countdownSeconds;

            var loadStart = Time.realtimeSinceStartup;
            yield return LoadTrack();
            if (TrackRuntime == null)
            {
                FailLoad($"[Race] Track scene '{Track.scene.Path}' has no TrackRuntime.");
                yield break;
            }
            var trackLoaded = Time.realtimeSinceStartup;
            yield return null; // let colliders settle before placing karts
            yield return SpawnKarts();
            AttachModeRules();
            AttachOnline();
            yield return WaitForGhosts();
            Debug.Log($"[Race] {Track.Id}: track scene {trackLoaded - loadStart:0.00} s, karts and ghosts {Time.realtimeSinceStartup - trackLoaded:0.00} s.");
            if (!IsAttract) MusicPlayer.PlayTrack(Track);

            if (IsOnline)
            {
                // Ended while loading (connection lost, lobby gone): the loading screen goes, Update returns to the lobby.
                if (Online == null) Phase = RacePhase.Intro;
                else yield return RunOnlineStart();
                yield break;
            }
            yield return RunIntro();
            yield return RunCountdown();
            StartRacing();
        }

        // Time trials: the ghosts load over frames after the karts; the loading screen stays up until they are all there.
        IEnumerator WaitForGhosts()
        {
            if (TimeTrial == null) yield break;
            var from = KartsLoadedProgress;
            while (!TimeTrial.GhostsLoaded)
            {
                LoadProgress = from + (1f - from) * TimeTrial.LoadProgress;
                yield return null;
            }
            LoadProgress = 1f;
        }

        /// <summary>Share of <see cref="LoadProgress"/> reached when every kart is spawned.</summary>
        float KartsLoadedProgress => TrackLoadShare + (1f - TrackLoadShare) * (Session.mode == GameMode.TimeTrial ? TimeTrialKartShare : 1f);

        void EnsureSession()
        {
            var root = GameRoot.Instance;
            Session = root.Session;
            if (Session != null && (Session.racers.Count > 0 || Session.IsReplay) && Session.CurrentTrackId != null) return;
            Session = GameSession.CreateQuickRace(root.Content, string.IsNullOrEmpty(debugTrackId) ? null : debugTrackId, debugOpponents);
            root.Session = Session;
        }

        /// <summary>
        /// Runs <paramref name="session"/> as the menu's background race instead of the GameRoot's session. Call right
        /// after instantiating, before Start.
        /// </summary>
        public void RunAsAttract(GameSession session)
        {
            Session = session;
            IsAttract = true;
        }

        void FailLoad(string message)
        {
            Debug.LogError(message);
            LoadFailed = true;
            if (!IsOnline) return;
            // The server takes the racer out after its load timeout; here the race's UDP seat and overrides end now.
            if (MHK.Online.OnlineRace.Current != null) MHK.Online.OnlineRace.Current.End();
            PrepareOnlineMenu();
        }

        // A failed load returns to the main menu (SceneFlow): online it opens on the lobby, or the online page once out of it.
        static void PrepareOnlineMenu()
        {
            UI.MenuFlow.OpenOnStart = MHK.Online.OnlineLobby.InLobby ? typeof(UI.LobbyScreen) : typeof(UI.OnlineScreen);
        }

        IEnumerator LoadTrack()
        {
            Physics.gravity = Vector3.down * (Track.gravity > 0f ? Track.gravity : GameRoot.Instance.Config.gravity);
            UnityEngine.SceneManagement.Scene scene = default;
            yield return TrackSceneLoader.Load(Track.scene.Path, s => scene = s, p => LoadProgress = p * TrackLoadShare);
            TrackScene = scene;
            PropMass.Apply(scene, GameRoot.Instance.Config);
            TrackRuntime = TrackSceneLoader.Find(scene);
            if (TrackRuntime == null) yield break;
            WorldSkyDome.Apply(scene, Track, TrackRuntime, GameRoot.Instance.Config);
            SetUpCamera(RaceCamera);
        }

        void SetUpCamera(Camera cam)
        {
            if (cam == null) return;
            cam.farClipPlane = Mathf.Max(MinFarClip, TrackRuntime.farClip);
            cam.nearClipPlane = NearClip;
            cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            cam.backgroundColor = TrackRuntime.backgroundColor;
        }

        void AttachModeRules()
        {
            if (!IsAttract && !Session.IsReplay) gameObject.AddComponent<Achievements.RaceAchievements>().Begin(this);
            if (Session.mode == GameMode.TimeTrial)
            {
                TimeTrial = gameObject.AddComponent<TimeTrialRun>();
                TimeTrial.Load(this);
                gameObject.AddComponent<Validation.GhostValidation>();
            }
            if (!IsBattle) return;
            Battle = gameObject.AddComponent<BattleRules>();
            Battle.Begin(this);
        }

        /// <summary>
        /// Laps of a race on <paramref name="track"/>: the track's or the session's count, or the cup's override,
        /// int.MaxValue in battles (no finish line).
        /// </summary>
        public static int LapsFor(GameSession session, TrackDefinition track)
        {
            if (session == null) return track.defaultLaps;
            if (session.mode == GameMode.Battle) return int.MaxValue;
            var cup = GameRoot.Instance.Content.Get<CupDefinition>(session.cupId);
            if (cup != null && cup.lapsOverride > 0) return cup.lapsOverride;
            return session.lapsFromTrack ? track.defaultLaps : session.laps;
        }

        /// <summary>Computer driver of this race's engine class.</summary>
        public AIKartDriver CreateAiDriver(int seed) => new(Difficulty, seed);

        /// <summary>Lets the computer drive <paramref name="kart"/> (autopilot after the line, benchmarks).</summary>
        public void HandToAi(Kart kart, int seed) => kart.Driver = CreateAiDriver(seed);

        // The loading screen fades out (it waits for this phase) while the camera orbits the player's kart.
        IEnumerator RunIntro()
        {
            Phase = RacePhase.Intro;
            if (IntroSeconds <= 0f) yield break;
            chaseCamera.CurrentMode = ChaseCamera.Mode.Orbit;
            var countdown = GameRoot.Instance.Config.countdownSeconds;
            for (var left = IntroSeconds; left > 0f; left -= Time.deltaTime)
            {
                SecondsToGo = left + countdown;
                yield return null;
            }
            chaseCamera.CurrentMode = ChaseCamera.Mode.Chase;
            chaseCamera.SnapToTarget();
        }

        float IntroSeconds => chaseCamera == null || IsAttract ? 0f : IntroOrbitSeconds;

        IEnumerator RunCountdown()
        {
            Phase = RacePhase.Countdown;
            var cfg = GameRoot.Instance.Config;
            var difficulty = Difficulty;
            var countdown = cfg.countdownSeconds;
            var lastTick = int.MaxValue;
            powerStart.Clear();
            SecondsToGo = countdown;
            while (countdown > 0f)
            {
                countdown -= Time.deltaTime;
                SecondsToGo = Mathf.Max(0f, countdown);
                // MHK2: beeps when trunc(T + 1) changes to 3, 2, 1 and GO at 0.
                var tick = Mathf.FloorToInt(countdown + 1f);
                if (tick != lastTick && tick > 0 && tick <= MaxCountdownTick)
                {
                    lastTick = tick;
                    CountdownTick?.Invoke(tick);
                }
                powerStart.Sample(karts, countdown, cfg, difficulty);
                yield return null;
            }
            CountdownTick?.Invoke(0);
        }

        void StartRacing()
        {
            Phase = RacePhase.Racing;
            RaceTicks = 0;
            ClockRunning = true;
            SecondsToGo = 0f;
            var cfg = GameRoot.Instance.Config;
            foreach (var k in karts)
            {
                k.ControlsEnabled = true;
                k.Progress.StartClock();
                // A remote racer's start is its own client's business.
                if (!k.IsRemote) powerStart.Apply(k, cfg);
            }
            if (TimeTrial != null) TimeTrial.OnGo();
        }

        void FixedUpdate()
        {
            StepOnlineStart();
            if (ClockRunning) RaceTicks++;
        }

        void OnRacerFinished(RacerProgress p)
        {
            var kart = p.GetComponent<Kart>();
            finishCounter++;
            // Races only (in battles an elimination also "finishes" a kart): the local racer's and, as AI host, the AI
            // karts' finishes go to the server.
            if (Online != null && Battle == null) Online.OnKartFinished(kart, p);
            KartFinished?.Invoke(kart);
            if (Battle != null || !kart.IsHuman) return;
            HandToAi(kart, kart.RacerIndex + 100);
            kart.Celebrate(finishCounter <= 3);
            if (kart == LocalPlayer && chaseCamera != null) chaseCamera.CurrentMode = ChaseCamera.Mode.Finished;
            // Online races end with the server's result, shown once the finish had its moment.
            if (IsOnline)
            {
                if (kart == LocalPlayer) finishTimer = GameRoot.Instance.Config.finishGraceSeconds;
            }
            else if (karts.Where(k => k.IsHuman).All(k => k.Progress.Finished))
                finishTimer = GameRoot.Instance.Config.finishGraceSeconds;
        }

        void Update()
        {
            if (Phase is RacePhase.Countdown or RacePhase.Racing or RacePhase.Finished) UpdateRanking();
            HandlePause();
            if (IsOnline)
            {
                UpdateOnline();
                return;
            }
            if (Phase == RacePhase.Racing && RaceOver()) EndRace();
        }

        void UpdateOnline()
        {
            if (Online == null)
            {
                // The online race ended while this scene loaded or ran (connection lost, lobby gone): back to the lobby.
                if (Phase != RacePhase.Results && Phase != RacePhase.Loading && !SceneFlow.Busy && !abandoned)
                {
                    abandoned = true;
                    SetPaused(false);
                    MHK.Online.OnlineLobby.ReturnToLobby();
                }
                return;
            }
            if (finishTimer > 0f) finishTimer -= Time.unscaledDeltaTime;
            // The result may come before GO too (taken out while loading, a race with one human ended early).
            if (Online.Results != null && finishTimer <= 0f && Phase is RacePhase.Intro or RacePhase.Countdown or RacePhase.Racing)
                EndRace();
        }

        bool RaceOver()
        {
            if (TimeTrial != null && TimeTrial.IsReplay) return TimeTrial.ReplayOver;
            if (Battle != null) return Battle.TickRoundEnd(Time.deltaTime);
            // A time trial runs on for the finish grace after the line (the ghost records its run-out).
            if (AllFinished() && TimeTrial == null) return true;
            if (finishTimer < 0f) return false;
            finishTimer -= Time.deltaTime;
            return finishTimer <= 0f;
        }

        bool AllFinished()
        {
            for (var i = 0; i < karts.Count; i++)
                if (!karts[i].Progress.Finished) return false;
            return true;
        }

        void HandlePause()
        {
            if (Phase is RacePhase.Results or RacePhase.Loading) return;
            foreach (var d in humanDrivers)
            {
                if (!d.Pause.WasPressedThisFrame()) continue;
                if (PauseKeyHandler == null || !PauseKeyHandler()) SetPaused(!Paused);
                break;
            }
        }

        /// <summary>
        /// Gets the Pause key before it toggles the pause and returns true when it took the press (the pause menu's
        /// options use it as Back, so the race stays paused while they are open).
        /// </summary>
        public Func<bool> PauseKeyHandler { get; set; }

        /// <summary>Applies settings the pause menu's options changed mid-race: key rebinds and the camera view.</summary>
        public void ApplySettings()
        {
            foreach (var d in humanDrivers) d.ReloadBindings();
            var view = GameRoot.Instance.Settings.cameraMode;
            if (chaseCamera != null && chaseCamera.View != view) chaseCamera.SetView(view);
        }

        public void SetPaused(bool paused)
        {
            if (Paused == paused) return;
            Paused = paused;
            // Online the race goes on for everybody: the menu only covers it.
            if (IsOnline) HoldLocalInput(paused);
            else
            {
                Time.timeScale = paused ? 0f : playbackSpeed;
                AudioListener.pause = paused;
            }
            PauseChanged?.Invoke(paused);
        }

        /// <summary>
        /// Speed the race runs at while it is not paused (the time scale): 1 = real time, 0 holds the picture. Replays
        /// change it, a race leaves it at 1.
        /// </summary>
        public float PlaybackSpeed
        {
            get => playbackSpeed;
            set
            {
                playbackSpeed = Mathf.Max(0f, value);
                if (!Paused) Time.timeScale = playbackSpeed;
            }
        }

        /// <summary>Replay seeking (<see cref="TimeTrialRun.Seek"/>): the race clock jumps to <paramref name="ticks"/>.</summary>
        internal void SeekReplayTicks(int ticks)
        {
            if (Session.IsReplay && ClockRunning) RaceTicks = Mathf.Max(0, ticks);
        }

        void UpdateRanking() => ranking.Update(karts, Battle);

        /// <summary>Takes a kart out of the race (an online racer that left): no longer ranked or shown.</summary>
        internal void RemoveKart(Kart kart)
        {
            if (!karts.Remove(kart)) return;
            UpdateRanking();
        }

        void EndRace()
        {
            // Online the race can end while the menu covers it: the results take over (keys and focus go to them).
            if (IsOnline) SetPaused(false);
            Phase = RacePhase.Finished;
            if (Battle == null && Online == null)
                RaceResultsBuilder.EstimateUnfinished(karts, ranking.Order, TrackRuntime.MainPath.Length, Laps);
            UpdateRanking();
            foreach (var k in karts) k.Progress.StopClock();
            if (TimeTrial != null) TimeTrial.Complete();
            if (Online != null)
            {
                Online.LogStats();
                RaceResultsBuilder.BuildOnline(this, Online, Results);
            }
            else RaceResultsBuilder.Build(this, ranking.Order, Results);
            Phase = RacePhase.Results;
            ResultsReady?.Invoke();
        }

        /// <summary>Called by the results UI: next race of the cup, else back to the menu (online: back to the lobby).</summary>
        public void Continue()
        {
            SetPaused(false);
            if (IsOnline) ReturnToLobby();
            else if (Session.mode == GameMode.Championship && Session.HasNextRace) SceneFlow.NextRace();
            else SceneFlow.GoToMenu();
        }

        /// <summary>Runs this race again, taking back the points and time it awarded. Not online (the server starts races).</summary>
        public void Restart()
        {
            if (SceneFlow.Busy || IsOnline) return; // already leaving (a second click must not take the points again)
            SetPaused(false);
            foreach (var r in Results) r.Rollback();
            SceneFlow.RestartRace();
        }

        /// <summary>Back to the main menu; online it leaves the race and the lobby (<see cref="LeaveOnline"/>).</summary>
        public void QuitToMenu()
        {
            if (IsOnline)
            {
                LeaveOnline();
                return;
            }
            SetPaused(false);
            SceneFlow.GoToMenu();
        }
    }
}
