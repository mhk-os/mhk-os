using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace MHK.Assets
{
    public static partial class GameAssets
    {
        [AutoStaticsCleanup] static bool ready = true;
        [NoAutoStaticsCleanup] static CancellationTokenSource applying;

        /// <summary>False while a content load applies the overrides (scene changes wait for it).</summary>
        internal static bool Ready => ready;

        /// <summary>Content load, before the mods load: scans the asset packs (the Mods screen and stand-in logos need them).</summary>
        internal static void BeginContent(UserSettings settings)
        {
            Cancel();
            ready = false;
            AssetPacks.Refresh(settings);
        }

        /// <summary>Content load, after the mods loaded: registers their indexes and applies the overrides behind the loading screen.</summary>
        internal static void ApplyContent(CancellationToken ct)
        {
            Catalog.Register();
            ClearPathLists();
            applying = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ApplyContentAsync(applying.Token).LogExceptionsAndForget();
        }

        /// <summary>Before the mods unload: drops what belongs to their bundles. The player's textures stay until the next apply decides.</summary>
        internal static void UnloadContent()
        {
            Cancel();
            TextureOverrides.ForgetBundles();
            AudioOverrides.Clear();
            Catalog.Clear();
            ClearPathLists();
        }

        static void Cancel()
        {
            if (applying == null) return;
            applying.Cancel();
            applying.Dispose();
            applying = null;
        }

        internal static async Awaitable WaitReadyAsync(CancellationToken ct)
        {
            while (!ready) await Awaitable.NextFrameAsync(ct);
        }

        static async Awaitable ApplyContentAsync(CancellationToken ct)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var textures = new List<TextureOverrides.Job>();
                var sounds = new List<AudioOverrides.Job>();
                var wanted = new HashSet<Texture2D>();
                CollectContent(textures, sounds, wanted);
                CollectEditor(textures, sounds, wanted);
                var restored = TextureOverrides.RestoreWhere((t, scenes, _) => scenes == null && !wanted.Contains(t));
                var stats = await TextureOverrides.ApplyAsync(textures, ct);
                var textureSeconds = watch.Elapsed.TotalSeconds;
                var loaded = await AudioOverrides.LoadAsync(sounds, ct);
                Log("Overrides applied", textures.Count, stats, textureSeconds, loaded, watch.Elapsed.TotalSeconds - textureSeconds, restored);
                ready = true;
                RaiseReloaded();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                ready = true;
            }
        }

        static void Log(string what, int textures, TextureOverrides.Stats stats, double textureSeconds, int sounds, double soundSeconds, int restored)
        {
            if (textures + sounds + restored == 0) return;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[Assets] {0}: {1} textures ({2:0.0} Mpx, decode {3:0.00} s over {4} frames) in {5:0.00} s, {6} sounds in {7:0.00} s, {8} restored.",
                what, textures, stats.pixels / 1e6, stats.decodeSeconds, stats.frames, textureSeconds, sounds, soundSeconds, restored));
        }

        // Entries a scene of a mod bundle uses alone wait for that scene, the player's own are few and apply now.
        static void CollectContent(List<TextureOverrides.Job> textures, List<AudioOverrides.Job> sounds, HashSet<Texture2D> wanted)
        {
            foreach (var source in Catalog.Sources)
            {
                var entries = source.index.entries;
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.asset == null || string.IsNullOrEmpty(e.path)) continue;
                    var file = Catalog.WinningFile(e.path, source);
                    if (file != null) AddJob(e.asset, e.path, file, null, source.bundle != null, textures, sounds, wanted);
                }
            }
        }

        static void CollectEditor(List<TextureOverrides.Job> textures, List<AudioOverrides.Job> sounds, HashSet<Texture2D> wanted)
        {
            if (EditorSource == null || !AssetPacks.HasFiles) return;
            foreach (var path in EditorSource.Paths())
            {
                var file = AssetPacks.FileFor(path);
                if (file == null) continue;
                var asset = EditorSource.Load(path);
                if (asset != null && !Catalog.TryPathOf(asset, out _)) AddJob(asset, path, file, null, false, textures, sounds, wanted);
            }
        }

        static void AddJob(UnityEngine.Object asset, string path, string file, string[] scenes, bool fromBundle,
            List<TextureOverrides.Job> textures, List<AudioOverrides.Job> sounds, HashSet<Texture2D> wanted)
        {
            if (asset is Texture2D texture)
            {
                wanted.Add(texture);
                if (!TextureOverrides.IsCurrent(texture, file))
                    textures.Add(new TextureOverrides.Job { texture = texture, file = file, scenes = scenes, fromBundle = fromBundle });
            }
            else if (asset is AudioClip clip && !AudioOverrides.IsMusic(path, clip))
                sounds.Add(new AudioOverrides.Job { original = clip, file = file, scenes = scenes });
        }

        /// <summary>
        /// Before the scene at <paramref name="scenePath"/> loads: replaces the textures and sounds only that scene uses
        /// (from its mod's content bundle) and releases those of scenes no longer loaded.
        /// </summary>
        internal static async Awaitable PrepareSceneAsync(string scenePath, CancellationToken ct)
        {
            await WaitReadyAsync(ct);
            ReleaseScenes(scenePath);
            var entries = Catalog.SceneEntries(scenePath);
            if (entries.Count == 0) return;
            var watch = Stopwatch.StartNew();
            var textures = new List<TextureOverrides.Job>();
            var sounds = new List<AudioOverrides.Job>();
            var wanted = new HashSet<Texture2D>();
            foreach (var at in entries)
            {
                var e = at.Entry;
                var file = Catalog.WinningFile(e.path, at.source);
                if (file == null) continue;
                var asset = Catalog.Load(at);
                if (asset == null || (asset is Texture2D t && TextureOverrides.IsApplied(t) && TextureOverrides.IsCurrent(t, file))) continue;
                AddJob(asset, e.path, file, e.scenes, at.source.bundle != null, textures, sounds, wanted);
            }
            var stats = await TextureOverrides.ApplyAsync(textures, ct);
            var textureSeconds = watch.Elapsed.TotalSeconds;
            var loaded = await AudioOverrides.LoadAsync(sounds, ct);
            Log("Scene '" + scenePath + "'", textures.Count, stats, textureSeconds, loaded, watch.Elapsed.TotalSeconds - textureSeconds, 0);
        }

        // Scene-only replacements of scenes that are no longer loaded go back to their placeholders.
        static void ReleaseScenes(string keep)
        {
            bool Unused(string[] scenes)
            {
                if (scenes == null) return false;
                foreach (var s in scenes)
                    if (string.Equals(s, keep, StringComparison.OrdinalIgnoreCase) || SceneManager.GetSceneByPath(s).isLoaded) return false;
                return true;
            }
            var textures = TextureOverrides.RestoreWhere((_, scenes, _) => Unused(scenes));
            var sounds = AudioOverrides.ReleaseScenes(Unused);
            if (textures + sounds > 0) Debug.Log($"[Assets] Released {textures} textures and {sounds} sounds of unloaded scenes.");
        }

        /// <summary>
        /// Replaces <paramref name="texture"/> of <paramref name="index"/> from its winning file right away (a disabled mod's
        /// logo before its bundle unloads). <paramref name="modFolder"/> holds the mod's Assets folder.
        /// </summary>
        public static void ApplyNow(Texture2D texture, PackAssetIndex index, string modFolder)
        {
            if (texture == null || index == null) return;
            foreach (var e in index.entries)
            {
                if (e.asset != texture) continue;
                var source = new Catalog.Source { index = index, looseRoot = modFolder != null ? System.IO.Path.Combine(modFolder, "Assets") : null };
                var file = Catalog.WinningFile(e.path, source);
                if (file != null) TextureOverrides.ReplaceNow(texture, file);
                return;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void HookScenes()
        {
            Cancel();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
#if UNITY_EDITOR
            Application.quitting -= RestoreAll;
            Application.quitting += RestoreAll;
#endif
        }

        // Scene AudioSources keep the bundled clip they were saved with: they get the loaded one, playing ones restart.
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (AudioOverrides.Count == 0) return;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
            {
                var clip = source.clip;
                var replacement = Audio(clip);
                if (replacement == clip) continue;
                var playing = source.isPlaying;
                source.clip = replacement;
                if (playing) source.Play();
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (!SceneFlow.Busy && ready) ReleaseScenes(null);
        }

        // Editor: project and bundle textures must leave play mode unchanged.
        static void RestoreAll()
        {
            Cancel();
            var n = TextureOverrides.Count;
            TextureOverrides.RestoreAll();
            AudioOverrides.Clear();
            Catalog.Clear();
            if (n > 0) Debug.Log($"[Assets] Restored {n} replaced textures at the end of play mode.");
        }
    }
}
