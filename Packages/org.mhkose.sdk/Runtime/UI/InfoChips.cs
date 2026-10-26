using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>Look of an info chip (MHKOnline.uss <c>.info-chip</c> variants).</summary>
    public enum InfoChipState
    {
        Normal,
        /// <summary>The setting is on and matters (accent).</summary>
        On,
        /// <summary>The setting is off: dimmed.</summary>
        Off,
        Good,
        Bad,
    }

    /// <summary>
    /// Compact icon + short label chips that summarize settings and states (anticheat, engine class, laps, items, AI,
    /// players) in the same look on every screen. Explanations go into the tooltip. Styles: MHKOnline.uss, family
    /// <c>.info-chips</c> (row container), <c>.info-chip</c>, <c>.info-chip-icon</c>, <c>.info-chip-label</c>, variants
    /// <c>.on</c> <c>.off</c> <c>.good</c> <c>.bad</c>, sizes <c>.big</c>, and <c>.dark</c> on dark plates (HUD).
    /// </summary>
    public static class InfoChips
    {
        public const string ContainerClass = "info-chips";
        public const string ChipClass = "info-chip";
        static readonly string[] StateClasses = { "", "on", "off", "good", "bad" };

        /// <summary>
        /// Adds a chip with Material icon <paramref name="icon"/> (an <c>i-*</c> name, null = none) and
        /// <paramref name="text"/> (null or empty = icon only) to <paramref name="parent"/>.
        /// </summary>
        public static VisualElement Add(VisualElement parent, string icon, string text, string tooltip = null,
            InfoChipState state = InfoChipState.Normal)
        {
            var chip = new VisualElement { tooltip = tooltip ?? "" };
            chip.AddToClassList(ChipClass);
            if (state != InfoChipState.Normal) chip.AddToClassList(StateClasses[(int)state]);
            if (!string.IsNullOrEmpty(icon))
            {
                var i = new VisualElement { pickingMode = PickingMode.Ignore };
                i.AddToClassList("icon");
                i.AddToClassList("info-chip-icon");
                i.AddToClassList("i-" + icon);
                chip.Add(i);
            }
            if (!string.IsNullOrEmpty(text))
            {
                var l = new Label(text) { pickingMode = PickingMode.Ignore };
                l.AddToClassList("info-chip-label");
                chip.Add(l);
            }
            else chip.AddToClassList("icon-only");
            // Tooltips need the pointer: chips without one let clicks through.
            chip.pickingMode = string.IsNullOrEmpty(tooltip) ? PickingMode.Ignore : PickingMode.Position;
            parent?.Add(chip);
            return chip;
        }
    }
}
