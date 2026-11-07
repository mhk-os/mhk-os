using MHK.Core;
using MHK.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Pause menu (Pause.uxml), open while the race is paused: Continue / Standings / Screenshot / Restart / Options / Quit to
    /// menu. The title and plate scale in (<c>.hud-pause-column.shown</c>). Standings (championships, from the second race on)
    /// swaps the plate for the standings before this race (<see cref="StandingsTable"/>); Back, Cancel or the Pause key
    /// return. Screenshot (offline races, championships and battles) opens the <see cref="photo"/> mode, which hides this menu
    /// and the HUD while it is open; the race stays paused. Options opens the main menu's Options page (and its UI layout,
    /// graphics and keyboard keys screens) in the <see cref="options"/> overlay over the pause scrim; the race stays paused
    /// and takes the changed settings when it closes.
    /// </summary>
    public class PauseMenu : RaceLayer
    {
        const string UiCancelAction = ProjectActions.UiMap + "/Cancel";

        [SerializeField, Tooltip("Overlay MenuFlow holding the menu's Options screen and the screens it links to, opened by the " +
                                 "Options button. Empty: no Options button.")]
        MenuFlow options;
        [SerializeField, Tooltip("The championship standings before this race (Standings button): row template, texts and timing.")]
        StandingsTable standings = new();
        [SerializeField, Tooltip("Photo mode (a GameObject next to the HUD), opened by the Screenshot button in offline races, " +
                                 "championships and battles. Empty: no Screenshot button.")]
        PhotoMode photo;

        [Header("Online")]
        [SerializeField, Tooltip("Quit button in online races.")]
        string leaveRaceText = "Leave race";
        [SerializeField, Tooltip("Question before leaving an online race.")]
        string leaveRaceQuestion = "Leave the race and the lobby?";
        [SerializeField, Tooltip("Confirm button of the leave question.")]
        string leaveRaceConfirm = "Leave";
        [SerializeField, Tooltip("Solo race, someone waits to join: the End race button ({0} = their name).")]
        string endRaceFormat = "End race for {0}";

        VisualElement panel, standingsColumn, standingsPage;
        Button continueButton, optionsButton, endRaceButton, standingsButton, standingsBack, photoButton;
        InputAction uiCancel;
        bool shownEndRace, standingsOpen;
        // Escape is the UI cancel (closes the standings) and the Pause key: the frame they closed in, whichever came first.
        int standingsClosedFrame = -1;

        bool OptionsOpen => options != null && options.Current != null;

        protected override void Bind()
        {
            panel = Q<VisualElement>("panel");
            panel.RemoveFromClassList("shown");
            continueButton = Q<Button>("continue");
            continueButton.clicked += () => Race.SetPaused(false);
            Q<Button>("restart").clicked += () => Race.Restart();
            optionsButton = Q<Button>("options");
            if (options != null)
            {
                optionsButton.clicked += OpenOptions;
                options.Closed += OnOptionsClosed;
            }
            else optionsButton.SetVisible(false);
            Race.PauseKeyHandler = OnPauseKey;
            BindStandings();
            BindPhoto();
            var quit = Q<Button>("quit");
            endRaceButton = Q<Button>("end-race");
            endRaceButton.SetVisible(false);
            shownEndRace = false;
            if (Race.IsOnline) BindOnline(quit);
            else quit.clicked += () => Race.QuitToMenu();
        }

        // Championships from the second race on: the standings before this race in place of the plate (the first race
        // has none yet, online races keep their set points in the lobby).
        void BindStandings()
        {
            standingsColumn = Q<VisualElement>("standings-column");
            standingsColumn.SetVisible(false);
            standingsOpen = false;
            standingsButton = Q<Button>("standings");
            var session = Race.Session;
            var shown = !Race.IsOnline && session.mode == GameMode.Championship && session.raceIndex > 0;
            standingsButton.SetVisible(shown);
            if (!shown) return;
            standingsPage = Q<VisualElement>("standings-page");
            Q<Label>("standings-title").text = standings.FillBeforeCupRace(standingsPage, session, this);
            standingsBack = Q<Button>("standings-back");
            standingsButton.clicked += OpenStandings;
            standingsBack.clicked += CloseStandings;
            Root.RegisterCallback<NavigationCancelEvent>(OnCancel);
        }

        // Offline single races, championships and battles: not online (the race goes on), time trials, replays or the menu
        // background race.
        void BindPhoto()
        {
            photoButton = Q<Button>("photo");
            var session = Race.Session;
            var shown = photo != null && session != null && !Race.IsOnline && !Race.IsAttract && Race.RaceCamera != null &&
                        session.mode is GameMode.SingleRace or GameMode.Championship or GameMode.Battle;
            photoButton.SetVisible(shown);
            if (!shown) return;
            photoButton.clicked += OpenPhoto;
            photo.Closed += OnPhotoClosed;
        }

        void OpenPhoto()
        {
            photoButton.Blur();
            photo.Open(Race, Hud);
        }

        void OnPhotoClosed()
        {
            if (IsBound) ShowPanel(photoButton);
        }

        void OpenStandings()
        {
            standingsOpen = true;
            panel.SetVisible(false);
            ShowColumn(standingsColumn, standingsBack);
            standings.Show(standingsPage);
        }

        void CloseStandings()
        {
            standingsOpen = false;
            standingsClosedFrame = Time.frameCount;
            standingsColumn.SetVisible(false);
            ShowPanel(standingsButton);
        }

        void OnCancel(NavigationCancelEvent e)
        {
            if (!standingsOpen) return;
            e.StopPropagation();
            CloseStandings();
        }

        // Online the race cannot stop or restart: the menu covers it, and quitting leaves race and lobby after a question.
        // In a solo race someone waits to join, End race is here too (reachable with keys and gamepad).
        void BindOnline(Button quit)
        {
            // The race goes on behind the menu: its title is the track, not "Paused".
            Q<Label>("title").text = Race.Track != null ? Race.Track.DisplayName : "";
            Q<Button>("restart").SetVisible(false);
            quit.Q<VisualElement>(className: "icon")?.SetIcon("logout");
            var label = quit.Q<Label>(className: "button-label");
            if (label != null) label.text = leaveRaceText;
            quit.clicked += () => ConfirmPrompt.Show(panel, leaveRaceQuestion, leaveRaceConfirm, () => Race.QuitToMenu(), () => quit.Focus());
            endRaceButton.clicked += () =>
            {
                Race.Online?.EndSoloRaceEarly();
                Race.SetPaused(false);
            };
            RefreshEndRace();
        }

        // Someone may start waiting (or the race end) while the menu is open: the button follows.
        void Update()
        {
            if (IsBound && Race != null && Race.IsOnline) RefreshEndRace();
        }

        void RefreshEndRace()
        {
            var online = Race.Online;
            var waiting = online != null && online.Solo && online.JoinWaitingUser != null && !online.EndRequested && online.Results == null;
            if (waiting == shownEndRace) return;
            shownEndRace = waiting;
            var focused = endRaceButton.focusController != null && endRaceButton.focusController.focusedElement == endRaceButton;
            endRaceButton.SetVisible(waiting);
            if (waiting) endRaceButton.Q<Label>("end-race-label").text = string.Format(endRaceFormat, online.JoinWaitingUser);
            // Never leave the focus on a hidden button (keys and gamepad would find nothing).
            else if (focused) continueButton.Focus();
        }

        protected override void AfterBind() => ShowPanel(continueButton);

        protected override void Unbind()
        {
            if (options != null)
            {
                // Unpaused another way (touch pause button) while the options were open: keep what was changed.
                if (OptionsOpen)
                {
                    options.Close();
                    GameRoot.Instance.SaveSettings();
                }
                options.Closed -= OnOptionsClosed;
            }
            if (photo != null)
            {
                // Unpaused another way while taking photos: the camera goes back to the race first.
                photo.Closed -= OnPhotoClosed;
                photo.Close();
            }
            if (Race != null) Race.PauseKeyHandler = null;
            panel = standingsColumn = standingsPage = null;
            continueButton = optionsButton = endRaceButton = standingsButton = standingsBack = photoButton = null;
            standingsOpen = false;
        }

        void ShowPanel(Button focus) => ShowColumn(panel, focus);

        // Shows one column of the menu (plate or standings), scaling it in, and focuses a button of it.
        static void ShowColumn(VisualElement column, Button focus)
        {
            column.SetVisible(true);
            column.RemoveFromClassList("shown");
            column.schedule.Execute(() => column.AddToClassList("shown")).ExecuteLater(UiAnim.FrameMs);
            focus.Focus();
        }

        void OpenOptions()
        {
            // The UI layout screen shows this track behind the HUD it edits.
            options.Selection.track = Race.Track;
            panel.SetVisible(false);
            options.Open<OptionsScreen>();
        }

        void OnOptionsClosed()
        {
            Race.ApplySettings();
            if (Hud != null) Hud.ApplySettings();
            if (IsBound) ShowPanel(optionsButton);
        }

        // The Pause key is the standings' Back (the race stays paused); Escape is also the UI cancel, which may have
        // closed them already this frame. In the photo mode it goes back one step there (PhotoMode.TakePauseKey). While the
        // options are open the Pause key is their Back too: a cancel on the focused element, so the screens' own cancel
        // handling (prompts, key capture) applies. Escape already goes back there by itself.
        bool OnPauseKey()
        {
            if (photo != null && photo.TakePauseKey()) return true;
            if (standingsOpen)
            {
                CloseStandings();
                return true;
            }
            if (standingsClosedFrame == Time.frameCount) return true;
            if (!OptionsOpen) return false;
            uiCancel ??= InputSystem.actions != null ? InputSystem.actions.FindAction(UiCancelAction) : null;
            if (uiCancel != null && uiCancel.WasPressedThisFrame()) return true;
            var root = options.Current.Root;
            if (root == null) return true;
            var target = root.focusController?.focusedElement as VisualElement ?? root;
            using var e = NavigationCancelEvent.GetPooled();
            target.SendEvent(e);
            return true;
        }
    }
}
