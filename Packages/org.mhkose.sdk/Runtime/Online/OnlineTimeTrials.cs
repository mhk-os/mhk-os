using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Ghosts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Time-trial leaderboards and ghosts on the server (docs/protocol.md, time trials): boards, leaderboard pages,
    /// uploads (also the automatic one of a new personal best), downloads into <see cref="OnlineGhostCache"/> and reports
    /// of suspicious runs. Main thread only; failures are results, never exceptions.
    /// </summary>
    public static class OnlineTimeTrials
    {
        /// <summary>Downloads in flight at most; races ask for up to 50 ghosts at once.</summary>
        public const int MaxConcurrentDownloads = 6;
        // Ghost files are megabytes at worst: more than the default wait.
        static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(90);

        /// <summary>An upload finished or MyRuns answered: <see cref="TryGetUploaded"/> may say something new.</summary>
        public static event Action Changed;

        // Runs of the logged-in account by ghost id, from uploads and MyRuns; valid for uploadedFor only.
        [AutoStaticsCleanup] static Dictionary<string, RunEntry> uploaded;
        [AutoStaticsCleanup] static ulong uploadedFor;
        [AutoStaticsCleanup] static Dictionary<string, Task<OnlineResult<OnlineGhostInfo>>> downloads;
        [AutoStaticsCleanup] static Queue<TaskCompletionSource<bool>> waitingDownloads;
        [AutoStaticsCleanup] static int activeDownloads;
        [AutoStaticsCleanup] static string autoUploadGhost;
        [AutoStaticsCleanup] static Task<OnlineResult<UploadResult>> autoUpload;

        /// <summary>Boards of <paramref name="trackId"/>, most recent upload first. 255 = any engine class, contentVersion 0 = any.</summary>
        public static Task<OnlineResult<List<BoardInfo>>> ListBoards(string trackId, int engineClass, int contentVersion)
        {
            var request = new ListBoardsRequest { trackId = trackId ?? "", engineClass = (byte)Mathf.Clamp(engineClass, 0, 255), contentVersion = contentVersion };
            return OnlineService.Call(MessageType.ListBoards, request.Write, ListBoardsRequest.ReadAnswer, false);
        }

        /// <summary>Rows <paramref name="offset"/> on (at most <paramref name="limit"/>, 1-100) of a board, filtered, with the account's own row.</summary>
        public static Task<OnlineResult<LeaderboardPage>> GetLeaderboard(long boardId, int offset, int limit, LeaderboardFilter filter)
        {
            var request = new GetLeaderboardRequest
            {
                boardId = boardId, offset = Mathf.Max(0, offset), limit = (ushort)Mathf.Clamp(limit, 1, LeaderboardPage.MaxLimit),
                filter = filter ?? new LeaderboardFilter(),
            };
            return OnlineService.Call(MessageType.GetLeaderboard, request.Write, LeaderboardPage.Read, false);
        }

        /// <summary>
        /// Uploads the stored file of a local ghost; remembers the run for <see cref="TryGetUploaded"/>. Refused here when the
        /// run's kart, character or track is replaced by a mod (as the server would), and only sent once the content is
        /// announced: the server checks the run against it.
        /// </summary>
        public static async Task<OnlineResult<UploadResult>> Upload(GhostEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.location)) return OnlineResult.Failed("This ghost has no file to upload.");
            var modified = ModifiedContent(entry.header);
            if (modified != null) return new OnlineResult(Status.Forbidden, modified);
            var login = await OnlineService.EnsureLoggedIn();
            if (!login.Ok) return login;
            var content = await OnlineLobby.EnsureContent();
            if (!content.Ok) return content;
            byte[] file;
            try
            {
                file = File.ReadAllBytes(entry.location);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return OnlineResult.Failed($"Could not read the ghost file: {e.Message}");
            }
            if (file.Length > UploadRunRequest.MaxFile) return new OnlineResult(Status.TooLarge, "The ghost file is too large to upload.");
            var r = await OnlineService.Call(MessageType.UploadRun, new UploadRunRequest { file = file }.Write, UploadResult.Read, true, TransferTimeout);
            if (r.Ok)
            {
                Remember(r.value.run);
                Achievements.AchievementStats.Add(Achievements.AchievementStats.TimeTrialsUploaded);
                Changed?.Invoke();
            }
            return r;
        }

        // Why the server refuses this run for its content, as far as this game can tell (replaced ids), null when it would not.
        static string ModifiedContent(GhostHeader h)
        {
            var registry = GameRoot.Instance != null ? GameRoot.Instance.Content : null;
            if (registry == null || h == null) return null;
            return Replaced(registry, h.kartId) ?? Replaced(registry, h.characterId) ?? Replaced(registry, h.trackId);
        }

        static string Replaced(Modding.ContentRegistry registry, string id)
        {
            var by = ContentManifests.OverriddenBy(registry, id);
            return by != null ? $"This run uses {id} replaced by {by}. Runs with modified content can't be uploaded." : null;
        }

        /// <summary>
        /// The ghost of <paramref name="run"/> in <see cref="OnlineGhostCache"/>: at once when cached for this run (taking the
        /// uploader's current name from <paramref name="run"/>, accounts can be renamed), else downloaded (at most
        /// <see cref="MaxConcurrentDownloads"/> at a time, one download per run however often it is asked for), checked to be
        /// a ghost file and stored with the uploader's name.
        /// </summary>
        public static Task<OnlineResult<OnlineGhostInfo>> Download(RunEntry run)
        {
            var cached = CachedInfo(run);
            if (cached != null)
            {
                if (!string.IsNullOrEmpty(run.username) && cached.username != run.username)
                {
                    cached.username = run.username;
                    OnlineGhostCache.Update(cached);
                }
                return Task.FromResult(new OnlineResult<OnlineGhostInfo>(OnlineResult.Success, cached));
            }
            downloads ??= new Dictionary<string, Task<OnlineResult<OnlineGhostInfo>>>(StringComparer.OrdinalIgnoreCase);
            if (downloads.TryGetValue(run.ghostId, out var running)) return running;
            var task = DownloadAsync(run);
            if (!task.IsCompleted) downloads[run.ghostId] = task;
            return task;
        }

        /// <summary>
        /// Whether the cache holds <paramref name="run"/>'s ghost as this run: run ids repeat across servers (and a reset
        /// database), so a cached ghost of the same id with another run id is downloaded again, which also keeps reports of
        /// it going to the right run.
        /// </summary>
        public static bool IsCached(RunEntry run) => CachedInfo(run) != null;

        static OnlineGhostInfo CachedInfo(RunEntry run) =>
            run != null && OnlineGhostCache.TryGet(run.ghostId, out var info) && info.runId == run.runId ? info : null;

        /// <summary>
        /// Reports what the ghost validator found in run <paramref name="runId"/> (the downloaded ghost
        /// <paramref name="ghostId"/>); the answer is the run's flag count.
        /// </summary>
        public static async Task<OnlineResult<int>> ReportRun(string ghostId, long runId, FlagReason reason, float atSeconds, string detail)
        {
            var request = new ReportRunRequest
            {
                runId = runId, reason = reason, atSeconds = float.IsFinite(atSeconds) ? atSeconds : 0f, detail = Clip(detail, RunReport.MaxDetail),
            };
            var r = await OnlineService.Call<int>(MessageType.ReportRun, request.Write, TimeTrialWire.U32, true);
            if (r.Ok && OnlineGhostCache.TryGet(ghostId, out var info) && info.runId == runId && info.flags != r.value)
            {
                info.flags = r.value;
                OnlineGhostCache.Update(info);
            }
            return r;
        }

        /// <summary>Why run <paramref name="runId"/> is hidden: for moderators, and for the run's owner (NotFound otherwise).</summary>
        public static Task<OnlineResult<RunHiddenInfo>> HiddenReason(long runId) =>
            OnlineService.Call(MessageType.RunHiddenReason, new IdRequest { id = runId }.Write, RunHiddenInfo.Read, true);

        /// <summary>Every run of the logged-in account on <paramref name="trackId"/> (all boards, hidden ones too).</summary>
        public static async Task<OnlineResult<List<RunEntry>>> MyRuns(string trackId)
        {
            var r = await OnlineService.Call(MessageType.MyRuns, w => w.String(trackId ?? "", BoardInfo.MaxTrackId), ListAnswers.ReadRuns, true);
            if (r.Ok)
            {
                foreach (var run in r.value) Remember(run);
                Changed?.Invoke();
            }
            return r;
        }

        /// <summary>The logged-in account's run of the local ghost <paramref name="ghostId"/>, as last uploaded or listed by MyRuns.</summary>
        public static bool TryGetUploaded(string ghostId, out RunEntry run)
        {
            run = null;
            var account = OnlineService.Account;
            if (account == null || uploaded == null || account.id != uploadedFor || string.IsNullOrEmpty(ghostId)) return false;
            return uploaded.TryGetValue(ghostId, out run);
        }

        /// <summary>
        /// After a time trial stored a new personal best: uploads it in the background when
        /// <see cref="UserSettings.autoUploadBests"/> is on and an account is (or is being) logged in.
        /// <see cref="TryGetAutoUpload"/> then finds the upload by the ghost's id.
        /// </summary>
        public static void AutoUploadBest(GhostEntry entry)
        {
            var root = GameRoot.Instance;
            if (entry == null || root == null || !root.Settings.autoUploadBests || OnlineService.Username == null) return;
            autoUploadGhost = entry.Id;
            autoUpload = Upload(entry);
            LogAutoUpload(entry.Id, autoUpload).ContinueWith(t => Debug.LogException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        static async Task LogAutoUpload(string ghostId, Task<OnlineResult<UploadResult>> upload)
        {
            var r = await upload;
            if (r.Ok) Debug.Log($"[Online] Uploaded record {ghostId}: run {r.value.run.runId}, rank {r.value.run.rank}.");
            else Debug.Log($"[Online] Record upload of {ghostId} failed: {r.result.message}");
        }

        /// <summary>The automatic upload of the local ghost <paramref name="ghostId"/> (running or done), false when none was started.</summary>
        public static bool TryGetAutoUpload(string ghostId, out Task<OnlineResult<UploadResult>> upload)
        {
            upload = autoUploadGhost != null && autoUploadGhost == ghostId ? autoUpload : null;
            return upload != null;
        }

        static void Remember(RunEntry run)
        {
            var account = OnlineService.Account;
            if (run == null || account == null || string.IsNullOrEmpty(run.ghostId)) return;
            if (uploaded == null || uploadedFor != account.id)
            {
                uploaded = new Dictionary<string, RunEntry>(StringComparer.OrdinalIgnoreCase);
                uploadedFor = account.id;
            }
            uploaded[run.ghostId] = run;
        }

        static async Task<OnlineResult<OnlineGhostInfo>> DownloadAsync(RunEntry run)
        {
            try
            {
                await AcquireDownloadSlot();
                OnlineResult<DownloadRunResponse> r;
                try
                {
                    r = await OnlineService.Call(MessageType.DownloadRun, new IdRequest { id = run.runId }.Write, DownloadRunResponse.Read, false, TransferTimeout);
                }
                finally
                {
                    ReleaseDownloadSlot();
                }
                return r.Ok ? StoreDownload(r.value) : r.result;
            }
            finally
            {
                downloads?.Remove(run.ghostId);
            }
        }

        static OnlineResult<OnlineGhostInfo> StoreDownload(DownloadRunResponse d)
        {
            GhostHeader header;
            try
            {
                using var stream = new MemoryStream(d.file, false);
                header = GhostFile.Read(stream, true).header;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is FormatException)
            {
                return OnlineResult.Failed($"The server sent a damaged ghost ({e.Message}).");
            }
            if (header == null || string.IsNullOrEmpty(header.id) || !string.Equals(header.id, d.run.ghostId, StringComparison.OrdinalIgnoreCase))
                return OnlineResult.Failed("The server sent another ghost than the run it named.");
            var info = new OnlineGhostInfo
            {
                ghostId = header.id, runId = d.run.runId, boardId = d.run.boardId, username = d.run.username, country = d.run.country,
                trackId = header.trackId, finishMs = d.run.finishMs, flags = d.run.flags,
            };
            try
            {
                OnlineGhostCache.Store(info, d.file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return OnlineResult.Failed($"Could not store the ghost: {e.Message}");
            }
            return new OnlineResult<OnlineGhostInfo>(OnlineResult.Success, info);
        }

        static Task AcquireDownloadSlot()
        {
            if (activeDownloads < MaxConcurrentDownloads)
            {
                activeDownloads++;
                return Task.CompletedTask;
            }
            var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            (waitingDownloads ??= new Queue<TaskCompletionSource<bool>>()).Enqueue(wait);
            return wait.Task;
        }

        // Hands the slot to the next waiting download, if any.
        static void ReleaseDownloadSlot()
        {
            if (waitingDownloads != null && waitingDownloads.Count > 0) waitingDownloads.Dequeue().SetResult(true);
            else activeDownloads--;
        }

        // Cuts at a character boundary so the UTF-8 form fits (a validator detail must never fail the report).
        internal static string Clip(string s, int maxBytes)
        {
            s ??= "";
            var bytes = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var pair = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                // A lone surrogate cannot be encoded: the text ends before it.
                var size = pair ? 4 : s[i] < 0x80 ? 1 : s[i] < 0x800 ? 2 : char.IsSurrogate(s[i]) ? 0 : 3;
                if (size == 0 || bytes + size > maxBytes) return s.Substring(0, i);
                bytes += size;
                if (pair) i++;
            }
            return s;
        }
    }
}
