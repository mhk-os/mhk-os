using System.Threading.Tasks;

namespace MHK.Online
{
    /// <summary>Account management of the logged-in account beyond the session (docs/protocol.md, accounts).</summary>
    public static class OnlineAccount
    {
        /// <summary>
        /// Deletes the logged-in account after checking <paramref name="password"/>. Its time-trial runs stay on the
        /// leaderboards as Anonymous, or are also hidden with <paramref name="deleteRuns"/>. On success the stored session is
        /// forgotten; the connection stays open, logged out.
        /// </summary>
        public static async Task<OnlineResult> Delete(string password, bool deleteRuns)
        {
            var r = await OnlineService.Call(MessageType.DeleteAccount, new DeleteAccountRequest { password = password ?? "", deleteRuns = deleteRuns }.Write, true);
            if (r.Ok) OnlineService.ForgetSession();
            return r;
        }
    }
}
