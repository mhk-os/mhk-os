using MHK.Core;
using MHK.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Pause menu (Pause.uxml), open while the race is paused: Continue / Restart / Options / Quit to menu. The title and
    /// plate scale in (<c>.hud-pause-column.shown</c>). Options opens the main menu's Options page (and its UI layout,
    /// graphics and keyboard keys screens) in the <see cref="options"/> overlay over the pause scrim; the race stays paused
    /// and takes the changed settings when it closes.
    /// </summary>
    public class PauseMenu : RaceLayer
    {
        const string UiCancelAction = ProjectActions.UiMap + "/Cancel";

        [SerializeField, Tooltip("Overlay MenuFlow holding the menu's Options screen and the screens it links to, opened by the " +
                                 "Options button. Empty: no Options button.")]
        MenuFlow options;

        [Header("Online")]
        [SerializeField, Tooltip("Quit button in online races.")]
        string leaveRaceText = "Leave race";
        [SerializeField, Tooltip("Question before leaving an online race.")]
        string leaveRaceQuestion = "Leave the race and the lobby?";
        [SerializeField, Tooltip("Confirm button of the leave question.")]
        string leaveRaceConfirm = "Leave";
        [SerializeField, Tooltip("Solo race, someone waits to join: the End race button ({0} = their name).")]
        string endRaceFormat = "End race for {0}";

        VisualElement panel;
        Button continueButton, optionsButton, endRaceButton;
        InputAction uiCancel;
        bool shownEndRace;

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
                Race.PauseKeyHandler = OnPauseKey;
            }
            else optionsButton.SetVisible(false);
            var quit = Q<Button>("quit");
            endRaceButton = Q<Button>("end-race");
            endRaceButton.SetVisible(false);
            shownEndRace = false;
            if (Race.IsOnline) BindOnline(quit);
            else quit.clicked += () => Race.QuitToMenu();
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
                if (Race != null) Race.PauseKeyHandler = null;
            }
            panel = null;
            continueButton = optionsButton = endRaceButton = null;
        }

        void ShowPanel(Button focus)
        {
            var p = panel;
            p.SetVisible(true);
            p.RemoveFromClassList("shown");
            p.schedule.Execute(() => p.AddToClassList("shown")).ExecuteLater(UiAnim.FrameMs);
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

        // While the options are open the Pause key is their Back (the race stays paused): a cancel on the focused element,
        // so the screens' own cancel handling (prompts, key capture) applies. Escape is also the UI cancel, which
        // already goes back by itself.
        bool OnPauseKey()
        {
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
