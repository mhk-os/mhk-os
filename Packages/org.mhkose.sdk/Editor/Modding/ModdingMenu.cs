using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>The <b>MHK ▸ Modding</b> menu commands for building, installing and creating mod packs.</summary>
    static class ModdingMenu
    {
        const string MenuRoot = "MHK/Modding/";
        const string BuildSelectedPath = MenuRoot + "Build Selected Packs as Mods";
        const string BuildAllPath = MenuRoot + "Build Selected Packs as Mods for All Platforms";
        const string LoosePath = MenuRoot + "Loose Textures and Sounds in Desktop Builds";
        const string LoosePrefKey = "MHK.Modding.LooseBuilds";

        /// <summary>Platforms of <i>Build Selected Packs as Mods for All Platforms</i> (those whose build support is installed).</summary>
        static readonly BuildTarget[] AllTargets =
        {
            BuildTarget.StandaloneOSX, BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64, BuildTarget.Android,
        };

        [MenuItem(BuildSelectedPath, priority = 20)]
        static void BuildSelected() => BuildSelectedFor(EditorUserBuildSettings.activeBuildTarget, true);

        [MenuItem(BuildAllPath, priority = 21)]
        static void BuildSelectedAll()
        {
            // A bundle build for another platform switches the editor to it: the active platform goes last.
            var active = EditorUserBuildSettings.activeBuildTarget;
            foreach (var target in AllTargets.OrderBy(t => t == active))
            {
                if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
                {
                    Debug.Log($"[Mods] {target}: build support not installed, skipped.");
                    continue;
                }
                if (!BuildSelectedFor(target, false)) break;
            }
            EditorUtility.RevealInFinder(Path.Combine("Builds", "Mods"));
        }

        [MenuItem(BuildSelectedPath, true)]
        [MenuItem(BuildAllPath, true)]
        static bool BuildSelectedValidate() => Selection.objects.OfType<ContentPack>().Any();

        static bool BuildSelectedFor(BuildTarget target, bool reveal)
        {
            var packs = Selection.objects.OfType<ContentPack>().ToList();
            if (packs.Count == 0)
            {
                EditorUtility.DisplayDialog("Build mods", "Select one or more ContentPack assets in the Project window.", "OK");
                return false;
            }
            var outDir = ModBundleBuilder.Build(packs, target, loose: EditorPrefs.GetBool(LoosePrefKey, false));
            if (outDir != null && reveal) EditorUtility.RevealInFinder(outDir);
            return outDir != null;
        }

        [MenuItem(LoosePath, priority = 22)]
        static void ToggleLoose() => EditorPrefs.SetBool(LoosePrefKey, !EditorPrefs.GetBool(LoosePrefKey, false));

        [MenuItem(LoosePath, true)]
        static bool ToggleLooseValidate()
        {
            Menu.SetChecked(LoosePath, EditorPrefs.GetBool(LoosePrefKey, false));
            return true;
        }

        [MenuItem(MenuRoot + "Open Mods Folder", priority = 30)]
        static void OpenModsFolder()
        {
            var dir = ModLoader.UserFolder;
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        [MenuItem(MenuRoot + "Create New Content Pack", priority = 0)]
        static void CreatePack()
        {
            var path = EditorUtility.SaveFilePanelInProject("New content pack", "MyMod", "asset", "Content pack asset", "Assets/Mods");
            if (string.IsNullOrEmpty(path)) return;
            Selection.activeObject = ContentPackAssets.Create(path, Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
        }
    }
}
