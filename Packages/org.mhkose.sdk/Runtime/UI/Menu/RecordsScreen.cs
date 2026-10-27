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
    /// Time Trials hub (Records.uxml), opened by the title's Time Trials: every circuit (and tracks of mods that are not
    /// loaded but have a record) with your best ghost and the world record (<see cref="WorldRecords"/>) in the chosen
    /// engine class. The side panel sums up the focused track per class and holds the automatic upload of new bests and
    /// the online account. Clicking or submitting a track (or Open) opens its page (<see cref="TrackRecordsScreen"/>:
    /// ghosts, leaderboard, Race). Moderators get Reports (<see cref="FlaggedRunsScreen"/>).
    /// </summary>
    public class RecordsScreen : GhostListScreen
    {
        [SerializeField, Tooltip("Track row (Templates/RecordRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Engine class chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;

        [Header("Texts")]
        [SerializeField, Tooltip("Status after an import ({0} = ghosts taken in).")]
        string importedCountFormat = "Ghosts imported: {0}";
        [SerializeField, Tooltip("Status part for import files that could not be read ({0} = files).")]
        string unreadableFormat = "Unreadable files: {0}";
        [SerializeField, Tooltip("Status after Reload found nothing new.")]
        string nothingNewText = "No new ghosts.";
        [SerializeField, Tooltip("Tooltip of the stored ghost count chip.")]
        string storedGhostsTip = "Stored ghosts";
        [SerializeField, Tooltip("Auto-upload toggle: label and tooltips (on, off; logged out the tooltip says it waits for a login).")]
        string autoUploadLabel = "Auto-upload", autoUploadOnTip = "New personal bests go to the leaderboard",
            autoUploadOffTip = "New personal bests stay on this device", autoUploadLoginTip = "Log in so new bests go to the leaderboard";
        [SerializeField, Tooltip("Account chip: logged out label, tooltips logged in ({0} = name) and out.")]
        string loginText = "Log in", accountChipTip = "Signed in as {0}", loginChipTip = "Log in for leaderboards and uploads";
        [SerializeField, Tooltip("World record tooltips: one player, players ({0}); the online chip while loading and offline (label).")]
        string worldOnePlayerTip = "World record | 1 player", worldPlayersTip = "World record | {0} players",
            loadingWorldTip = "Loading world records", offlineLabel = "Offline";
        [SerializeField, Tooltip("Tooltips of the side panel times: your best, the world record.")]
        string yourBestTip = "Your best", worldRecordTip = "World record";

        string shownTrackId;
        Difficulty hubClass;
        bool classChosen;
        VisualElement selectedRow;
        Label status;
        Button open, autoUpload;
        readonly List<(string trackId, VisualElement row)> rows = new();

        static SaveData Save => GameRoot.Instance.Save;

        /// <summary>The next opening focuses <paramref name="trackId"/> (and shows <paramref name="engineClass"/> when given).</summary>
        public void FocusTrack(string trackId, Difficulty? engineClass = null)
        {
            shownTrackId = trackId;
            if (engineClass == null) return;
            hubClass = engineClass.Value;
            classChosen = true;
        }

        protected override void Bind()
        {
            if (!classChosen) hubClass = MenuSelection.TimeTrialClass;
            classChosen = true;
            status = Q<Label>("ghost-status");
            open = OnClick("open", OpenTrack, click: false);
            SetStatus("");
            autoUpload = OnClick("auto-upload", () =>
            {
                Settings.autoUploadBests = !Settings.autoUploadBests;
                ShowAccount();
            });
            OnClick("rec-account", () => Flow.Show<AccountScreen>());
            OnClick("reports", () => Flow.Show<FlaggedRunsScreen>());
            OnClick("players", () => Flow.Get<PlayersScreen>()?.Open(false));
            OnClick("reload", () => ImportNow(true));
            OnClick("open-import", () => GhostUi.Reveal(GhostUi.ImportFolder));
            BindClasses();
            BindNavigation();
            OnlineService.Changed += ShowAccount;
            ShowAccount();
            FocusTarget = FillTracks();
            StartAutoImport();
            LoadWorldRecords();
        }

        protected override void Unbind()
        {
            base.Unbind();
            StopAutoImport();
            OnlineService.Changed -= ShowAccount;
            GameRoot.Instance.SaveSettings();
            rows.Clear();
            selectedRow = null;
            status = null;
            open = null;
            autoUpload = null;
        }

        protected override void OnImported(int imported, int failed)
        {
            if (status == null) return;
            var text = imported + failed == 0 ? nothingNewText : string.Format(importedCountFormat, imported);
            if (failed > 0) text += UiText.Separator + string.Format(unreadableFormat, failed);
            SetStatus(text);
            ShowBests();
            ShowSummary();
        }

        void SetStatus(string text)
        {
            status.text = text;
            status.SetVisible(!string.IsNullOrEmpty(text));
        }

        void BindClasses()
        {
            var labels = GameRoot.Instance.Config.difficulties.Select(d => d.label).ToList();
            ChipGroup.Fill(Q<VisualElement>("classes"), chipTemplate, labels, (int)hubClass, index =>
            {
                Click();
                hubClass = (Difficulty)index;
                ShowBests();
                ShowWorldRecords();
            });
        }

        // Arrow keys: Right from a track goes to the side panel's toggle and Left back, Down from the class chips goes to
        // the shown track; no wrapping at the edges.
        void BindNavigation()
        {
            var list = Q<VisualElement>("rec-list");
            var scroll = Q<ScrollView>("rec-scroll");
            KeepFocusedInView(scroll, list);
            LeaderRows.NoScrollbarFocus(scroll);
            LeaderRows.NoWrap(Root);
            LeaderRows.DownFromTo(Q<VisualElement>("classes"), () => selectedRow);
            LeaderRows.UpFromTopTo(scroll, () => LeaderRows.SelectedChip(Q<VisualElement>("classes")));
            list.RegisterCallback<NavigationMoveEvent>(e => MoveFocus(e, NavigationMoveEvent.Direction.Right, autoUpload));
            Q<VisualElement>("records-side").RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.target != Q<Button>("rec-account")) MoveFocus(e, NavigationMoveEvent.Direction.Left, selectedRow);
            });
        }

        // Account chip, auto-upload toggle and the moderators' Reports button follow the login; logging in may make the
        // world records reachable.
        void ShowAccount()
        {
            if (status == null) return;
            var account = OnlineService.Account;
            var chip = Q<Button>("rec-account");
            chip.EnableInClassList("online", account != null);
            chip.tooltip = account != null ? string.Format(accountChipTip, account.username) : loginChipTip;
            Q<VisualElement>("rec-account-icon").SetIcon(account != null ? "account_circle" : "login");
            Q<Label>("rec-account-label").text = account != null ? account.username : loginText;
            var on = Settings.autoUploadBests;
            autoUpload.EnableInClassList("on", on);
            Q<VisualElement>("auto-upload-icon").SetIcon(on ? "toggle_on" : "toggle_off");
            Q<Label>("auto-upload-label").text = autoUploadLabel;
            autoUpload.tooltip = !on ? autoUploadOffTip : account != null ? autoUploadOnTip : autoUploadLoginTip;
            SetVisible("reports", OnlineService.IsModerator);
            if (!WorldRecords.Online && OnlineService.State >= OnlineState.Connected) LoadWorldRecords();
        }

        VisualElement FillTracks()
        {
            var list = Q<VisualElement>("rec-list");
            list.Clear();
            rows.Clear();
            var i = 0;
            VisualElement first = null, shown = null;
            string firstId = null;
            foreach (var id in TrackIds())
            {
                var row = SpawnTrackRow(list, id, i++ % 2 == 1);
                if (row == null) break;
                if (first == null)
                {
                    first = row;
                    firstId = id;
                }
                if (id == shownTrackId) shown = row;
            }
            ShowBests();
            ShowWorldRecords();
            if (shown != null) ShowTrack(shownTrackId, shown);
            else if (first != null) ShowTrack(firstId, first);
            else open.SetEnabled(false);
            return shown ?? first;
        }

        // Loaded circuits in content order (time trials need a circuit), then tracks of mods that are not loaded with a
        // record and a stored name.
        static List<string> TrackIds()
        {
            var ids = Content.All<TrackDefinition>().Where(t => t.layout == TrackLayout.Circuit).Select(t => t.Id).ToList();
            ids.AddRange(Save.records
                .Where(r => (r.bestLap > 0f || r.bestRace > 0f) && Content.Get<TrackDefinition>(r.trackId) == null
                            && SaveData.Find(Save.knownTracks, r.trackId) != null)
                .Select(r => r.trackId).OrderBy(ContentDisplay.TrackName));
            return ids;
        }

        VisualElement SpawnTrackRow(VisualElement list, string trackId, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
            if (row == null) return null;
            var loaded = Content.Get<TrackDefinition>(trackId) != null;
            row.EnableInClassList("alt", alt);
            row.EnableInClassList(ContentDisplay.MissingClass, !loaded);
            row.tooltip = loaded ? "" : ContentDisplay.MissingTrackText;
            row.focusable = true;
            row.SetText("rec-track", ContentDisplay.TrackName(trackId));
            row.SetText("rec-game", ContentDisplay.TrackGame(trackId));
            rows.Add((trackId, row));
            row.RegisterCallback<FocusInEvent>(_ => ShowTrack(trackId, row));
            row.RegisterCallback<PointerDownEvent>(_ => row.Focus());
            // A click or tap opens the track (the pointer down focused it first); so does submit.
            row.RegisterCallback<ClickEvent>(_ => OpenTrack());
            row.RegisterCallback<NavigationSubmitEvent>(_ => OpenTrack());
            return row;
        }

        // Your best ghost of the shown class per track (the personal best file: one header read per track).
        void ShowBests()
        {
            foreach (var (trackId, row) in rows)
            {
                var best = Store.Best(trackId, hubClass);
                var cell = row.Q("rec-best-cell");
                var has = best != null;
                cell.EnableInClassList("empty", !has);
                row.SetText("rec-best", has ? UiText.Clock(best.header.finishTime) : UiText.NoClock);
                ContentDisplay.SetDriver(cell, best?.header.characterId, best?.header.kartId);
                cell.Q(className: "rec-driver").SetVisible(has);
            }
        }

        void ShowWorldRecords()
        {
            foreach (var (trackId, row) in rows)
            {
                var has = WorldRecords.TryGet(trackId, (int)hubClass, out var board);
                var cell = row.Q("rec-world-cell");
                cell.EnableInClassList("empty", !has);
                row.SetText("rec-world", has ? UiText.Clock(board.bestMs / 1000f) : UiText.NoClock);
                cell.tooltip = has ? PlayersTip(board.players) : "";
            }
        }

        string PlayersTip(int players) => players == 1 ? worldOnePlayerTip : string.Format(CultureInfo.InvariantCulture, worldPlayersTip, players);

        // One request for every track (cached for a minute); the chip says when it loads or the server is out of reach.
        void LoadWorldRecords() => LoadWorldRecordsAsync().LogExceptionsAndForget();

        async Awaitable LoadWorldRecordsAsync()
        {
            var task = WorldRecords.Load();
            if (!task.IsCompleted) ShowOnlineChip(true);
            await task;
            if (status == null) return;
            ShowOnlineChip(false);
            ShowWorldRecords();
            ShowSummary();
        }

        void ShowOnlineChip(bool loading)
        {
            var chips = Q<VisualElement>("rec-online");
            chips.Clear();
            if (loading) InfoChips.Add(chips, "sync", null, loadingWorldTip);
            else if (!WorldRecords.Online) InfoChips.Add(chips, "cloud_off", offlineLabel, WorldRecords.LastError, InfoChipState.Off);
        }

        void ShowTrack(string trackId, VisualElement row)
        {
            selectedRow?.RemoveFromClassList("selected");
            selectedRow = row;
            row.AddToClassList("selected");
            if (trackId != shownTrackId) SetStatus("");
            shownTrackId = trackId;
            open.SetEnabled(true);
            ShowSummary();
        }

        // The focused track's picture, the ghost count, and per engine class your best (time, driver and kart) and the world record.
        void ShowSummary()
        {
            if (status == null || shownTrackId == null) return;
            var track = Content.Get<TrackDefinition>(shownTrackId);
            Q<VisualElement>("rec-picture").SetImage(track != null ? track.Picture : null);
            Q<Label>("ghosts-title").text = ContentDisplay.TrackName(shownTrackId);
            Q<Label>("rec-side-game").text = ContentDisplay.TrackGame(shownTrackId);
            var entries = Store.List(shownTrackId);
            var classes = Q<VisualElement>("rec-classes");
            classes.Clear();
            var difficulties = GameRoot.Instance.Config.difficulties;
            for (var c = 0; c < difficulties.Length; c++)
            {
                var best = entries.Where(e => e.header.engineClass == c && e.kind == GhostKind.Best && e.IsCurrent)
                    .OrderBy(e => e.header.finishTime).FirstOrDefault();
                var line = new VisualElement();
                line.AddToClassList("rec-class-line");
                line.EnableInClassList("empty", best == null);
                InfoChips.Add(line, "speed", difficulties[c].label).AddToClassList("rec-class-chip");
                var icons = new VisualElement();
                icons.AddToClassList("driver-icons");
                icons.AddToClassList("rec-class-driver");
                icons.Add(DriverIcon("driver-portrait"));
                icons.Add(DriverIcon("driver-kart"));
                line.Add(icons);
                ContentDisplay.SetDriver(icons, best?.header.characterId, best?.header.kartId);
                icons.style.visibility = best != null ? Visibility.Visible : Visibility.Hidden;
                var time = new Label(best != null ? UiText.Clock(best.header.finishTime) : UiText.NoClock) { tooltip = yourBestTip };
                time.AddToClassList("rec-class-time");
                line.Add(time);
                var hasWorld = WorldRecords.TryGet(shownTrackId, c, out var board);
                var world = new VisualElement { tooltip = hasWorld ? PlayersTip(board.players) : worldRecordTip };
                world.AddToClassList("rec-class-world");
                world.EnableInClassList("empty", !hasWorld);
                var globe = new VisualElement { pickingMode = PickingMode.Ignore };
                globe.AddToClassList("icon");
                globe.AddToClassList("i-public");
                world.Add(globe);
                world.Add(new Label(hasWorld ? UiText.Clock(board.bestMs / 1000f) : UiText.NoClock) { pickingMode = PickingMode.Ignore });
                line.Add(world);
                classes.Add(line);
            }
            var chips = Q<VisualElement>("rec-chips");
            chips.Clear();
            InfoChips.Add(chips, "inventory_2", entries.Count.ToString(CultureInfo.InvariantCulture), storedGhostsTip);
        }

        static void MoveFocus(NavigationMoveEvent e, NavigationMoveEvent.Direction direction, VisualElement to)
        {
            if (e.direction != direction || to == null || to.panel == null || e.target is not VisualElement from) return;
            to.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        static VisualElement DriverIcon(string name)
        {
            var e = new ContentImage { name = name };
            e.AddToClassList("driver-icon");
            e.AddToClassList(name);
            return e;
        }

        void OpenTrack()
        {
            if (shownTrackId == null) return;
            Click();
            Flow.Get<TrackRecordsScreen>().Open(shownTrackId, hubClass);
        }
    }
}
