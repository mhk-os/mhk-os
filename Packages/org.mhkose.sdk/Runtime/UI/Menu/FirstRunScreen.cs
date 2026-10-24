using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// First start (FirstRun.uxml), shown instead of the title until the player picked graphics once: three preset
    /// cards and the frame-rate cap, both applied immediately. Continue remembers the choice and opens the title (or
    /// the built-in mods download while they are missing).
    /// </summary>
    public class FirstRunScreen : MenuScreen
    {
        [SerializeField, Tooltip("Frame-rate value. {0} = frames per second.")]
        string fpsFormat = "{0} fps";
        [SerializeField, Tooltip("Frame-rate value without a cap.")]
        string unlimitedText = "Unlimited";
        [SerializeField, Tooltip("Tooltip of the frame-rate value.")]
        string frameRateTip = "30 fps keeps the device cool on long sessions. Higher rates look smoother.";

        void Reset() => allowBack = false;

        protected override void Bind()
        {
            var g = Settings.graphics;
            BindPresetCards(g);
            var fps = GraphicsOptions.FrameRateChoices;
            BindChoice("frame-rate", () => fps.Count, () => GraphicsOptions.FrameRateIndex(g.maxFps),
                i => UiText.Fps(fps[i], fpsFormat, unlimitedText), i =>
                {
                    g.maxFps = fps[i];
                    GameRoot.Instance.ApplyGraphicsSettings();
                }, _ => frameRateTip);
            OnClick("continue", () =>
            {
                Settings.graphicsChosen = true;
                GameRoot.Instance.SaveSettings();
                Flow.ShowStart();
            });
        }

        void BindPresetCards(GraphicsOptions g)
        {
            var presets = new[]
            {
                (preset: GraphicsPreset.Low, button: Q<Button>("preset-low")),
                (preset: GraphicsPreset.Medium, button: Q<Button>("preset-medium")),
                (preset: GraphicsPreset.High, button: Q<Button>("preset-high")),
            };
            void MarkChoice()
            {
                foreach (var (p, b) in presets) b.EnableInClassList("selected", g.preset == p);
            }
            foreach (var (preset, button) in presets)
            {
                button.clicked += () =>
                {
                    Click();
                    g.SetPreset(preset);
                    GameRoot.Instance.ApplyGraphicsSettings();
                    UiGlass.Apply(Root);
                    MarkChoice();
                };
                if (g.preset == preset) FocusTarget = button;
            }
            MarkChoice();
        }
    }
}
