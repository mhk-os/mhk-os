using MHK.Modding;

namespace MHK.Karts
{
    /// <summary>
    /// The one path for hitting a kart: powerups, track hazards and props apply their reaction (a
    /// <see cref="StatusEffectDefinition"/>, usually based on a shared core.reaction.* definition) through it.
    /// </summary>
    public static class KartReactions
    {
        /// <summary>Stopped by an explosion, burnt look, respawn (rocket, missile, flame ring without the respawn).</summary>
        public const string Blast = "core.reaction.blast";
        /// <summary>Squashed flat and held, other karts drive over it, put back on the track (hammer, 16-t, stompers).</summary>
        public const string Flatten = "core.reaction.flatten";
        /// <summary>Spins out with locked controls while it coasts to a stop (rammed, panzer, water bomb, banana).</summary>
        public const string SpinOut = "core.reaction.spinout";
        /// <summary>Electric shock: almost stopped, no control (electroshock, shocker).</summary>
        public const string Stun = "core.reaction.stun";
        /// <summary>Held in place (frozen, magnet).</summary>
        public const string Freeze = "core.reaction.freeze";
        /// <summary>Stuck and coasting with locked controls (chewing gum, x-field).</summary>
        public const string Slide = "core.reaction.slide";
        /// <summary>Low grip, the kart slides around (soap, oil slick).</summary>
        public const string Slip = "core.reaction.slip";
        /// <summary>Falls into a hole and respawns (black hole).</summary>
        public const string Fall = "core.reaction.fall";
        /// <summary>Vanishes (not drawn, intangible) and respawns (MHK Extra firework rocket).</summary>
        public const string Vanish = "core.reaction.vanish";

        /// <summary>
        /// Applies <paramref name="reaction"/> to <paramref name="victim"/> on behalf of <paramref name="source"/> (null for
        /// hazards). False when nothing landed: no victim or reaction, shield, ghost, respawn protection, already flattened.
        /// <paramref name="usage"/> is the powerup use the hit belongs to (<see cref="Powerups.PowerupContext.usage"/>, 0 =
        /// none): a victim loses health once per use, every hit still applies the reaction.
        /// </summary>
        public static bool Apply(Kart victim, StatusEffectDefinition reaction, Kart source = null, float durationOverride = -1f, int usage = 0) =>
            victim != null && reaction != null && victim.Effects.Apply(reaction, source, durationOverride, usage);
    }
}
