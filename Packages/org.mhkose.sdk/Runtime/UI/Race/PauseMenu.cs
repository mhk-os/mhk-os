using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Pause menu (Pause.uxml), open while the race is paused: Continue / Restart / Quit to menu. The title and plate
    /// scale in (<c>.hud-pause-column.shown</c>).
    /// </summary>
    public class PauseMenu : RaceLayer
    {
        VisualElement panel;
        Button continueButton;

        protected override void Bind()
        {
            panel = Q<VisualElement>("panel");
            panel.RemoveFromClassList("shown");
            continueButton = Q<Button>("continue");
            continueButton.clicked += () => Race.SetPaused(false);
            Q<Button>("restart").clicked += () => Race.Restart();
            Q<Button>("quit").clicked += () => Race.QuitToMenu();
        }

        protected override void AfterBind()
        {
            var p = panel;
            p.schedule.Execute(() => p.AddToClassList("shown")).ExecuteLater(UiAnim.FrameMs);
            continueButton.Focus();
        }

        protected override void Unbind()
        {
            panel = null;
            continueButton = null;
        }
    }
}
