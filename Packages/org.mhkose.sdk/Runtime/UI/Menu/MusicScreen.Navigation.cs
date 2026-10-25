using MHK.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class MusicScreen
    {
        // Fixed moves between the two lists and down to the player bar, instead of the geometric pick (which may land
        // on the seek slider). Right leaves the library from its add button (or a row whose add is disabled), Left
        // leaves the playlist from its drag handle on the left, Down leaves either list from its last row.
        void OnListNavigate(NavigationMoveEvent e, bool library)
        {
            if (e.target is not VisualElement from) return;
            var list = library ? songList : playlistList;
            var row = RowOf(from, list);
            if (row == null) return;
            var to = e.direction switch
            {
                NavigationMoveEvent.Direction.Right when library && LeavesRight(from, row) => Nearest(playlistList, row, "song-main") ?? shuffle,
                NavigationMoveEvent.Direction.Left when !library && from.name == "song-drag" => LastLibraryFocus() ?? Nearest(songList, row, "song-add"),
                NavigationMoveEvent.Direction.Down when IsLastRow(row, list) => playPause,
                _ => null
            };
            if (to == null) return;
            to.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        // Up from the player bar returns to the list element focused last.
        void OnPlayerNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Up || e.target is not VisualElement from) return;
            if (lastListFocus == null || lastListFocus.panel == null || !lastListFocus.enabledInHierarchy) return;
            lastListFocus.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        void RememberListFocus(FocusInEvent e)
        {
            if (e.target is not VisualElement v) return;
            lastListFocus = v;
            if (songList.Contains(v)) lastLibraryFocus = v;
        }

        // Left from the playlist goes back to where the library was left, the row nearest in height when that is gone.
        VisualElement LastLibraryFocus() =>
            lastLibraryFocus != null && lastLibraryFocus.panel != null && lastLibraryFocus.enabledInHierarchy ? lastLibraryFocus : null;

        static bool LeavesRight(VisualElement from, VisualElement row)
        {
            if (from.name == "song-add") return true;
            var add = row.Q<Button>("song-add");
            return from.name == "song-main" && (add == null || !add.enabledSelf);
        }

        static VisualElement RowOf(VisualElement element, VisualElement list)
        {
            while (element != null && element.parent != list) element = element.parent;
            return element != null && element.userData is MusicLibrary.Song ? element : null;
        }

        static bool IsLastRow(VisualElement row, VisualElement list)
        {
            for (var i = list.childCount - 1; i >= 0; i--)
                if (list[i].userData is MusicLibrary.Song) return list[i] == row;
            return false;
        }

        // The focusable button named buttonName of the row in list closest in height to from (a disabled add button
        // falls back to the row's play button).
        static Button Nearest(VisualElement list, VisualElement from, string buttonName)
        {
            var y = from.worldBound.center.y;
            Button best = null;
            var bestDistance = float.MaxValue;
            foreach (var row in list.Children())
            {
                if (row.userData is not MusicLibrary.Song) continue;
                var distance = Mathf.Abs(row.worldBound.center.y - y);
                if (distance >= bestDistance) continue;
                var button = row.Q<Button>(buttonName);
                if (button == null || !button.enabledSelf) button = row.Q<Button>("song-main");
                best = button;
                bestDistance = distance;
            }
            return best;
        }
    }
}
