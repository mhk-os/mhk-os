using System.Diagnostics;
using System.Globalization;
using MHK.Ghosts;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace MHK.UI
{
    /// <summary>
    /// A menu page listing stored ghosts as GhostRow.uxml rows (ghost picker, Records page). While it is open, ghosts
    /// dropped into the import folder are taken in when the page opens and whenever the game gets the focus back.
    /// </summary>
    public abstract class GhostListScreen : MenuScreen
    {
        [Header("Ghost row tooltips")]
        [SerializeField, Tooltip("Tooltip of the star on the personal best.")]
        protected string bestTip = "Your best";
        [SerializeField, Tooltip("Tooltip of the last run's icon.")]
        protected string lastTip = "Last run";
        [SerializeField, Tooltip("Tooltip of the icon of a best kept from an earlier version.")]
        protected string savedTip = "Your earlier best, kept from another version";
        [SerializeField, Tooltip("Tooltip of the icon of an imported ghost.")]
        protected string importedTip = "Imported";
        [SerializeField, Tooltip("Tooltip of the icon of a ghost recorded with another simulation version or other content (the game version doesn't matter).")]
        protected string otherVersionTip = "Other version. It can be raced but never counts as a record.";

        static readonly string[] KindClasses = { "best", "last", "saved", "imported" };

        protected static IGhostStore Store => GhostStores.Current;

        protected void Reset() => showcaseVisible = false;

        /// <summary>Fills a GhostRow.uxml row: time, player, date, driver and kart pictures, engine class, kind and version icons.</summary>
        protected void FillGhostRow(VisualElement row, GhostEntry e)
        {
            var h = e.header;
            row.EnableInClassList("other", !e.IsCurrent);
            row.SetText("ghost-time", UiText.Clock(h.finishTime));
            row.SetText("ghost-player", string.IsNullOrEmpty(h.playerName) ? "?" : h.playerName);
            row.SetText("ghost-date", h.Date.ToLocalTime().ToString("d"));
            row.SetText("ghost-class", h.engineClassLabel);
            ContentDisplay.SetDriver(row, h.characterId, h.kartId);
            var kind = row.Q("ghost-kind");
            kind.SetExclusiveClass((int)e.kind, KindClasses);
            kind.tooltip = KindTag(e);
            var version = row.Q("ghost-version");
            version.tooltip = otherVersionTip;
            version.SetVisible(!e.IsCurrent);
        }

        string KindTag(GhostEntry e) => e.kind switch
        {
            GhostKind.Best => bestTip,
            GhostKind.LastRun => lastTip,
            GhostKind.Imported => importedTip,
            _ => savedTip,
        };

        /// <summary>Shows the empty note instead of the list when there are no rows.</summary>
        protected void ShowEmptyState(bool empty)
        {
            Q<VisualElement>("ghost-empty").SetVisible(empty);
            Q<VisualElement>("ghost-scroll").SetVisible(!empty);
        }

        /// <summary>Imports waiting ghosts now and again whenever the game gets the focus back, until <see cref="StopAutoImport"/>.</summary>
        protected void StartAutoImport()
        {
            Application.focusChanged -= OnFocusChanged;
            Application.focusChanged += OnFocusChanged;
            ImportNow(false);
        }

        protected void StopAutoImport() => Application.focusChanged -= OnFocusChanged;

        void OnFocusChanged(bool focused)
        {
            if (focused && IsBound) ImportNow(false);
        }

        /// <summary>Takes in the ghosts of the import folder. <see cref="OnImported"/> runs when files were found or <paramref name="always"/> is set.</summary>
        protected void ImportNow(bool always)
        {
            var clock = Stopwatch.StartNew();
            var (imported, failed) = Store.ImportPending();
            if (imported + failed > 0)
                Debug.Log($"[Ghosts] Imported {imported} ghost(s), {failed} unreadable, in {clock.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms.");
            if (imported + failed > 0 || always) OnImported(imported, failed);
        }

        /// <summary>After an import: refresh the list and report the result.</summary>
        protected abstract void OnImported(int imported, int failed);
    }
}
