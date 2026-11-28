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
    /// A page with a turntable area (<see cref="FrameShowcaseIn"/>) turns the turntable when the area is dragged, and a
    /// button in the area (the hanger) is reached from the page's go button in the footer.
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
        VisualElement showcaseArea;
        Button showcaseButton;
        int showcasePointer = -1;

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
            EndShowcaseDrag();
            FocusTarget = null;
            footer = null;
            lastBodyFocus = null;
            showcaseArea = null;
            showcaseButton = null;
        }

        void OnCancel(NavigationCancelEvent e) => GoBack();

        /// <summary>The Back button and the cancel action: returns to the previous screen. A screen may ask first.</summary>
        protected virtual void GoBack() => Flow.Back();

        void OnFocusIn(FocusInEvent e)
        {
            if (e.target is VisualElement v && footer != null && !footer.Contains(v) && v != showcaseButton) lastBodyFocus = v;
        }

        // Down from the lowest element of the page goes to the footer and Up from the footer comes back, instead of the
        // default wrap-around (centred panels have nothing straight above the footer buttons). Up from the go button in
        // the footer goes to the turntable area's button, which goes down to it again and left back to the page.
        void OnNavigate(NavigationMoveEvent e)
        {
            if (e.target is not VisualElement from) return;
            VisualElement to = null;
            if (from == showcaseButton)
                to = e.direction switch
                {
                    NavigationMoveEvent.Direction.Down => GoButton ?? from,
                    NavigationMoveEvent.Direction.Left => lastBodyFocus != null && lastBodyFocus.panel != null ? lastBodyFocus : LowestBodyFocusable(),
                    _ => from,
                };
            else if (footer != null)
            {
                var inFooter = footer.Contains(from);
                if (e.direction == NavigationMoveEvent.Direction.Down && !inFooter && !FocusableBelow(from)) to = FirstButton(footer);
                else if (e.direction == NavigationMoveEvent.Direction.Up && inFooter)
                    to = from == GoButton && ShowcaseButton != null ? ShowcaseButton
                        : lastBodyFocus != null && lastBodyFocus.panel != null ? lastBodyFocus : LowestBodyFocusable();
            }
            if (to == null) return;
            to.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        // The button of the footer's right side (Next, Start!), null without one.
        VisualElement GoButton => footer?.Q(className: "footer-right") is { } right ? FirstButton(right) : null;

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
            v.canGrabFocus && v.resolvedStyle.display != DisplayStyle.None && v.resolvedStyle.visibility == Visibility.Visible && !footer.Contains(v) &&
            v != showcaseButton;

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

        /// <summary>The first button of the page footer, null without one (or before the page is bound).</summary>
        protected VisualElement FooterButton => footer != null ? FirstButton(footer) : null;

        /// <summary>The shown button in the turntable area (the hanger), null without one.</summary>
        protected VisualElement ShowcaseButton => showcaseButton != null && showcaseButton.resolvedStyle.display != DisplayStyle.None ? showcaseButton : null;

        /// <summary>
        /// Stands the 3D turntable in <paramref name="area"/> (the page's turntable area, null does nothing) whenever its
        /// layout changes, so it fills the free part of the page whatever the screen's aspect. Dragging the area (not a
        /// button in it) turns the turntable.
        /// </summary>
        protected void FrameShowcaseIn(VisualElement area)
        {
            if (area == null) return;
            showcaseArea = area;
            showcaseButton = area.Q<Button>();
            area.pickingMode = PickingMode.Position;
            area.RegisterCallback<GeometryChangedEvent>(OnShowcaseAreaChanged);
            area.RegisterCallback<PointerDownEvent>(OnShowcasePointerDown);
            area.RegisterCallback<PointerMoveEvent>(OnShowcasePointerMove);
            area.RegisterCallback<PointerUpEvent>(OnShowcasePointerUp);
            area.RegisterCallback<PointerCaptureOutEvent>(_ => EndShowcaseDrag());
        }

        void OnShowcaseAreaChanged(GeometryChangedEvent e)
        {
            if (e.target is not VisualElement area || area.panel == null) return;
            var size = area.panel.visualTree.layout.size;
            var r = area.worldBound;
            Flow.FrameShowcase(r.width > 0f && r.height > 0f && size.x > 0f && size.y > 0f
                ? new Rect(r.x / size.x, 1f - r.yMax / size.y, r.width / size.x, r.height / size.y)
                : null);
        }

        void OnShowcasePointerDown(PointerDownEvent e)
        {
            if (e.target != showcaseArea || e.button != 0 || showcasePointer >= 0) return;
            showcasePointer = e.pointerId;
            showcaseArea.CapturePointer(e.pointerId);
            Flow.DragShowcase(true);
            e.StopPropagation();
        }

        void OnShowcasePointerMove(PointerMoveEvent e)
        {
            if (e.pointerId == showcasePointer) Flow.DragShowcaseBy(e.deltaPosition.x);
        }

        void OnShowcasePointerUp(PointerUpEvent e)
        {
            if (e.pointerId != showcasePointer) return;
            showcaseArea.ReleasePointer(e.pointerId);
            EndShowcaseDrag();
        }

        // The drag ends with the pointer's release, a lost capture or the page closing. The turntable keeps the throw.
        void EndShowcaseDrag()
        {
            if (showcasePointer < 0) return;
            showcasePointer = -1;
            Flow.DragShowcase(false);
        }

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
