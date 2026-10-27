using System;
using MHK.Online;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Fills Templates/LeaderRow.uxml rows (track records leaderboard and Hidden tab, Reports page, player runs) from a
    /// <see cref="RunEntry"/>.
    /// </summary>
    public static class LeaderRows
    {
        public static string NoRankText { get; set; } = "-";
        public static string FlagsTipFormat { get; set; } = "Reported by {0} players";
        public static string FlagsTipOne { get; set; } = "Reported by 1 player";
        public static string HideTip { get; set; } = "Hide this run";
        public static string UnhideTip { get; set; } = "Show this run again";
        /// <summary>Tooltip of a hidden run until <see cref="HiddenReasons"/> knows why.</summary>
        public static string HiddenTip { get; set; } = "Hidden";

        static readonly string[] MedalClasses = { "medal-1", "medal-2", "medal-3" };

        /// <summary>
        /// Texts, pictures and state classes of <paramref name="row"/>: rank (medals for the top three), driver and kart
        /// pictures, name (and You when it is the logged-in account's), country, <paramref name="sub"/> (null hides the
        /// line), flags (moderators only), setup mismatch, hidden (its reason loaded on hover or focus), time and best lap.
        /// <paramref name="alt"/> shades every second row.
        /// </summary>
        public static void Fill(VisualElement row, RunEntry run, bool alt, string sub = null)
        {
            var me = OnlineService.Account;
            var mine = me != null && (long)me.id == run.accountId;
            row.EnableInClassList("alt", alt);
            row.EnableInClassList("mine", mine);
            row.EnableInClassList("flagged", run.flags > 0 && OnlineService.IsModerator);
            row.EnableInClassList("mismatch", run.setupMismatch);
            row.EnableInClassList("hidden-run", run.hidden);
            if (run.hidden) HiddenReasons.Attach(row.Q("lb-hidden"), run.runId, HiddenTip, row);
            var rank = row.Q<Label>("lb-rank");
            rank.text = run.rank > 0 ? run.rank.ToString(System.Globalization.CultureInfo.InvariantCulture) : NoRankText;
            rank.SetExclusiveClass(run.rank - 1, MedalClasses);
            var country = row.Q<Label>("lb-country");
            country.text = string.IsNullOrEmpty(run.country) ? "" : run.country.ToUpperInvariant();
            country.tooltip = Countries.Name(run.country);
            country.SetVisible(!string.IsNullOrEmpty(run.country));
            row.SetText("lb-name", run.username);
            // Shown by AttachPlayer once the uploader's rank is known.
            row.Q("lb-player-rank").SetVisible(false);
            var subLabel = row.Q<Label>("lb-sub");
            subLabel.text = sub ?? "";
            subLabel.SetVisible(!string.IsNullOrEmpty(sub));
            ContentDisplay.SetDriver(row, run.characterId, run.kartId);
            row.SetText("lb-time", UiText.Clock(run.finishMs / 1000f));
            row.SetText("lb-lap", UiText.Clock(run.bestLapMs / 1000f));
            row.Q("lb-lap-box").SetVisible(run.bestLapMs > 0);
            row.SetText("lb-flags-count", run.flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
            row.Q("lb-flags").tooltip = run.flags == 1 ? FlagsTipOne : string.Format(FlagsTipFormat, run.flags);
            row.Q("lb-hide-icon").SetIcon(run.hidden ? "visibility" : "visibility_off");
            row.Q("lb-hide").tooltip = run.hidden ? UnhideTip : HideTip;
        }

        /// <summary>
        /// The uploader's rank tag (lb-player-rank, through <paramref name="ranks"/>) and player card: hovering or tapping
        /// the name, or the row's lb-profile button (the gamepad's way), opens it with <paramref name="options"/>. Runs of
        /// deleted accounts (<see cref="RunEntry.Anonymous"/>) have neither, except for moderators.
        /// </summary>
        public static void AttachPlayer(VisualElement row, RunEntry run, RankTags ranks, PlayerCardOptions options)
        {
            var profile = row.Q<Button>("lb-profile");
            if (run.Anonymous && !OnlineService.IsModerator)
            {
                profile.SetVisible(false);
                return;
            }
            ranks.Add(row.Q<Label>("lb-player-rank"), run.accountId);
            var name = row.Q<Label>("lb-name");
            PlayerCard.Attach(name, run.accountId, run.username, options);
            PlayerCard.Attach(profile, name, run.accountId, run.username, options);
        }

        /// <summary>
        /// Scrolls <paramref name="scroll"/> so the row of <paramref name="list"/> holding the focused element (a row's
        /// button, not the row itself) stays visible. Its scrollbars take no focus (the arrows walk the rows).
        /// </summary>
        public static void KeepFocusedRowInView(ScrollView scroll, VisualElement list)
        {
            NoScrollbarFocus(scroll);
            list.RegisterCallback<FocusInEvent>(e =>
            {
                var row = e.target as VisualElement;
                while (row != null && row.parent != list) row = row.parent;
                if (row != null) scroll.ScrollTo(row);
            });
        }

        /// <summary>The scrollbars of <paramref name="scroll"/> take no focus, so Up / Down never stop on them.</summary>
        public static void NoScrollbarFocus(ScrollView scroll)
        {
            scroll.verticalScroller.slider.focusable = false;
            scroll.horizontalScroller.slider.focusable = false;
        }

        /// <summary>
        /// Up from the top of <paramref name="column"/> (nothing focusable above in it) focuses <paramref name="target"/>:
        /// spatial navigation misses toolbars that do not lie straight above, or wraps to the footer.
        /// </summary>
        public static void UpFromTopTo(VisualElement column, Func<VisualElement> target) =>
            column.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.direction != NavigationMoveEvent.Direction.Up || e.target is not VisualElement from) return;
                var to = target();
                if (to == null) return;
                var top = from.worldBound.yMin;
                var above = column.Query<VisualElement>().Where(v => Navigable(v) && v.worldBound.yMax <= top + 1f).First();
                if (above != null) return;
                to.Focus();
                e.StopPropagation();
                from.focusController?.IgnoreEvent(e);
            });

        /// <summary>
        /// Down from anything in <paramref name="container"/> (a toolbar or filter row) focuses <paramref name="target"/>
        /// (the first row of the list below) when there is one: spatial navigation prefers the footer below wide rows.
        /// </summary>
        public static void DownFromTo(VisualElement container, Func<VisualElement> target) =>
            container.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.direction != NavigationMoveEvent.Direction.Down || e.target is not VisualElement from) return;
                var to = target();
                if (to == null || !Navigable(to)) return;
                to.Focus();
                e.StopPropagation();
                from.focusController?.IgnoreEvent(e);
            });

        /// <summary>The selected chip of a <see cref="ChipGroup"/> container, else its first.</summary>
        public static VisualElement SelectedChip(VisualElement chips)
        {
            foreach (var c in chips.Children())
                if (c.ClassListContains("selected")) return c;
            return chips.childCount > 0 ? chips[0] : null;
        }

        /// <summary>
        /// Left / Right at the edge of a row and Up at the top of <paramref name="root"/> stay put instead of wrapping to
        /// the far side of the page (spatial navigation jumps from the last leaderboard button to the ghost list, and
        /// from the toolbar to the footer).
        /// </summary>
        public static void NoWrap(VisualElement root) =>
            root.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.target is not VisualElement from) return;
                var b = from.worldBound;
                Func<VisualElement, bool> beyond = e.direction switch
                {
                    NavigationMoveEvent.Direction.Right => v => v.worldBound.xMin >= b.xMax - 1f && Overlaps(v.worldBound, b),
                    NavigationMoveEvent.Direction.Left => v => v.worldBound.xMax <= b.xMin + 1f && Overlaps(v.worldBound, b),
                    NavigationMoveEvent.Direction.Up => v => v.worldBound.yMax <= b.yMin + 1f,
                    _ => null,
                };
                if (beyond == null) return;
                if (root.Query<VisualElement>().Where(v => v != from && Navigable(v) && beyond(v)).First() != null) return;
                e.StopPropagation();
                from.focusController?.IgnoreEvent(e);
            });

        // Focusable and laid out on screen (children of a hidden container keep their own display but get no size).
        static bool Navigable(VisualElement v) =>
            v.canGrabFocus && v.resolvedStyle.display != DisplayStyle.None && v.resolvedStyle.visibility == Visibility.Visible
            && v.worldBound.width > 0f && v.worldBound.height > 0f;

        static bool Overlaps(UnityEngine.Rect a, UnityEngine.Rect b) => a.yMax > b.yMin + 1f && a.yMin < b.yMax - 1f;
    }
}
