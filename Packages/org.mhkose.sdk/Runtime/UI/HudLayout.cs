using System.Collections.Generic;
using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Applies the HUD part of a <see cref="UiLayoutSettings"/> to a RaceHud.uxml tree: moves (translate), scales and fades
    /// the elements named by <see cref="ElementName"/>. An element that was never placed keeps its stylesheet place, so
    /// the default layout writes no styles. The minimap keeps its own size (<see cref="ScalesBox"/>). Shared by the race
    /// HUD and the UI Layout screen. Positions are in the HUD element's coordinates (UI reference pixels).
    /// </summary>
    public sealed class HudLayout
    {
        readonly VisualElement hud;
        readonly VisualElement[] elements = new VisualElement[UiLayoutSettings.HudTypes.Length];
        readonly HudElementSetup[] setups = new HudElementSetup[UiLayoutSettings.HudTypes.Length];
        readonly float ghostOpacity;

        /// <summary>
        /// Binds the elements below <paramref name="hud"/>. <paramref name="ghostOpacity"/> above 0 draws hidden elements
        /// with that opacity instead of hiding them (UI Layout screen).
        /// </summary>
        public HudLayout(VisualElement hud, float ghostOpacity = 0f)
        {
            this.hud = hud;
            this.ghostOpacity = ghostOpacity;
            for (var i = 0; i < elements.Length; i++)
            {
                var e = hud.Q<VisualElement>(ElementName(UiLayoutSettings.HudTypes[i]));
                elements[i] = e;
                var index = i;
                e?.RegisterCallback<GeometryChangedEvent>(_ => Place(index));
            }
            hud.RegisterCallback<GeometryChangedEvent>(_ => PlaceAll());
        }

        /// <summary>Size of the HUD (the race screen inside the safe area).</summary>
        public Vector2 ScreenSize => hud.layout.size;

        /// <summary>Name of the RaceHud.uxml element of <paramref name="type"/>.</summary>
        public static string ElementName(HudElementType type) => type switch
        {
            HudElementType.Position => "position",
            HudElementType.Item => "item-slot",
            HudElementType.Lap => "lap-row",
            HudElementType.Time => "time-row",
            HudElementType.BestLap => "best-slot",
            HudElementType.Split => "split-slot",
            HudElementType.Health => "health-slot",
            HudElementType.Speedometer => "speedometer",
            HudElementType.Minimap => "minimap",
            HudElementType.Replay => "replay-plate",
            HudElementType.Countdown => "countdown-slot",
            HudElementType.FinalLap => "banner-slot",
            HudElementType.WrongWay => "warn-slot",
            _ => "flash-slot",
        };

        /// <summary>The element's scale sizes its own box (the minimap) instead of scaling it as a picture.</summary>
        public static bool ScalesBox(HudElementType type) => type == HudElementType.Minimap;

        /// <summary>The element of <paramref name="type"/>, null when the tree has none.</summary>
        public VisualElement Element(HudElementType type) => elements[(int)type];

        /// <summary>Applies every HUD element of <paramref name="layout"/>.</summary>
        public void ApplyAll(UiLayoutSettings layout)
        {
            for (var i = 0; i < elements.Length; i++) Apply(layout.Hud(UiLayoutSettings.HudTypes[i]));
        }

        /// <summary>Applies the HUD elements in <paramref name="layout"/>.</summary>
        public void ApplyAll(IReadOnlyList<HudElementSetup> layout)
        {
            for (var i = 0; i < layout.Count; i++) Apply(layout[i]);
        }

        /// <summary>Shows, fades and places the element of <paramref name="setup"/> (again after a change).</summary>
        public void Apply(HudElementSetup setup)
        {
            var i = (int)setup.type;
            if (i < 0 || i >= elements.Length || elements[i] == null) return;
            setups[i] = setup;
            var e = elements[i];
            var ghost = ghostOpacity > 0f && setup.shown;
            var opacity = Mathf.Clamp01(setup.opacity);
            if (ghost) opacity = setup.visible ? Mathf.Max(opacity, ghostOpacity) : ghostOpacity;
            var drawn = ghost || setup.Drawn;
            e.style.visibility = drawn ? new StyleEnum<Visibility>(StyleKeyword.Null) : Visibility.Hidden;
            e.style.opacity = opacity >= 1f ? new StyleFloat(StyleKeyword.Null) : opacity;
            Place(i);
        }

        /// <summary>The element's rectangle as drawn (place and scale), in HUD coordinates. Zero when it has none.</summary>
        public Rect Bounds(HudElementType type)
        {
            var i = (int)type;
            var e = elements[i];
            var s = setups[i];
            if (e == null || s == null || !TryNatural(e, out var natural, out var size)) return Rect.zero;
            var k = Scale(s);
            return new Rect(Target(s, natural, size, k, ScreenSize), Vector2.Scale(size, k));
        }

        /// <summary>
        /// Places <paramref name="setup"/> so its drawn rectangle is <paramref name="rect"/> (HUD coordinates, at its current
        /// scale): the anchor is the screen third its centre is in, per axis.
        /// </summary>
        public static void Anchor(HudElementSetup setup, Rect rect, Vector2 screen)
        {
            setup.anchor = Thirds(rect.center, screen);
            setup.offset = rect.position + Vector2.Scale(setup.anchor, rect.size) - Vector2.Scale(setup.anchor, screen);
            setup.placed = true;
        }

        void PlaceAll()
        {
            for (var i = 0; i < elements.Length; i++) Place(i);
        }

        void Place(int i)
        {
            var e = elements[i];
            var s = setups[i];
            if (e == null || s == null) return;
            var k = Scale(s);
            if (!s.placed && k == Vector2.one)
            {
                e.style.translate = StyleKeyword.Null;
                e.style.scale = StyleKeyword.Null;
                e.style.transformOrigin = StyleKeyword.Null;
                return;
            }
            var screen = ScreenSize;
            if (!TryNatural(e, out var natural, out var size) || float.IsNaN(screen.x)) return;
            var target = Target(s, natural, size, k, screen);
            e.style.transformOrigin = new TransformOrigin(0f, 0f);
            e.style.scale = new Scale(new Vector3(k.x, k.y, 1f));
            e.style.translate = new Translate(target.x - natural.x, target.y - natural.y);
        }

        static Vector2 Scale(HudElementSetup s) =>
            ScalesBox(s.type) ? Vector2.one : new Vector2(Mathf.Max(0.01f, s.scale.x), Mathf.Max(0.01f, s.scale.y));

        // Top left corner of the drawn element: its anchor point sits at the screen anchor point plus the offset. An element
        // scaled without a place keeps the point of its default rectangle that is nearest to its screen third.
        static Vector2 Target(HudElementSetup s, Vector2 natural, Vector2 size, Vector2 k, Vector2 screen)
        {
            var drawn = Vector2.Scale(size, k);
            if (s.placed) return Vector2.Scale(s.anchor, screen) + s.offset - Vector2.Scale(s.anchor, drawn);
            var pivot = Thirds(natural + size * 0.5f, screen);
            return natural + Vector2.Scale(pivot, size - drawn);
        }

        static Vector2 Thirds(Vector2 point, Vector2 screen) => new(Third(point.x, screen.x), Third(point.y, screen.y));

        static float Third(float v, float length) => v < length / 3f ? 0f : v > length * 2f / 3f ? 1f : 0.5f;

        // Layout position below the HUD element without transforms (fly-in, pops and the layout's own translate).
        bool TryNatural(VisualElement e, out Vector2 position, out Vector2 size)
        {
            position = Vector2.zero;
            size = e.layout.size;
            if (float.IsNaN(size.x) || float.IsNaN(size.y)) return false;
            for (var v = e; v != null && v != hud; v = v.parent) position += v.layout.position;
            return !float.IsNaN(position.x) && !float.IsNaN(position.y);
        }
    }
}
