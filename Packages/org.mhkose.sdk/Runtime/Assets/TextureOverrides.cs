using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;

namespace MHK.Assets
{
    /// <summary>
    /// Replaces textures in place with image files: the Texture2D keeps its identity (materials, UI and sprites keep
    /// their reference), its size, mips and pixels come from the file, sRGB, wrap, filter and aniso stay. DXT targets
    /// are compressed by the decoder, others stay uncompressed. Restoring unloads the texture, so the bundle or project
    /// asset loads again unchanged.
    /// </summary>
    static partial class TextureOverrides
    {
        // Longest stretch of decoding per frame, so the loading screen keeps moving.
        const double FrameBudgetMs = 40.0;
        const int ReadAhead = 4;
        const int BlockSize = 4;

        /// <summary>A texture to replace.</summary>
        internal struct Job
        {
            public Texture2D texture;
            public string file;
            public string[] scenes;
            public bool fromBundle;
        }

        sealed class Applied
        {
            public string key;
            public string[] scenes;
            public bool fromBundle;
        }

        [AutoStaticsCleanup] static readonly Dictionary<Texture2D, Applied> applied = new();
        [AutoStaticsCleanup] static readonly List<Texture2D> scratch = new();

        internal static int Count => applied.Count;

        /// <summary>Whether <paramref name="texture"/> already shows <paramref name="file"/> as it is on disk now.</summary>
        internal static bool IsCurrent(Texture2D texture, string file) =>
            applied.TryGetValue(texture, out var a) && a.key == Key(file);

        internal static bool IsApplied(Texture2D texture) => applied.ContainsKey(texture);

        static string Key(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? file + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks : file;
        }

        /// <summary>Totals of one <see cref="ApplyAsync"/> run.</summary>
        internal struct Stats
        {
            public long pixels;
            public double decodeSeconds;
            public int frames;
        }

        /// <summary>Replaces every job's texture, reading the next files while the current one decodes.</summary>
        internal static async Awaitable<Stats> ApplyAsync(List<Job> jobs, CancellationToken ct)
        {
            var stats = new Stats();
            if (jobs.Count == 0) return stats;
            var reads = new Queue<Task<byte[]>>();
            var queued = 0;
            var frame = Stopwatch.StartNew();
            var decode = new Stopwatch();
            for (var i = 0; i < jobs.Count; i++)
            {
                while (queued < jobs.Count && queued < i + ReadAhead) reads.Enqueue(File.ReadAllBytesAsync(jobs[queued++].file, ct));
                var read = reads.Dequeue();
                while (!read.IsCompleted) await Awaitable.NextFrameAsync(ct);
                decode.Start();
                stats.pixels += Replace(jobs[i], read);
                decode.Stop();
                if (frame.Elapsed.TotalMilliseconds < FrameBudgetMs) continue;
                await Awaitable.NextFrameAsync(ct);
                stats.frames++;
                frame.Restart();
            }
            stats.decodeSeconds = decode.Elapsed.TotalSeconds;
            return stats;
        }

        /// <summary>Replaces <paramref name="texture"/> with <paramref name="file"/> right away, without recording it (a texture about to be copied).</summary>
        internal static bool ReplaceNow(Texture2D texture, string file)
        {
            try
            {
                return Decode(texture, File.ReadAllBytes(file), file);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Assets] Cannot read '{file}': {e.Message}");
                return false;
            }
        }

        static long Replace(in Job job, Task<byte[]> read)
        {
            var t = job.texture;
            if (t == null) return 0;
            if (read.IsFaulted || read.IsCanceled)
            {
                Debug.LogWarning($"[Assets] Cannot read '{job.file}': {(read.Exception != null ? read.Exception.GetBaseException().Message : "cancelled")}");
                return 0;
            }
            if (!Decode(t, read.Result, job.file)) return 0;
            applied[t] = new Applied { key = Key(job.file), scenes = job.scenes, fromBundle = job.fromBundle };
            return (long)t.width * t.height;
        }

        static bool Decode(Texture2D t, byte[] bytes, string file)
        {
            if (GraphicsFormatUtility.IsCompressedFormat(t.graphicsFormat) && ImageHeader.TryRead(bytes, out var w, out var h, out var alpha) &&
                (w % BlockSize != 0 || h % BlockSize != 0))
                bytes = ToBlockSize(bytes, w, h, alpha);
            if (bytes != null && ImageConversion.LoadImage(t, bytes, !t.isReadable)) return true;
            Debug.LogWarning($"[Assets] '{file}' is not a readable PNG or JPG: '{t.name}' keeps its bundled look.");
            return false;
        }

        // Block-compressed textures need sizes in whole 4 pixel blocks (decoding fails otherwise): the image is scaled up
        // to the next multiple of 4 on the GPU, raw values passed through.
        static byte[] ToBlockSize(byte[] bytes, int width, int height, bool alpha)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Bilinear };
            var target = new Texture2D(RoundUp(width), RoundUp(height), alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
            var rt = RenderTexture.GetTemporary(target.width, target.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var active = RenderTexture.active;
            try
            {
                if (!source.LoadImage(bytes)) return null;
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                target.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                return target.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(source);
                UnityEngine.Object.Destroy(target);
            }
        }

        static int RoundUp(int size) => (size + BlockSize - 1) / BlockSize * BlockSize;

        /// <summary>Puts every replaced texture that <paramref name="restore"/> accepts (texture, scenes, from a bundle) back to its bundled or project state.</summary>
        internal static int RestoreWhere(Func<Texture2D, string[], bool, bool> restore)
        {
            scratch.Clear();
            foreach (var pair in applied)
                if (pair.Key == null || restore(pair.Key, pair.Value.scenes, pair.Value.fromBundle)) scratch.Add(pair.Key);
            foreach (var t in scratch)
            {
                applied.Remove(t);
                if (t != null) Resources.UnloadAsset(t);
            }
            var n = scratch.Count;
            scratch.Clear();
            return n;
        }

        /// <summary>Forgets the textures of mod bundles (they unload with their bundle) and of scenes.</summary>
        internal static void ForgetBundles()
        {
            scratch.Clear();
            foreach (var pair in applied)
                if (pair.Key == null || pair.Value.fromBundle) scratch.Add(pair.Key);
            foreach (var t in scratch) applied.Remove(t);
            scratch.Clear();
        }

        internal static void RestoreAll() => RestoreWhere((_, _, _) => true);
    }
}
