using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Audio import settings per clip kind: music streams, long ambience loops stay compressed in memory, short effects
    /// decompress on load. Everything except music is preloaded so the first play does not hitch.
    /// </summary>
    public static class AudioImportRules
    {
        /// <summary>How a clip is used, which decides its load type.</summary>
        public enum Kind
        {
            Effect,
            Ambience,
            Music,
        }

        /// <summary>Non-music clips at least this long (s) count as <see cref="Kind.Ambience"/>.</summary>
        public const float AmbienceMinSeconds = 5f;

        [MenuItem("MHK/Content/Apply Audio Import Rules")]
        static void ApplyAllMenu()
        {
            var changed = ApplyAll();
            Debug.Log($"[AudioImportRules] {changed} clips reimported");
        }

        /// <summary>
        /// Applies the rules to every clip under <paramref name="folders"/> (default: the whole project). A clip that
        /// already streams counts as music. Returns the number of reimported clips.
        /// </summary>
        public static int ApplyAll(params string[] folders) =>
            EditorAssets.FindPaths("t:AudioClip", folders)
                .Select(path => AssetImporter.GetAtPath(path) as AudioImporter)
                .Count(importer => importer != null && Apply(importer, KindOf(importer)));

        /// <summary>Kind of an imported clip: streaming means music, otherwise its length decides.</summary>
        public static Kind KindOf(AudioImporter importer)
        {
            if (importer.defaultSampleSettings.loadType == AudioClipLoadType.Streaming) return Kind.Music;
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(importer.assetPath);
            return clip != null && clip.length >= AmbienceMinSeconds ? Kind.Ambience : Kind.Effect;
        }

        /// <summary>Sets the load type and preload flag of <paramref name="kind"/> and reimports when anything changed.</summary>
        public static bool Apply(AudioImporter importer, Kind kind)
        {
            var settings = importer.defaultSampleSettings;
            var loadType = kind switch
            {
                Kind.Music => AudioClipLoadType.Streaming,
                Kind.Ambience => AudioClipLoadType.CompressedInMemory,
                _ => AudioClipLoadType.DecompressOnLoad,
            };
            var preload = kind != Kind.Music;
            var background = kind == Kind.Music;
            if (settings.loadType == loadType && settings.preloadAudioData == preload && importer.loadInBackground == background) return false;
            settings.loadType = loadType;
            settings.preloadAudioData = preload;
            importer.defaultSampleSettings = settings;
            importer.loadInBackground = background;
            importer.SaveAndReimport();
            return true;
        }
    }
}
