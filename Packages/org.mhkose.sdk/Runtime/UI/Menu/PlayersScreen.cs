using System.Collections.Generic;
using System.Globalization;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The players page (Players.uxml), opened from the Online page, the Time Trials hub and the Account page. Ranking:
    /// the score ranking (<see cref="OnlineProfiles.ScoreLeaderboard"/>) with the player's own row pinned; All players:
    /// the listed accounts (<see cref="OnlineProfiles.ListPlayers"/>) newest or A-Z with their rank next to the name.
    /// Both search by name and page <see cref="pageSize"/> rows at a time. A row (or its name) opens the player card, its
    /// runs button the player's time trial runs (<see cref="PlayerRunsScreen"/>). Also holds the texts of every player
    /// card (<see cref="CardTexts"/>).
    /// </summary>
    public class PlayersScreen : MenuScreen
    {
        [SerializeField, Tooltip("Tab and sort chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Player row (Templates/PlayerRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Rows per page (1-100).")]
        int pageSize = 25;

        [Header("Texts")]
        [SerializeField, Tooltip("Tabs: the score ranking, every listed player.")]
        string rankingTab = "Ranking", allTab = "All players";
        [SerializeField, Tooltip("Sort chips of All players: newest registration first, by name.")]
        string newestSort = "Newest", nameSort = "A-Z";
        [SerializeField, Tooltip("Placeholder of the player search.")]
        string searchHint = "Player";
        [SerializeField, Tooltip("States: loading, nobody ranked yet, no players, no matches, offline (the reason is the tooltip).")]
        string stateLoading = "Loading", stateNoRanked = "Nobody ranked yet", stateNoPlayers = "No players yet", stateNoMatch = "No matches",
            stateOffline = "Offline";
        [SerializeField, Tooltip("Pager text ({0} = first row, {1} = last row, {2} = all rows).")]
        string pageRangeFormat = "{0}-{1} / {2}";
        [SerializeField, Tooltip("Rank of a row without one.")]
        string noRankText = "-";
        [SerializeField, Tooltip("Row line: registration ({0} = date), and while the player is online.")]
        string registeredFormat = "Registered {0}", onlineNowText = "Online now";
        [SerializeField, Tooltip("Score column ({0} = points) and its tooltip.")]
        string scoreFormat = "{0}", scoreTip = "Points from online races";
        [SerializeField, Tooltip("Date format of the rows (.NET custom format).")]
        string dateFormat = "d MMM yyyy";
        [SerializeField, Tooltip("Texts of the player cards on every screen.")]
        PlayerCardTexts cardTexts = new();

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly string[] MedalClasses = { "medal-1", "medal-2", "medal-3" };

        // Kept while the menu lives: the page reopens as it was left.
        bool ranking = true;
        PlayerSort sort = PlayerSort.Newest;
        string search = "";
        int offset, request;
        bool focusMine, shownLoggedIn;

        VisualElement list, mine, state, stateIcon, pager, sortChips, page;
        ScrollView scroll;
        Label stateLabel, range;
        Button retry, prev, next;
        TextField searchField;
        IVisualElementScheduledItem searchApply;
        PlayerCardOptions cardOptions;

        /// <summary>The texts of every player card (<see cref="PlayerCard.Texts"/>).</summary>
        public PlayerCardTexts CardTexts => cardTexts;

        int Size => Mathf.Clamp(pageSize, 1, PlayerSummary.MaxList);

        void Reset() => showcaseVisible = false;

        /// <summary>Opens the page on the ranking (<paramref name="showRanking"/>) or on every player.</summary>
        public void Open(bool showRanking)
        {
            if (ranking != showRanking) offset = 0;
            ranking = showRanking;
            Flow.Show(this);
        }

        /// <summary>The Account page's Your profile: the ranking without a search, the focus (and so the card) on the own row.</summary>
        public void OpenMine()
        {
            ranking = true;
            search = "";
            offset = 0;
            focusMine = true;
            Flow.Show(this);
        }

        protected override void Bind()
        {
            page = Q<VisualElement>("page");
            list = Q<VisualElement>("pl-list");
            mine = Q<VisualElement>("pl-mine");
            state = Q<VisualElement>("pl-state");
            stateIcon = Q<VisualElement>("pl-state-icon");
            stateLabel = Q<Label>("pl-state-label");
            pager = Q<VisualElement>("pl-pager");
            range = Q<Label>("pl-range");
            scroll = Q<ScrollView>("pl-scroll");
            sortChips = Q<VisualElement>("pl-sort");
            cardOptions = new PlayerCardOptions { runsFlow = Flow, report = SetStatus, changed = Load };
            SetStatus("", true);
            LeaderRows.KeepFocusedRowInView(scroll, list);
            LeaderRows.NoWrap(Root);
            LeaderRows.UpFromTopTo(Q<VisualElement>("pl-panel"), () => LeaderRows.SelectedChip(Q<VisualElement>("pl-tabs")));
            LeaderRows.DownFromTo(Q<VisualElement>("pl-toolbar"), FirstRow);
            retry = OnClick("pl-retry", () =>
            {
                OnlineService.Connect();
                Load();
            });
            prev = OnClick("pl-prev", () => Page(-1));
            next = OnClick("pl-next", () => Page(1));
            OnClick("refresh", Load);
            ChipGroup.Fill(Q<VisualElement>("pl-tabs"), chipTemplate, new[] { rankingTab, allTab }, ranking ? 0 : 1, index =>
            {
                Click();
                if (ranking == (index == 0)) return;
                ranking = index == 0;
                offset = 0;
                ShowMode();
                Load();
            });
            ChipGroup.Fill(sortChips, chipTemplate, new[] { newestSort, nameSort }, (int)sort, index =>
            {
                Click();
                if ((int)sort == index) return;
                sort = (PlayerSort)index;
                offset = 0;
                Load();
            });
            BindSearch();
            shownLoggedIn = OnlineService.IsLoggedIn;
            OnlineService.Changed += OnOnlineChanged;
            ShowMode();
            Load();
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineService.Changed -= OnOnlineChanged;
            PlayerCard.Close();
            searchApply?.Pause();
            searchApply = null;
            request++;
            list = mine = state = stateIcon = pager = sortChips = page = null;
            scroll = null;
            stateLabel = range = null;
            retry = prev = next = null;
            searchField = null;
            cardOptions = null;
        }

        void BindSearch()
        {
            searchField = Q<TextField>("pl-search");
            searchField.SetValueWithoutNotify(search);
            searchField.textEdition.placeholder = searchHint;
            searchApply = Root.schedule.Execute(ApplySearch);
            searchApply.Pause();
            searchField.RegisterValueChangedCallback(_ => searchApply?.ExecuteLater(400));
            searchField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                searchApply?.Pause();
                ApplySearch();
            }, TrickleDown.TrickleDown);
        }

        void ApplySearch()
        {
            if (searchField == null) return;
            var text = searchField.value.Trim();
            if (text == search) return;
            search = text;
            offset = 0;
            Load();
        }

        void ShowMode()
        {
            sortChips.SetVisible(!ranking);
            page.EnableInClassList("pl-ranking", ranking);
            page.EnableInClassList("pl-all", !ranking);
        }

        // Login and logout change the pinned own row.
        void OnOnlineChanged()
        {
            if (list == null || OnlineService.IsLoggedIn == shownLoggedIn) return;
            shownLoggedIn = OnlineService.IsLoggedIn;
            if (ranking) Load();
        }

        void SetStatus(string text, bool good)
        {
            var status = Q<Label>("status");
            status.text = text ?? "";
            status.SetVisible(!string.IsNullOrEmpty(text));
        }

        void Page(int direction)
        {
            offset = Mathf.Max(0, offset + direction * Size);
            Load();
        }

        void Load()
        {
            if (list != null) LoadAsync(++request).LogExceptionsAndForget();
        }

        async Awaitable LoadAsync(int id)
        {
            ShowState("sync", stateLoading, false);
            var r = ranking ? await OnlineProfiles.ScoreLeaderboard(offset, Size, search) : await OnlineProfiles.ListPlayers(offset, Size, search, sort);
            if (id != request || list == null) return;
            if (!r.Ok)
            {
                if (r.result.status == null) ShowState("cloud_off", stateOffline, true, r.result.message);
                else ShowState("error", r.result.message, true);
                return;
            }
            var p = r.value;
            // A page that emptied meanwhile steps back to the last one.
            if (p.players.Count == 0 && offset > 0 && p.total > 0)
            {
                offset = Mathf.Max(0, (p.total - 1) / Size * Size);
                Load();
                return;
            }
            Fill(p);
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
            mine.SetVisible(false);
            pager.SetVisible(false);
        }

        void Fill(PlayerPage p)
        {
            list.Clear();
            mine.Clear();
            if (p.players.Count == 0)
            {
                ShowState(search.Length > 0 ? "search" : "group", search.Length > 0 ? stateNoMatch : ranking ? stateNoRanked : stateNoPlayers, false);
                return;
            }
            state.SetVisible(false);
            scroll.SetVisible(true);
            Button own = null;
            var me = OnlineService.Account;
            for (var i = 0; i < p.players.Count; i++)
            {
                var row = SpawnRow(list, p.players[i], i % 2 == 1);
                if (row != null && me != null && p.players[i].accountId == (long)me.id) own = row.Q<Button>("pl-main");
            }
            var pinned = ranking && p.mine != null && !p.players.Exists(x => x.accountId == p.mine.accountId);
            if (pinned) own = SpawnRow(mine, p.mine, false)?.Q<Button>("pl-main");
            mine.SetVisible(pinned);
            pager.SetVisible(p.total > p.players.Count || offset > 0);
            range.text = string.Format(Invariant, pageRangeFormat, offset + 1, offset + p.players.Count, p.total);
            prev.SetEnabled(offset > 0);
            next.SetEnabled(offset + p.players.Count < p.total);
            scroll.scrollOffset = Vector2.zero;
            if (focusMine) FocusLater(own);
            focusMine = false;
        }

        VisualElement SpawnRow(VisualElement parent, PlayerSummary s, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(rowTemplate, parent);
            if (row == null) return null;
            var me = OnlineService.Account;
            row.EnableInClassList("alt", alt);
            row.EnableInClassList("mine", me != null && (long)me.id == s.accountId);
            var rank = row.Q<Label>("pl-rank");
            rank.text = s.rank > 0 ? s.rank.ToString(Invariant) : noRankText;
            rank.SetExclusiveClass(s.rank - 1, MedalClasses);
            var name = row.Q<Label>("pl-name");
            name.text = s.username;
            var tag = row.Q<Label>("pl-player-rank");
            tag.SetVisible(!ranking && s.rank > 0);
            tag.text = string.Format(Invariant, cardTexts.rankFormat, s.rank);
            tag.tooltip = string.Format(Invariant, cardTexts.rankTipFormat, s.rank, s.score);
            var country = row.Q<Label>("pl-country");
            country.SetVisible(!string.IsNullOrEmpty(s.country));
            country.text = string.IsNullOrEmpty(s.country) ? "" : s.country.ToUpperInvariant();
            country.tooltip = Countries.Name(s.country);
            var mod = row.Q("pl-mod");
            mod.SetVisible(s.IsModerator);
            mod.tooltip = cardTexts.moderatorText;
            row.Q("pl-online").SetVisible(s.online);
            row.Q("pl-online").tooltip = onlineNowText;
            row.SetText("pl-sub", s.online ? onlineNowText : string.Format(registeredFormat, s.created == default ? "-" : s.created.ToLocalTime().ToString(dateFormat, Invariant)));
            var score = row.Q<Label>("pl-score");
            score.text = string.Format(Invariant, scoreFormat, s.score);
            score.tooltip = scoreTip;
            PlayerCard.Attach(name, s.accountId, s.username, cardOptions);
            PlayerCard.Attach(row.Q<Button>("pl-main"), name, s.accountId, s.username, cardOptions);
            var runs = row.Q<Button>("pl-runs");
            runs.tooltip = cardTexts.runsText;
            runs.clicked += () =>
            {
                Click();
                Flow.Get<PlayerRunsScreen>()?.Open(s.accountId, s.username);
            };
            return row;
        }

        VisualElement FirstRow()
        {
            var first = mine.resolvedStyle.display != DisplayStyle.None ? mine.Q<Button>("pl-main") : null;
            return first ?? (scroll.resolvedStyle.display != DisplayStyle.None ? list.Q<Button>("pl-main") : null);
        }
    }
}
