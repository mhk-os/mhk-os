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
    /// ghosts of that class, fastest first. The personal best is picked by default, up to <see cref="GameConfig.maxGhosts"/> can be raced.
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
        [SerializeField, Tooltip("Picked count ({0} = picked, {1} = most).")]
        string countFormat = "{0} of {1} ghosts";
        [SerializeField, Tooltip("Tooltip of the count once the most ghosts are picked ({0} = most).")]
        string fullTipFormat = "Up to {0} ghosts can be raced.";

        const float FullPopScale = 1.4f;
        const int FullPopMs = 320;

        TrackDefinition track;
        VisualElement list;
        Label count;
        readonly List<GhostEntry> entries = new();
        readonly List<string> picked = new();
        string bestId;

        static int MaxGhosts => Mathf.Max(1, GameRoot.Instance.Config.maxGhosts);

        /// <summary>Opens the picker for a time trial on <paramref name="t"/>.</summary>
        public void Open(TrackDefinition t)
        {
            track = t;
            Flow.Show(this);
        }

        protected override void Bind()
        {
            if (track == null) track = Content.All<TrackDefinition>().FirstOrDefault(t => t.layout == TrackLayout.Circuit);
            Q<Label>("ghost-track").text = track != null ? track.DisplayName : "";
            list = Q<VisualElement>("ghost-list");
            count = Q<Label>("ghost-count");
            KeepFocusedInView(Q<ScrollView>("ghost-scroll"), list);
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

        // Picks survive a refill after an import, a class change starts from the personal best again.
        void CollectEntries(bool keepPicks)
        {
            var kept = keepPicks ? new HashSet<string>(picked) : null;
            entries.Clear();
            picked.Clear();
            bestId = null;
            if (track == null) return;
            var engineClass = (int)Selection.difficulty;
            entries.AddRange(Store.List(track.Id).Where(e => e.header.engineClass == engineClass).OrderBy(e => e.header.finishTime));
            var best = entries.FirstOrDefault(e => e.kind == GhostKind.Best);
            if (best != null) bestId = best.Id;
            foreach (var e in entries)
                if (kept != null ? kept.Contains(e.Id) : e == best) picked.Add(e.Id);
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
            count.text = string.Format(countFormat, picked.Count, MaxGhosts);
            count.tooltip = picked.Count >= MaxGhosts ? string.Format(fullTipFormat, MaxGhosts) : "";
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
            SceneFlow.StartSession(session);
        }
    }
}
