using System;
using System.Globalization;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>Display texts shared by the UI: race clocks, ordinals, signed deltas and content names (numbers in the invariant culture).</summary>
    public static class UiText
    {
        /// <summary>Clock text of a missing time.</summary>
        public const string NoClock = "--:--.--";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>Race time as "mm:ss.cc", rounded to whole centiseconds first (so 119.996 s is "02:00.00").</summary>
        public static string Clock(float seconds)
        {
            var centis = Centis(seconds);
            if (centis < 0) return NoClock;
            Span<char> text = stackalloc char[24];
            var n = WriteMinutesSeconds(text, centis / 6000, centis / 100 % 60);
            text[n] = '.';
            WriteTwoDigits(text.Slice(n + 1), centis % 100);
            return new string(text.Slice(0, n + 3));
        }

        /// <summary>Whole centiseconds of <paramref name="seconds"/> (rounded), -1 for negative or missing times.</summary>
        public static int Centis(float seconds) => seconds >= 0f ? Mathf.RoundToInt(seconds * 100f) : -1;

        /// <summary>Writes "mm:ss" (minutes with at least two digits) and returns the number of chars written.</summary>
        public static int WriteMinutesSeconds(Span<char> destination, int minutes, int seconds)
        {
            minutes.TryFormat(destination, out var n, "00", Invariant);
            destination[n] = ':';
            WriteTwoDigits(destination.Slice(n + 1), seconds);
            return n + 3;
        }

        /// <summary>Writes <paramref name="value"/> (0 to 99) as two digits.</summary>
        public static void WriteTwoDigits(Span<char> destination, int value)
        {
            destination[0] = (char)('0' + value / 10 % 10);
            destination[1] = (char)('0' + value % 10);
        }

        /// <summary>Shows a song position or length as "mm:ss" (whole seconds, rounded down) without allocating.</summary>
        public static void SetMinutesSeconds(this TextElement label, float seconds)
        {
            var whole = Mathf.Max(0, Mathf.FloorToInt(seconds));
            Span<char> text = stackalloc char[16];
            label.SetText(text.Slice(0, WriteMinutesSeconds(text, whole / 60, whole % 60)));
        }

        /// <summary>Shows <paramref name="value"/> after <paramref name="prefix"/> ("/6", "x3") without allocating.</summary>
        public static void SetPrefixedNumber(this TextElement label, char prefix, int value)
        {
            Span<char> text = stackalloc char[12];
            text[0] = prefix;
            value.TryFormat(text.Slice(1), out var n, default, Invariant);
            label.SetText(text.Slice(0, n + 1));
        }

        /// <summary>Shows "value/total" ("2/3") without allocating.</summary>
        public static void SetRatio(this TextElement label, int value, int total)
        {
            Span<char> text = stackalloc char[24];
            value.TryFormat(text, out var n, default, Invariant);
            text[n] = '/';
            total.TryFormat(text.Slice(n + 1), out var m, default, Invariant);
            label.SetText(text.Slice(0, n + 1 + m));
        }

        /// <summary>Signed seconds with two decimals ("-0.42", "+1.05").</summary>
        public static string SignedSeconds(float seconds) =>
            (seconds < 0f ? "-" : "+") + Mathf.Abs(seconds).ToString("0.00", Invariant);

        /// <summary>English ordinal suffix: "st", "nd", "rd" or "th" (11th to 13th, 111th, 21st, 22nd ...).</summary>
        public static string OrdinalSuffix(int n)
        {
            var lastTwo = n % 100;
            if (lastTwo >= 11 && lastTwo <= 13) return "th";
            return (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        }

        /// <summary>English ordinal ("1st", "21st", "12th").</summary>
        public static string Ordinal(int n) => n.ToString(Invariant) + OrdinalSuffix(n);

        /// <summary>Percentage with at most one decimal and a space before the sign ("75 %", "3.8 %").</summary>
        public static string Percent(float percent) => percent.ToString("0.#", Invariant) + " %";

        /// <summary>Human-readable name of a content source (the title of the pack with that id, e.g. "Moorhuhn Kart 2").</summary>
        public static string GameName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "Mod";
            var root = GameRoot.Instance;
            if (root != null && root.Content != null)
                foreach (var p in root.Content.Packs)
                    if (string.Equals(p.packId, id, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.title))
                        return p.title;
            return id;
        }

        /// <summary>The track's game name, plus <paramref name="variantFormat"/> ({0} = variant) when it has a variant.</summary>
        public static string GameWithVariant(TrackDefinition track, string variantFormat)
        {
            var game = GameName(track.SourceGame);
            return string.IsNullOrEmpty(track.variant) ? game : game + string.Format(variantFormat, track.variant);
        }

        /// <summary>Separator between parts of one line of UI text ("Race 2 of 4 | Cup name").</summary>
        public const string Separator = " | ";

        /// <summary>"Race 2 of 4 | Cup name" (<paramref name="format"/> gets the race number and the race count).</summary>
        public static string RaceOfCup(GameSession session, string format)
        {
            var text = string.Format(format, session.raceIndex + 1, session.trackIds.Count);
            var cup = GameRoot.Instance.Content.Get<CupDefinition>(session.cupId);
            return cup != null ? text + Separator + cup.DisplayName : text;
        }

        /// <summary>Frame-rate cap text: <paramref name="format"/> ({0} = fps), or <paramref name="unlimited"/> for 0.</summary>
        public static string Fps(int fps, string format, string unlimited) => fps > 0 ? string.Format(format, fps) : unlimited;
    }
}
