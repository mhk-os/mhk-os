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
    /// Ghost picker (Ghosts.uxml), opened after the kart in time trials: engine class chips and the track's stored
    /// ghosts of that class, fastest first. The personal best is picked by default, up to <see cref="GameConfig.maxGhosts"/>
    /// can be raced. Coming from a track's records page with picked ghosts (<see cref="Preselect"/>), those are listed
    /// (downloads too) and picked instead.
    /// </summary>
    public class GhostScreen : GhostListScreen
    {
        [SerializeField, Tooltip("Engine class chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Ghost row (Templates/GhostRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.05f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.1f;

        [Header("Texts")]
        [SerializeField, Tooltip("Pick pill ({0} = picked, {1} = most).")]
        string pickFormat = "{0} / {1}";
        [SerializeField, Tooltip("Tooltip of the pick pill ({0} = most).")]
        string pickTipFormat = "Picked ghosts to race (up to {0})";

        const float FullPopScale = 1.4f;
        const int FullPopMs = 320;

        TrackDefinition track;
        VisualElement list;
        Label count;
        readonly List<GhostEntry> entries = new();
        readonly List<string> picked = new();
        string bestId;
        // Ghosts picked on the track records page for the next picker of that track and class (null: none).
        string preTrackId;
        Difficulty preClass;
        List<string> preIds;

        static int MaxGhosts => Mathf.Max(1, GameRoot.Instance.Config.maxGhosts);

        /// <summary>Opens the picker for a time trial on <paramref name="t"/>.</summary>
        public void Open(TrackDefinition t)
        {
            track = t;
            // Picks of the records page only apply to the fixed-track walk they started.
            if (!Selection.trackFixed) preIds = null;
            Flow.Show(this);
        }

        /// <summary>
        /// The next picker for <paramref name="trackId"/> in <paramref name="engineClass"/> starts with exactly
        /// <paramref name="ghostIds"/> picked (stored or downloaded ghosts) instead of the personal best. Null or none
        /// clears an earlier preselection.
        /// </summary>
        public void Preselect(string trackId, Difficulty engineClass, IEnumerable<string> ghostIds)
        {
            preTrackId = trackId;
            preClass = engineClass;
            preIds = ghostIds?.ToList();
            if (preIds is { Count: 0 }) preIds = null;
        }

        protected override void Bind()
        {
            if (track == null) track = Content.All<TrackDefinition>().FirstOrDefault(t => t.layout == TrackLayout.Circuit);
            if (Preselected) Selection.difficulty = preClass;
            Q<Label>("ghost-track").text = track != null ? track.DisplayName : "";
            list = Q<VisualElement>("ghost-list");
            count = Q<Label>("ghost-count");
            KeepFocusedInView(Q<ScrollView>("ghost-scroll"), list);
            LeaderRows.NoWrap(Root);
            LeaderRows.NoScrollbarFocus(Q<ScrollView>("ghost-scroll"));
            LeaderRows.UpFromTopTo(Q<ScrollView>("ghost-scroll"), () => LeaderRows.SelectedChip(Q<VisualElement>("classes")));
            LeaderRows.DownFromTo(Q<VisualElement>("classes"), () => list.Q<Button>("ghost-main"));
            OnClick("race", StartRace);
            FillClasses();
            FocusTarget = FillList(false);
            StartAutoImport();
        }

        protected override void Unbind()
        {
            base.Unbind();
            StopAutoImport();
            list = null;
            count = null;
        }

        protected override void OnImported(int imported, int failed)
        {
            if (list != null) FocusLater(FillList(true));
        }

        void FillClasses()
        {
            var labels = GameRoot.Instance.Config.difficulties.Select(d => d.label).ToList();
            ChipGroup.Fill(Q<VisualElement>("classes"), chipTemplate, labels, (int)Selection.difficulty, index =>
            {
                Click();
                Selection.difficulty = (Difficulty)index;
                FocusLater(FillList(false));
            });
        }

        Button FillList(bool keepPicks)
        {
            list.Clear();
            CollectEntries(keepPicks);
            Button first = null;
            foreach (var entry in entries)
            {
                var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
                if (row == null) break;
                row.EnableInClassList("selected", picked.Contains(entry.Id));
                FillGhostRow(row, entry);
                var main = row.Q<Button>("ghost-main");
                if (first == null) first = main;
                main.clicked += () =>
                {
                    Click();
                    Toggle(entry, row);
                };
            }
            UiAnim.Stagger(list, rowStep, rowFirst, "from-left");
            ShowEmptyState(entries.Count == 0);
            UpdateCount();
            return first;
        }

        bool Preselected => preIds != null && track != null && string.Equals(track.Id, preTrackId, System.StringComparison.OrdinalIgnoreCase);

        // Picks survive a refill after an import; a class change starts from the personal best again, or from the ghosts
        // picked on the records page in their class (downloads among them are listed with the stored ghosts).
        void CollectEntries(bool keepPicks)
        {
            var kept = keepPicks ? new HashSet<string>(picked) : null;
            entries.Clear();
            picked.Clear();
            bestId = null;
            if (track == null) return;
            var engineClass = (int)Selection.difficulty;
            entries.AddRange(Store.List(track.Id).Where(e => e.header.engineClass == engineClass));
            var pre = Preselected && preClass == Selection.difficulty ? preIds : null;
            if (pre != null)
                foreach (var id in pre)
                {
                    if (entries.Exists(e => e.Id == id)) continue;
                    var found = Store.Find(id);
                    if (found != null && found.header.trackId == track.Id && found.header.engineClass == engineClass) entries.Add(found);
                }
            entries.Sort((a, b) => a.header.finishTime.CompareTo(b.header.finishTime));
            var best = entries.FirstOrDefault(e => e.kind == GhostKind.Best);
            if (best != null) bestId = best.Id;
            if (kept == null && pre != null) kept = new HashSet<string>(pre);
            foreach (var e in entries)
                if (picked.Count < MaxGhosts && (kept != null ? kept.Contains(e.Id) : e == best)) picked.Add(e.Id);
        }

        void Toggle(GhostEntry e, VisualElement row)
        {
            if (picked.Remove(e.Id)) row.RemoveFromClassList("selected");
            else
            {
                if (picked.Count >= MaxGhosts)
                {
                    UiAnim.Pop(count, FullPopScale, FullPopMs);
                    return;
                }
                picked.Add(e.Id);
                row.AddToClassList("selected");
            }
            UpdateCount();
        }

        void UpdateCount()
        {
            count.text = string.Format(pickFormat, picked.Count, MaxGhosts);
            var pill = count.parent;
            pill.tooltip = string.Format(pickTipFormat, MaxGhosts);
            pill.EnableInClassList("full", picked.Count >= MaxGhosts);
            pill.EnableInClassList("empty", picked.Count == 0);
        }

        void StartRace()
        {
            if (track == null || SceneFlow.Busy) return;
            var session = Selection.BuildSession(new[] { track }, 0);
            session.powerupsEnabled = false;
            session.raceOwnBest = bestId != null && picked.Contains(bestId);
            // The split reference is the personal best when it is raced, else the fastest picked ghost.
            foreach (var e in entries)
                if (e.Id != bestId && picked.Contains(e.Id)) session.ghostIds.Add(e.Id);
            Selection.trackFixed = false;
            preIds = null;
            SceneFlow.StartSession(session);
        }
    }
}
