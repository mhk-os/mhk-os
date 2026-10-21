using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>
    /// Frosted glass plates (<c>.panel</c> and <c>.page-footer</c>): each shows the part of <see cref="UiGlassBlur"/>'s
    /// blurred camera picture behind it as its background image, faded over its gray fill by the USS
    /// <c>-unity-background-image-tint-color</c>. With <c>.glass-lite</c> on the screen root the plates skip the blur.
    /// </summary>
    public static class UiGlass
    {
        const string LiteClass = "glass-lite";
        const string GlassClass = "glass";
        const string NoGlassClass = "hud-widget";
        static readonly string[] PlateClasses = { "panel", "page-footer" };

        /// <summary>True when plates skip the blur: mobile devices below the High preset.</summary>
        public static bool Lite => Application.isMobilePlatform && GraphicsQuality.Tier != GraphicsPreset.High;

        /// <summary>Sets the plate look of a screen root for the current graphics settings and makes its plates glass.</summary>
        public static void Apply(VisualElement root)
        {
            root.EnableInClassList(LiteClass, Lite);
            foreach (var plateClass in PlateClasses)
                root.Query(className: plateClass).ForEach(MakeGlass);
        }

        static void MakeGlass(VisualElement plate)
        {
            if (plate.ClassListContains(GlassClass) || plate.ClassListContains(NoGlassClass) || InsidePlate(plate)) return;
            plate.AddToClassList(GlassClass);
            new Plate(plate);
        }

        static bool InsidePlate(VisualElement e)
        {
            for (var p = e.parent; p != null; p = p.parent)
                foreach (var plateClass in PlateClasses)
                    if (p.ClassListContains(plateClass)) return true;
            return false;
        }

        // Keeps the plate's slice of the blurred picture lined up with the screen while the plate moves (entry animations).
        sealed class Plate
        {
            readonly VisualElement element;
            Rect placedRect;
            Vector2 placedScreen;
            int placedVersion = -1;
            bool acquired;

            public Plate(VisualElement element)
            {
                this.element = element;
                element.RegisterCallback<AttachToPanelEvent>(OnAttach);
                element.RegisterCallback<DetachFromPanelEvent>(OnDetach);
                if (element.panel != null) OnAttach(null);
                element.schedule.Execute(Track).Every(0);
            }

            void OnAttach(AttachToPanelEvent e)
            {
                if (acquired) return;
                acquired = true;
                placedVersion = -1;
                UiGlassBlur.Acquire();
            }

            void OnDetach(DetachFromPanelEvent e)
            {
                if (!acquired) return;
                acquired = false;
                UiGlassBlur.Release();
            }

            void Track()
            {
                var texture = UiGlass.Lite ? null : UiGlassBlur.Texture;
                var version = texture != null ? UiGlassBlur.Version : 0;
                if (element.panel == null) return;
                var rect = element.worldBound;
                var screen = element.panel.visualTree.layout.size;
                if (version == placedVersion && rect == placedRect && screen == placedScreen) return;
                placedVersion = version;
                placedRect = rect;
                placedScreen = screen;
                Place(texture, rect, screen);
            }

            void Place(RenderTexture texture, Rect rect, Vector2 screen)
            {
                var style = element.style;
                if (texture == null)
                {
                    style.backgroundImage = StyleKeyword.Null;
                    return;
                }
                style.backgroundImage = Background.FromRenderTexture(texture);
                style.backgroundSize = new BackgroundSize(screen.x, screen.y);
                style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Left, -rect.x);
                style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Top, -rect.y);
                style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            }
        }
    }
}
