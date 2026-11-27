using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Whole-number setting of a row (Templates/NumberRow.uxml): − and + buttons (step 1, held down they repeat) around a
    /// text field. Left / Right anywhere in the input step the value too (held gamepad sticks and keys repeat), Up / Down
    /// leave the row. Typed text applies on Enter or when the field loses focus: a number outside the range is clamped, other
    /// text is refused (the old value returns), and either way the range hint flashes. Touch opens the number keypad. An
    /// optional zero text stands for 0 (laps: "Track" for the track's own count) and may be typed too.
    /// </summary>
    public sealed class NumberInput
    {
        const long RepeatDelayMs = 380, RepeatIntervalMs = 70, HintMs = 1800;
        const string LimitClass = "at-limit";
        const string WarnClass = "warn";

        readonly int min, max;
        readonly Func<int> get;
        readonly Action<int> set;
        readonly Action click;
        readonly Button minus, plus;
        readonly TextField field;
        readonly Label hint;
        readonly string zeroText;
        IVisualElementScheduledItem hintTimer;

        /// <summary>The text field (focus it to type a value).</summary>
        public TextField Field => field;

        /// <summary>Shows the current value again (after something else changed it, or the getter reads another value now).</summary>
        public void Refresh() => Show(Mathf.Clamp(get(), min, max));

        /// <summary>
        /// Binds the number input of <paramref name="row"/> (number-minus, number-field, number-plus, number-hint) to the
        /// value <paramref name="get"/> / <paramref name="set"/> between <paramref name="min"/> and <paramref name="max"/>.
        /// <paramref name="rangeFormat"/> ({0} = min, {1} = max) is the hint and the field's tooltip, <paramref name="click"/>
        /// (optional) plays the menu click on every change, <paramref name="zeroText"/> (optional) is shown instead of 0.
        /// </summary>
        public static NumberInput Bind(VisualElement row, int min, int max, Func<int> get, Action<int> set, string rangeFormat, Action click = null,
            string zeroText = null) =>
            new(row, min, max, get, set, rangeFormat, click, zeroText);

        NumberInput(VisualElement row, int min, int max, Func<int> get, Action<int> set, string rangeFormat, Action click, string zeroText)
        {
            this.min = min;
            this.max = Mathf.Max(min, max);
            this.get = get;
            this.set = set;
            this.click = click;
            this.zeroText = string.IsNullOrEmpty(zeroText) ? null : zeroText;
            minus = row.Q<Button>("number-minus");
            plus = row.Q<Button>("number-plus");
            field = row.Q<TextField>("number-field");
            hint = row.Q<Label>("number-hint");
            var range = string.Format(CultureInfo.InvariantCulture, rangeFormat, min, this.max);
            hint.text = range;
            hint.RemoveFromClassList(WarnClass);
            field.tooltip = range;
            field.isDelayed = true;
            field.maxLength = Mathf.Max(this.max.ToString(CultureInfo.InvariantCulture).Length + 1, this.zeroText?.Length ?? 0);
            field.keyboardType = TouchScreenKeyboardType.NumberPad;
            // Repeating clickables: one step on press, then steps while held.
            minus.clickable = new Clickable(() => Step(-1), RepeatDelayMs, RepeatIntervalMs);
            plus.clickable = new Clickable(() => Step(1), RepeatDelayMs, RepeatIntervalMs);
            field.RegisterValueChangedCallback(e => Commit(e.newValue));
            row.RegisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            Show(Mathf.Clamp(get(), min, this.max));
        }

        void Step(int delta)
        {
            var v = Mathf.Clamp(get() + delta, min, max);
            if (v == get())
            {
                Show(v);
                return;
            }
            Apply(v);
        }

        void Commit(string text)
        {
            var t = text?.Trim() ?? "";
            var typed = 0;
            if (!(zeroText != null && string.Equals(t, zeroText, StringComparison.OrdinalIgnoreCase))
                && !int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out typed))
            {
                Show(get());
                FlashHint();
                return;
            }
            var v = Mathf.Clamp(typed, min, max);
            if (v != typed) FlashHint();
            if (v == get()) Show(v);
            else Apply(v);
        }

        void Apply(int v)
        {
            click?.Invoke();
            set(v);
            Show(v);
        }

        void Show(int v)
        {
            field.SetValueWithoutNotify(v == 0 && zeroText != null ? zeroText : v.ToString(CultureInfo.InvariantCulture));
            minus.EnableInClassList(LimitClass, v <= min);
            plus.EnableInClassList(LimitClass, v >= max);
        }

        void FlashHint()
        {
            hint.AddToClassList(WarnClass);
            hintTimer?.Pause();
            hintTimer = hint.schedule.Execute(() => hint.RemoveFromClassList(WarnClass));
            hintTimer.ExecuteLater(HintMs);
        }

        // Left / Right step the value wherever the focus is in the input (a typed value applies first).
        void OnNavigate(NavigationMoveEvent e)
        {
            var delta = e.direction switch
            {
                NavigationMoveEvent.Direction.Left => -1,
                NavigationMoveEvent.Direction.Right => 1,
                _ => 0,
            };
            if (delta == 0) return;
            if (e.target is VisualElement t && (t == field || field.Contains(t))) Commit(field.text);
            Step(delta);
            e.StopPropagation();
            (e.target as Focusable)?.focusController?.IgnoreEvent(e);
        }
    }
}
