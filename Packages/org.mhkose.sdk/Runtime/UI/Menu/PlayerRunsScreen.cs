using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MHK.Core;
using MHK.Ghosts;
using MHK.Modding;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// One player's time trial runs (PlayerRuns.uxml), opened from player cards and the Players page: every run the
    /// server lists for the account (<see cref="OnlineProfiles.PlayerRuns"/>, up to <see cref="maxRuns"/>), grouped by
    /// track, each with its class, time, board rank and upload date. A row opens the track's records with the leaderboard
    /// searched for the player (<see cref="TrackRecordsScreen.OpenSearched"/>), Watch downloads and replays it,
    /// moderators also Hide / Unhide (hidden runs are listed for them). The name opens the player card.
    /// </summary>
    public class PlayerRunsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Run row (Templates/LeaderRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Most runs listed (fetched 100 at a time).")]
        int maxRuns = 500;

        [Header("Texts")]
        [SerializeField, Tooltip("Row line ({0} = engine class, {1} = upload date).")]
        string rowFormat = "{0} | {1}";
        [SerializeField, Tooltip("Date format of the rows (.NET custom format).")]
        string dateFormat = "d MMM yyyy";
        [SerializeField, Tooltip("Tooltip of a row: opens the track's records searched for the player.")]
        string openTrackTip = "Open the track's leaderboard";
        [SerializeField, Tooltip("Track group line ({0} = runs on the track).")]
        string trackRunsFormat = "{0} runs", oneTrackRunText = "1 run";
        [SerializeField, Tooltip("States: loading, no runs, offline (the reason is the tooltip).")]
        string stateLoading = "Loading", stateNone = "No runs uploaded yet", stateOffline = "Offline";
        [SerializeField, Tooltip("Note when more runs exist than listed ({0} = listed, {1} = all).")]
        string truncatedFormat = "Showing {0} of {1} runs";
        [SerializeField, Tooltip("Status while a run downloads for watching.")]
        string downloadingText = "Downloading the ghost...";
        [SerializeField, Tooltip("Status when a downloaded ghost can't be read.")]
        string unreadableDownloadText = "The downloaded ghost could not be read.";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        long accountId;
        string username = "";
        int request;
        bool busy;
        VisualElement list, state, stateIcon;
        ScrollView scroll;
        Label stateLabel, status;
        Button retry;
        readonly RankTags ranks = new();
        PlayerCardOptions cardOptions;

        void Reset() => showcaseVisible = false;

        /// <summary>Opens the runs of <paramref name="id"/> (<paramref name="name"/> until the server's answer).</summary>
        public void Open(long id, string name)
        {
            accountId = id;
            username = name ?? "";
            Flow.Show(this);
        }

        protected override void Bind()
        {
            list = Q<VisualElement>("pr-list");
            state = Q<VisualElement>("pr-state");
            stateIcon = Q<VisualElement>("pr-state-icon");
            stateLabel = Q<Label>("pr-state-label");
            scroll = Q<ScrollView>("pr-scroll");
            status = Q<Label>("status");
            SetStatus("", true);
            LeaderRows.KeepFocusedRowInView(scroll, list);
            LeaderRows.NoWrap(Root);
            retry = OnClick("pr-retry", () =>
            {
                OnlineService.Connect();
                Load();
            });
            OnClick("refresh", Load);
            cardOptions = new PlayerCardOptions { report = SetStatus, changed = Load };
            ranks.Attach(Root);
            var name = Q<Label>("pr-name");
            name.text = username;
            ranks.Add(Q<Label>("pr-rank"), accountId);
            PlayerCard.Attach(name, accountId, username, cardOptions);
            Q<Label>("pr-note").SetVisible(false);
            Load();
        }

        protected override void Unbind()
        {
            base.Unbind();
            ranks.Detach();
            PlayerCard.Close();
            cardOptions = null;
            request++;
            busy = false;
            list = state = stateIcon = null;
            scroll = null;
            stateLabel = status = null;
            retry = null;
        }

        void SetStatus(string text, bool good)
        {
            if (status == null) return;
            status.text = text ?? "";
            status.SetVisible(!string.IsNullOrEmpty(text));
        }

        void Load()
        {
            if (list != null) LoadAsync(++request).LogExceptionsAndForget();
        }

        // Pages of 100 until every run (or maxRuns) arrived.
        async Awaitable LoadAsync(int id)
        {
            ShowState("sync", stateLoading, false);
            var runs = new List<RunEntry>();
            var total = 0;
            var most = Mathf.Max(1, maxRuns);
            do
            {
                var r = await OnlineProfiles.PlayerRuns(accountId, runs.Count, Mathf.Min(LeaderboardPage.MaxLimit, most - runs.Count));
                if (id != request || list == null) return;
                if (!r.Ok)
                {
                    if (r.result.status == null) ShowState("cloud_off", stateOffline, true, r.result.message);
                    else ShowState("error", r.result.message, true);
                    return;
                }
                total = r.value.total;
                runs.AddRange(r.value.runs);
                if (r.value.runs.Count == 0) break;
            } while (runs.Count < total && runs.Count < most);
            if (runs.Count > 0 && runs[0].username.Length > 0)
            {
                username = runs[0].username;
                Q<Label>("pr-name").text = username;
            }
            Fill(runs, total);
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
        }

        void Fill(List<RunEntry> runs, int total)
        {
            list.Clear();
            var note = Q<Label>("pr-note");
            note.text = string.Format(Invariant, truncatedFormat, runs.Count, total);
            note.SetVisible(total > runs.Count);
            if (runs.Count == 0)
            {
                ShowState("timer", stateNone, false);
                return;
            }
            state.SetVisible(false);
            scroll.SetVisible(true);
            var mod = OnlineService.IsModerator;
            list.EnableInClassList("lb-list-mod", mod);
            // By track name, then class; the board's ranked run first, then by time.
            var groups = runs.GroupBy(r => r.trackId.ToLowerInvariant()).OrderBy(g => ContentDisplay.TrackName(g.First().trackId));
            Button first = null;
            foreach (var g in groups)
            {
                var sorted = g.OrderBy(r => r.engineClass).ThenBy(r => r.rank > 0 ? 0 : 1).ThenBy(r => r.finishMs).ToList();
                var head = GroupHead(sorted[0].trackId, sorted.Count);
                head.EnableInClassList("first", list.childCount == 0);
                list.Add(head);
                for (var i = 0; i < sorted.Count; i++)
                {
                    var row = SpawnRow(sorted[i], i % 2 == 1);
                    first ??= row?.Q<Button>("lb-main");
                }
            }
            FocusLater(first);
        }

        VisualElement GroupHead(string trackId, int count)
        {
            var head = new VisualElement();
            head.AddToClassList("pr-group");
            var name = new Label(ContentDisplay.TrackName(trackId));
            name.AddToClassList("pr-group-name");
            head.Add(name);
            var game = new Label(ContentDisplay.TrackGame(trackId));
            game.AddToClassList("pr-group-game");
            head.Add(game);
            var runs = new Label(count == 1 ? oneTrackRunText : string.Format(Invariant, trackRunsFormat, count));
            runs.AddToClassList("pr-group-count");
            head.Add(runs);
            return head;
        }

        VisualElement SpawnRow(RunEntry run, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
            if (row == null) return null;
            var uploaded = run.uploaded == default ? "-" : run.uploaded.ToLocalTime().ToString(dateFormat, Invariant);
            LeaderRows.Fill(row, run, alt, string.Format(Invariant, rowFormat, ClassLabel(run.engineClass), uploaded));
            row.AddToClassList("pr-row");
            // The player is this page's: no card or account action per row.
            row.Q("lb-profile").SetVisible(false);
            row.Q("lb-account").SetVisible(false);
            var main = row.Q<Button>("lb-main");
            main.tooltip = openTrackTip;
            main.clicked += () =>
            {
                Click();
                Flow.Get<TrackRecordsScreen>()?.OpenSearched(run.trackId, ClassOf(run.engineClass), username);
            };
            var watch = row.Q<Button>("lb-watch");
            watch.SetEnabled(Content.Get<TrackDefinition>(run.trackId) != null);
            if (!watch.enabledSelf) watch.tooltip = ContentDisplay.MissingTrackText;
            watch.clicked += () => Watch(run);
            var hide = row.Q<Button>("lb-hide");
            hide.clicked += () =>
            {
                Click();
                ModerationPrompts.ToggleHidden(hide, run, SetStatus, Load);
            };
            return row;
        }

        static string ClassLabel(int engineClass)
        {
            var classes = GameRoot.Instance.Config.difficulties;
            return engineClass >= 0 && engineClass < classes.Length ? classes[engineClass].label : engineClass.ToString(Invariant);
        }

        static Difficulty ClassOf(int engineClass) =>
            (Difficulty)Mathf.Clamp(engineClass, 0, Mathf.Max(0, GameRoot.Instance.Config.difficulties.Length - 1));

        void Watch(RunEntry run)
        {
            if (busy || SceneFlow.Busy || Content.Get<TrackDefinition>(run.trackId) == null) return;
            Click();
            WatchAsync(run).LogExceptionsAndForget();
        }

        async Awaitable WatchAsync(RunEntry run)
        {
            busy = true;
            SetStatus(downloadingText, true);
            var r = await OnlineTimeTrials.Download(run);
            busy = false;
            if (list == null) return;
            if (!r.Ok)
            {
                SetStatus(r.result.message, false);
                return;
            }
            var entry = GhostStores.Current.Find(r.value.ghostId);
            if (entry == null)
            {
                SetStatus(unreadableDownloadText, false);
                return;
            }
            SetStatus("", true);
            if (!SceneFlow.Busy) SceneFlow.StartSession(TimeTrialRun.ReplaySession(entry));
        }
    }
}
