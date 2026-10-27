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
    /// and Disable / Enable (<see cref="ModerationPrompts"/>). The names carry rank tags and player cards. Leaving
    /// moderator rights (logout) goes back.
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
        [SerializeField, Tooltip("Row line ({0} = track, {1} = engine class).")]
        string reportRowFormat = "{0} | {1}";
        [SerializeField, Tooltip("States: loading, nothing reported, not a moderator, offline (the reason is the tooltip).")]
        string stateLoading = "Loading", stateNone = "All quiet", stateNotModerator = "Moderators only", stateOffline = "Offline";
        [SerializeField, Tooltip("Pager text ({0} = first row, {1} = last row, {2} = all rows).")]
        string pageRangeFormat = "{0}-{1} / {2}";
        [SerializeField, Tooltip("Tooltips of the run chips: track, class, upload date, flags.")]
        string trackTip = "Track", classTip = "Engine class", uploadedTip = "Uploaded", flagsTip = "Players who reported it";
        [SerializeField, Tooltip("Report chips: tooltip of the ghost time, of the reporter, the mark of an ignored report.")]
        string ghostTimeTip = "Ghost time of the finding", reporterTip = "Reported by", ignoredTip = "Ignored: the reporter is not trusted";
        [SerializeField, Tooltip("State of a run without reports.")]
        string noReports = "None";
        [SerializeField, Tooltip("Account chip tooltips: country, joined, last login, runs, hidden runs, cheat kicks, disabled.")]
        string countryTip = "Country", joinedTip = "Joined", lastLoginTip = "Last login", runsTip = "Runs", hiddenRunsTip = "Hidden runs",
            cheatKicksTip = "Kicked from races for cheating", disabledTip = "Disabled: {0}";
        [SerializeField, Tooltip("Buttons of the detail: hide / unhide, disable / enable, the disabled chip.")]
        string hideLabel = "Hide", unhideLabel = "Unhide", disableLabel = "Disable", enableLabel = "Enable", disabledLabel = "Disabled";

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
        readonly RankTags ranks = new();
        PlayerCardOptions cardOptions;

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
            LeaderRows.NoWrap(Root);
            LeaderRows.NoScrollbarFocus(Q<ScrollView>("fr-detail"));
            LeaderRows.UpFromTopTo(scroll, () => LeaderRows.SelectedChip(Q<VisualElement>("filter")));
            LeaderRows.DownFromTo(Q<VisualElement>("filter"), () => list.Q<Button>("lb-main"));
            // Right from a row's last button opens the details' actions, Left from the first action returns to the row.
            list.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.target is Button { name: "lb-account" } && detail.resolvedStyle.display != DisplayStyle.None)
                    MoveFocus(e, NavigationMoveEvent.Direction.Right, Q<Button>("fr-watch"));
            });
            Q<Button>("fr-watch").RegisterCallback<NavigationMoveEvent>(e =>
                MoveFocus(e, NavigationMoveEvent.Direction.Left, rows.Find(r => shown != null && r.run.runId == shown.runId).row?.Q<Button>("lb-main")));
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
            cardOptions = new PlayerCardOptions { runsFlow = Flow, report = SetStatus, changed = AfterChange };
            ranks.Attach(Root);
            focusFirst = true;
            ShowDetail(shown);
            Load();
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineService.Changed -= OnOnlineChanged;
            ranks.Detach();
            PlayerCard.Close();
            cardOptions = null;
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

        static void MoveFocus(NavigationMoveEvent e, NavigationMoveEvent.Direction direction, VisualElement to)
        {
            if (e.direction != direction || to == null || to.panel == null || e.target is not VisualElement from) return;
            to.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
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
            ShowState("sync", stateLoading, false);
            var r = await OnlineModeration.ListFlagged(offset, Size, includeHidden);
            if (id != request || list == null) return;
            if (!r.Ok)
            {
                if (r.result.status == null) ShowState("cloud_off", stateOffline, true, r.result.message);
                else ShowState("error", r.result.status == Status.Forbidden ? stateNotModerator : r.result.message, true);
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

        void ShowState(string icon, string text, bool showRetry, string tip = "")
        {
            if (state == null) return;
            state.SetVisible(true);
            stateIcon.SetIcon(icon);
            stateLabel.text = text;
            stateLabel.tooltip = tip ?? "";
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
                ShowState("check_circle", stateNone, false);
                return;
            }
            state.SetVisible(false);
            scroll.SetVisible(true);
            for (var i = 0; i < runs.Count; i++) SpawnRow(runs[i], i % 2 == 1);
            pager.SetVisible(total > runs.Count || offset > 0);
            range.text = string.Format(Invariant, pageRangeFormat, offset + 1, offset + runs.Count, total);
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
            LeaderRows.Fill(row, run, alt, string.Format(reportRowFormat, ContentDisplay.TrackName(run.trackId), ClassLabel(run.engineClass)));
            rows.Add((run, row));
            LeaderRows.AttachPlayer(row, run, ranks, cardOptions);
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
            Q<Label>("fr-run-title").text = run.username;
            Q<Label>("fr-run-time").text = UiText.Clock(run.finishMs / 1000f);
            var chips = Q<VisualElement>("fr-run-chips");
            chips.Clear();
            InfoChips.Add(chips, "map", ContentDisplay.TrackName(run.trackId), trackTip);
            InfoChips.Add(chips, "speed", ClassLabel(run.engineClass), classTip);
            InfoChips.Add(chips, "upload", Date(run.uploaded), uploadedTip);
            InfoChips.Add(chips, "flag", run.flags.ToString(Invariant), flagsTip, run.flags > 0 ? InfoChipState.Bad : InfoChipState.Normal);
            if (run.hidden) HiddenReasons.Attach(InfoChips.Add(chips, "visibility_off", null, LeaderRows.HiddenTip, InfoChipState.Off), run.runId, LeaderRows.HiddenTip);
            Q<Label>("fr-hide-label").text = run.hidden ? unhideLabel : hideLabel;
            Q<VisualElement>("fr-hide-icon").SetIcon(run.hidden ? "visibility" : "visibility_off");
            Q<Button>("fr-watch").SetEnabled(Content.Get<TrackDefinition>(run.trackId) != null);
            reports.Clear();
            SetPart("fr-reports-state", stateLoading);
            SetPart("fr-account-state", stateLoading);
            Q<VisualElement>("fr-account").Clear();
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
            SetPart("fr-reports-state", r.value.Count == 0 ? noReports : "");
            foreach (var report in r.value)
            {
                var item = new VisualElement();
                item.AddToClassList("fr-report");
                item.EnableInClassList("ignored", !report.counted);
                var head = new VisualElement();
                head.AddToClassList(InfoChips.ContainerClass);
                InfoChips.Add(head, "flag", report.reason.ToString(), null, InfoChipState.Bad);
                InfoChips.Add(head, "timer", UiText.Clock(report.atSeconds), ghostTimeTip);
                InfoChips.Add(head, "person", report.reporter, reporterTip);
                InfoChips.Add(head, "history", Date(report.created));
                if (!report.counted) InfoChips.Add(head, "remove_moderator", null, ignoredTip, InfoChipState.Off);
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
            var account = Q<VisualElement>("fr-account");
            account.Clear();
            if (info.disabled) InfoChips.Add(account, "gavel", disabledLabel, string.Format(disabledTip, info.disabledReason), InfoChipState.Bad);
            var code = info.account?.country;
            InfoChips.Add(account, "public", string.IsNullOrEmpty(code) ? "--" : code.ToUpperInvariant(), string.IsNullOrEmpty(code) ? countryTip : Countries.Name(code));
            InfoChips.Add(account, "person_add", Date(info.created), joinedTip);
            InfoChips.Add(account, "login", Date(info.lastLogin), lastLoginTip);
            InfoChips.Add(account, "inventory_2", info.runs.ToString(Invariant), runsTip);
            InfoChips.Add(account, "visibility_off", info.hiddenRuns.ToString(Invariant), hiddenRunsTip, info.hiddenRuns > 0 ? InfoChipState.Normal : InfoChipState.Off);
            InfoChips.Add(account, "gavel", info.cheatKicks.ToString(Invariant), cheatKicksTip, info.cheatKicks > 0 ? InfoChipState.Bad : InfoChipState.Off);
            Q<Label>("fr-disable-label").text = info.disabled ? enableLabel : disableLabel;
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
            SetStatus(stateLoading, true);
            var r = await OnlineTimeTrials.Download(run);
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
