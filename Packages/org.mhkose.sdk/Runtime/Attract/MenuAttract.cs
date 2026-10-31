using System;
using System.Collections.Generic;
using System.Threading;
using MHK.Audio;
using MHK.Core;
using MHK.Modding;
using MHK.Race;
using MHK.UI;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Application = UnityEngine.Device.Application;
using Screen = UnityEngine.Device.Screen;

namespace MHK.Attract
{
    /// <summary>
    /// The main menu's background race: a random circuit of any loaded pack with random computer drivers, built from a
    /// <see cref="RaceManager"/> prefab in attract mode and shown through the <see cref="MenuStage"/>. Race sounds are
    /// muted. A new track loads on every menu visit and after <see cref="AttractSettings.minutesPerTrack"/>.
    /// </summary>
    public partial class MenuAttract : MonoBehaviour
    {
        const int AttractLaps = 999;
        const int LoadAttempts = 3;
        const string CommandLineMode = "-attract";

        [SerializeField, Tooltip("Shots, race setup, track rotation and the setups per platform and preset.")]
        AttractSettings settings;
        [SerializeField, Tooltip("Attract race prefab: a RaceManager with its karts root and a camera with ChaseCamera and AttractDirector.")]
        RaceManager racePrefab;
        [SerializeField, Tooltip("The menu stage that fades the race or its picture in and out.")]
        MenuStage stage;

        /// <summary>
        /// Mode for every platform and preset, null = the settings' setups. The regression harness sets Off before play
        /// mode starts (deterministic menu pictures). The command line <c>-attract off|still|live</c> does the same in players.
        /// </summary>
        [NoAutoStaticsCleanup] public static AttractMode? Forced;

        /// <summary>The background shows what it will keep showing: the race, its picture, or the neutral background for good.</summary>
        [AutoStaticsCleanup] public static bool Settled { get; private set; }

        [NoAutoStaticsCleanup] static string lastTrackId;

        readonly List<TrackDefinition> tracks = new();
        readonly List<CharacterDefinition> drivers = new();
        readonly System.Random random = new(Environment.TickCount);
        RaceManager race;
        AttractDirector director;
        RenderTexture target;
        Texture2D picture;

        ContentRegistry Content => GameRoot.Instance.Content;

        void Start()
        {
            Settled = false;
            RunUntilLeftAsync(destroyCancellationToken).LogExceptionsAndForget();
        }

        void OnDestroy()
        {
            Settled = false;
            if (GameRoot.Instance != null) AudioManager.WorldMuted = false;
            FreeTarget();
            if (picture != null) Destroy(picture);
        }

