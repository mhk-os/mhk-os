using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Placeholders for a loose desktop build: while open, the given textures import at <see cref="TextureSize"/> pixels
    /// and the sounds at the lowest rate and quality for the build target's platform. Their .meta files come back byte
    /// for byte on dispose (also after a failure, and after a crash through <see cref="BuildRestoreJournal"/>).
    /// </summary>
    public sealed class PlaceholderImportScope : IDisposable
    {
        /// <summary>Largest side of a placeholder texture (the smallest maxTextureSize Unity offers).</summary>
        public const int TextureSize = 32;
        /// <summary>Sample rate of a placeholder sound (the lowest Unity offers).</summary>
        public const int AudioSampleRate = 8000;
        /// <summary>Vorbis quality of a placeholder sound (the lowest Unity offers).</summary>
        public const float AudioQuality = 0.01f;

        // Nested scopes (a player build with its shipped mods) leave assets an outer scope already changed to it.
        static readonly HashSet<string> Active = new(StringComparer.Ordinal);

        readonly BuildRestoreJournal journal = new();
        readonly List<string> owned = new();

        /// <summary>Switches <paramref name="assetPaths"/> (replaceable textures and sounds) to placeholders for <paramref name="target"/>.</summary>
        public PlaceholderImportScope(IEnumerable<string> assetPaths, BuildTarget target)
        {
            var platform = BuildPipeline.GetBuildTargetGroup(target).ToString();
            var started = EditorApplication.timeSinceStartup;
            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var path in assetPaths) Apply(path, platform);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            Debug.Log($"[Assets] {owned.Count} placeholders for {platform} imported in {EditorApplication.timeSinceStartup - started:0.0} s.");
        }

        void Apply(string path, string platform)
        {
            if (!Active.Add(path)) return;
            owned.Add(path);
            journal.Save(path + ".meta");
            switch (AssetImporter.GetAtPath(path))
            {
                case TextureImporter t:
                    t.SetPlatformTextureSettings(TextureSettings(t, platform));
                    t.SaveAndReimport();
                    break;
                case AudioImporter a:
                    var audio = a.GetOverrideSampleSettings(platform);
                    audio.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                    audio.sampleRateOverride = AudioSampleRate;
                    audio.compressionFormat = AudioCompressionFormat.Vorbis;
                    audio.quality = AudioQuality;
                    a.SetOverrideSampleSettings(platform, audio);
                    a.forceToMono = true;
                    a.SaveAndReimport();
                    break;
            }
        }

        // The runtime's in-place replacement compresses only into DXT1 and DXT5 targets, other compressed formats end up
        // uncompressed. So a compressed texture's placeholder is DXT5 with alpha, DXT1 without, uncompressed stays as it is.
        static TextureImporterPlatformSettings TextureSettings(TextureImporter t, string platform)
        {
            var s = t.GetPlatformTextureSettings(platform);
            var compressed = s.overridden && s.format != TextureImporterFormat.Automatic
                ? (int)s.format >= 0 && GraphicsFormatUtility.IsCompressedFormat((TextureFormat)s.format)
                : (s.overridden ? s.textureCompression : t.textureCompression) != TextureImporterCompression.Uncompressed;
            if (compressed)
            {
                var alpha = t.alphaSource == TextureImporterAlphaSource.FromGrayScale ||
                            t.alphaSource == TextureImporterAlphaSource.FromInput && t.DoesSourceTextureHaveAlpha();
                s.format = alpha ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1;
                s.crunchedCompression = false;
            }
            s.overridden = true;
            s.maxTextureSize = TextureSize;
            return s;
        }

        /// <summary>Restores the import settings and reimports the assets.</summary>
        public void Dispose()
        {
            var started = EditorApplication.timeSinceStartup;
            journal.Dispose();
            foreach (var path in owned) Active.Remove(path);
            Debug.Log($"[Assets] {owned.Count} import settings restored in {EditorApplication.timeSinceStartup - started:0.0} s.");
            owned.Clear();
        }
    }
}
