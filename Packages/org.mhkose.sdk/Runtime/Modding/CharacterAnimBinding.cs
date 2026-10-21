using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>How a role's clip is played (see <see cref="CharacterAnimBinding.Playback"/>).</summary>
    public enum CharacterAnimPlayback
    {
        [Tooltip("The role's default (MHK2's animation logic, FUN_0042d6c0).")] Auto,
        [Tooltip("Repeat from the start.")] Loop,
        [Tooltip("Play forwards and backwards alternately (LightWave 'oscillate' envelopes, e.g. MHK3 'Fahren').")] PingPong,
        [Tooltip("Play once and hold the last frame (as an action: return to driving when done).")] Once,
        [Tooltip("Play once and hold the last frame while the role stays active, then play backwards to the start " +
                 "before the next role starts (MHK2 'backward': turning round to look / reverse).")] HoldReverse,
        [Tooltip("The pose follows the commanded steering: straight = first frame, full lock = last frame (MHK2 turnleft/turnright).")] SteerScrub,
    }

    /// <summary>A character's clip for one animation role, with its playback mode and speed.</summary>
    [Serializable]
    public struct CharacterAnimBinding
    {
        public CharacterAnimRole role;
        public AnimationClip clip;
        [Tooltip("How the clip plays. Auto = the role's default.")]
        public CharacterAnimPlayback playback;
        [Tooltip("Playback speed. 0 = the role's default (MHK2: accelerate/shake/backward ×2, collisions/landing/shouts ×1.5, " +
                 "drop/respawn ×1.25, driving speeds up with the kart).")]
        [Min(0f)] public float speed;

        const float MinSpeed = 0.01f;

        public CharacterAnimPlayback Playback => playback != CharacterAnimPlayback.Auto ? playback : DefaultPlayback(role);
        public float Speed => speed > 0f ? speed : DefaultSpeed(role);
        /// <summary>MHK2 drive: the loop runs at 0.2 + 2·min(|v|·0.02, 1) × real time (only with the default speed).</summary>
        public bool SpeedFollowsKart => speed <= 0f && role == CharacterAnimRole.Drive;
        /// <summary>Real-time length of one play-through.</summary>
        public float Duration => clip != null ? clip.length / Mathf.Max(MinSpeed, Speed) : 0f;

        /// <summary>MHK2 per-animation playback (car anim logic FUN_0042d6c0, see work/mhk2/gameplay.md §4.11).</summary>
        public static CharacterAnimPlayback DefaultPlayback(CharacterAnimRole role) => role switch
        {
            CharacterAnimRole.Drive or CharacterAnimRole.Win or CharacterAnimRole.EngineOnly => CharacterAnimPlayback.Loop,
            CharacterAnimRole.Backward => CharacterAnimPlayback.HoldReverse,
            CharacterAnimRole.TurnLeft or CharacterAnimRole.TurnRight
                or CharacterAnimRole.BackwardTurnLeft or CharacterAnimRole.BackwardTurnRight => CharacterAnimPlayback.SteerScrub,
            _ => CharacterAnimPlayback.Once,
        };

        public static float DefaultSpeed(CharacterAnimRole role) => role switch
        {
            CharacterAnimRole.Accelerate or CharacterAnimRole.Shake or CharacterAnimRole.Backward => 2f,
            CharacterAnimRole.JumpLanding or CharacterAnimRole.CollideFront or CharacterAnimRole.CollideBack
                or CharacterAnimRole.CollideLeft or CharacterAnimRole.CollideRight
                or CharacterAnimRole.ShoutLeft or CharacterAnimRole.ShoutRight => 1.5f,
            CharacterAnimRole.Drop or CharacterAnimRole.Respawn => 1.25f,
            _ => 1f,
        };
    }
}
