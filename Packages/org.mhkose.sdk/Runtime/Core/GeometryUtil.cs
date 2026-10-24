using UnityEngine;

namespace MHK.Core
{
    /// <summary>Small vector helpers shared by gameplay code. Callers pass their own epsilons so results stay bit-identical.</summary>
    public static class GeometryUtil
    {
        /// <summary>
        /// Clamped parameter t of the point on segment a..a+ab closest to <paramref name="p"/>, or
        /// <paramref name="degenerate"/> when <c>ab.sqrMagnitude</c> is not above <paramref name="epsilon"/>.
        /// </summary>
        public static float SegmentT(Vector3 p, Vector3 a, Vector3 ab, float lengthSq, float epsilon, float degenerate = 0f) =>
            lengthSq > epsilon ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSq) : degenerate;

        public static float SegmentT(Vector3 p, Vector3 a, Vector3 ab, float epsilon, float degenerate = 0f) =>
            SegmentT(p, a, ab, ab.sqrMagnitude, epsilon, degenerate);

        public static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b, float epsilon, float degenerate = 0f)
        {
            var ab = b - a;
            return a + ab * SegmentT(p, a, ab, epsilon, degenerate);
        }

        public static float DistanceSqToSegment(Vector3 p, Vector3 a, Vector3 b, float epsilon, float degenerate = 0f) =>
            (p - ClosestOnSegment(p, a, b, epsilon, degenerate)).sqrMagnitude;

        /// <summary><paramref name="v"/> with y = 0, normalized, or <paramref name="fallback"/> when its squared length is not above <paramref name="minSqr"/>.</summary>
        public static Vector3 FlatNormalized(Vector3 v, Vector3 fallback, float minSqr = 1e-6f)
        {
            v.y = 0f;
            return v.sqrMagnitude > minSqr ? v.normalized : fallback;
        }
    }
}
