using System;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>First scene: initialises <see cref="GameRoot"/>, then opens the menu or a command-line quick race (<c>-race</c>, <c>-mode</c>, <c>-opponents</c>).</summary>
    public class BootLoader : MonoBehaviour
    {
        [Tooltip("Scene opened after booting (unless the command line starts a race).")]
        [SerializeField] string nextScene = SceneFlow.MainMenu;
        [Tooltip("Shortest time (s, real time) the boot scene stays up.")]
        [SerializeField] float minimumSeconds = 0.5f;

        async Awaitable Start()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            var t = Time.realtimeSinceStartup;
            var root = GameRoot.Instance;
            while (Time.realtimeSinceStartup - t < minimumSeconds) await Awaitable.NextFrameAsync(destroyCancellationToken);
            if (!TryStartQuickRace(root)) SceneFlow.Load(nextScene);
        }

        static bool TryStartQuickRace(GameRoot root)
        {
            var trackId = CommandLine.Value("-race");
            if (trackId == null) return false;
            var opponents = CommandLine.TryInt("-opponents", out var n) ? n : GameSession.DefaultOpponents;
            var session = GameSession.CreateQuickRace(root.Content, trackId, opponents);
            if (Enum.TryParse<GameMode>(CommandLine.Value("-mode"), true, out var mode)) session.mode = mode;
            SceneFlow.StartSession(session);
            return true;
        }
    }
}
