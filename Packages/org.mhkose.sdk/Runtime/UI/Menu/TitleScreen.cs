using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>
    /// Title screen (Title.uxml): logo, the built-in mods download while they are missing, the game modes, records / mods
    /// (dot for updates and downloads) / options / music / quit, the short commit of the build and the game update link.
    /// </summary>
    public class TitleScreen : MenuScreen
    {
        Button getMods, gameUpdate;
        VisualElement modsDot;

        protected override void Bind()
        {
            var logo = GameRoot.Instance.Config.gameLogo;
            var hasLogo = logo != null;
            Q<VisualElement>("logo").SetImage(logo);
            SetVisible("logo-wrap", hasLogo);
            SetVisible("logo-text", !hasLogo);
            Q<Label>("version").text = BuildInfo.Commit;
            getMods = OnClick("get-mods", () => ModsScreen.OpenModsSection(Flow));
            if (ModSources.BaseGamesMissing && !ModSources.AnyDownloading) FocusTarget = getMods;
            modsDot = Q<VisualElement>("mods-dot");
            gameUpdate = OnClick("game-update", () => Application.OpenURL(GameUpdate.ReleaseUrl));
            BindModes();
            ModSources.Changed += ShowSources;
            GameUpdate.Changed += ShowGameUpdate;
            ShowSources();
            ShowGameUpdate();
        }

        protected override void Unbind()
        {
            base.Unbind();
            ModSources.Changed -= ShowSources;
            GameUpdate.Changed -= ShowGameUpdate;
            getMods = gameUpdate = null;
            modsDot = null;
        }

        void BindModes()
        {
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

        void ShowSources()
        {
            if (getMods == null) return;
            var downloading = ModSources.AnyDownloading;
            getMods.SetVisible(ModSources.BaseGamesMissing && !downloading);
            var updates = ModSources.HasUpdates(ModSources.Kind.Mod) || ModSources.HasUpdates(ModSources.Kind.AssetPack);
            NotifyDot.Show(modsDot, updates, downloading);
        }

        void ShowGameUpdate()
        {
            if (gameUpdate == null) return;
            gameUpdate.SetVisible(BuildInfo.Number > 0 && GameUpdate.Available && !string.IsNullOrEmpty(GameUpdate.ReleaseUrl));
        }

        void StartMode(GameMode mode)
        {
            Selection.mode = mode;
            Flow.Show<CharacterScreen>();
        }
    }
}
