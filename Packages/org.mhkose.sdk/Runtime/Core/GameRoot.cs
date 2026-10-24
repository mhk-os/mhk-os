using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Core
{
    /// <summary>
    /// Persistent root holding the global services (config, content, settings, save, session). Created by the Boot scene
    /// or lazily on first access.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public partial class GameRoot : MonoBehaviour
    {
        public const string SettingsFile = "settings.json";
        public const string SaveFile = "save.json";
        const string ConfigResource = "GameConfig";
        const int MinPhysicsHz = 30;

        [AutoStaticsCleanup] static GameRoot instance;
        [AutoStaticsCleanup] static bool quitting;

        /// <summary>The running game's root. Created on first use in play mode, null in edit mode (never adds a scene object there).</summary>
        public static GameRoot Instance
        {
            get
            {
                if (instance == null && !quitting)
                {
                    instance = FindAnyObjectByType<GameRoot>();
                    if (instance == null && Application.isPlaying) new GameObject(nameof(GameRoot)).AddComponent<GameRoot>();
                }
                return instance;
            }
        }

        public ContentRegistry Content { get; private set; }
        public GameConfig Config { get; private set; }
        public UserSettings Settings { get; private set; }
        public SaveData Save { get; private set; }
        public GameSession Session { get; set; }
        /// <summary>The loading screen (a child of this object, from <see cref="GameConfig.loadingScreen"/>), null when none is set.</summary>
        public UI.LoadingScreen LoadingScreen { get; private set; }

        readonly CommandLineGraphics commandLineGraphics = new();

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }

        void Initialize()
        {
            LoadConfig();
            LoadUserData();
            LoadContent();
            ModSources.Startup(destroyCancellationToken);
            if (Save.RememberContent(Content)) SaveProgress();
            Time.fixedDeltaTime = 1f / Mathf.Max(MinPhysicsHz, Config.physicsHz);
            CreateChildServices();
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplySettings();
        }

        void LoadConfig()
        {
            Config = Resources.Load<GameConfig>(ConfigResource);
            if (Config == null)
            {
                Debug.LogError("[GameRoot] Resources/GameConfig missing; using defaults.");
                Config = ScriptableObject.CreateInstance<GameConfig>();
            }
            Config.ClearCaches();
        }

        void LoadUserData()
        {
            Settings = JsonStore.Load<UserSettings>(SettingsFile);
            Settings.graphics.Upgrade();
            commandLineGraphics.Apply(Settings);
            ScreenSetup.ApplyAtStartup(Settings.graphics);
            Save = JsonStore.Load<SaveData>(SaveFile);
        }

        void LoadContent()
        {
            Content = new ContentRegistry();
            ModSources.ApplyPending();
            Assets.GameAssets.BeginContent(Settings);
            ModLoader.LoadAll(Content, Settings.disabledPacks, Settings.modOrder);
            ModSources.ContentLoaded();
            Assets.GameAssets.ApplyContent(destroyCancellationToken);
        }

        void CreateChildServices()
        {
            if (!TryGetComponent<Audio.AudioManager>(out _)) gameObject.AddComponent<Audio.AudioManager>();
            if (Config.loadingScreen == null) return;
            LoadingScreen = Instantiate(Config.loadingScreen, transform);
            LoadingScreen.gameObject.SetActive(false);
        }

        /// <summary>Applies the volume and graphics settings.</summary>
        public void ApplySettings()
        {
            ApplyAudioSettings();
            ApplyGraphicsSettings();
        }

        /// <summary>Applies the volume settings only (cheap: for sliders that change every frame).</summary>
        public void ApplyAudioSettings() => Audio.AudioManager.RefreshVolumes();

        public void ApplyGraphicsSettings() => GraphicsQuality.Apply(Settings.graphics);

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => GraphicsQuality.ApplyToScene();

        void Update() => GraphicsQuality.Tick();

        /// <summary>Writes the settings file (with the player's own graphics while command-line graphics are active).</summary>
        public void SaveSettings() => commandLineGraphics.Save(Settings, s => JsonStore.Save(SettingsFile, s));

        public void SaveProgress() => JsonStore.Save(SaveFile, Save);

        /// <summary>
        /// Unloads every mod and registers the packs again in <see cref="UserSettings.modOrder"/>, without those in <see cref="UserSettings.disabledPacks"/>.
        /// Only while no scene uses content: <see cref="SceneFlow.ReloadContent"/> runs it between menu unload and reload.
        /// </summary>
        internal void ReloadContent()
        {
            Content.Clear();
            AI.AIItemProfile.ClearCache();
            Powerups.ItemBoxLook.ReleaseTints();
            Assets.GameAssets.UnloadContent();
            ModLoader.UnloadExternal();
            ModSources.ApplyPending();
            Assets.GameAssets.BeginContent(Settings);
            ModLoader.LoadAll(Content, Settings.disabledPacks, Settings.modOrder);
            ModSources.ContentLoaded();
            Assets.GameAssets.ApplyContent(destroyCancellationToken);
            if (Save.RememberContent(Content)) SaveProgress();
        }

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            if (instance != this) return;
            instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}
