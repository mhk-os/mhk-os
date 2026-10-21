using System;
using System.Collections.Generic;
using System.Globalization;
using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>
    /// All graphics options (Graphics.uxml), applied immediately. A preset rewrites the other rows, changing a row that
    /// presets write switches to Custom. VSync, screen size and display mode are hidden on mobile, ambient occlusion
    /// when the pipeline lacks it.
    /// </summary>
    public class GraphicsScreen : MenuScreen
    {
        const long ScreenRefreshDelayMs = 300;

        [SerializeField, Tooltip("Frame-rate value. {0} = frames per second.")]
        string fpsFormat = "{0} fps";
        [SerializeField, Tooltip("Frame-rate value without a cap.")]
        string unlimitedText = "Unlimited";
        [SerializeField, Tooltip("Render resolution value without a cap.")]
        string nativeText = "Native";
        [SerializeField, Tooltip("Render resolution value. {0} = vertical lines.")]
        string resolutionFormat = "{0}p";
        [SerializeField, Tooltip("Render resolution value for Auto. {0} = vertical lines it renders at on this screen.")]
        string autoRenderFormat = "Auto ({0}p)";
        [SerializeField, Tooltip("Screen size value. {0} = width, {1} = height in pixels.")]
        string screenSizeFormat = "{0} x {1}";
        [SerializeField, Tooltip("Screen size value for Auto. {0} = width, {1} = height in pixels.")]
        string autoScreenFormat = "Auto ({0} x {1})";
        [SerializeField, Tooltip("Display mode value: a borderless window covering the screen.")]
        string fullscreenText = "Fullscreen";
        [SerializeField, Tooltip("Display mode value: a resizable window.")]
        string windowedText = "Windowed";
        [SerializeField, Tooltip("Display mode value: exclusive fullscreen (Windows).")]
        string exclusiveText = "Exclusive fullscreen";
        [SerializeField, Tooltip("Anti-aliasing value for MSAA 2x.")]
        string msaa2Text = "MSAA 2x";
        [SerializeField, Tooltip("Anti-aliasing value for MSAA 4x.")]
        string msaa4Text = "MSAA 4x";
        [SerializeField, Tooltip("Switch value when on.")]
        string onText = "On";
        [SerializeField, Tooltip("Switch value when off.")]
        string offText = "Off";
        [SerializeField, Tooltip("Tooltip of the preset value, in GraphicsPreset order (Low, Medium, High, Custom).")]
        string[] presetTips =
        {
            "For older devices. Runs coolest and saves battery.",
            "Balanced. A sharper picture, soft shadows and anti-aliasing.",
            "For recent devices. Full resolution, the best shadows and ambient occlusion.",
            "Your own choice of the settings below.",
        };
        [SerializeField, Tooltip("Tooltip of the render resolution value Auto.")]
        string autoRenderTip = "Auto renders at the resolution of the preset for this display.";
        [SerializeField, Tooltip("Tooltip of the screen size value Auto.")]
        string autoScreenTip = "Auto uses the size of the display.";

        readonly List<SettingChoice> choices = new();
        List<Vector2Int> sizes = new();
        GraphicsOptions options;

        protected override void Bind()
        {
            var g = options = Settings.graphics;
            choices.Clear();
            EnumRow("preset", () => g.preset, g.SetPreset, inPreset: false, tip: PresetTip);
            var fps = GraphicsOptions.FrameRateChoices;
            Row("frame-rate", () => fps.Count, () => GraphicsOptions.FrameRateIndex(g.maxFps),
                i => UiText.Fps(fps[i], fpsFormat, unlimitedText), i => g.maxFps = fps[i], inPreset: false);
            if (Application.isMobilePlatform) SetVisible("vsync", false);
            else Switch("vsync", () => g.vSync, on => g.vSync = on, inPreset: false);
            BindScreenRows(g);
            EnumRow("render-resolution", () => g.resolution, r => g.resolution = r, RenderResolutionText,
                tip: r => r == RenderResolution.Auto ? autoRenderTip : null);
            EnumRow("shadows", () => g.shadows, v => g.shadows = v);
            EnumRow("textures", () => g.textures, v => g.textures = v);
            EnumRow("anti-aliasing", () => g.antiAliasing, v => g.antiAliasing = v, AntiAliasingText);
            if (GraphicsQuality.AmbientOcclusionAvailable) Switch("ambient-occlusion", () => g.ambientOcclusion, on => g.ambientOcclusion = on);
            else SetVisible("ambient-occlusion", false);
        }

        protected override void Unbind()
        {
            base.Unbind();
            choices.Clear();
        }

        // Screen size and mode apply on a later frame, so their labels (and Auto's render height) refresh once more after it.
        void BindScreenRows(GraphicsOptions g)
        {
            if (!ScreenSetup.Available)
            {
                SetVisible("screen-resolution", false);
                SetVisible("display-mode", false);
                return;
            }
            sizes = ScreenSetup.Sizes(g.screenMode);
            Row("screen-resolution", () => sizes.Count + 1, ScreenSizeIndex, ScreenSizeText, i => ApplyScreen(() => SelectSize(i)),
                inPreset: false, tip: i => i == 0 ? autoScreenTip : null);
            var modes = ScreenSetup.Modes;
            Row("display-mode", () => modes.Count, () => Mathf.Min((int)ScreenSetup.Effective(g.screenMode), modes.Count - 1),
                i => ScreenModeText(modes[i]), i => ApplyScreen(() => ScreenSetup.SelectMode(g, modes[i])), inPreset: false);
        }

        void ApplyScreen(Action change)
        {
            change();
            ScreenSetup.Apply(options);
            Q<VisualElement>("page").schedule.Execute(RefreshLabels).ExecuteLater(ScreenRefreshDelayMs);
        }

        // inPreset: presets write the option, so changing it by hand switches to Custom (which remembers it).
        void Row(string name, Func<int> count, Func<int> index, Func<int, string> text, Action<int> select, bool inPreset = true,
            Func<int, string> tip = null) =>
            choices.Add(BindChoice(name, count, index, text, i =>
            {
                select(i);
                Changed(inPreset);
            }, tip));

        void EnumRow<T>(string name, Func<T> get, Action<T> set, Func<T, string> text = null, bool inPreset = true,
            Func<T, string> tip = null) where T : struct, Enum =>
            choices.Add(BindChoice(name, get, v =>
            {
                set(v);
                Changed(inPreset);
            }, text, tip));

        void Switch(string name, Func<bool> get, Action<bool> set, bool inPreset = true) =>
            Row(name, () => 2, () => get() ? 1 : 0, i => OnOff(i == 1), i => set(i == 1), inPreset);

        void Changed(bool inPreset)
        {
            if (inPreset) options.MarkCustom();
            GameRoot.Instance.ApplyGraphicsSettings();
            UiGlass.Apply(Root);
            RefreshLabels();
        }

        void RefreshLabels()
        {
            if (ScreenSetup.Available) sizes = ScreenSetup.Sizes(options.screenMode);
            foreach (var choice in choices) choice.Refresh();
        }

        string PresetTip(GraphicsPreset p)
        {
            var i = (int)p;
            return presetTips != null && i < presetTips.Length ? presetTips[i] : null;
        }

        string OnOff(bool on) => on ? onText : offText;

        string RenderResolutionText(RenderResolution r) => r switch
        {
            RenderResolution.Auto => string.Format(CultureInfo.InvariantCulture, autoRenderFormat, GraphicsQuality.AutoRenderHeight(options)),
            RenderResolution.Native => nativeText,
            _ => string.Format(CultureInfo.InvariantCulture, resolutionFormat, GraphicsOptions.CapHeight(r)),
        };

        // Value 0 is Auto, value i the size sizes[i - 1].
        int ScreenSizeIndex()
        {
            var size = ScreenSetup.Resolve(options, out var auto);
            return auto ? 0 : sizes.IndexOf(size) + 1;
        }

        string ScreenSizeText(int i)
        {
            var size = i == 0 ? ScreenSetup.AutoSize(options.screenMode) : sizes[i - 1];
            return string.Format(CultureInfo.InvariantCulture, i == 0 ? autoScreenFormat : screenSizeFormat, size.x, size.y);
        }

        void SelectSize(int i)
        {
            var size = i == 0 ? Vector2Int.zero : sizes[i - 1];
            (options.screenWidth, options.screenHeight) = (size.x, size.y);
        }

        string ScreenModeText(ScreenMode mode) => mode switch
        {
            ScreenMode.Windowed => windowedText,
            ScreenMode.ExclusiveFullscreen => exclusiveText,
            _ => fullscreenText,
        };

        string AntiAliasingText(AntiAliasing a) => a switch
        {
            AntiAliasing.MSAA2x => msaa2Text,
            AntiAliasing.MSAA4x => msaa4Text,
            _ => a.ToString(),
        };
    }
}
