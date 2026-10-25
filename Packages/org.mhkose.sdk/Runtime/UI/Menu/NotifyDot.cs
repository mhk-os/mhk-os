using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>The small dot on a button (child with class <c>notify-dot</c>) or a chip: red for an update, blue while something downloads.</summary>
    public static class NotifyDot
    {
        /// <summary>
        /// Red dot on a chip for an update (class <c>notify-update</c>, drawn as its background image: a chip is a text
        /// button, a child would take over its size).
        /// </summary>
        public static void ShowOnChip(VisualElement chip, bool update) => chip?.EnableInClassList("notify-update", update);

        /// <summary>Shows <paramref name="dot"/> blue while <paramref name="downloading"/>, else red for an <paramref name="update"/>, else hides it.</summary>
        public static void Show(VisualElement dot, bool update, bool downloading)
        {
            if (dot == null) return;
            dot.EnableInClassList("on", update || downloading);
            dot.EnableInClassList("downloading", downloading);
        }
    }
}
