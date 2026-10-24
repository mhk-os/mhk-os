using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>A row of chips (Templates/Chip.uxml) with exactly one <c>.selected</c>. Picking one moves the selection.</summary>
    public static class ChipGroup
    {
        /// <summary>
        /// Replaces the children of <paramref name="container"/> with one chip per label, selects the chip at
        /// <paramref name="selected"/> and calls <paramref name="onPick"/> with the index of a clicked chip.
        /// </summary>
        public static void Fill(VisualElement container, VisualTreeAsset template, IReadOnlyList<string> labels, int selected, Action<int> onPick)
        {
            container.Clear();
            for (var i = 0; i < labels.Count; i++)
            {
                var chip = UiTree.Spawn<Button>(template, container);
                if (chip == null) return;
                var index = i;
                chip.text = labels[i];
                chip.EnableInClassList("selected", i == selected);
                chip.clicked += () =>
                {
                    foreach (var other in container.Children()) other.EnableInClassList("selected", other == chip);
                    onPick(index);
                };
            }
        }
    }
}
