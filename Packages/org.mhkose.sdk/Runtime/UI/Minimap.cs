using System.Collections.Generic;
using MHK.Karts;
using MHK.Race;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Race minimap: the track drawn once per race with Painter2D from the racing line and the alternate routes
    /// flagged for the minimap, plus a dot per kart and <see cref="MinimapBlip"/>. Call <see cref="Tick"/> once per frame.
    /// </summary>
    [UxmlElement]
    public partial class Minimap : VisualElement
    {
        const int SampleNodes = -2, SamplePoints = 72;
        const float SampleRadiusX = 120f, SampleRadiusY = 65f, SampleWaveX = 10f, SampleDent = 60f, SampleDentShift = 0.5f;
        const float SampleWidth = 10f;
        static readonly float[] SampleKarts = { 1.5f, 2.6f, 4.2f, 0.4f };

        // Largest map box at scale 1 and the space kept free above it, in reference-resolution (1920x1080) panel pixels.
        const float BaseWidth = 300f, BaseHeight = 220f, TopReserve = 260f, MinBoxHeight = 80f;
        // Road stroke: the median drivable width in map pixels, clamped (per-node widths would draw blobs at forks).
        const float MinStrokePx = 5f, MaxStrokePx = 12f, AbsoluteMinStrokePx = 3f;
        const float OutlinePx = 2.5f, ShadowPx = 2f, ShadowOffsetPx = 3f;
        const float DefaultWidth = 8f, UnsetWidth = 1.01f, MinPxPerMetre = 0.01f;
        const float DotPx = 17f, MinDotPx = 11f, TouchDotFactor = 1.25f, MinBlipScale = 0.2f;
        const float RingBorderShare = 0.24f, MinRingBorder = 2.5f, DotBorderShare = 0.14f, MinDotBorder = 1.5f;
        const float EliminatedAlpha = 0.35f;
        const int RotationSteps = 60;
        const float RotationStepDeg = 3f, RotationStartDeg = -87f, RotationTolerance = 0.96f;

        static readonly CustomStyleProperty<Color> TrackProp = new("--minimap-track"), OutlineProp = new("--minimap-outline"),
            ShadowProp = new("--minimap-shadow"), PlayerProp = new("--minimap-player"), OpponentProp = new("--minimap-opponent"),
            DotBorderProp = new("--minimap-dot-border"), RingFillProp = new("--minimap-ring-fill");

        Color trackColor = new(0.96f, 0.965f, 0.973f, 1f), outlineColor = new(0.09f, 0.098f, 0.114f, 0.92f), shadowColor = new(0f, 0f, 0f, 0.4f);
        Color playerColor = new(0.2f, 0.92f, 0.25f, 1f), opponentColor = new(0.95f, 0.15f, 0.12f, 1f);
        Color dotBorderColor = new(0.09f, 0.098f, 0.114f, 0.95f), ringFillColor = new(0.09f, 0.098f, 0.114f, 0.35f);

        struct Line
        {
            /// <summary>Rotated top-down coordinates in metres.</summary>
            public Vector2[] points;
            public bool closed;
        }

        sealed class Dot
        {
            public VisualElement element;
            public float size = -1f;
            public Color color = Color.clear;
            public bool visible = true;
            public bool ring;
        }

        bool touch;
        readonly VisualElement map, trackLayer;
        readonly List<Line> lines = new();
        readonly List<Dot> dots = new();
        readonly float[] fits = new float[RotationSteps];

        TrackRuntime source;
        int sourceNodes = -1;
        float cos = 1f, sin;
        float minX, minY, maxX, maxY, medianWidth = DefaultWidth;
        float pxPerMetre, pad, strokePx, width, height, dotSize;
        Vector2 appliedScale = -Vector2.one;
        float appliedAvailable = -1f;
        bool shown = true;

        public Minimap()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("hud-minimap");
            map = new VisualElement { pickingMode = PickingMode.Ignore };
            map.AddToClassList("hud-minimap-map");
            Add(map);
            trackLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            trackLayer.AddToClassList("hud-minimap-track");
            trackLayer.generateVisualContent += DrawTrack;
            map.Add(trackLayer);
            if (Application.isPlaying) SetShown(false);
            else
            {
                map.style.width = BaseWidth;
                map.style.height = BaseHeight;
            }
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyle);
        }

        void OnCustomStyle(CustomStyleResolvedEvent e)
        {
            var s = e.customStyle;
            s.ReadColor(TrackProp, ref trackColor);
            s.ReadColor(OutlineProp, ref outlineColor);
            s.ReadColor(ShadowProp, ref shadowColor);
            s.ReadColor(PlayerProp, ref playerColor);
            s.ReadColor(OpponentProp, ref opponentColor);
            s.ReadColor(DotBorderProp, ref dotBorderColor);
            s.ReadColor(RingFillProp, ref ringFillColor);
            trackLayer.MarkDirtyRepaint();
            for (var i = 0; i < dots.Count; i++) dots[i].color = Color.clear;
        }

        /// <summary>Touch controls on (<c>.touch</c>): placed above the touch buttons by default, larger kart dots.</summary>
        [UxmlAttribute]
        public bool TouchMode
        {
            get => touch;
            set
            {
                if (touch == value) return;
                touch = value;
                EnableInClassList("touch", value);
                appliedScale = -Vector2.one;
            }
        }

        /// <summary>Per-frame update. <paramref name="scale"/> is the map box size per axis (UI layout, 1 = default size).</summary>
        public void Tick(Vector2 scale)
        {
            var track = TrackRuntime.Current;
            if (track != source || CountNodes(track) != sourceNodes) Generate(track);
            if (lines.Count == 0)
            {
                SetShown(false);
                return;
            }
            SetShown(true);
            Fit(scale);
            UpdateDots();
        }

        /// <summary>Per-frame update in the UI Layout screen: a sample track with a few karts, box size <paramref name="scale"/>.</summary>
        public void TickSample(Vector2 scale)
        {
            if (source != null || sourceNodes != SampleNodes) GenerateSample();
            SetShown(true);
            Fit(scale);
            for (var i = 0; i < SampleKarts.Length; i++)
            {
                var p = SamplePoint(SampleKarts[i]);
                SetDot(i, new Vector3(p.x, 0f, p.y), i == SampleKarts.Length - 1 ? playerColor : opponentColor, dotSize);
            }
        }

        void Fit(Vector2 scale)
        {
            var available = parent != null ? parent.layout.height : float.NaN;
            if (float.IsNaN(available)) available = 0f;
            if (scale != appliedScale || !Mathf.Approximately(available, appliedAvailable)) Relayout(scale, available);
        }

        void SetShown(bool value)
        {
            if (shown == value) return;
            shown = value;
            this.SetVisible(value);
        }

        static int CountNodes(TrackRuntime track)
        {
            if (track == null) return -1;
            var n = track.path != null ? track.path.Count : 0;
            var alternates = track.alternatePaths;
            if (alternates != null)
                for (var i = 0; i < alternates.Count; i++)
                    if (Shows(alternates[i])) n += alternates[i].Count;
            return n;
        }

        static bool Shows(TrackPath route) => route != null && route.showOnMinimap;

        void Generate(TrackRuntime track)
        {
            source = track;
            sourceNodes = CountNodes(track);
            lines.Clear();
            appliedScale = -Vector2.one;
            if (track == null) return;
            AddPath(track.path);
            medianWidth = MedianWidth(track.path);
            var alternates = track.alternatePaths;
            if (alternates != null)
                for (var i = 0; i < alternates.Count; i++)
                    if (Shows(alternates[i])) AddPath(alternates[i]);
            if (lines.Count > 0) FitLines();
        }

        void GenerateSample()
        {
            source = null;
            sourceNodes = SampleNodes;
            lines.Clear();
            appliedScale = -Vector2.one;
            var points = new Vector2[SamplePoints];
            for (var i = 0; i < SamplePoints; i++) points[i] = SamplePoint(i * 2f * Mathf.PI / SamplePoints);
            lines.Add(new Line { points = points, closed = true });
            medianWidth = SampleWidth;
            FitLines();
        }

        static Vector2 SamplePoint(float t) => new(SampleRadiusX * Mathf.Cos(t) + SampleWaveX * Mathf.Cos(3f * t),
            SampleRadiusY * Mathf.Sin(t) - SampleDent * Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t + SampleDentShift)), 8f));

        void FitLines()
        {
            ChooseRotation();
            for (var l = 0; l < lines.Count; l++)
            {
                var pts = lines[l].points;
                for (var i = 0; i < pts.Length; i++) pts[i] = Rotate(pts[i]);
            }
            Bounds(1f, 0f, out minX, out minY, out maxX, out maxY);
            trackLayer.MarkDirtyRepaint();
        }

        void AddPath(TrackPath path)
        {
            if (path == null || path.Count < 2) return;
            var n = path.Count;
            var line = new Line { points = new Vector2[n], closed = path.closed };
            for (var i = 0; i < n; i++)
            {
                var p = path.GetNodePosition(i);
                line.points[i] = new Vector2(p.x, p.z);
            }
            lines.Add(line);
        }

        static float MedianWidth(TrackPath path)
        {
            if (path == null || path.Count == 0) return DefaultWidth;
            var widths = new List<float>(path.Count);
            for (var i = 0; i < path.Count; i++)
            {
                var w = path.GetNodeWidth(i);
                if (w > UnsetWidth) widths.Add(w);
            }
            if (widths.Count == 0) return DefaultWidth;
            widths.Sort();
            return widths[widths.Count / 2];
        }

        void ChooseRotation()
        {
            var bestFit = 0f;
            for (var step = 0; step < RotationSteps; step++)
            {
                // -87..90 degrees (+180 would only flip the map). Near-equal fits prefer north-up below.
                var a = StepDegrees(step) * Mathf.Deg2Rad;
                Bounds(Mathf.Cos(a), Mathf.Sin(a), out var x0, out var y0, out var x1, out var y1);
                var fit = Mathf.Min(BaseWidth / Mathf.Max(x1 - x0, 1f), BaseHeight / Mathf.Max(y1 - y0, 1f));
                fits[step] = fit;
                bestFit = Mathf.Max(bestFit, fit);
            }
            var bestAngle = 0f;
            var bestAbs = float.MaxValue;
            for (var step = 0; step < RotationSteps; step++)
            {
                var deg = StepDegrees(step);
                if (fits[step] < bestFit * RotationTolerance || Mathf.Abs(deg) >= bestAbs) continue;
                bestAbs = Mathf.Abs(deg);
                bestAngle = deg;
            }
            cos = Mathf.Cos(bestAngle * Mathf.Deg2Rad);
            sin = Mathf.Sin(bestAngle * Mathf.Deg2Rad);
        }

        static float StepDegrees(int step) => step * RotationStepDeg + RotationStartDeg;

        void Bounds(float c, float s, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = float.MaxValue;
            x1 = y1 = float.MinValue;
            for (var l = 0; l < lines.Count; l++)
            {
                var pts = lines[l].points;
                for (var i = 0; i < pts.Length; i++)
                {
                    var p = pts[i];
                    var rx = p.x * c - p.y * s;
                    var ry = p.x * s + p.y * c;
                    x0 = Mathf.Min(x0, rx);
                    x1 = Mathf.Max(x1, rx);
                    y0 = Mathf.Min(y0, ry);
                    y1 = Mathf.Max(y1, ry);
                }
            }
        }

        Vector2 Rotate(Vector2 p) => new(p.x * cos - p.y * sin, p.x * sin + p.y * cos);

        void Relayout(Vector2 scale, float available)
        {
            appliedScale = scale;
            appliedAvailable = available;
            var w = Mathf.Max(maxX - minX, 1f);
            var h = Mathf.Max(maxY - minY, 1f);
            var boxW = BaseWidth * scale.x;
            var boxH = BaseHeight * scale.y;
            if (available > 0f) boxH = Mathf.Min(boxH, Mathf.Max(MinBoxHeight, available - BottomAndFrame() - TopReserve));
            var lineScale = Mathf.Min(scale.x, scale.y);
            var maxStroke = Mathf.Max(AbsoluteMinStrokePx, MaxStrokePx * lineScale);
            var minStroke = Mathf.Max(AbsoluteMinStrokePx, MinStrokePx * lineScale);
            pad = 0.5f * maxStroke + OutlinePx + ShadowPx + ShadowOffsetPx;
            pxPerMetre = Mathf.Max(MinPxPerMetre, Mathf.Min((boxW - 2f * pad) / w, (boxH - 2f * pad) / h));
            strokePx = Mathf.Clamp(medianWidth * pxPerMetre, minStroke, maxStroke);
            width = w * pxPerMetre + 2f * pad;
            height = h * pxPerMetre + 2f * pad;
            map.style.width = width;
            map.style.height = height;
            dotSize = Mathf.Max(MinDotPx, DotPx * Mathf.Sqrt(lineScale)) * (touch ? TouchDotFactor : 1f);
            trackLayer.MarkDirtyRepaint();
        }

        float BottomAndFrame()
        {
            var bottom = resolvedStyle.bottom;
            if (float.IsNaN(bottom)) bottom = 0f;
            var frame = resolvedStyle.paddingTop + resolvedStyle.paddingBottom + resolvedStyle.borderTopWidth + resolvedStyle.borderBottomWidth;
            if (float.IsNaN(frame)) frame = 0f;
            return bottom + frame;
        }

        Vector2 MapRotated(Vector2 r) => new((r.x - minX) * pxPerMetre + pad, (maxY - r.y) * pxPerMetre + pad);

        void DrawTrack(MeshGenerationContext ctx)
        {
            if (lines.Count == 0 || pxPerMetre <= 0f) return;
            var painter = ctx.painter2D;
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;
            // Each pass covers every line before the next starts, so crossings and branches merge cleanly.
            var outline = strokePx + 2f * OutlinePx;
            StrokeLines(painter, shadowColor, outline + 2f * ShadowPx, ShadowOffsetPx);
            StrokeLines(painter, outlineColor, outline, 0f);
            StrokeLines(painter, trackColor, strokePx, 0f);
        }

        void StrokeLines(Painter2D painter, Color color, float lineWidth, float dy)
        {
            painter.strokeColor = color;
            painter.lineWidth = lineWidth;
            for (var l = 0; l < lines.Count; l++) StrokeLine(painter, lines[l], dy);
        }

        void StrokeLine(Painter2D painter, in Line line, float dy)
        {
            var pts = line.points;
            var offset = new Vector2(0f, dy);
            painter.BeginPath();
            painter.MoveTo(MapRotated(pts[0]) + offset);
            for (var i = 1; i < pts.Length; i++) painter.LineTo(MapRotated(pts[i]) + offset);
            if (line.closed) painter.ClosePath();
            painter.Stroke();
        }

        void UpdateDots()
        {
            var used = 0;
            var blips = MinimapBlip.All;
            for (var i = 0; i < blips.Count; i++)
            {
                var b = blips[i];
                if (b == null) continue;
                SetDot(used++, b.transform.position, b.color, dotSize * Mathf.Max(MinBlipScale, b.size), b.ring);
            }
            // Opponents first, humans last so the player's dot is drawn on top.
            var karts = Kart.All;
            for (var pass = 0; pass < 2; pass++)
            {
                var humans = pass == 1;
                for (var i = 0; i < karts.Count; i++)
                {
                    var k = karts[i];
                    if (k == null || k.IsHuman != humans) continue;
                    var c = humans ? playerColor : opponentColor;
                    if (k.Eliminated) c.a = EliminatedAlpha;
                    SetDot(used++, k.transform.position, c, dotSize);
                }
            }
            for (var i = used; i < dots.Count; i++)
            {
                var d = dots[i];
                if (!d.visible) continue;
                d.visible = false;
                d.element.SetVisible(false);
            }
        }

        void SetDot(int index, Vector3 world, Color color, float size, bool ring = false)
        {
            var d = EnsureDot(index);
            if (!d.visible)
            {
                d.visible = true;
                d.element.SetVisible(true);
            }
            var restyle = d.ring != ring;
            d.ring = ring;
            if (restyle || !Mathf.Approximately(d.size, size)) ApplyDotSize(d, size, ring);
            if (restyle || d.color != color) ApplyDotColor(d, color, ring);
            var m = MapRotated(Rotate(new Vector2(world.x, world.z)));
            m.x = Mathf.Clamp(m.x, 0f, width);
            m.y = Mathf.Clamp(m.y, 0f, height);
            d.element.style.translate = new Translate(m.x - size * 0.5f, m.y - size * 0.5f);
        }

        Dot EnsureDot(int index)
        {
            while (dots.Count <= index)
            {
                var e = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                e.AddToClassList("hud-minimap-dot");
                map.Add(e);
                dots.Add(new Dot { element = e });
            }
            return dots[index];
        }

        static void ApplyDotSize(Dot d, float size, bool ring)
        {
            d.size = size;
            var s = d.element.style;
            s.width = size;
            s.height = size;
            var border = ring ? Mathf.Max(MinRingBorder, size * RingBorderShare) : Mathf.Max(MinDotBorder, size * DotBorderShare);
            s.borderLeftWidth = border;
            s.borderRightWidth = border;
            s.borderTopWidth = border;
            s.borderBottomWidth = border;
        }

        void ApplyDotColor(Dot d, Color color, bool ring)
        {
            d.color = color;
            var s = d.element.style;
            s.backgroundColor = ring ? ringFillColor : color;
            var border = ring ? color : dotBorderColor;
            if (!ring) border.a *= color.a;
            s.borderLeftColor = border;
            s.borderRightColor = border;
            s.borderTopColor = border;
            s.borderBottomColor = border;
        }
    }
}
