using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine.UIElements;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Dev mods folders: folders with built mods (e.g. a mod project's Builds/Mods) that play mode loads before the
    /// installed mods, to test what ships. Kept in the editor preferences (Preferences ▸ MHK Mods), players take
    /// <c>-mods &lt;folder&gt;</c> instead. A folder with one subfolder per platform loads the editor platform's one.
    /// </summary>
    [InitializeOnLoad]
    public static class DevModFolders
    {
        const string PrefKey = "MHK.Modding.DevModFolders";
        const string MenuRoot = "MHK/Modding/Dev Mods Folders/";
        const string PreferencesPath = "Preferences/MHK Mods";

        static DevModFolders() => ModLoader.EditorModFolders = () => Folders;

        /// <summary>The folders in search order (absolute paths).</summary>
        public static IReadOnlyList<string> Folders
        {
            get => EditorPrefs.GetString(PrefKey, "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            set => EditorPrefs.SetString(PrefKey, string.Join("\n", value.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct()));
        }

        /// <summary>Adds <paramref name="folder"/> (made absolute) at the end of the list.</summary>
        public static void Add(string folder) => Folders = Folders.Append(Path.GetFullPath(folder)).ToList();

        /// <summary>Removes <paramref name="folder"/> from the list.</summary>
        public static void Remove(string folder) => Folders = Folders.Where(f => f != folder).ToList();

        [MenuItem(MenuRoot + "Add Folder...", priority = 41)]
        static void AddMenu()
        {
            var folder = EditorUtility.OpenFolderPanel("Dev mods folder", "Builds/Mods", "");
            if (!string.IsNullOrEmpty(folder)) Add(folder);
        }

        [MenuItem(MenuRoot + "Edit...", priority = 42)]
        static void EditMenu() => SettingsService.OpenUserPreferences(PreferencesPath);

        [MenuItem(MenuRoot + "Clear", priority = 43)]
        static void ClearMenu() => Folders = Array.Empty<string>();

        [MenuItem(MenuRoot + "Clear", true)]
        static bool ClearValidate() => Folders.Count > 0;

        [SettingsProvider]
        static SettingsProvider Preferences() => new(PreferencesPath, SettingsScope.User)
        {
            label = "MHK Mods",
            activateHandler = (_, root) =>
            {
                root.style.paddingLeft = 10;
                root.Add(new Label("Dev mods folders (searched before the installed mods)") { style = { unityFontStyleAndWeight = UnityEngine.FontStyle.Bold } });
                var list = new VisualElement();
                root.Add(list);
                FillList(list);
                root.Add(new Button(() => { AddMenu(); FillList(list); }) { text = "Add Folder..." });
            },
            keywords = new HashSet<string> { "mods", "dev", "folder", "mhkmod" },
        };

        static void FillList(VisualElement list)
        {
            list.Clear();
            if (Folders.Count == 0) list.Add(new Label("No dev mods folders."));
            foreach (var folder in Folders)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
                row.Add(new Label(folder) { style = { flexGrow = 1 } });
                row.Add(new Button(() => { Remove(folder); FillList(list); }) { text = "Remove" });
                list.Add(row);
            }
        }
    }
}
