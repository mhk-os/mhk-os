using MHK.Attract;
using MHK.Core;

namespace MHK.Tests.Performance
{
    /// <summary>
    /// Work per frame a device class affords at its frame rate: counts the profiler reports the same on every machine
    /// (camera and racers aside), so a desktop run catches a change that would cost the device its frame rate.
    /// </summary>
    public readonly struct RenderBudget
    {
        /// <summary>Shader and render state changes (each one costs the CPU and the driver), shadow passes included.</summary>
        public readonly int setPassCalls;
        /// <summary>Triangles drawn, shadow passes included.</summary>
        public readonly int triangles;
        /// <summary>Particles alive at once (overdraw and simulation).</summary>
        public readonly int particles;
        /// <summary>Longest frame (ms) on the title while the menu shows its background race (and, in players, loads it).</summary>
        public readonly float menuHitchMs;

        public RenderBudget(int setPassCalls, int triangles, int particles, float menuHitchMs)
        {
            this.setPassCalls = setPassCalls;
            this.triangles = triangles;
            this.particles = particles;
            this.menuHitchMs = menuHitchMs;
        }
    }

    /// <summary>
    /// A class of device the game has to run smoothly on, with the graphics it recommends there (the first-run defaults:
    /// the Low preset at 30 fps), the menu background it gets and the <see cref="RenderBudget"/> that holds its frame rate.
    /// A run on another machine applies the same settings (Quality level, preset, frame-rate cap and render height), so
    /// the counts match the device's while the frame times are the machine's own.
    /// </summary>
    public sealed class DeviceProfile
    {
        public readonly string name;
        /// <summary>Renders with the Mobile Quality levels (URP assets) instead of the PC ones.</summary>
        public readonly bool mobile;
        public readonly GraphicsPreset preset;
        /// <summary>Frame-rate cap the device plays at and has to hold.</summary>
        public readonly int fps;
        /// <summary>Height of the 3D image: Auto is the preset tier's limit on the device's platform.</summary>
        public readonly RenderResolution resolution;
        /// <summary>The menu's background race on the device (AttractSettings of its platform and tier).</summary>
        public readonly AttractMode attract;
        /// <summary>Karts of a race: the player and the default opponents of a single race.</summary>
        public readonly int karts;
        public readonly RenderBudget budget;

        public DeviceProfile(string name, bool mobile, GraphicsPreset preset, int fps, RenderResolution resolution, AttractMode attract,
            int karts, RenderBudget budget)
        {
            this.name = name;
            this.mobile = mobile;
            this.preset = preset;
            this.fps = fps;
            this.resolution = resolution;
            this.attract = attract;
            this.karts = karts;
            this.budget = budget;
        }

        /// <summary>Frame time (ms) of the target frame rate.</summary>
        public float FrameMs => 1000f / fps;

        /// <summary>The graphics options the device plays with.</summary>
        public GraphicsOptions Graphics()
        {
            var o = new GraphicsOptions { version = GraphicsOptions.CurrentVersion };
            o.SetPreset(preset);
            o.maxFps = fps;
            o.resolution = resolution;
            return o;
        }

        // Test case names.
        public override string ToString() => name;

        /// <summary>
        /// The device classes under test. Count budgets: the highest of several desktop runs of the race scenarios (the
        /// counts move with where the camera and the racers are) plus about 30 %. The menu may stall for about three frames
        /// at most. Raise a budget only with a measurement on such a device that still holds its frame rate.
        /// </summary>
        public static readonly DeviceProfile[] All =
        {
            // Entry-level phone (Mali-G52 / Adreno 610 class, 3-4 GB): Low, 30 fps, 720p, a still menu picture.
            new("LowEndPhone", true, GraphicsPreset.Low, 30, RenderResolution.P720, AttractMode.Still, 1 + GameSession.DefaultOpponents,
                new RenderBudget(setPassCalls: 110, triangles: 500_000, particles: 500, menuHitchMs: 100f)),
            // Laptop with integrated graphics: Low, 30 fps, 1080p, the live menu race.
            new("LowEndPc", false, GraphicsPreset.Low, 30, RenderResolution.Auto, AttractMode.Live, 1 + GameSession.DefaultOpponents,
                new RenderBudget(setPassCalls: 110, triangles: 500_000, particles: 500, menuHitchMs: 100f)),
        };
    }
}
