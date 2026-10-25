using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MHK.Race
{
    /// <summary>
    /// Plays one clip on a track prop without an AnimatorController, looped or ping-ponged, on the <see cref="RaceClock"/>:
    /// clip time 0 at GO, so a prop is in the same pose at the same race time in every run and in ghost replays. With
    /// <see cref="bodies"/> set the clip is evaluated in FixedUpdate at the race time of the step and kinematic
    /// rigidbodies follow via MovePosition/MoveRotation; cosmetic props follow the drawn race time (their visible meshes
    /// stay with invisible physics props of the same motion).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PropAnimator : MonoBehaviour
    {
        [Tooltip("Clip played on this prop.")]
        public AnimationClip clip;
        [Tooltip("Cosmetic props pick one of these instead of clip half of the time (chosen by a per-object seed).")]
        public AnimationClip[] alternates = Array.Empty<AnimationClip>();
        [Tooltip("Playback speed multiplier.")]
        public float speed = 1f;
        [Tooltip("Play the clip to its end, then backwards to its start, and so on (MHK2 'animation mode' 4), instead of " +
                 "looping it. For clips whose last frame differs from the first.")]
        public bool pingPong;
        [Tooltip("Start offset from a per-object seed so identical props don't move in sync (ignored for physics props).")]
        public bool randomizeStart = true;
        [Tooltip("Kinematic rigidbodies on animated nodes: evaluated in FixedUpdate, moved with MovePosition/MoveRotation.")]
        public Rigidbody[] bodies = Array.Empty<Rigidbody>();

        // Value01 indices of the per-object seed.
        const int AlternateRoll = 0, AlternatePick = 1, StartRoll = 2;

        PlayableGraph graph;
        AnimationClipPlayable playable;
        AnimationClip current;
        uint seed;
        double offset;
        int lastTick;

        bool PhysicsDriven => bodies != null && bodies.Length > 0;

        float ClipTime(double raceSeconds) => RaceClock.Cycle(raceSeconds * speed + offset, current.length, pingPong);

        void OnEnable()
        {
            seed = RaceClock.Seed(this);
            current = PickClip();
            if (current == null) return;
            var animator = GetComponent<Animator>();
            animator.runtimeAnimatorController = null;
            graph = PlayableGraph.Create(name + ".Prop");
            playable = AnimationClipPlayable.Create(graph, current);
            AnimationPlayableOutput.Create(graph, "Prop", animator).SetSourcePlayable(playable);
            if (PhysicsDriven) StartPhysicsDriven(animator);
            else StartCosmetic();
        }

        AnimationClip PickClip()
        {
            if (PhysicsDriven || alternates.Length == 0 || RaceClock.Value01(seed, AlternateRoll) >= 0.5f) return clip;
            return alternates[Mathf.Min((int)(RaceClock.Value01(seed, AlternatePick) * alternates.Length), alternates.Length - 1)];
        }

        // Crushers and their moving walls are separate props with the same motion: the shared race clock keeps them together.
        void StartPhysicsDriven(Animator animator)
        {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            offset = 0.0;
            lastTick = int.MinValue;
            Evaluate(RaceClock.Seconds);
            PlaceBodies();
        }

        // The graph only applies the pose, the clip time comes from the race clock (looped or ping-ponged).
        void StartCosmetic()
        {
            playable.SetSpeed(0f);
            offset = randomizeStart ? RaceClock.Value01(seed, StartRoll) * current.length * (pingPong ? 2.0 : 1.0) : 0.0;
            playable.SetTime(ClipTime(RaceClock.RenderSeconds));
            graph.Play();
        }

        void Evaluate(double raceSeconds)
        {
            playable.SetTime(ClipTime(raceSeconds));
            graph.Evaluate();
        }

        void FixedUpdate()
        {
            if (!PhysicsDriven || !graph.IsValid() || current == null) return;
            var running = RaceClock.Running;
            var ticks = RaceClock.Ticks;
            // First step after GO or a replay seek: the step starts from the previous tick's pose, so the bodies' motion
            // never depends on where the frame-timed lead-in or the old replay time left them.
            if (running && ticks != lastTick + 1)
            {
                Evaluate(RaceClock.SecondsAt(ticks - 1));
                PlaceBodies();
            }
            Evaluate(RaceClock.Seconds);
            FollowBodies();
            lastTick = running ? ticks : int.MinValue;
        }

        // Teleports the bodies to the pose the graph wrote into the transforms.
        void PlaceBodies()
        {
            foreach (var b in bodies)
            {
                if (b == null) continue;
                b.position = b.transform.position;
                b.rotation = b.transform.rotation;
            }
        }

        // The graph wrote the pose at the end of this step into the transforms: they go back to the physics poses and
        // the bodies travel there during the step.
        void FollowBodies()
        {
            foreach (var b in bodies)
            {
                if (b == null) continue;
                var tr = b.transform;
                tr.GetPositionAndRotation(out var targetPos, out var targetRot);
                tr.SetPositionAndRotation(b.position, b.rotation);
                b.MovePosition(targetPos);
                b.MoveRotation(targetRot);
            }
        }

        void Update()
        {
            if (PhysicsDriven || !graph.IsValid() || current == null) return;
            playable.SetTime(ClipTime(RaceClock.RenderSeconds));
        }

        void OnDisable()
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
