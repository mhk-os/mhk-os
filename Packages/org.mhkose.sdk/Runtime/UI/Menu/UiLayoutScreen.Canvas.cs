using System.Collections.Generic;
using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class UiLayoutScreen
    {
        enum Drag { None, Move, Resize, Pinch }

        // Handle sides in drawing order: -1 = left / top edge, 1 = right / bottom edge, 0 = the middle.
        static readonly Vector2Int[] HandleSides =
        {
            new(-1, -1), new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0),
        };

        readonly VisualElement[] handles = new VisualElement[HandleSides.Length];
        readonly List<float> snapX = new(), snapY = new();
        VisualElement gizmo, box, guideX, guideY, draggedHandle;
        Drag drag;
        int dragPointer = -1, pinchPointer = -1;
        Vector2Int handleSide;
        Vector2 screenSize, dragStart, dragAt, pinchAt, startScale;
        Rect startRect, shownBox;
        float screenScale = 1f, pinchDistance;

        void BindCanvas()
        {
            gizmo = Q<VisualElement>("ul-gizmo");
            box = Q<VisualElement>("ul-box");
            guideX = Q<VisualElement>("ul-guide-x");
            guideY = Q<VisualElement>("ul-guide-y");
            for (var i = 0; i < HandleSides.Length; i++)
            {
                var handle = new VisualElement();
                handle.AddToClassList("ul-handle");
                var side = HandleSides[i];
                handle.RegisterCallback<PointerDownEvent>(e => OnHandleDown(side, handle, e));
                gizmo.Add(handle);
                handles[i] = handle;
            }
            frame.RegisterCallback<GeometryChangedEvent>(_ => FitScreen());
            frame.RegisterCallback<PointerDownEvent>(OnFrameDown);
            frame.RegisterCallback<PointerMoveEvent>(OnFrameMove);
            frame.RegisterCallback<PointerUpEvent>(e => EndPointer(e.pointerId));
            frame.RegisterCallback<PointerCancelEvent>(e => EndPointer(e.pointerId));
            frame.RegisterCallback<PointerCaptureOutEvent>(e => EndPointer(e.pointerId));
            frame.RegisterCallback<WheelEvent>(OnWheel);
            frame.RegisterCallback<NavigationMoveEvent>(OnFrameNavigate);
            frame.RegisterCallback<NavigationSubmitEvent>(LeaveFrame);
            frame.RegisterCallback<NavigationCancelEvent>(LeaveFrame);
        }

        void FitScreen()
        {
            var size = Root.panel != null ? Root.panel.visualTree.layout.size : Vector2.zero;
            var room = frame.layout.size;
            if (size.x <= 0f || size.y <= 0f || float.IsNaN(room.x) || room.x <= 0f) return;
            screenSize = size;
            screenScale = Mathf.Min(room.x / size.x, room.y / size.y);
            screen.style.width = size.x;
            screen.style.height = size.y;
            screen.style.scale = new Scale(new Vector3(screenScale, screenScale, 1f));
            screen.style.left = (room.x - size.x * screenScale) * 0.5f;
            screen.style.top = (room.y - size.y * screenScale) * 0.5f;
            shownBox = Rect.zero;
        }

        Rect RectOf(Item item)
        {
            if (item.hud != null) return hudView != null ? hudView.Bounds(item.hud.type) : Rect.zero;
            var size = item.element.Size;
            return new Rect(CentreOf(item.control, screenSize) - Vector2.one * (size * 0.5f), new Vector2(size, size));
        }

        static Vector2 ScaleOf(Item item) => item.hud != null ? item.hud.scale : Vector2.one * item.control.scale;

        Vector2 AnchorOf(Item item)
        {
            if (item.control != null || hudView == null) return Vector2.one * 0.5f;
            if (!item.hud.placed) HudLayout.Anchor(item.hud, RectOf(item), hudView.ScreenSize);
            return item.hud.anchor;
        }

        void SetRect(Item item, Rect rect, Vector2 scale)
        {
            if (item.hud != null)
            {
                if (hudView == null) return;
                item.hud.scale = scale;
                HudLayout.Anchor(item.hud, rect, hudView.ScreenSize);
                hudView.Apply(item.hud);
            }
            else
            {
                item.control.scale = scale.x;
                Place(item.control, rect.center);
                item.element.Apply(hiddenOpacity);
            }
            isDefault = false;
            RefreshPanel();
        }

        void ScaleAround(Item item, Vector2 scale, Vector2 pivot) => ScaleFrom(item, RectOf(item), ScaleOf(item), scale, pivot);

        void ScaleFrom(Item item, Rect from, Vector2 fromScale, Vector2 scale, Vector2 pivot)
        {
            scale.x = Mathf.Clamp(scale.x, UiLayoutSettings.MinScale, UiLayoutSettings.MaxScale);
            scale.y = Mathf.Clamp(scale.y, UiLayoutSettings.MinScale, UiLayoutSettings.MaxScale);
            var point = from.min + Vector2.Scale(pivot, from.size);
            var size = new Vector2(from.width * scale.x / Mathf.Max(0.01f, fromScale.x), from.height * scale.y / Mathf.Max(0.01f, fromScale.y));
            SetRect(item, new Rect(point - Vector2.Scale(pivot, size), size), scale);
        }

        void Place(TouchControlSetup setup, Vector2 centre)
        {
            var half = TouchControlElement.BaseSize(setup.type) * setup.scale * 0.5f;
            centre.x = Mathf.Clamp(centre.x, Mathf.Min(half, screenSize.x * 0.5f), Mathf.Max(screenSize.x - half, screenSize.x * 0.5f));
            centre.y = Mathf.Clamp(centre.y, Mathf.Min(half, screenSize.y * 0.5f), Mathf.Max(screenSize.y - half, screenSize.y * 0.5f));
            setup.anchor = new Vector2(centre.x < screenSize.x * 0.5f ? 0f : 1f, centre.y < screenSize.y * 0.5f ? 0f : 1f);
            setup.offset = centre - Vector2.Scale(setup.anchor, screenSize);
        }

        static Vector2 CentreOf(TouchControlSetup setup, Vector2 screenSize) => setup.offset + Vector2.Scale(setup.anchor, screenSize);

        Vector2 ScreenPoint(Vector2 panelPosition) => screen.WorldToLocal(panelPosition);

        // The picked element keeps the pointer where it overlaps others. Touch controls lie over the HUD, of overlapping
        // HUD elements the smallest wins.
        Item HitTest(Vector2 at)
        {
            if (picked != null && RectOf(picked).Contains(at)) return picked;
            for (var i = controlItems.Count - 1; i >= 0; i--)
                if (RectOf(controlItems[i]).Contains(at)) return controlItems[i];
            Item best = null;
            var bestArea = float.MaxValue;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].hud == null) continue;
                var r = RectOf(items[i]);
                var area = r.width * r.height;
                if (area <= 0f || area >= bestArea || !r.Contains(at)) continue;
                best = items[i];
                bestArea = area;
            }
            return best;
        }

        void OnFrameDown(PointerDownEvent e)
        {
            if (e.pointerType == UnityEngine.UIElements.PointerType.mouse && e.button != 0) return;
            frame.Focus();
            var at = ScreenPoint(e.position);
            if (drag == Drag.Move && e.pointerId != dragPointer)
            {
                StartPinch(e.pointerId, at);
                return;
            }
            if (drag != Drag.None) return;
            var hit = HitTest(at);
            if (hit == null) return;
            if (hit != picked) Pick(hit);
            StartDrag(Drag.Move, e.pointerId, at);
        }

        void OnHandleDown(Vector2Int side, VisualElement handle, PointerDownEvent e)
        {
            e.StopPropagation();
            if (drag != Drag.None || picked == null || (e.pointerType == UnityEngine.UIElements.PointerType.mouse && e.button != 0)) return;
            frame.Focus();
            handleSide = side;
            draggedHandle = handle;
            handle.AddToClassList("dragged");
            StartDrag(Drag.Resize, e.pointerId, ScreenPoint(e.position));
        }

        void StartDrag(Drag mode, int pointerId, Vector2 at)
        {
            drag = mode;
            dragPointer = pointerId;
            dragStart = dragAt = at;
            startRect = RectOf(picked);
            startScale = ScaleOf(picked);
            CollectSnaps(picked);
            frame.CapturePointer(pointerId);
        }

        void StartPinch(int pointerId, Vector2 at)
        {
            if (pinchPointer >= 0 || picked == null) return;
            pinchPointer = pointerId;
            pinchAt = at;
            pinchDistance = Mathf.Max(1f, Vector2.Distance(dragAt, at));
            drag = Drag.Pinch;
            startRect = RectOf(picked);
            startScale = ScaleOf(picked);
            ShowGuides(float.NaN, float.NaN);
            frame.CapturePointer(pointerId);
        }

        void OnFrameMove(PointerMoveEvent e)
        {
            if (drag == Drag.None || picked == null) return;
            var at = ScreenPoint(e.position);
            if (e.pointerId == pinchPointer) pinchAt = at;
            else if (e.pointerId == dragPointer) dragAt = at;
            else return;
            if (drag == Drag.Pinch)
                ScaleFrom(picked, startRect, startScale, startScale * (Vector2.Distance(dragAt, pinchAt) / pinchDistance), Vector2.one * 0.5f);
            else if (drag == Drag.Move) MoveTo(startRect.position + dragAt - dragStart, true);
            else ResizeTo(dragAt);
        }

        void MoveTo(Vector2 position, bool snap)
        {
            var rect = new Rect(position, startRect.size);
            if (picked.hud != null)
            {
                rect.x = Mathf.Clamp(rect.x, Mathf.Min(0f, screenSize.x - rect.width), Mathf.Max(0f, screenSize.x - rect.width));
                rect.y = Mathf.Clamp(rect.y, Mathf.Min(0f, screenSize.y - rect.height), Mathf.Max(0f, screenSize.y - rect.height));
            }
            if (snap) rect.position += Snap(rect);
            SetRect(picked, rect, ScaleOf(picked));
        }

        // Edge handles scale one axis and snap the dragged edge. Corner handles (and every handle of a touch control) scale
        // both axes by the same factor. The opposite edge or corner stays where it is.
        void ResizeTo(Vector2 at)
        {
            if (startRect.width <= 0f || startRect.height <= 0f) return;
            var side = handleSide;
            var pivot = new Vector2(side.x < 0 ? 1f : side.x > 0 ? 0f : 0.5f, side.y < 0 ? 1f : side.y > 0 ? 0f : 0.5f);
            var fixedPoint = startRect.min + Vector2.Scale(pivot, startRect.size);
            var corner = side.x != 0 && side.y != 0;
            float guideAtX = float.NaN, guideAtY = float.NaN;
            var ratio = Vector2.one;
            if (side.x != 0) ratio.x = (EdgeTo(at.x, snapX, !corner, out guideAtX) - fixedPoint.x) * side.x / startRect.width;
            if (side.y != 0) ratio.y = (EdgeTo(at.y, snapY, !corner, out guideAtY) - fixedPoint.y) * side.y / startRect.height;
            if (corner || picked.control != null)
            {
                var f = side.x == 0 ? ratio.y : side.y == 0 ? ratio.x : 0.5f * (ratio.x + ratio.y);
                f = Mathf.Clamp(f, Mathf.Max(UiLayoutSettings.MinScale / startScale.x, UiLayoutSettings.MinScale / startScale.y),
                    Mathf.Min(UiLayoutSettings.MaxScale / startScale.x, UiLayoutSettings.MaxScale / startScale.y));
                ratio = new Vector2(f, f);
            }
            ShowGuides(guideAtX, guideAtY);
            ScaleFrom(picked, startRect, startScale, Vector2.Scale(startScale, ratio), pivot);
        }

        float EdgeTo(float value, List<float> lines, bool snap, out float guide)
        {
            guide = float.NaN;
            if (!snap) return value;
            var best = SnapRange;
            var snapped = value;
            for (var i = 0; i < lines.Count; i++)
            {
                var d = Mathf.Abs(lines[i] - value);
                if (d >= best) continue;
                best = d;
                snapped = guide = lines[i];
            }
            return snapped;
        }

        float SnapRange => snapDistance / Mathf.Max(0.01f, screenScale);

        Vector2 Snap(Rect r)
        {
            var dx = Nearest(snapX, r.xMin, r.center.x, r.xMax, out var gx);
            var dy = Nearest(snapY, r.yMin, r.center.y, r.yMax, out var gy);
            ShowGuides(gx, gy);
            return new Vector2(dx, dy);
        }

        float Nearest(List<float> lines, float a, float b, float c, out float guide)
        {
            guide = float.NaN;
            var best = SnapRange;
            var delta = 0f;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                for (var k = 0; k < 3; k++)
                {
                    var d = line - (k == 0 ? a : k == 1 ? b : c);
                    if (Mathf.Abs(d) >= best) continue;
                    best = Mathf.Abs(d);
                    delta = d;
                    guide = line;
                }
            }
            return delta;
        }

        void CollectSnaps(Item self)
        {
            snapX.Clear();
            snapY.Clear();
            AddSnaps(new Rect(Vector2.zero, screenSize));
            for (var i = 0; i < items.Count; i++)
                if (items[i] != self) AddSnaps(RectOf(items[i]));
        }

        void AddSnaps(Rect r)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            snapX.Add(r.xMin);
            snapX.Add(r.center.x);
            snapX.Add(r.xMax);
            snapY.Add(r.yMin);
            snapY.Add(r.center.y);
            snapY.Add(r.yMax);
        }

        void ShowGuides(float x, float y)
        {
            var area = screen.ChangeCoordinatesTo(gizmo, new Rect(Vector2.zero, screenSize));
            guideX.SetVisible(!float.IsNaN(x));
            guideY.SetVisible(!float.IsNaN(y));
            if (!float.IsNaN(x))
            {
                guideX.style.left = area.x + x * screenScale;
                guideX.style.top = area.y;
                guideX.style.height = area.height;
            }
            if (float.IsNaN(y)) return;
            guideY.style.top = area.y + y * screenScale;
            guideY.style.left = area.x;
            guideY.style.width = area.width;
        }

        void EndPointer(int pointerId)
        {
            if (pointerId == pinchPointer)
            {
                pinchPointer = -1;
                ReleasePointer(pointerId);
                if (drag != Drag.Pinch || picked == null) return;
                drag = Drag.Move;
                startRect = RectOf(picked);
                startScale = ScaleOf(picked);
                dragStart = dragAt;
                return;
            }
            if (pointerId == dragPointer) EndDrag();
        }

        void EndDrag()
        {
            var first = dragPointer;
            var second = pinchPointer;
            drag = Drag.None;
            dragPointer = pinchPointer = -1;
            ReleasePointer(first);
            ReleasePointer(second);
            draggedHandle?.RemoveFromClassList("dragged");
            draggedHandle = null;
            if (guideX != null) ShowGuides(float.NaN, float.NaN);
        }

        void ReleasePointer(int pointerId)
        {
            if (pointerId >= 0 && frame != null && frame.HasPointerCapture(pointerId)) frame.ReleasePointer(pointerId);
        }

        void OnWheel(WheelEvent e)
        {
            if (picked == null || e.delta.y == 0f) return;
            var step = -Mathf.Sign(e.delta.y) * wheelScaleStep;
            ScaleAround(picked, ScaleOf(picked) + new Vector2(step, step), AnchorOf(picked));
            e.StopPropagation();
        }

        // Direction keys and the D-pad move the picked element while the race screen has the focus, Tab picks the next.
        void OnFrameNavigate(NavigationMoveEvent e)
        {
            var move = e.direction switch
            {
                NavigationMoveEvent.Direction.Left => Vector2.left,
                NavigationMoveEvent.Direction.Right => Vector2.right,
                NavigationMoveEvent.Direction.Up => Vector2.down,
                NavigationMoveEvent.Direction.Down => Vector2.up,
                _ => Vector2.zero,
            };
            if (e.direction == NavigationMoveEvent.Direction.Next) Step(1);
            else if (e.direction == NavigationMoveEvent.Direction.Previous) Step(-1);
            else if (picked != null && drag == Drag.None)
            {
                startRect = RectOf(picked);
                MoveTo(startRect.position + move * keyStep, false);
            }
            e.StopPropagation();
            frame.focusController?.IgnoreEvent(e);
        }

        void LeaveFrame(EventBase e)
        {
            moveButton.Focus();
            e.StopPropagation();
            frame.focusController?.IgnoreEvent(e);
        }

        void UpdateGizmo()
        {
            if (box == null) return;
            var rect = picked != null && screenScale > 0f ? RectOf(picked) : Rect.zero;
            var shown = rect.width > 0f && rect.height > 0f;
            if (!shown)
            {
                if (shownBox == Rect.zero) return;
                shownBox = Rect.zero;
                box.SetVisible(false);
                foreach (var handle in handles) handle.SetVisible(false);
                return;
            }
            var r = screen.ChangeCoordinatesTo(gizmo, rect);
            if (r == shownBox) return;
            shownBox = r;
            box.SetVisible(true);
            box.style.left = r.x;
            box.style.top = r.y;
            box.style.width = r.width;
            box.style.height = r.height;
            for (var i = 0; i < handles.Length; i++)
            {
                var side = HandleSides[i];
                var at = r.center + Vector2.Scale(new Vector2(side.x, side.y), r.size * 0.5f);
                handles[i].style.left = at.x;
                handles[i].style.top = at.y;
                handles[i].SetVisible(picked.hud != null || (side.x != 0 && side.y != 0));
            }
        }
    }
}
