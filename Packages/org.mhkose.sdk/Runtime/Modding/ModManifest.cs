using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// mod.json of a mod release (and of a mod folder the game installed): pack id, texts, mod and SDK commit, the SDK
    /// build number and mod API level (<see cref="ModSdk.ApiLevel"/>, 0 when missing) the mod was built with and the
    /// zip asset of each platform.
    /// </summary>
    [Serializable]
    sealed class ModManifest
    {
        public string packId, title, description, commit, sdkCommit;
        public int sdkBuild, sdkApi;
        public ModTargets targets;

        static readonly Regex PackIdPattern = new("^[a-z0-9][a-z0-9_.-]*$", RegexOptions.CultureInvariant);

        /// <summary>The manifest in <paramref name="json"/>, null when it is unreadable or has no usable pack id or commit.</summary>
        public static ModManifest Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            ModManifest m;
            try
            {
                m = JsonUtility.FromJson<ModManifest>(json);
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (m == null || string.IsNullOrEmpty(m.commit) || m.packId == null) return null;
            return PackIdPattern.IsMatch(m.packId) && !m.packId.Contains("..") ? m : null;
        }
    }

    /// <summary>Zip asset name per BuildTarget, null where the release has none.</summary>
    [Serializable]
    sealed class ModTargets
    {
        public string StandaloneOSX, StandaloneWindows64, StandaloneLinux64, Android, iOS;

        public string For(string buildTarget) => buildTarget switch
        {
            nameof(StandaloneOSX) => StandaloneOSX,
            nameof(StandaloneWindows64) => StandaloneWindows64,
            nameof(StandaloneLinux64) => StandaloneLinux64,
            nameof(Android) => Android,
            nameof(iOS) => iOS,
            _ => null,
        };
    }

    /// <summary>
    /// pack.json of an asset pack (in its folder, and optionally as a release asset so the game knows the pack before the
    /// download): id, texts and an optional commit that, with the release tag, identifies the release.
    /// </summary>
    [Serializable]
    sealed class PackManifest
    {
        public string id, title, description, author, commit;

        static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

        /// <summary>The pack.json in <paramref name="json"/>, null when it is unreadable or its id cannot be a folder name.</summary>
        public static PackManifest Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            PackManifest m;
            try
            {
                m = JsonUtility.FromJson<PackManifest>(json);
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (m == null || m.id == null) return null;
            m.id = m.id.Trim();
            if (string.IsNullOrEmpty(m.commit)) m.commit = null;
            return IdPattern.IsMatch(m.id) && !m.id.Contains("..") ? m : null;
        }
    }

    /// <summary>source.json of a mod or asset pack folder the game installed.</summary>
    [Serializable]
    sealed class ModSourceRecord
    {
        public string repo, tag, installed;
    }
}
