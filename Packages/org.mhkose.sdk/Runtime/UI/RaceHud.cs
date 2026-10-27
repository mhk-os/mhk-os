using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// In-race HUD (RaceHud.uxml): places the HUD widgets by the player's UI layout (<see cref="HudLayout"/>), binds them
    /// to the race each frame and opens the race layers (touch controls, pause menu, results).
    /// </summary>
    public partial class RaceHud : UiScreen
    {
        [SerializeField, Tooltip("Race to show, found in the scene when empty.")]
        RaceManager race;
        [SerializeField, Tooltip("Item slot icon for powerups without an icon.")]
        Texture2D unknownItemIcon;

        [Header("Layers (child GameObjects, opened by the HUD)")]
        [SerializeField, Tooltip("Touch buttons (child GameObject), open while touch controls are active.")]
        TouchControls touch;
        [SerializeField, Tooltip("Pause menu (child GameObject), open while the race is paused.")]
        PauseMenu pause;
        [SerializeField, Tooltip("Results screen (child GameObject), opened when the results are ready.")]
        RaceResults results;
        [SerializeField, Tooltip("Replay controls (child GameObject), open while a time-trial replay runs (from GO to the results).")]
        ReplayControls replay;

        [Header("Messages")]
        [SerializeField, Tooltip("Flash when the player finishes a race ({0} = position, e.g. 2nd).")]
        string finishedFormat = "Finished {0}!";
        [SerializeField, Tooltip("Flash when the player is knocked out of a battle.")]
        string playerOutText = "You're out!";
        [SerializeField, Tooltip("Flash when an opponent is knocked out of a battle ({0} = name).")]
        string kartOutFormat = "{0} is out!";
        [SerializeField, Tooltip("Lap panel caption in battles (the race caption is the lap-caption text in the UXML).")]
        string aliveCaption = "Alive";
        [SerializeField, Tooltip("Health bar status of the knocked-out player.")]
        string outStatus = "OUT";
        [SerializeField, Tooltip("Countdown text at the start signal.")]
        string goText = "GO!";

        [Header("Time trial")]
        [SerializeField, Tooltip("Flash when the player finishes a time trial ({0} = finish time).")]
        string timeTrialFinishedFormat = "Finish  {0}";
        [SerializeField, Tooltip("Split caption at a lap line ({0} = lap number).")]
        string splitLapFormat = "Lap {0}";
        [SerializeField, Tooltip("Split caption at the finish line.")]
        string splitFinishText = "Finish";
        [SerializeField, Tooltip("Name tag of a ghost ({0} = player name, {1} = its finish time).")]
        string ghostTagFormat = "{0}  {1}";
        [SerializeField, Tooltip("Name tag of the personal-best ghost ({0} = its finish time).")]
        string ownBestTagFormat = "Your best  {0}";
        [SerializeField, Tooltip("Replay plate ({0} = player name, {1} = engine class, {2} = finish time).")]
        string replayFormat = "{0} | {1} | {2}";

        [Header("Online")]
        [SerializeField, Tooltip("Tooltip of the racer chips while the race waits for everybody to load.")]
        string waitingForPlayersText = "Waiting for every racer to load.";
        [SerializeField, Tooltip("Tooltip of the racer chips after the player's finish, until the server's result.")]
        string waitingForResultText = "Waiting for the others to finish.";
        [SerializeField, Tooltip("Tooltip of the connection trouble icon.")]
        string connectionTroubleText = "Connection trouble: the other karts are not updating.";
        [SerializeField, Tooltip("Tooltip of the solo race join toast ({0} = the waiting player's name).")]
        string joinWaitingFormat = "{0} wants to join. End this race to race together.";
        [SerializeField, Tooltip("Button of the solo race join toast.")]
        string endRaceText = "End race";
        [SerializeField, Tooltip("The join toast's button once the player chose to end the race.")]
        string endingRaceText = "Ending...";

        const float FinishFlashSeconds = 4f, KartOutFlashSeconds = 2f;
        const float SpeedoHeadroom = 1.4f, SpeedoStepKmh = 10f;
        static readonly Color UnsetOverlay = new(-1f, -1f, -1f, -1f);

        VisualElement hud, overlay;
        HudSpeedometer speedometer;
        Minimap minimap;
        HudElementSetup minimapSetup;
        PositionView position;
        LapPanel lapPanel;
        ItemSlot itemSlot;
        Countdown countdown;
        Messages messages;
        TimeTrialView timeTrial;
        OnlineView online;

        BattleRules battle;
        bool widgetsShown, speedoScaled, resultsShown;
        int lastLap;
        Color shownOverlay;
        Kart player;

        protected override bool ReadyToBind => race != null && GameRoot.Instance != null;

        protected override bool InsetBySafeArea => true;

        protected override void Awake()
        {
            base.Awake();
            position = new PositionView(this);
            lapPanel = new LapPanel(this);
            itemSlot = new ItemSlot(this);
            countdown = new Countdown(this);
            messages = new Messages(this);
            timeTrial = new TimeTrialView(this);
            online = new OnlineView(this);
        }

        protected override void OnEnable()
        {
            if (race == null) race = FindAnyObjectByType<RaceManager>();
            base.OnEnable();
        }

        protected override void Bind()
        {
            widgetsShown = speedoScaled = false;
            shownOverlay = UnsetOverlay;
            var touchActive = GameRoot.Instance.Settings.TouchControlsActive;
            hud = Q<VisualElement>("hud");
            hud.AddToClassList("intro");
            hud.EnableInClassList("touch", touchActive);
            hud.SetVisible(!resultsShown);
            overlay = Q<VisualElement>("overlay");
            position.Bind();
            lapPanel.Bind();
            itemSlot.Bind();
            BindGauges(touchActive);
            countdown.Bind();
            messages.Bind();
            timeTrial.Bind();
            online.Bind();
            hud.NoPick();
            BindLayout(touchActive);

            race.CountdownTick += OnCountdown;
            race.ResultsReady += ShowResults;
            race.PauseChanged += OnPause;
            race.KartFinished += OnKartFinished;
            BindLayers(touchActive);
        }

        void BindGauges(bool touchActive)
        {
            minimap = Q<Minimap>("minimap");
            minimap.TouchMode = touchActive;
            speedometer = Q<HudSpeedometer>("speedometer");
            speedometer.TouchMode = touchActive;
            speedometer.SetSpeed(0);
        }

        void BindLayout(bool touchActive)
        {
            var layout = GameRoot.Instance.Settings.uiLayout;
            new HudLayout(hud).ApplyAll(layout, touchActive);
            minimapSetup = layout.Hud(HudElementType.Minimap, touchActive);
        }

        void BindLayers(bool touchActive)
        {
            // Layers left enabled in the scene (to preview them) start closed. The race events open them.
            SetLayer(touch, false);
            SetLayer(pause, false);
            SetLayer(replay, false);
            SetLayer(touch, touchActive && !resultsShown);
            SetLayer(pause, race.Paused);
            SetLayer(replay, ReplayRunning);
            SetLayer(results, false);
            SetLayer(results, resultsShown);
        }

        bool ReplayRunning => !resultsShown && race.ClockRunning && race.Session != null && race.Session.IsReplay;

        protected override void Unbind()
        {
            race.CountdownTick -= OnCountdown;
            race.ResultsReady -= ShowResults;
            race.PauseChanged -= OnPause;
            race.KartFinished -= OnKartFinished;
            if (battle != null) battle.KartEliminated -= OnEliminated;
            battle = null;
            timeTrial.Unbind();
            online.Unbind();
            messages.Unbind();
        }

        void SetLayer(RaceLayer layer, bool open)
        {
            if (layer == null) return;
            if (open)
            {
                layer.Race = race;
                layer.Hud = this;
            }
            if (layer.gameObject.activeSelf != open) layer.gameObject.SetActive(open);
        }

        /// <summary>
        /// Applies settings changed mid-race (the pause menu's options), which <see cref="Bind"/> otherwise reads once:
        /// touch controls on or off and the UI layout of the HUD and the touch controls.
        /// </summary>
        public void ApplySettings()
        {
            if (!IsBound || hud.panel == null) return;
            var touchActive = GameRoot.Instance.Settings.TouchControlsActive;
            hud.EnableInClassList("touch", touchActive);
            minimap.TouchMode = touchActive;
            speedometer.TouchMode = touchActive;
            BindLayout(touchActive);
            var showTouch = touchActive && !resultsShown;
            // Open touch controls rebuild from the (maybe changed) control layout.
            if (showTouch && touch != null && touch.gameObject.activeSelf) touch.Rebuild();
            else SetLayer(touch, showTouch);
            if (replay != null && replay.gameObject.activeSelf) replay.RefreshTouchMode();
        }

        void ShowWidgets()
        {
            if (widgetsShown) return;
            widgetsShown = true;
            hud.RemoveFromClassList("intro");
        }

        void OnCountdown(int n)
        {
            var packs = GameRoot.Instance.Content.Packs;
            var owner = race.Track != null ? race.Track.OwnerPack : null;
            Audio.AudioManager.PlayUi(n > 0 ? Modding.ContentPack.Find(packs, p => p.countdownSound, owner) : Modding.ContentPack.Find(packs, p => p.goSound, owner));
            ShowWidgets();
            countdown.Show(n);
        }

        void OnEliminated(Kart k)
        {
            if (k == player) messages.Flash(playerOutText, FinishFlashSeconds, "bad");
            else if (race.Battle.AliveCount > 1) messages.Flash(string.Format(kartOutFormat, k.DisplayName), KartOutFlashSeconds, null);
        }

        void OnKartFinished(Kart k)
        {
            if (k != player || race.IsBattle) return;
            if (race.TimeTrial != null)
            {
                messages.Flash(string.Format(timeTrialFinishedFormat, UiText.Clock(k.Progress.FinishTime)), FinishFlashSeconds, "good");
                return;
            }
            var pos = k.Progress.Position;
            messages.Flash(string.Format(finishedFormat, UiText.Ordinal(pos)), FinishFlashSeconds, pos <= 3 ? "good" : null);
        }

        void OnPause(bool paused) => SetLayer(pause, paused);

        void ShowResults()
        {
            resultsShown = true;
            SetLayer(touch, false);
            SetLayer(replay, false);
            hud.SetVisible(false);
            messages.StopPulse();
            if (results != null && results.gameObject.activeSelf) results.Rebuild();
            else SetLayer(results, true);
        }

        void ScaleSpeedometer()
        {
            if (speedoScaled || race.Session == null) return;
            speedoScaled = true;
            var top = GameRoot.Instance.Config.GetDifficulty(race.Session.difficulty).topSpeedKmh;
            speedometer.MaxKmh = Mathf.Ceil(top * SpeedoHeadroom / SpeedoStepKmh) * SpeedoStepKmh;
        }

        void Update()
        {
            // A live reload (UXML/USS changed in play mode) cleared the bound tree. The rebind runs on the panel's next
            // scheduler tick. Writing styles on the cleared elements until then throws.
            if (!IsBound || hud.panel == null) return;
            // Opened after the countdown started (live reload): no countdown event will slide the widgets in.
            if (!widgetsShown && race.Phase is not (RacePhase.Loading or RacePhase.Intro)) ShowWidgets();
            if (replay != null && !replay.gameObject.activeSelf && ReplayRunning) SetLayer(replay, true);
            if (minimapSetup.Drawn) minimap.Tick(minimapSetup.scale);
            player = race.LocalPlayer;
            var dt = Time.unscaledDeltaTime;
            countdown.Tick(dt);
            messages.Tick(dt);
            timeTrial.Tick(race.TimeTrial);
            online.Tick(dt);
            if (player != null) UpdatePlayer();
            else if (race.TimeTrial != null && race.TimeTrial.Spectated != null) UpdateSpectated(race.TimeTrial.Spectated);
        }

        void UpdatePlayer()
        {
            ScaleSpeedometer();
            var prog = player.Progress;
            var kartCount = race.Karts.Count;
            position.Refresh(prog.Position, kartCount);
            lapPanel.RefreshClock(prog.RaceClock);
            if (race.Battle != null) UpdateBattle(kartCount);
            else lapPanel.RefreshLaps(prog.Lap, race.Laps, prog.BestLap);
            speedometer.SetSpeed(Mathf.RoundToInt(Mathf.Abs(player.Physics.SpeedKmh)));
            UpdateFinalLapBanner(prog);
            messages.SetWrongWay(prog.WrongWay && race.Battle == null && race.Phase == RacePhase.Racing);
            itemSlot.Refresh(player.Inventory);
            UpdateOverlay();
        }

        void UpdateBattle(int kartCount)
        {
            if (battle == null)
            {
                battle = race.Battle;
                battle.KartEliminated += OnEliminated;
            }
            lapPanel.RefreshBattle(battle.AliveCount, kartCount, player.Health, GameRoot.Instance.Config.maxHealth, player.Eliminated);
        }

        void UpdateFinalLapBanner(RacerProgress prog)
        {
            if (prog.Lap == lastLap || race.Phase != RacePhase.Racing || race.Battle != null) return;
            if (prog.Lap == race.Laps && race.Laps > 1) messages.ShowBanner();
            lastLap = prog.Lap;
        }

        void UpdateOverlay()
        {
            var color = player.Effects.ScreenOverlay;
            if (color == shownOverlay) return;
            shownOverlay = color;
            overlay.style.backgroundColor = color;
        }

        void UpdateSpectated(Ghosts.GhostPlayer ghost)
        {
            ScaleSpeedometer();
            var h = ghost.Data.header;
            var time = race.ClockRunning ? Mathf.Min(ghost.ShownTime, h.finishTime) : 0f;
            lapPanel.RefreshClock(time);
            var best = -1f;
            for (var i = 0; i < h.lapTimes.Length; i++)
                if (h.LapEndTime(i + 1) <= time && (best < 0f || h.lapTimes[i] < best)) best = h.lapTimes[i];
            lapPanel.RefreshLaps(ghost.Lap, race.Laps, best);
            speedometer.SetSpeed(Mathf.RoundToInt(Mathf.Abs(ghost.ForwardSpeed) * Units.KmhPerMs));
        }
    }
}
