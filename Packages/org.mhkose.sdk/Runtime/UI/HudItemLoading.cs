using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Race HUD item loading timer: a Painter2D pie over the grayed item icon while a battle item loads. The loaded part
    /// grows clockwise from 12 o'clock over a faint disc. <see cref="SetProgress"/> only repaints when the shown slice
    /// changes by a whole step.
    /// </summary>
    [UxmlElement]
    public partial class HudItemLoading : VisualElement
    {
        const int Steps = 120;
        const float TopDeg = -90f;

        static readonly CustomStyleProperty<Color> FillProp = new("--item-load-fill"), TrackProp = new("--item-load-track");

        Color fillColor = new(1f, 1f, 1f, 0.55f), trackColor = new(0.09f, 0.098f, 0.114f, 0.45f);
        int shownStep = -1;

        public HudItemLoading()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            AddToClassList("hud-item-loading");
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyle);
        }

        void OnCustomStyle(CustomStyleResolvedEvent e)
        {
            var s = e.customStyle;
            s.ReadColor(FillProp, ref fillColor);
            s.ReadColor(TrackProp, ref trackColor);
            MarkDirtyRepaint();
        }

        /// <summary>Shown loading progress (0 to 1), set through <see cref="SetProgress"/>.</summary>
        [UxmlAttribute]
        public float Progress
        {
            get => Mathf.Max(0, shownStep) / (float)Steps;
            set => SetProgress(value);
        }

        /// <summary>Shows <paramref name="fraction"/> (0 to 1) loaded, no-op while the slice stays on the same step.</summary>
        public void SetProgress(float fraction)
        {
            var step = Mathf.RoundToInt(Mathf.Clamp01(fraction) * Steps);
            if (step == shownStep) return;
            shownStep = step;
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            var rect = contentRect;
            var radius = Mathf.Min(rect.width, rect.height) * 0.5f;
            if (radius <= 0f) return;
            var c = rect.center;
            var p = ctx.painter2D;
            p.fillColor = trackColor;
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f));
            p.ClosePath();
            p.Fill();
            if (shownStep <= 0) return;
            p.fillColor = fillColor;
            p.BeginPath();
            p.MoveTo(c);
            p.Arc(c, radius, Angle.Degrees(TopDeg), Angle.Degrees(TopDeg + 360f * shownStep / Steps));
            p.ClosePath();
            p.Fill();
        }
    }
}
