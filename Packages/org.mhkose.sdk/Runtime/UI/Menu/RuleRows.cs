using System;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Setting rows built by code in one look (<c>.rule-row</c> in MHKMenu.uss), shared by the race settings and the room
    /// settings. Each row has an icon and a bold label, its control sits at the right edge. Controls are a value pill (<see cref="SettingChoice"/>,
    /// cycling up to three values, a dropdown list for more), a link pill that opens another screen, a switch, or a number
    /// input (<see cref="NumberInput"/>, typing and held - / +). Rows of Templates/SettingRow.uxml added to the same
    /// container (<see cref="TrackVariantRows"/>) take the look with <see cref="Adopt"/>. <see cref="Route"/> moves Up / Down
    /// row by row for <see cref="OnlineUi.Spatial"/>.
    /// </summary>
    public sealed class RuleRows
    {
        /// <summary>Class of every rule row (rows also keep <c>.setting-row</c> for the value pill looks).</summary>
        public const string RowClass = "rule-row";

        readonly VisualElement host;
        readonly Action click;
        readonly string rangeFormat;

        /// <summary>The element holding the rows.</summary>
        public VisualElement Container { get; }

        /// <summary>A control changed its value (after the setter ran).</summary>
        public event Action Changed;

        /// <summary>
        /// Rows go into <paramref name="container"/>; <paramref name="host"/> (the screen root) holds the open dropdown lists,
        /// <paramref name="click"/> plays the menu click, <paramref name="rangeFormat"/> ({0} = min, {1} = max) is the hint of
        /// the number inputs.
        /// </summary>
        public RuleRows(VisualElement container, VisualElement host, Action click, string rangeFormat)
        {
            Container = container;
            this.host = host;
            this.click = click;
            this.rangeFormat = rangeFormat;
        }

        /// <summary>Removes every row (UI Builder samples included).</summary>
        public void Clear() => Container.Clear();

        /// <summary>An empty row with an icon and a label, the explanation <paramref name="tip"/> is the row's tooltip.</summary>
        public VisualElement Row(string icon, string label, string tip)
        {
            var row = new VisualElement { tooltip = tip ?? "" };
            row.AddToClassList("setting-row");
            row.AddToClassList(RowClass);
            row.Add(Icon(icon));
            var l = new Label(label) { name = "setting-label", pickingMode = PickingMode.Ignore };
            l.AddToClassList("setting-label");
            l.AddToClassList("rule-label");
            row.Add(l);
            Container.Add(row);
            return row;
        }

        /// <summary>Gives a row of Templates/SettingRow.uxml the rule row look with <paramref name="icon"/>.</summary>
        public static void Adopt(VisualElement row, string icon)
        {
            if (row.ClassListContains(RowClass)) return;
            row.AddToClassList(RowClass);
            row.Insert(0, Icon(icon));
            row.Q<Label>("setting-label")?.AddToClassList("rule-label");
        }

        /// <summary>A value pill row choosing one of <paramref name="count"/> values (<see cref="SettingChoice"/>).</summary>
        public SettingChoice Choice(string icon, string label, string tip, Func<int> count, Func<int> index, Func<int, string> text,
            Action<int> select, Func<int, string> valueTip = null)
        {
            var row = Row(icon, label, tip);
            row.Add(ValueButton());
            return new SettingChoice(row, host, count, index, text, i =>
            {
                select(i);
                Changed?.Invoke();
            }, valueTip, click);
        }

        /// <summary>A value pill row showing <paramref name="value"/> that opens another screen (<paramref name="open"/>).</summary>
        public Button Link(string icon, string label, string tip, string value, Action open)
        {
            var row = Row(icon, label, tip);
            row.AddToClassList(SettingChoice.LinkClass);
            var button = ValueButton();
            row.Add(button);
            row.SetText("setting-value-label", value);
            button.clicked += () =>
            {
                click?.Invoke();
                open();
            };
            return button;
        }

        /// <summary>An on / off switch row.</summary>
        public Button Switch(string icon, string label, string tip, Func<bool> get, Action<bool> set)
        {
            var row = Row(icon, label, tip);
            var button = new Button();
            button.AddToClassList("mhk-switch");
            var knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.AddToClassList("switch-knob");
            button.Add(knob);
            button.EnableInClassList("on", get());
            button.clicked += () =>
            {
                click?.Invoke();
                set(!get());
                button.EnableInClassList("on", get());
                Changed?.Invoke();
            };
            row.Add(button);
            return button;
        }

        /// <summary>
        /// A whole-number row (<see cref="NumberInput"/>) between <paramref name="min"/> and <paramref name="max"/>;
        /// <paramref name="zeroText"/> (optional) stands for 0, <paramref name="range"/> (optional) replaces the hint format.
        /// </summary>
        public VisualElement Number(string icon, string label, string tip, int min, int max, Func<int> get, Action<int> set,
            string zeroText = null, string range = null)
        {
            var row = Row(icon, label, tip);
            BindNumber(row, min, max, get, set, zeroText, range);
            return row;
        }

        /// <summary>Puts a new number input into <paramref name="row"/> in place of the one it has (a range that changed).</summary>
        public NumberInput BindNumber(VisualElement row, int min, int max, Func<int> get, Action<int> set, string zeroText = null,
            string range = null)
        {
            row.Q(className: "number-input")?.RemoveFromHierarchy();
            var box = new VisualElement();
            box.AddToClassList("number-input");
            var hint = new Label { name = "number-hint" };
            hint.AddToClassList("number-hint");
            var field = new TextField { name = "number-field" };
            field.AddToClassList("acc-input");
            field.AddToClassList("number-field");
            box.Add(hint);
            box.Add(StepButton("number-minus", "remove"));
            box.Add(field);
            box.Add(StepButton("number-plus", "add"));
            row.Add(box);
            return NumberInput.Bind(box, min, max, get, v =>
            {
                set(v);
                Changed?.Invoke();
            }, range ?? rangeFormat, click, zeroText);
        }

        /// <summary>
        /// Up / Down from a control of a row go to the first control of the next shown row that has one (controls sit at
        /// different heights and widths, so the nearest element is not always the next row). Null for other moves, outside
        /// the rows and past the last row.
        /// </summary>
        public VisualElement Route(VisualElement from, NavigationMoveEvent.Direction d)
        {
            if (d is not (NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Down)) return null;
            var row = RowOf(from);
            if (row == null) return null;
            var step = d == NavigationMoveEvent.Direction.Down ? 1 : -1;
            for (var i = Container.IndexOf(row) + step; i >= 0 && i < Container.childCount; i += step)
            {
                var target = FirstControl(Container[i]);
                if (target != null) return target;
            }
            return null;
        }

        /// <summary>The row holding <paramref name="v"/>, null outside the rows.</summary>
        public VisualElement RowOf(VisualElement v)
        {
            while (v != null && v.parent != Container) v = v.parent;
            return v;
        }

        /// <summary>The first control of <paramref name="row"/> that takes the focus now, null when none does.</summary>
        public static VisualElement FirstControl(VisualElement row) => row.Query<Button>().Where(OnlineUi.CanFocus).First();

        static VisualElement Icon(string icon)
        {
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.AddToClassList("icon");
            i.AddToClassList("rule-icon");
            i.AddToClassList("i-" + icon);
            return i;
        }

        // The value button of Templates/SettingRow.uxml, the value between two arrows.
        static Button ValueButton()
        {
            var button = new Button();
            button.AddToClassList("mhk-button");
            button.AddToClassList("small");
            button.AddToClassList("setting-value");
            button.Add(Arrow("setting-prev", "chevron_left"));
            var value = new Label { name = "setting-value-label", pickingMode = PickingMode.Ignore };
            value.AddToClassList("button-label");
            button.Add(value);
            button.Add(Arrow("setting-next", "chevron_right"));
            return button;
        }

        static VisualElement Arrow(string name, string icon)
        {
            var arrow = new VisualElement { name = name };
            arrow.AddToClassList("setting-arrow");
            arrow.AddToClassList(name);
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.AddToClassList("icon");
            i.AddToClassList("i-" + icon);
            arrow.Add(i);
            return arrow;
        }

        static Button StepButton(string name, string icon)
        {
            var b = new Button { name = name };
            b.AddToClassList("mhk-button");
            b.AddToClassList("small");
            b.AddToClassList("secondary");
            b.AddToClassList("icon-only");
            b.AddToClassList("number-step");
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.AddToClassList("icon");
            i.AddToClassList("i-" + icon);
            b.Add(i);
            return b;
        }
    }
}
