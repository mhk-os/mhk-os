using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>Title screen (Title.uxml): logo, the built-in mods download while they are missing, the game modes, records / mods / options / music / quit, the short commit of the build.</summary>
    public class TitleScreen : MenuScreen
    {
        protected override void Bind()
        {
            var logo = GameRoot.Instance.Config.gameLogo;
            var hasLogo = logo != null;
            Q<VisualElement>("logo").SetImage(logo);
            SetVisible("logo-wrap", hasLogo);
            SetVisible("logo-text", !hasLogo);
            Q<Label>("version").text = BuildInfo.Commit;
            if (ModInstalls.ReloadIfPending()) return;
            var getMods = OnClick("get-mods", () => ModsScreen.OpenModsSection(Flow));
            getMods.SetVisible(ModSources.BaseGamesMissing);
            if (ModSources.BaseGamesMissing) FocusTarget = getMods;

            OnClick("championship", () => StartMode(GameMode.Championship));
            OnClick("single-race", () => StartMode(GameMode.SingleRace));
            OnClick("time-trial", () => StartMode(GameMode.TimeTrial));
            OnClick("battle", () =>
            {
                Selection.opponents = Mathf.Max(1, Selection.opponents);
                StartMode(GameMode.Battle);
            });
            OnClick("records", () => Flow.Show<RecordsScreen>());
            OnClick("mods", () => Flow.Show<ModsScreen>());
            OnClick("options", () => Flow.Show<OptionsScreen>());
            OnClick("music", () => Flow.Show<MusicScreen>());
            var quit = OnClick("quit", Application.Quit, click: false);
            if (Application.platform == RuntimePlatform.WebGLPlayer) quit.SetVisible(false);
        }

        void StartMode(GameMode mode)
        {
            Selection.mode = mode;
            Flow.Show<CharacterScreen>();
        }
    }
}
