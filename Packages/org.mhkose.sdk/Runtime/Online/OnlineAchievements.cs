using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using AchievementList = MHK.Achievements.Achievements;

namespace MHK.Online
{
    /// <summary>
    /// Achievements online (docs/protocol.md, achievements): on every login the progress no account has received yet
    /// (also what was played logged out) goes to the account, then the account's totals are mirrored
    /// (<see cref="AchievementList.Synced"/>); later progress follows once a race is over. A sync without an answer is
    /// retried later, and on the next login. Also fetches how many players unlocked each achievement.
    /// </summary>
    public static class OnlineAchievements
    {
        const float RetrySeconds = 30f;

        // The logged-in account (0 = none) and whether a sync for it runs, should run again, or waits to retry.
        [AutoStaticsCleanup] static ulong accountId;
        [AutoStaticsCleanup] static bool syncing;
        [AutoStaticsCleanup] static bool again;
        [AutoStaticsCleanup] static bool retryScheduled;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Listen()
        {
            OnlineService.Changed -= OnOnlineChanged;
            OnlineService.Changed += OnOnlineChanged;
            OnOnlineChanged();
        }

        static void OnOnlineChanged()
        {
            var id = OnlineService.Account?.id ?? 0;
            if (id == accountId) return;
            accountId = id;
            AchievementList.SetAccountTotals(null);
            Sync();
        }

        /// <summary>Sends the pending progress to the logged-in account and mirrors its totals; nothing while logged out.</summary>
        internal static void Sync()
        {
            if (accountId == 0) return;
            if (syncing)
            {
                again = true;
                return;
            }
            RunSync().LogExceptionsAndForget();
        }

        // Batches until nothing is pending, then the account's totals stay mirrored. An answer received for an account
        // that is no longer logged in still delivered its batch; the loop goes on for the account now logged in.
        static async Awaitable RunSync()
        {
            syncing = true;
            try
            {
                while (accountId != 0)
                {
                    var account = accountId;
                    again = false;
                    var batch = AchievementList.TakeBatch();
                    var r = await OnlineService.Call(MessageType.SyncAchievements, batch.Write, AchievementTotals.Read, true);
                    var refused = r.result.status == Status.BadRequest;
                    // Ids or account limits the server does not take: retrying cannot help.
                    if (refused) Debug.LogWarning($"[Achievements] The server refused achievement progress, it is dropped: {r.result.message}");
                    if (r.Ok || refused) AchievementList.Delivered(batch);
                    if (account != accountId) continue;
                    if (!r.Ok)
                    {
                        if (refused && !batch.IsEmpty) continue;
                        if (Transient(r.result.status)) RetryLater().LogExceptionsAndForget();
                        else if (!refused) Debug.LogWarning($"[Achievements] Achievements not synced: {r.result.message}");
                        return;
                    }
                    AchievementList.SetAccountTotals(r.value);
                    if (!again && !AchievementList.HasPending) return;
                }
            }
            finally
            {
                syncing = false;
            }
        }

        // No answer, or the server could not take it now.
        static bool Transient(Status? status) => status is null or Status.ServerError or Status.RateLimited;

        static async Awaitable RetryLater()
        {
            if (retryScheduled) return;
            retryScheduled = true;
            try
            {
                await Awaitable.WaitForSecondsAsync(RetrySeconds, Application.exitCancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                retryScheduled = false;
            }
            Sync();
        }

        /// <summary>Asks how many players unlocked <paramref name="ids"/> (empty = every achievement) and updates the views.</summary>
        internal static void FetchShares(List<string> ids) => FetchSharesAsync(ids).LogExceptionsAndForget();

        static async Awaitable FetchSharesAsync(List<string> ids)
        {
            var request = new AchievementSharesRequest();
            request.ids.AddRange(ids);
            var r = await OnlineService.Call(MessageType.AchievementShares, request.Write, AchievementShareCounts.Read, false);
            if (r.Ok) AchievementList.SetShares(r.value);
        }
    }
}
