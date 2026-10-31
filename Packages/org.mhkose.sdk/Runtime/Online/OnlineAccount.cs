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

        /// <summary>
        /// Renames the logged-in account (the registration rules; a case-only change of the own name is allowed; the server
        /// allows 3 changes per 30 days). On success <see cref="OnlineService.Account"/>, the stored session's name and with
        /// them the ghost signature take the new name; the server tells the account's other connections. Failures carry the
        /// server's message (InvalidUsername, UsernameTaken, RateLimited).
        /// </summary>
        public static async Task<OnlineResult> ChangeUsername(string username)
        {
            var r = await OnlineService.Call(MessageType.ChangeUsername, new ChangeUsernameRequest { username = username ?? "" }.Write, Account.Read, true);
            if (r.Ok) OnlineService.ApplyAccount(r.value);
            return r.result;
        }
    }
}
