using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Semantic animation slots a driver can play. Missing slots fall back via <see cref="CharacterDefinition.TryResolve(CharacterAnimRole, out CharacterAnimBinding)"/>.
    /// Append new roles at the end only: role numbers are serialized in assets and ghost files.
    /// </summary>
    public enum CharacterAnimRole
    {
        Idle, Drive, Accelerate, Brake, Backward,
        TurnLeft, TurnRight, BackwardTurnLeft, BackwardTurnRight,
        LookLeft, LookRight, ShoutLeft, ShoutRight,
        CollideFront, CollideBack, CollideLeft, CollideRight,
        JumpLanding, Shake, ShotDown, Drop, Respawn,
        Start, PowerStart, PowerFail, Win, Lose, EngineOnly,
        [Tooltip("Dazed, e.g. stars circling the head (TINcan jamming signal victim). Falls back to Shake.")] Dizzy,
        [Tooltip("Electric shock (TINcan shocker victim). Falls back to Shake.")] Shocked,
        [Tooltip("On fire (TINcan flame thrower victim). Falls back to ShotDown.")] Burning,
        [Tooltip("Crushed by a press or a heavy object. Falls back to ShotDown.")] Crushed,
    }
}
