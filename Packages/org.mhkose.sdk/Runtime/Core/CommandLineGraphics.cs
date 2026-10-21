using System;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// <c>-graphics low|medium|high</c>, <c>-fps N</c> (0 = unlimited) and <c>-vsync 0|1</c>: graphics for this run only
    /// (benchmarks). The settings file keeps the player's own choice.
    /// </summary>
    sealed class CommandLineGraphics
    {
        GraphicsOptions storedGraphics;
        bool storedGraphicsChosen;

        /// <summary>Overrides <paramref name="settings"/>' graphics from the command line, keeping the player's for saving.</summary>
        public void Apply(UserSettings settings)
        {
            var preset = CommandLine.Value("-graphics");
            var fps = CommandLine.Value("-fps");
            var vSync = CommandLine.Value("-vsync");
            if (preset == null && fps == null && vSync == null) return;
            storedGraphics = settings.graphics;
            storedGraphicsChosen = settings.graphicsChosen;
            settings.graphics = storedGraphics.Clone();
            settings.graphicsChosen = true;
            if (preset != null) ApplyPreset(settings.graphics, preset);
            if (fps != null) ApplyFps(settings.graphics, fps);
            if (vSync != null) settings.graphics.vSync = vSync == "1";
        }

        /// <summary>Runs <paramref name="save"/> with the player's own graphics in <paramref name="settings"/>.</summary>
        public void Save(UserSettings settings, Action<UserSettings> save)
        {
            if (storedGraphics == null)
            {
                save(settings);
                return;
            }
            var (session, chosen) = (settings.graphics, settings.graphicsChosen);
            (settings.graphics, settings.graphicsChosen) = (storedGraphics, storedGraphicsChosen);
            save(settings);
            (settings.graphics, settings.graphicsChosen) = (session, chosen);
        }

        static void ApplyPreset(GraphicsOptions graphics, string preset)
        {
            if (Enum.TryParse<GraphicsPreset>(preset, true, out var p) && p != GraphicsPreset.Custom) graphics.SetPreset(p);
            else Debug.LogWarning($"[GameRoot] Unknown -graphics '{preset}' (low, medium or high).");
        }

        static void ApplyFps(GraphicsOptions graphics, string fps)
        {
            if (CommandLine.TryInt("-fps", out var n) && n >= 0) graphics.maxFps = n;
            else Debug.LogWarning($"[GameRoot] Invalid -fps '{fps}'.");
        }
    }
}
