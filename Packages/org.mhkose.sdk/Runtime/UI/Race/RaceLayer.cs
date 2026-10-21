using MHK.Core;
using MHK.Race;

namespace MHK.UI
{
    /// <summary>
    /// A layer of the race UI above the HUD (touch controls, pause menu, results): a child GameObject of the
    /// <see cref="RaceHud"/>, which hands it the race and opens or closes it on race events.
    /// </summary>
    public abstract class RaceLayer : UiScreen
    {
        /// <summary>Race this layer shows, set by the HUD before it opens the layer.</summary>
        public RaceManager Race { get; internal set; }

        protected static UserSettings Settings => GameRoot.Instance.Settings;

        protected override bool ReadyToBind => Race != null && GameRoot.Instance != null;
    }
}
