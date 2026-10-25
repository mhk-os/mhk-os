using System;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// One main-menu screen (child of <see cref="MenuFlow"/>), bound to its UXML page by element name. A Button named
    /// <c>back</c> and the cancel action go back. A setting row (Templates/SettingRow.uxml) is a <see cref="SettingChoice"/> or a link (<see cref="BindLink"/>).
    /// </summary>
    public abstract class MenuScreen : UiScreen
    {
        [SerializeField, Tooltip("Keep the 3D driver showcase visible behind this screen.")]
        protected bool showcaseVisible = true;
        [SerializeField, Tooltip("Back button and the cancel action (Escape, gamepad B) return to the previous screen.")]
        protected bool allowBack = true;

        MenuFlow flow;
        VisualElement footer;
        VisualElement lastBodyFocus;

        public bool ShowcaseVisible => showcaseVisible;

        protected MenuFlow Flow => flow != null ? flow : flow = GetComponentInParent<MenuFlow>(true);
        protected MenuSelection Selection => Flow.Selection;
        protected static ContentRegistry Content => GameRoot.Instance.Content;
        protected static UserSettings Settings => GameRoot.Instance.Settings;

        /// <summary>Element focused when the screen opens (default: the first visible button that is not a chip).</summary>
        protected VisualElement FocusTarget { get; set; }

        protected override bool ReadyToBind => Flow != null && Flow.Current == this && GameRoot.Instance != null;

        protected override bool InsetBySafeArea => true;

        protected override void AfterBind()
        {
            var back = Root.Q<Button>("back");
            if (!allowBack) back?.RemoveFromHierarchy();
            else
            {
                if (back != null) back.clicked += GoBack;
                Root.RegisterCallback<NavigationCancelEvent>(OnCancel);
            }
            footer = Root.Q(className: "page-footer");
            Root.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationMoveEvent>(OnNavigate);
            var root = Root;
            root.schedule.Execute(() => (FocusTarget ?? FirstButton(root))?.Focus());
        }

        protected override void Unbind()
        {
            FocusTarget = null;
            footer = null;
            lastBodyFocus = null;
        }

        void OnCancel(NavigationCancelEvent e) => GoBack();

        /// <summary>The Back button and the cancel action: returns to the previous screen. A screen may ask first.</summary>
        protected virtual void GoBack() => Flow.Back();

        void OnFocusIn(FocusInEvent e)
        {
            if (e.target is VisualElement v && footer != null && !footer.Contains(v)) lastBodyFocus = v;
        }

        // Down from the lowest element of the page goes to the footer and Up from the footer comes back, instead of the
        // default wrap-around (centred panels have nothing straight above the footer buttons).
        void OnNavigate(NavigationMoveEvent e)
        {
            if (footer == null || e.target is not VisualElement from) return;
            var inFooter = footer.Contains(from);
            VisualElement to = null;
            if (e.direction == NavigationMoveEvent.Direction.Down && !inFooter && !FocusableBelow(from)) to = FirstButton(footer);
            else if (e.direction == NavigationMoveEvent.Direction.Up && inFooter)
                to = lastBodyFocus != null && lastBodyFocus.panel != null ? lastBodyFocus : LowestBodyFocusable();
            if (to == null) return;
            to.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        bool FocusableBelow(VisualElement from)
        {
            var bottom = from.worldBound.yMax;
            return Root.Query<VisualElement>().Where(v => IsBodyFocusable(v) && v.worldBound.yMin >= bottom - 1f).First() != null;
        }

        VisualElement LowestBodyFocusable()
        {
            VisualElement lowest = null;
            Root.Query<VisualElement>().Where(IsBodyFocusable).ForEach(v =>
            {
                if (lowest == null || v.worldBound.yMax > lowest.worldBound.yMax) lowest = v;
            });
            return lowest;
        }

        bool IsBodyFocusable(VisualElement v) =>
            v.canGrabFocus && v.resolvedStyle.display != DisplayStyle.None && v.resolvedStyle.visibility == Visibility.Visible && !footer.Contains(v);

        static Button FirstButton(VisualElement root) =>
            root.Query<Button>().Where(b => !b.ClassListContains("chip") && b.resolvedStyle.display != DisplayStyle.None).First();

        protected void Click() => Flow.Click();

        /// <summary>Button <paramref name="name"/> runs <paramref name="action"/> (after the click sound unless <paramref name="click"/> is false).</summary>
        protected Button OnClick(string name, Action action, bool click = true)
        {
            var b = Q<Button>(name);
            b.clicked += () =>
            {
                if (click) Click();
                action();
            };
            return b;
        }

        /// <summary>Shows or hides the element <paramref name="name"/> (setting rows that do not apply to the mode, ...).</summary>
        protected void SetVisible(string name, bool visible) => Q<VisualElement>(name).SetVisible(visible);

        /// <summary>
        /// Setting row <paramref name="rowName"/> choosing one of <paramref name="count"/> values (<see cref="SettingChoice"/>):
        /// <paramref name="index"/> is the current one, <paramref name="text"/> and the optional tooltip <paramref name="tip"/>
        /// describe a value, <paramref name="select"/> applies one (after the click sound unless <paramref name="click"/> is false).
        /// </summary>
        protected SettingChoice BindChoice(string rowName, Func<int> count, Func<int> index, Func<int, string> text,
            Action<int> select, Func<int, string> tip = null, bool click = true) =>
            new(Q<VisualElement>(rowName), Root, count, index, text, select, tip, click ? Click : (Action)null);

        /// <summary>Setting row <paramref name="rowName"/> choosing a value of the enum <typeparamref name="T"/> in declaration order (text: the value's name unless given).</summary>
        protected SettingChoice BindChoice<T>(string rowName, Func<T> get, Action<T> set, Func<T, string> text = null,
            Func<T, string> tip = null, bool click = true) where T : struct, Enum
        {
            var values = (T[])Enum.GetValues(typeof(T));
            return BindChoice(rowName, () => values.Length, () => Mathf.Max(0, Array.IndexOf(values, get())),
                i => text != null ? text(values[i]) : values[i].ToString(), i => set(values[i]),
                tip != null ? i => tip(values[i]) : (Func<int, string>)null, click);
        }

        /// <summary>Setting row <paramref name="rowName"/> showing <paramref name="value"/> whose button opens another screen (<paramref name="open"/>).</summary>
        protected Button BindLink(string rowName, string value, Action open)
        {
            var row = Q<VisualElement>(rowName);
            row.AddToClassList(SettingChoice.LinkClass);
            row.SetText("setting-value-label", value);
            var button = row.Q<Button>() ?? new Button();
            button.clicked += () =>
            {
                Click();
                open();
            };
            return button;
        }

        /// <summary>Slider <paramref name="name"/> starts at <paramref name="value"/>. Changes call <paramref name="set"/> and apply the audio settings.</summary>
        protected Slider BindSlider(string name, float value, Action<float> set)
        {
            var slider = Q<Slider>(name);
            slider.SetValueWithoutNotify(value);
            slider.RegisterValueChangedCallback(e =>
            {
                set(e.newValue);
                GameRoot.Instance.ApplyAudioSettings();
            });
            return slider;
        }

        /// <summary>Scrolls <paramref name="scroll"/> so the focused direct child of <paramref name="list"/> stays visible.</summary>
        protected static void KeepFocusedInView(ScrollView scroll, VisualElement list) =>
            list.RegisterCallback<FocusInEvent>(e =>
            {
                if (e.target is VisualElement child && child.parent == list) scroll.ScrollTo(child);
            });

        /// <summary>Focuses <paramref name="e"/> on the next frame (after a list was refilled). Null does nothing.</summary>
        protected static void FocusLater(VisualElement e) => e?.schedule.Execute(e.Focus);
    }
}
