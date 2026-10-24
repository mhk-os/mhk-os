using System;
using System.Collections.Generic;
using UnityEngine;
using Application = UnityEngine.Device.Application;

namespace MHK.Core
{
    /// <summary>When the on-screen touch controls show.</summary>
    public enum TouchControlsMode { Auto, On, Off }

    /// <summary>Race camera view (<see cref="Cameras.ChaseCamera"/> presets).</summary>
    public enum CameraMode { Close, Far, Pov }

    /// <summary>User preferences, persisted as JSON in persistentDataPath. Field names are JSON keys: never rename them.</summary>
    [Serializable]
    public class UserSettings
    {
        public const float MaxMinimapScale = 2f;
        const string DefaultSignature = "Player";

        public float masterVolume = 1f;
        public float musicVolume = 0.7f;
        public float sfxVolume = 1f;
        [Tooltip("Main menu playlist (Music screen): virtual music paths in play order. Empty = every pack's menu music.")]
        public List<string> menuMusic = new();
        [Tooltip("The main menu playlist plays in random order (a new order each round).")]
        public bool menuMusicShuffle = true;
        [Tooltip("Track playlists (Music screen), one per track that has its own. A track without one plays its own music.")]
        public List<TrackMusic> trackMusic = new();
        public TouchControlsMode touchControls = TouchControlsMode.Auto;
        [Tooltip("Places, sizes and opacity of the on-screen touch controls (Touch Layout screen).")]
        public TouchLayoutSettings touchLayout = new();
        public GraphicsOptions graphics = new();
        [Tooltip("False until the player confirmed the first-run graphics screen.")]
        public bool graphicsChosen;
        [Tooltip("Race minimap size: 0 = hidden, 1 = default, up to MaxMinimapScale.")]
        public float minimapScale = 1f;
        public List<string> disabledPacks = new();
        [Tooltip("Mod load order set in the Mods screen: pack ids, first = highest priority (its content replaces the same ids " +
                 "of the mods below). Mods not listed are placed by their ContentPack.loadOrder.")]
        public List<string> modOrder = new();
        [Tooltip("Asset pack order set in the Mods screen: asset pack ids, first = highest priority (its files win). Packs " +
                 "not listed come after the listed ones.")]
        public List<string> assetPackOrder = new();
        [Tooltip("Asset pack ids switched off in the Mods screen.")]
        public List<string> disabledAssetPacks = new();
        public string lastCharacterId;
        [Tooltip("Race camera view. The Camera key (minus) cycles it in races.")]
        public CameraMode cameraMode = CameraMode.Close;
        [Tooltip("Keyboard rebinds: Input System binding overrides of MHKControls as JSON (empty = default keys).")]
        public string keyBindings = "";
        [Tooltip("Name written into time-trial ghosts (Records screen), empty = the system user name.")]
        public string playerName = "";
        [Tooltip("Stable random id of this player (time-trial ghosts, a future server groups runs by it). Created on first use.")]
        public string playerId = "";
        [Tooltip("Own single race and battle item odds, one set per item family of the track (Powerups screen). A family " +
                 "without a set uses the default odds.")]
        public List<ItemOddsSet> itemOdds = new();

        /// <summary>The player's own item odds for tracks of the family <paramref name="family"/> (top-level pack id), null = default odds.</summary>
        public ItemOddsSet ItemOddsFor(string family)
        {
            for (var i = 0; i < itemOdds.Count; i++)
                if (itemOdds[i] != null && itemOdds[i].family == family) return itemOdds[i];
            return null;
        }

        /// <summary>The name ghosts are signed with: <see cref="playerName"/>, else the system user name, else "Player".</summary>
        public string SignatureName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(playerName)) return playerName.Trim();
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
    }
}
