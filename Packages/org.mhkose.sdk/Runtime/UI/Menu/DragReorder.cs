using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace MHK.UI
{
    /// <summary>
    /// Reordering of a list's rows by their <c>::</c> handle (a focusable element named like the handle in each row, MHKTheme
    /// <c>.drag-handle</c> with the <c>i-drag_indicator</c> icon, on the left of the row). Pointer (mouse and touch): the row
    /// lifts and follows, the other rows slide aside, the list scrolls near its edges and the row snaps into its gap on
    /// release, the scroll view then settles inside its range with the row in view. Gamepad and keyboard: Submit on the
    /// focused handle picks the row up, Up and Down move it, Submit or Back put it down.
    /// Only rows with a shown, enabled handle move, anywhere from the first to the last such row.
    /// </summary>
    public sealed class DragReorder
    {
        const string LiftedClass = "drag-lifted";
        const string PlaceholderClass = "drag-placeholder";
        const string ActiveClass = "drag-active";
        const float EdgeZone = 64f;
        const float ScrollSpeed = 900f;
        const int ShiftMs = 140, SnapMs = 120, TickMs = 16;

        readonly VisualElement list;
        readonly string handleName;
        readonly Action<int, int> onMoved;
        readonly ScrollView scroll;
        readonly List<VisualElement> rows = new();
        readonly List<float> tops = new(), bottoms = new();
        readonly Dictionary<VisualElement, (ValueAnimation<float> anim, float y)> offsets = new();
        VisualElement root, row, placeholder, carried;
        IVisualElementScheduledItem tick;
        int pointerId = -1, from, target, first, last, carryFrom;
        float grab, rowY, rowHeight, outerHeight;
        Vector2 pointer;
        bool snapping;

        /// <summary>
        /// Lets the rows (direct children) of <paramref name="list"/> be reordered by their focusable element <paramref name="handleName"/>.
        /// <paramref name="onMoved"/>(from, to) runs once per drop that changed the order: the rows are already in the new order,
        /// apply it to the data as <c>item = data[from]; data.RemoveAt(from); data.Insert(to, item)</c> (refilling the list is fine,
        /// after a gamepad or keyboard move the handle of row <c>to</c> gets the focus on the next frame). <paramref name="scroll"/>
        /// (optional) scrolls while a row is dragged near its edges. Call once per list, refills keep working.
        /// </summary>
        public static DragReorder Attach(VisualElement list, string handleName, Action<int, int> onMoved, ScrollView scroll = null) =>
            new(list, handleName, onMoved, scroll);

        /// <summary>A row is being dragged, carried or snapping into place.</summary>
        public bool Busy => row != null || carried != null || snapping;

        DragReorder(VisualElement list, string handleName, Action<int, int> onMoved, ScrollView scroll)
        {
            this.list = list;
            this.handleName = handleName;
            this.onMoved = onMoved;
            this.scroll = scroll;
            list.RegisterCallback<AttachToPanelEvent>(_ => Hook());
            list.RegisterCallback<DetachFromPanelEvent>(_ => Unhook());
            list.RegisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
            list.RegisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            list.RegisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            list.RegisterCallback<FocusOutEvent>(_ => list.schedule.Execute(CheckCarryFocus));
            // The list captures the pointer while a row is dragged, and captured pointer events go to the capturing element only.
            list.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            list.RegisterCallback<PointerUpEvent>(OnPointerUp);
            list.RegisterCallback<PointerCancelEvent>(OnPointerCancel);
            list.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            if (list.panel != null) Hook();
        }

        // Pointer down sits on the panel root (trickle down): it runs before the scroll view's touch scrolling and the handle's click.
        void Hook()
        {
            Unhook();
            root = list.panel?.visualTree;
            root?.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
        }

        void Unhook()
        {
            root?.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            root = null;
            Abort();
        }

        VisualElement HandleOf(VisualElement e)
        {
            for (; e != null && e != list; e = e.parent)
                if (e.name == handleName && e.parent != null && list.Contains(e)) return e;
            return null;
        }

        VisualElement RowOf(VisualElement e)
        {
            while (e != null && e.parent != list) e = e.parent;
            return e;
        }

        bool Movable(VisualElement r)
        {
            var h = r?.Q(handleName);
            return h != null && h.enabledInHierarchy && h.resolvedStyle.display != DisplayStyle.None && h.resolvedStyle.visibility == Visibility.Visible;
        }

        bool FindRange(int index)
        {
            first = last = -1;
            for (var i = 0; i < list.childCount; i++)
            {
                if (!Movable(list[i])) continue;
                if (first < 0) first = i;
                last = i;
            }
            return first >= 0 && index >= first && index <= last && Movable(list[index]);
        }

        void OnPointerDown(PointerDownEvent e)
        {
            if (Busy || e.button != 0 || HandleOf(e.target as VisualElement) is not { } handle) return;
            var r = RowOf(handle);
            if (!FindRange(list.IndexOf(r)) || first == last) return;
            e.StopPropagation();
            pointerId = e.pointerId;
            list.CapturePointer(pointerId);
            pointer = e.position;
            Click();
            Lift(r);
        }

        void Lift(VisualElement r)
        {
            rows.Clear();
            tops.Clear();
            bottoms.Clear();
            foreach (var c in list.Children())
            {
                rows.Add(c);
                tops.Add(c.layout.yMin);
                bottoms.Add(c.layout.yMax);
            }
            row = r;
            from = target = rows.IndexOf(r);
            var s = r.resolvedStyle;
            rowY = r.layout.y;
            rowHeight = r.layout.height;
            outerHeight = rowHeight + s.marginTop + s.marginBottom;
            placeholder = new VisualElement { pickingMode = PickingMode.Ignore };
            placeholder.AddToClassList(PlaceholderClass);
            placeholder.style.height = rowHeight;
            placeholder.style.marginTop = s.marginTop;
            placeholder.style.marginBottom = s.marginBottom;
            list.Insert(from, placeholder);
            var left = r.layout.x - list.resolvedStyle.borderLeftWidth;
            var width = r.layout.width;
            r.RemoveFromHierarchy();
            list.Add(r);
            r.style.position = Position.Absolute;
            r.style.marginTop = r.style.marginBottom = r.style.marginLeft = r.style.marginRight = 0;
            r.style.left = left;
            r.style.width = width;
            SetRowY(rowY);
            list.AddToClassList(ActiveClass);
            r.AddToClassList(LiftedClass);
            grab = list.WorldToLocal(pointer).y - rowY;
            tick = list.schedule.Execute(Tick).Every(TickMs);
        }

        void SetRowY(float y)
        {
            rowY = y;
            row.style.top = y - list.resolvedStyle.borderTopWidth;
        }

        float SlotTop(int t) => t == from ? tops[from] : t > from ? bottoms[t] - rowHeight : tops[t];

        void OnPointerMove(PointerMoveEvent e)
        {
            if (e.pointerId != pointerId || row == null) return;
            e.StopPropagation();
            pointer = e.position;
            Place();
        }

        // Follows the pointer inside the movable range. The row's centre past another row's centre moves the gap there.
        void Place()
        {
            if (placeholder.parent != list)
            {
                Abort();
                return;
            }
            SetRowY(Mathf.Clamp(list.WorldToLocal(pointer).y - grab, SlotTop(first), SlotTop(last)));
            var t = first;
            var best = float.MaxValue;
            for (var j = first; j <= last; j++)
            {
                var distance = Mathf.Abs(rowY - SlotTop(j));
                if (distance >= best) continue;
                best = distance;
                t = j;
            }
            if (t == target) return;
            target = t;
            for (var j = first; j <= last; j++)
                if (j != from) Slide(rows[j], from < j && j <= target ? -outerHeight : target <= j && j < from ? outerHeight : 0f);
        }

        void Slide(VisualElement e, float to)
        {
            offsets.TryGetValue(e, out var o);
            if (o.anim != null)
            {
                if (o.anim.isRunning) o.anim.Stop();
                o.anim.Recycle();
            }
            var anim = e.experimental.animation.Start(o.y, to, ShiftMs, (el, v) =>
            {
                el.style.translate = new Translate(0, v);
                if (offsets.TryGetValue(el, out var cur)) offsets[el] = (cur.anim, v);
            }).Ease(Easing.OutCubic);
            anim.autoRecycle = false;
            offsets[e] = (anim, o.y);
        }

        void Tick()
        {
            if (row == null || snapping) return;
            if (scroll != null)
            {
                var view = scroll.contentViewport.worldBound;
                var speed = Mathf.Clamp01((view.yMin + EdgeZone - pointer.y) / EdgeZone) - Mathf.Clamp01((pointer.y - view.yMax + EdgeZone) / EdgeZone);
                if (speed != 0f) ScrollTo(scroll.scrollOffset.y - speed * ScrollSpeed * TickMs / 1000f);
            }
            Place();
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (e.pointerId != pointerId) return;
            e.StopPropagation();
            Drop();
        }

        void OnPointerCancel(PointerCancelEvent e)
        {
            if (e.pointerId == pointerId) Drop();
        }

        void OnCaptureOut(PointerCaptureOutEvent e)
        {
            if (e.pointerId == pointerId && e.target == list) Drop();
        }

        void Drop()
        {
            var id = pointerId;
            pointerId = -1;
            if (list.HasPointerCapture(id)) list.ReleasePointer(id);
            if (row == null || snapping) return;
            tick?.Pause();
            snapping = true;
            row.experimental.animation.Start(rowY, SlotTop(target), SnapMs, (_, v) => SetRowY(v)).Ease(Easing.OutCubic).OnCompleted(Finish);
        }

        void Finish()
        {
            if (row == null) return;
            var (f, t) = (from, target);
            var r = row;
            var intact = placeholder != null && placeholder.parent == list && r.parent == list;
            Clear();
            if (!intact) return;
            r.RemoveFromHierarchy();
            list.Insert(t, r);
            Click();
            if (f != t) onMoved(f, t);
            Settle(t);
        }

        void ScrollTo(float y)
        {
            var max = Mathf.Max(0f, scroll.contentContainer.layout.height - scroll.contentViewport.layout.height);
            var offset = scroll.scrollOffset;
            var clamped = Mathf.Clamp(y, 0f, max);
            if (!Mathf.Approximately(clamped, offset.y)) scroll.scrollOffset = new Vector2(offset.x, clamped);
        }

        // Once laid out (onMoved may refill the list): back inside the scroll range, with the dropped row in view.
        void Settle(int index)
        {
            if (scroll == null) return;
            list.schedule.Execute(() =>
            {
                if (index < list.childCount) scroll.ScrollTo(list[index]);
                ScrollTo(scroll.scrollOffset.y);
            }).ExecuteLater(UiAnim.FrameMs);
        }

        void Abort()
        {
            if (pointerId >= 0 && list.HasPointerCapture(pointerId)) list.ReleasePointer(pointerId);
            pointerId = -1;
            if (row != null) Clear();
            if (carried != null)
            {
                carried.RemoveFromClassList(LiftedClass);
                list.RemoveFromClassList(ActiveClass);
            }
            carried = null;
        }

        void Clear()
        {
            tick?.Pause();
            tick = null;
            foreach (var (e, o) in offsets)
            {
                if (o.anim != null && o.anim.isRunning) o.anim.Stop();
                o.anim?.Recycle();
                e.style.translate = StyleKeyword.Null;
            }
            offsets.Clear();
            row.RemoveFromClassList(LiftedClass);
            row.style.position = StyleKeyword.Null;
            row.style.left = row.style.top = row.style.width = StyleKeyword.Null;
            row.style.marginTop = row.style.marginBottom = row.style.marginLeft = row.style.marginRight = StyleKeyword.Null;
            placeholder?.RemoveFromHierarchy();
            list.RemoveFromClassList(ActiveClass);
            row = placeholder = null;
            rows.Clear();
            snapping = false;
        }

        void OnSubmit(NavigationSubmitEvent e)
        {
            if (row != null || snapping)
            {
                e.StopPropagation();
                return;
            }
            if (carried != null)
            {
                e.StopPropagation();
                PutDown();
                return;
            }
            if (HandleOf(e.target as VisualElement) is not { } handle) return;
            var r = RowOf(handle);
            if (!FindRange(list.IndexOf(r)) || first == last) return;
            e.StopPropagation();
            carried = r;
            carryFrom = list.IndexOf(r);
            r.AddToClassList(LiftedClass);
            list.AddToClassList(ActiveClass);
            Click();
        }

        void OnCancel(NavigationCancelEvent e)
        {
            if (carried == null) return;
            e.StopPropagation();
            PutDown();
        }

        void OnNavigate(NavigationMoveEvent e)
        {
            if (carried == null) return;
            e.StopPropagation();
            (e.target as Focusable)?.focusController?.IgnoreEvent(e);
            var step = e.direction switch
            {
                NavigationMoveEvent.Direction.Up => -1,
                NavigationMoveEvent.Direction.Down => 1,
                _ => 0,
            };
            var i = list.IndexOf(carried);
            if (i < 0 || !FindRange(i)) return;
            var j = Mathf.Clamp(i + step, first, last);
            if (j != i) Step(i, j);
        }

        // Moves the carried row one place and slides both rows from where they were drawn.
        void Step(int i, int j)
        {
            var other = list[j];
            var carriedHeight = Outer(carried);
            var otherHeight = Outer(other);
            carried.RemoveFromHierarchy();
            list.Insert(j, carried);
            Bump(carried, j < i ? otherHeight : -otherHeight);
            Bump(other, j < i ? -carriedHeight : carriedHeight);
            carried.Q(handleName)?.Focus();
            scroll?.ScrollTo(carried);
        }

        static void Click() => UnityEngine.Object.FindAnyObjectByType<MenuFlow>()?.Click();

        static float Outer(VisualElement e) => e.layout.height + e.resolvedStyle.marginTop + e.resolvedStyle.marginBottom;

        static void Bump(VisualElement e, float fromY) =>
            e.experimental.animation.Start(fromY, 0f, ShiftMs, (el, v) => el.style.translate = new Translate(0, v)).Ease(Easing.OutCubic);

        void PutDown()
        {
            var r = carried;
            Click();
            carried = null;
            r.RemoveFromClassList(LiftedClass);
            list.RemoveFromClassList(ActiveClass);
            var to = list.IndexOf(r);
            if (to < 0 || to == carryFrom) return;
            onMoved(carryFrom, to);
            list.schedule.Execute(() =>
            {
                if (to < list.childCount) list[to].Q(handleName)?.Focus();
            });
        }

        // The focus left the carried row's handle (a click elsewhere): it is put down where it is.
        void CheckCarryFocus()
        {
            if (carried == null) return;
            var handle = carried.Q(handleName);
            if (carried.parent != list) Abort();
            else if (handle == null || handle.focusController?.focusedElement != handle) PutDown();
        }
    }
}
