using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Race HUD speedometer: a Painter2D arc gauge with the speed digits in the centre. The plate, track and ticks are
    /// drawn once per resize, <see cref="SetSpeed"/> only repaints the fill layer when the shown integer changes.
    /// </summary>
    [UxmlElement]
    public partial class HudSpeedometer : VisualElement
    {
        const float StartDeg = 150f, SweepDeg = 240f, EndDeg = StartDeg + SweepDeg;
        const int Ticks = 12;
        const float Inset = 3f, MinRadius = 20f;
        const float RimShadeWidth = 6f, RimWidth = 3f;
        const float ArcInset = 34f, ArcWidthShare = 0.17f, MinArcWidth = 10f, MaxArcWidth = 24f, TrackExtraWidth = 6f;
        const float RedlineWidth = 5f, RedlineGap = 7f, RedlineStart = 0.85f;
        const float TickInset = 9f, MajorTickLength = 11f, MinorTickLength = 6f, MajorTickWidth = 3f, MinorTickWidth = 2f;
        const float MinFill = 0.004f;
        const float HotFraction = 0.9f, FillHotFrom = 0.75f, FillHotRange = 0.17f;

        static readonly CustomStyleProperty<Color> PlateProp = new("--speedo-plate"), RimProp = new("--speedo-rim"),
            RimShadeProp = new("--speedo-rim-shade"), TrackProp = new("--speedo-track"), RedlineProp = new("--speedo-redline"),
            TickProp = new("--speedo-tick"), FillProp = new("--speedo-fill"), FillHotProp = new("--speedo-fill-hot");

        Color plateColor = new(0.09f, 0.098f, 0.114f, 0.72f), rimColor = new(1f, 1f, 1f, 0.9f), rimShade = new(0.09f, 0.098f, 0.114f, 0.85f);
        Color trackColor = new(1f, 1f, 1f, 0.16f), redlineColor = new(1f, 0.706f, 0f, 1f), tickColor = new(1f, 1f, 1f, 0.7f);
        Color fillColor = Color.white, fillHotColor = new(1f, 0.706f, 0f, 1f);

        readonly VisualElement fillLayer;
        readonly Label digits;
        int shownSpeed = -1;
        float maxKmh = 140f;
        bool hot, touch;

        public HudSpeedometer()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawStatic;
            AddToClassList("hud-speedo");
            fillLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            fillLayer.StretchToParentSize();
            fillLayer.generateVisualContent += DrawFill;
            Add(fillLayer);
            digits = new Label("0") { pickingMode = PickingMode.Ignore };
            digits.AddToClassList("hud-speedo-value");
            Add(digits);
            var unit = new Label("km/h") { pickingMode = PickingMode.Ignore };
            unit.AddToClassList("hud-speedo-unit");
            Add(unit);
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyle);
        }

        void OnCustomStyle(CustomStyleResolvedEvent e)
        {
            var s = e.customStyle;
            s.ReadColor(PlateProp, ref plateColor);
            s.ReadColor(RimProp, ref rimColor);
            s.ReadColor(RimShadeProp, ref rimShade);
            s.ReadColor(TrackProp, ref trackColor);
            s.ReadColor(RedlineProp, ref redlineColor);
            s.ReadColor(TickProp, ref tickColor);
            s.ReadColor(FillProp, ref fillColor);
            s.ReadColor(FillHotProp, ref fillHotColor);
            MarkDirtyRepaint();
            fillLayer.MarkDirtyRepaint();
        }

        /// <summary>Touch layout (<c>.touch</c>): smaller gauge, above the steering buttons.</summary>
        [UxmlAttribute]
        public bool TouchLayout
        {
            get => touch;
            set
            {
                touch = value;
                EnableInClassList("touch", value);
            }
        }

        /// <summary>Shown speed (km/h), set through <see cref="SetSpeed"/>.</summary>
        [UxmlAttribute]
        public int Speed
        {
            get => Mathf.Max(0, shownSpeed);
            set => SetSpeed(value);
        }

        /// <summary>Speed at the end of the scale (km/h).</summary>
        [UxmlAttribute]
        public float MaxKmh
        {
            get => maxKmh;
            set
            {
                var v = Mathf.Max(10f, value);
                if (Mathf.Approximately(v, maxKmh)) return;
                maxKmh = v;
                UpdateHot();
                fillLayer.MarkDirtyRepaint();
            }
        }

        /// <summary>Shows <paramref name="kmh"/> (absolute, rounded), no-op when unchanged.</summary>
        public void SetSpeed(int kmh)
        {
            if (kmh < 0) kmh = -kmh;
            if (kmh == shownSpeed) return;
            shownSpeed = kmh;
            // The UXML speed attribute sets this while the tree is cloned, before it is in a panel.
            if (panel == null) digits.text = kmh.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else digits.SetText(kmh);
            UpdateHot();
            fillLayer.MarkDirtyRepaint();
        }

        void UpdateHot()
        {
            var h = shownSpeed >= maxKmh * HotFraction;
            if (h == hot) return;
            hot = h;
            EnableInClassList("hot", h);
        }

        Color FillColor(float frac) => Color.Lerp(fillColor, fillHotColor, Mathf.Clamp01((frac - FillHotFrom) / FillHotRange));

        bool TryGeometry(Rect rect, out Vector2 centre, out float radius, out float arcRadius, out float arcWidth)
        {
            centre = rect.center;
            radius = Mathf.Min(rect.width, rect.height) * 0.5f - Inset;
            arcRadius = radius - ArcInset;
            arcWidth = Mathf.Clamp(radius * ArcWidthShare, MinArcWidth, MaxArcWidth);
            return radius >= MinRadius;
        }

        void DrawStatic(MeshGenerationContext ctx)
        {
            if (!TryGeometry(contentRect, out var c, out var radius, out var arcRadius, out var arcWidth)) return;
            var p = ctx.painter2D;
            DrawPlate(p, c, radius);
            DrawTrackAndRedline(p, c, arcRadius, arcWidth);
            DrawTicks(p, c, radius);
        }

        void DrawPlate(Painter2D p, Vector2 c, float radius)
        {
            p.fillColor = plateColor;
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f));
            p.ClosePath();
            p.Fill();
            StrokeCircle(p, c, radius - 1f, RimShadeWidth, rimShade);
            StrokeCircle(p, c, radius - 2f, RimWidth, rimColor);
        }

        static void StrokeCircle(Painter2D p, Vector2 c, float radius, float width, Color color)
        {
            p.lineWidth = width;
            p.strokeColor = color;
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f));
            p.ClosePath();
            p.Stroke();
        }

        void DrawTrackAndRedline(Painter2D p, Vector2 c, float arcRadius, float arcWidth)
        {
            p.lineCap = LineCap.Round;
            StrokeArc(p, c, arcRadius, StartDeg, EndDeg, arcWidth + TrackExtraWidth, trackColor);
            p.lineCap = LineCap.Butt;
            StrokeArc(p, c, arcRadius + arcWidth * 0.5f + RedlineGap, StartDeg + SweepDeg * RedlineStart, EndDeg, RedlineWidth, redlineColor);
        }

        void DrawTicks(Painter2D p, Vector2 c, float radius)
        {
            p.strokeColor = tickColor;
            var outer = radius - TickInset;
            for (var i = 0; i <= Ticks; i++)
            {
                var major = i % 2 == 0;
                var a = (StartDeg + SweepDeg * i / Ticks) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                p.lineWidth = major ? MajorTickWidth : MinorTickWidth;
                p.BeginPath();
                p.MoveTo(c + dir * (outer - (major ? MajorTickLength : MinorTickLength)));
                p.LineTo(c + dir * outer);
                p.Stroke();
            }
        }

        void DrawFill(MeshGenerationContext ctx)
        {
            if (!TryGeometry(this.ChangeCoordinatesTo(fillLayer, contentRect), out var c, out _, out var arcRadius, out var arcWidth)) return;
            var frac = Mathf.Clamp01(Mathf.Max(0, shownSpeed) / maxKmh);
            if (frac <= MinFill) return;
            var p = ctx.painter2D;
            var fillEnd = StartDeg + SweepDeg * frac;
            p.lineCap = LineCap.Round;
            StrokeArc(p, c, arcRadius, StartDeg, fillEnd, arcWidth, FillColor(frac));
        }

        static void StrokeArc(Painter2D p, Vector2 c, float radius, float fromDeg, float toDeg, float width, Color color)
        {
            p.lineWidth = width;
            p.strokeColor = color;
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(fromDeg), Angle.Degrees(toDeg));
            p.Stroke();
        }
    }
}
