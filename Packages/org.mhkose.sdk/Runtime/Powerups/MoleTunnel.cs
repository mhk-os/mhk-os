using System.Collections.Generic;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// The ground a <see cref="MolehillPowerup"/> digs along: the steered heading, the head pose, the checked ground
    /// ahead (probe) and the checked ground passed (trail).
    /// </summary>
    sealed class MoleTunnel
    {
        /// <summary>Ground check settings, copied from the powerup's serialized fields.</summary>
        public struct Settings
        {
            public int groundMask;
            public float probeStep, maxClimb, stepTolerance, maxSlope, wallCheckHeight;
        }

        const float StartProbeAbove = 1.5f;
        const float StartProbeBelow = 2.5f;
        const float ProbeLift = 0.2f;
        const float MinProbeStep = 0.05f;
        const float CaptureStep = 2f;
        const float CaptureStepSlack = 0.01f;
        const float ChassisShrink = 0.9f;
        const float ChassisLift = 0.15f;

        static readonly Collider[] overlaps = new Collider[16];

        readonly Settings settings;
        readonly List<Pose> trail = new();
        readonly List<Pose> probe = new();
        float probeSpacing;

        public Vector3 Heading { get; private set; }
        public Vector3 Ground { get; private set; }
        public Vector3 Normal { get; private set; }
        public Vector3 Forward { get; private set; }

        public MoleTunnel(Settings settings) => this.settings = settings;

        /// <summary>Starts at the ground under <paramref name="kart"/>. False when there is no ground in reach.</summary>
        public bool TryBegin(Transform kart, out RaycastHit hit)
        {
            var up = kart.up;
            if (!ItemPhysics.TryGroundAlong(kart.position, up, StartProbeAbove, StartProbeBelow, out hit, settings.groundMask)) return false;
            var heading = Vector3.ProjectOnPlane(kart.forward, Vector3.up);
            Heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.forward;
            SetHead(hit.point, hit.normal, FlatForward(hit.normal));
            trail.Clear();
            trail.Add(new Pose(Ground, Quaternion.LookRotation(Forward, Normal)));
            return true;
        }

        /// <summary>Turns the heading around the vertical by <paramref name="degrees"/>.</summary>
        public void Turn(float degrees) => Heading = (Quaternion.AngleAxis(degrees, Vector3.up) * Heading).normalized;

        /// <summary>Moves the head to <paramref name="pose"/>.</summary>
        public void SetHead(Pose pose) => SetHead(pose.position, pose.rotation * Vector3.up, pose.rotation * Vector3.forward);

        void SetHead(Vector3 ground, Vector3 normal, Vector3 forward)
        {
            Ground = ground;
            Normal = normal;
            Forward = forward;
        }

        Vector3 FlatForward(Vector3 up)
        {
            var f = Vector3.ProjectOnPlane(Heading, up);
            return f.sqrMagnitude > 1e-6f ? f.normalized : Heading;
        }

        /// <summary>
        /// Checks the ground from the head along the heading, step by step, up to <paramref name="reach"/> metres, and
        /// returns how far it is solid, drivable and reachable.
        /// </summary>
        public float Probe(float reach)
        {
            probe.Clear();
            probe.Add(new Pose(Ground, Quaternion.LookRotation(FlatForward(Normal), Normal)));
            var count = Mathf.Max(1, Mathf.CeilToInt(reach / Mathf.Max(MinProbeStep, settings.probeStep) - 1e-3f));
            var step = probeSpacing = reach / count;
            var climb = settings.stepTolerance + settings.maxClimb * step;
            var p = Ground;
            var n = Normal;
            for (var i = 1; i <= count; i++)
            {
                var origin = new Vector3(p.x + Heading.x * step, p.y + climb + ProbeLift, p.z + Heading.z * step);
                if (!ItemPhysics.Raycast(origin, Vector3.down, 2f * climb + ProbeLift, out var hit, settings.groundMask)
                    || !Diggable(hit, p, n)) return (i - 1) * step;
                p = hit.point;
                n = Vector3.Slerp(n, hit.normal, 0.5f).normalized;
                probe.Add(new Pose(p, Quaternion.LookRotation(FlatForward(n), n)));
            }
            return reach;
        }

        // Not too steep, above the kill height, a drivable surface and no wall between the last step and this one.
        bool Diggable(RaycastHit hit, Vector3 from, Vector3 fromNormal)
        {
            if (hit.normal.y < Mathf.Cos(settings.maxSlope * Mathf.Deg2Rad) || TrackRuntime.IsBelowKillHeight(hit.point)) return false;
            var surface = hit.collider.GetComponentInParent<TrackSurface>();
            if (surface != null && (surface.kind == SurfaceKind.Deadly || surface.kind == SurfaceKind.Drop
                                    || surface.kind == SurfaceKind.Wall || surface.kind == SurfaceKind.Water)) return false;
            return !ItemPhysics.Linecast(from + fromNormal * settings.wallCheckHeight, hit.point + hit.normal * settings.wallCheckHeight,
                out _, settings.groundMask);
        }

        /// <summary>Pose <paramref name="move"/> metres along the checked probe (interpolated). The probe poses passed join the trail.</summary>
        public Pose Advance(float move)
        {
            var k = move / Mathf.Max(1e-4f, probeSpacing);
            var i = Mathf.Min(Mathf.FloorToInt(k), probe.Count - 1);
            for (var j = 1; j <= i; j++) trail.Add(probe[j]);
            if (i + 1 >= probe.Count) return probe[i];
            var a = probe[i];
            var b = probe[i + 1];
            var t = Mathf.Clamp01(k - i);
            return new Pose(Vector3.Lerp(a.position, b.position, t), Quaternion.Slerp(a.rotation, b.rotation, t));
        }

        /// <summary>
        /// Ground points of the area the dig can reach next: a fan of <paramref name="rays"/> paths from full left to
        /// full right steering (<paramref name="turnRate"/> degrees per second at <paramref name="speed"/>), over
        /// <paramref name="length"/> metres but within <paramref name="reach"/> ahead and across. Each point stays within
        /// climbing range of the previous, so roofs and bridges above are never taken.
        /// </summary>
        public List<Vector3> CaptureArea(float length, float reach, float turnRate, float speed, int rays)
        {
            var area = new List<Vector3> { Ground };
            var right = new Vector3(Heading.z, 0f, -Heading.x);
            var climb = settings.stepTolerance + settings.maxClimb * CaptureStep;
            var turnPerStep = turnRate / Mathf.Max(1f, speed) * CaptureStep;
            for (var r = 0; r < rays; r++)
            {
                var share = rays == 1 ? 0f : Mathf.Lerp(-1f, 1f, r / (float)(rays - 1));
                TraceCapturePath(area, share * turnPerStep, length, reach, right, climb);
            }
            return area;
        }

        void TraceCapturePath(List<Vector3> area, float turn, float length, float reach, Vector3 right, float climb)
        {
            var dir = Heading;
            var p = Ground;
            for (var s = CaptureStep; s <= length + CaptureStepSlack; s += CaptureStep)
            {
                dir = Quaternion.AngleAxis(turn, Vector3.up) * dir;
                var q = p + dir * CaptureStep;
                var rel = q - Ground;
                var along = Vector3.Dot(rel, Heading);
                if (along < 0f || along > reach || Mathf.Abs(Vector3.Dot(rel, right)) > reach * 0.5f) break;
                if (ItemPhysics.Raycast(new Vector3(q.x, p.y + climb + ProbeLift, q.z), Vector3.down, 2f * climb + ProbeLift, out var h, settings.groundMask))
                    q.y = h.point.y;
                else q.y = p.y;
                p = q;
                area.Add(q);
            }
        }

        /// <summary>The furthest pose up to <paramref name="head"/> where the chassis <paramref name="box"/> fits (never inside geometry).</summary>
        public Pose SurfacePose(Pose head, BoxCollider box, float rideHeight)
        {
            if (ChassisFits(head, box, rideHeight)) return head;
            for (var i = trail.Count - 1; i >= 0; i--)
                if (ChassisFits(trail[i], box, rideHeight)) return trail[i];
            return trail[0];
        }

        bool ChassisFits(Pose pose, BoxCollider box, float rideHeight)
        {
            if (box == null) return true;
            var scale = box.transform.lossyScale;
            var half = Vector3.Scale(box.size * 0.5f, scale) * ChassisShrink;
            var up = pose.rotation * Vector3.up;
            var centre = pose.position + up * (rideHeight + ChassisLift) + pose.rotation * Vector3.Scale(box.center, scale);
            var n = Physics.OverlapBoxNonAlloc(centre, half, overlaps, pose.rotation, settings.groundMask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var c = overlaps[i];
                if (c.attachedRigidbody != null || ItemPhysics.IsPassThrough(c)) continue;
                return false;
            }
            return true;
        }
    }
}
