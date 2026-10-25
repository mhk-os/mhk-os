using System;
using System.Globalization;
using System.IO;
using System.Threading;
using MHK.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace MHK.Modding
{
    public static partial class ModSources
    {
        const float StallSeconds = 30f;
        const string PartExtension = ".part", PartInfoExtension = ".json";

        static string DownloadFolder => Path.Combine(DataPaths.Root, "Cache", "ModDownloads");
        static string PartPath(string repo) => Path.Combine(DownloadFolder, Key(repo) + PartExtension);
        static string PartInfoPath(string repo) => Path.Combine(DownloadFolder, Key(repo) + PartInfoExtension);
        static string Key(string repo) => repo.Replace('/', '_').ToLowerInvariant();

        /// <summary>Which release asset a partial download belongs to. The ETag of its answer lets a resume ask for the same bytes (If-Range).</summary>
        [Serializable]
        sealed class PartInfo
        {
            public string repo, tag, name, etag;
            public long size;

            public bool Matches(Release r) => r != null && tag == r.tag && name == r.zipName && size == r.zipSize;
        }

        /// <summary>Bytes of the kept partial download of the release <paramref name="s"/> would install, 0 when there is none.</summary>
        static long PartialBytes(Source s)
        {
            var info = ReadJson<PartInfo>(PartInfoPath(s.repo));
            if (info == null || !info.Matches(s.latest)) return 0;
            var part = new FileInfo(PartPath(s.repo));
            return part.Exists && part.Length <= s.latest.zipSize ? part.Length : 0;
        }

        static void DeletePartial(string repo)
        {
            DeleteQuietly(PartPath(repo));
            DeleteQuietly(PartInfoPath(repo));
        }

        // Game start: downloads of sources no longer listed and files of older game versions go.
        static void PruneDownloads()
        {
            try
            {
                if (!Directory.Exists(DownloadFolder)) return;
                foreach (var file in Directory.GetFiles(DownloadFolder))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file);
                    var listed = false;
                    foreach (var s in All) listed |= Key(s.repo) == name;
                    if (!listed || (ext != PartExtension && ext != PartInfoExtension)) DeleteQuietly(file);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[ModSources] Cannot clean {DownloadFolder}: {e.Message}");
            }
        }

        // The zip goes to a partial file that a resume continues with an HTTP Range request (If-Range with the ETag of the
        // first answer). A server that answers 200 instead of 206 sends everything again: the file starts over. No overall
        // timeout (mods are hundreds of MB): a download that receives nothing for StallSeconds stops (Offline, bytes kept).
        static async Awaitable<string> DownloadAsync(Source s, Release release, CancellationToken ct)
        {
            Directory.CreateDirectory(DownloadFolder);
            var path = PartPath(s.repo);
            var info = ReadJson<PartInfo>(PartInfoPath(s.repo));
            if (info == null || !info.Matches(release) || !File.Exists(path) || new FileInfo(path).Length > release.zipSize)
            {
                DeletePartial(s.repo);
                info = new PartInfo { repo = s.repo, tag = release.tag, name = release.zipName, size = release.zipSize };
                SavePartInfo(info);
            }
            var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
            s.downloadBytes = release.zipSize;
            s.downloadedBytes = offset;
            SetState(s, State.Downloading, null);
            if (offset > 0 && offset == release.zipSize) return path;
            using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                file.Seek(offset, SeekOrigin.Begin);
                await ReceiveAsync(s, release, info, file, offset, ct);
            }
            var size = new FileInfo(path).Length;
            if (release.zipSize > 0 && size != release.zipSize)
            {
                DeletePartial(s.repo);
                throw new SourceException(State.Error, "The download is incomplete.");
            }
            s.downloadedBytes = size;
            RaiseChanged();
            return path;
        }

        static async Awaitable ReceiveAsync(Source s, Release release, PartInfo info, FileStream file, long offset, CancellationToken ct)
        {
            using var r = Request(release.zipUrl, false);
            r.timeout = 0;
            if (offset > 0)
            {
                r.SetRequestHeader("Range", "bytes=" + offset.ToString(CultureInfo.InvariantCulture) + "-");
                if (!string.IsNullOrEmpty(info.etag)) r.SetRequestHeader("If-Range", info.etag);
            }
            var handler = new ResumeHandler(r, file, offset, answer =>
            {
                info.etag = answer.GetResponseHeader("ETag") ?? info.etag;
                SavePartInfo(info);
            });
            r.downloadHandler = handler;
            var lastBytes = offset;
            var lastProgress = Time.realtimeSinceStartup;
            var stalled = false;
            await SendAsync(r, () =>
            {
                if (handler.Written != lastBytes)
                {
                    lastBytes = s.downloadedBytes = handler.Written;
                    lastProgress = Time.realtimeSinceStartup;
                    RaiseChanged(true);
                }
                else if (!stalled && Time.realtimeSinceStartup - lastProgress > StallSeconds)
                {
                    stalled = true;
                    r.Abort();
                }
            }, ct);
            s.downloadedBytes = handler.Written;
            if (handler.Restarted) Debug.Log($"[ModSources] {s.repo}: the server sent the whole file instead of the rest, the download started over.");
            if (handler.Failure != null)
            {
                DeletePartial(s.repo);
                throw new SourceException(State.Error, handler.Failure);
            }
            if (stalled) throw new SourceException(State.Offline, "The download stopped.");
            if (r.responseCode == 416) DeletePartial(s.repo);
            ThrowOnFailure(r);
        }

        static void SavePartInfo(PartInfo info)
        {
            try
            {
                SafeFile.WriteAllText(PartInfoPath(info.repo), JsonUtility.ToJson(info));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[ModSources] Cannot write {PartInfoPath(info.repo)}: {e.Message}");
            }
        }

        /// <summary>
        /// Writes the answer behind the bytes already in the file. On the first bytes it looks at the answer: 206 must
        /// continue at the file's end, 200 is the whole file (the file starts over), other answers are not written.
        /// </summary>
        sealed class ResumeHandler : DownloadHandlerScript
        {
            const int BufferBytes = 256 * 1024;

            readonly UnityWebRequest request;
            readonly FileStream file;
            readonly long offset;
            readonly Action<UnityWebRequest> started;
            bool begun, discard;

            public long Written { get; private set; }
            public bool Restarted { get; private set; }
            public string Failure { get; private set; }

            public ResumeHandler(UnityWebRequest request, FileStream file, long offset, Action<UnityWebRequest> started) : base(new byte[BufferBytes])
            {
                this.request = request;
                this.file = file;
                this.offset = offset;
                this.started = started;
                Written = offset;
            }

            protected override bool ReceiveData(byte[] data, int length)
            {
                if (data == null || length <= 0) return true;
                if (!begun)
                {
                    begun = true;
                    if (!Begin()) return false;
                }
                if (discard) return true;
                file.Write(data, 0, length);
                Written += length;
                return true;
            }

            bool Begin()
            {
                var code = request.responseCode;
                if (code == 206 && StartOf(request.GetResponseHeader("Content-Range")) != offset)
                {
                    Failure = "The server sent the wrong part of the download.";
                    return false;
                }
                if (code == 200 && offset > 0)
                {
                    file.SetLength(0);
                    file.Seek(0, SeekOrigin.Begin);
                    Written = 0;
                    Restarted = true;
                }
                discard = code != 200 && code != 206;
                if (!discard) started?.Invoke(request);
                return true;
            }

            // "bytes 100-999/1000" starts at 100.
            static long StartOf(string contentRange)
            {
                if (string.IsNullOrEmpty(contentRange)) return -1;
                var space = contentRange.IndexOf(' ');
                var dash = contentRange.IndexOf('-');
                if (space < 0 || dash <= space) return -1;
                return long.TryParse(contentRange.Substring(space + 1, dash - space - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : -1;
            }
        }
    }
}
