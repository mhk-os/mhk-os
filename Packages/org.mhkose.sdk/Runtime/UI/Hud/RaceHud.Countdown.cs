using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>3 / 2 / 1 pop in from large and fade, GO! springs up with the sunburst turning behind it (unscaled time).</summary>
        sealed class Countdown
        {
            const float CountSeconds = 0.95f, GoSeconds = 1.35f;
            const float BurstAlpha = 0.85f, BurstDegreesPerSecond = 45f;

            readonly RaceHud hud;
            VisualElement burst;
            Label number;
            int shown = -1;
            float age;

            public Countdown(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                burst = hud.Q<VisualElement>("countdown-burst");
                number = hud.Q<Label>("countdown-number");
                burst.style.opacity = 0f;
                number.style.opacity = 0f;
                number.text = "";
                shown = -1;
            }

            /// <summary>Starts the animation of <paramref name="n"/> (3, 2, 1, or 0 for GO).</summary>
            public void Show(int n)
            {
                shown = n;
                age = 0f;
                if (n > 0) number.SetText(n);
                else number.text = hud.goText;
                number.EnableInClassList("n3", n == 3);
                number.EnableInClassList("n2", n == 2);
                number.EnableInClassList("n1", n == 1);
                number.EnableInClassList("go", n <= 0);
                if (n > 0) burst.style.opacity = 0f;
                Animate();
            }

            public void Tick(float dt)
            {
                if (shown < 0) return;
                age += dt;
                Animate();
            }

            void Animate()
            {
                var go = shown == 0;
                var t = age / (go ? GoSeconds : CountSeconds);
                if (t >= 1f)
                {
                    shown = -1;
                    number.style.opacity = 0f;
                    burst.style.opacity = 0f;
                    return;
                }
                float scale, alpha;
                if (go)
                {
                    GoCurve(t, out scale, out alpha);
                    AnimateBurst(t, alpha);
                }
                else NumberCurve(t, out scale, out alpha);
                number.style.scale = new Scale(new Vector3(scale, scale, 1f));
                number.style.opacity = alpha;
            }

            static void NumberCurve(float t, out float scale, out float alpha)
            {
                if (t < 0.28f)
                {
                    var k = t / 0.28f;
                    scale = Mathf.LerpUnclamped(2.6f, 1f, Easing.OutBack(k));
                    alpha = Mathf.Clamp01(k * 2.5f);
                }
                else if (t < 0.7f)
                {
                    scale = 1f;
                    alpha = 1f;
                }
                else
                {
                    var k = (t - 0.7f) / 0.3f;
                    scale = 1f - 0.3f * k;
                    alpha = 1f - k;
                }
            }

            static void GoCurve(float t, out float scale, out float alpha)
            {
                if (t < 0.18f)
                {
                    var k = t / 0.18f;
                    scale = Mathf.LerpUnclamped(0.3f, 1f, Easing.OutBack(k));
                    alpha = k;
                }
                else if (t < 0.6f)
                {
                    scale = 1f + 0.08f * (t - 0.18f) / 0.42f;
                    alpha = 1f;
                }
                else
                {
                    var k = (t - 0.6f) / 0.4f;
                    scale = 1.08f + 1.1f * k * k;
                    alpha = 1f - k;
                }
            }

            void AnimateBurst(float t, float alpha)
            {
                var b = 0.6f + 0.5f * Mathf.Clamp01(t / 0.25f);
                burst.style.opacity = alpha * BurstAlpha;
                burst.style.scale = new Scale(new Vector3(b, b, 1f));
                burst.style.rotate = new Rotate(Angle.Degrees(age * BurstDegreesPerSecond));
            }
        }
    }
}
