using UnityEditor.PackageManager;

namespace MHK.Editor
{
    /// <summary>
    /// Identity and asset paths of the SDK package (org.mhkose.sdk): the runtime, the editor tools and the shared base
    /// assets that the game and every mod project use.
    /// </summary>
    public static class SdkPaths
    {
        public const string PackageName = "org.mhkose.sdk";
        /// <summary>Asset path of the package root.</summary>
        public const string Root = "Packages/" + PackageName;
        public const string TrackLighting = Root + "/Settings/TrackLighting.lighting";
        /// <summary>Lighting settings of the scenes with <see cref="MHK.Lighting.BakedIndirectLighting"/>.</summary>
        public const string TrackLightingIndirect = Root + "/Settings/TrackLightingIndirect.lighting";
        public const string TrackGroundMaterial = Root + "/Settings/Physics/TrackGround.physicMaterial";
        public const string TrackWallMaterial = Root + "/Settings/Physics/TrackWall.physicMaterial";
        /// <summary>Ids, types and GUIDs of the game's and the base content's definitions (written by the game project).</summary>
        public const string CoreIndex = Root + "/CoreIndex/core-content.json";
        /// <summary>The game's item box with the looks of the original games. Only the game project has it.</summary>
        public const string GameItemBoxPrefab = "Assets/MHK/Prefabs/ItemBox.prefab";
        /// <summary>Folder (in <see cref="ResolvedRoot"/>, not imported) with the settings a mod project copies from the game.</summary>
        public const string ProjectSettingsKitFolder = "ProjectSettings~";

        static PackageInfo Info => PackageInfo.FindForAssembly(typeof(SdkPaths).Assembly);

        /// <summary>Folder of the package on disk, null while the SDK code is not inside the package.</summary>
        public static string ResolvedRoot => Info?.resolvedPath;

        /// <summary>Whether this project is the game: it embeds the SDK, mod projects reference it.</summary>
        public static bool IsGameProject => Info?.source == PackageSource.Embedded;
    }
}
