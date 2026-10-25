using System.Collections.Generic;
using MHK.Modding;
using UnityEditor;
using UnityEngine.UIElements;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Editor side of <see cref="ModSources"/>: <b>MHK ▸ Modding ▸ Mod Update Check in Play Mode</b> lets play mode check
    /// the mod sources at start like a player (off by default, no network in normal development), and Preferences ▸ MHK
    /// Mod Updates holds another GitHub API base URL for tests with a mock server.
    /// </summary>
    [InitializeOnLoad]
    public static class ModSourcesPrefs
    {
        const string AutomaticKey = "MHK.ModSources.EditorAuto";
        const string ApiKey = "MHK.ModSources.ApiBase";
        const string MenuPath = "MHK/Modding/Mod Update Check in Play Mode";
        const string PreferencesPath = "Preferences/MHK Mod Updates";

        static ModSourcesPrefs()
        {
            ModSources.EditorAutomatic = () => Automatic;
            ModSources.EditorApiBase = () => ApiBase;
        }

        /// <summary>Whether play mode checks the mod sources at start like a player.</summary>
        public static bool Automatic
        {
            get => EditorPrefs.GetBool(AutomaticKey, false);
            set => EditorPrefs.SetBool(AutomaticKey, value);
        }

        /// <summary>GitHub API base URL for play mode, empty for <see cref="ModSources.DefaultApi"/>.</summary>
        public static string ApiBase
        {
            get => EditorPrefs.GetString(ApiKey, "");
            set => EditorPrefs.SetString(ApiKey, value?.Trim() ?? "");
        }

        [MenuItem(MenuPath, priority = 45)]
        static void Toggle() => Automatic = !Automatic;

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Automatic);
            return true;
        }

        [SettingsProvider]
        static SettingsProvider Preferences() => new(PreferencesPath, SettingsScope.User)
        {
            label = "MHK Mod Updates",
            activateHandler = (_, root) =>
            {
                root.style.paddingLeft = 10;
                var automatic = new Toggle("Mod update check in play mode") { value = Automatic, tooltip = "Check the mod sources at start, like a player." };
                automatic.RegisterValueChangedCallback(e => Automatic = e.newValue);
                root.Add(automatic);
                var api = new TextField("GitHub API base URL") { value = ApiBase, tooltip = "Empty for " + ModSources.DefaultApi + ". A local mock server for tests, e.g. http://127.0.0.1:8765." };
                api.RegisterValueChangedCallback(e => ApiBase = e.newValue);
                root.Add(api);
            },
            keywords = new HashSet<string> { "mods", "update", "github", "sources" },
        };
    }
}
