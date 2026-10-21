using System.Globalization;
using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Pause menu (Pause.uxml), open while the race is paused: Continue / Restart / Quit to menu and the minimap size
    /// slider. The title and plate scale in (<c>.hud-pause-column.shown</c>), changed settings are saved when it closes.
    /// </summary>
    public class PauseMenu : RaceLayer
    {
        [SerializeField, Tooltip("Minimap slider value text when the minimap is off.")]
        string minimapOffText = "Off";

        VisualElement panel;
        Label minimapValue;
        Button continueButton;
        bool settingsDirty;

        protected override void Bind()
        {
            panel = Q<VisualElement>("panel");
            panel.RemoveFromClassList("shown");
            continueButton = Q<Button>("continue");
            continueButton.clicked += () => Race.SetPaused(false);
            Q<Button>("restart").clicked += () => Race.Restart();
            Q<Button>("quit").clicked += () => Race.QuitToMenu();
            BindMinimapSlider();
        }

        void BindMinimapSlider()
        {
            var scale = Settings.minimapScale;
            var slider = Q<Slider>("minimap-size");
            slider.lowValue = 0f;
            slider.highValue = UserSettings.MaxMinimapScale;
            slider.SetValueWithoutNotify(scale);
            minimapValue = Q<Label>("minimap-value");
            minimapValue.text = MinimapText(scale);
            slider.RegisterValueChangedCallback(e =>
            {
                Settings.minimapScale = e.newValue;
                minimapValue.text = MinimapText(e.newValue);
                settingsDirty = true;
            });
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
            minimapValue = null;
            continueButton = null;
            if (!settingsDirty) return;
            settingsDirty = false;
            GameRoot.Instance.SaveSettings();
        }

        string MinimapText(float scale) =>
            scale < Minimap.OffScale ? minimapOffText : Mathf.RoundToInt(scale * 100f).ToString(CultureInfo.InvariantCulture) + "%";
    }
}
