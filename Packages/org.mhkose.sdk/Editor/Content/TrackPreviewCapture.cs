using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MHK.Core;
using MHK.Modding;
using MHK.Race;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Takes in-game track preview pictures: queues tracks, runs each as a real race in play mode at the High preset
    /// and renders the race camera above the grid. Results are imported when play mode ends.
    /// </summary>
    [InitializeOnLoad]
    public static class TrackPreviewCapture
    {
        const float SettleSeconds = 2.5f;
        const float TimeoutSeconds = 120f;
        const int PreviewMaxSize = 2048;

        const string QueueKey = "MHK.TrackPreviewCapture.Queue";
        const string ResultsKey = "MHK.TrackPreviewCapture.Results";
        const string StartedPlayKey = "MHK.TrackPreviewCapture.StartedPlay";
        const string RunInBackgroundKey = "MHK.TrackPreviewCapture.RunInBackground";
        const string GraphicsKey = "MHK.TrackPreviewCapture.Graphics";
        const string ProjectModsKey = "MHK.TrackPreviewCapture.ProjectMods";

        static CancellationTokenSource run;

        static TrackPreviewCapture()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (EditorApplication.isPlaying) EditorApplication.delayCall += StartRun;
        }

        [MenuItem("MHK/Content/Capture Track Previews In Game/All Tracks")]
        static void CaptureAll()
        {
            var packs = Modding.ContentPackAssets.ShippedPacks().Concat(Modding.ProjectModPacks.All());
            Capture(packs.SelectMany(p => p.GetAllIncluded<TrackDefinition>()).Select(t => t.Id).Distinct().ToArray());
        }

        [MenuItem("MHK/Content/Capture Track Previews In Game/Selected Tracks")]
        static void CaptureSelected() => Capture(Selection.GetFiltered<TrackDefinition>(SelectionMode.Assets).Select(t => t.Id).ToArray());

        [MenuItem("MHK/Content/Capture Track Previews In Game/Selected Tracks", true)]
        static bool CaptureSelectedValid() => Selection.GetFiltered<TrackDefinition>(SelectionMode.Assets).Length > 0;

        /// <summary>
        /// Queues the tracks <paramref name="trackIds"/> and enters play mode (if not playing yet, play mode then ends
        /// when the last picture is taken). A play session started here also loads the project's mods, so mod tracks
        /// can be captured.
        /// </summary>
        public static void Capture(params string[] trackIds)
        {
            var queue = Queue();
            queue.AddRange(trackIds.Where(id => !string.IsNullOrEmpty(id) && !queue.Contains(id)));
            SetQueue(queue);
            if (queue.Count == 0) return;
            if (EditorApplication.isPlaying)
            {
                StartRun();
                return;
            }
            SessionState.SetBool(StartedPlayKey, true);
            SessionState.SetBool(RunInBackgroundKey, PlayerSettings.runInBackground);
            SessionState.SetBool(ProjectModsKey, Modding.ProjectModPacks.Enabled);
            PlayerSettings.runInBackground = true; // keeps the races running while the editor is in the background
            Modding.ProjectModPacks.Enabled = true;
            EditorApplication.isPlaying = true;
        }

        static List<string> Queue() =>
            SessionState.GetString(QueueKey, "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        static void SetQueue(List<string> queue) => SessionState.SetString(QueueKey, string.Join(",", queue));

        static void Pop(string id)
        {
            var queue = Queue();
            queue.Remove(id);
            SetQueue(queue);
        }

        static void StartRun()
        {
            if (run != null || !EditorApplication.isPlaying) return;
            if (Queue().Count == 0 && SessionState.GetString(GraphicsKey, "") == "") return;
            Application.runInBackground = true; // the PlayerSettings value alone does not keep an unfocused editor playing
            var cts = CancellationTokenSource.CreateLinkedTokenSource(Application.exitCancellationToken);
            run = cts;
            _ = RunAsync(cts);
        }

        static void StopRun()
        {
            var cts = run;
            if (cts == null) return;
            run = null;
            cts.Cancel();
            cts.Dispose();
        }

        static async Awaitable RunAsync(CancellationTokenSource cts)
        {
            try
            {
                await RunQueueAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                if (run == cts && !cts.IsCancellationRequested)
                {
                    run = null;
                    cts.Dispose();
                }
            }
        }

        static async Awaitable RunQueueAsync(CancellationToken token)
        {
            while (true)
            {
                await WaitFor(() => !SceneFlow.Busy, double.PositiveInfinity, token);
                var root = GameRoot.Instance;
                UseHighGraphics(root);
                var queue = Queue();
                if (queue.Count == 0)
                {
                    Finish(root);
                    return;
                }
                await CaptureTrack(root, queue[0], token);
                Pop(queue[0]);
            }
        }

        static async Awaitable CaptureTrack(GameRoot root, string id, CancellationToken token)
        {
            var track = root.Content.Get<TrackDefinition>(id);
            if (track == null || !track.scene.IsSet || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(track)))
            {
                Debug.LogWarning($"[TrackPreviewCapture] '{id}' is not a loaded project track with a scene; skipped " +
                                 "(capturing in a running play session: mod tracks need MHK ▸ Modding ▸ Load Project Mods in Play Mode).");
                return;
            }
            var session = GameSession.CreateQuickRace(root.Content, id, 1);
            session.mode = track.layout == TrackLayout.Arena ? GameMode.Battle : GameMode.SingleRace;
            SceneFlow.StartSession(session);
            if (!await WaitFor(() => OnGrid(id), TimeoutSeconds, token))
            {
                Debug.LogError($"[TrackPreviewCapture] '{id}' did not reach its start grid in {TimeoutSeconds} s; skipped.");
                return;
            }
            var settleStart = EditorApplication.timeSinceStartup;
            await WaitFor(() => EditorApplication.timeSinceStartup - settleStart >= SettleSeconds, double.PositiveInfinity, token);
            try
            {
                var race = RaceManager.Current;
                var path = TrackPreviewShot.TakePicture(race);
                RecordResult(race.Track, path);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static bool OnGrid(string id)
        {
            var race = RaceManager.Current;
            return race != null && race.Track != null && race.Track.Id == id && race.TrackRuntime != null
                   && race.Phase >= RacePhase.Intro && !SceneFlow.Busy;
        }

        static async Awaitable<bool> WaitFor(Func<bool> ready, double timeout, CancellationToken token)
        {
            var start = EditorApplication.timeSinceStartup;
            while (true)
            {
                await Awaitable.NextFrameAsync(token);
                if (!CanProgress()) continue;
                if (ready()) return true;
                if (EditorApplication.timeSinceStartup - start > timeout) return false;
            }
        }

        static bool CanProgress()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPaused) return false;
            var root = GameRoot.Instance;
            return root != null && root.Content != null;
        }

        static void RecordResult(TrackDefinition track, string path)
        {
            var results = SessionState.GetString(ResultsKey, "");
            SessionState.SetString(ResultsKey, results + (results == "" ? "" : ";") + AssetDatabase.GetAssetPath(track) + "|" + path);
            Debug.Log($"[TrackPreviewCapture] {track.Id} -> {path}");
        }

        static void UseHighGraphics(GameRoot root)
        {
            if (SessionState.GetString(GraphicsKey, "") != "") return;
            SessionState.SetString(GraphicsKey, JsonUtility.ToJson(root.Settings.graphics));
            root.Settings.graphics.SetPreset(GraphicsPreset.High);
            root.ApplySettings();
        }

        static void Finish(GameRoot root)
        {
            var saved = SessionState.GetString(GraphicsKey, "");
            if (saved != "")
            {
                JsonUtility.FromJsonOverwrite(saved, root.Settings.graphics);
                root.ApplySettings();
                SessionState.EraseString(GraphicsKey);
            }
            if (SessionState.GetBool(StartedPlayKey, false)) EditorApplication.isPlaying = false;
            else ApplyResults();
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) StartRun();
            if (change == PlayModeStateChange.ExitingPlayMode) StopRun();
            if (change != PlayModeStateChange.EnteredEditMode) return;
            StopRun();
            SessionState.EraseString(GraphicsKey);
            SessionState.EraseString(QueueKey); // a run ends with its play session, stopped by hand the rest is dropped
            if (SessionState.GetBool(StartedPlayKey, false))
            {
                PlayerSettings.runInBackground = SessionState.GetBool(RunInBackgroundKey, false);
                Modding.ProjectModPacks.Enabled = SessionState.GetBool(ProjectModsKey, false);
                SessionState.EraseBool(StartedPlayKey);
                SessionState.EraseBool(RunInBackgroundKey);
                SessionState.EraseBool(ProjectModsKey);
            }
            ApplyResults();
        }

        static void ApplyResults()
        {
            var results = SessionState.GetString(ResultsKey, "");
            SessionState.EraseString(ResultsKey);
            if (results == "") return;
            var entries = results.Split(';').Select(e => e.Split('|')).Where(p => p.Length == 2).ToList();
            ImportPictures(entries);
            foreach (var p in entries)
            {
                var track = AssetDatabase.LoadAssetAtPath<TrackDefinition>(p[0]);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p[1]);
                if (track == null || tex == null) continue;
                track.previewImage = tex;
                if (track.Icon == null) track.EditorSetIcon(tex);
                EditorUtility.SetDirty(track);
            }
            AssetDatabase.SaveAssets();
        }

        static void ImportPictures(List<string[]> entries)
        {
            // One refresh and one batch: importing the pictures one by one would re-import the screens that show them as
            // samples (Track.uxml, Cup.uxml, ...) once per picture, which Unity reports as an import loop.
            AssetDatabase.Refresh();
            using (new AssetDatabase.AssetEditingScope())
            {
                foreach (var p in entries)
                    if (AssetImporter.GetAtPath(p[1]) is TextureImporter ti && Configure(ti))
                        ti.SaveAndReimport();
            }
        }

        static bool Configure(TextureImporter ti)
        {
            var changed = ti.textureType != TextureImporterType.Default || !ti.sRGBTexture
                          || ti.alphaSource != TextureImporterAlphaSource.None || !ti.mipmapEnabled
                          || ti.npotScale != TextureImporterNPOTScale.None || ti.wrapMode != TextureWrapMode.Clamp
                          || ti.filterMode != FilterMode.Bilinear || ti.maxTextureSize != PreviewMaxSize
                          || ti.textureCompression != TextureImporterCompression.Compressed || ti.crunchedCompression;
            if (!changed) return false;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.mipmapEnabled = true;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = PreviewMaxSize;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.crunchedCompression = false;
            return true;
        }
    }
}
