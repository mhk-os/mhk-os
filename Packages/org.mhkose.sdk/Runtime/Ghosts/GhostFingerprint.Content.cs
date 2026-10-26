using MHK.Core;
using MHK.Modding;

namespace MHK.Ghosts
{
    // Entry points for the online content fingerprints (Online/ContentFingerprints.cs): the same value lists the ghost
    // hashes use, so a kart, effect or track definition counts the same gameplay data in both. Nothing here feeds the
    // ghost hashes themselves.
    public static partial class GhostFingerprint
    {
        /// <summary>The kart part of <see cref="SetupHash"/>: id, physics profile (or the default one) and the chassis values.</summary>
        internal static void AddContentKart(FingerprintBuilder b, KartDefinition kart) => AddKart(b, GameRoot.Instance.Config, kart);

        /// <summary>The definition part of <see cref="TrackHash"/>: id, gravity, physics overrides.</summary>
        internal static void AddContentTrack(FingerprintBuilder b, TrackDefinition track) => AddTrackDefinition(b, track);

        /// <summary>Every gameplay value of a status effect, nested landing and aura effects included.</summary>
        internal static void AddContentEffect(FingerprintBuilder b, StatusEffectDefinition effect) => AddEffect(b, effect);
    }
}
