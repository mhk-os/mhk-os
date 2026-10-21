using System;
using System.Collections.Generic;
using MHK.Cameras;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Attract
{
    /// <summary>
    /// Camera director of the menu's background race: cuts or blends between the <see cref="AttractSettings"/> shots,
    /// each on the next driver in turn. Chase shots use the race's <see cref="ChaseCamera"/>, the others an <see cref="AttractRig"/>.
    /// </summary>
    [DefaultExecutionOrder(500)] // after ChaseCamera.LateUpdate, before the sky dome and weather follow the camera
    [RequireComponent(typeof(Camera))]
    public class AttractDirector : MonoBehaviour
    {
        [SerializeField, Tooltip("Shots and their placement values.")]
        AttractSettings settings;
        [SerializeField, Tooltip("The background race whose karts are filmed.")]
        RaceManager race;
        [SerializeField, Tooltip("Race camera on this object, used by the Chase shots.")]
        ChaseCamera chaseCamera;

        readonly List<Kart> order = new();
        Camera cam;
        AttractRig rig;
        System.Random random;
        AttractShot shot, held;
        Kart target;
        int nextTarget;
        bool opening;
        float shotEnd, blendStart, blendLength, fromFov;
        Vector3 fromPosition;
        Quaternion fromRotation;

        /// <summary>The camera shows a shot (the race's first picture is ready).</summary>
        public bool HasShot => shot != null;

        /// <summary>The camera the shots are filmed with.</summary>
        public Camera Camera => cam;

        void Awake()
        {
            cam = GetComponent<Camera>();
            random = new System.Random(Environment.TickCount);
            rig = new AttractRig(settings, random);
        }

        /// <summary>From now on films only <paramref name="fixedShot"/> (the still picture's shot), starting at once.</summary>
        public void Hold(AttractShot fixedShot)
        {
            held = fixedShot;
            shot = null;
        }

        void LateUpdate()
        {
            if (settings == null || race == null || race.TrackRuntime == null || race.Karts.Count == 0) return;
            if (NeedsCut()) Cut();
            Vector3 position;
            Quaternion rotation;
            float fov;
            if (shot.kind == AttractShotKind.Chase)
            {
                position = transform.position;
                rotation = transform.rotation;
                fov = cam.fieldOfView;
            }
            else
            {
                rig.Tick(Time.deltaTime, out position, out rotation);
                fov = Camera.HorizontalToVerticalFieldOfView(shot.fov > 0f ? shot.fov : settings.defaultFov, cam.aspect);
            }
            Blend(ref position, ref rotation, ref fov);
            transform.SetPositionAndRotation(position, rotation);
            cam.fieldOfView = fov;
        }

        bool NeedsCut()
        {
            if (shot == null || target == null || !target.gameObject.activeInHierarchy) return true;
            if (held != null) return false;
            if (shot.kind != AttractShotKind.Chase && rig.LostSight) return true;
            return opening ? race.Phase == RacePhase.Racing : Time.time >= shotEnd;
        }

        void Cut()
        {
            if (order.Count == 0) Shuffle();
            var previous = shot;
            opening = held == null && race.Phase != RacePhase.Racing;
            shot = held ?? (opening ? settings.openingShot : Pick(previous));
            target = order[nextTarget++ % order.Count];
            var range = shot.seconds;
            shotEnd = Time.time + Mathf.Lerp(range.x, range.y, (float)random.NextDouble());
            blendLength = previous != null ? shot.blendSeconds : 0f;
            blendStart = Time.time;
            fromPosition = transform.position;
            fromRotation = transform.rotation;
            fromFov = cam.fieldOfView;
            Aim();
        }

        void Aim()
        {
            if (shot.kind != AttractShotKind.Chase)
            {
                chaseCamera.Target = null;
                chaseCamera.enabled = false;
                rig.Begin(shot, target, race.TrackRuntime);
                return;
            }
            chaseCamera.enabled = true;
            chaseCamera.Target = target;
            chaseCamera.CurrentMode = ChaseCamera.Mode.Chase;
            chaseCamera.SetView(shot.view);
            chaseCamera.SnapToTarget();
        }

        // Weighted pick that avoids repeating the previous kind when another kind can be picked.
        AttractShot Pick(AttractShot previous)
        {
            var shots = settings.shots;
            var total = 0f;
            var avoid = previous != null && HasOtherKind(previous.kind);
            foreach (var s in shots)
                if (s.weight > 0f && !(avoid && s.kind == previous.kind)) total += s.weight;
            if (total <= 0f) return settings.openingShot;
            var roll = (float)random.NextDouble() * total;
            foreach (var s in shots)
            {
                if (s.weight <= 0f || (avoid && s.kind == previous.kind)) continue;
                roll -= s.weight;
                if (roll <= 0f) return s;
            }
            return shots[shots.Count - 1];
        }

        bool HasOtherKind(AttractShotKind kind)
        {
            foreach (var s in settings.shots)
                if (s.weight > 0f && s.kind != kind) return true;
            return false;
        }

        void Shuffle()
        {
            order.AddRange(race.Karts);
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
        }

        void Blend(ref Vector3 position, ref Quaternion rotation, ref float fov)
        {
            if (blendLength <= 0f) return;
            var t = (Time.time - blendStart) / blendLength;
            if (t >= 1f)
            {
                blendLength = 0f;
                return;
            }
            t = Mathf.SmoothStep(0f, 1f, t);
            position = Vector3.Lerp(fromPosition, position, t);
            rotation = Quaternion.Slerp(fromRotation, rotation, t);
            fov = Mathf.Lerp(fromFov, fov, t);
        }
    }
}
