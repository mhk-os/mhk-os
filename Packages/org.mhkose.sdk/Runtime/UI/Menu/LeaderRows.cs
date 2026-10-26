using MHK.Online;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>Fills Templates/LeaderRow.uxml rows (track records leaderboard, Reports page) from a <see cref="RunEntry"/>.</summary>
    public static class LeaderRows
    {
        public static string NoRankText { get; set; } = "-";
        public static string DriverFormat { get; set; } = "{0} | {1}";
        public static string FlagsTipFormat { get; set; } = "Reported by {0} player(s) whose game found something odd while watching it.";
        public static string HideTip { get; set; } = "Hide this run";
        public static string UnhideTip { get; set; } = "Show this run again";

        /// <summary>
        /// Texts, pictures and state classes of <paramref name="row"/>: rank, country, name (and You when it is the
        /// logged-in account's), <paramref name="sub"/> (default: driver and kart), time, best lap, flags, setup mismatch,
        /// hidden. <paramref name="alt"/> shades every second row.
        /// </summary>
        public static void Fill(VisualElement row, RunEntry run, bool alt, string sub = null)
        {
            var me = OnlineService.Account;
            var mine = me != null && (long)me.id == run.accountId;
            row.EnableInClassList("alt", alt);
            row.EnableInClassList("mine", mine);
            row.EnableInClassList("flagged", run.flags > 0);
            row.EnableInClassList("mismatch", run.setupMismatch);
            row.EnableInClassList("hidden-run", run.hidden);
            row.SetText("lb-rank", run.rank > 0 ? run.rank.ToString(System.Globalization.CultureInfo.InvariantCulture) : NoRankText);
            var country = row.Q<Label>("lb-country");
            country.text = string.IsNullOrEmpty(run.country) ? "--" : run.country.ToUpperInvariant();
            country.tooltip = Countries.Name(run.country);
            row.SetText("lb-name", run.username);
            row.SetText("lb-sub", sub ?? string.Format(DriverFormat, ContentDisplay.CharacterName(run.characterId), ContentDisplay.KartName(run.kartId)));
            ContentDisplay.SetDriver(row, run.characterId, run.kartId);
            row.SetText("lb-time", UiText.Clock(run.finishMs / 1000f));
            row.SetText("lb-lap", run.bestLapMs > 0 ? UiText.Clock(run.bestLapMs / 1000f) : UiText.NoClock);
            row.SetText("lb-flags-count", run.flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
            row.Q("lb-flags").tooltip = string.Format(FlagsTipFormat, run.flags);
            row.Q("lb-hide-icon").SetIcon(run.hidden ? "visibility" : "visibility_off");
            row.Q("lb-hide").tooltip = run.hidden ? UnhideTip : HideTip;
        }

        /// <summary>
        /// Scrolls <paramref name="scroll"/> so the row of <paramref name="list"/> holding the focused element (a row's
        /// button, not the row itself) stays visible.
        /// </summary>
        public static void KeepFocusedRowInView(ScrollView scroll, VisualElement list) =>
            list.RegisterCallback<FocusInEvent>(e =>
            {
                var row = e.target as VisualElement;
                while (row != null && row.parent != list) row = row.parent;
                if (row != null) scroll.ScrollTo(row);
            });
    }
}
