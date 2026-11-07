using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Variant picker of a track: one setting row (Templates/SettingRow.uxml, a <see cref="SettingChoice"/>) per modifier
    /// the track offers with two or more options (<see cref="TrackVariants"/>), titled with the modifier's name, valued with
    /// its options' names. <see cref="Show"/> picks one variant; <see cref="ShowSetup"/> edits a custom championship race
    /// (<see cref="CustomRaceSetup"/>) whose rows offer Random after the options. Rows are added to a container that may
    /// hold other rows too; only the picker's own are removed.
    /// </summary>
    public sealed class TrackVariantRows
    {
        const string RowClass = "setting-row";
        const string LabelName = "setting-label";

        readonly VisualElement container, host;
        readonly VisualTreeAsset rowTemplate;
        readonly Action click;
        readonly List<VisualElement> rows = new();
        TrackVariantSet set;
        CustomRaceSetup setup;
        string randomText;

        /// <summary>The picked variant, null until <see cref="Show"/> ran for a track (and while a setup is shown).</summary>
        public TrackVariant Variant { get; private set; }

        /// <summary>The shown track has a modifier to pick (some row is shown).</summary>
        public bool HasRows => rows.Count > 0;

        /// <summary>The first row's value button, null without rows.</summary>
        public Button FirstButton => rows.Count > 0 ? rows[0].Q<Button>() : null;

        /// <summary>The player picked another option: the new variant.</summary>
        public event Action<TrackVariant> Changed;

        /// <summary>The player picked another option (or Random) of the shown setup, which is already updated.</summary>
        public event Action<CustomRaceSetup> SetupChanged;

        /// <summary>
        /// Rows go into <paramref name="container"/>, built from <paramref name="rowTemplate"/> (SettingRow.uxml);
        /// <paramref name="host"/> (the screen root) holds their open lists, <paramref name="click"/> plays the click sound.
        /// </summary>
        public TrackVariantRows(VisualElement container, VisualElement host, VisualTreeAsset rowTemplate, Action click)
        {
            this.container = container;
            this.host = host;
            this.rowTemplate = rowTemplate;
            this.click = click;
        }

        /// <summary>Shows the rows of <paramref name="track"/> with variant <paramref name="key"/> picked (the track's default when it does not offer it).</summary>
        public void Show(TrackDefinition track, string key)
        {
            Clear();
            set = track != null ? TrackVariants.Of(track) : null;
            Variant = set?.Resolve(key) ?? set?.Default;
            AddRows();
        }

        /// <summary>
        /// Shows the rows of <paramref name="track"/> editing <paramref name="raceSetup"/> in place: each row offers the
        /// modifier's options and last <paramref name="randomText"/> (the modifier is rolled when the championship starts).
        /// </summary>
        public void ShowSetup(TrackDefinition track, CustomRaceSetup raceSetup, string randomText)
        {
            Clear();
            if (raceSetup == null) return;
            set = track != null ? TrackVariants.Of(track) : null;
            setup = raceSetup;
            this.randomText = randomText;
            AddRows();
        }

        /// <summary>Removes the rows; <see cref="Variant"/> becomes null.</summary>
        public void Clear()
        {
            foreach (var row in rows) row.RemoveFromHierarchy();
            rows.Clear();
            set = null;
            setup = null;
            Variant = null;
        }

        void AddRows()
        {
            if (set == null || container == null || rowTemplate == null) return;
            for (var m = 0; m < set.Modifiers.Count; m++)
                if (set.Modifiers[m].IsChoice) AddRow(m);
        }

        void AddRow(int modifierIndex)
        {
            var modifier = set.Modifiers[modifierIndex];
            var row = new VisualElement();
            row.AddToClassList(RowClass);
            rowTemplate.CloneTree(row);
            row.SetText(LabelName, modifier.DisplayName);
            row.tooltip = modifier.DisplayName;
            container.Add(row);
            rows.Add(row);
            var options = modifier.Options.Count;
            if (setup == null)
            {
                new SettingChoice(row, host, () => options, () => Variant.PickOf(modifierIndex),
                    i => modifier.Options[i].DisplayName, i => Pick(modifierIndex, i), null, click);
                return;
            }
            // Random is the value after the options.
            var raceSetup = setup;
            new SettingChoice(row, host, () => options + 1, () => raceSetup.PickOf(modifier) is var pick && pick >= 0 ? pick : options,
                i => i < options ? modifier.Options[i].DisplayName : randomText, i =>
                {
                    raceSetup.SetPick(modifier, i < options ? i : -1);
                    SetupChanged?.Invoke(raceSetup);
                }, null, click);
        }

        void Pick(int modifierIndex, int optionIndex)
        {
            Variant = set.With(Variant, modifierIndex, optionIndex);
            Changed?.Invoke(Variant);
        }
    }
}
