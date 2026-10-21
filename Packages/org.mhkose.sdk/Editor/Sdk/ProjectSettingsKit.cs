using System.IO;
using System.Linq;
using MHK.Modding;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Sdk
{
    /// <summary>
    /// The game's project settings in the SDK (<see cref="SdkPaths.ProjectSettingsKitFolder"/>, not imported) and
    /// <b>MHK ▸ SDK ▸ Apply Project Settings</b>, which copies them into a mod project: layers, physics, quality levels,
    /// graphics, editor settings, the player settings bundles depend on and the URP assets with the game's GUIDs, so
    /// bundles get the same shader variants, texture formats and collision data.
    /// </summary>
    public static class ProjectSettingsKit
    {
        /// <summary>Files of ProjectSettings/ in the kit.</summary>
        public static readonly string[] ProjectSettingsFiles =
        {
            "TagManager.asset", "DynamicsManager.asset", "QualitySettings.asset", "GraphicsSettings.asset", "EditorSettings.asset",
            "URPProjectSettings.asset", "ProjectVersion.txt",
        };

        /// <summary>Project folder of the URP assets and renderers (and the track bake settings), copied whole with their metas.</summary>
        public const string SettingsAssetFolder = "Assets/Settings";

        /// <summary>
        /// Player settings of the kit (<see cref="PlayerSettingsFile"/>, serialized PlayerSettings properties): colour
        /// space, input handling, prebaked collision meshes, skinning and the per-platform graphics APIs, lightmap, HDR
        /// cubemap and normal map encodings and texture formats. Names, ids and versions stay the mod project's own.
        /// </summary>
        public static readonly string[] PlayerSettingsKeys =
        {
            "m_ActiveColorSpace", "activeInputHandler", "bakeCollisionMeshes", "gpuSkinning", "meshDeformation", "openGLRequireES31",
            "m_BuildTargetGraphicsAPIs", "m_BuildTargetGroupLightmapEncodingQuality", "m_BuildTargetGroupHDRCubemapEncodingQuality",
            "m_BuildTargetNormalMapEncoding", "m_BuildTargetDefaultTextureCompressionFormat",
        };

        /// <summary>File of the kit with the values of <see cref="PlayerSettingsKeys"/>.</summary>
        public const string PlayerSettingsFile = "PlayerSettings.json";

        /// <summary>This project's values of <see cref="PlayerSettingsKeys"/> (what the kit stores).</summary>
        public static JObject ReadPlayerSettings()
        {
            var so = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton(nameof(PlayerSettings)));
            var values = new JObject();
            foreach (var key in PlayerSettingsKeys)
            {
                var p = so.FindProperty(key);
                if (p != null) values[key] = SerializedPropertyJson.ToJson(p);
            }
            return values;
        }

        const string ProjectSettingsFolder = "ProjectSettings";
        const string ProjectVersionFile = "ProjectVersion.txt";
        const string MenuPath = "MHK/SDK/Apply Project Settings";

        /// <summary>The kit folder on disk, null while the SDK code is not inside the package.</summary>
        public static string KitRoot => SdkPaths.ResolvedRoot == null ? null : Path.Combine(SdkPaths.ResolvedRoot, SdkPaths.ProjectSettingsKitFolder);

        /// <summary>Whether this project is the game (embedded SDK or the game's built-in catalog), whose settings are the source of the kit.</summary>
        public static bool IsGameProject => SdkPaths.IsGameProject || Resources.Load<BuiltinContentCatalog>(ModLoader.BuiltinCatalogResource) != null;

        [MenuItem(MenuPath, priority = 0)]
        static void ApplyMenu()
        {
            if (!EditorUtility.DisplayDialog("Apply project settings", "Replace this project's layers, physics, quality, graphics and editor " +
                                             "settings and the URP assets in Assets/Settings with the game's? Unity reopens the project afterwards.", "Apply", "Cancel"))
                return;
            if (Apply()) EditorApplication.OpenProject(Directory.GetCurrentDirectory());
        }

        [MenuItem(MenuPath, true)]
        static bool ApplyValidate() => !IsGameProject;

        /// <summary>
        /// The <c>-executeMethod MHK.Editor.Sdk.ProjectSettingsKit.ApplyFromCommandLine</c> entry: <see cref="Apply"/>, then in batch
        /// mode exits with a non-zero code on failure.
        /// </summary>
        public static void ApplyFromCommandLine()
        {
            var ok = Apply();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Copies the kit into this project. The project settings take effect when the project opens next. Refuses in the
        /// game project. Returns whether everything was copied.
        /// </summary>
        public static bool Apply()
        {
            if (IsGameProject)
            {
                Debug.LogError("[SDK] This is the game project: its settings are the source of the kit, nothing applied.");
                return false;
            }
            var kit = KitRoot;
            if (kit == null || !Directory.Exists(kit))
            {
                Debug.LogError($"[SDK] No project settings kit at {kit}.");
                return false;
            }
            CheckUnityVersion(kit);
            var assets = CopyFolder(Path.Combine(kit, SettingsAssetFolder), SettingsAssetFolder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var settings = 0;
            foreach (var file in ProjectSettingsFiles.Where(f => f != ProjectVersionFile))
            {
                var source = Path.Combine(kit, ProjectSettingsFolder, file);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(ProjectSettingsFolder, file), true);
                settings++;
            }
            var player = ApplyPlayerSettings(kit);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            AssetDatabase.SaveAssets();
            Debug.Log($"[SDK] Applied the game's project settings: {settings} ProjectSettings files, {assets} files in {SettingsAssetFolder}, " +
                      $"{(player ? PlayerSettingsKeys.Length + " player settings" : "no player settings")}, linear colour space. Reopen the project to load them.");
            return true;
        }

        static bool ApplyPlayerSettings(string kit)
        {
            var file = Path.Combine(kit, PlayerSettingsFile);
            if (!File.Exists(file)) return false;
            var values = JObject.Parse(File.ReadAllText(file));
            var so = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton(nameof(PlayerSettings)));
            foreach (var key in PlayerSettingsKeys)
            {
                var p = so.FindProperty(key);
                if (p != null && values.TryGetValue(key, out var value)) SerializedPropertyJson.FromJson(p, value);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        static void CheckUnityVersion(string kit)
        {
            var file = Path.Combine(kit, ProjectSettingsFolder, ProjectVersionFile);
            if (!File.Exists(file)) return;
            var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith("m_EditorVersion:"));
            var version = line?.Substring(line.IndexOf(':') + 1).Trim();
            if (version != null && version != Application.unityVersion)
                Debug.LogWarning($"[SDK] The game uses Unity {version}, this editor is {Application.unityVersion}: mods must be built with {version}.");
        }

        static int CopyFolder(string from, string to)
        {
            if (!Directory.Exists(from)) return 0;
            Directory.CreateDirectory(to);
            var count = 0;
            foreach (var file in Directory.GetFiles(from).Where(f => !Path.GetFileName(f).StartsWith(".")))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
                count++;
            }
            return count;
        }
    }
}
