using System.Collections.Generic;
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
    /// Records (Records.uxml): best lap and best race of every track with the driver and kart that set them (also of
    /// mods that are not loaded). The side panel sums up the focused track (its best ghost per engine class) and holds the
    /// player's name written into new ghosts, the automatic upload of new bests and the online account. Clicking or
    /// submitting a track (or Open) opens its records page (<see cref="TrackRecordsScreen"/>: ghosts, leaderboard, races
    /// against picked ghosts). Moderators get Reports (<see cref="FlaggedRunsScreen"/>).
    /// </summary>
    public class RecordsScreen : GhostListScreen
    {
        [SerializeField, Tooltip("Track row (Templates/RecordRow.uxml).")]
        VisualTreeAsset rowTemplate;

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

        string shownTrackId;
        VisualElement selectedRow;
        Label status;
        Button open, autoUpload;

        static SaveData Save => GameRoot.Instance.Save;

        protected override void Bind()
        {
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
            OnClick("reload", () => ImportNow(true));
            OnClick("open-import", () => GhostUi.Reveal(GhostUi.ImportFolder));
            OnlineService.Changed += ShowAccount;
            ShowAccount();
            FocusTarget = FillTracks();
            StartAutoImport();
        }

        protected override void Unbind()
        {
            base.Unbind();
            StopAutoImport();
            OnlineService.Changed -= ShowAccount;
            GameRoot.Instance.SaveSettings();
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
            ShowSummary();
        }

        void SetStatus(string text)
        {
            status.text = text;
            status.SetVisible(!string.IsNullOrEmpty(text));
        }

        // Account chip, auto-upload toggle and the moderators' Reports button follow the login.
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
        }

        VisualElement FillTracks()
        {
            var list = Q<VisualElement>("rec-list");
            list.Clear();
            KeepFocusedInView(Q<ScrollView>("rec-scroll"), list);
            LeaderRows.NoWrap(Root);
            LeaderRows.NoScrollbarFocus(Q<ScrollView>("rec-scroll"));
            // Right from a track goes to the side panel's toggle, Left from there back to the track.
            list.RegisterCallback<NavigationMoveEvent>(e => MoveFocus(e, NavigationMoveEvent.Direction.Right, autoUpload));
            Q<VisualElement>("records-side").RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.target != Q<Button>("rec-account")) MoveFocus(e, NavigationMoveEvent.Direction.Left, selectedRow);
            });
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
            if (shown != null) ShowTrack(shownTrackId, shown);
            else if (first != null) ShowTrack(firstId, first);
            else open.SetEnabled(false);
            return shown ?? first;
        }

        // Loaded tracks in content order, then tracks of mods that are not loaded with a record and a stored name.
        static List<string> TrackIds()
        {
            var ids = Content.All<TrackDefinition>().Select(t => t.Id).ToList();
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
            var rec = Save.records.Find(r => r.trackId == trackId);
            SetTime(row.Q("rec-lap-cell"), rec?.bestLap ?? 0f, rec?.bestLapCharacter, rec?.bestLapKart);
            SetTime(row.Q("rec-race-cell"), rec?.bestRace ?? 0f, rec?.bestRaceCharacter, rec?.bestRaceKart);
            row.RegisterCallback<FocusInEvent>(_ => ShowTrack(trackId, row));
            row.RegisterCallback<PointerDownEvent>(_ => row.Focus());
            // A click or tap opens the track (the pointer down focused it first); so does submit.
            row.RegisterCallback<ClickEvent>(_ => OpenTrack());
            row.RegisterCallback<NavigationSubmitEvent>(_ => OpenTrack());
            return row;
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

        // The focused track's picture, its best stored ghost per engine class (time, driver and kart) and the ghost count.
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
                icons.SetVisible(best != null);
                var time = new Label(best != null ? UiText.Clock(best.header.finishTime) : UiText.NoClock);
                time.AddToClassList("rec-class-time");
                line.Add(time);
                classes.Add(line);
            }
            var chips = Q<VisualElement>("rec-chips");
            chips.Clear();
            InfoChips.Add(chips, "inventory_2", entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), storedGhostsTip);
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
            Flow.Get<TrackRecordsScreen>().Open(shownTrackId);
        }

        static void SetTime(VisualElement cell, float seconds, string characterId, string kartId)
        {
            var label = cell.Q<Label>(className: "rec-time");
            var has = seconds > 0f;
            label.text = has ? UiText.Clock(seconds) : UiText.NoClock;
            label.EnableInClassList("accent", has);
            label.EnableInClassList("empty", !has);
            ContentDisplay.SetDriver(cell, has ? characterId : null, has ? kartId : null);
        }
    }
}
