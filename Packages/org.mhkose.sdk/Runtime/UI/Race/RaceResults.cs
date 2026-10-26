using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Ghosts;
using MHK.Online;
using MHK.Race;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Results layer (Results.uxml): result table or time-trial lap table, champion and record plates, and the
    /// next-step buttons.
    /// </summary>
    public class RaceResults : RaceLayer
    {
        [SerializeField, Tooltip("One result table row (Race/Templates/ResultRow.uxml).")]
        VisualTreeAsset resultRowTemplate;
        [SerializeField, Tooltip("One time-trial lap row (Race/Templates/LapRow.uxml).")]
        VisualTreeAsset lapRowTemplate;

        [Header("Texts")]
        [SerializeField, Tooltip("Info line in championships ({0} = race number, {1} = races in the cup). The cup name is appended.")]
        string raceOfCupFormat = "Race {0} of {1}";
        [SerializeField, FormerlySerializedAs("battleSubtitle"), Tooltip("Info line in battles.")]
        string battleInfo = "Battle";
        [SerializeField, Tooltip("Info line in single races.")]
        string raceInfo = "Single Race";
        [SerializeField, Tooltip("Status of a battle racer still in the game at the end.")]
        string survivorText = "Survivor";
        [SerializeField, Tooltip("Status of a knocked-out battle racer.")]
        string outText = "Out";
        [SerializeField, Tooltip("Champion plate after the last cup race ({0} = name, {1} = points).")]
        string championFormat = "Champion: {0} with {1} points!";

        [Header("Time trial texts")]
        [SerializeField, Tooltip("Info line of a time trial ({0} = engine class).")]
        string timeTrialInfoFormat = "Time Trial | {0}";
        [SerializeField, Tooltip("Info line of a replay ({0} = player name, {1} = engine class).")]
        string replayInfoFormat = "Replay | {0} | {1}";
        [SerializeField, Tooltip("Race again button in time trials.")]
        string retryText = "Retry";
        [SerializeField, Tooltip("Race again button in replays.")]
        string watchAgainText = "Watch again";
        [SerializeField, Tooltip("Lap row caption ({0} = lap number).")]
        string lapFormat = "Lap {0}";
        [SerializeField, Tooltip("Caption of the finish row.")]
        string totalText = "Total";
        [SerializeField, Tooltip("Delta column header against the personal best.")]
        string vsBestText = "vs Your best";
        [SerializeField, Tooltip("Delta column header against another ghost ({0} = its player).")]
        string vsGhostFormat = "vs {0}";
        [SerializeField, Tooltip("Plate of a new personal best without an earlier one.")]
        string newRecordText = "New record!";
        [SerializeField, Tooltip("Plate of a new personal best ({0} = time gained, e.g. -1.24).")]
        string newRecordFormat = "New record!  {0}";
        [SerializeField, Tooltip("Note when the run is the new personal best ({0} = track, {1} = engine class).")]
        string savedBestFormat = "Saved as your best on {0} ({1}).";
        [SerializeField, Tooltip("Note when the run missed the personal best ({0} = the best time, {1} = this run's gap to it).")]
        string savedLastFormat = "Your best stays {0} ({1}). This run is kept as your last run.";
        [SerializeField, Tooltip("Note when the run could not be stored.")]
        string notSavedText = "This run could not be saved.";
        [SerializeField, Tooltip("Note in a replay ({0} = date, {1} = version and commit it was recorded with).")]
        string recordedNoteFormat = "Recorded {0} | build {1}";
        [SerializeField, Tooltip("Appended to the replay note for a ghost of another simulation version or content.")]
        string otherVersionNote = " | Other version";
        [SerializeField, Tooltip("Tooltip of the replay note of a ghost of another simulation version or content.")]
        string otherVersionTip = "Recorded with another version. It can be raced but never counts as a record.";
        [SerializeField, Tooltip("Note after exporting ({0} = file).")]
        string exportedFormat = "Exported: {0}";
        [SerializeField, Tooltip("Note when exporting failed.")]
        string exportFailedText = "Export failed (see the log).";
        [SerializeField, Tooltip("Upload line while a new record uploads to the leaderboards.")]
        string uploadingText = "Uploading your record...";
        [SerializeField, Tooltip("Upload line once uploaded ({0} = rank on the online leaderboard).")]
        string uploadedFormat = "Uploaded: rank #{0} online.";
        [SerializeField, Tooltip("Upload line once uploaded when the server ranks another run of the account higher.")]
        string uploadedText = "Uploaded to the online leaderboard.";
        [SerializeField, Tooltip("Upload line when the upload failed ({0} = reason).")]
        string uploadFailedFormat = "Upload failed: {0}";
        [SerializeField, Tooltip("Upload line of a new record while no account is logged in.")]
        string loginHintText = "Log in on the Account screen to post your records online.";

        [Header("Online texts")]
        [SerializeField, Tooltip("Mode chips of the online results: race and battle.")]
        string onlineRaceText = "Online race", onlineBattleText = "Online battle";
        [SerializeField, Tooltip("Race chip of an online race in a set ({0} = race number, {1} = races in the set).")]
        string onlineSetFormat = "Race {0}/{1}";
        [SerializeField, Tooltip("Chip of a solo race against computer drivers.")]
        string soloChipText = "vs AI";
        [SerializeField, Tooltip("Tooltip of the solo chip.")]
        string soloChipTip = "Raced alone against computer drivers: no points.";
        [SerializeField, Tooltip("Button back to the lobby after an online race.")]
        string backToLobbyText = "Back to lobby";
        [SerializeField, Tooltip("Tooltip of the countdown on the lobby button.")]
        string autoReturnTip = "Back to the lobby by itself when the countdown ends.";
        [SerializeField, Tooltip("Info line while the server's result has not come (the player left the race or lost it).")]
        string noResultText = "No result";
        [SerializeField, Tooltip("Outcome badges of the online results: DNF, left, kicked for cheating, removed by a moderator, desync.")]
        string dnfText = "DNF", leftText = "Left", kickedText = "Kicked", removedText = "Removed", desyncText = "Desync";
        [SerializeField, Tooltip("Outcome tooltips, in the same order.")]
        string dnfTip = "Did not finish in time.", leftTip = "Left the race.", kickedTip = "Removed: the other players' checks found cheating.",
            removedTip = "Removed by a moderator.", desyncTip = "This racer's game data differed from the others'.";

        [Header("Timing (seconds)")]
        [SerializeField, Tooltip("Delay before the first result row enters.")]
        float rowsFirstDelay = 0.26f;
        [SerializeField, Tooltip("Delay between two result rows.")]
        float rowsStep = 0.11f;
        [SerializeField, Tooltip("Points gain pop, after its row started entering.")]
        float pointsPopDelay = 0.28f;
        [SerializeField, Tooltip("Champion plate, after the last row started entering.")]
        float championDelay = 0.12f;
        [SerializeField, Tooltip("Buttons, after the last row started entering.")]
        float buttonsDelay = 0.2f;

        const float GainPopScale = 2f, WinnerSpinDegreesPerSecond = 30f;
        const int GainPopMs = 420;
        static readonly string[] MedalClasses = { "gold", "silver", "bronze" };

        VisualElement staggered;
        // Bumped on every unbind, so an upload finishing later leaves a closed screen alone.
        int bindVersion;

        // Online: back to the lobby on its own once the lobby moved on from the results, but never before the table
        // could be read (a solo race ended early goes straight back to waiting).
        bool onlineReturnArmed;
        float onlineReturnAfter;
        Label returnCountdown;
        int shownCountdown = -1;
        const float MinOnlineResultsSeconds = 6f;

        protected override void Bind()
        {
            var race = Race;
            var session = race.Session;
            var champ = session.mode == GameMode.Championship;
            var online = race.IsOnline;
            var solo = online && race.Online != null && race.Online.Solo;
            var battle = race.Battle != null;
            var screen = Q<VisualElement>("results");
            screen.EnableInClassList("online", online);
            screen.EnableInClassList("solo", solo);
            BindHeader(race, session, champ, battle);
            var rows = Q<VisualElement>("rows");
            var rowCount = FillResultRows(rows, race, champ || (online && !solo), battle);
            var afterRows = UiAnim.Ms(rowsFirstDelay) + UiAnim.Ms(rowsStep) * rowCount;
            BindChampion(session, champ, afterRows);
            var laps = BindTimeTrial(race.TimeTrial);
            staggered = laps ?? rows;
            if (laps != null) afterRows = UiAnim.Ms(rowsFirstDelay) + UiAnim.Ms(rowsStep) * laps.childCount;
            if (online) BindOnlineButtons(race, afterRows, rowCount);
            else BindButtons(race, champ && session.HasNextRace, champ, afterRows);
            onlineReturnArmed = online;
            onlineReturnAfter = Time.unscaledTime + MinOnlineResultsSeconds;
            shownCountdown = -1;
        }

        void Update()
        {
            if (!onlineReturnArmed || Race == null || SceneFlow.Busy) return;
            // The next race was set up meanwhile: it loads by itself.
            if (OnlineRace.Current != null && OnlineRace.Current != Race.Online)
            {
                onlineReturnArmed = false;
                return;
            }
            var lobby = OnlineLobby.Current;
            var resultsPhase = lobby != null && lobby.Phase is not (LobbyPhase.Waiting or LobbyPhase.Voting);
            var left = Mathf.Max(onlineReturnAfter - Time.unscaledTime, resultsPhase ? OnlineLobby.PhaseSecondsLeft : 0f);
            ShowCountdown(Mathf.CeilToInt(left));
            if (Time.unscaledTime < onlineReturnAfter || resultsPhase) return;
            onlineReturnArmed = false;
            Race.Continue();
        }

        void ShowCountdown(int seconds)
        {
            if (returnCountdown == null || seconds == shownCountdown) return;
            shownCountdown = seconds;
            returnCountdown.text = seconds.ToString(CultureInfo.InvariantCulture);
            returnCountdown.SetVisible(seconds > 0);
        }

        void BindOnlineButtons(RaceManager race, long afterRows, int rowCount)
        {
            Q<Button>("race-again").SetVisible(false);
            Q<Button>("next-race").SetVisible(false);
            var menu = Q<Button>("menu");
            menu.SetVisible(true);
            var icon = menu.Q<VisualElement>(className: "icon");
            if (icon != null) icon.SetIcon("group");
            var label = menu.Q<Label>(className: "button-label");
            if (label != null) label.text = backToLobbyText;
            menu.tooltip = "";
            returnCountdown.tooltip = autoReturnTip;
            menu.clicked += () => race.Continue();
            Q<VisualElement>("buttons").SetTransitionDelay(afterRows + UiAnim.Ms(buttonsDelay));
            Root.schedule.Execute(() => menu.Focus());
            if (rowCount == 0) InfoChips.Add(Q<VisualElement>("online-chips"), "cloud_off", noResultText, null, InfoChipState.Bad);
        }

        protected override void AfterBind() => UiAnim.Stagger(staggered, rowsStep, rowsFirstDelay, "from-left");

        protected override void Unbind()
        {
            staggered = null;
            bindVersion++;
        }

        void BindHeader(RaceManager race, GameSession session, bool champ, bool battle)
        {
            Q<Label>("title").text = race.Track.DisplayName;
            var info = Q<Label>("info");
            var chips = Q<VisualElement>("online-chips");
            returnCountdown = Q<Label>("return-countdown");
            returnCountdown.SetVisible(false);
            chips.Clear();
            var setup = race.Online != null ? race.Online.Setup : null;
            chips.SetVisible(race.IsOnline);
            info.SetVisible(!race.IsOnline);
            if (race.IsOnline) AddOnlineChips(chips, setup, session, battle);
            else info.text = champ ? UiText.RaceOfCup(session, raceOfCupFormat) : battle ? battleInfo : raceInfo;
            Q<VisualElement>("head-best").SetVisible(!battle);
            Q<VisualElement>("head-status").SetVisible(battle);
            Q<VisualElement>("head-points").SetVisible(champ || (race.IsOnline && (setup == null || !setup.solo)));
        }

        // The online race at a glance: mode, its place in the set, engine class, solo against AI.
        void AddOnlineChips(VisualElement chips, RaceSetup setup, GameSession session, bool battle)
        {
            InfoChips.Add(chips, battle ? "swords" : "sports_score", battle ? onlineBattleText : onlineRaceText, null, InfoChipState.On);
            if (setup != null && setup.setSize > 0 && !setup.solo)
                InfoChips.Add(chips, "flag", string.Format(onlineSetFormat, setup.setRace, setup.setSize));
            InfoChips.Add(chips, "speed", GameRoot.Instance.Config.GetDifficulty(session.difficulty).label);
            if (setup != null && setup.solo) InfoChips.Add(chips, "smart_toy", soloChipText, soloChipTip);
        }

        int FillResultRows(VisualElement rows, RaceManager race, bool points, bool battle)
        {
            rows.Clear();
            var index = 0;
            foreach (var r in race.Results)
            {
                var row = UiTree.Spawn<VisualElement>(resultRowTemplate, rows);
                if (row == null) break;
                FillResultRow(row, r, points, battle, UiAnim.Ms(rowsFirstDelay) + UiAnim.Ms(rowsStep) * index + UiAnim.Ms(pointsPopDelay));
                index++;
            }
            return index;
        }

        void FillResultRow(VisualElement row, RaceResult r, bool champ, bool battle, long gainPopMs)
        {
            row.EnableInClassList("me", r.isHuman);
            var outcome = OutcomeBadge(r.outcome, battle);
            // Medals and the winner's burst only for racers who finished (online a leaver can be ranked 2nd of 3).
            var medal = outcome.icon == null ? r.position - 1 : -1;
            var burst = row.Q<VisualElement>("burst");
            if (medal == 0)
            {
                burst.SetVisible(true);
                UiAnim.Spin(burst, WinnerSpinDegreesPerSecond);
            }
            else burst.RemoveFromHierarchy();
            row.Q<VisualElement>("badge").SetExclusiveClass(medal, MedalClasses);
            row.SetText("badge-label", r.position.ToString(CultureInfo.InvariantCulture));
            row.SetText("name", r.name);
            ContentDisplay.SetDriver(row, r.characterId, r.kartId);
            var timeLabel = row.Q<Label>("time");
            timeLabel.text = r.time > 0f || !r.online ? UiText.Clock(r.time) : "-";
            if (outcome.icon != null)
            {
                // Not finished: the badge takes the time's place, its explanation is the tooltip.
                var cell = new VisualElement { pickingMode = PickingMode.Position };
                cell.AddToClassList("hud-res-outcome");
                InfoChips.Add(cell, outcome.icon, outcome.text, outcome.tip, InfoChipState.Bad);
                timeLabel.parent.Insert(timeLabel.parent.IndexOf(timeLabel), cell);
                timeLabel.SetVisible(false);
            }
            var extra = row.Q<Label>("best");
            extra.text = battle ? (r.survived ? survivorText : outText) : outcome.icon == null && r.bestLap > 0 ? UiText.Clock(r.bestLap) : "";
            extra.EnableInClassList("good", battle && r.survived);
            extra.EnableInClassList("bad", battle && !r.survived);
            row.Q<VisualElement>("points").SetVisible(champ);
            if (!champ) return;
            var gain = row.Q<Label>("gain");
            // Online, a racer who gained nothing shows only its set standing.
            gain.text = r.online && r.pointsGained == 0 ? "" : "+" + r.pointsGained.ToString(CultureInfo.InvariantCulture);
            row.SetText("total", "(" + r.totalPoints.ToString(CultureInfo.InvariantCulture) + ")");
            if (r.pointsGained > 0) gain.schedule.Execute(() => UiAnim.Pop(gain, GainPopScale, GainPopMs)).ExecuteLater(gainPopMs);
        }

        // Outcome badge of an online row (icon, label, tooltip), no icon for a finish.
        (string icon, string text, string tip) OutcomeBadge(Online.RaceOutcome? outcome, bool battle) => outcome switch
        {
            Online.RaceOutcome.Dnf when !battle => ("flag", dnfText, dnfTip),
            Online.RaceOutcome.Left => ("logout", leftText, leftTip),
            Online.RaceOutcome.KickedCheating => ("gavel", kickedText, kickedTip),
            Online.RaceOutcome.KickedModerator => ("shield_person", removedText, removedTip),
            Online.RaceOutcome.Desync => ("sync_problem", desyncText, desyncTip),
            _ => (null, null, null),
        };

        void BindChampion(GameSession session, bool champ, long afterRows)
        {
            var champion = Q<VisualElement>("champion");
            var cupOver = champ && !session.HasNextRace;
            champion.SetVisible(cupOver);
            if (!cupOver) return;
            var winner = session.racers.OrderByDescending(r => r.points).First();
            Q<Label>("champion-label").text = string.Format(championFormat, winner.displayName, winner.points);
            champion.SetTransitionDelay(afterRows + UiAnim.Ms(championDelay));
        }

        void BindButtons(RaceManager race, bool hasNext, bool champ, long afterRows)
        {
            var raceAgain = Q<Button>("race-again");
            var next = Q<Button>("next-race");
            var menu = Q<Button>("menu");
            raceAgain.SetVisible(!champ);
            next.SetVisible(hasNext);
            menu.SetVisible(!hasNext);
            raceAgain.clicked += () => race.Restart();
            next.clicked += () => race.Continue();
            menu.clicked += () => race.Continue();
            Q<VisualElement>("buttons").SetTransitionDelay(afterRows + UiAnim.Ms(buttonsDelay));
            var focus = !champ ? raceAgain : hasNext ? next : menu;
            Root.schedule.Execute(() => focus.Focus());
        }

        // Returns the lap table (staggered in), or null outside time trials.
        VisualElement BindTimeTrial(TimeTrialRun tt)
        {
            var watch = Q<Button>("watch-replay");
            var export = Q<Button>("export");
            Q<VisualElement>("tt").SetVisible(tt != null);
            watch.SetVisible(false);
            export.SetVisible(false);
            if (tt == null) return null;
            Q<VisualElement>("head").SetVisible(false);
            Q<VisualElement>("rows").SetVisible(false);

            var replay = tt.IsReplay;
            var run = replay ? tt.Spectated != null ? tt.Spectated.Data.header : null : tt.Run != null ? tt.Run.header : null;
            var saved = tt.Saved;
            var entry = replay ? tt.Spectated != null ? tt.Spectated.Entry : null : saved != null ? saved.entry : null;
            var classLabel = GameRoot.Instance.Config.GetDifficulty(Race.Session.difficulty).label;
            BindTimeTrialHeader(replay, run, classLabel);
            // Deltas against the ghost raced (the personal best first), none in replays.
            var reference = !replay && tt.Reference != null ? tt.Reference.Data.header : null;
            BindDeltaHeader(tt, reference);
            var laps = FillLapRows(run, reference);
            BindRecordPlate(replay, saved, run);
            var note = Q<Label>("tt-note");
            note.text = NoteText(replay, saved, run, entry, classLabel);
            note.tooltip = replay && entry != null && !entry.IsCurrent ? otherVersionTip : "";
            BindGhostButtons(watch, export, note, entry, replay);
            BindUpload(replay, saved);
            return laps;
        }

        void BindTimeTrialHeader(bool replay, GhostHeader run, string classLabel)
        {
            Q<Label>("info").text = replay && run != null ? string.Format(replayInfoFormat, PlayerName(run), run.engineClassLabel)
                : string.Format(timeTrialInfoFormat, classLabel);
            Q<Label>("race-again-label").text = replay ? watchAgainText : retryText;
        }

        void BindDeltaHeader(TimeTrialRun tt, GhostHeader reference)
        {
            var deltaHead = Q<Label>("tt-head-delta");
            deltaHead.SetVisible(reference != null);
            if (reference == null) return;
            deltaHead.text = tt.Reference.Entry != null && tt.Reference.Entry.kind == GhostKind.Best ? vsBestText : string.Format(vsGhostFormat, PlayerName(reference));
        }

        // A downloaded ghost shows its uploader's account name; the name inside the file is not trusted.
        static string PlayerName(GhostHeader h) => OnlineGhostCache.TryGet(h.id, out var online) ? online.username : h.playerName;

        VisualElement FillLapRows(GhostHeader run, GhostHeader reference)
        {
            var laps = Q<VisualElement>("tt-laps");
            laps.Clear();
            if (run == null) return laps;
            var best = run.BestLap;
            var hasReference = reference != null;
            for (var i = 0; i < run.lapTimes.Length; i++)
            {
                var t = run.lapTimes[i];
                var refLap = hasReference && i < reference.lapTimes.Length ? reference.lapTimes[i] : float.NaN;
                LapRow(laps, string.Format(lapFormat, i + 1), t, refLap, hasReference, t == best && run.lapTimes.Length > 1, false);
            }
            LapRow(laps, totalText, run.finishTime, hasReference ? reference.finishTime : float.NaN, hasReference, false, true);
            return laps;
        }

        void BindRecordPlate(bool replay, GhostSaveResult saved, GhostHeader run)
        {
            var newRecord = !replay && saved != null && saved.newRecord;
            Q<VisualElement>("tt-record").SetVisible(newRecord);
            if (!newRecord) return;
            Q<Label>("tt-record-label").text = saved.previousBest > 0f && run != null
                ? string.Format(newRecordFormat, UiText.SignedSeconds(run.finishTime - saved.previousBest))
                : newRecordText;
        }

        // Status of the automatic upload of a new record, followed until it ends.
        void BindUpload(bool replay, GhostSaveResult saved)
        {
            var label = Q<Label>("tt-upload");
            label.text = "";
            var newRecord = !replay && saved != null && saved.newRecord && saved.entry != null;
            if (newRecord && OnlineTimeTrials.TryGetAutoUpload(saved.entry.Id, out var upload))
            {
                label.text = upload.IsCompleted ? UploadText(upload.Result) : uploadingText;
                if (!upload.IsCompleted) ShowUploadWhenDone(label, upload, bindVersion);
            }
            else if (newRecord && Settings.autoUploadBests && !OnlineService.IsLoggedIn) label.text = loginHintText;
            label.SetVisible(label.text.Length > 0);
        }

        async void ShowUploadWhenDone(Label label, Task<OnlineResult<UploadResult>> upload, int version)
        {
            var r = await upload;
            if (version == bindVersion && label.panel != null) label.text = UploadText(r);
        }

        string UploadText(OnlineResult<UploadResult> r)
        {
            if (r.Ok) return r.value.run.rank > 0 ? string.Format(uploadedFormat, r.value.run.rank) : uploadedText;
            return r.result.status == Status.NotLoggedIn ? loginHintText : string.Format(uploadFailedFormat, r.result.message);
        }

        string NoteText(bool replay, GhostSaveResult saved, GhostHeader run, GhostEntry entry, string classLabel)
        {
            if (replay)
                return run == null ? "" : string.Format(recordedNoteFormat, run.Date.ToLocalTime().ToString("g"), run.gameBuild)
                                          + (entry != null && !entry.IsCurrent ? otherVersionNote : "");
            if (saved == null || saved.entry == null) return notSavedText;
            if (saved.newRecord) return string.Format(savedBestFormat, Race.Track.DisplayName, classLabel);
            return string.Format(savedLastFormat, UiText.Clock(saved.previousBest), UiText.SignedSeconds(run.finishTime - saved.previousBest));
        }

        void BindGhostButtons(Button watch, Button export, Label note, GhostEntry entry, bool replay)
        {
            watch.SetVisible(!replay && entry != null);
            export.SetVisible(entry != null);
            watch.clicked += () => SceneFlow.StartSession(TimeTrialRun.ReplaySession(entry));
            export.clicked += () => note.text = GhostUi.ExportStatus(entry, exportedFormat, exportFailedText);
        }

        void LapRow(VisualElement parent, string caption, float time, float referenceTime, bool deltaColumn, bool best, bool total)
        {
            var row = UiTree.Spawn<VisualElement>(lapRowTemplate, parent);
            if (row == null) return;
            row.EnableInClassList("best", best);
            row.EnableInClassList("total", total);
            row.SetText("lap-name", caption);
            row.SetText("lap-time", UiText.Clock(time));
            var delta = row.Q<Label>("lap-delta");
            delta.SetVisible(deltaColumn);
            var has = !float.IsNaN(referenceTime);
            delta.text = has ? UiText.SignedSeconds(time - referenceTime) : "";
            delta.EnableInClassList("good", has && time < referenceTime);
            delta.EnableInClassList("bad", has && time > referenceTime);
        }
    }
}
