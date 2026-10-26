using System;
using System.Collections.Generic;
using MHK.Audio;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.UI
{
    /// <summary>
    /// Main menu owner: its child screens (exactly one active), the back history, the player's choices
    /// (<see cref="Selection"/>), menu music and the 3D showcase. As an overlay it hosts menu screens over another
    /// scene instead (the race pause menu's options): <see cref="Open{T}"/> shows one, Back from it closes the overlay.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public partial class MenuFlow : MonoBehaviour
    {
        [Tooltip("Turntable that shows the selected driver and kart.")]
        public MenuShowcase showcase;
        [SerializeField, Tooltip("Menu stage that spins the showcase. Selection pages frame the turntable through it.")]
        MenuStage stage;
        [SerializeField, Tooltip("Volume (0-1) of the main menu playlist and of the Music screen's player.")]
        [Range(0f, 1f)] float menuMusicVolume = 0.6f;
        [SerializeField, Tooltip("Empty: the highest-priority content pack's UI click.")]
        AudioClip clickSound;
        [Tooltip("Opened at start.")]
        public MenuScreen titleScreen;
        [Tooltip("Opened instead of the title screen until the player picked graphics settings once.")]
        public MenuScreen firstRunScreen;
        [SerializeField, Tooltip("Flat ambient light while the menu is shown (matches the backdrop picture).")]
        Color ambientLight = new(0.86f, 0.74f, 0.66f);
        [SerializeField, Tooltip("Hosts menu screens over another scene (the race pause menu's options): no menu music, lighting, " +
                                 "showcase or start screen. Back from the first opened screen closes the overlay.")]
        bool overlay;

        readonly Stack<MenuScreen> history = new();

        public MenuScreen Current { get; private set; }
        public MenuSelection Selection { get; } = new();
        public float MenuMusicVolume => menuMusicVolume;

        /// <summary>Overlay: the overlay closed (Back from its first screen, or <see cref="Close"/>).</summary>
        public event Action Closed;

        /// <summary>
        /// Screen type the next menu start opens on top of the title screen (Back returns to the title), cleared once
        /// used. The Mods screen's Reload sets it to come back after the content reload.
        /// </summary>
        [AutoStaticsCleanup] public static Type OpenOnStart { get; set; }

        ContentRegistry Content => GameRoot.Instance.Content;

        void Awake()
        {
            // Screens left enabled in the scene (for previewing them in the editor) start closed.
            foreach (var s in GetComponentsInChildren<MenuScreen>(true)) s.gameObject.SetActive(false);
        }

        void Start()
        {
            if (clickSound == null) clickSound = ContentPack.Find(Content.Packs, p => p.uiClick);
            if (overlay) return;
            MusicPlayer.PlayMenu(menuMusicVolume);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambientLight;

            var settings = GameRoot.Instance.Settings;
            var last = Content.Get<CharacterDefinition>(settings.lastCharacterId);
            var all = Content.All<CharacterDefinition>();
            Selection.character = last != null ? last : all.Count > 0 ? all[0] : null;
            Selection.kart = MenuSelection.LoadedDefaultKart(Selection.character);
            ShowcaseSelection();
            var start = settings.graphicsChosen || firstRunScreen == null ? StartScreen() : firstRunScreen;
            Show(start, false);
            var reopen = OpenOnStart;
            OpenOnStart = null;
            if (start != titleScreen) return;
            // A screen asked for (online lobby, mods), else back to the track's records page after a time trial.
            if (reopen != null) Show(GetComponentInChildren(reopen, true) as MenuScreen);
            else TrackRecordsScreen.ReturnAfterRace(this);
        }

        // The built-in mods download while they are missing (unless the player skipped it), else the title.
        MenuScreen StartScreen()
        {
            var baseMods = Get<BaseModsScreen>();
            var wanted = baseMods != null && ModSources.Enabled && ModSources.BaseGamesMissing && !GameRoot.Instance.Settings.baseModsSkipped;
            return wanted ? baseMods : titleScreen;
        }

        /// <summary>The child screen of type <typeparamref name="T"/>.</summary>
        public T Get<T>() where T : MenuScreen => GetComponentInChildren<T>(true);

        /// <summary>Opens <paramref name="screen"/> (fresh from its UXML). <paramref name="push"/> lets Back return here.</summary>
        public void Show(MenuScreen screen, bool push = true)
        {
            if (screen == null) return;
            if (push && Current != null && Current != screen) history.Push(Current);
            var previous = Current;
            Current = screen;
            if (previous != null && previous != screen) previous.gameObject.SetActive(false);
            SetShowcaseVisible(screen.ShowcaseVisible);
            if (screen.gameObject.activeSelf) screen.Rebuild();
            else screen.gameObject.SetActive(true);
        }

        public void Show<T>(bool push = true) where T : MenuScreen => Show(Get<T>(), push);

        /// <summary>Returns to the previous screen (saving the settings), if there is one. In an overlay the first screen closes it.</summary>
        public void Back()
        {
            if (history.Count == 0 && !(overlay && Current != null)) return;
            Click();
            GameRoot.Instance.SaveSettings();
            if (history.Count == 0)
            {
                Close();
                return;
            }
            if (Current is CharacterScreen) Selection.trackFixed = false;
            Show(history.Pop(), false);
        }

        /// <summary>Overlay: opens <typeparamref name="T"/> as its first screen (Back from it closes the overlay).</summary>
        public void Open<T>() where T : MenuScreen
        {
            ClearHistory();
            Show<T>(false);
        }

        /// <summary>Overlay: closes the open screen (and the ones below it) without saving.</summary>
        public void Close()
        {
            if (Current == null) return;
            ClearHistory();
            var last = Current;
            Current = null;
            last.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        /// <summary>Clears the history (and a fixed track) and opens the title screen.</summary>
        public void ShowTitle()
        {
            ClearHistory();
            Selection.trackFixed = false;
            Show(titleScreen, false);
        }

        /// <summary>After the first run screen: the built-in mods download while they are missing, else the title screen.</summary>
        public void ShowStart()
        {
            ClearHistory();
            Show(StartScreen(), false);
        }

        public void ClearHistory() => history.Clear();

        public void Click() => AudioManager.PlayUi(clickSound);

        public void SetShowcaseVisible(bool visible)
        {
            if (showcase != null && showcase.gameObject.activeSelf != visible) showcase.gameObject.SetActive(visible);
        }

        /// <summary>Shows <paramref name="character"/> on <paramref name="kart"/> on the turntable.</summary>
        public void ShowOnTurntable(CharacterDefinition character, KartDefinition kart)
        {
            if (showcase != null && character != null) showcase.Show(character, kart);
        }

        public void ShowcaseSelection() => ShowOnTurntable(Selection.character, Selection.kart);

        /// <summary>Puts the turntable into <paramref name="viewportArea"/> (viewport units, y up). Null: its place in the scene.</summary>
        public void FrameShowcase(Rect? viewportArea)
        {
            if (stage != null) stage.FrameTurntable(viewportArea);
        }

        /// <summary>
        /// After driver and kart: saves the choice and opens the cup (championship) or track choice, or what follows the
        /// track choice when the track is fixed (<see cref="MenuSelection.trackFixed"/>).
        /// </summary>
        public void ShowTrackChoice()
        {
            GameRoot.Instance.SaveSettings();
            ShowcaseSelection();
            if (Selection.mode == GameMode.Championship) Show<CupScreen>();
            else if (Selection.trackFixed && Selection.track != null) Get<TrackScreen>().Continue(Selection.track);
            else Show<TrackScreen>();
        }
    }
}
