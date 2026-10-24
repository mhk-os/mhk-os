using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using UnityEditor;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// The packs play mode loads from the project like installed mods: always the shipped base content (players load it
    /// from their Mods folder), and the project's other mods with <b>MHK ▸ Modding ▸ Load Project Mods in Play Mode</b>.
    /// When play mode ends, project definitions get back the references a mod's replacements took over.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectModPacks
    {
        const string PrefKey = "MHK.Modding.LoadProjectMods";
        const string MenuPath = "MHK/Modding/Load Project Mods in Play Mode";

        static ProjectModPacks()
        {
            ModLoader.EditorProjectPacks = PlayModePacks;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredEditMode) ModLoader.RestoreResolvedReferences();
            };
        }

        /// <summary>Whether play mode also loads the project's mod packs (the shipped base content always loads).</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, false);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        [MenuItem(MenuPath, priority = 40)]
        static void Toggle() => Enabled = !Enabled;

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        static IEnumerable<ContentPack> PlayModePacks()
        {
            var packs = ContentPackAssets.ShippedPacks();
            if (Enabled) packs.AddRange(All());
            return packs;
        }

        /// <summary>Top-level content packs of the project that are neither the game's own nor shipped (the project's mods).</summary>
        public static IEnumerable<ContentPack> All()
        {
            var known = new HashSet<ContentPack>(ContentPackAssets.BuiltinPacks());
            known.UnionWith(ContentPackAssets.ShippedPacks().SelectMany(p => p.SelfAndIncluded()));
            return ContentPackAssets.TopLevel(EditorAssets.FindAll<ContentPack>().Where(p => !known.Contains(p)));
        }
    }
}
