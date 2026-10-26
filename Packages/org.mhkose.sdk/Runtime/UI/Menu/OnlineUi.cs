using System;
using System.Collections.Generic;
using System.Globalization;
using MHK.Core;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Shared bits of the online pages: short names of mod sets ("MHK", "MHK + TINcan", "MHK 2"), the settings of a lobby as
    /// <see cref="InfoChips"/>, ping bars and engine class names.
    /// </summary>
    public static class OnlineUi
    {
        /// <summary>The four original games: together they are "MHK".</summary>
        static readonly string[] BaseGames = { "mhk2", "mhk3", "mhke", "mhkt" };

        // Short names of the known packs; other packs use their title.
        static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
        {
            { "mhk2", "MHK 2" }, { "mhk3", "MHK 3" }, { "mhke", "MHK Extra" }, { "mhkt", "MHK Thunder" }, { "tincan", "TINcan" },
        };

        /// <summary>Most pack names a set name lists before it becomes "N mods".</summary>
        const int MaxNamedPacks = 2;

        /// <summary>
        /// Fills <paramref name="container"/> (a <c>.tabs</c> row) with segmented chips (icon, label, tooltip; a null icon
        /// = label only) with exactly one <c>.selected</c>; picking one moves the selection and calls <paramref name="onPick"/>.
        /// Left / Right move between them like any row of buttons.
        /// </summary>
        public static List<Button> Segmented(VisualElement container, IReadOnlyList<(string icon, string text, string tip)> items, int selected,
            Action<int> onPick)
        {
            container.Clear();
            var buttons = new List<Button>();
            for (var i = 0; i < items.Count; i++)
            {
                var (icon, text, tip) = items[i];
                var chip = new Button { tooltip = tip ?? "" };
                chip.AddToClassList("chip");
                chip.AddToClassList("seg-chip");
                if (!string.IsNullOrEmpty(icon))
                {
                    var iconElement = new VisualElement { pickingMode = PickingMode.Ignore };
                    iconElement.AddToClassList("icon");
                    iconElement.AddToClassList("i-" + icon);
                    chip.Add(iconElement);
                }
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("seg-chip-label");
                chip.Add(label);
                chip.EnableInClassList("selected", i == selected);
                var index = i;
                chip.clicked += () =>
                {
                    foreach (var other in buttons) other.EnableInClassList("selected", other == chip);
                    onPick(index);
                };
                container.Add(chip);
                buttons.Add(chip);
            }
            return buttons;
        }

        /// <summary>Short name of one pack: "MHK 2", "TINcan", else its title.</summary>
        public static string PackName(string packId) =>
            ShortNames.TryGetValue(packId ?? "", out var name) ? name : UiText.GameName(packId);

        /// <summary>
        /// Short name of a set of packs: the four original games are "MHK", others are added by name ("MHK + TINcan"),
        /// more than two names become "N mods". <see cref="SetTooltip"/> lists them all.
        /// </summary>
        public static string SetName(IReadOnlyList<string> packs)
        {
            if (packs == null || packs.Count == 0) return "-";
            var names = new List<string>();
            var hasBase = Array.TrueForAll(BaseGames, g => Contains(packs, g));
            if (hasBase) names.Add("MHK");
            foreach (var p in packs)
                if (!(hasBase && Array.IndexOf(BaseGames, p.ToLowerInvariant()) >= 0)) names.Add(PackName(p));
            if (names.Count > MaxNamedPacks)
                return hasBase ? $"MHK + {names.Count - 1} mods" : $"{names.Count} mods";
            return string.Join(" + ", names);
        }

        /// <summary>Every pack of the set by name, one per line (tooltips).</summary>
        public static string SetTooltip(IReadOnlyList<string> packs)
        {
            var names = new List<string>();
            if (packs != null)
                foreach (var p in packs) names.Add(UiText.GameName(p));
            return string.Join("\n", names);
        }

        static bool Contains(IReadOnlyList<string> list, string id)
        {
            foreach (var s in list)
                if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string EngineClassName(int engineClass)
        {
            var classes = GameRoot.Instance.Config.difficulties;
            return engineClass >= 0 && engineClass < classes.Length ? classes[engineClass].label : "";
        }

        /// <summary>Icon and name of a lobby mode.</summary>
        public static (string icon, string text) Mode(RoomSettings s) => s.mode switch
        {
            RoomMode.BattleSet => ("swords", "Battles"),
            RoomMode.Championship => ("emoji_events", CupName(s.cupId)),
            _ => ("sports_score", "Races"),
        };

        static string CupName(string cupId)
        {
            var cup = GameRoot.Instance.Content.Get<Modding.CupDefinition>(cupId);
            return cup != null ? cup.DisplayName : "Championship";
        }

        /// <summary>
        /// The rules of a lobby as chips into <paramref name="row"/> (cleared first): mode, anticheat, engine class, laps,
        /// items, AI and, for private rooms, track pick and races per set. <paramref name="detailed"/> adds the last two.
        /// </summary>
        public static void SettingsChips(VisualElement row, RoomSettings s, bool detailed)
        {
            row.Clear();
            var (modeIcon, modeText) = Mode(s);
            InfoChips.Add(row, modeIcon, modeText, s.mode == RoomMode.Championship ? "Championship" : null);
            AnticheatChip(row, s.anticheat);
            InfoChips.Add(row, "speed", EngineClassName(s.engineClass), "Engine class");
            if (s.mode != RoomMode.BattleSet)
                InfoChips.Add(row, "flag", s.laps == 0 ? "Track laps" : s.laps == 1 ? "1 lap" : s.laps.ToString(CultureInfo.InvariantCulture) + " laps",
                    s.laps == 0 ? "Every track's own lap count" : "Laps per race");
            InfoChips.Add(row, "redeem", s.items ? s.itemOdds.Count > 0 ? "Own odds" : "Items" : "No items",
                s.items ? s.itemOdds.Count > 0 ? "Items with the room's own odds" : "Items with standard odds" : "No items",
                s.items ? InfoChipState.Normal : InfoChipState.Off);
            if (!detailed) return;
            InfoChips.Add(row, "smart_toy", "AI", s.aiWhenAlone ? "Computer drivers race the owner while they are alone" : "No computer drivers",
                s.aiWhenAlone ? InfoChipState.Normal : InfoChipState.Off);
            if (s.mode != RoomMode.Championship)
            {
                InfoChips.Add(row, s.OwnerPicks ? "edit" : "how_to_vote", s.OwnerPicks ? "Owner picks" : "Vote", "How the next track is chosen");
                InfoChips.Add(row, "repeat", s.racesPerSet == 1 ? "1 race" : s.racesPerSet.ToString(CultureInfo.InvariantCulture) + " races", "Races per set");
            }
        }

        public static VisualElement AnticheatChip(VisualElement row, bool on, bool iconOnly = false) =>
            InfoChips.Add(row, on ? "shield" : "remove_moderator", iconOnly ? null : on ? "Anticheat" : "No anticheat",
                on ? "Anticheat: players' games check each other's karts, cheaters are removed from the race."
                    : "No anticheat: nobody checks the karts.", on ? InfoChipState.Good : InfoChipState.Off);

        /// <summary>Ping as signal bars: the icon name for <paramref name="ms"/> (0 = unknown).</summary>
        public static string PingIcon(int ms) => ms <= 0 ? "signal_cellular_alt_1_bar" : ms <= 80 ? "signal_cellular_alt" : ms <= 160 ? "signal_cellular_alt_2_bar" : "signal_cellular_alt_1_bar";

        /// <summary>Ping state: good up to 80 ms, bad above 160 ms (the anticheat stops counting a player at 200 ms).</summary>
        public static InfoChipState PingState(int ms) => ms <= 0 ? InfoChipState.Off : ms <= 80 ? InfoChipState.Good : ms <= 160 ? InfoChipState.Normal : InfoChipState.Bad;

        /// <summary>
        /// Keyboard / gamepad moves on the whole screen <paramref name="root"/> (footer included): the nearest focusable element
        /// whose centre lies beyond the focused element's edge, nearest row (or column) first, then the least sideways, without
        /// wrapping around. The default navigation only takes elements straight in line, wraps around, and is thrown off by
        /// grown (scaled) selected chips and cards. <paramref name="route"/> may decide first: it returns where to go, null for
        /// the nearest, or the element itself to stay. Text fields keep Left / Right for the caret until it reaches their edge.
        /// </summary>
        public static void Spatial(VisualElement root, Func<VisualElement, NavigationMoveEvent.Direction, VisualElement> route = null)
        {
            // Text fields move the focus themselves (wrapping around) before the event bubbles up: their moves are taken on
            // the way down, every other one on the way up (after controls such as SettingChoice used Left / Right).
            root.RegisterCallback<NavigationMoveEvent>(e => Move(e, true), TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationMoveEvent>(e => Move(e, false));

            void Move(NavigationMoveEvent e, bool fields)
            {
                if (e.target is not VisualElement from || e.direction is NavigationMoveEvent.Direction.None
                        or NavigationMoveEvent.Direction.Next or NavigationMoveEvent.Direction.Previous)
                    return;
                var field = from as TextField ?? from.GetFirstAncestorOfType<TextField>();
                if ((field != null) != fields) return;
                var to = route?.Invoke(from, e.direction);
                if (to == null)
                {
                    var length = field?.value?.Length ?? 0;
                    if (field != null && ((e.direction == NavigationMoveEvent.Direction.Left && field.cursorIndex > 0)
                                          || (e.direction == NavigationMoveEvent.Direction.Right && field.cursorIndex < length)))
                        return;
                    to = Nearest(root, field ?? from, e.direction) ?? from;
                }
                if (to != from && !CanFocus(to)) to = from;
                if (to != from) to.Focus();
                e.StopImmediatePropagation();
                from.focusController?.IgnoreEvent(e);
            }
        }

        // Unscaled rectangle on the panel (layout in the parent's space: a grown selected card keeps its grid cell).
        static Rect Rect(VisualElement v) => v.parent != null ? v.parent.LocalToWorld(v.layout) : v.worldBound;

        static VisualElement Nearest(VisualElement root, VisualElement from, NavigationMoveEvent.Direction d)
        {
            var f = Rect(from);
            var vertical = d is NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Down;
            var sign = d is NavigationMoveEvent.Direction.Down or NavigationMoveEvent.Direction.Right ? 1f : -1f;
            VisualElement best = null;
            var bestScore = float.MaxValue;
            root.Query<VisualElement>().Where(v => v != from && v.tabIndex >= 0 && CanFocus(v) && !from.Contains(v)).ForEach(v =>
            {
                var c = Rect(v);
                if (c.width <= 0f || c.height <= 0f) return;
                // Ahead: the candidate starts beyond the focused element's far edge (a quarter of the smaller size of overlap
                // allowed: grid rows may overlap a little).
                var ahead = vertical ? (sign > 0 ? c.yMin - f.yMax : f.yMin - c.yMax) : (sign > 0 ? c.xMin - f.xMax : f.xMin - c.xMax);
                if (ahead < -0.25f * (vertical ? Mathf.Min(f.height, c.height) : Mathf.Min(f.width, c.width))) return;
                var gap = Mathf.Max(0f, ahead);
                var side = vertical
                    ? Mathf.Max(0f, Mathf.Max(c.xMin - f.xMax, f.xMin - c.xMax))
                    : Mathf.Max(0f, Mathf.Max(c.yMin - f.yMax, f.yMin - c.yMax));
                var across = vertical ? Mathf.Abs(c.center.x - f.center.x) : Mathf.Abs(c.center.y - f.center.y);
                var score = gap + side * 2f + across * 0.02f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = v;
                }
            });
            return best;
        }

        /// <summary>The element takes the focus now (shown, enabled, focusable).</summary>
        public static bool CanFocus(VisualElement v) =>
            v != null && v.panel != null && v.canGrabFocus && v.resolvedStyle.display != DisplayStyle.None && v.resolvedStyle.visibility == Visibility.Visible;

        /// <summary>The selected chip of a <see cref="Segmented"/> row, else its first.</summary>
        public static VisualElement SelectedChip(VisualElement row) => row.Q(className: "selected") ?? (row.childCount > 0 ? row[0] : null);
    }
}
