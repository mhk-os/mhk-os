using System.Collections.Generic;
using System.Threading.Tasks;

namespace MHK.Online
{
    /// <summary>
    /// In-game moderation (docs/protocol.md, moderation): hide runs, disable accounts, review flagged runs and their
    /// reports. The server answers <see cref="Status.Forbidden"/> unless the account is a moderator
    /// (<see cref="OnlineService.IsModerator"/>). Nothing here is permanent; deleting is the server's command line.
    /// </summary>
    public static class OnlineModeration
    {
        /// <summary>Hides a run from everyone but moderators; <paramref name="reason"/> goes to the audit log.</summary>
        public static Task<OnlineResult> HideRun(long runId, string reason) =>
            OnlineService.Call(MessageType.ModHideRun, new IdRequest { id = runId, reason = Reason(reason) }.Write, true);

        public static Task<OnlineResult> UnhideRun(long runId) =>
            OnlineService.Call(MessageType.ModUnhideRun, new IdRequest { id = runId }.Write, true);

        /// <summary>Disables a player account (not a moderator's): its sessions end, <paramref name="reason"/> is shown to it.</summary>
        public static Task<OnlineResult> DisableAccount(long accountId, string reason) =>
            OnlineService.Call(MessageType.ModDisableAccount, new IdRequest { id = accountId, reason = Reason(reason) }.Write, true);

        public static Task<OnlineResult> EnableAccount(long accountId) =>
            OnlineService.Call(MessageType.ModEnableAccount, new IdRequest { id = accountId }.Write, true);

        /// <summary>Runs with at least one counted report, most flags first, then newest; <paramref name="limit"/> 1-100.</summary>
        public static Task<OnlineResult<(int total, List<RunEntry> runs)>> ListFlagged(int offset, int limit, bool includeHidden)
        {
            var request = new ModListFlaggedRequest
            {
                offset = offset < 0 ? 0 : offset, limit = (ushort)(limit < 1 ? 1 : limit > LeaderboardPage.MaxLimit ? LeaderboardPage.MaxLimit : limit),
                includeHidden = includeHidden,
            };
            return OnlineService.Call(MessageType.ModListFlagged, request.Write, ModListFlaggedRequest.ReadAnswer, true);
        }

        /// <summary>Every report on a run, also the ignored ones (<see cref="RunReport.counted"/> false).</summary>
        public static Task<OnlineResult<List<RunReport>>> RunReports(long runId) =>
            OnlineService.Call(MessageType.ModRunReports, new IdRequest { id = runId }.Write, ListAnswers.ReadReports, true);

        public static Task<OnlineResult<ModAccountInfo>> GetAccount(long accountId) =>
            OnlineService.Call(MessageType.ModGetAccount, new IdRequest { id = accountId }.Write, ModAccountInfo.Read, true);

        static string Reason(string reason) => OnlineTimeTrials.Clip(reason?.Trim(), ModAccountInfo.MaxReason);
    }
}
