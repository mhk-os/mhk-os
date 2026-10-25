using MHK.Core;
using UnityEngine;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>
    /// Options (Options.uxml): volumes, race camera, touch controls. The UI layout row opens the
    /// <see cref="UiLayoutScreen"/>, the graphics row the <see cref="GraphicsScreen"/>, the controls row (desktop only) the <see cref="ControlsScreen"/>.
    /// </summary>
    public class OptionsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Camera row values, in CameraMode order (Close, Far, POV).")]
        string[] cameraModeNames = { "Close", "Far", "POV" };
        [SerializeField, Tooltip("Keyboard keys and UI layout row value while they are the defaults.")]
        string defaultKeysText = "Default";
        [SerializeField, Tooltip("Keyboard keys and UI layout row value after the player changed them.")]
        string customKeysText = "Custom";

        protected override void Bind()
        {
            var s = Settings;
            BindSlider("master-volume", s.masterVolume, v => s.masterVolume = v);
            BindSlider("music-volume", s.musicVolume, v => s.musicVolume = v);
            BindSlider("effects-volume", s.sfxVolume, v => s.sfxVolume = v);
            BindChoice("camera", () => s.cameraMode, v => s.cameraMode = v, CameraModeName);
            BindChoice("touch-controls", () => s.touchControls, v => s.touchControls = v, click: false);
            BindLink("ui-layout", s.uiLayout.custom ? customKeysText : defaultKeysText, () => Flow.Show<UiLayoutScreen>());
            BindLink("graphics", s.graphics.preset.ToString(), () => Flow.Show<GraphicsScreen>());
            if (Application.isMobilePlatform) SetVisible("controls", false);
            else BindLink("controls", string.IsNullOrEmpty(s.keyBindings) ? defaultKeysText : customKeysText, () => Flow.Show<ControlsScreen>());
        }

        string CameraModeName(CameraMode mode)
        {
            var i = (int)mode;
            return cameraModeNames != null && i < cameraModeNames.Length ? cameraModeNames[i] : mode.ToString();
        }
    }
}
