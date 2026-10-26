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
        [SerializeField, Tooltip("Ghost count of the focused track: none, one, several ({0} = count).")]
        string noGhostsText = "No ghosts stored yet.", oneGhostText = "1 ghost stored", ghostsFormat = "{0} ghosts stored";
        [SerializeField, Tooltip("Values of Upload new bests.")]
        string offText = "Off", onText = "On";
        [SerializeField, Tooltip("Tooltip of Upload new bests.")]
        string autoUploadTip = "Uploads every new personal best of a time trial to the leaderboard while you are logged in.";
        [SerializeField, Tooltip("Account line: logged in ({0} = name), logged out (with auto upload on, off).")]
        string onlineFormat = "Uploading as {0}.", loggedOutAutoText = "Log in so new bests reach the leaderboards.",
            loggedOutText = "Log in to upload your records. Leaderboards work without.";
        [SerializeField, Tooltip("Account button: logged out, logged in.")]
        string loginText = "Log in", accountText = "Account";

        string shownTrackId;
        VisualElement selectedRow;
        Label status;
        Button open;
        SettingChoice autoUpload;

        static SaveData Save => GameRoot.Instance.Save;

        protected override void Bind()
        {
            status = Q<Label>("ghost-status");
            open = OnClick("open", OpenTrack, click: false);
            SetStatus("");
            BindPlayerName();
            autoUpload = BindChoice("auto-upload", () => 2, () => Settings.autoUploadBests ? 1 : 0, i => i == 1 ? onText : offText,
                i =>
                {
                    Settings.autoUploadBests = i == 1;
                    ShowAccount();
                }, _ => autoUploadTip);
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

        void BindPlayerName()
        {
            var nameField = Q<TextField>("player-name");
            nameField.SetValueWithoutNotify(Settings.playerName);
            nameField.textEdition.placeholder = Settings.SignatureName;
            nameField.RegisterValueChangedCallback(e => Settings.playerName = e.newValue);
        }

        void ShowAccount()
        {
            if (status == null) return;
            var account = OnlineService.Account;
            var row = Root.Q(className: "rec-account-row");
            row.EnableInClassList("online", account != null);
            Q<VisualElement>("rec-account-icon").SetIcon(account != null ? "cloud_done" : "cloud_off");
            Q<Label>("rec-account-label").text = account != null ? string.Format(onlineFormat, account.username)
                : Settings.autoUploadBests ? loggedOutAutoText : loggedOutText;
            Q<Label>("rec-account-button-label").text = account != null ? accountText : loginText;
            Q<VisualElement>("rec-account-button-icon").SetIcon(account != null ? "account_circle" : "login");
            SetVisible("reports", OnlineService.IsModerator);
            autoUpload?.Refresh();
        }

        VisualElement FillTracks()
        {
            var list = Q<VisualElement>("rec-list");
            list.Clear();
            KeepFocusedInView(Q<ScrollView>("rec-scroll"), list);
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

        // The focused track's picture and its best stored ghost per engine class.
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
                var name = new Label(difficulties[c].label);
                name.AddToClassList("rec-class-name");
                var time = new Label(best != null ? UiText.Clock(best.header.finishTime) : UiText.NoClock);
                time.AddToClassList("rec-class-time");
                time.EnableInClassList("empty", best == null);
                line.Add(name);
                line.Add(time);
                classes.Add(line);
            }
            Q<Label>("rec-ghost-count").text = entries.Count switch
            {
                0 => noGhostsText,
                1 => oneGhostText,
                _ => string.Format(ghostsFormat, entries.Count),
            };
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
