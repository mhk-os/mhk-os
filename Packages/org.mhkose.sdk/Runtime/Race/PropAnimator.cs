using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Random = UnityEngine.Random;

namespace MHK.Race
{
    /// <summary>
    /// Plays one clip on a track prop without an AnimatorController, looped or ping-ponged. With <see cref="bodies"/>
    /// set the clip is evaluated in FixedUpdate on a shared clock and kinematic rigidbodies follow via
    /// MovePosition/MoveRotation.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PropAnimator : MonoBehaviour
    {
        [Tooltip("Clip played on this prop.")]
        public AnimationClip clip;
        [Tooltip("Cosmetic props pick one of these instead of clip half of the time.")]
        public AnimationClip[] alternates = Array.Empty<AnimationClip>();
        [Tooltip("Playback speed multiplier.")]
        public float speed = 1f;
        [Tooltip("Play the clip to its end, then backwards to its start, and so on (MHK2 'animation mode' 4), instead of " +
                 "looping it. For clips whose last frame differs from the first.")]
        public bool pingPong;
        [Tooltip("Random start time so identical props don't move in sync (ignored for physics props).")]
        public bool randomizeStart = true;
        [Tooltip("Kinematic rigidbodies on animated nodes: evaluated in FixedUpdate, moved with MovePosition/MoveRotation.")]
        public Rigidbody[] bodies = Array.Empty<Rigidbody>();

        PlayableGraph graph;
        AnimationClipPlayable playable;
        AnimationClip current;
        float startTime;
        float clock;

        bool PhysicsDriven => bodies != null && bodies.Length > 0;

        float ClipTime(float t)
        {
            var length = current.length;
            if (length <= 0f) return 0f;
            return pingPong ? Mathf.PingPong(t, length) : Mathf.Repeat(t, length);
        }

        void OnEnable()
        {
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
            if (!PhysicsDriven && alternates.Length > 0 && Random.value < 0.5f) return alternates[Random.Range(0, alternates.Length)];
            return clip;
        }

        // All physics props of a track share one clock (Time.fixedTime at scene start): crushers and their moving walls
        // are separate props with the same motion and must stay together.
        void StartPhysicsDriven(Animator animator)
        {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            startTime = Time.fixedTime;
            Evaluate(0f);
            foreach (var b in bodies)
            {
                if (b == null) continue;
                b.position = b.transform.position;
                b.rotation = b.transform.rotation;
            }
        }

        // The graph only applies the pose, the clip time comes from our own clock (looped or ping-ponged).
        void StartCosmetic()
        {
            playable.SetSpeed(0f);
            clock = randomizeStart ? Random.value * current.length * (pingPong ? 2f : 1f) : 0f;
            playable.SetTime(ClipTime(clock));
            graph.Play();
        }

        void Evaluate(float t)
        {
            playable.SetTime(ClipTime(t));
            graph.Evaluate();
        }

        void FixedUpdate()
        {
            if (!PhysicsDriven || !graph.IsValid() || current == null) return;
            Evaluate((Time.fixedTime + Time.fixedDeltaTime - startTime) * speed);
            FollowBodies();
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
            clock += Time.deltaTime * speed;
            playable.SetTime(ClipTime(clock));
        }

        void OnDisable()
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
