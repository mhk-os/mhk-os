using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Attract
{
    /// <summary>Free camera placement of the director's own shots (orbit, overhead, trackside, flyby) around one kart.</summary>
    sealed class AttractRig
    {
        const float LookHeight = 0.6f;
        const float MinKartSpeed = 8f;
        const float ProbeAbove = 10f;
        const float ProbeDepth = 60f;
        const float RoadEyeHeight = 1f;
        const float TangentSmoothing = 6f;
        const float HeadingRateShare = 0.25f;
        const float FlybyAimShare = 4f;

        readonly AttractSettings settings;
        readonly System.Random random;
        AttractShot shot;
        Kart kart;
        TrackPath path;
        int mask;
        float angle, lineOffset, hiddenSeconds;
        Vector3 anchor, focus, back, eye;
        Quaternion look;

        public AttractRig(AttractSettings settings, System.Random random)
        {
            this.settings = settings;
            this.random = random;
        }

        /// <summary>Starts <paramref name="newShot"/> on <paramref name="target"/> on the track <paramref name="track"/>.</summary>
        public void Begin(AttractShot newShot, Kart target, TrackRuntime track)
        {
            shot = newShot;
            kart = target;
            path = track.path;
            mask = track.ObstacleMask;
            var kt = kart.transform;
            focus = kt.position + Vector3.up * LookHeight;
            back = -GeometryUtil.FlatNormalized(kt.forward, Vector3.forward);
            angle = (float)random.NextDouble() * 360f;
            hiddenSeconds = 0f;
            var d = kart.Progress.PathDistance;
            var ahead = Mathf.Max(MinKartSpeed, kart.Physics.Body.linearVelocity.magnitude) * shot.lead;
            if (shot.kind == AttractShotKind.Trackside) anchor = TracksideCamera(d, ahead);
            lineOffset = -ahead;
            eye = FlybyPoint(d + lineOffset);
            var toKart = focus - eye;
            look = toKart.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toKart) : Quaternion.LookRotation(path.GetTangent(d, TangentSmoothing));
        }

        /// <summary>The filmed kart stayed hidden behind the track longer than the settings allow.</summary>
        public bool LostSight => hiddenSeconds > settings.lostSightSeconds;

        /// <summary>Camera pose of the running shot after <paramref name="dt"/> seconds.</summary>
        public void Tick(float dt, out Vector3 position, out Quaternion rotation)
        {
            var target = kart.transform.position + Vector3.up * LookHeight;
            focus = Vector3.Lerp(focus, target, Smoothing.Factor(settings.followRate, dt));
            Place(dt, target, out position, out rotation);
            hiddenSeconds = Physics.Linecast(position, target, mask, QueryTriggerInteraction.Ignore) ? hiddenSeconds + dt : 0f;
        }

        void Place(float dt, Vector3 target, out Vector3 position, out Quaternion rotation)
        {
            switch (shot.kind)
            {
                case AttractShotKind.Orbit:
                    angle += shot.speed * dt;
                    position = Clear(target, target + Quaternion.Euler(0f, angle, 0f) * Vector3.back * shot.distance + Vector3.up * shot.height);
                    rotation = Quaternion.LookRotation(target - position);
                    return;
                case AttractShotKind.Overhead:
                    var heading = -GeometryUtil.FlatNormalized(kart.transform.forward, -back);
                    back = Vector3.Slerp(back, heading, Smoothing.Factor(settings.followRate * HeadingRateShare, dt));
                    position = Clear(focus, focus + Vector3.up * shot.height + back * shot.distance);
                    rotation = Quaternion.LookRotation(focus - position);
                    return;
                case AttractShotKind.Trackside:
                    position = anchor;
                    rotation = Quaternion.LookRotation(focus - position);
                    return;
                default:
                    TickFlyby(dt, out position, out rotation);
                    return;
            }
        }

        // Rides the line beside the kart at a growing offset (from behind it to ahead of it), always looking at the
        // kart. The sideways offset keeps the view from flipping over it, the smoothing rounds the line's corners.
        void TickFlyby(float dt, out Vector3 position, out Quaternion rotation)
        {
            lineOffset += shot.speed * dt;
            var follow = Smoothing.Factor(settings.followRate, dt);
            eye = Vector3.Lerp(eye, FlybyPoint(kart.Progress.PathDistance + lineOffset), follow);
            var toKart = kart.transform.position + Vector3.up * LookHeight - eye;
            if (toKart.sqrMagnitude > 1e-4f) look = Quaternion.Slerp(look, Quaternion.LookRotation(toKart), Smoothing.Factor(settings.followRate * FlybyAimShare, dt));
            position = eye;
            rotation = look;
        }

        Vector3 FlybyPoint(float d)
        {
            var road = path.GetPoint(d) + Vector3.up * RoadEyeHeight;
            var side = path.GetRight(d, TangentSmoothing) * shot.distance;
            return Clear(road, road + side + Vector3.up * (shot.height - RoadEyeHeight));
        }

        // The side of the road with a clear view of the stretch the kart comes along, the first side when neither has.
        Vector3 TracksideCamera(float kartDistance, float ahead)
        {
            var first = random.Next(2) == 0 ? -1f : 1f;
            var camera = TracksidePoint(kartDistance + ahead, first);
            if (SeesRoad(camera, kartDistance, ahead)) return camera;
            var other = TracksidePoint(kartDistance + ahead, -first);
            return SeesRoad(other, kartDistance, ahead) ? other : camera;
        }

        bool SeesRoad(Vector3 camera, float kartDistance, float ahead)
        {
            var eyeUp = Vector3.up * RoadEyeHeight;
            return !Physics.Linecast(camera, path.GetPoint(kartDistance) + eyeUp, mask, QueryTriggerInteraction.Ignore)
                   && !Physics.Linecast(camera, path.GetPoint(kartDistance + ahead * 0.5f) + eyeUp, mask, QueryTriggerInteraction.Ignore);
        }

        Vector3 TracksidePoint(float d, float side)
        {
            var road = path.GetPoint(d);
            var spot = road + path.GetRight(d, TangentSmoothing) * (side * (path.GetWidth(d) * 0.5f + shot.distance));
            if (GroundProbe.Down(spot, ProbeAbove, ProbeDepth, mask, out var hit)) spot.y = hit.point.y;
            var eyeAtRoad = road + Vector3.up * RoadEyeHeight;
            return Clear(eyeAtRoad, spot + Vector3.up * shot.height);
        }

        // Pulls a camera point in front of the first wall or ground between it and the point it shows.
        Vector3 Clear(Vector3 from, Vector3 to)
        {
            var span = to - from;
            var length = span.magnitude;
            if (length < 1e-3f || !Physics.Raycast(from, span / length, out var hit, length, mask, QueryTriggerInteraction.Ignore))
                return to;
            return hit.point + hit.normal * settings.wallSkin;
        }
    }
}
