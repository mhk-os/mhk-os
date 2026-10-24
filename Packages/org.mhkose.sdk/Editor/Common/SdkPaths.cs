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
        /// <summary>The shared item box: the look comes from the track's pack (ContentPack.itemBoxLook), else its default look.</summary>
        public const string ItemBoxPrefab = Root + "/Prefabs/ItemBox/ItemBox.prefab";
        /// <summary>Folder (in <see cref="ResolvedRoot"/>, not imported) with the settings a mod project copies from the game.</summary>
        public const string ProjectSettingsKitFolder = "ProjectSettings~";

        internal static PackageInfo Info => PackageInfo.FindForAssembly(typeof(SdkPaths).Assembly);

        /// <summary>Folder of the package on disk, null while the SDK code is not inside the package.</summary>
        public static string ResolvedRoot => Info?.resolvedPath;

        /// <summary>Whether this project is the game: it embeds the SDK, mod projects reference it.</summary>
        public static bool IsGameProject => Info?.source == PackageSource.Embedded;

        /// <summary>The version field of the SDK's package.json: package metadata the Package Manager requires, never shown or bumped.</summary>
        public static string PackageVersion => Info?.version ?? "";
    }
}
