using System.Collections.Generic;
using System.Globalization;
using MHK.Core;
using MHK.Ghosts;
using MHK.Modding;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Moderators' Reports page (FlaggedRuns.uxml), opened from the Records pages: runs other players' games reported
    /// while watching them, most flags first, a page of <see cref="pageSize"/> at a time, optionally with the hidden ones.
    /// The focused or clicked run shows its reports and its uploader's account on the side, with Watch, Hide / Unhide
    /// and Disable / Enable (<see cref="ModerationPrompts"/>). Leaving moderator rights (logout) goes back.
    /// </summary>
    public class FlaggedRunsScreen : MenuScreen
    {
        const long DetailDelayMs = 250;

        [SerializeField, Tooltip("Filter chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Run row (Templates/LeaderRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Runs per page (1-100).")]
        int pageSize = 25;

        [Header("Texts")]
        [SerializeField, Tooltip("Filter chips: visible runs only, also hidden ones.")]
        string visibleTab = "Visible", hiddenTab = "With hidden";
        [SerializeField, Tooltip("Row line ({0} = track, {1} = engine class, {2} = driver, {3} = kart).")]
        string rowSubFormat = "{0} | {1} | {2} | {3}";
        [SerializeField, Tooltip("States: loading, nothing reported, not a moderator, offline ({0} = reason).")]
        string loadingText = "Loading...", noneText = "No reported runs. All quiet.", notModeratorText = "Only moderators see reports.",
            offlineFormat = "The server can't be reached. {0}";
        [SerializeField, Tooltip("Pager text ({0} = first row, {1} = last row, {2} = all rows).")]
        string rangeFormat = "{0}-{1} of {2}";
        [SerializeField, Tooltip("Detail title ({0} = player, {1} = time) and line ({0} = track, {1} = class, {2} = upload date, {3} = flags).")]
        string runTitleFormat = "{0} | {1}", runSubFormat = "{0} | {1} | uploaded {2} | {3} flag(s)";
        [SerializeField, Tooltip("One report's head ({0} = reason, {1} = ghost time, {2} = reporter, {3} = date) and the mark of an ignored one.")]
        string reportHeadFormat = "{0} at {1} | {2} | {3}", ignoredSuffix = " (ignored: reporter not trusted)";
        [SerializeField, Tooltip("States of the reports and account parts.")]
        string noReportsText = "No reports.";
        [SerializeField, Tooltip("Account lines ({0} = country, {1} = joined, {2} = last login) and ({0} = runs, {1} = hidden, {2} = cheat kicks), disabled ({0} = reason).")]
        string accountLineFormat = "{0} | joined {1} | last login {2}", accountStatsFormat = "{0} runs ({1} hidden) | {2} cheat kicks",
            disabledFormat = "Disabled: {0}";
        [SerializeField, Tooltip("Buttons of the detail: hide / unhide, disable / enable.")]
        string hideText = "Hide run", unhideText = "Unhide run", disableText = "Disable account", enableText = "Enable account";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        bool includeHidden, focusFirst;
        int offset, request, detailRequest;
        RunEntry shown;
        ModAccountInfo shownAccount;
        VisualElement list, state, pager, detail, detailEmpty, reports, stateIcon;
        ScrollView scroll;
        Label stateLabel, range, status;
        Button retry, prev, next, hide, disable;
        IVisualElementScheduledItem detailLater;
        RunEntry pendingDetail;
        readonly List<(RunEntry run, VisualElement row)> rows = new();

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            list = Q<VisualElement>("fr-list");
            state = Q<VisualElement>("fr-state");
            stateIcon = Q<VisualElement>("fr-state-icon");
            stateLabel = Q<Label>("fr-state-label");
            pager = Q<VisualElement>("fr-pager");
            range = Q<Label>("fr-range");
            scroll = Q<ScrollView>("fr-scroll");
            detail = Q<VisualElement>("fr-detail");
            detailEmpty = Q<VisualElement>("fr-detail-empty");
            reports = Q<VisualElement>("fr-reports");
            status = Q<Label>("status");
            LeaderRows.KeepFocusedRowInView(scroll, list);
            SetStatus("", true);
            retry = OnClick("fr-retry", () =>
            {
                OnlineService.Connect();
                Load();
            });
            prev = OnClick("fr-prev", () => Page(-1));
            next = OnClick("fr-next", () => Page(1));
            OnClick("refresh", Load);
            OnClick("fr-watch", () => Watch(shown));
            hide = OnClick("fr-hide", () =>
            {
                if (shown != null) ModerationPrompts.ToggleHidden(hide, shown, SetStatus, AfterChange);
            });
            disable = OnClick("fr-disable", () =>
            {
                if (shownAccount != null) ModerationPrompts.Offer(disable, shownAccount, shown?.username, SetStatus, AfterChange);
            });
            ChipGroup.Fill(Q<VisualElement>("filter"), chipTemplate, new[] { visibleTab, hiddenTab }, includeHidden ? 1 : 0, index =>
            {
                Click();
                includeHidden = index == 1;
                offset = 0;
                Load();
            });
            detailLater = Root.schedule.Execute(() => ShowDetail(pendingDetail));
            detailLater.Pause();
            OnlineService.Changed += OnOnlineChanged;
            focusFirst = true;
            ShowDetail(shown);
            Load();
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineService.Changed -= OnOnlineChanged;
            detailLater?.Pause();
            detailLater = null;
            request++;
            detailRequest++;
            rows.Clear();
            list = state = pager = detail = detailEmpty = reports = stateIcon = null;
            scroll = null;
            stateLabel = range = status = null;
            retry = prev = next = hide = disable = null;
        }

        void OnOnlineChanged()
        {
            if (list != null && OnlineService.State == OnlineState.LoggedIn && !OnlineService.IsModerator) Flow.Back();
        }

        void SetStatus(string text, bool good)
        {
            if (status == null) return;
            status.text = text ?? "";
            status.SetVisible(!string.IsNullOrEmpty(text));
        }

        void AfterChange()
        {
            Load();
            if (shown != null) ShowDetail(shown);
        }

        void Page(int direction)
        {
            offset = Mathf.Max(0, offset + direction * Size);
            Load();
        }

        int Size => Mathf.Clamp(pageSize, 1, LeaderboardPage.MaxLimit);

        void Load() => LoadAsync(++request).LogExceptionsAndForget();

        async Awaitable LoadAsync(int id)
        {
            ShowState("sync", loadingText, false);
            var r = await OnlineModeration.ListFlagged(offset, Size, includeHidden);
            if (id != request || list == null) return;
            if (!r.Ok)
            {
                if (r.result.status == null) ShowState("cloud_off", string.Format(offlineFormat, r.result.message), true);
                else ShowState("error", r.result.status == Status.Forbidden ? notModeratorText : r.result.message, true);
                return;
            }
            var (total, runs) = r.value;
            if (runs.Count == 0 && offset > 0 && total > 0)
            {
                offset = Mathf.Max(0, (total - 1) / Size * Size);
                Load();
                return;
            }
            Fill(total, runs);
        }

        void ShowState(string icon, string text, bool showRetry)
        {
            if (state == null) return;
            state.SetVisible(true);
            stateIcon.SetIcon(icon);
            stateLabel.text = text;
            retry.SetVisible(showRetry);
            scroll.SetVisible(false);
            pager.SetVisible(false);
        }

        void Fill(int total, List<RunEntry> runs)
        {
            list.Clear();
            rows.Clear();
            if (runs.Count == 0)
            {
                ShowState("check", noneText, false);
                return;
            }
            state.SetVisible(false);
            scroll.SetVisible(true);
            for (var i = 0; i < runs.Count; i++) SpawnRow(runs[i], i % 2 == 1);
            pager.SetVisible(total > runs.Count || offset > 0);
            range.text = string.Format(Invariant, rangeFormat, offset + 1, offset + runs.Count, total);
            prev.SetEnabled(offset > 0);
            next.SetEnabled(offset + runs.Count < total);
            // The shown run's row follows its new state (hidden, flags) after a refresh.
            if (shown != null)
            {
                var again = runs.Find(r => r.runId == shown.runId);
                if (again != null) shown = again;
                MarkShown();
            }
            // The first rows that arrive take the focus once; later refreshes leave it where the moderator is.
            if (focusFirst) FocusLater(rows[0].row.Q<Button>("lb-main"));
            focusFirst = false;
        }

        void SpawnRow(RunEntry run, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
            if (row == null) return;
            LeaderRows.Fill(row, run, alt, string.Format(rowSubFormat, ContentDisplay.TrackName(run.trackId), ClassLabel(run.engineClass),
                ContentDisplay.CharacterName(run.characterId), ContentDisplay.KartName(run.kartId)));
            rows.Add((run, row));
            var main = row.Q<Button>("lb-main");
            main.clicked += () =>
            {
                Click();
                detailLater?.Pause();
                ShowDetail(run);
            };
            // Walking the list with a gamepad shows each run after a short pause on it.
            main.RegisterCallback<FocusInEvent>(_ =>
            {
                if (shown != null && shown.runId == run.runId) return;
                pendingDetail = run;
                detailLater?.ExecuteLater(DetailDelayMs);
            });
            row.Q<Button>("lb-watch").clicked += () => Watch(run);
            var hideButton = row.Q<Button>("lb-hide");
            hideButton.clicked += () =>
            {
                Click();
                ModerationPrompts.ToggleHidden(hideButton, run, SetStatus, AfterChange);
            };
            var account = row.Q<Button>("lb-account");
            account.clicked += () =>
            {
                Click();
                ModerationPrompts.ToggleAccount(account, run.accountId, run.username, SetStatus, AfterChange);
            };
        }

        static string ClassLabel(int engineClass)
        {
            var classes = GameRoot.Instance.Config.difficulties;
            return engineClass >= 0 && engineClass < classes.Length ? classes[engineClass].label : engineClass.ToString(Invariant);
        }

        void MarkShown()
        {
            foreach (var (run, row) in rows) row.EnableInClassList("selected", shown != null && run.runId == shown.runId);
        }

        void ShowDetail(RunEntry run)
        {
            if (detail == null) return;
            shown = run;
            MarkShown();
            detail.SetVisible(run != null);
            detailEmpty.SetVisible(run == null);
            if (run == null) return;
            Q<Label>("fr-run-title").text = string.Format(runTitleFormat, run.username, UiText.Clock(run.finishMs / 1000f));
            Q<Label>("fr-run-sub").text = string.Format(Invariant, runSubFormat, ContentDisplay.TrackName(run.trackId), ClassLabel(run.engineClass),
                Date(run.uploaded), run.flags);
            Q<Label>("fr-hide-label").text = run.hidden ? unhideText : hideText;
            Q<VisualElement>("fr-hide-icon").SetIcon(run.hidden ? "visibility" : "visibility_off");
            Q<Button>("fr-watch").SetEnabled(Content.Get<TrackDefinition>(run.trackId) != null);
            reports.Clear();
            SetPart("fr-reports-state", loadingText);
            SetPart("fr-account-state", loadingText);
            Q<VisualElement>("fr-account").SetVisible(false);
            shownAccount = null;
            disable.SetEnabled(false);
            var id = ++detailRequest;
            LoadReportsAsync(run, id).LogExceptionsAndForget();
            LoadAccountAsync(run, id).LogExceptionsAndForget();
        }

        void SetPart(string name, string text)
        {
            var label = Q<Label>(name);
            label.text = text ?? "";
            label.SetVisible(!string.IsNullOrEmpty(text));
        }

        static string Date(System.DateTime utc) => utc == default ? "-" : utc.ToLocalTime().ToString("d MMM yyyy", Invariant);

        async Awaitable LoadReportsAsync(RunEntry run, int id)
        {
            var r = await OnlineModeration.RunReports(run.runId);
            if (id != detailRequest || reports == null) return;
            if (!r.Ok)
            {
                SetPart("fr-reports-state", r.result.message);
                return;
            }
            SetPart("fr-reports-state", r.value.Count == 0 ? noReportsText : "");
            foreach (var report in r.value)
            {
                var item = new VisualElement();
                item.AddToClassList("fr-report");
                item.EnableInClassList("ignored", !report.counted);
                var head = new Label(string.Format(reportHeadFormat, report.reason, UiText.Clock(report.atSeconds), report.reporter, Date(report.created)) +
                                     (report.counted ? "" : ignoredSuffix));
                head.AddToClassList("fr-report-head");
                item.Add(head);
                if (!string.IsNullOrEmpty(report.detail))
                {
                    var text = new Label(report.detail);
                    text.AddToClassList("fr-report-detail");
                    item.Add(text);
                }
                reports.Add(item);
            }
        }

        async Awaitable LoadAccountAsync(RunEntry run, int id)
        {
            var r = await OnlineModeration.GetAccount(run.accountId);
            if (id != detailRequest || reports == null) return;
            if (!r.Ok)
            {
                SetPart("fr-account-state", r.result.message);
                return;
            }
            var info = r.value;
            shownAccount = info;
            SetPart("fr-account-state", "");
            Q<VisualElement>("fr-account").SetVisible(true);
            var country = info.account != null && !string.IsNullOrEmpty(info.account.country) ? Countries.Name(info.account.country) : "-";
            Q<Label>("fr-account-line").text = string.Format(accountLineFormat, country, Date(info.created), Date(info.lastLogin));
            Q<Label>("fr-account-stats").text = string.Format(Invariant, accountStatsFormat, info.runs, info.hiddenRuns, info.cheatKicks);
            var disabledLabel = Q<Label>("fr-account-disabled");
            disabledLabel.text = string.Format(disabledFormat, info.disabledReason);
            disabledLabel.SetVisible(info.disabled);
            Q<Label>("fr-disable-label").text = info.disabled ? enableText : disableText;
            disable.SetEnabled(true);
        }

        void Watch(RunEntry run)
        {
            if (run == null || SceneFlow.Busy || Content.Get<TrackDefinition>(run.trackId) == null) return;
            Click();
            WatchAsync(run).LogExceptionsAndForget();
        }

        async Awaitable WatchAsync(RunEntry run)
        {
            SetStatus(loadingText, true);
            var r = await OnlineTimeTrials.Download(run.runId);
            if (list == null) return;
            if (!r.Ok)
            {
                SetStatus(r.result.message, false);
                return;
            }
            var entry = GhostStores.Current.Find(r.value.ghostId);
            SetStatus("", true);
            if (entry != null && !SceneFlow.Busy) SceneFlow.StartSession(TimeTrialRun.ReplaySession(entry));
        }
    }
}
