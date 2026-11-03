using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MHK.Core;
using MHK.Race;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The championship standings page (Race/Templates/Standings.uxml, one StandingRow.uxml per racer) of the results
    /// (after a cup race or an online set race) and of the pause menu (before the cup race under way): every racer by
    /// total points with the rank badge, the points of the last race, the places won or lost by it and, after the
    /// last cup race, the champion. The screens fill it when they bind and call <see cref="Show"/> when it opens.
    /// </summary>
    [Serializable]
    public sealed class StandingsTable
    {
        [SerializeField, Tooltip("One standings row (Race/Templates/StandingRow.uxml).")]
        VisualTreeAsset rowTemplate;

        [Header("Texts")]
        [SerializeField, Tooltip("Page title during the championship or set, and after its last race.")]
        string titleText = "Standings", finalTitleText = "Final standings";
        [SerializeField, Tooltip("Info line after a cup race ({0} = race number, {1} = races in the cup). The cup name is appended.")]
        string afterRaceFormat = "After race {0} of {1}";
        [SerializeField, Tooltip("Info line in the pause menu ({0} = number of the race under way, {1} = races in the cup). The cup name is appended.")]
        string beforeRaceFormat = "Before race {0} of {1}";
        [SerializeField, Tooltip("Info line after an online set race ({0} = race number, {1} = races in the set).")]
        string afterSetRaceFormat = "Online set | After race {0} of {1}";
        [SerializeField, Tooltip("Champion plate after the last cup race ({0} = name, {1} = points).")]
        string championFormat = "Champion: {0} with {1} points!";
        [SerializeField, Tooltip("Tooltips of the movement column: places won, places lost ({0} = places), the same place.")]
        string upTipFormat = "Up {0} since before the race", downTipFormat = "Down {0} since before the race",
            sameTip = "Same place as before the race";

        [Header("Timing (seconds)")]
        [SerializeField, Tooltip("Delay before the first row enters when the page opens.")]
        float rowsFirstDelay = 0.08f;
        [SerializeField, Tooltip("Delay between two rows.")]
        float rowsStep = 0.06f;
        [SerializeField, Min(1), Tooltip("Rows that enter one after another; the rows after them enter with the last of them.")]
        int staggeredRows = 10;

        const float WinnerSpinDegreesPerSecond = 30f;
        static readonly string[] MedalClasses = { "gold", "silver", "bronze" };
        static readonly string[] MoveClasses = { "up", "down", "same" };

        sealed class Line
        {
            public string name, characterId, kartId;
            public bool isHuman;
            public int points, gained, rank, move;
        }

        /// <summary>
        /// Fills <paramref name="page"/> with the standings after the cup race of <paramref name="results"/> (points
        /// already awarded) and returns the page title. The places moved show from the second race on, the champion
        /// after the last race.
        /// </summary>
        public string FillAfterCupRace(VisualElement page, GameSession session, IReadOnlyList<RaceResult> results, UnityEngine.Object context)
        {
            var gained = new Dictionary<RacerSetup, int>();
            foreach (var result in results)
                if (result.racer != null) gained[result.racer] = result.pointsGained;
            int Gained(RacerSetup racer) => gained.TryGetValue(racer, out var points) ? points : 0;
            var placeBefore = new Dictionary<RacerSetup, int>();
            foreach (var racer in session.racers.OrderByDescending(r => r.points - Gained(r))) placeBefore[racer] = placeBefore.Count + 1;
            var lines = session.Standings().Select((s, i) =>
            {
                var line = OfRacer(s, i + 1);
                line.gained = Gained(s);
                line.move = placeBefore[s] - line.rank;
                return line;
            }).ToList();
            var final = !session.HasNextRace;
            Fill(page, context, UiText.RaceOfCup(session, afterRaceFormat), lines, true, session.raceIndex > 0, final);
            return final ? finalTitleText : titleText;
        }

        /// <summary>Fills <paramref name="page"/> with the standings before the cup race under way (pause menu) and returns the page title.</summary>
        public string FillBeforeCupRace(VisualElement page, GameSession session, UnityEngine.Object context)
        {
            var lines = session.Standings().Select((s, i) => OfRacer(s, i + 1)).ToList();
            Fill(page, context, UiText.RaceOfCup(session, beforeRaceFormat), lines, false, false, false);
            return titleText;
        }

        /// <summary>
        /// Fills <paramref name="page"/> with the set standings after online set race <paramref name="setRace"/> of
        /// <paramref name="setSize"/> from the server's rows (set points of the racers of this race) and returns the
        /// page title. Racers with the same points share their place (the server breaks ties by who joined first, which
        /// the race does not know); the set winner is the lobby's to announce.
        /// </summary>
        public string FillAfterSetRace(VisualElement page, IReadOnlyList<RaceResult> results, int setRace, int setSize, UnityEngine.Object context)
        {
            var lines = results.Select(r => new Line
            {
                name = r.name, characterId = r.characterId, kartId = r.kartId, isHuman = r.isHuman,
                points = r.totalPoints, gained = r.pointsGained,
            }).ToList();
            var before = SharedRanks(lines.Select(l => l.points - l.gained).ToList());
            var after = SharedRanks(lines.Select(l => l.points).ToList());
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i].rank = after[i];
                lines[i].move = before[i] - after[i];
            }
            // Stable: a shared place keeps this race's order.
            lines = lines.OrderBy(l => l.rank).ToList();
            Fill(page, context, string.Format(afterSetRaceFormat, setRace, setSize), lines, true, setRace > 1, false);
            return setRace >= setSize ? finalTitleText : titleText;
        }

        /// <summary>The champion plate's text: the first of the standings and the points.</summary>
        public string ChampionText(GameSession session)
        {
            var winner = OfRacer(session.Standings().First(), 1);
            return string.Format(championFormat, winner.name, winner.points);
        }

        /// <summary>The filled page was just made visible: its rows enter one after another.</summary>
        public void Show(VisualElement page)
        {
            var rows = page.Q<VisualElement>("standings-rows");
            if (rows != null) UiAnim.Stagger(rows, rowsStep, rowsFirstDelay, "from-left", staggeredRows);
        }

        /// <summary>
        /// Rank cell of a result or standings row: the badge with <paramref name="rank"/>, gold / silver / bronze for
        /// <paramref name="medal"/> 1-3 (none otherwise) and the spinning burst behind the <paramref name="winner"/> (removed otherwise).
        /// </summary>
        public static void SetRank(VisualElement row, int rank, int medal, bool winner)
        {
            var burst = row.Q<VisualElement>("burst");
            if (winner)
            {
                burst.SetVisible(true);
                UiAnim.Spin(burst, WinnerSpinDegreesPerSecond);
            }
            else burst.RemoveFromHierarchy();
            row.Q<VisualElement>("badge").SetExclusiveClass(medal - 1, MedalClasses);
            row.SetText("badge-label", rank.ToString(CultureInfo.InvariantCulture));
        }

        static Line OfRacer(RacerSetup s, int rank) => new()
        {
            name = string.IsNullOrEmpty(s.displayName) ? ContentDisplay.CharacterName(s.characterId) : s.displayName,
            characterId = s.characterId, kartId = s.kartId, isHuman = s.isHuman, points = s.points, rank = rank,
        };

        // Competition ranking (1, 2, 2, 4) by points, highest first.
        static int[] SharedRanks(IReadOnlyList<int> points)
        {
            var ranks = new int[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                ranks[i] = 1;
                for (var j = 0; j < points.Count; j++)
                    if (points[j] > points[i]) ranks[i]++;
            }
            return ranks;
        }

        void Fill(VisualElement page, UnityEngine.Object context, string info, List<Line> lines, bool showGain, bool showMove, bool final)
        {
            var panel = UiTree.Require<VisualElement>(page, "standings-panel", context);
            panel.EnableInClassList("no-gain", !showGain);
            panel.EnableInClassList("no-move", !showMove);
            panel.SetText("standings-info", info);
            var rows = UiTree.Require<VisualElement>(page, "standings-rows", context);
            rows.Clear();
            VisualElement me = null;
            foreach (var line in lines)
            {
                var row = UiTree.Spawn<VisualElement>(rowTemplate, rows);
                if (row == null) break;
                FillRow(row, line, final && line.rank == 1);
                if (line.isHuman) me ??= row;
            }
            if (me != null) UiTree.Require<ScrollView>(page, "standings-scroll", context).ScrollToWhenLaidOut(me);
            var champion = UiTree.Require<VisualElement>(page, "standings-champion", context);
            var crowned = final && lines.Count > 0;
            champion.SetVisible(crowned);
            if (crowned) champion.SetText("standings-champion-label", string.Format(championFormat, lines[0].name, lines[0].points));
        }

        void FillRow(VisualElement row, Line line, bool champion)
        {
            row.EnableInClassList("me", line.isHuman);
            SetRank(row, line.rank, line.rank, champion);
            row.SetText("name", line.name);
            ContentDisplay.SetDriver(row, line.characterId, line.kartId);
            var move = row.Q<VisualElement>("move");
            var places = Math.Abs(line.move);
            move.SetExclusiveClass(line.move > 0 ? 0 : line.move < 0 ? 1 : 2, MoveClasses);
            move.Q<VisualElement>("move-icon")?.SetIcon(line.move > 0 ? "arrow_upward" : line.move < 0 ? "arrow_downward" : "remove");
            move.SetText("move-label", places > 0 ? places.ToString(CultureInfo.InvariantCulture) : "");
            move.tooltip = line.move > 0 ? string.Format(upTipFormat, places) : line.move < 0 ? string.Format(downTipFormat, places) : sameTip;
            row.SetText("gain", "+" + line.gained.ToString(CultureInfo.InvariantCulture));
            row.SetText("total", line.points.ToString(CultureInfo.InvariantCulture));
        }
    }
}