        // Leaving the menu cancels every wait, the scene change unloads the race with the menu.
        async Awaitable RunUntilLeftAsync(CancellationToken ct)
        {
            try
            {
                await RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        async Awaitable RunAsync(CancellationToken ct)
        {
            if (stage != null && !CanRace())
            {
                stage.ShowEmpty();
                Settled = true;
                return;
            }
            var karts = 0;
            var mode = settings != null && racePrefab != null && stage != null ? Mode(out karts) : AttractMode.Off;
            if (mode == AttractMode.Off)
            {
                Settled = true;
                return;
            }
            AudioManager.WorldMuted = true;
            while (await LoadRaceAsync(karts, mode == AttractMode.Still, ct))
            {
                if (mode == AttractMode.Still)
                {
                    await TakePictureAsync(ct);
                    break;
                }
                director.Camera.enabled = true;
                stage.ShowRace(director.Camera, settings.fadeSeconds);
                Settled = true;
                if (settings.minutesPerTrack <= 0f) return;
                await Awaitable.WaitForSecondsAsync(settings.minutesPerTrack * 60f, ct);
                Settled = false;
                await stage.HideAsync(settings.fadeSeconds, ct);
                await UnloadRaceAsync(ct);
            }
            Settled = true;
        }

        AttractMode Mode(out int karts)
        {
            var profile = (Application.isMobilePlatform ? settings.mobile : settings.desktop).For(GraphicsQuality.Tier);
            karts = Mathf.Clamp(profile.karts, 1, GameRoot.Instance.Config.maxRacers);
            if (Forced.HasValue) return Forced.Value;
            return Enum.TryParse<AttractMode>(CommandLine.Value(CommandLineMode), true, out var fromCommandLine) ? fromCommandLine : profile.mode;
        }

        // Tries other tracks when one cannot be built. False when none could.
        async Awaitable<bool> LoadRaceAsync(int karts, bool offScreen, CancellationToken ct)
        {
            for (var attempt = 0; attempt < LoadAttempts; attempt++)
            {
                var track = PickTrack();
                if (track == null) return false;
                lastTrackId = track.Id;
                race = Instantiate(racePrefab, transform);
                director = race.GetComponentInChildren<AttractDirector>();
                race.RunAsAttract(BuildSession(track, karts));
                // Off screen until shown: live races draw nothing while they load, still pictures render into a target.
                if (offScreen) director.Camera.targetTexture = NewTarget();
                else director.Camera.enabled = false;
                while (race.Phase == RacePhase.Loading && !race.LoadFailed) await Awaitable.NextFrameAsync(ct);
                while (!race.LoadFailed && !director.HasShot) await Awaitable.NextFrameAsync(ct);
                if (!race.LoadFailed) return true;
                await UnloadRaceAsync(ct);
            }
            return false;
        }

        static bool Raceable(TrackDefinition t) => t != null && t.layout == TrackLayout.Circuit && t.scene.IsSet;

        // No mod with a circuit and a driver (the base content disabled): the title shows the empty background.
        bool CanRace()
        {
            if (Content.All<CharacterDefinition>().Count == 0) return false;
            foreach (var t in Content.All<TrackDefinition>())
                if (Raceable(t)) return true;
            return false;
        }

        TrackDefinition PickTrack()
        {
            tracks.Clear();
            foreach (var t in Content.All<TrackDefinition>())
                if (Raceable(t) && t.Id != lastTrackId) tracks.Add(t);
            if (tracks.Count == 0 && lastTrackId != null)
            {
                var last = Content.Get<TrackDefinition>(lastTrackId);
                if (Raceable(last)) tracks.Add(last);
            }
            return tracks.Count > 0 ? tracks[random.Next(tracks.Count)] : null;
        }

        GameSession BuildSession(TrackDefinition track, int karts)
        {
            var session = new GameSession
            {
                mode = GameMode.SingleRace,
                difficulty = settings.difficulty,
                laps = AttractLaps,
                lapsFromTrack = false,
                powerupsEnabled = settings.items,
            };
            session.trackIds.Add(track.Id);
            drivers.Clear();
            drivers.AddRange(Content.All<CharacterDefinition>());
            for (var i = drivers.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (drivers[i], drivers[j]) = (drivers[j], drivers[i]);
            }
            var kartPool = AiRoster.Karts(Content);
            for (var i = 0; i < karts && drivers.Count > 0; i++)
            {
                var c = drivers[i % drivers.Count];
                var kart = AiRoster.RandomKart(kartPool, random);
                session.racers.Add(new RacerSetup
                {
                    characterId = c.Id,
                    kartId = kart != null ? kart.Id : null,
                    displayName = c.DisplayName,
                });
            }
            return session;
        }

        // The race renders into the picture until it is taken, then unloads: nothing runs while the menu is open.
        async Awaitable TakePictureAsync(CancellationToken ct)
        {
            while (race.Phase != RacePhase.Racing) await Awaitable.NextFrameAsync(ct);
            director.Hold(settings.stillShot);
            await Awaitable.WaitForSecondsAsync(settings.stillAfterSeconds, ct);
            await Awaitable.EndOfFrameAsync(ct);
            var cam = director.Camera;
            cam.enabled = false;
            picture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false) { name = "AttractPicture" };
            Graphics.CopyTexture(target, picture);
            cam.targetTexture = null;
            FreeTarget();
            stage.ShowPicture(picture, settings.fadeSeconds);
            await UnloadRaceAsync(ct);
        }

        RenderTexture NewTarget()
        {
            FreeTarget();
            target = new RenderTexture(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height), 24) { name = "AttractTarget" };
            target.Create();
            return target;
        }

        void FreeTarget()
        {
            if (target != null) Destroy(target);
            target = null;
        }

        async Awaitable UnloadRaceAsync(CancellationToken ct)
        {
            var scene = race != null ? race.TrackScene : default;
            if (race != null) Destroy(race.gameObject);
            race = null;
            director = null;
            if (gameObject.scene.IsValid()) SceneManager.SetActiveScene(gameObject.scene);
            if (scene.IsValid() && scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(scene);
                while (unload != null && !unload.isDone) await Awaitable.NextFrameAsync(ct);
            }
            var unused = Resources.UnloadUnusedAssets();
            while (!unused.isDone) await Awaitable.NextFrameAsync(ct);
        }
    }
}
