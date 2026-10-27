using System;
using System.Globalization;
using System.Threading.Tasks;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The moderator actions of the leaderboard and Reports pages: hide / unhide a run (a reason is asked when hiding) and
    /// disable / enable its uploader's account (the account's state is fetched first; a reason is asked when disabling).
    /// Each reports a player-facing line through <c>report</c> (good or not) and calls <c>done</c> after a change.
    /// </summary>
    public static class ModerationPrompts
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static string HideQuestionFormat { get; set; } = "Hide {0}'s run {1}?";
        public static string HideText { get; set; } = "Hide";
        public static string UnhideQuestionFormat { get; set; } = "Show {0}'s run {1} again?";
        public static string UnhideText { get; set; } = "Unhide";
        public static string HiddenText { get; set; } = "Run hidden.";
        public static string UnhiddenText { get; set; } = "Run visible again.";
        public static string DisableQuestionFormat { get; set; } = "Disable {0}'s account?\n{1}";
        public static string DisableText { get; set; } = "Disable";
        public static string EnableQuestionFormat { get; set; } = "Enable {0}'s account?\nDisabled: {1}\n{2}";
        public static string EnableText { get; set; } = "Enable";
        public static string DisabledFormat { get; set; } = "{0}'s account disabled.";
        public static string EnabledFormat { get; set; } = "{0}'s account enabled.";
        public static string AccountStatsFormat { get; set; } = "{0} | {1} hidden | {2}";
        public static string RunsFormat { get; set; } = "{0} runs";
        public static string OneRunText { get; set; } = "1 run";
        public static string CheatKicksFormat { get; set; } = "{0} cheat kicks";
        public static string OneCheatKickText { get; set; } = "1 cheat kick";
        public static string LoadingAccountText { get; set; } = "Loading";
        /// <summary>Hint in the reason field; hiding reasons go to the log, disabling reasons to the player.</summary>
        public static string HideReasonHint { get; set; } = "Reason (moderation log)";
        public static string DisableReasonHint { get; set; } = "Reason (the player sees it)";

        /// <summary>Hides <paramref name="run"/> (asking a reason) or shows it again (asking first).</summary>
        public static void ToggleHidden(VisualElement from, RunEntry run, Action<string, bool> report, Action done)
        {
            var time = UiText.Clock(run.finishMs / 1000f);
            if (run.hidden)
                ConfirmPrompt.Show(from, string.Format(UnhideQuestionFormat, run.username, time), UnhideText,
                    () => Apply(OnlineModeration.UnhideRun(run.runId), UnhiddenText, report, done).LogExceptionsAndForget());
            else
                TextPrompt.Show(from, string.Format(HideQuestionFormat, run.username, time), HideText,
                    reason => Apply(OnlineModeration.HideRun(run.runId, reason), HiddenText, report, done).LogExceptionsAndForget(),
                    ModAccountInfo.MaxReason, hint: HideReasonHint);
        }

        /// <summary>Fetches the account of <paramref name="accountId"/>, then offers to disable or enable it.</summary>
        public static void ToggleAccount(VisualElement from, long accountId, string username, Action<string, bool> report, Action done) =>
            ToggleAccountAsync(from, accountId, username, report, done).LogExceptionsAndForget();

        static async Awaitable ToggleAccountAsync(VisualElement from, long accountId, string username, Action<string, bool> report, Action done)
        {
            report(LoadingAccountText, true);
            var r = await OnlineModeration.GetAccount(accountId);
            if (from.panel == null) return;
            if (!r.Ok)
            {
                report(r.result.message, false);
                return;
            }
            report("", true);
            Offer(from, r.value, username, report, done);
        }

        /// <summary>Offers to disable or enable the account described by <paramref name="info"/>.</summary>
        public static void Offer(VisualElement from, ModAccountInfo info, string username, Action<string, bool> report, Action done)
        {
            var name = string.IsNullOrEmpty(info.account?.username) ? username : info.account.username;
            var id = (long)info.account.id;
            var stats = string.Format(Invariant, AccountStatsFormat,
                info.runs == 1 ? OneRunText : string.Format(Invariant, RunsFormat, info.runs), info.hiddenRuns,
                info.cheatKicks == 1 ? OneCheatKickText : string.Format(Invariant, CheatKicksFormat, info.cheatKicks));
            if (info.disabled)
                ConfirmPrompt.Show(from, string.Format(EnableQuestionFormat, name, info.disabledReason, stats), EnableText,
                    () => Apply(OnlineModeration.EnableAccount(id), string.Format(EnabledFormat, name), report, done).LogExceptionsAndForget());
            else
                TextPrompt.Show(from, string.Format(DisableQuestionFormat, name, stats), DisableText,
                    reason => Apply(OnlineModeration.DisableAccount(id, reason), string.Format(DisabledFormat, name), report, done).LogExceptionsAndForget(),
                    ModAccountInfo.MaxReason, hint: DisableReasonHint);
        }

        static async Awaitable Apply(Task<OnlineResult> call, string success, Action<string, bool> report, Action done)
        {
            var r = await call;
            report(r.Ok ? success : r.message, r.Ok);
            if (!r.Ok) return;
            HiddenReasons.Clear();
            done?.Invoke();
        }
    }
}
