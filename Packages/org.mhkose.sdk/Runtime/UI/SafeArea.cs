using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Keeps a full-screen page inside <see cref="Screen.safeArea"/> (notches, rounded corners): insets the page's
    /// left / top / right / bottom, while descendants with class <c>bleed</c> keep covering the whole screen.
    /// </summary>
    public static partial class SafeArea
    {
        const long PollMs = 250;
        const float MinInset = 0.5f;

        /// <summary>Safe area used instead of the device's (screen pixels, y up), for testing layouts in the editor.</summary>
        [AutoStaticsCleanup] public static Rect? Override;

        /// <summary>Insets <paramref name="page"/> now and whenever the safe area or the panel size changes, while it is in a panel.</summary>
        public static void Track(VisualElement page)
        {
            var applied = new Rect(-1f, -1f, -1f, -1f);
            page.schedule.Execute(() => applied = Apply(page, applied)).Every(PollMs);
        }

        static Rect Apply(VisualElement page, Rect applied)
        {
            var panel = page.panel;
            if (panel == null) return applied;
            var screen = Override ?? UnityEngine.Device.Screen.safeArea;
            var size = panel.visualTree.layout.size;
            if (screen == applied || float.IsNaN(size.x) || size.x <= 0f) return applied;
            var height = UnityEngine.Device.Screen.height;
            var min = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.xMin, height - screen.yMax));
            var max = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.xMax, height - screen.yMin));
            SetInsets(page, min.x, min.y, size.x - max.x, size.y - max.y);
            return screen;
        }

        static void SetInsets(VisualElement page, float left, float top, float right, float bottom)
        {
            left = Clean(left);
            top = Clean(top);
            right = Clean(right);
            bottom = Clean(bottom);
            var inset = left > 0f || top > 0f || right > 0f || bottom > 0f;
            if (!inset && !HasInlineInsets(page)) return;
            Place(page.style, left, top, right, bottom);
            page.Query(className: "bleed").ForEach(e => Place(e.style, -left, -top, -right, -bottom));
        }

        static float Clean(float inset) => inset >= MinInset ? inset : 0f;

        static bool HasInlineInsets(VisualElement page) => page.style.left.keyword == StyleKeyword.Undefined;

        static void Place(IStyle style, float left, float top, float right, float bottom)
        {
            style.left = left;
            style.top = top;
            style.right = right;
            style.bottom = bottom;
        }
    }
}
