using System.Collections.Generic;
using UnityEngine;

namespace MHK.Race
{
    public partial class TrackRuntime
    {
        const float FallbackGridStart = -4f;
        const float FallbackGridSpacing = 2.5f;
        const float FallbackGridColumnOffset = 1.5f;
        const float FallbackGridLift = 0.5f;
        const float SameColumnTolerance = 0.8f;
        const float SameRowTolerance = 0.5f;
        const float MinSlotSpacing = 1.5f;
        const float DefaultSlotSpacing = 3f;

        /// <summary>
        /// Start slot <paramref name="index"/> as authored. More racers than slots continue the grid behind its rearmost
        /// slot, no slots: a grid generated behind the finish line. Use <see cref="FindGridGround"/> to put a kart on
        /// the road there.
        /// </summary>
        public Pose GetGridPose(int index)
        {
            if (startGrid.Count > 0)
            {
                if (index >= startGrid.Count) return ExtraGridPose(index - startGrid.Count);
                var slot = startGrid[Mathf.Max(0, index)];
                return new Pose(slot.position, slot.rotation);
            }
            if (path != null && path.Count > 1) return FallbackGridPose(index);
            return new Pose(transform.position, transform.rotation);
        }

        Pose FallbackGridPose(int index)
        {
            var d = FallbackGridStart - index * FallbackGridSpacing;
            var p = path.GetPoint(d);
            var fwd = path.GetTangent(d);
            var side = Vector3.Cross(Vector3.up, fwd).normalized;
            p += side * ((index % 2 == 0) ? -FallbackGridColumnOffset : FallbackGridColumnOffset);
            return new Pose(p + Vector3.up * FallbackGridLift, Quaternion.LookRotation(fwd, Vector3.up));
        }

        // Rows continue backwards from the rearmost slot at the grid's own spacing, alternating between its two columns.
        Pose ExtraGridPose(int extra)
        {
            var pole = startGrid[0];
            var fwd = Vector3.ProjectOnPlane(pole.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var side = Vector3.Cross(Vector3.up, fwd);
            var step = MeasureGrid(pole, fwd, side, out var rear, out var other);
            var column = other != null && extra % 2 == 0 ? other : rear;
            var lateral = Vector3.Dot(column.position - pole.position, side);
            var back = Vector3.Dot(rear.position - pole.position, fwd) - step * (extra + 1);
            var p = pole.position + fwd * back + side * lateral;
            p.y = rear.position.y;
            return new Pose(p, Quaternion.LookRotation(fwd, Vector3.up));
        }

        float MeasureGrid(Transform pole, Vector3 fwd, Vector3 side, out Transform rear, out Transform other)
        {
            var along = new List<float>(startGrid.Count);
            rear = pole;
            other = null;
            foreach (var s in startGrid)
            {
                var a = Vector3.Dot(s.position - pole.position, fwd);
                along.Add(a);
                if (a < Vector3.Dot(rear.position - pole.position, fwd)) rear = s;
            }
            var rearLat = Vector3.Dot(rear.position - pole.position, side);
            foreach (var s in startGrid)
            {
                if (Mathf.Abs(Vector3.Dot(s.position - pole.position, side) - rearLat) < SameColumnTolerance) continue;
                if (other == null || Vector3.Dot(s.position - other.position, fwd) < 0f) other = s;
            }
            return MedianSlotSpacing(along);
        }

        static float MedianSlotSpacing(List<float> along)
        {
            along.Sort();
            var gaps = new List<float>();
            for (var i = 1; i < along.Count; i++)
                if (along[i] - along[i - 1] > SameRowTolerance) gaps.Add(along[i] - along[i - 1]);
            gaps.Sort();
            return gaps.Count > 0 ? Mathf.Max(MinSlotSpacing, gaps[gaps.Count / 2]) : DefaultSlotSpacing;
        }
    }
}
