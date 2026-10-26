using MHK.Core;
using MHK.Modding;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>
    /// Title screen (Title.uxml): logo, the built-in mods download while they are missing, the game modes (Time Trials
    /// opens the records hub), Online, mods (dot for updates and downloads) / options / music / quit, the short commit of
    /// the build, the game update link and the online account (username or Log in). Without Quit (mobile, WebGL) Music
    /// joins Mods and Options in one row; pages shorter than <see cref="compactHeight"/> get a smaller logo.
    /// </summary>
    public class TitleScreen : MenuScreen
    {
        [SerializeField, Tooltip("Account button text while no account is logged in or stored.")]
        string loginText = "Log in";
        [SerializeField, Tooltip("Page height (panel units) below which the logo shrinks (title-compact) so the buttons stay clear of the footer: wide phones, ultrawide monitors.")]
        float compactHeight = 1000f;

        Button getMods, gameUpdate;
        Label accountLabel;
        VisualElement modsDot;

        protected override void Bind()
        {
            var logo = GameRoot.Instance.Config.gameLogo;
            var hasLogo = logo != null;
            Q<VisualElement>("logo").SetImage(logo);
            SetVisible("logo-wrap", hasLogo);
            SetVisible("logo-text", !hasLogo);
            Q<Label>("version").text = BuildInfo.Commit;
            var page = Q<VisualElement>("page");
            page.RegisterCallback<GeometryChangedEvent>(_ => page.EnableInClassList("title-compact", page.layout.height < compactHeight));
            getMods = OnClick("get-mods", () => ModsScreen.OpenModsSection(Flow));
            if (ModSources.BaseGamesMissing && !ModSources.AnyDownloading) FocusTarget = getMods;
            modsDot = Q<VisualElement>("mods-dot");
            gameUpdate = OnClick("game-update", () => Application.OpenURL(GameUpdate.ReleaseUrl));
            OnClick("account", () => Flow.Show<AccountScreen>());
            accountLabel = Q<Label>("account-label");
            BindModes();
            ModSources.Changed += ShowSources;
            GameUpdate.Changed += ShowGameUpdate;
            OnlineService.Changed += ShowAccount;
            ShowSources();
            ShowGameUpdate();
            ShowAccount();
        }

        protected override void Unbind()
        {
            base.Unbind();
            ModSources.Changed -= ShowSources;
            GameUpdate.Changed -= ShowGameUpdate;
            OnlineService.Changed -= ShowAccount;
            getMods = gameUpdate = null;
            accountLabel = null;
            modsDot = null;
        }

        void BindModes()
        {
            OnClick("championship", () => StartMode(GameMode.Championship));
            OnClick("single-race", () => StartMode(GameMode.SingleRace));
            // Time trials start from a track's page in the hub (records, leaderboard, ghosts to race).
            OnClick("time-trials", () => Flow.Show<RecordsScreen>());
            OnClick("battle", () =>
            {
                Selection.opponents = Mathf.Max(1, Selection.opponents);
                StartMode(GameMode.Battle);
            });
            OnClick("online", () => Flow.Show<OnlineScreen>());
            OnClick("mods", () => Flow.Show<ModsScreen>());
            OnClick("options", () => Flow.Show<OptionsScreen>());
            OnClick("music", () => Flow.Show<MusicScreen>());
            var quit = OnClick("quit", Application.Quit, click: false);
            if (Application.isMobilePlatform || Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // No Quit: Music joins Mods and Options instead of filling the second row alone.
                Q<Button>("options").parent.Add(Q<Button>("music"));
                quit.parent.SetVisible(false);
            }
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

        void ShowAccount()
        {
            if (accountLabel == null) return;
            accountLabel.text = OnlineService.Username ?? loginText;
        }

        void StartMode(GameMode mode)
        {
            Selection.mode = mode;
            Selection.lobbyPick = LobbyPick.None;
            Flow.Show<CharacterScreen>();
        }
    }
}
