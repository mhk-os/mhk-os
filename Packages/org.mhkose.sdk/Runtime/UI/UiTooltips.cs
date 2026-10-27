using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Shows the <see cref="VisualElement.tooltip"/> of hovered elements (disabled ones included) and, after a longer
    /// delay, of the keyboard or gamepad focus, in a themed bubble (<c>.mhk-tooltip</c>) kept inside the screen. A shown
    /// bubble follows later changes of its tooltip (texts loaded on hover).
    /// </summary>
    public sealed class UiTooltips
    {
        const string BubbleClass = "mhk-tooltip";
        const string ShownClass = "shown";
        const long HoverDelayMs = 350;
        const long FocusDelayMs = 700;
        const float Gap = 10f;
        const long FollowMs = 200;
        const float EdgeMargin = 12f;

        readonly VisualElement root;
        readonly Label bubble;
        readonly IVisualElementScheduledItem showLater, follow;
        VisualElement owner;
        VisualElement hovered;

        /// <summary>Screen root the tooltips of whose descendants this presenter shows.</summary>
        public VisualElement Root => root;

        public UiTooltips(VisualElement root)
        {
            this.root = root;
            bubble = new Label { pickingMode = PickingMode.Ignore };
            bubble.AddToClassList(BubbleClass);
            bubble.style.display = DisplayStyle.None;
            bubble.RegisterCallback<GeometryChangedEvent>(OnBubbleLayout);
            showLater = root.schedule.Execute(Show);
            showLater.Pause();
            follow = root.schedule.Execute(Follow).Every(FollowMs);
            follow.Pause();
            root.RegisterCallback<PointerOverEvent>(OnPointerOver, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
            root.RegisterCallback<PointerDownEvent>(OnAct, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(OnAct, TrickleDown.TrickleDown);
            root.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            root.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
        }

        /// <summary>Hides the bubble and drops a pending one.</summary>
        public void Hide()
        {
            showLater.Pause();
            follow.Pause();
            owner = null;
            bubble.RemoveFromClassList(ShownClass);
            bubble.style.display = DisplayStyle.None;
        }

        void OnPointerOver(PointerOverEvent e) => Hover(OwnerOf(e.target as VisualElement));

        void OnPointerLeave(PointerLeaveEvent e) => Hover(null);

        void Hover(VisualElement next)
        {
            if (next == hovered) return;
            var hoverShown = owner != null && owner == hovered;
            hovered = next;
            if (next == null && hoverShown) Hide();
            else if (next != null && next != owner) Schedule(next, HoverDelayMs);
        }

        void OnAct(EventBase e) => Hide();

        void OnFocusIn(FocusInEvent e)
        {
            var target = OwnerOf(e.target as VisualElement);
            if (target != null && target != owner) Schedule(target, FocusDelayMs);
        }

        void OnFocusOut(FocusOutEvent e)
        {
            if (owner != null && owner != hovered) Hide();
        }

        VisualElement OwnerOf(VisualElement e)
        {
            for (; e != null && e != root; e = e.parent)
                if (!string.IsNullOrEmpty(e.tooltip)) return e;
            return null;
        }

        void Schedule(VisualElement target, long delayMs)
        {
            Hide();
            owner = target;
            showLater.ExecuteLater(delayMs);
        }

        void Show()
        {
            showLater.Pause();
            if (owner == null || owner.panel == null || string.IsNullOrEmpty(owner.tooltip)) return;
            if (bubble.parent != root) root.Add(bubble);
            bubble.BringToFront();
            bubble.text = owner.tooltip;
            bubble.style.display = DisplayStyle.Flex;
            Place();
            follow.Resume();
        }

        void Follow()
        {
            if (owner == null || owner.panel == null || string.IsNullOrEmpty(owner.tooltip))
            {
                Hide();
                return;
            }
            if (bubble.text != owner.tooltip) bubble.text = owner.tooltip;
        }

        void OnBubbleLayout(GeometryChangedEvent e) => Place();

        void Place()
        {
            var size = bubble.layout.size;
            if (owner == null || owner.panel == null || float.IsNaN(size.x) || size.x <= 0f) return;
            var target = root.WorldToLocal(owner.worldBound);
            var area = root.layout.size;
            var x = Mathf.Clamp(target.center.x - size.x * 0.5f, EdgeMargin, Mathf.Max(EdgeMargin, area.x - size.x - EdgeMargin));
            var y = target.yMax + Gap;
            if (y + size.y > area.y - EdgeMargin) y = Mathf.Max(EdgeMargin, target.yMin - Gap - size.y);
            bubble.style.left = x;
            bubble.style.top = y;
            bubble.AddToClassList(ShownClass);
        }
    }
}
