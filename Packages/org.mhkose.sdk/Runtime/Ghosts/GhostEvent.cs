using MHK.Modding;

namespace MHK.Ghosts
{
    /// <summary>Kinds of <see cref="GhostEvent"/>. Values are stored in ghost files.</summary>
    public enum GhostEventKind : byte
    {
        /// <summary>An action animation started (<see cref="GhostEvent.role"/>, <see cref="GhostEvent.hold"/>).</summary>
        Action = 0,
        /// <summary>A held action animation was released.</summary>
        Release = 1,
        /// <summary>Driver and kart model cut to their rest pose (respawn).</summary>
        Snap = 2,
        /// <summary>A status effect started (its attached visual appears).</summary>
        EffectApplied = 3,
        /// <summary>A status effect ended (its attached visual goes).</summary>
        EffectEnded = 4,
    }

    /// <summary>Something that happened to the recorded kart's visuals between ticks, at race time <see cref="time"/>.</summary>
    public struct GhostEvent
    {
        public float time;
        public GhostEventKind kind;
        public CharacterAnimRole role;
        public bool hold;
        /// <summary>Status effect id (<see cref="GhostEventKind.EffectApplied"/> / <see cref="GhostEventKind.EffectEnded"/>).</summary>
        public string effectId;
    }
}
