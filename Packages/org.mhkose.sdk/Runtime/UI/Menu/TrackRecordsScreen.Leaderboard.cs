using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The leaderboard of the track records page: the track's boards in the shown class (the board of this simulation
    /// version and track content first, others Archived), filters, pages of <see cref="pageSize"/> rows with the
    /// player's own row pinned, Watch and picking, and for moderators hidden runs and their actions. Browsing needs no
    /// login; an unreachable server shows inline with Retry, and the board reloads when the connection or login changes.
    /// </summary>
    public partial class TrackRecordsScreen
    {
        [Header("Leaderboard")]
        [SerializeField, Tooltip("Rows per leaderboard page (1-100).")]
        int pageSize = 25;
        [SerializeField, Tooltip("Board names: this version ({0} = players), archived ({0} = players, {1} = simulation version, {2} = last upload).")]
        string currentBoardFormat = "This version | {0} players", archivedBoardFormat = "Archived | sim {1} | {2} | {0} players";
        [SerializeField, Tooltip("Board value while there is none / while loading.")]
        string noBoardText = "No board yet", loadingBoardText = "Loading...";
        [SerializeField, Tooltip("Region filter: everywhere, the player's continent ({0}), the player's country ({0}).")]
        string worldText = "World", myContinentFormat = "My continent: {0}", myCountryFormat = "My country: {0}";
        [SerializeField, Tooltip("Kart and driver filters without a choice.")]
        string anyKartText = "Any kart", anyDriverText = "Any driver";
        [SerializeField, Tooltip("Placeholder of the player search.")]
        string searchPlaceholder = "Search player";
        [SerializeField, Tooltip("Leaderboard states: loading, no runs on any board, no rows matching the filters.")]
        string loadingText = "Loading the leaderboard...", noRunsText = "Nobody has uploaded a run in this class yet.",
            noMatchText = "No runs match the filters.";
        [SerializeField, Tooltip("Leaderboard state without an answer ({0} = reason).")]
        string offlineFormat = "The server can't be reached. {0}";
        [SerializeField, Tooltip("Pager text ({0} = first row, {1} = last row, {2} = all rows).")]
        string rangeFormat = "{0}-{1} of {2}";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly (string code, string name)[] Continents =
        {
            ("AF", "Africa"), ("AN", "Antarctica"), ("AS", "Asia"), ("EU", "Europe"), ("NA", "North America"), ("OC", "Oceania"), ("SA", "South America"),
        };

        // Kept while the page is left for a race and back; reset per track and class.
        List<BoardInfo> boards;
        long boardId;
        int offset;
        // Filters, kept for the whole visit.
        string search = "", scopeContinent = "", scopeCountry = "", kartFilter = "", characterFilter = "";
        int request;
        bool shownLoggedIn, shownModerator, shownOnline, offlineShown;

        VisualElement lbList, lbMine, lbState, lbPager;
        ScrollView lbScroll;
        Label lbStateLabel, lbRange;
        VisualElement lbStateIcon;
        Button lbRetry, lbPrev, lbNext;
        TextField lbSearch;
        SettingChoice boardChoice, scopeChoice, kartChoice, characterChoice;
        IVisualElementScheduledItem searchApply;
        readonly List<(RunEntry run, VisualElement row)> globalRows = new();
        readonly List<(string label, string continent, string country)> scopes = new();
        readonly List<KartDefinition> karts = new();
        readonly List<CharacterDefinition> characters = new();
        readonly List<string> kartNames = new(), characterNames = new();

        void BindLeaderboard()
        {
            lbList = Q<VisualElement>("lb-list");
            lbMine = Q<VisualElement>("lb-mine");
            lbState = Q<VisualElement>("lb-state");
            lbPager = Q<VisualElement>("lb-pager");
            lbScroll = Q<ScrollView>("lb-scroll");
            lbStateLabel = Q<Label>("lb-state-label");
            lbStateIcon = Q<VisualElement>("lb-state-icon");
            lbRange = Q<Label>("lb-range");
            LeaderRows.KeepFocusedRowInView(lbScroll, lbList);
            lbRetry = OnClick("lb-retry", () =>
            {
                OnlineService.Connect();
                Reload();
            });
            lbPrev = OnClick("lb-prev", () => Page(-1));
            lbNext = OnClick("lb-next", () => Page(1));
            BindFilters();
            shownLoggedIn = OnlineService.IsLoggedIn;
            shownModerator = OnlineService.IsModerator;
            shownOnline = OnlineService.State >= OnlineState.Connected;
            SetVisible("reports", shownModerator);
            OnlineService.Changed += OnOnlineChanged;
            Reload();
        }

        void UnbindLeaderboard()
        {
            OnlineService.Changed -= OnOnlineChanged;
            searchApply?.Pause();
            searchApply = null;
            request++;
            globalRows.Clear();
            lbList = lbMine = lbState = lbPager = lbStateIcon = null;
            lbScroll = null;
            lbStateLabel = lbRange = null;
            lbRetry = lbPrev = lbNext = null;
            lbSearch = null;
            boardChoice = scopeChoice = kartChoice = characterChoice = null;
        }

        void ResetLeaderboard()
        {
            boards = null;
            boardId = 0;
            offset = 0;
            request++;
        }

        void BindFilters()
        {
            boardChoice = BindChoice("lb-board", () => Mathf.Max(1, boards?.Count ?? 0), () => Mathf.Max(0, boards?.FindIndex(b => b.boardId == boardId) ?? 0),
                BoardText, i =>
                {
                    if (boards == null || i >= boards.Count) return;
                    boardId = boards[i].boardId;
                    offset = 0;
                    LoadPage();
                });
            BuildScopes();
            scopeChoice = BindChoice("lb-scope", () => scopes.Count, ScopeIndex, i => scopes[i].label, i =>
            {
                scopeContinent = scopes[i].continent;
                scopeCountry = scopes[i].country;
                FilterChanged();
            });
            FillChoices(Content.All<KartDefinition>(), karts, kartNames);
            kartChoice = BindChoice("lb-kart", () => karts.Count + 1, () => karts.FindIndex(k => k.Id == kartFilter) + 1,
                i => i == 0 ? anyKartText : kartNames[i - 1], i =>
                {
                    kartFilter = i == 0 ? "" : karts[i - 1].Id;
                    FilterChanged();
                });
            FillChoices(Content.All<CharacterDefinition>(), characters, characterNames);
            characterChoice = BindChoice("lb-character", () => characters.Count + 1, () => characters.FindIndex(c => c.Id == characterFilter) + 1,
                i => i == 0 ? anyDriverText : characterNames[i - 1], i =>
                {
                    characterFilter = i == 0 ? "" : characters[i - 1].Id;
                    FilterChanged();
                });
            lbSearch = Q<TextField>("lb-search");
            lbSearch.SetValueWithoutNotify(search);
            lbSearch.textEdition.placeholder = searchPlaceholder;
            searchApply = Root.schedule.Execute(ApplySearch);
            searchApply.Pause();
            lbSearch.RegisterValueChangedCallback(_ => searchApply?.ExecuteLater(400));
            lbSearch.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                searchApply?.Pause();
                ApplySearch();
            }, TrickleDown.TrickleDown);
        }

        // Karts and drivers by name; packs reuse names (every game has a "Red Kart"), so those get their game added.
        static void FillChoices<T>(IEnumerable<T> all, List<T> items, List<string> names) where T : ContentDefinition
        {
            items.Clear();
            items.AddRange(all.OrderBy(d => d.DisplayName).ThenBy(d => UiText.GameName(d.SourceGame)));
            names.Clear();
            var counts = items.GroupBy(d => d.DisplayName).ToDictionary(g => g.Key, g => g.Count());
            foreach (var d in items)
                names.Add(counts[d.DisplayName] > 1 ? $"{d.DisplayName} ({UiText.GameName(d.SourceGame)})" : d.DisplayName);
        }

        // World, the player's continent and country (account, else the IP's), then every country.
        void BuildScopes()
        {
            scopes.Clear();
            scopes.Add((worldText, "", ""));
            var mine = OnlineService.Account?.country;
            if (string.IsNullOrEmpty(mine)) mine = OnlineService.IpCountry;
            var continent = Countries.Continent(mine);
            if (continent.Length > 0) scopes.Add((string.Format(myContinentFormat, ContinentName(continent)), continent, ""));
            if (Countries.IsKnown(mine)) scopes.Add((string.Format(myCountryFormat, Countries.Name(mine)), "", mine.ToUpperInvariant()));
            foreach (var (code, name) in Continents) scopes.Add((name, code, ""));
            foreach (var (code, name) in Countries.All) scopes.Add((name, "", code));
        }

        static string ContinentName(string code)
        {
            foreach (var (c, name) in Continents)
                if (c == code) return name;
            return code;
        }

        int ScopeIndex()
        {
            var i = scopes.FindIndex(s => s.continent == scopeContinent && string.Equals(s.country, scopeCountry, StringComparison.OrdinalIgnoreCase));
            return Mathf.Max(0, i);
        }

        void ApplySearch()
        {
            if (lbSearch == null) return;
            var text = lbSearch.value.Trim();
            if (text == search) return;
            search = text;
            FilterChanged();
        }

        void FilterChanged()
        {
            offset = 0;
            LoadPage();
        }

        string BoardText(int i)
        {
            if (boards == null) return offlineShown ? noBoardText : loadingBoardText;
            if (boards.Count == 0) return noBoardText;
            var b = boards[i];
            if (IsCurrentBoard(b)) return string.Format(Invariant, currentBoardFormat, b.players);
            var date = b.lastUpload == default ? "" : b.lastUpload.ToLocalTime().ToString("d MMM yyyy", Invariant);
            return string.Format(Invariant, archivedBoardFormat, b.players, b.simVersion, date);
        }

        // A board of another simulation version or track content is archived; with no race on this track yet the
        // content can't be compared, so only the version decides.
        bool IsCurrentBoard(BoardInfo b)
        {
            if (b.simVersion != SimulationVersion.Current) return false;
            var known = Store.KnownTrackHash(trackId);
            return string.IsNullOrEmpty(known) || string.Equals(b.trackHash, known, StringComparison.OrdinalIgnoreCase);
        }

        BoardInfo DefaultBoard()
        {
            var known = Store.KnownTrackHash(trackId);
            if (!string.IsNullOrEmpty(known))
                foreach (var b in boards)
                    if (b.simVersion == SimulationVersion.Current && string.Equals(b.trackHash, known, StringComparison.OrdinalIgnoreCase)) return b;
            // Most recently active (the server lists the latest upload first).
            return boards[0];
        }

        LeaderboardFilter Filter() => new()
        {
            username = search,
            continent = scopeContinent,
            country = scopeCountry,
            kartId = kartFilter,
            characterId = characterFilter,
            includeHidden = OnlineService.IsModerator,
        };

        void Reload()
        {
            if (boards == null) LoadBoards();
            else LoadPage();
        }

        void LoadBoards() => LoadBoardsAsync(++request).LogExceptionsAndForget();

        async Awaitable LoadBoardsAsync(int id)
        {
            ShowState("sync", loadingText, false);
            boardChoice?.Refresh();
            var r = await OnlineTimeTrials.ListBoards(trackId, (int)engineClass, 0);
            if (id != request || !IsBound) return;
            if (!r.Ok)
            {
                ShowOffline(r.result);
                return;
            }
            boards = r.value ?? new List<BoardInfo>();
            if (boards.Count == 0)
            {
                boardId = 0;
                boardChoice.Refresh();
                boardChoice.Button.SetEnabled(false);
                ShowState("leaderboard", noRunsText, false);
                return;
            }
            if (!boards.Exists(b => b.boardId == boardId)) boardId = DefaultBoard().boardId;
            boardChoice.Refresh();
            boardChoice.Button.SetEnabled(true);
            await LoadPageAsync(id);
        }

        void LoadPage()
        {
            if (boards == null) LoadBoards();
            else LoadPageAsync(++request).LogExceptionsAndForget();
        }

        async Awaitable LoadPageAsync(int id)
        {
            if (boards == null || !boards.Exists(b => b.boardId == boardId)) return;
            ShowState("sync", loadingText, false);
            var size = Mathf.Clamp(pageSize, 1, LeaderboardPage.MaxLimit);
            var r = await OnlineTimeTrials.GetLeaderboard(boardId, offset, size, Filter());
            if (id != request || !IsBound) return;
            if (!r.Ok)
            {
                ShowOffline(r.result);
                return;
            }
            var page = r.value;
            // A page that emptied (rows hidden or filtered away meanwhile) steps back to the last one.
            if (page.entries.Count == 0 && offset > 0 && page.total > 0)
            {
                offset = Mathf.Max(0, (page.total - 1) / size * size);
                LoadPage();
                return;
            }
            FillRows(page, size);
        }

        void Page(int direction)
        {
            var size = Mathf.Clamp(pageSize, 1, LeaderboardPage.MaxLimit);
            offset = Mathf.Max(0, offset + direction * size);
            LoadPage();
        }

        void ShowState(string icon, string text, bool retry)
        {
            if (lbState == null) return;
            offlineShown = retry;
            lbState.SetVisible(true);
            lbStateIcon.SetIcon(icon);
            lbStateLabel.text = text;
            lbRetry.SetVisible(retry);
            lbScroll.SetVisible(false);
            lbMine.SetVisible(false);
            lbPager.SetVisible(false);
        }

        void ShowOffline(OnlineResult result)
        {
            if (result.status == null) ShowState("cloud_off", string.Format(offlineFormat, result.message), true);
            else ShowState("error", result.message, true);
            // Without boards the picker has nothing to offer until Retry.
            if (boards != null || boardChoice == null) return;
            boardChoice.Refresh();
            boardChoice.Button.SetEnabled(false);
        }

        void FillRows(LeaderboardPage page, int size)
        {
            lbList.Clear();
            lbMine.Clear();
            globalRows.Clear();
            var mod = OnlineService.IsModerator;
            lbList.EnableInClassList("lb-list-mod", mod);
            lbMine.EnableInClassList("lb-list-mod", mod);
            Root.Q(className: "lb-head").EnableInClassList("mod", mod);
            if (page.entries.Count == 0)
            {
                var filtered = search.Length > 0 || scopeContinent.Length > 0 || scopeCountry.Length > 0 || kartFilter.Length > 0 || characterFilter.Length > 0;
                ShowState("leaderboard", filtered ? noMatchText : noRunsText, false);
                return;
            }
            offlineShown = false;
            lbState.SetVisible(false);
            lbScroll.SetVisible(true);
            for (var i = 0; i < page.entries.Count; i++) SpawnRunRow(lbList, page.entries[i], i % 2 == 1);
            var mine = page.mine;
            var pinned = mine != null && !page.entries.Exists(e => e.runId == mine.runId);
            if (pinned) SpawnRunRow(lbMine, mine, false);
            lbMine.SetVisible(pinned);
            lbPager.SetVisible(page.total > page.entries.Count || offset > 0);
            lbRange.text = string.Format(Invariant, rangeFormat, offset + 1, offset + page.entries.Count, page.total);
            lbPrev.SetEnabled(offset > 0);
            lbNext.SetEnabled(offset + page.entries.Count < page.total);
            lbScroll.scrollOffset = Vector2.zero;
            ShowLeaderboardPicks();
        }

        void SpawnRunRow(VisualElement parent, RunEntry run, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(leaderRowTemplate, parent);
            if (row == null) return;
            LeaderRows.Fill(row, run, alt);
            globalRows.Add((run, row));
            row.Q<Button>("lb-main").clicked += () =>
            {
                Click();
                TogglePick(run.ghostId, run);
            };
            row.Q<Button>("lb-watch").clicked += () => WatchRun(run);
            var hide = row.Q<Button>("lb-hide");
            hide.clicked += () =>
            {
                Click();
                ModerationPrompts.ToggleHidden(hide, run, Report, LoadPage);
            };
            var account = row.Q<Button>("lb-account");
            account.clicked += () =>
            {
                Click();
                ModerationPrompts.ToggleAccount(account, run.accountId, run.username, Report, LoadPage);
            };
        }

        void Report(string text, bool good) => SetStatus(text);

        void ShowLeaderboardPicks()
        {
            foreach (var (run, row) in globalRows) row.EnableInClassList("selected", pickOrder.Contains(run.ghostId));
        }

        // After an upload: its board may be new (reload the list) or the shown one (reload the page).
        void ReloadBoardsAfterUpload(long uploadedBoard)
        {
            if (boards == null || !boards.Exists(b => b.boardId == uploadedBoard)) LoadBoards();
            else if (uploadedBoard == boardId) LoadPage();
        }

        // Login, logout and the role change the own row, hidden runs and the moderator buttons; a returning
        // connection retries a board that could not load.
        void OnOnlineChanged()
        {
            if (lbList == null) return;
            var loggedIn = OnlineService.IsLoggedIn;
            var mod = OnlineService.IsModerator;
            var online = OnlineService.State >= OnlineState.Connected;
            var cameOnline = online && !shownOnline;
            shownOnline = online;
            if (loggedIn != shownLoggedIn || mod != shownModerator)
            {
                shownLoggedIn = loggedIn;
                shownModerator = mod;
                SetVisible("reports", mod);
                BuildScopes();
                scopeChoice.Refresh();
                ShowUploadMarks();
                LoadMyRuns();
                Reload();
            }
            else if (cameOnline && offlineShown) Reload();
        }
    }
}
