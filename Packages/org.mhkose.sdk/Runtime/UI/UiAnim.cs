using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace MHK.UI
{
    /// <summary>Animation helpers of the design system (MHKTheme.uss entry animations, pops and looping bob, pulse and spin).</summary>
    public static class UiAnim
    {
        /// <summary>One frame of the panel scheduler, the smallest useful delay (start state laid out before the target).</summary>
        public const int FrameMs = 16;
        /// <summary>Bob and pulse of the <c>.bob</c> / <c>.pulse</c> classes (<see cref="UiTree.PlayEntry"/>).</summary>
        public const float EntryBobPixels = 5f, EntryBobPeriod = 3.2f, EntryPulseAmount = 0.03f, EntryPulsePeriod = 1.2f;

        /// <summary>Seconds as whole milliseconds (scheduler and transition delays).</summary>
        public static long Ms(float seconds) => (long)(seconds * 1000f);

        /// <summary>
        /// Entry animation (fade and slide, MHKTheme <c>.anim</c>): the element starts in the <c>.enter</c> state and
        /// animates to its laid-out state after <paramref name="delayMs"/> (0 keeps a transition-delay set in USS or UXML).
        /// <paramref name="from"/>: null (below), "from-left", "from-right" or "pop".
        /// </summary>
        public static T AnimateIn<T>(T e, long delayMs = 0, string from = null) where T : VisualElement
        {
            e.AddToClassList("anim");
            if (!string.IsNullOrEmpty(from)) e.AddToClassList(from);
            if (delayMs > 0) e.SetTransitionDelay(delayMs);
            e.AddToClassList("enter");
            e.schedule.Execute(() => e.RemoveFromClassList("enter")).ExecuteLater(FrameMs);
            return e;
        }

        /// <summary>Animates the children of <paramref name="container"/> in one after another.</summary>
        public static void Stagger(VisualElement container, long stepMs, long firstMs, string from = null)
        {
            var i = 0;
            foreach (var child in container.Children()) AnimateIn(child, firstMs + stepMs * i++, from);
        }

        /// <summary><see cref="Stagger(VisualElement, long, long, string)"/> with the timing in seconds (inspector values).</summary>
        public static void Stagger(VisualElement container, float stepSeconds, float firstSeconds, string from = null) =>
            Stagger(container, Ms(stepSeconds), Ms(firstSeconds), from);

        /// <summary>Scale pop: starts at <paramref name="from"/> and settles at 1 with an overshoot.</summary>
        public static void Pop(VisualElement e, float from, int durationMs)
        {
            e.experimental.animation.Start(from, 1f, durationMs, (el, v) => el.style.scale = new Scale(new Vector3(v, v, 1f))).Ease(Easing.OutBack);
        }

        /// <summary>Repeating vertical bob of <paramref name="pixels"/> with a period of <paramref name="period"/> seconds.</summary>
        public static IVisualElementScheduledItem Bob(VisualElement e, float pixels, float period)
        {
            var start = Time.realtimeSinceStartup;
            return e.schedule.Execute(() => e.style.translate = new Translate(0, Mathf.Sin(Phase(start, period)) * pixels)).Every(FrameMs);
        }

        /// <summary>Repeating scale pulse by <paramref name="amount"/> with a period of <paramref name="period"/> seconds.</summary>
        public static IVisualElementScheduledItem Pulse(VisualElement e, float amount, float period)
        {
            var start = Time.realtimeSinceStartup;
            return e.schedule.Execute(() =>
            {
                var s = 1f + Mathf.Sin(Phase(start, period)) * amount;
                e.style.scale = new Scale(new Vector3(s, s, 1f));
            }).Every(FrameMs);
        }

        /// <summary>Endless turn at <paramref name="degreesPerSecond"/> (winner sunburst).</summary>
        public static IVisualElementScheduledItem Spin(VisualElement e, float degreesPerSecond) =>
            e.schedule.Execute(() => e.style.rotate = new Rotate(Angle.Degrees(Time.realtimeSinceStartup * degreesPerSecond))).Every(FrameMs);

        static float Phase(float start, float period) => (Time.realtimeSinceStartup - start) / period * Mathf.PI * 2f;
    }
}
