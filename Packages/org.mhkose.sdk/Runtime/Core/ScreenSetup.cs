using System.Collections.Generic;
using UnityEngine;
using Application = UnityEngine.Device.Application;
using Screen = UnityEngine.Device.Screen;

namespace MHK.Core
{
    /// <summary>
    /// Output size and window mode of desktop players (<see cref="GraphicsOptions.screenWidth"/>,
    /// <see cref="GraphicsOptions.screenMode"/>). Applied at startup over the size Unity remembers, unless Unity's own
    /// screen options were given on the command line. Mobile devices and the editor keep their size.
    /// </summary>
    public static class ScreenSetup
    {
        const int MinHeight = 720;
        const float WindowHeightShare = 0.9f;
        const float AutoWindowShare = 0.8f;

        static readonly string[] UnityScreenOptions = { "-screen-width", "-screen-height", "-screen-fullscreen", "-window-mode" };
        static readonly Vector2Int[] WindowSizes = { new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160) };
        static readonly ScreenMode[] AllModes = { ScreenMode.Fullscreen, ScreenMode.Windowed, ScreenMode.ExclusiveFullscreen };
        static readonly ScreenMode[] WindowModes = { ScreenMode.Fullscreen, ScreenMode.Windowed };

        /// <summary>The player can choose size and mode (desktop). The editor shows the choices but its Game view sets the size.</summary>
        public static bool Available => !Application.isMobilePlatform;

        /// <summary>Exclusive fullscreen exists (Windows only).</summary>
        public static bool ExclusiveAvailable =>
            Application.platform is RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor;

        /// <summary>
        /// Sizes offered for <paramref name="mode"/>, smallest first: the display's modes with at least 720 lines and its
        /// native size, each once. Windowed adds the 16:9 sizes and keeps only what fits the display's work area.
        /// </summary>
        public static List<Vector2Int> Sizes(ScreenMode mode)
        {
            var sizes = new List<Vector2Int>();
            foreach (var r in Screen.resolutions) AddSize(sizes, new Vector2Int(r.width, r.height));
            if (Effective(mode) == ScreenMode.Windowed)
            {
                foreach (var s in WindowSizes) AddSize(sizes, s);
                sizes.RemoveAll(s => !FitsWindow(s));
            }
            else AddSize(sizes, NativeSize());
            sizes.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            return sizes;
        }

        /// <summary>
        /// Auto size: the display's native resolution in the fullscreen modes (the 3D image has its own render height,
        /// so a native output only keeps the menus sharp). Windowed: the biggest 16:9 size within 80 % of the work area.
        /// </summary>
        public static Vector2Int AutoSize(ScreenMode mode)
        {
            if (Effective(mode) == ScreenMode.Windowed)
            {
                var best = WindowSizes[0];
                foreach (var s in WindowSizes)
                    if (FitsWorkArea(s, AutoWindowShare, AutoWindowShare)) best = s;
                return best;
            }
            var native = NativeSize();
            if (native.y >= MinHeight) return native;
            var sizes = Sizes(mode);
            return sizes.Count > 0 ? sizes[^1] : new Vector2Int(Screen.width, Screen.height);
        }

        /// <summary>The size <paramref name="o"/> asks for: its own when the display offers it in its mode, else Auto.</summary>
        public static Vector2Int Resolve(GraphicsOptions o, out bool auto)
        {
            var chosen = new Vector2Int(o.screenWidth, o.screenHeight);
            auto = chosen.y <= 0 || !Sizes(o.screenMode).Contains(chosen);
            return auto ? AutoSize(o.screenMode) : chosen;
        }

        /// <summary>The mode <paramref name="mode"/> runs as here (exclusive fullscreen falls back to a fullscreen window).</summary>
        public static ScreenMode Effective(ScreenMode mode) =>
            mode == ScreenMode.ExclusiveFullscreen && !ExclusiveAvailable ? ScreenMode.Fullscreen : mode;

        /// <summary>Window modes this platform has, in <see cref="ScreenMode"/> order.</summary>
        public static IReadOnlyList<ScreenMode> Modes => ExclusiveAvailable ? AllModes : WindowModes;

        /// <summary>Switches <paramref name="o"/> to <paramref name="mode"/>. A size the new mode lacks becomes Auto.</summary>
        public static void SelectMode(GraphicsOptions o, ScreenMode mode)
        {
            o.screenMode = mode;
            Resolve(o, out var auto);
            if (auto) o.screenWidth = o.screenHeight = 0;
        }

        /// <summary>Startup: applies <paramref name="o"/> unless -screen-width, -screen-height, -screen-fullscreen or -window-mode was given.</summary>
        public static void ApplyAtStartup(GraphicsOptions o)
        {
            if (!Available || UnityEngine.Application.isEditor) return;
            var info = Screen.mainWindowDisplayInfo;
            Debug.Log($"[Display] {info.name}: {info.width}x{info.height}, work area {info.workArea.width}x{info.workArea.height}, " +
                      $"system {Display.main.systemWidth}x{Display.main.systemHeight}, desktop {Screen.currentResolution.width}x{Screen.currentResolution.height}, " +
                      $"window {Screen.width}x{Screen.height} {Screen.fullScreenMode}, sizes {string.Join(" ", Sizes(ScreenMode.Fullscreen))}.");
            foreach (var option in UnityScreenOptions)
            {
                if (!CommandLine.Has(option)) continue;
                Debug.Log($"[Display] {option} given: Unity's screen options decide this run.");
                return;
            }
            Apply(o);
        }

        /// <summary>Applies the size and mode of <paramref name="o"/> (startup and menu changes). No effect on mobile and in the editor.</summary>
        public static void Apply(GraphicsOptions o)
        {
            if (!Available || UnityEngine.Application.isEditor) return;
            var mode = Effective(o.screenMode);
            var size = Resolve(o, out var auto);
            Screen.SetResolution(size.x, size.y, FullScreenModeOf(mode), RefreshRateOf(size));
            Debug.Log($"[Display] Set {size.x}x{size.y} {mode}{(auto ? " (Auto)" : "")}.");
        }

        static FullScreenMode FullScreenModeOf(ScreenMode mode) => mode switch
        {
            ScreenMode.Windowed => FullScreenMode.Windowed,
            ScreenMode.ExclusiveFullscreen => FullScreenMode.ExclusiveFullScreen,
            _ => FullScreenMode.FullScreenWindow,
        };

        static RefreshRate RefreshRateOf(Vector2Int size)
        {
            var best = Screen.currentResolution.refreshRateRatio;
            var found = false;
            foreach (var r in Screen.resolutions)
            {
                if (r.width != size.x || r.height != size.y) continue;
                if (!found || r.refreshRateRatio.value > best.value) best = r.refreshRateRatio;
                found = true;
            }
            return best;
        }

        static Vector2Int NativeSize()
        {
            var info = Screen.mainWindowDisplayInfo;
            return info.height > 0 ? new Vector2Int(info.width, info.height) : new Vector2Int(Display.main.systemWidth, Display.main.systemHeight);
        }

        static bool FitsWindow(Vector2Int size) => FitsWorkArea(size, 1f, WindowHeightShare);

        static bool FitsWorkArea(Vector2Int size, float widthShare, float heightShare)
        {
            var work = Screen.mainWindowDisplayInfo.workArea;
            return work.height <= 0 || (size.x <= work.width * widthShare && size.y <= work.height * heightShare);
        }

        static void AddSize(List<Vector2Int> sizes, Vector2Int size)
        {
            if (size.y >= MinHeight && !sizes.Contains(size)) sizes.Add(size);
        }
    }
}
