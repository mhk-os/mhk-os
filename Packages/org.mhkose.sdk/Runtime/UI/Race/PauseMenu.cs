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
        [SerializeField, Tooltip("Title of the menu in online races (the race goes on behind it).")]
        string onlineTitle = "Menu";
        [SerializeField, Tooltip("Quit button in online races.")]
        string leaveRaceText = "Leave race";
        [SerializeField, Tooltip("Question before leaving an online race.")]
        string leaveRaceQuestion = "Leave the race and the lobby? The race counts as left.";
        [SerializeField, Tooltip("Confirm button of the leave question.")]
        string leaveRaceConfirm = "Leave";

        VisualElement panel;
        Button continueButton, optionsButton;
        InputAction uiCancel;

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
            if (Race.IsOnline) BindOnline(quit);
            else quit.clicked += () => Race.QuitToMenu();
        }

        // Online the race cannot stop or restart: the menu covers it, and quitting leaves race and lobby after a question.
        void BindOnline(Button quit)
        {
            Q<Label>("title").text = onlineTitle;
            Q<Button>("restart").SetVisible(false);
            var label = quit.Q<Label>(className: "button-label");
            if (label != null) label.text = leaveRaceText;
            quit.clicked += () => ConfirmPrompt.Show(panel, leaveRaceQuestion, leaveRaceConfirm, () => Race.QuitToMenu(), () => quit.Focus());
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
            continueButton = optionsButton = null;
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
