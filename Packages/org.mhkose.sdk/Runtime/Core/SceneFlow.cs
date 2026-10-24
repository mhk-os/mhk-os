using System;
using System.Globalization;
using System.Threading;
using MHK.Modding;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Core
{
    /// <summary>
    /// Scene names and transitions. Each change shows the loading screen, loads the scene asynchronously and hides the
    /// screen once the scene (a race: its track and karts) is ready. Requests while a change runs are ignored.
    /// </summary>
    public static partial class SceneFlow
    {
        public const string MainMenu = "MainMenu";
        public const string Race = "Race";

        // Share of Progress taken by the Race scene itself, the track and the karts take the rest.
        const float RaceSceneShare = 0.1f;
        const float CoverGraceSeconds = 1f;
        // AsyncOperation.progress stops here while the scene waits for activation.
        const float SceneLoadPhaseEnd = 0.9f;

        /// <summary>True while a scene change runs.</summary>
        [AutoStaticsCleanup] public static bool Busy { get; private set; }

        /// <summary>Progress 0..1 of the running scene change (the loading screen's bar).</summary>
        [AutoStaticsCleanup] public static float Progress { get; private set; }

        public static void GoToMenu() => Load(MainMenu);

        public static void StartSession(GameSession session)
        {
            if (Busy) return;
            GameRoot.Instance.Session = session;
            Load(Race);
        }

        public static void NextRace()
        {
            if (Busy) return;
            GameRoot.Instance.Session.raceIndex++;
            Load(Race);
        }

        public static void RestartRace() => Load(Race);

        /// <summary>Changes to <paramref name="scene"/> behind the loading screen (<see cref="Race"/>: the session's current race).</summary>
        public static void Load(string scene)
        {
            if (Busy)
            {
                Debug.LogWarning($"[SceneFlow] '{scene}' requested while a scene is loading; ignored.");
                return;
            }
            var root = GameRoot.Instance;
            ChangeAsync(root, scene, root.destroyCancellationToken).LogExceptionsAndForget();
        }

        /// <summary>
        /// Mods screen Apply: behind the loading screen unloads every scene, reloads mods and content
        /// (<see cref="GameRoot.ReloadContent"/>) and starts the main menu again at its title screen.
        /// </summary>
        public static void ReloadContent()
        {
            if (Busy)
            {
                Debug.LogWarning("[SceneFlow] Content reload requested while a scene is loading; ignored.");
                return;
            }
            var root = GameRoot.Instance;
            ReloadAsync(root, root.destroyCancellationToken).LogExceptionsAndForget();
        }

        static async Awaitable ReloadAsync(GameRoot root, CancellationToken ct)
        {
            Busy = true;
            Progress = 0f;
            var screen = root.LoadingScreen;
            try
            {
                await CoverScreenAsync(screen, null, null, ct);
                var timer = new SceneLoadTimer();
                await UnloadScenesAsync(ct);
                root.Session = null;
                Audio.AudioManager.PlayMusic(null);
                root.ReloadContent();
                var loaded = await LoadSceneAsync(MainMenu, 1f, timer, ct);
                await SettleAsync(timer, ct);
                Progress = 1f;
                if (loaded) Debug.Log(timer.Report("Content reload"));
                Complete(screen, null);
            }
            catch (OperationCanceledException)
            {
                Busy = false;
            }
        }

        // Mod bundles may only unload when no scene uses their assets: an empty scene replaces the menu and its background track.
        static async Awaitable UnloadScenesAsync(CancellationToken ct)
        {
            var empty = SceneManager.CreateScene("ContentReload");
            SceneManager.SetActiveScene(empty);
            var scenes = new Scene[SceneManager.sceneCount];
            for (var i = 0; i < scenes.Length; i++) scenes[i] = SceneManager.GetSceneAt(i);
            foreach (var scene in scenes)
            {
                if (scene == empty || !scene.isLoaded) continue;
                var op = SceneManager.UnloadSceneAsync(scene);
                while (op != null && !op.isDone) await Awaitable.NextFrameAsync(ct);
            }
            var unused = Resources.UnloadUnusedAssets();
            while (!unused.isDone) await Awaitable.NextFrameAsync(ct);
        }

        static async Awaitable ChangeAsync(GameRoot root, string scene, CancellationToken ct)
        {
            Busy = true;
            Progress = 0f;
            var race = scene == Race;
            var session = race ? root.Session : null;
            var track = session != null ? root.Content.Get<TrackDefinition>(session.CurrentTrackId) : null;
            var screen = root.LoadingScreen;
            try
            {
                await CoverScreenAsync(screen, session, track, ct);
                var timer = new SceneLoadTimer();
                var sceneShare = race ? RaceSceneShare : 1f;
                var loaded = await LoadSceneAsync(scene, sceneShare, timer, ct);
                var manager = loaded && race ? RaceManager.Current : null;
                if (manager != null) await WaitForRaceAsync(manager, sceneShare, timer, ct);
                else await SettleAsync(timer, ct);
                Progress = 1f;
                if (loaded) Debug.Log(timer.Report(track != null ? track.Id : scene));
                Complete(screen, manager);
            }
            catch (OperationCanceledException)
            {
                Busy = false;
            }
        }

        // Waits until the screen covers the view, with a grace in case it cannot bind.
        static async Awaitable CoverScreenAsync(UI.LoadingScreen screen, GameSession session, TrackDefinition track, CancellationToken ct)
        {
            if (screen == null) return;
            screen.Show(session, track);
            var giveUp = Time.realtimeSinceStartup + screen.fadeInSeconds + CoverGraceSeconds;
            while (!screen.Covering && Time.realtimeSinceStartup < giveUp) await Awaitable.NextFrameAsync(ct);
        }

        static async Awaitable<bool> LoadSceneAsync(string scene, float sceneShare, SceneLoadTimer timer, CancellationToken ct)
        {
            var op = SceneManager.LoadSceneAsync(scene);
            while (op != null && !op.isDone)
            {
                Progress = sceneShare * Mathf.Clamp01(op.progress / SceneLoadPhaseEnd);
                await Awaitable.NextFrameAsync(ct);
                timer.Frame();
            }
            timer.SceneDone();
            return op != null;
        }

        static async Awaitable WaitForRaceAsync(RaceManager manager, float sceneShare, SceneLoadTimer timer, CancellationToken ct)
        {
            while (manager != null && manager.Phase == RacePhase.Loading && !manager.LoadFailed)
            {
                Progress = sceneShare + (1f - sceneShare) * manager.LoadProgress;
                await Awaitable.NextFrameAsync(ct);
                timer.Frame();
            }
        }

        // Menu screens bind a frame after loading.
        static async Awaitable SettleAsync(SceneLoadTimer timer, CancellationToken ct)
        {
            await Awaitable.NextFrameAsync(ct);
            timer.Frame();
        }

        static void Complete(UI.LoadingScreen screen, RaceManager manager)
        {
            Busy = false;
            if (manager != null && manager.LoadFailed)
            {
                Load(MainMenu);
                return;
            }
            if (screen != null) screen.Hide();
        }

        /// <summary>Durations of one scene change for the log.</summary>
        sealed class SceneLoadTimer
        {
            readonly float start = Time.realtimeSinceStartup;
            float sceneLoaded, longestFrame;

            public void Frame() => longestFrame = Mathf.Max(longestFrame, Time.unscaledDeltaTime);
            public void SceneDone() => sceneLoaded = Time.realtimeSinceStartup;

            public string Report(string what) => string.Format(CultureInfo.InvariantCulture,
                "[SceneFlow] {0} ready in {1:0.00} s (scene {2:0.00} s, longest frame {3:0} ms).",
                what, Time.realtimeSinceStartup - start, sceneLoaded - start, longestFrame * 1000f);
        }
    }
}
