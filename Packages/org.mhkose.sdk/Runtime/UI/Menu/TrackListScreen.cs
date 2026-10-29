using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Track list editor (TrackList.uxml), e.g. a custom championship. The library lists the allowed tracks of every loaded
    /// pack by game: a row or its + adds the track at the end (a track may race more than once). The list numbers the races:
    /// handles reorder (<see cref="DragReorder"/>), the red button removes a race, Clear empties the list after asking. At
    /// most <see cref="MaxTracks"/> races. The confirm button hands the list over, Back leaves without it.
    /// Open it with <see cref="Open"/>.
    /// </summary>
    public class TrackListScreen : MenuScreen
    {
        /// <summary>Races a list holds at most.</summary>
        public const int MaxTracks = GameSession.MaxCustomTracks;

        [SerializeField, Tooltip("One track of the library and the list (Templates/TrackListRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Page title when the caller gives none.")]
        string defaultTitle = "Custom championship";
        [SerializeField, Tooltip("Confirm button text when the caller gives none.")]
        string defaultConfirmText = "Next";
        [SerializeField, Tooltip("Count line of the list ({0} = races, {1} = most).")]
        string countFormat = "{0} / {1} races";
        [SerializeField, Tooltip("Tooltip of the add buttons while the list is full ({0} = most).")]
        string fullTipFormat = "At most {0} races";
        [SerializeField, Tooltip("Tooltip of the confirm button while the list is empty.")]
        string emptyTip = "Add at least one track";
        [SerializeField, Tooltip("Question of Clear.")]
        string clearQuestion = "Remove every race from the list?";
        [SerializeField, Tooltip("Confirm button of the Clear question.")]
        string clearConfirm = "Clear";

        const string WarnClass = "warn";
        const long WarnMs = 1500;

        readonly List<TrackDefinition> races = new();
        Func<TrackDefinition, bool> allowed;
        Action<List<string>> onConfirm, onChanged;
        string title, confirmText;
        VisualElement libraryList, raceList;
        ScrollView raceScroll;
        Label count;
        Button confirm, clear;
        IVisualElementScheduledItem warnTimer;

        /// <summary>
        /// Opens the editor on <paramref name="trackIds"/> (ids that are not loaded or not <paramref name="allowed"/> are
        /// dropped, the rest is cut to <see cref="MaxTracks"/>). The library lists every loaded track <paramref name="allowed"/>
        /// accepts (null = every circuit). <paramref name="onConfirm"/> gets the list when the confirm button is pressed (the
        /// editor stays in the back history), <paramref name="onChanged"/> (optional) after every edit. <paramref name="title"/>
        /// and <paramref name="confirmText"/> replace the page title and the confirm button text.
        /// </summary>
        public void Open(IEnumerable<string> trackIds, Func<TrackDefinition, bool> allowed, Action<List<string>> onConfirm,
            Action<List<string>> onChanged = null, string title = null, string confirmText = null)
        {
            this.allowed = allowed ?? IsCircuit;
            this.onConfirm = onConfirm;
            this.onChanged = onChanged;
            this.title = string.IsNullOrEmpty(title) ? defaultTitle : title;
            this.confirmText = string.IsNullOrEmpty(confirmText) ? defaultConfirmText : confirmText;
            races.Clear();
            if (trackIds != null)
                foreach (var id in trackIds)
                {
                    var t = Content.Get<TrackDefinition>(id);
                    if (t != null && this.allowed(t) && races.Count < MaxTracks) races.Add(t);
                }
            Flow.Show(this);
        }

        /// <summary>The list as track ids in race order.</summary>
        public List<string> TrackIds => races.Select(t => t.Id).ToList();

        protected override void Bind()
        {
            Q<Label>("title").text = title;
            allowed ??= IsCircuit;
            libraryList = Q<VisualElement>("library-list");
            raceList = Q<VisualElement>("race-list");
            raceScroll = Q<ScrollView>("race-scroll");
            count = Q<Label>("race-count");
            count.RemoveFromClassList(WarnClass);
            clear = OnClick("clear", AskClear, false);
            confirm = OnClick("confirm", () => onConfirm?.Invoke(TrackIds));
            confirm.SetText("confirm-label", confirmText);
            OnlineUi.Spatial(Root);
            ScrollFocusedRowIntoView(Q<ScrollView>("library-scroll"), libraryList);
            ScrollFocusedRowIntoView(raceScroll, raceList);
            DragReorder.Attach(raceList, "tl-drag", Move, raceScroll);
            var first = FillLibrary();
            FillRaces(-1, null);
            FocusTarget = first ?? confirm;
        }

        protected override void Unbind()
        {
            base.Unbind();
            warnTimer?.Pause();
            warnTimer = null;
        }

        static bool IsCircuit(TrackDefinition t) => t.layout == TrackLayout.Circuit;

        // Rows by game (a heading each); games keep the order in which their first track was registered.
        Button FillLibrary()
        {
            libraryList.Clear();
            var tracks = Content.All<TrackDefinition>().Where(allowed).ToList();
            var games = tracks.Select(t => t.SourceGame).Distinct().ToList();
            Button first = null;
            foreach (var game in games)
            {
                var heading = new Label(UiText.GameName(game));
                heading.AddToClassList("tl-group");
                libraryList.Add(heading);
                foreach (var t in tracks)
                {
                    if (t.SourceGame != game) continue;
                    var row = SpawnRow(libraryList, t);
                    if (row == null) break;
                    var main = row.Q<Button>("tl-main");
                    main.clicked += () => Add(t);
                    row.Q<Button>("tl-add").clicked += () => Add(t);
                    first ??= main;
                }
            }
            SetVisible("library-empty", first == null);
            return first;
        }

        // Refills the numbered list; focus goes to button focusName of row focusIndex (clamped), none for -1.
        void FillRaces(int focusIndex, string focusName)
        {
            raceList.Clear();
            Focusable focus = null;
            for (var i = 0; i < races.Count; i++)
            {
                var row = SpawnRow(raceList, races[i]);
                if (row == null) break;
                row.SetText("tl-number", (i + 1).ToString(CultureInfo.InvariantCulture));
                var main = row.Q<Button>("tl-main");
                main.focusable = false;
                main.pickingMode = PickingMode.Ignore;
                var index = i;
                var remove = row.Q<Button>("tl-remove");
                remove.clicked += () => Remove(index);
                row.Q("tl-drag").SetEnabled(races.Count > 1);
                if (focusName != null && index == Mathf.Clamp(focusIndex, 0, races.Count - 1)) focus = row.Q(focusName);
            }
            raceScroll.SetVisible(races.Count > 0);
            SetVisible("race-empty", races.Count == 0);
            ShowState();
            if (focus != null) FocusLater(focus as VisualElement);
        }

        VisualElement SpawnRow(VisualElement list, TrackDefinition t)
        {
            var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
            if (row == null) return null;
            row.Q("tl-image").SetImage(t.Picture);
            row.SetText("tl-name", t.DisplayName);
            row.SetText("tl-game", UiText.GameName(t.SourceGame));
            return row;
        }

        void ShowState()
        {
            var full = races.Count >= MaxTracks;
            count.text = string.Format(CultureInfo.InvariantCulture, countFormat, races.Count, MaxTracks);
            var fullTip = full ? string.Format(CultureInfo.InvariantCulture, fullTipFormat, MaxTracks) : null;
            libraryList.Query<Button>("tl-add").ForEach(b =>
            {
                b.SetEnabled(!full);
                b.tooltip = fullTip ?? "";
            });
            clear.SetEnabled(races.Count > 0);
            confirm.SetEnabled(races.Count > 0);
            confirm.tooltip = races.Count > 0 ? "" : emptyTip;
        }

        void Add(TrackDefinition t)
        {
            if (races.Count >= MaxTracks)
            {
                WarnCount();
                return;
            }
            Click();
            races.Add(t);
            FillRaces(-1, null);
            raceScroll.schedule.Execute(() => raceScroll.scrollOffset = new Vector2(0f, float.MaxValue));
            Changed();
        }

        void Remove(int index)
        {
            if (index < 0 || index >= races.Count) return;
            Click();
            races.RemoveAt(index);
            // Focus stays in the list (the next race's remove button), or goes to the library when it is empty.
            FillRaces(index, "tl-remove");
            if (races.Count == 0) FocusLater(libraryList.Q<Button>("tl-main"));
            Changed();
        }

        void Move(int from, int to)
        {
            var t = races[from];
            races.RemoveAt(from);
            races.Insert(to, t);
            FillRaces(to, null);
            Changed();
        }

        void AskClear()
        {
            Click();
            ConfirmPrompt.Show(clear, clearQuestion, clearConfirm, () =>
            {
                races.Clear();
                FillRaces(-1, null);
                FocusLater(libraryList.Q<Button>("tl-main"));
                Changed();
            });
        }

        void Changed() => onChanged?.Invoke(TrackIds);

        // The count line turns red for a moment: the list is full.
        void WarnCount()
        {
            count.AddToClassList(WarnClass);
            warnTimer?.Pause();
            warnTimer = count.schedule.Execute(() => count.RemoveFromClassList(WarnClass));
            warnTimer.ExecuteLater(WarnMs);
        }

        // The row holding the focused element scrolls into view (focus sits on the row's buttons and handle).
        static void ScrollFocusedRowIntoView(ScrollView scroll, VisualElement list) =>
            list.RegisterCallback<FocusInEvent>(e =>
            {
                var v = e.target as VisualElement;
                while (v != null && v.parent != list) v = v.parent;
                if (v != null) scroll.ScrollTo(v);
            });
    }
}
