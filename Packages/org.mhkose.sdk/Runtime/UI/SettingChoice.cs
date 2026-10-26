using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Value button of a setting row (Templates/SettingRow.uxml) choosing one of a few values. Up to three it cycles: the left
    /// arrow and a right click step back, the rest of the button steps on. More values open a <see cref="ChoiceList"/>. Left
    /// and Right navigation step in both looks.
    /// </summary>
    public sealed class SettingChoice
    {
        /// <summary>Most values a row cycles through, more open a dropdown list.</summary>
        public const int MaxCycleValues = 3;
        /// <summary>Row class of the dropdown look (value and a down arrow).</summary>
        public const string DropdownClass = "setting-dropdown";
        /// <summary>Row class of a value that opens another screen (value and a right arrow).</summary>
        public const string LinkClass = "setting-link";

        const string ValueLabelName = "setting-value-label";
        const string PrevArrowName = "setting-prev";

        readonly VisualElement row, host, prevArrow;
        readonly Label label;
        readonly Func<int> count, index;
        readonly Func<int, string> text, tip;
        readonly Action<int> select;
        readonly Action click;

        /// <summary>The row's value button.</summary>
        public Button Button { get; }

        /// <summary>
        /// Open a list even for a few values, and let Left / Right move the focus instead of stepping (choices whose number
        /// varies, or compact dropdowns side by side such as leaderboard filters).
        /// </summary>
        public bool AlwaysList { get; set; }

        bool IsDropdown => AlwaysList || count() > MaxCycleValues;

        /// <summary>
        /// Binds <paramref name="row"/> to the values 0 to <paramref name="count"/> - 1: <paramref name="index"/> is the current one,
        /// <paramref name="text"/> and <paramref name="tip"/> (optional tooltip) describe a value, <paramref name="select"/> applies
        /// one. <paramref name="host"/> (the screen root) holds the open list, <paramref name="click"/> plays the click sound.
        /// </summary>
        public SettingChoice(VisualElement row, VisualElement host, Func<int> count, Func<int> index, Func<int, string> text,
            Action<int> select, Func<int, string> tip = null, Action click = null)
        {
            this.row = row;
            this.host = host;
            this.count = count;
            this.index = index;
            this.text = text;
            this.select = select;
            this.tip = tip;
            this.click = click;
            Button = row.Q<Button>() ?? new Button();
            label = Button.Q<Label>(ValueLabelName) ?? Button.Q<Label>() ?? new Label();
            prevArrow = Button.Q(PrevArrowName);
            Button.clickable = new Clickable((Action<EventBase>)OnClick);
            Button.RegisterCallback<PointerDownEvent>(OnPointerDown);
            Button.RegisterCallback<NavigationMoveEvent>(OnNavigate);
            Refresh();
        }

        /// <summary>Shows the current value again (after something else changed it).</summary>
        public void Refresh()
        {
            row.EnableInClassList(DropdownClass, IsDropdown);
            var i = index();
            label.text = text(i);
            if (tip != null) Button.tooltip = tip(i);
        }

        /// <summary>Applies the value <paramref name="steps"/> after (or before) the current one, wrapping around.</summary>
        public void Step(int steps)
        {
            var n = count();
            if (n <= 0) return;
            click?.Invoke();
            select(((index() + steps) % n + n) % n);
            Refresh();
        }

        void OnClick(EventBase e)
        {
            if (IsDropdown) OpenList();
            else Step(OnPrevArrow(e) ? -1 : 1);
        }

        bool OnPrevArrow(EventBase e)
        {
            if (prevArrow == null) return false;
            Vector2 position;
            if (e is IPointerEvent p) position = p.position;
            else if (e is IMouseEvent m) position = m.mousePosition;
            else return false;
            return position.x <= prevArrow.worldBound.xMax;
        }

        void OnPointerDown(PointerDownEvent e)
        {
            if (e.button != (int)MouseButton.RightMouse) return;
            Step(-1);
            e.StopPropagation();
        }

        void OnNavigate(NavigationMoveEvent e)
        {
            var steps = e.direction switch
            {
                NavigationMoveEvent.Direction.Left => -1,
                NavigationMoveEvent.Direction.Right => 1,
                _ => 0,
            };
            if (steps == 0 || AlwaysList) return;
            Step(steps);
            e.StopPropagation();
            Button.focusController?.IgnoreEvent(e);
        }

        void OpenList()
        {
            click?.Invoke();
            ChoiceList.Open(host, Button, count(), index(), text, tip, Chosen);
        }

        void Chosen(int value)
        {
            if (value != index())
            {
                click?.Invoke();
                select(value);
            }
            Refresh();
        }
    }
}
