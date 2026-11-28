using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Arrow and stick moves in a wrapped grid of cards, by grid index. The panel's geometric navigation skips columns
    /// there, since focused cards are scaled and neighbours overlap by the negative margins.
    /// </summary>
    internal static class CardGridNavigation
    {
        /// <summary>The card a move from <paramref name="card"/> of <paramref name="grid"/> reaches, null when it leaves the grid.</summary>
        public static VisualElement Target(VisualElement grid, VisualElement card, NavigationMoveEvent.Direction direction)
        {
            var index = grid.IndexOf(card);
            var columns = Columns(grid);
            var next = direction switch
            {
                NavigationMoveEvent.Direction.Left => index - 1,
                NavigationMoveEvent.Direction.Right => index + 1,
                NavigationMoveEvent.Direction.Up => index - columns,
                NavigationMoveEvent.Direction.Down => Below(grid, index, columns),
                _ => -1
            };
            return next >= 0 && next < grid.childCount ? grid[next] : null;
        }

        /// <summary>Whether <paramref name="card"/> is the last card of its row in <paramref name="grid"/>.</summary>
        public static bool EndsRow(VisualElement grid, VisualElement card)
        {
            var index = grid.IndexOf(card);
            var columns = Columns(grid);
            return index == grid.childCount - 1 || index % columns == columns - 1;
        }

        /// <summary>Focuses <paramref name="target"/> (null keeps the focus) and ends the move.</summary>
        public static void Move(NavigationMoveEvent e, VisualElement target)
        {
            target?.Focus();
            e.StopPropagation();
            if (e.target is VisualElement from) from.focusController?.IgnoreEvent(e);
        }

        // A partial last row means down from a column it lacks goes to its last card.
        static int Below(VisualElement grid, int index, int columns)
        {
            var below = index + columns;
            if (below < grid.childCount) return below;
            var lastRowStart = (grid.childCount - 1) / columns * columns;
            return index < lastRowStart ? grid.childCount - 1 : -1;
        }

        static int Columns(VisualElement grid)
        {
            var top = grid[0].layout.y;
            var columns = 1;
            while (columns < grid.childCount && Mathf.Approximately(grid[columns].layout.y, top)) columns++;
            return columns;
        }
    }
}
