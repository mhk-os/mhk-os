using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Player profiles and the score ranking (docs/protocol.md, profiles and score): the players list, the ranking,
    /// mini-profiles, an account's time-trial runs and the own listing. Names shown in lists get their rank from a cache
    /// of summaries: <see cref="Want"/> the account ids on screen, they are fetched in batches of up to 100 on the next
    /// frame (again after <see cref="SummaryMaxAge"/>), <see cref="Changed"/> fires when new ones arrived and
    /// <see cref="TryGetSummary"/> reads them. The cache starts over when the logged-in account or its role changes (moderators
    /// also see disabled accounts). Main thread only; failures are results, never exceptions.
    /// </summary>
    public static class OnlineProfiles
    {
        /// <summary>Seconds a cached summary counts as fresh; <see cref="Want"/> asks again after that.</summary>
        public const float SummaryMaxAge = 60f;
        /// <summary>Seconds <see cref="Get"/> answers from its cache.</summary>
        public const float ProfileMaxAge = 20f;
        // Seconds before ids are asked again after a failed batch (offline, server error).
        const float RetryAfterFailure = 15f;

        /// <summary>Summaries arrived (<see cref="TryGetSummary"/> may say something new).</summary>
        public static event Action Changed;

        // Summary by account id with the time it arrived; a null summary is an id the server did not know (or failed).
        [AutoStaticsCleanup] static Dictionary<long, (PlayerSummary summary, float at)> summaries;
        [AutoStaticsCleanup] static Dictionary<long, (PlayerProfile profile, float at)> profiles;
        [AutoStaticsCleanup] static Dictionary<long, Task<OnlineResult<PlayerProfile>>> profileRequests;
        [AutoStaticsCleanup] static HashSet<long> pending, inFlight;
        [AutoStaticsCleanup] static bool flushScheduled;
        [AutoStaticsCleanup] static (ulong account, bool moderator) cacheFor;

        /// <summary>Listed accounts (<paramref name="limit"/> 1-100) matching <paramref name="search"/>, in <paramref name="sort"/> order.</summary>
        public static async Task<OnlineResult<PlayerPage>> ListPlayers(int offset, int limit, string search, PlayerSort sort)
        {
            var request = new PlayerListRequest { offset = Mathf.Max(0, offset), limit = Limit(limit), search = Search(search), sort = sort };
            var r = await OnlineService.Call(MessageType.ListPlayers, request.Write, PlayerPage.Read, false);
            if (r.Ok) Remember(r.value.players, null);
            return r;
        }

        /// <summary>Ranked accounts (<paramref name="limit"/> 1-100) matching <paramref name="search"/>, with the logged-in account's row.</summary>
        public static async Task<OnlineResult<PlayerPage>> ScoreLeaderboard(int offset, int limit, string search)
        {
            var request = new PlayerListRequest { offset = Mathf.Max(0, offset), limit = Limit(limit), search = Search(search) };
            var r = await OnlineService.Call(MessageType.ScoreLeaderboard, request.Write, PlayerPage.ReadRanked, false);
            if (r.Ok) Remember(r.value.players, r.value.mine);
            return r;
        }

        /// <summary>The summaries of up to 100 <paramref name="accountIds"/> (unknown ones are left out), also into the cache.</summary>
        public static async Task<OnlineResult<List<PlayerSummary>>> GetMany(IReadOnlyList<long> accountIds)
        {
            var request = new GetProfilesRequest();
            for (var i = 0; i < accountIds.Count && request.ids.Count < PlayerSummary.MaxList; i++)
                if (!request.ids.Contains(accountIds[i])) request.ids.Add(accountIds[i]);
            if (request.ids.Count == 0) return new OnlineResult<List<PlayerSummary>>(OnlineResult.Success, new List<PlayerSummary>());
            var r = await OnlineService.Call(MessageType.GetProfiles, request.Write, PlayerSummary.ReadList, false);
            if (r.Ok) Remember(r.value, null);
            return r;
        }

        /// <summary>
        /// The mini-profile of <paramref name="accountId"/>: from the cache while it is younger than
        /// <see cref="ProfileMaxAge"/> (unless <paramref name="fresh"/>), one request however often it is asked for.
        /// </summary>
        public static Task<OnlineResult<PlayerProfile>> Get(long accountId, bool fresh = false)
        {
            Validate();
            if (!fresh && profiles != null && profiles.TryGetValue(accountId, out var cached) && Time.realtimeSinceStartup - cached.at < ProfileMaxAge)
                return Task.FromResult(new OnlineResult<PlayerProfile>(OnlineResult.Success, cached.profile));
            profileRequests ??= new Dictionary<long, Task<OnlineResult<PlayerProfile>>>();
            if (profileRequests.TryGetValue(accountId, out var running)) return running;
            var task = GetAsync(accountId);
            if (!task.IsCompleted) profileRequests[accountId] = task;
            return task;
        }

        static async Task<OnlineResult<PlayerProfile>> GetAsync(long accountId)
        {
            try
            {
                var r = await OnlineService.Call(MessageType.GetProfile, w => w.I64(accountId), PlayerProfile.Read, false);
                if (r.Ok)
                {
                    Validate();
                    (profiles ??= new Dictionary<long, (PlayerProfile, float)>())[accountId] = (r.value, Time.realtimeSinceStartup);
                    Remember(new List<PlayerSummary> { r.value.player }, null);
                }
                return r;
            }
            finally
            {
                profileRequests?.Remove(accountId);
            }
        }

        /// <summary>Runs of <paramref name="accountId"/> on every board (<paramref name="limit"/> 1-100), newest upload first.</summary>
        public static Task<OnlineResult<(int total, List<RunEntry> runs)>> PlayerRuns(long accountId, int offset, int limit)
        {
            var request = new IdPageRequest { id = accountId, offset = Mathf.Max(0, offset), limit = (ushort)Mathf.Clamp(limit, 1, LeaderboardPage.MaxLimit) };
            return OnlineService.Call(MessageType.PlayerRuns, request.Write, ModListFlaggedRequest.ReadAnswer, false);
        }

        /// <summary>Shows the logged-in account in the players list (or takes it off). Its cached profile follows.</summary>
        public static async Task<OnlineResult> SetListed(bool listed)
        {
            var r = await OnlineService.Call(MessageType.SetProfileListed, w => w.Bool(listed), true);
            var me = OnlineService.Account;
            if (r.Ok && me != null && profiles != null && profiles.TryGetValue((long)me.id, out var own)) own.profile.listed = listed;
            return r;
        }

        /// <summary>The cached summary of <paramref name="accountId"/> (of any age), false when none arrived.</summary>
        public static bool TryGetSummary(long accountId, out PlayerSummary summary)
        {
            Validate();
            summary = null;
            return summaries != null && summaries.TryGetValue(accountId, out var e) && (summary = e.summary) != null;
        }

        /// <summary>Fetches the summary of <paramref name="accountId"/> when it is missing or stale (0 = AI or nobody: ignored).</summary>
        public static void Want(long accountId)
        {
            if (accountId <= 0) return;
            Validate();
            if (summaries != null && summaries.TryGetValue(accountId, out var e) && Time.realtimeSinceStartup - e.at < SummaryMaxAge) return;
            if (inFlight != null && inFlight.Contains(accountId)) return;
            (pending ??= new HashSet<long>()).Add(accountId);
            if (flushScheduled) return;
            flushScheduled = true;
            FlushAsync().ContinueWith(t => Debug.LogException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Drops what is cached of <paramref name="accountId"/> (after a moderator changed the account).</summary>
        public static void Invalidate(long accountId)
        {
            summaries?.Remove(accountId);
            profiles?.Remove(accountId);
        }

        // Everything asked for during this frame goes out together, 100 ids per request.
        static async Task FlushAsync()
        {
            await Awaitable.NextFrameAsync();
            flushScheduled = false;
            if (pending == null || pending.Count == 0) return;
            var ids = new List<long>(pending);
            pending.Clear();
            inFlight ??= new HashSet<long>();
            foreach (var id in ids) inFlight.Add(id);
            var batches = new List<Task>();
            for (var start = 0; start < ids.Count; start += PlayerSummary.MaxList)
                batches.Add(FetchAsync(ids.GetRange(start, Mathf.Min(PlayerSummary.MaxList, ids.Count - start))));
            await Task.WhenAll(batches);
        }

        static async Task FetchAsync(List<long> ids)
        {
            var r = await GetMany(ids);
            foreach (var id in ids) inFlight?.Remove(id);
            summaries ??= new Dictionary<long, (PlayerSummary, float)>();
            var now = Time.realtimeSinceStartup;
            var answered = new HashSet<long>();
            if (r.Ok)
                foreach (var s in r.value) answered.Add(s.accountId);
            foreach (var id in ids)
            {
                if (answered.Contains(id)) continue;
                // Unknown ids wait a full age before they are asked again; after a failure the old summary stays and
                // is asked again soon.
                summaries.TryGetValue(id, out var old);
                summaries[id] = r.Ok ? ((PlayerSummary)null, now) : (old.summary, now - SummaryMaxAge + RetryAfterFailure);
            }
        }

        // Stores answered summaries (and the own ranking row) and tells the screens.
        static void Remember(List<PlayerSummary> players, PlayerSummary mine)
        {
            Validate();
            summaries ??= new Dictionary<long, (PlayerSummary, float)>();
            var now = Time.realtimeSinceStartup;
            foreach (var p in players)
                if (p != null) summaries[p.accountId] = (p, now);
            if (mine != null) summaries[mine.accountId] = (mine, now);
            Changed?.Invoke();
        }

        // Another account or role sees other things (disabled accounts, moderation fields): start over.
        static void Validate()
        {
            var me = OnlineService.Account;
            var key = (me?.id ?? 0, me?.IsModerator ?? false);
            if (key == cacheFor) return;
            cacheFor = key;
            summaries?.Clear();
            profiles?.Clear();
        }

        static ushort Limit(int limit) => (ushort)Mathf.Clamp(limit, 1, PlayerSummary.MaxList);

        static string Search(string search) => OnlineTimeTrials.Clip(search?.Trim(), Account.MaxUsername);
    }
}
