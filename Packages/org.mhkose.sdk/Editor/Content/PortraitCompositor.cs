using System.Collections.Generic;
using UnityEngine;

namespace MHK.Editor.Content
{
    /// <summary>Pixel composition of rendered driver busts into MHK2-style character portraits.</summary>
    internal static class PortraitCompositor
    {
        const float White = 220f / 255f;
        const float MaxGain = 4f;
        const float WhitePercentile = 0.98f;
        const float AlphaThreshold = 8f / 255f;
        const float FadeSide = 0.06f, FadeBottom = 0.12f;
        const float CanvasFill = 0.98f, HeadroomAspect = 0.92f, BottomBleed = 0.02f;
        const float MinAlpha = 0.0001f;

        /// <summary>
        /// Tone-levels <paramref name="src"/> by a uniform gain, crops it to its visible pixels, scales it to fill the
        /// width bottom-anchored with faded cut edges and box-filters it down to <paramref name="size"/>.
        /// </summary>
        public static Texture2D Compose(Texture2D src, int size)
        {
            int w = src.width, h = src.height;
            var px = src.GetPixels();
            var outTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            if (!OpaqueBounds(px, w, h, out var box, out var lum))
            {
                outTex.SetPixelData(new Color32[size * size], 0);
                outTex.Apply();
                return outTex;
            }
            var pm = PremultiplyAndFade(px, w, h, LevelGain(lum));
            var composed = ComposeOnCanvas(pm, w, h, box);
            outTex.SetPixels(BoxDownsample(composed, w, size));
            outTex.Apply();
            return outTex;
        }

        static bool OpaqueBounds(Color[] px, int w, int h, out RectInt box, out List<float> lum)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            lum = new List<float>();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = px[y * w + x];
                if (c.a <= AlphaThreshold) continue;
                x0 = Mathf.Min(x0, x);
                x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y);
                y1 = Mathf.Max(y1, y);
                lum.Add((c.r + c.g + c.b) / 3f);
            }
            box = new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            return x1 >= 0;
        }

        static float LevelGain(List<float> lum)
        {
            lum.Sort();
            return Mathf.Min(MaxGain, White / Mathf.Max(1f / 255f, lum[(int)((lum.Count - 1) * WhitePercentile)]));
        }

        static Color[] PremultiplyAndFade(Color[] px, int w, int h, float gain)
        {
            // Texture rows run bottom-up: y = 0 is the bottom edge.
            float fadeSide = w * FadeSide, fadeBottom = h * FadeBottom;
            var pm = new Color[px.Length];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = px[y * w + x];
                var a = c.a * Fade(x / fadeSide) * Fade((w - 1 - x) / fadeSide) * Fade(y / fadeBottom);
                pm[y * w + x] = new Color(Mathf.Min(1f, c.r * gain) * a, Mathf.Min(1f, c.g * gain) * a, Mathf.Min(1f, c.b * gain) * a, a);
            }
            return pm;
        }

        static Color[] ComposeOnCanvas(Color[] pm, int w, int h, RectInt box)
        {
            var canvas = w;
            int x0 = box.x, y0 = box.y, bw = box.width, bh = box.height;
            var scale = canvas * CanvasFill / Mathf.Max(bw, bh * HeadroomAspect);
            var ox = (canvas - bw * scale) * 0.5f;
            var oy = -BottomBleed * canvas;
            var composed = new Color[canvas * canvas];
            for (var y = 0; y < canvas; y++)
            for (var x = 0; x < canvas; x++)
                composed[y * canvas + x] = Bilinear(pm, w, h, x0 + (x + 0.5f - ox) / scale - 0.5f, y0 + (y + 0.5f - oy) / scale - 0.5f);
            return composed;
        }

        static Color[] BoxDownsample(Color[] composed, int canvas, int size)
        {
            var k = canvas / size;
            var result = new Color[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var sum = new Color(0f, 0f, 0f, 0f);
                for (var dy = 0; dy < k; dy++)
                for (var dx = 0; dx < k; dx++)
                    sum += composed[(y * k + dy) * canvas + x * k + dx];
                sum /= k * k;
                result[y * size + x] = sum.a > MinAlpha ? new Color(sum.r / sum.a, sum.g / sum.a, sum.b / sum.a, sum.a) : new Color(0f, 0f, 0f, 0f);
            }
            return result;
        }

        static float Fade(float t) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

        static Color Bilinear(Color[] img, int w, int h, float x, float y)
        {
            var ix = Mathf.FloorToInt(x);
            var iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            return Lerp(Lerp(At(img, w, h, ix, iy), At(img, w, h, ix + 1, iy), fx),
                Lerp(At(img, w, h, ix, iy + 1), At(img, w, h, ix + 1, iy + 1), fx), fy);
        }

        static Color At(Color[] img, int w, int h, int x, int y) =>
            x < 0 || y < 0 || x >= w || y >= h ? new Color(0f, 0f, 0f, 0f) : img[y * w + x];

        static Color Lerp(Color a, Color b, float t) => a + (b - a) * t;
    }
}
