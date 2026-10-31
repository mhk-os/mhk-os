using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>Credits (Credits.uxml): project acknowledgments and the Moorhuhnverse Discord invite.</summary>
    public class CreditsScreen : MenuScreen
    {
        protected override void Bind()
        {
            OnClick("discord", () => Application.OpenURL("https://discord.gg/buJ64SrHxY"));
        }
    }
}
