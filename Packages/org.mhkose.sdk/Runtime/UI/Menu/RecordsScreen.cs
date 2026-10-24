using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Ghosts;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Records (Records.uxml): best lap and best race of every track with the driver and kart that set them (also of
    /// mods that are not loaded), the focused track's time-trial ghosts with Watch / Export / Delete, the player's name
    /// written into new ghosts, and Play: a time trial on the focused track through the driver, kart and ghost choice.
    /// </summary>
    public class RecordsScreen : GhostListScreen
    {
        [SerializeField, Tooltip("Track row (Templates/RecordRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Ghost row (Templates/GhostRow.uxml).")]
        VisualTreeAsset ghostRowTemplate;

        [Header("Texts")]
        [SerializeField, Tooltip("Tooltip of Play on a track of a circuit-less layout (battle arena).")]
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
        [SerializeField, Tooltip("Status after the first Delete press (the button stays armed).")]
        string confirmDeleteQuestion = "Delete this ghost?";
        [SerializeField, Tooltip("Status after deleting.")]
        string deletedText = "Ghost deleted.";

        string shownTrackId;
        Difficulty attemptClass;
        VisualElement ghostList, selectedRow;
        Label ghostsTitle, status;
        Button play, armedDelete;
        GhostEntry pendingDelete;

        static SaveData Save => GameRoot.Instance.Save;

        protected override void Bind()
        {
            ghostList = Q<VisualElement>("ghost-list");
            ghostsTitle = Q<Label>("ghosts-title");
            status = Q<Label>("ghost-status");
            play = OnClick("play", Play, click: false);
            pendingDelete = null;
            SetStatus("");
            BindPlayerName();
            OnClick("reload", () => ImportNow(true));
            OnClick("open-import", () => GhostUi.Reveal(GhostUi.ImportFolder));
            FocusTarget = FillTracks();
            StartAutoImport();
        }

        protected override void Unbind()
        {
            base.Unbind();
            StopAutoImport();
            GameRoot.Instance.SaveSettings();
            ghostList = selectedRow = null;
            ghostsTitle = status = null;
            play = armedDelete = null;
            pendingDelete = null;
        }

        protected override void OnImported(int imported, int failed)
        {
            if (ghostList == null) return;
            var text = imported + failed == 0 ? nothingNewText : string.Format(importedCountFormat, imported);
            if (failed > 0) text += UiText.Separator + string.Format(unreadableFormat, failed);
            SetStatus(text);
            FillGhosts();
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
            else UpdatePlay();
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
            row.RegisterCallback<NavigationSubmitEvent>(_ => Play());
            return row;
        }

        void ShowTrack(string trackId, VisualElement row)
        {
            selectedRow?.RemoveFromClassList("selected");
            selectedRow = row;
            row.AddToClassList("selected");
            if (trackId != shownTrackId) SetStatus("");
            shownTrackId = trackId;
            DisarmDelete();
            FillGhosts();
        }

        void FillGhosts()
        {
            if (ghostList == null || shownTrackId == null) return;
            ghostsTitle.text = ContentDisplay.TrackName(shownTrackId);
            ghostList.Clear();
            var entries = Store.List(shownTrackId).OrderBy(e => e.header.engineClass).ThenBy(e => e.header.finishTime).ToList();
            foreach (var entry in entries)
                if (!SpawnGhostRow(entry)) break;
            ShowEmptyState(entries.Count == 0);
            var best = entries.Where(e => e.kind == GhostKind.Best && e.IsCurrent).OrderBy(e => e.header.finishTime).FirstOrDefault();
            attemptClass = best != null ? ClassOf(best) : Selection.difficulty;
            UpdatePlay();
        }

        bool SpawnGhostRow(GhostEntry entry)
        {
            var row = UiTree.Spawn<VisualElement>(ghostRowTemplate, ghostList);
            if (row == null) return false;
            FillGhostRow(row, entry);
            var main = row.Q<Button>("ghost-main");
            var watch = row.Q<Button>("ghost-watch");
            var delete = row.Q<Button>("ghost-delete");
            var loaded = Content.Get<TrackDefinition>(entry.header.trackId) != null;
            watch.SetEnabled(loaded);
            if (!loaded) watch.tooltip = ContentDisplay.MissingTrackText;
            main.RegisterCallback<FocusInEvent>(_ => attemptClass = ClassOf(entry));
            main.clicked += () => Watch(entry);
            watch.clicked += () => Watch(entry);
            row.Q<Button>("ghost-export").clicked += () =>
            {
                Click();
                SetStatus(GhostUi.ExportStatus(entry, exportedFormat, exportFailedText));
            };
            delete.clicked += () =>
            {
                Click();
                Delete(entry, delete);
            };
            return true;
        }

        static Difficulty ClassOf(GhostEntry e) =>
            (Difficulty)Mathf.Clamp(e.header.engineClass, 0, Mathf.Max(0, GameRoot.Instance.Config.difficulties.Length - 1));

        void Delete(GhostEntry entry, Button button)
        {
            if (pendingDelete != entry)
            {
                DisarmDelete();
                pendingDelete = entry;
                armedDelete = button;
                button.AddToClassList("armed");
                SetStatus(confirmDeleteQuestion);
                return;
            }
            Store.Delete(entry);
            DisarmDelete();
            SetStatus(deletedText);
            FillGhosts();
            FocusLater(selectedRow);
        }

        void DisarmDelete()
        {
            armedDelete?.RemoveFromClassList("armed");
            armedDelete = null;
            pendingDelete = null;
        }

        void Watch(GhostEntry entry)
        {
            if (SceneFlow.Busy || Content.Get<TrackDefinition>(entry.header.trackId) == null) return;
            Click();
            SceneFlow.StartSession(TimeTrialRun.ReplaySession(entry));
        }

        void UpdatePlay()
        {
            if (play == null) return;
            var track = Content.Get<TrackDefinition>(shownTrackId);
            var circuit = track != null && track.layout == TrackLayout.Circuit;
            play.SetEnabled(circuit);
            play.tooltip = track == null ? ContentDisplay.MissingTrackText : circuit ? "" : arenaTip;
        }

        // A record attempt: a time trial on the shown track in the class of the focused or fastest best ghost.
        void Play()
        {
            var track = Content.Get<TrackDefinition>(shownTrackId);
            if (track == null || track.layout != TrackLayout.Circuit || SceneFlow.Busy) return;
            Click();
            Selection.mode = GameMode.TimeTrial;
            Selection.track = track;
            Selection.difficulty = attemptClass;
            Selection.trackFixed = true;
            Flow.Show<CharacterScreen>();
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
