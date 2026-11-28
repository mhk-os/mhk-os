using System;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MHK.UI
{
    /// <summary>
    /// A colour picker on two elements, each with a child of class <see cref="HandleClass"/>. The square shows the
    /// current hue with saturation across and brightness up, the bar shows every hue from top to bottom. The pointer
    /// drags in either. While one of them has the focus the arrows move its handle, and a move past the square's top or
    /// bottom edge (or sideways off the bar) leaves it. Owns its textures until <see cref="Dispose"/>.
    /// </summary>
    internal sealed class ColorPicker : IDisposable
    {
        public const string HandleClass = "picker-handle";
        // Texture sizes in pixels (stretched and filtered on screen) and the share one arrow press moves a handle.
        const int SquarePixels = 48, BarPixels = 128;
        const float Step = 0.04f;

        readonly VisualElement square, squareHandle, bar, barHandle;
        readonly Texture2D squareTexture, barTexture;
        readonly Color32[] squareColors = new Color32[SquarePixels * SquarePixels];
        float hue, saturation, brightness;
        float paintedHue = -1f;

        /// <summary>The player moved a handle.</summary>
        public event Action<Color> Changed;

        public Color Color => Color.HSVToRGB(hue, saturation, brightness);

        public ColorPicker(VisualElement square, VisualElement bar)
        {
            this.square = square;
            this.bar = bar;
            squareHandle = square.Q(className: HandleClass);
            barHandle = bar.Q(className: HandleClass);
            squareTexture = NewTexture("Colour picker square", SquarePixels, SquarePixels);
            barTexture = NewTexture("Colour picker hues", 1, BarPixels);
            var hues = new Color32[BarPixels];
            for (var y = 0; y < BarPixels; y++) hues[y] = Color.HSVToRGB(1f - y / (BarPixels - 1f), 1f, 1f);
            barTexture.SetPixels32(hues);
            barTexture.Apply(false, true);
            square.style.backgroundImage = new StyleBackground(squareTexture);
            bar.style.backgroundImage = new StyleBackground(barTexture);
            square.focusable = bar.focusable = true;
            foreach (var handle in new[] { squareHandle, barHandle }) handle?.NoPick();
            Track(square, PickInSquare);
            Track(bar, PickInBar);
            square.RegisterCallback<NavigationMoveEvent>(OnSquareNavigate);
            bar.RegisterCallback<NavigationMoveEvent>(OnBarNavigate);
        }

        /// <summary>Shows <paramref name="color"/> (no <see cref="Changed"/>). Grays and black keep the hue shown before.</summary>
        public void SetColor(Color color)
        {
            Color.RGBToHSV(color, out var h, out var s, out var v);
            if (s > 0f && v > 0f) hue = h;
            saturation = s;
            brightness = v;
            Refresh();
        }

        public void Dispose()
        {
            Object.Destroy(squareTexture);
            Object.Destroy(barTexture);
        }

        static Texture2D NewTexture(string name, int width, int height) =>
            new(width, height, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

        // Down captures the pointer, so a drag keeps picking outside the element until it is released.
        static void Track(VisualElement element, Action<Vector2> pick)
        {
            element.RegisterCallback<PointerDownEvent>(e =>
            {
                element.CapturePointer(e.pointerId);
                element.Focus();
                pick(Fraction(element, e.position));
                e.StopPropagation();
            });
            element.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (element.HasPointerCapture(e.pointerId)) pick(Fraction(element, e.position));
            });
            element.RegisterCallback<PointerUpEvent>(e =>
            {
                if (element.HasPointerCapture(e.pointerId)) element.ReleasePointer(e.pointerId);
            });
        }

        // The pointer's place in the element, 0 to 1 from the left and from the top.
        static Vector2 Fraction(VisualElement element, Vector2 position)
        {
            var local = element.WorldToLocal(position);
            var size = element.layout.size;
            return new Vector2(size.x > 0f ? Mathf.Clamp01(local.x / size.x) : 0f, size.y > 0f ? Mathf.Clamp01(local.y / size.y) : 0f);
        }

        void PickInSquare(Vector2 at)
        {
            saturation = at.x;
            brightness = 1f - at.y;
            Pick();
        }

        void PickInBar(Vector2 at)
        {
            hue = at.y;
            Pick();
        }

        void Pick()
        {
            Refresh();
            Changed?.Invoke(Color);
        }

        void OnSquareNavigate(NavigationMoveEvent e)
        {
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Left: saturation = Mathf.Max(0f, saturation - Step); break;
                case NavigationMoveEvent.Direction.Right: saturation = Mathf.Min(1f, saturation + Step); break;
                case NavigationMoveEvent.Direction.Up when brightness < 1f: brightness = Mathf.Min(1f, brightness + Step); break;
                case NavigationMoveEvent.Direction.Down when brightness > 0f: brightness = Mathf.Max(0f, brightness - Step); break;
                default: return;
            }
            Consume(e);
        }

        void OnBarNavigate(NavigationMoveEvent e)
        {
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Up when hue > 0f: hue = Mathf.Max(0f, hue - Step); break;
                case NavigationMoveEvent.Direction.Down when hue < 1f: hue = Mathf.Min(1f, hue + Step); break;
                default: return;
            }
            Consume(e);
        }

        // An arrow that moved a handle changes the colour instead of the focus.
        void Consume(NavigationMoveEvent e)
        {
            e.StopPropagation();
            if (e.target is VisualElement from) from.focusController?.IgnoreEvent(e);
            Pick();
        }

        void Refresh()
        {
            if (!Mathf.Approximately(paintedHue, hue)) PaintSquare();
            if (squareHandle != null)
            {
                squareHandle.style.left = Length.Percent(saturation * 100f);
                squareHandle.style.top = Length.Percent((1f - brightness) * 100f);
                squareHandle.style.backgroundColor = Color;
            }
            if (barHandle != null) barHandle.style.top = Length.Percent(hue * 100f);
        }

        // Rows from the bottom (dark) up to full brightness, saturation from the left.
        void PaintSquare()
        {
            paintedHue = hue;
            for (var y = 0; y < SquarePixels; y++)
            for (var x = 0; x < SquarePixels; x++)
                squareColors[y * SquarePixels + x] = Color.HSVToRGB(hue, x / (SquarePixels - 1f), y / (SquarePixels - 1f));
            squareTexture.SetPixels32(squareColors);
            squareTexture.Apply(false, false);
        }
    }
}
