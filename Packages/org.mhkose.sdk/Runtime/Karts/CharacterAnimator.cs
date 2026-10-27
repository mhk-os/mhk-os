using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MHK.Karts
{
    /// <summary>
    /// Plays a character's clips by role through a PlayableGraph: per-frame locomotion plus prioritised action overlays,
    /// with MHK2 playback modes and rest-pose restore for partially animated rigs. A kart-following drive loop (MHK2
    /// bob) keeps running on an additive layer over the steering poses, on one clock with the drive clip itself.
    /// </summary>
    public class CharacterAnimator : MonoBehaviour
    {
        [Tooltip("Cross-fade between roles (MHK2: 0.2 s). Reactions (collisions, landing, shake) cut in instantly.")]
        [SerializeField] private float fadeTime = 0.2f;

        const float EndEpsilon = 1e-4f;
        const CharacterAnimRole None = (CharacterAnimRole)(-1);

        struct Layer
        {
            public AnimationClipPlayable playable;
            public CharacterAnimRole role;
            public CharacterAnimBinding binding;
            public bool action;
            public float time;
            public float played;
            public bool reversing;

            public bool IsValid => playable.IsValid();
            public float Length => binding.clip != null ? binding.clip.length : 0f;
            public CharacterAnimPlayback Mode
            {
                get
                {
                    var m = binding.Playback;
                    return action && m == CharacterAnimPlayback.SteerScrub ? CharacterAnimPlayback.Once : m;
                }
            }
        }

        IReadOnlyList<CharacterAnimBinding> animations;
        PlayableGraph graph;
        AnimationMixerPlayable mixer;
        AnimationLayerMixerPlayable layers;
        Animator target;
        AnimatorCullingMode awakeCulling;
        Layer current, previous, bob;
        float bobWeight;
        float fade = 1f, fadeDuration;
        bool paused;

        CharacterAnimRole locomotion = CharacterAnimRole.Idle;
        float scrub, rate = 1f;
        bool restartLocomotion;

        bool actionActive, actionHeld;
        CharacterAnimRole actionRole = None;

        Transform[] rig;
        Vector3[] restPosition, restScale;
        Quaternion[] restRotation;
        bool restorePending;

        /// <summary>Role of the playing locomotion clip (None while an action plays).</summary>
        public CharacterAnimRole LocomotionRole => current.IsValid && !current.action ? current.role : None;
        /// <summary>True while an action overrides the locomotion.</summary>
        public bool ActionActive => actionActive;
        /// <summary>Role of the playing action (None without one).</summary>
        public CharacterAnimRole ActionRole => actionActive ? actionRole : None;
        /// <summary>Not evaluated (<see cref="SetDormant"/>).</summary>
        public bool Dormant { get; private set; }

        /// <summary>Starts the graph on <paramref name="animator"/> playing clips from <paramref name="clips"/> by role.</summary>
        public void Setup(IReadOnlyList<CharacterAnimBinding> clips, Animator animator)
        {
            animations = clips;
            target = animator;
            CaptureRestPose(animator.transform);
            graph = PlayableGraph.Create($"{name}.CharacterAnimator");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
            mixer = AnimationMixerPlayable.Create(graph, 2);
            if (CreateBobLayer()) output.SetSourcePlayable(layers);
            else output.SetSourcePlayable(mixer);
            graph.Play();
            StartLocomotion();
            ApplyTimes();
            UpdateBob(0f);
        }

        // The drive clip, when its speed follows the kart, also plays additively (offset from its first frame) over the
        // turn poses, so the bob carries on while steering. Other drive clips (MHK3 'Fahren', TINcan) stay as they are.
        bool CreateBobLayer()
        {
            if (!CharacterDefinition.TryResolve(animations, CharacterAnimRole.Drive, out var drive) || !drive.SpeedFollowsKart
                || drive.Playback is not (CharacterAnimPlayback.Loop or CharacterAnimPlayback.PingPong))
                return false;
            layers = AnimationLayerMixerPlayable.Create(graph, 2);
            graph.Connect(mixer, 0, layers, 0);
            layers.SetInputWeight(0, 1f);
            var playable = AnimationClipPlayable.Create(graph, drive.clip);
            playable.SetApplyFootIK(false);
            playable.SetSpeed(0);
            graph.Connect(playable, 0, layers, 1);
            layers.SetLayerAdditive(1, true);
            layers.SetInputWeight(1, 0f);
            bob = new Layer { playable = playable, role = CharacterAnimRole.Drive, binding = drive };
            return true;
        }

        /// <summary>
        /// Stops evaluating the graph (the pose freezes, clips do not advance) and culls the animator, for a model too far
        /// away to watch (many time-trial ghosts). Waking resumes where it stopped; <see cref="Snap"/> and
        /// <see cref="PlayAction"/> bring it up to date.
        /// </summary>
        public void SetDormant(bool dormant)
        {
            if (!graph.IsValid() || dormant == Dormant) return;
            Dormant = dormant;
            if (dormant)
            {
                awakeCulling = target.cullingMode;
                target.cullingMode = AnimatorCullingMode.CullCompletely;
                graph.Stop();
                return;
            }
            target.cullingMode = awakeCulling;
            graph.Play();
        }

        /// <summary>
        /// Cuts straight to the locomotion clip in the rig's rest pose: ends any action and drops the outgoing pose without
        /// a cross-fade. For discontinuities such as a respawn, where blending out of the last pose (fallen into a chasm,
        /// shot down) would show it for the fade time (an MHK3 fall pose lies metres below the kart).
        /// </summary>
        public void Snap()
        {
            if (!graph.IsValid()) return;
            actionActive = actionHeld = false;
            actionRole = None;
            restartLocomotion = false;
            if (Resolve(locomotion, false, out var binding)) StartLayer(locomotion, binding, false, 0f, 0f);
            else DropPrevious();
            restorePending = true;
            ApplyTimes();
            UpdateBob(0f);
        }

        void CaptureRestPose(Transform root)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            rig = new Transform[all.Length - 1];
            System.Array.Copy(all, 1, rig, 0, rig.Length);
            restPosition = new Vector3[rig.Length];
            restRotation = new Quaternion[rig.Length];
            restScale = new Vector3[rig.Length];
            for (var i = 0; i < rig.Length; i++)
            {
                rig[i].GetLocalPositionAndRotation(out restPosition[i], out restRotation[i]);
                restScale[i] = rig[i].localScale;
            }
        }

        void RestoreRestPose()
        {
            restorePending = false;
            if (rig == null) return;
            for (var i = 0; i < rig.Length; i++)
            {
                var t = rig[i];
                if (t == null) continue;
                t.SetLocalPositionAndRotation(restPosition[i], restRotation[i]);
                t.localScale = restScale[i];
            }
        }

        void OnDestroy()
        {
            if (graph.IsValid()) graph.Destroy();
        }

        /// <summary>
        /// The locomotion role wanted this frame. <paramref name="steerScrub"/> (0..1) poses
        /// <see cref="CharacterAnimPlayback.SteerScrub"/> clips, <paramref name="kartRate"/> scales clips whose speed
        /// follows the kart (MHK2 drive), <paramref name="restart"/> replays the role from its first frame (MHK2 idle).
        /// </summary>
        public void SetLocomotion(CharacterAnimRole role, float steerScrub = 0f, float kartRate = 1f, bool restart = false)
        {
            locomotion = role;
            scrub = Mathf.Clamp01(steerScrub);
            rate = Mathf.Max(0f, kartRate);
            restartLocomotion |= restart;
        }

        /// <summary>
        /// Plays an action over the locomotion. Returns false when the character has no clip for it (or only the plain
        /// driving clip), while paused, or when a more important action is playing (see <see cref="Priority"/>).
        /// A held action keeps its last frame (or loops) until <see cref="ReleaseAction"/>.
        /// </summary>
        public bool PlayAction(CharacterAnimRole role, bool hold = false)
        {
            if (paused || !graph.IsValid() || !Resolve(role, true, out var binding)) return false;
            if (actionActive && !ActionFinished(false) && !CanInterrupt(role, actionRole)) return false;
            actionActive = true;
            actionHeld = hold;
            actionRole = role;
            StartLayer(role, binding, true, 0f, Priority(role) == 1 ? 0f : fadeTime);
            return true;
        }

        /// <summary>Lets a held action of <paramref name="role"/> finish (HoldReverse plays back first).</summary>
        public void ReleaseAction(CharacterAnimRole role)
        {
            if (actionActive && actionRole == role) actionHeld = false;
        }

        /// <summary>Real-time length of <paramref name="role"/> as an action (0 when this character has no clip for it).</summary>
        public float ActionDuration(CharacterAnimRole role) => Resolve(role, true, out var b) ? b.Duration : 0f;

        /// <summary>Moves the playing action <paramref name="seconds"/> on at once and ends a running cross-fade (replay seeking).</summary>
        public void SkipAction(float seconds)
        {
            if (!graph.IsValid() || !actionActive || !current.IsValid || !current.action) return;
            fade = 1f;
            DropPrevious();
            UpdateWeights();
            Advance(ref current, seconds);
            ApplyTimes();
            UpdateBob(0f);
        }

        /// <summary>
        /// Interrupt order: fidgets (glances, shouts) 0 &lt; reactions (collisions, landing, shake, dizzy, shocked) 1 &lt;
        /// race start 2 &lt; hit / burning / crushed / fall / respawn 3 &lt; finish 4. Equal priorities from 2 up replace
        /// each other, so a landing or a collision can never cut off a fall or a shot-down animation.
        /// </summary>
        public static int Priority(CharacterAnimRole role) => role switch
        {
            CharacterAnimRole.LookLeft or CharacterAnimRole.LookRight or CharacterAnimRole.ShoutLeft or CharacterAnimRole.ShoutRight
                or CharacterAnimRole.Idle or CharacterAnimRole.Drive => 0,
            CharacterAnimRole.Start or CharacterAnimRole.PowerStart or CharacterAnimRole.PowerFail => 2,
            CharacterAnimRole.ShotDown or CharacterAnimRole.Burning or CharacterAnimRole.Crushed
                or CharacterAnimRole.Drop or CharacterAnimRole.Respawn => 3,
            CharacterAnimRole.Win or CharacterAnimRole.Lose => 4,
            _ => 1,
        };

        internal static bool CanInterrupt(CharacterAnimRole next, CharacterAnimRole active)
        {
            int n = Priority(next), a = Priority(active);
            return n > a || (n == a && n >= 2);
        }

        /// <summary>
        /// Freezes the current pose (MHK2 x-field: the driver stops moving). While paused, clip time and cross-fades
        /// stand still and actions are refused. <c>SetPaused(false)</c> resumes where it stopped.
        /// </summary>
        public void SetPaused(bool value) => paused = value;

        bool Resolve(CharacterAnimRole role, bool action, out CharacterAnimBinding binding)
        {
            if (!CharacterDefinition.TryResolve(animations, role, out binding)) return false;
            return !action || !IsOnlyDriveClip(role, binding);
        }

        bool IsOnlyDriveClip(CharacterAnimRole role, CharacterAnimBinding binding) =>
            role != CharacterAnimRole.Drive
            && CharacterDefinition.TryResolve(animations, CharacterAnimRole.Drive, out var drive) && drive.clip == binding.clip;

        void Update()
        {
            if (!graph.IsValid() || paused || Dormant) return;
            var dt = Time.deltaTime;
            if (fade < 1f)
            {
                fade = fadeDuration > 0f ? Mathf.MoveTowards(fade, 1f, dt / fadeDuration) : 1f;
                UpdateWeights();
            }
            if (fade >= 1f && previous.IsValid) DropPrevious();

            if (current.IsValid) Advance(ref current, dt);
            if (bob.IsValid) Advance(ref bob, dt);
            if (actionActive)
            {
                if (ActionFinished(true))
                {
                    actionActive = false;
                    actionRole = None;
                    StartLocomotion();
                }
            }
            else UpdateLocomotion();
            ApplyTimes();
            UpdateBob(dt);
            // Runs before this frame's animation evaluation, which then poses everything the playing clips animate.
            if (restorePending) RestoreRestPose();
        }

        bool ActionFinished(bool advance)
        {
            if (!current.IsValid || !current.action) return true;
            var len = current.Length;
            switch (current.Mode)
            {
                case CharacterAnimPlayback.HoldReverse:
                    if (!actionHeld && !current.reversing && advance) current.reversing = true;
                    return current.reversing && current.time <= 0f;
                default:
                    return !actionHeld && current.played >= len - EndEpsilon;
            }
        }

        void UpdateLocomotion()
        {
            var target = locomotion;
            if (!current.IsValid || current.action)
            {
                StartLocomotion();
                return;
            }
            if (current.role == target)
            {
                current.reversing = false;
                if (restartLocomotion) current.time = current.played = 0f;
                restartLocomotion = false;
                return;
            }
            restartLocomotion = false;
            if (!Resolve(target, false, out var binding)) return;
            if (SameClip(binding))
            {
                current.role = target;
                return;
            }
            var from = current.role;
            if (current.Mode == CharacterAnimPlayback.HoldReverse && ContinueTurnRound(target, binding)) return;
            StartLayer(target, binding, false, ResumeTime(target, from, binding), fadeTime);
        }

        bool SameClip(CharacterAnimBinding binding) => binding.clip == current.binding.clip;

        bool ContinueTurnRound(CharacterAnimRole target, CharacterAnimBinding binding)
        {
            // MHK2 'backward' to steering backwards: finish turning round first, then cut over.
            // Anything else: play the turn-round back to the start before the next role (FUN_0042c190).
            if (IsReverseFamily(target))
            {
                current.reversing = false;
                if (current.time >= current.Length - EndEpsilon * 2f) StartLayer(target, binding, false, 0f, 0f);
                return true;
            }
            current.reversing = true;
            return current.time > 0f;
        }

        static float ResumeTime(CharacterAnimRole target, CharacterAnimRole from, CharacterAnimBinding binding) =>
            target == CharacterAnimRole.Backward && IsReverseFamily(from) && binding.clip != null
                ? binding.clip.length - EndEpsilon : 0f;

        static bool IsReverseFamily(CharacterAnimRole r) =>
            r is CharacterAnimRole.Backward or CharacterAnimRole.BackwardTurnLeft or CharacterAnimRole.BackwardTurnRight;

        void StartLocomotion()
        {
            restartLocomotion = false;
            if (Resolve(locomotion, false, out var binding)) StartLayer(locomotion, binding, false, 0f, fadeTime);
        }

        void Advance(ref Layer layer, float dt)
        {
            var len = layer.Length;
            if (len <= 0f) return;
            var end = Mathf.Max(0f, len - EndEpsilon);
            var step = dt * layer.binding.Speed * (!layer.action && layer.binding.SpeedFollowsKart ? rate : 1f);
            switch (layer.Mode)
            {
                case CharacterAnimPlayback.Loop:
                    layer.played += step;
                    layer.time = Mathf.Repeat(layer.played, len);
                    break;
                case CharacterAnimPlayback.PingPong:
                    layer.played += step;
                    layer.time = Mathf.Min(Mathf.PingPong(layer.played, len), end);
                    break;
                case CharacterAnimPlayback.HoldReverse:
                    layer.played += step;
                    layer.time = layer.reversing ? Mathf.Max(0f, layer.time - step) : Mathf.Min(end, layer.time + step);
                    break;
                case CharacterAnimPlayback.SteerScrub:
                    layer.time = scrub * end;
                    layer.played = layer.time;
                    break;
                default:
                    layer.played += step;
                    layer.time = Mathf.Min(end, layer.time + step);
                    break;
            }
        }

        void StartLayer(CharacterAnimRole role, CharacterAnimBinding binding, bool action, float startTime, float fadeIn)
        {
            if (binding.clip == null) return;
            DropPrevious();
            if (current.IsValid)
            {
                graph.Disconnect(mixer, 0);
                previous = current;
                graph.Connect(previous.playable, 0, mixer, 1);
            }

            var playable = AnimationClipPlayable.Create(graph, binding.clip);
            playable.SetApplyFootIK(false);
            // Time is set explicitly every frame, the graph must not advance it.
            playable.SetSpeed(0);
            graph.Connect(playable, 0, mixer, 0);
            current = new Layer
            {
                playable = playable, role = role, binding = binding, action = action,
                time = Mathf.Clamp(startTime, 0f, Mathf.Max(0f, binding.clip.length - EndEpsilon)), played = startTime,
            };
            if (current.Mode == CharacterAnimPlayback.SteerScrub) current.time = scrub * Mathf.Max(0f, binding.clip.length - EndEpsilon);
            // The drive clip picks up the bob's clock, so the bob runs on without a jump between straight and steering.
            if (!action && bob.IsValid && binding.clip == bob.binding.clip && current.Mode == bob.Mode)
            {
                current.time = bob.time;
                current.played = bob.played;
            }
            fadeDuration = fadeIn;
            fade = previous.IsValid && fadeIn > 0f ? 0f : 1f;
            if (fade >= 1f) DropPrevious();
            UpdateWeights();
        }

        void DropPrevious()
        {
            if (!previous.IsValid) return;
            graph.Disconnect(mixer, 1);
            restorePending = true;
            previous.playable.Destroy();
            previous = default;
            UpdateWeights();
        }

        void ApplyTimes()
        {
            // The outgoing clip stays frozen on the pose it had: the cross-fade blends from that pose, like MHK2.
            if (current.IsValid) current.playable.SetTime(current.time);
        }

        // The bob fades in and out with the cross-fade into or out of a steering pose; it is off during actions.
        void UpdateBob(float dt)
        {
            if (!bob.IsValid) return;
            var target = current.IsValid && !current.action && current.Mode == CharacterAnimPlayback.SteerScrub
                && current.role is (CharacterAnimRole.TurnLeft or CharacterAnimRole.TurnRight)
                && current.binding.clip != bob.binding.clip ? 1f : 0f;
            bobWeight = fade < 1f && fadeDuration > 0f ? Mathf.MoveTowards(bobWeight, target, dt / fadeDuration) : target;
            bob.playable.SetTime(bob.time);
            layers.SetInputWeight(1, bobWeight);
        }

        void UpdateWeights()
        {
            mixer.SetInputWeight(0, current.IsValid ? fade : 0f);
            mixer.SetInputWeight(1, previous.IsValid ? 1f - fade : 0f);
        }
    }
}
