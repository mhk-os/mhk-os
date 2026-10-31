using System;
using System.Collections.Generic;
using UnityEngine;
using Application = UnityEngine.Device.Application;

namespace MHK.Core
{
    /// <summary>When the on-screen touch controls show.</summary>
    public enum TouchControlsMode { Auto, On, Off }

    /// <summary>When a human kart accelerates by itself.</summary>
    public enum AutoAccelerateMode { Auto, On, Off }

    /// <summary>Race camera view (<see cref="Cameras.ChaseCamera"/> presets).</summary>
    public enum CameraMode { Close, Far, Pov }

    /// <summary>User preferences, persisted as JSON in persistentDataPath. Field names are JSON keys: never rename them.</summary>
    [Serializable]
    public class UserSettings
    {
        const string DefaultSignature = "Player";

        public float masterVolume = 1f;
        public float musicVolume = 0.7f;
        public float sfxVolume = 1f;
        [Tooltip("Main menu playlist (Music screen): virtual music paths in play order. Empty = every pack's menu music, or silence when menuMusicEmpty is on.")]
        public List<string> menuMusic = new();
        [Tooltip("The main menu playlist plays in random order (a new order each round).")]
        public bool menuMusicShuffle = true;
        [Tooltip("The player removed every song of the main menu playlist: the menu stays silent. Off with an empty menuMusic = every pack's menu music.")]
        public bool menuMusicEmpty;
        [Tooltip("Track playlists (Music screen), one per track that has its own. A track without one plays its own music.")]
        public List<TrackMusic> trackMusic = new();
        public TouchControlsMode touchControls = TouchControlsMode.Auto;
        [Tooltip("Full throttle while the brake is released (after GO). Auto = on while the touch controls show.")]
        public AutoAccelerateMode autoAccelerate = AutoAccelerateMode.Auto;
        [Tooltip("Race screen layout (UI Layout screen): places, sizes and visibility of the HUD elements and the touch controls.")]
        public UiLayoutSettings uiLayout = new();
        public GraphicsOptions graphics = new();
        [Tooltip("False until the player confirmed the first-run graphics screen.")]
        public bool graphicsChosen;
        [Tooltip("The player continued without the built-in mods on the download screen. The title screen then offers the download.")]
        public bool baseModsSkipped;
        public List<string> disabledPacks = new();
        [Tooltip("Mod load order set in the Mods screen: pack ids, first = highest priority (its content replaces the same ids " +
                 "of the mods below). Mods not listed are placed by their ContentPack.loadOrder.")]
        public List<string> modOrder = new();
        [Tooltip("Asset pack order set in the Mods screen: asset pack ids, first = highest priority (its files win). Packs " +
                 "not listed come after the listed ones.")]
        public List<string> assetPackOrder = new();
        [Tooltip("Asset pack ids switched off in the Mods screen.")]
        public List<string> disabledAssetPacks = new();
        [Tooltip("Mod sources the player added in the Mods screen (GitHub repositories, owner/name), listed after the built-in ones.")]
        public List<string> modSources = new();
        [Tooltip("Built-in mod sources the player removed in the Mods screen: they stay removed until added again.")]
        public List<string> removedModSources = new();
        public string lastCharacterId;
        [Tooltip("Race camera view. The Camera key (minus) cycles it in races.")]
        public CameraMode cameraMode = CameraMode.Close;
        [Tooltip("Keyboard rebinds: Input System binding overrides of MHKControls as JSON (empty = default keys).")]
        public string keyBindings = "";
        [Tooltip("Stable random id of this player (time-trial ghosts, a future server groups runs by it). Created on first use.")]
        public string playerId = "";
        [Tooltip("Own single race and battle item odds (Powerups screen), the same on every track: percent per powerup id. " +
                 "Empty = the default odds. Ids of packs that are not loaded stay stored and are ignored.")]
        public List<PowerupOdds> ownItemOdds = new();
        [Tooltip("Game server (Account screen, advanced): tls://host:port, tcp://host:port (local network only) or host[:port]. " +
                 "Empty = the built-in server. The -server command-line option wins over it.")]
        public string onlineServer = "";
        [Tooltip("Upload a new time-trial personal best to the leaderboards right after the race while logged in (Records screen).")]
        public bool autoUploadBests = true;
        [Tooltip("Driver and kart last chosen in an online lobby (the default loadout of the next lobby).")]
        public string lastOnlineCharacterId = "";
        public string lastOnlineKartId = "";
        [Tooltip("Settings of the last private room the player created (Create room screen).")]
        public Online.RoomSettings lastRoom = new();
        [Tooltip("The player's custom championship (Championship page): track ids in race order, duplicates allowed, at " +
                 "most GameSession.MaxCustomTracks. Tracks that are not loaded are skipped, and dropped when the list is edited.")]
        public List<string> customChampionship = new();

        /// <summary>
        /// The name ghosts are signed with: the account's username (logged in, else the stored session of the server in
        /// use, <see cref="Online.OnlineService.Username"/>), else the system user name, else "Player".
        /// </summary>
        public string SignatureName
        {
            get
            {
                var account = Online.OnlineService.Username;
                if (!string.IsNullOrWhiteSpace(account)) return account.Trim();
                var system = Application.isMobilePlatform ? "" : Environment.UserName;
                return string.IsNullOrWhiteSpace(system) ? DefaultSignature : system;
            }
        }

        /// <summary><see cref="playerId"/>, created (and to be saved) the first time it is needed.</summary>
        public string EnsurePlayerId()
        {
            if (string.IsNullOrEmpty(playerId)) playerId = Guid.NewGuid().ToString("N");
            return playerId;
        }

        /// <summary>The touch controls show: forced on, or Auto on a mobile device.</summary>
        public bool TouchControlsActive =>
            touchControls == TouchControlsMode.On || (touchControls == TouchControlsMode.Auto && Application.isMobilePlatform);

        /// <summary>Human karts accelerate by themselves: forced on, or Auto while the touch controls show (touchscreen play).</summary>
        public bool AutoAccelerateActive =>
            autoAccelerate == AutoAccelerateMode.On || (autoAccelerate == AutoAccelerateMode.Auto && TouchControlsActive);
    }
}
