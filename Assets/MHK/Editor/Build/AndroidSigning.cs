using System.IO;
using MHK.Core;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Build
{
    /// <summary>
    /// Signs <see cref="CiBuild"/> Android builds with the release keystore given on the command line
    /// (<c>-androidKeystoreName</c>, <c>-androidKeystorePass</c>, <c>-androidKeyaliasName</c>, <c>-androidKeyaliasPass</c>:
    /// game-ci's unity-builder passes them from the ANDROID_* secrets and writes the keystore into the project folder).
    /// Android only installs an update signed with the same key, so a numbered build (<see cref="BuildStamp.BuildCodeOption"/>,
    /// the releases) does not start without the keystore instead of shipping a debug-signed APK that no later build can
    /// update. Without the options (local builds) Unity signs with its debug key.
    /// </summary>
    public static class AndroidSigning
    {
        const string KeystoreOption = "-androidKeystoreName";
        const string KeystorePassOption = "-androidKeystorePass";
        const string AliasOption = "-androidKeyaliasName";
        const string AliasPassOption = "-androidKeyaliasPass";

        static (bool custom, string keystore, string alias)? saved;

        /// <summary>Sets the keystore for the next build (undone by <see cref="Restore"/>); false when the build must not run.</summary>
        public static bool Apply()
        {
            Restore();
            var keystore = CommandLine.Value(KeystoreOption);
            if (string.IsNullOrEmpty(keystore))
            {
                if (!CommandLine.TryInt(BuildStamp.BuildCodeOption, out var code) || code <= 0) return true;
                Debug.LogError($"[Build] Release APKs need the release keystore ({KeystoreOption}, the ANDROID_* secrets).");
                return false;
            }
            var path = Path.GetFullPath(keystore);
            if (!File.Exists(path))
            {
                Debug.LogError($"[Build] Keystore {path} not found.");
                return false;
            }
            saved = (PlayerSettings.Android.useCustomKeystore, PlayerSettings.Android.keystoreName, PlayerSettings.Android.keyaliasName);
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = path;
            PlayerSettings.Android.keystorePass = CommandLine.Value(KeystorePassOption) ?? "";
            PlayerSettings.Android.keyaliasName = CommandLine.Value(AliasOption) ?? "";
            PlayerSettings.Android.keyaliasPass = CommandLine.Value(AliasPassOption) ?? "";
            Debug.Log($"[Build] Signing with {Path.GetFileName(path)}, key {PlayerSettings.Android.keyaliasName}.");
            return true;
        }

        /// <summary>Puts the project's own signing settings back.</summary>
        public static void Restore()
        {
            if (saved == null) return;
            var (custom, keystore, alias) = saved.Value;
            PlayerSettings.Android.useCustomKeystore = custom;
            PlayerSettings.Android.keystoreName = keystore;
            PlayerSettings.Android.keystorePass = "";
            PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keyaliasPass = "";
            saved = null;
        }
    }
}
