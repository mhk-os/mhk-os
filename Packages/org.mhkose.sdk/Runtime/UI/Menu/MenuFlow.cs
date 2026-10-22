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
    /// (<see cref="Selection"/>), menu music and the 3D showcase.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public partial class MenuFlow : MonoBehaviour
    {
        [Tooltip("Turntable that shows the selected driver and kart.")]
        public MenuShowcase showcase;
        [SerializeField, Tooltip("Menu stage that spins the showcase. Selection pages frame the turntable through it.")]
        MenuStage stage;
        [SerializeField, Tooltip("Empty: the highest-priority content pack's menu music.")]
        AudioClip menuMusic;
        [SerializeField, Tooltip("Menu music volume (0-1).")]
        [Range(0f, 1f)] float menuMusicVolume = 0.6f;
        [SerializeField, Tooltip("Empty: the highest-priority content pack's UI click.")]
        AudioClip clickSound;
        [Tooltip("Opened at start.")]
        public MenuScreen titleScreen;
        [Tooltip("Opened instead of the title screen until the player picked graphics settings once.")]
        public MenuScreen firstRunScreen;
        [SerializeField, Tooltip("Flat ambient light while the menu is shown (matches the backdrop picture).")]
        Color ambientLight = new(0.86f, 0.74f, 0.66f);

        readonly Stack<MenuScreen> history = new();

        public MenuScreen Current { get; private set; }
        public MenuSelection Selection { get; } = new();

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
            var packs = Content.Packs;
            if (menuMusic == null) menuMusic = ContentPack.Find(packs, p => p.menuMusic);
            if (clickSound == null) clickSound = ContentPack.Find(packs, p => p.uiClick);
            if (menuMusic != null) AudioManager.PlayMusic(menuMusic, menuMusicVolume);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambientLight;

            var settings = GameRoot.Instance.Settings;
            var last = Content.Get<CharacterDefinition>(settings.lastCharacterId);
            var all = Content.All<CharacterDefinition>();
            Selection.character = last != null ? last : all.Count > 0 ? all[0] : null;
            Selection.kart = MenuSelection.LoadedDefaultKart(Selection.character);
            ShowcaseSelection();
            Show(settings.graphicsChosen || firstRunScreen == null ? titleScreen : firstRunScreen, false);
            var reopen = OpenOnStart;
            OpenOnStart = null;
            if (reopen != null && settings.graphicsChosen) Show(GetComponentInChildren(reopen, true) as MenuScreen);
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

        /// <summary>Returns to the previous screen (saving the settings), if there is one.</summary>
        public void Back()
        {
            if (history.Count == 0) return;
            Click();
            GameRoot.Instance.SaveSettings();
            if (Current is CharacterScreen) Selection.trackFixed = false;
            Show(history.Pop(), false);
        }

        /// <summary>Clears the history (and a fixed track) and opens the title screen.</summary>
        public void ShowTitle()
        {
            ClearHistory();
            Selection.trackFixed = false;
            Show(titleScreen, false);
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
