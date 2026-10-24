using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace MHK.Assets
{
    /// <summary>
    /// Sounds loaded from files: sound effects decoded during content load and mapped from the bundled clip to the
    /// loaded one (<see cref="GameAssets.Audio"/>), music loaded on request and cached per path.
    /// </summary>
    static partial class AudioOverrides
    {
        const int ParallelLoads = 8;
        const int MaxMusicClips = 4;
        const string MusicFolder = "Music/";

        /// <summary>A sound effect to load.</summary>
        internal struct Job
        {
            public AudioClip original;
            public string file;
            public string[] scenes;
        }

        [AutoStaticsCleanup] static readonly Dictionary<AudioClip, AudioClip> replacements = new();
        [AutoStaticsCleanup] static readonly Dictionary<AudioClip, string[]> sceneClips = new();
        [AutoStaticsCleanup] static readonly Dictionary<string, AudioClip> music = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly Dictionary<string, AudioClip> musicOriginals = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly List<string> musicOrder = new();
        [AutoStaticsCleanup] static readonly Dictionary<string, float> durations = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly List<AudioClip> scratch = new();

        internal static int Count => replacements.Count;

        internal static AudioClip Resolve(AudioClip clip) =>
            clip != null && replacements.TryGetValue(clip, out var r) && r != null ? r : clip;

        /// <summary>Music: below a Music folder of its pack or a streamed clip. Loaded on request, never during content load.</summary>
        internal static bool IsMusic(string path, UnityEngine.Object asset)
        {
            var slash = path.IndexOf('/');
            if (slash >= 0 && string.Compare(path, slash + 1, MusicFolder, 0, MusicFolder.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
            return asset is AudioClip clip && clip.loadType == AudioClipLoadType.Streaming;
        }

        /// <summary>Loads the sound effects of <paramref name="jobs"/>, several files at a time. Returns the number loaded.</summary>
        internal static async Awaitable<int> LoadAsync(List<Job> jobs, CancellationToken ct)
        {
            var pending = new List<(Job job, UnityWebRequest request)>();
            var next = 0;
            var loaded = 0;
            try
            {
                while (next < jobs.Count || pending.Count > 0)
                {
                    while (pending.Count < ParallelLoads && next < jobs.Count)
                    {
                        pending.Add((jobs[next], Request(jobs[next].file, false)));
                        next++;
                    }
                    await Awaitable.NextFrameAsync(ct);
                    for (var i = pending.Count - 1; i >= 0; i--)
                    {
                        if (!pending[i].request.isDone) continue;
                        if (Finish(pending[i].job, pending[i].request)) loaded++;
                        pending[i].request.Dispose();
                        pending.RemoveAt(i);
                    }
                }
            }
            finally
            {
                foreach (var p in pending) p.request.Dispose();
            }
            return loaded;
        }

        static bool Finish(in Job job, UnityWebRequest request)
        {
            var clip = Content(request, job.file);
            if (clip == null || job.original == null) return false;
            clip.name = job.original.name;
            if (replacements.TryGetValue(job.original, out var old) && old != null) UnityEngine.Object.Destroy(old);
            replacements[job.original] = clip;
            if (job.scenes != null) sceneClips[job.original] = job.scenes;
            return true;
        }

        /// <summary>The sound at <paramref name="path"/>: a cached or newly loaded file, else the packed clip.</summary>
        internal static async Awaitable<AudioClip> LoadAsync(string path, bool stream, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (music.TryGetValue(path, out var cached) && cached != null)
            {
                Touch(path);
                return cached;
            }
            var found = Catalog.TryFind(path, out var at);
            var original = found ? at.Entry.asset as AudioClip : null;
            if (original != null && replacements.TryGetValue(original, out var sfx) && sfx != null) return sfx;
            var file = Catalog.WinningFile(path, found ? at.source : null);
            if (file == null) return await PackedAsync(path, found, at, ct);
            var clip = await LoadFileAsync(file, stream, ct);
            if (clip == null) return await PackedAsync(path, found, at, ct);
            return Cache(path, clip, original);
        }

        static AudioClip Cache(string path, AudioClip clip, AudioClip original)
        {
            if (music.TryGetValue(path, out var other) && other != null)
            {
                UnityEngine.Object.Destroy(clip);
                Touch(path);
                return other;
            }
            clip.name = Path.GetFileNameWithoutExtension(path);
            music[path] = clip;
            musicOrder.Add(path);
            if (original != null)
            {
                musicOriginals[path] = original;
                replacements[original] = clip;
            }
            durations[path] = clip.length;
            Evict(path);
            return clip;
        }

        static void Touch(string path)
        {
            musicOrder.Remove(path);
            musicOrder.Add(path);
        }

        // The oldest cached music goes first, a clip an AudioSource holds (playing or queued) stays.
        static void Evict(string keep)
        {
            if (musicOrder.Count <= MaxMusicClips) return;
            var held = new HashSet<AudioClip>();
            foreach (var s in UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include))
                if (s.clip != null) held.Add(s.clip);
            for (var i = 0; i < musicOrder.Count && musicOrder.Count > MaxMusicClips; i++)
            {
                var path = musicOrder[i];
                if (path == keep || (music.TryGetValue(path, out var c) && held.Contains(c))) continue;
                Release(path);
                i--;
            }
        }

        static void Release(string path)
        {
            musicOrder.Remove(path);
            if (music.TryGetValue(path, out var clip) && clip != null) UnityEngine.Object.Destroy(clip);
            music.Remove(path);
            if (musicOriginals.TryGetValue(path, out var original)) replacements.Remove(original);
            musicOriginals.Remove(path);
        }

        static async Awaitable<AudioClip> PackedAsync(string path, bool found, Catalog.Located at, CancellationToken ct)
        {
            if (!found) return GameAssets.EditorSource != null ? GameAssets.EditorSource.Load(path) as AudioClip : null;
            var e = at.Entry;
            if (e.asset != null) return e.asset as AudioClip;
            if (at.source.bundle == null || string.IsNullOrEmpty(e.bundleAsset)) return null;
            var request = at.source.bundle.LoadAssetAsync<AudioClip>(e.bundleAsset);
            while (!request.isDone) await Awaitable.NextFrameAsync(ct);
            var clip = request.asset as AudioClip;
            Catalog.Remember(clip, e.path);
            return clip;
        }

        /// <summary>Length of the sound at <paramref name="path"/> in seconds, a file is loaded into a temporary clip.</summary>
        internal static async Awaitable<float> DurationAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path)) return 0f;
            if (durations.TryGetValue(path, out var known)) return known;
            var found = Catalog.TryFind(path, out var at);
            var file = Catalog.WinningFile(path, found ? at.source : null);
            float length;
            if (file == null)
            {
                var packed = await PackedAsync(path, found, at, ct);
                length = packed != null ? packed.length : 0f;
            }
            else
            {
                var clip = await LoadFileAsync(file, true, ct);
                length = clip != null ? clip.length : 0f;
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }
            durations[path] = length;
            return length;
        }

        static async Awaitable<AudioClip> LoadFileAsync(string file, bool stream, CancellationToken ct)
        {
            using var request = Request(file, stream);
            while (!request.isDone) await Awaitable.NextFrameAsync(ct);
            return Content(request, file);
        }

        static UnityWebRequest Request(string file, bool stream)
        {
            var request = UnityWebRequestMultimedia.GetAudioClip(FileUri(file), TypeOf(file));
            var handler = (DownloadHandlerAudioClip)request.downloadHandler;
            handler.streamAudio = stream;
            request.SendWebRequest();
            return request;
        }

        static AudioClip Content(UnityWebRequest request, string file)
        {
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Assets] Cannot load sound '{file}': {request.error}");
                return null;
            }
            var clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip != null && clip.length > 0f) return clip;
            Debug.LogWarning($"[Assets] '{file}' holds no playable sound.");
            if (clip != null) UnityEngine.Object.Destroy(clip);
            return null;
        }

        static AudioType TypeOf(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                case ".mp3": return AudioType.MPEG;
                case ".aif":
                case ".aiff": return AudioType.AIFF;
                default: return AudioType.UNKNOWN;
            }
        }

        // Each path segment escaped (spaces, '#'), a drive letter kept.
        static string FileUri(string file)
        {
            var full = Path.GetFullPath(file).Replace('\\', '/');
            var parts = full.Split('/');
            for (var i = 0; i < parts.Length; i++)
                if (!(i == 0 && parts[i].EndsWith(":", StringComparison.Ordinal))) parts[i] = Uri.EscapeDataString(parts[i]);
            return "file://" + (full.StartsWith("/", StringComparison.Ordinal) ? "" : "/") + string.Join("/", parts);
        }

        /// <summary>Destroys the loaded clips of scenes that <paramref name="unused"/> accepts.</summary>
        internal static int ReleaseScenes(Func<string[], bool> unused)
        {
            scratch.Clear();
            foreach (var pair in sceneClips)
                if (unused(pair.Value)) scratch.Add(pair.Key);
            foreach (var original in scratch)
            {
                sceneClips.Remove(original);
                if (replacements.TryGetValue(original, out var clip) && clip != null) UnityEngine.Object.Destroy(clip);
                replacements.Remove(original);
            }
            var n = scratch.Count;
            scratch.Clear();
            return n;
        }

        /// <summary>Destroys every loaded clip (content reload, end of play mode).</summary>
        internal static void Clear()
        {
            foreach (var clip in replacements.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            foreach (var clip in music.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            replacements.Clear();
            sceneClips.Clear();
            music.Clear();
            musicOriginals.Clear();
            musicOrder.Clear();
            durations.Clear();
        }
    }
}
