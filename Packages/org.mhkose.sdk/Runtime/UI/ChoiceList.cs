using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Open dropdown list of a <see cref="SettingChoice"/>: a glass panel below its button (above when there is more room),
    /// scrolling when long, the current value checked. Up and Down walk it, a click, tap or submit chooses, cancel or a
    /// click outside closes it unchanged.
    /// </summary>
    public sealed class ChoiceList
    {
        const string OverlayClass = "choice-overlay";
        const string ListClass = "choice-list";
        const string ScrollClass = "choice-scroll";
        const string ItemClass = "choice-item";
        const string ItemLabelClass = "choice-item-label";
        const string ShownClass = "shown";
        const string SelectedClass = "selected";
        const float Gap = 6f;
        const float EdgeMargin = 12f;

        readonly VisualElement host, anchor, overlay, list;
        readonly ScrollView scroll;
        readonly List<Button> items = new();
        readonly Action<int> chosen;
        readonly int current;
        bool placed, closed;

        /// <summary>
        /// Opens the list of <paramref name="count"/> values (<paramref name="text"/>, optional tooltips <paramref name="tip"/>)
        /// over <paramref name="host"/> at <paramref name="anchor"/>, <paramref name="current"/> checked and focused.
        /// <paramref name="chosen"/> receives the picked value (not called when the list closes unchanged).
        /// </summary>
        public static void Open(VisualElement host, VisualElement anchor, int count, int current, Func<int, string> text,
            Func<int, string> tip, Action<int> chosen) => new ChoiceList(host, anchor, count, current, text, tip, chosen);

        ChoiceList(VisualElement host, VisualElement anchor, int count, int current, Func<int, string> text,
            Func<int, string> tip, Action<int> chosen)
        {
            this.host = host;
            this.anchor = anchor;
            this.chosen = chosen;
            this.current = Mathf.Clamp(current, 0, Mathf.Max(0, count - 1));
            overlay = new VisualElement();
            overlay.AddToClassList(OverlayClass);
            list = new VisualElement();
            list.AddToClassList("panel");
            list.AddToClassList(ListClass);
            scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList(ScrollClass);
            scroll.verticalScroller.slider.focusable = false;
            for (var i = 0; i < count; i++) scroll.Add(Item(i, text(i), tip?.Invoke(i)));
            list.Add(scroll);
            overlay.Add(list);
            Register();
            host.Add(overlay);
            UiGlass.Apply(host);
        }

        Button Item(int value, string text, string tooltip)
        {
            var item = new Button { userData = value, tooltip = tooltip };
            item.AddToClassList(ItemClass);
            item.EnableInClassList(SelectedClass, value == current);
            var label = new Label(text);
            label.AddToClassList(ItemLabelClass);
            item.Add(label);
            var check = new VisualElement();
            check.AddToClassList("icon");
            check.AddToClassList("i-check");
            item.Add(check);
            item.clicked += () => Close(value);
            items.Add(item);
            return item;
        }

        void Register()
        {
            overlay.RegisterCallback<PointerDownEvent>(OnOverlayPointerDown);
            overlay.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            list.RegisterCallback<GeometryChangedEvent>(OnLayout);
            scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnLayout);
            scroll.contentContainer.RegisterCallback<FocusInEvent>(OnItemFocus);
            // On the screen root, so cancel and navigation reach the list wherever the focus is while it is open.
            host.RegisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            host.RegisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            host.RegisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
        }

        void OnDetach(DetachFromPanelEvent e)
        {
            closed = true;
            host.UnregisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            host.UnregisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            host.UnregisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
        }

        void Close(int value)
        {
            if (closed) return;
            closed = true;
            overlay.RemoveFromHierarchy();
            anchor.Focus();
            if (value >= 0) chosen(value);
        }

        void OnOverlayPointerDown(PointerDownEvent e)
        {
            if (e.target != overlay) return;
            Close(-1);
            e.StopPropagation();
        }

        void OnCancel(NavigationCancelEvent e)
        {
            Close(-1);
            e.StopPropagation();
        }

        // Submit outside the list (the button, before the list took the focus) must not open a second list.
        void OnSubmit(NavigationSubmitEvent e)
        {
            if (ItemIndex(e.target) >= 0) return;
            e.StopPropagation();
        }

        int ItemIndex(IEventHandler target) => target is Button b && b.userData is int i && items.Contains(b) ? i : -1;

        void OnNavigate(NavigationMoveEvent e)
        {
            var at = ItemIndex(e.target);
            var next = e.direction switch
            {
                NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Previous => at - 1,
                NavigationMoveEvent.Direction.Down or NavigationMoveEvent.Direction.Next => at + 1,
                _ => at,
            };
            if (at < 0) next = current;
            items[Mathf.Clamp(next, 0, items.Count - 1)].Focus();
            e.StopPropagation();
            (e.target as Focusable)?.focusController?.IgnoreEvent(e);
        }

        void OnItemFocus(FocusInEvent e)
        {
            if (e.target is VisualElement item && item.parent == scroll.contentContainer) scroll.ScrollTo(item);
        }

        // Below the button unless it fits only above or above has more room. The height is capped by that room, so long lists scroll.
        void OnLayout(GeometryChangedEvent e)
        {
            var area = overlay.layout.size;
            var content = scroll.contentContainer.layout.height;
            if (float.IsNaN(area.y) || area.y <= 0f || float.IsNaN(content) || items.Count == 0) return;
            var a = overlay.WorldToLocal(anchor.worldBound);
            var below = area.y - a.yMax - Gap - EdgeMargin;
            var above = a.yMin - Gap - EdgeMargin;
            var s = list.resolvedStyle;
            var natural = content + s.paddingTop + s.paddingBottom + s.borderTopWidth + s.borderBottomWidth;
            var down = natural <= below || below >= above;
            var room = Mathf.Max(0f, down ? below : above);
            var height = Mathf.Min(natural, room);
            var width = Mathf.Max(a.width, list.layout.width);
            list.style.minWidth = a.width;
            list.style.maxHeight = room;
            list.style.left = Mathf.Clamp(a.x, EdgeMargin, Mathf.Max(EdgeMargin, area.x - width - EdgeMargin));
            list.style.top = down ? a.yMax + Gap : a.yMin - Gap - height;
            if (placed) return;
            placed = true;
            list.AddToClassList(ShownClass);
            var item = items[current];
            list.schedule.Execute(() =>
            {
                scroll.ScrollTo(item);
                item.Focus();
            });
        }
    }
}
