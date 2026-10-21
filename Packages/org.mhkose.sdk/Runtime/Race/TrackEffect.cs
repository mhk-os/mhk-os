using MHK.Core;
using MHK.Modding;

namespace MHK.Race
{
    /// <summary>Status effects named by track components (zones, hazards).</summary>
    static class TrackEffect
    {
        /// <summary>The registered effect <paramref name="effectId"/>, null when empty, unknown or no game is running.</summary>
        public static StatusEffectDefinition Resolve(string effectId) =>
            string.IsNullOrEmpty(effectId) || GameRoot.Instance == null ? null : GameRoot.Instance.Content.Get<StatusEffectDefinition>(effectId);
    }
}
