using System;
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
    /// The Version dropdown and the leaderboard of the track records page. Versions come from the track's boards in the
    /// shown class and from the stored ghosts: one entry per content version, Current first, or one per board when a
    /// version has several (other track data or lap count). The chosen one filters both lists. The leaderboard has
    /// filters, pages of <see cref="pageSize"/> rows with the player's own row pinned, Watch and picking, rank tags and
    /// player cards on the names, and for moderators a Hidden tab (the board's hidden runs with Unhide) and the row
    /// actions. Browsing needs no login; an unreachable server shows inline with Retry, and the board reloads when the
    /// connection or login changes.
    /// </summary>
    public partial class TrackRecordsScreen
    {
        [Header("Leaderboard")]
        [SerializeField, Tooltip("Rows per leaderboard page (1-100).")]
        int pageSize = 25;
        [SerializeField, Tooltip("Version entries: the current content version, an older one ({0} = version), the separator before extra parts.")]
        string currentVersionLabel = "Current version", olderVersionLabel = "Version {0}", versionPartSeparator = " · ";
        [SerializeField, Tooltip("Version entry parts when a version has several boards: other track data than this device's, a lap count ({0} = laps).")]
        string otherTrackLabel = "other track data", boardLapsLabel = "{0} laps";
        [SerializeField, Tooltip("Version entry tooltip parts: one player, players ({0}), ghosts stored on this device only.")]
        string onePlayerText = "1 player", playersFormat = "{0} players", localOnlyText = "Only on this device";
        [SerializeField, Tooltip("Tooltips of the board chips: players, last upload.")]
        string playersTip = "Players on this board", lastUploadTip = "Last upload";
        [SerializeField, Tooltip("Region filter: everywhere, the player's continent ({0}), the player's country ({0}).")]
        string worldText = "World", myContinentFormat = "My continent: {0}", myCountryFormat = "My country: {0}";
        [SerializeField, Tooltip("Kart and driver filters without a choice.")]
        string anyKartText = "Any kart", anyDriverText = "Any driver";
        [SerializeField, Tooltip("Placeholder of the player search.")]
        string searchHint = "Player";
        [SerializeField, Tooltip("Leaderboard states: loading, no runs, no rows matching the filters, offline (the reason is the tooltip).")]
        string stateLoading = "Loading", stateNoRuns = "No runs yet", stateNoMatch = "No matches", stateOffline = "Offline";
        [SerializeField, Tooltip("Pager text ({0} = first row, {1} = last row, {2} = all rows).")]
        string pageRangeFormat = "{0}-{1} / {2}";
        [SerializeField, Tooltip("Moderators' tabs of the leaderboard: visible runs, hidden runs.")]
        string leaderboardTab = "Leaderboard", hiddenTab = "Hidden";
        [SerializeField, Tooltip("Hidden tab state without hidden runs on the board.")]
        string stateNoHidden = "No hidden runs";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly (string code, string name)[] Continents =
        {
            ("AF", "Africa"), ("AN", "Antarctica"), ("AS", "Asia"), ("EU", "Europe"), ("NA", "North America"), ("OC", "Oceania"), ("SA", "South America"),
        };

        /// <summary>One entry of the Version dropdown: a content version, or one board of it.</summary>
        sealed class VersionEntry
        {
            public int contentVersion;
            /// <summary>Track data of the board or ghosts, empty = any.</summary>
            public string trackHash = "";
            /// <summary>Laps of the board or ghosts, 0 = any.</summary>
            public int laps;
            /// <summary>The leaderboard, null when nobody uploaded.</summary>
            public BoardInfo board;
            public DateTime last;

            public bool Matches(int versionNumber, string hash, int lapCount) =>
                contentVersion == versionNumber && string.Equals(trackHash, hash ?? "", StringComparison.OrdinalIgnoreCase) && laps == lapCount;
        }

        // Kept while the page is left for a race and back; reset per track and class.
        List<BoardInfo> boards;
        VersionEntry version;
        bool versionPicked;
        int offset;
        // Filters, kept for the whole visit.
        string search = "", scopeContinent = "", scopeCountry = "", kartFilter = "", characterFilter = "";
        int request;
        bool shownLoggedIn, shownModerator, shownOnline, offlineShown, showHidden;
        readonly RankTags ranks = new();
        PlayerCardOptions cardOptions;

        VisualElement lbList, lbMine, lbState, lbPager, lbStateIcon, boardChips;
        ScrollView lbScroll;
        Label lbStateLabel, lbRange;
        Button lbRetry, lbPrev, lbNext;
        TextField lbSearch;
        SettingChoice versionChoice, scopeChoice, kartChoice, characterChoice;
        IVisualElementScheduledItem searchApply;
        readonly List<VersionEntry> versions = new();
        readonly List<(RunEntry run, VisualElement row)> globalRows = new();
        readonly List<(string label, string continent, string country)> scopes = new();
        readonly List<KartDefinition> karts = new();
        readonly List<CharacterDefinition> characters = new();
        readonly List<string> kartNames = new(), characterNames = new();

        long BoardId => version?.board?.boardId ?? 0;

        /// <summary>
        /// Opens the records of <paramref name="id"/> in <paramref name="engineClass"/> with the leaderboard searched for
        /// <paramref name="username"/> (the player runs page's Open track).
        /// </summary>
        public void OpenSearched(string id, Difficulty engineClass, string username)
        {
            search = OnlineTimeTrials.Clip(username?.Trim(), RunEntry.MaxUsername);
            showHidden = false;
            offset = 0;
            Open(id, engineClass);
        }

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
            boardChips = Q<VisualElement>("board-chips");
            LeaderRows.KeepFocusedRowInView(lbScroll, lbList);
            lbRetry = OnClick("lb-retry", () =>
            {
                OnlineService.Connect();
                Reload();
            });
            lbPrev = OnClick("lb-prev", () => Page(-1));
            lbNext = OnClick("lb-next", () => Page(1));
            versionChoice = BindChoice("version", () => Mathf.Max(1, versions.Count), () => Mathf.Max(0, versions.IndexOf(version)), VersionText, i =>
            {
                if (i >= versions.Count) return;
                version = versions[i];
                versionPicked = true;
                offset = 0;
                FillLocal();
                LoadPage();
            }, VersionTip);
            versionChoice.AlwaysList = true;
            BindFilters();
            shownLoggedIn = OnlineService.IsLoggedIn;
            shownModerator = OnlineService.IsModerator;
            shownOnline = OnlineService.State >= OnlineState.Connected;
            SetVisible("reports", shownModerator);
            cardOptions = new PlayerCardOptions { runsFlow = Flow, report = Report, changed = LoadPage };
            ranks.Attach(Root);
            BindTabs();
            OnlineService.Changed += OnOnlineChanged;
            RefreshVersions();
            Reload();
        }

        // Moderators: Leaderboard | Hidden. The Hidden tab lists the board's hidden runs (no filters, no own row).
        void BindTabs()
        {
            if (!shownModerator) showHidden = false;
            var tabs = Q<VisualElement>("lb-tabs");
            tabs.SetVisible(shownModerator);
            ChipGroup.Fill(tabs, chipTemplate, new[] { leaderboardTab, hiddenTab }, showHidden ? 1 : 0, index =>
            {
                Click();
                if (showHidden == (index == 1)) return;
                showHidden = index == 1;
                FilterChanged();
            });
            Q<VisualElement>("lb-filters").SetVisible(!showHidden);
        }

        void UnbindLeaderboard()
        {
            OnlineService.Changed -= OnOnlineChanged;
            ranks.Detach();
            PlayerCard.Close();
            cardOptions = null;
            searchApply?.Pause();
            searchApply = null;
            request++;
            globalRows.Clear();
            lbList = lbMine = lbState = lbPager = lbStateIcon = boardChips = null;
            lbScroll = null;
            lbStateLabel = lbRange = null;
            lbRetry = lbPrev = lbNext = null;
            lbSearch = null;
            versionChoice = scopeChoice = kartChoice = characterChoice = null;
        }

        void ResetLeaderboard()
        {
            boards = null;
            version = null;
            versionPicked = false;
            offset = 0;
            request++;
        }

        // ---- Versions ----

        // Rebuilds the entries from the boards (when loaded) and the stored ghosts; keeps the chosen entry, else picks
        // Current with the local track data, else the most active board of Current.
        void RefreshVersions()
        {
            var known = Store.KnownTrackHash(trackId) ?? "";
            var kept = version;
            versions.Clear();
            if (boards != null)
                foreach (var b in boards)
                {
                    var v = Entry(b.contentVersion, b.trackHash, b.laps);
                    v.board = b;
                    if (b.lastUpload > v.last) v.last = b.lastUpload;
                }
            foreach (var e in LocalEntries())
            {
                var v = Entry(e.header.contentVersion, e.header.trackHash, e.header.laps);
                if (e.header.Date > v.last) v.last = e.header.Date;
            }
            if (!versions.Exists(v => v.contentVersion == ContentVersion.Current))
                versions.Add(new VersionEntry { contentVersion = ContentVersion.Current, trackHash = known });
            versions.Sort((a, b) =>
            {
                if (a.contentVersion != b.contentVersion)
                    return a.contentVersion == ContentVersion.Current ? -1 : b.contentVersion == ContentVersion.Current ? 1 : b.contentVersion.CompareTo(a.contentVersion);
                var aKnown = known.Length > 0 && string.Equals(a.trackHash, known, StringComparison.OrdinalIgnoreCase);
                var bKnown = known.Length > 0 && string.Equals(b.trackHash, known, StringComparison.OrdinalIgnoreCase);
                if (aKnown != bKnown) return aKnown ? -1 : 1;
                return (b.board?.players ?? 0).CompareTo(a.board?.players ?? 0);
            });
            version = versionPicked && kept != null ? versions.Find(v => v.Matches(kept.contentVersion, kept.trackHash, kept.laps)) : null;
            if (version == null)
            {
                version = versions[0];
                versionPicked = false;
            }
            versionChoice?.Refresh();
            ShowBoardChips();
        }

        VersionEntry Entry(int contentVersion, string hash, int laps)
        {
            var v = versions.Find(x => x.Matches(contentVersion, hash, laps));
            if (v != null) return v;
            v = new VersionEntry { contentVersion = contentVersion, trackHash = hash ?? "", laps = laps };
            versions.Add(v);
            return v;
        }

        // "Current version", "Current version · other track data", "Version 3 · Oct 2026".
        string VersionText(int i)
        {
            if (i >= versions.Count) return currentVersionLabel;
            var v = versions[i];
            var current = v.contentVersion == ContentVersion.Current;
            var text = current ? currentVersionLabel : string.Format(Invariant, olderVersionLabel, v.contentVersion);
            var siblings = versions.Where(o => o.contentVersion == v.contentVersion).ToList();
            if (siblings.Count > 1)
            {
                var known = Store.KnownTrackHash(trackId);
                if (siblings.Select(o => o.trackHash.ToLowerInvariant()).Distinct().Count() > 1
                    && !string.Equals(v.trackHash, known, StringComparison.OrdinalIgnoreCase))
                    text += versionPartSeparator + otherTrackLabel;
                if (siblings.Select(o => o.laps).Distinct().Count() > 1) text += versionPartSeparator + string.Format(Invariant, boardLapsLabel, v.laps);
            }
            if (!current && v.last != default) text += versionPartSeparator + v.last.ToLocalTime().ToString("MMM yyyy", Invariant);
            return text;
        }

        // Players (or that only this device has ghosts of it) and the last activity, as the entry's tooltip.
        string VersionTip(int i)
        {
            if (i >= versions.Count) return "";
            var v = versions[i];
            var parts = new List<string>();
            if (v.board != null) parts.Add(v.board.players == 1 ? onePlayerText : string.Format(Invariant, playersFormat, v.board.players));
            else if (v.last != default) parts.Add(localOnlyText);
            if (v.last != default) parts.Add(v.last.ToLocalTime().ToString("d MMM yyyy", Invariant));
            return string.Join(versionPartSeparator, parts);
        }

        // A stored ghost shows when it belongs to the chosen version (and board).
        bool InSelectedVersion(GhostEntry e)
        {
            if (version == null) return true;
            var h = e.header;
            return h.contentVersion == version.contentVersion
                   && (version.trackHash.Length == 0 || string.Equals(h.trackHash, version.trackHash, StringComparison.OrdinalIgnoreCase))
                   && (version.laps == 0 || h.laps == version.laps);
        }

        void ShowBoardChips()
        {
            if (boardChips == null) return;
            boardChips.Clear();
            var b = version?.board;
            if (b == null) return;
            InfoChips.Add(boardChips, "group", b.players.ToString(Invariant), playersTip);
            if (b.lastUpload != default)
                InfoChips.Add(boardChips, "history", b.lastUpload.ToLocalTime().ToString("d MMM", Invariant), lastUploadTip);
        }

        // ---- Filters ----

        void BindFilters()
        {
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
            scopeChoice.AlwaysList = kartChoice.AlwaysList = characterChoice.AlwaysList = true;
            // Down from the filters goes to the first row (spatial navigation prefers the footer below the wide rows).
            Q<VisualElement>("lb-filters").RegisterCallback<NavigationMoveEvent>(DownToRows);
            lbSearch = Q<TextField>("lb-search");
            lbSearch.SetValueWithoutNotify(search);
            lbSearch.textEdition.placeholder = searchHint;
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

        void DownToRows(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Down || e.target is not VisualElement from) return;
            var first = lbMine.resolvedStyle.display != DisplayStyle.None ? lbMine.Q<Button>("lb-main") : null;
            if (first == null && lbScroll.resolvedStyle.display != DisplayStyle.None) first = lbList.Q<Button>("lb-main");
            if (first == null) return;
            first.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
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

        // World, the player's continent and country (account, else the IP's), then every continent and country.
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
            if (lbList != null) Q<VisualElement>("lb-filters").SetVisible(!showHidden);
            LoadPage();
        }

        LeaderboardFilter Filter() => new()
        {
            username = search,
            continent = scopeContinent,
            country = scopeCountry,
            kartId = kartFilter,
            characterId = characterFilter,
            includeHidden = false,
        };

        // ---- Loading ----

        void Reload()
        {
            if (boards == null) LoadBoards();
            else LoadPage();
        }

        void LoadBoards() => LoadBoardsAsync(++request).LogExceptionsAndForget();

        async Awaitable LoadBoardsAsync(int id)
        {
            ShowState("sync", stateLoading, false);
            var r = await OnlineTimeTrials.ListBoards(trackId, (int)engineClass, 0);
            if (id != request || !IsBound) return;
            if (!r.Ok)
            {
                ShowOffline(r.result);
                return;
            }
            boards = r.value ?? new List<BoardInfo>();
            var before = version;
            RefreshVersions();
            // The default may move to a board that just arrived; the shown ghosts follow it.
            if (before == null || !version.Matches(before.contentVersion, before.trackHash, before.laps)) FillLocal();
            await LoadPageAsync(id);
        }

        void LoadPage()
        {
            if (boards == null) LoadBoards();
            else LoadPageAsync(++request).LogExceptionsAndForget();
        }

        async Awaitable LoadPageAsync(int id)
        {
            ShowBoardChips();
            if (BoardId == 0)
            {
                ShowState("leaderboard", stateNoRuns, false);
                return;
            }
            ShowState("sync", stateLoading, false);
            var size = Mathf.Clamp(pageSize, 1, LeaderboardPage.MaxLimit);
            LeaderboardPage page;
            if (showHidden)
            {
                var hidden = await OnlineModeration.ListHidden(BoardId, offset, size);
                if (id != request || !IsBound) return;
                if (!hidden.Ok)
                {
                    ShowOffline(hidden.result);
                    return;
                }
                page = new LeaderboardPage { total = hidden.value.total, entries = hidden.value.runs };
            }
            else
            {
                var r = await OnlineTimeTrials.GetLeaderboard(BoardId, offset, size, Filter());
                if (id != request || !IsBound) return;
                if (!r.Ok)
                {
                    ShowOffline(r.result);
                    return;
                }
                page = r.value;
            }
            // A page that emptied (rows hidden or filtered away meanwhile) steps back to the last one.
            if (page.entries.Count == 0 && offset > 0 && page.total > 0)
            {
                offset = Mathf.Max(0, (page.total - 1) / size * size);
                LoadPage();
                return;
            }
            FillRows(page);
        }

        void Page(int direction)
        {
            var size = Mathf.Clamp(pageSize, 1, LeaderboardPage.MaxLimit);
            offset = Mathf.Max(0, offset + direction * size);
            LoadPage();
        }

        void ShowState(string icon, string text, bool retry, string tip = "")
        {
            if (lbState == null) return;
            offlineShown = retry;
            lbState.SetVisible(true);
            lbStateIcon.SetIcon(icon);
            lbStateLabel.text = text;
            lbStateLabel.tooltip = tip ?? "";
            lbRetry.SetVisible(retry);
            lbScroll.SetVisible(false);
            lbMine.SetVisible(false);
            lbPager.SetVisible(false);
        }

        // No answer: offline with the reason as tooltip; a refusal shows the server's message.
        void ShowOffline(OnlineResult result)
        {
            if (result.status == null) ShowState("cloud_off", stateOffline, true, result.message);
            else ShowState("error", result.message, true);
        }

        void FillRows(LeaderboardPage page)
        {
            lbList.Clear();
            lbMine.Clear();
            globalRows.Clear();
            var mod = OnlineService.IsModerator;
            lbList.EnableInClassList("lb-list-mod", mod);
            lbMine.EnableInClassList("lb-list-mod", mod);
            if (page.entries.Count == 0)
            {
                var filtered = search.Length > 0 || scopeContinent.Length > 0 || scopeCountry.Length > 0 || kartFilter.Length > 0 || characterFilter.Length > 0;
                if (showHidden) ShowState("visibility", stateNoHidden, false);
                else ShowState(filtered ? "search" : "leaderboard", filtered ? stateNoMatch : stateNoRuns, false);
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
            lbRange.text = string.Format(Invariant, pageRangeFormat, offset + 1, offset + page.entries.Count, page.total);
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
            LeaderRows.AttachPlayer(row, run, ranks, cardOptions);
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
                // Hiding an own run also changes the uploaded mark of its stored ghost.
                var own = OnlineService.Account is { } me && (long)me.id == run.accountId;
                ModerationPrompts.ToggleHidden(hide, run, Report, () =>
                {
                    LoadPage();
                    if (own) LoadMyRuns();
                });
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
            else if (uploadedBoard == BoardId) LoadPage();
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
                BindTabs();
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
