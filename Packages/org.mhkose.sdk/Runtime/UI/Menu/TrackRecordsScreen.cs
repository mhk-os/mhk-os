using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Ghosts;
using MHK.Modding;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// One track's records (TrackRecords.uxml), opened from the Time Trials hub (Records page): engine class chips, the
    /// player's stored ghosts of the class and version (Watch, Upload or the uploaded rank, Export, Delete) and the online
    /// leaderboard (TrackRecordsScreen.Leaderboard.cs). Ghosts of both lists can be picked, up to
    /// <see cref="GameConfig.maxGhosts"/>, the personal best is picked when the page opens. Race downloads the picked
    /// online ones, then walks driver and kart with the track fixed straight into the time trial
    /// (<see cref="MenuSelection.BuildTimeTrialSession"/>); after it the menu comes back here (<see cref="ReturnAfterRace"/>).
    /// On narrow screens (<see cref="narrowWidth"/>) the two sections show one at a time behind section chips.
    /// </summary>
    public partial class TrackRecordsScreen : GhostListScreen
    {
        [SerializeField, Tooltip("Engine class and section chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Stored ghost row (Templates/GhostRow.uxml).")]
        VisualTreeAsset ghostRowTemplate;
        [SerializeField, Tooltip("Leaderboard row (Templates/LeaderRow.uxml).")]
        VisualTreeAsset leaderRowTemplate;
        [SerializeField, Tooltip("Page width (panel units) below which the two sections show one at a time.")]
        float narrowWidth = 1560f;

        [Header("Texts")]
        [SerializeField, Tooltip("Section chips on narrow screens.")]
        string localTab = "Your ghosts", globalTab = "Leaderboard";
        [SerializeField, Tooltip("Pick pill ({0} = picked, {1} = most).")]
        string pickFormat = "{0} / {1}";
        [SerializeField, Tooltip("Tooltip of the pick pill ({0} = most).")]
        string pickTipFormat = "Picked ghosts to race (up to {0})";
        [SerializeField, Tooltip("Tooltips of the best race and best lap chips of your ghosts.")]
        string bestRaceTip = "Your best race", bestLapTip = "Your best lap";
        [SerializeField, Tooltip("Tooltip of Race on a battle arena.")]
        string arenaTip = "Time trials run on circuits only.";
        [SerializeField, Tooltip("Status after an import ({0} = ghosts taken in).")]
        string importedCountFormat = "Ghosts imported: {0}";
        [SerializeField, Tooltip("Status part for import files that could not be read ({0} = files).")]
        string unreadableFormat = "Unreadable files: {0}";
        [SerializeField, Tooltip("Status after Reload found nothing new.")]
        string nothingNewText = "No new ghosts.";
        [SerializeField, Tooltip("Status after exporting ({0} = file).")]
        string exportedFormat = "Exported: {0}";
        [SerializeField, Tooltip("Status when exporting failed.")]
        string exportFailedText = "Export failed (see the log).";
        [SerializeField, Tooltip("Question of Delete. {0} = the ghost's time.")]
        string confirmDeleteQuestion = "Delete the ghost {0}?";
        [SerializeField, Tooltip("Confirm button text of the delete question.")]
        string deleteText = "Delete";
        [SerializeField, Tooltip("Status after deleting.")]
        string deletedText = "Ghost deleted.";
        [SerializeField, Tooltip("Upload button tooltips: logged in, logged out, while uploading.")]
        string uploadTip = "Upload to the leaderboard", loginToUploadTip = "Log in to upload your ghosts", uploadingTip = "Uploading...";
        [SerializeField, Tooltip("Status when Upload is pressed logged out (the Account page opens).")]
        string loginToUploadText = "Log in to upload your ghosts to the leaderboards.";
        [SerializeField, Tooltip("Uploaded mark: rank text ({0} = rank), its tooltip, the tooltip of a run that is not the best on its board, of a hidden one.")]
        string uploadedRankFormat = "#{0}", uploadedRankTip = "On the leaderboard: rank {0}",
            uploadedSlowerTip = "Uploaded. Your best on this board is faster.", uploadedHiddenTip = "Uploaded, but hidden by a moderator.";
        [SerializeField, Tooltip("Status after uploading: a new best ({0} = rank), already there, slower than the best on the board.")]
        string uploadedFormat = "Uploaded: rank {0} on the leaderboard.", duplicateText = "This ghost was already uploaded.",
            uploadedSlowerText = "Uploaded. Your best on this board stays faster.";
        [SerializeField, Tooltip("Status while picked ghosts download ({0} = done, {1} = all).")]
        string downloadingFormat = "Downloading ghosts: {0} of {1}";
        [SerializeField, Tooltip("Status when downloads failed ({0} = failed, {1} = all, {2} = reason).")]
        string downloadsFailedFormat = "Downloads failed ({0} / {1}): {2}";
        [SerializeField, Tooltip("Status when a downloaded ghost can't be read.")]
        string unreadableDownloadText = "The downloaded ghost could not be read.";

        string trackId;
        Difficulty engineClass;
        bool showGlobal, busy;
        VisualElement page, ghostList;
        Label status, pickCount;
        Button race;
        readonly List<(GhostEntry entry, VisualElement row)> localRows = new();
        // Picked ghosts by header id in pick order, each a stored ghost or a leaderboard run.
        readonly List<string> pickOrder = new();
        readonly Dictionary<string, RunEntry> pickedRuns = new();
        readonly HashSet<string> uploading = new();

        static int MaxGhosts => Mathf.Max(1, GameRoot.Instance.Config.maxGhosts);
        static SaveData Save => GameRoot.Instance.Save;
        TrackDefinition Track => Content.Get<TrackDefinition>(trackId);

        /// <summary>
        /// Opens the records of <paramref name="id"/>, in <paramref name="engineClass"/> when given. Another track or class
        /// than last time starts fresh with the personal best picked.
        /// </summary>
        public void Open(string id, Difficulty? engineClass = null)
        {
            var cls = engineClass ?? (id == trackId ? this.engineClass : BestClass(id));
            if (id != trackId || cls != this.engineClass)
            {
                trackId = id;
                this.engineClass = cls;
                ClearPicks();
                PickOwnBest();
                ResetLeaderboard();
            }
            Flow.Show(this);
        }

        // The time trial session the menu came back from last (so a later return to the menu does not reopen it).
        [AutoStaticsCleanup] static GameSession returnedFrom;

        /// <summary>
        /// After an offline time trial or replay (the session that just ended): opens the Time Trials hub and that track's
        /// page in its class on top of <paramref name="flow"/>'s title screen. False when the last session was something else.
        /// </summary>
        public static bool ReturnAfterRace(MenuFlow flow)
        {
            var session = GameRoot.Instance.Session;
            if (session == null || session == returnedFrom || session.mode != GameMode.TimeTrial || string.IsNullOrEmpty(session.CurrentTrackId))
                return false;
            returnedFrom = session;
            var hub = flow.Get<RecordsScreen>();
            var page = flow.Get<TrackRecordsScreen>();
            if (hub == null || page == null) return false;
            hub.FocusTrack(session.CurrentTrackId, session.difficulty);
            flow.Show(hub);
            page.Open(session.CurrentTrackId, session.difficulty);
            return true;
        }

        // The personal best of the shown track and class is picked by default (raced as the split reference).
        void PickOwnBest()
        {
            var best = Store.Best(trackId, engineClass);
            if (best != null && best.IsCurrent && !pickOrder.Contains(best.Id)) pickOrder.Add(best.Id);
        }

        // The class of the fastest current personal best, else the one last raced.
        Difficulty BestClass(string id)
        {
            var best = Store.List(id).Where(e => e.kind == GhostKind.Best && e.IsCurrent).OrderBy(e => e.header.finishTime).FirstOrDefault();
            return best != null ? ClassOf(best.header.engineClass) : Selection.difficulty;
        }

        static Difficulty ClassOf(int engineClass) =>
            (Difficulty)Mathf.Clamp(engineClass, 0, Mathf.Max(0, GameRoot.Instance.Config.difficulties.Length - 1));

        protected override void Bind()
        {
            page = Q<VisualElement>("page");
            ghostList = Q<VisualElement>("ghost-list");
            status = Q<Label>("status");
            pickCount = Q<Label>("pick-count");
            race = OnClick("race", Race, click: false);
            SetStatus("");
            Q<Label>("track-name").text = ContentDisplay.TrackName(trackId);
            Q<Label>("track-game").text = ContentDisplay.TrackGame(trackId);
            var track = Track;
            Q<VisualElement>("track-picture").SetImage(track != null ? track.Picture : null);
            LeaderRows.KeepFocusedRowInView(Q<ScrollView>("ghost-scroll"), ghostList);
            BindClasses();
            BindSections();
            OnClick("pick-clear", () =>
            {
                ClearPicks();
                ShowPicks();
            });
            OnClick("reload", () => ImportNow(true));
            OnClick("open-import", () => GhostUi.Reveal(GhostUi.ImportFolder));
            OnClick("reports", () => Flow.Show<FlaggedRunsScreen>());
            page.RegisterCallback<GeometryChangedEvent>(_ => ApplyLayout());
            // Up from the top of a column reaches the toolbar: the class chips, or the version from the leaderboard.
            LeaderRows.NoWrap(Root);
            LeaderRows.UpFromTopTo(Q<VisualElement>("local-panel"), () => LeaderRows.SelectedChip(Q<VisualElement>("classes")));
            LeaderRows.UpFromTopTo(Q<VisualElement>("global-panel"), () => Q<VisualElement>("version").Q<Button>());
            // Down from the class chips goes to your first ghost, from the version to the leaderboard search.
            LeaderRows.DownFromTo(Q<VisualElement>("classes"), () => ghostList.Q<Button>("ghost-main"));
            LeaderRows.DownFromTo(Q<VisualElement>("version"), () => Q<TextField>("lb-search"));
            OnlineTimeTrials.Changed += ShowUploadMarks;
            BindLeaderboard();
            FocusTarget = (VisualElement)FillLocal() ?? race;
            UpdateRace();
            StartAutoImport();
            LoadMyRuns();
        }

        protected override void Unbind()
        {
            base.Unbind();
            StopAutoImport();
            OnlineTimeTrials.Changed -= ShowUploadMarks;
            UnbindLeaderboard();
            GameRoot.Instance.SaveSettings();
            localRows.Clear();
            page = ghostList = null;
            status = pickCount = null;
            race = null;
            busy = false;
        }

        protected override void OnImported(int imported, int failed)
        {
            if (ghostList == null) return;
            var text = imported + failed == 0 ? nothingNewText : string.Format(importedCountFormat, imported);
            if (failed > 0) text += UiText.Separator + string.Format(unreadableFormat, failed);
            SetStatus(text);
            FillLocal();
        }

        void SetStatus(string text)
        {
            if (status == null) return;
            status.text = text ?? "";
            status.SetVisible(!string.IsNullOrEmpty(text));
        }

        void BindClasses()
        {
            var labels = GameRoot.Instance.Config.difficulties.Select(d => d.label).ToList();
            ChipGroup.Fill(Q<VisualElement>("classes"), chipTemplate, labels, (int)engineClass, index =>
            {
                Click();
                if ((Difficulty)index == engineClass) return;
                engineClass = (Difficulty)index;
                // Ghosts of another class are not raced together.
                ClearPicks();
                PickOwnBest();
                SetStatus("");
                ResetLeaderboard();
                RefreshVersions();
                FillLocal();
                LoadBoards();
                UpdatePickCount();
            });
        }

        void BindSections()
        {
            ChipGroup.Fill(Q<VisualElement>("sections"), chipTemplate, new[] { localTab, globalTab }, showGlobal ? 1 : 0, index =>
            {
                Click();
                showGlobal = index == 1;
                ApplyLayout();
            });
        }

        void ApplyLayout()
        {
            if (page == null) return;
            var narrow = page.layout.width > 0f && page.layout.width < narrowWidth;
            page.EnableInClassList("narrow", narrow);
            page.EnableInClassList("show-global", showGlobal);
            page.EnableInClassList("show-local", !showGlobal);
        }

        // ---- Local ghosts ----

        Button FillLocal()
        {
            if (ghostList == null) return null;
            ghostList.Clear();
            localRows.Clear();
            var entries = LocalEntries().Where(InSelectedVersion).OrderBy(e => e.header.finishTime).ToList();
            ShowLocalChips(entries);
            Button first = null;
            foreach (var entry in entries)
            {
                var row = SpawnLocalRow(entry);
                if (row == null) break;
                first ??= row.Q<Button>("ghost-main");
            }
            ShowEmptyState(entries.Count == 0);
            ShowPicks();
            return first;
        }

        // The stored ghosts of the shown track and class, every version.
        IEnumerable<GhostEntry> LocalEntries() => Store.List(trackId).Where(e => e.header.engineClass == (int)engineClass);

        // Best race and best lap among the listed ghosts, as chips next to the section title.
        void ShowLocalChips(List<GhostEntry> entries)
        {
            var chips = Q<VisualElement>("local-chips");
            chips.Clear();
            if (entries.Count == 0) return;
            InfoChips.Add(chips, "sports_score", UiText.Clock(entries[0].header.finishTime), bestRaceTip, InfoChipState.On);
            var lap = entries.SelectMany(e => e.header.lapTimes ?? System.Array.Empty<float>()).Where(t => t > 0f).DefaultIfEmpty(0f).Min();
            if (lap > 0f) InfoChips.Add(chips, "timer", UiText.Clock(lap), bestLapTip);
        }

        VisualElement SpawnLocalRow(GhostEntry entry)
        {
            var row = UiTree.Spawn<VisualElement>(ghostRowTemplate, ghostList);
            if (row == null) return null;
            FillGhostRow(row, entry);
            localRows.Add((entry, row));
            var watch = row.Q<Button>("ghost-watch");
            var delete = row.Q<Button>("ghost-delete");
            var loaded = Track != null;
            watch.SetEnabled(loaded);
            if (!loaded) watch.tooltip = ContentDisplay.MissingTrackText;
            row.Q<Button>("ghost-main").clicked += () =>
            {
                Click();
                TogglePick(entry.Id, null);
            };
            watch.clicked += () => Watch(entry);
            row.Q<Button>("ghost-upload").clicked += () => Upload(entry);
            row.Q<Button>("ghost-export").clicked += () =>
            {
                Click();
                SetStatus(GhostUi.ExportStatus(entry, exportedFormat, exportFailedText));
            };
            delete.clicked += () =>
            {
                Click();
                ConfirmPrompt.Show(delete, string.Format(confirmDeleteQuestion, UiText.Clock(entry.header.finishTime)), deleteText, () => Delete(entry));
            };
            ShowUploadMark(entry, row);
            return row;
        }

        void ShowUploadMarks()
        {
            foreach (var (entry, row) in localRows) ShowUploadMark(entry, row);
        }

        // Own runs only: an imported ghost is someone else's.
        void ShowUploadMark(GhostEntry entry, VisualElement row)
        {
            var upload = row.Q<Button>("ghost-upload");
            var uploaded = row.Q<VisualElement>("ghost-uploaded");
            var own = entry.kind is GhostKind.Best or GhostKind.LastRun or GhostKind.Saved;
            RunEntry run = null;
            var isUploaded = own && OnlineTimeTrials.TryGetUploaded(entry.Id, out run);
            upload.SetVisible(own && !isUploaded);
            uploaded.SetVisible(isUploaded);
            if (isUploaded)
            {
                row.SetText("ghost-uploaded-label", run.rank > 0 ? string.Format(uploadedRankFormat, run.rank) : "");
                uploaded.tooltip = run.hidden ? uploadedHiddenTip : run.rank > 0 ? string.Format(uploadedRankTip, run.rank) : uploadedSlowerTip;
                return;
            }
            var running = uploading.Contains(entry.Id) ||
                          (OnlineTimeTrials.TryGetAutoUpload(entry.Id, out var auto) && !auto.IsCompleted);
            upload.SetEnabled(!running);
            upload.tooltip = running ? uploadingTip : OnlineService.IsLoggedIn ? uploadTip : loginToUploadTip;
        }

        void Upload(GhostEntry entry)
        {
            Click();
            if (!OnlineService.IsLoggedIn)
            {
                SetStatus(loginToUploadText);
                Flow.Show<AccountScreen>();
                return;
            }
            if (!uploading.Add(entry.Id)) return;
            ShowUploadMarks();
            UploadAsync(entry).LogExceptionsAndForget();
        }

        async Awaitable UploadAsync(GhostEntry entry)
        {
            var r = await OnlineTimeTrials.Upload(entry);
            uploading.Remove(entry.Id);
            if (!IsBound) return;
            if (r.Ok)
            {
                var u = r.value;
                SetStatus(u.duplicate ? duplicateText : u.personalBest ? string.Format(uploadedFormat, u.run.rank) : uploadedSlowerText);
                ReloadBoardsAfterUpload(u.run.boardId);
            }
            else SetStatus(r.result.message);
            ShowUploadMarks();
        }

        void LoadMyRuns()
        {
            if (OnlineService.IsLoggedIn) LoadMyRunsAsync(trackId).LogExceptionsAndForget();
        }

        // The answer lands in OnlineTimeTrials (Changed refreshes the marks); only a failure is shown.
        static async Awaitable LoadMyRunsAsync(string id)
        {
            var r = await OnlineTimeTrials.MyRuns(id);
            if (!r.Ok && r.result.status != Status.NotLoggedIn) Debug.Log($"[Records] My runs of {id}: {r.result.message}");
        }

        void Delete(GhostEntry entry)
        {
            Store.Delete(entry);
            if (pickOrder.Remove(entry.Id)) UpdatePickCount();
            SetStatus(deletedText);
            FocusLater(FillLocal());
        }

        void Watch(GhostEntry entry)
        {
            if (SceneFlow.Busy || busy || Track == null) return;
            Click();
            SceneFlow.StartSession(TimeTrialRun.ReplaySession(entry));
        }

        // ---- Picks ----

        void TogglePick(string ghostId, RunEntry run)
        {
            if (string.IsNullOrEmpty(ghostId) || busy) return;
            if (pickOrder.Remove(ghostId)) pickedRuns.Remove(ghostId);
            else
            {
                if (pickOrder.Count >= MaxGhosts)
                {
                    UiAnim.Pop(pickCount, 1.4f, 320);
                    return;
                }
                pickOrder.Add(ghostId);
                if (run != null) pickedRuns[ghostId] = run;
            }
            ShowPicks();
        }

        void ClearPicks()
        {
            pickOrder.Clear();
            pickedRuns.Clear();
        }

        void ShowPicks()
        {
            foreach (var (entry, row) in localRows) row.EnableInClassList("selected", pickOrder.Contains(entry.Id));
            ShowLeaderboardPicks();
            UpdatePickCount();
        }

        void UpdatePickCount()
        {
            if (pickCount == null) return;
            pickCount.text = string.Format(pickFormat, pickOrder.Count, MaxGhosts);
            var full = pickOrder.Count >= MaxGhosts;
            Q<VisualElement>("pick-pill").EnableInClassList("full", full);
            Q<VisualElement>("pick-pill").EnableInClassList("empty", pickOrder.Count == 0);
            Q<VisualElement>("pick-pill").tooltip = string.Format(pickTipFormat, MaxGhosts);
            Q<Button>("pick-clear").SetEnabled(pickOrder.Count > 0);
        }

        void UpdateRace()
        {
            if (race == null) return;
            var track = Track;
            var circuit = track != null && track.layout == TrackLayout.Circuit;
            race.SetEnabled(circuit && !busy);
            race.tooltip = track == null ? ContentDisplay.MissingTrackText : circuit ? "" : arenaTip;
        }

        // ---- Race ----

        void Race()
        {
            var track = Track;
            if (track == null || track.layout != TrackLayout.Circuit || SceneFlow.Busy || busy) return;
            Click();
            RaceAsync(track).LogExceptionsAndForget();
        }

        async Awaitable RaceAsync(TrackDefinition track)
        {
            // The player's own uploads are stored ghosts already; only the others' runs need their file.
            var missing = pickOrder.Where(id => pickedRuns.ContainsKey(id) && !localRows.Exists(l => l.entry.Id == id) && !OnlineGhostCache.TryGet(id, out _))
                .Select(id => pickedRuns[id]).ToList();
            if (missing.Count > 0)
            {
                busy = true;
                UpdateRace();
                var failure = await DownloadAll(missing);
                busy = false;
                if (!IsBound) return;
                UpdateRace();
                if (failure != null)
                {
                    SetStatus(failure);
                    return;
                }
                SetStatus("");
            }
            // The personal best races as such (the split reference), the other picks follow in pick order.
            var best = Store.Best(track.Id, engineClass);
            var selection = Selection;
            selection.mode = GameMode.TimeTrial;
            selection.lobbyPick = LobbyPick.None;
            selection.track = track;
            selection.difficulty = engineClass;
            selection.trackFixed = true;
            selection.raceOwnBest = best != null && pickOrder.Contains(best.Id);
            selection.ghostIds.Clear();
            selection.ghostIds.AddRange(pickOrder.Where(id => best == null || id != best.Id));
            Flow.Show<CharacterScreen>();
        }

        // Downloads every run (OnlineTimeTrials runs a few at a time), showing the progress; null when all arrived.
        async Task<string> DownloadAll(List<RunEntry> runs)
        {
            var pending = runs.Select(r => OnlineTimeTrials.Download(r.runId)).ToList();
            var all = pending.Count;
            var failed = 0;
            string reason = null;
            SetStatus(string.Format(downloadingFormat, 0, all));
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var r = done.Result;
                if (!r.Ok)
                {
                    failed++;
                    reason ??= r.result.message;
                }
                if (IsBound) SetStatus(string.Format(downloadingFormat, all - pending.Count, all));
            }
            return failed == 0 ? null : string.Format(downloadsFailedFormat, failed, all, reason);
        }

        /// <summary>Downloads <paramref name="run"/> when needed and replays it.</summary>
        void WatchRun(RunEntry run)
        {
            if (SceneFlow.Busy || busy || run == null) return;
            if (Content.Get<TrackDefinition>(run.trackId) == null)
            {
                SetStatus(ContentDisplay.MissingTrackText);
                return;
            }
            Click();
            WatchRunAsync(run).LogExceptionsAndForget();
        }

        async Awaitable WatchRunAsync(RunEntry run)
        {
            busy = true;
            UpdateRace();
            SetStatus(string.Format(downloadingFormat, 0, 1));
            var r = await OnlineTimeTrials.Download(run.runId);
            busy = false;
            if (!IsBound) return;
            UpdateRace();
            if (!r.Ok)
            {
                SetStatus(r.result.message);
                return;
            }
            var entry = Store.Find(r.value.ghostId);
            if (entry == null)
            {
                SetStatus(unreadableDownloadText);
                return;
            }
            SetStatus("");
            if (!SceneFlow.Busy) SceneFlow.StartSession(TimeTrialRun.ReplaySession(entry));
        }
    }
}
