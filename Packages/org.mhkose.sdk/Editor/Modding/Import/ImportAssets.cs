using System;
using System.IO;
using MHK.Editor.Content;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>Copies export files (audio, pictures) into the project and registers definitions in their pack.</summary>
    public static class ImportAssets
    {
        const float MusicVorbisQuality = 0.6f;

        /// <summary>Folder of a pack asset.</summary>
        public static string PackFolder(ContentPack pack) => AssetPaths.DirectoryOf(pack);

        /// <summary>Copies an external file to <paramref name="dest"/> (when its content differs), imports it and returns the asset path.</summary>
        public static string CopyIn(string srcFile, string dest)
        {
            AssetPaths.EnsureFolder(AssetPaths.DirectoryOf(dest));
            var abs = Path.GetFullPath(dest);
            if (!File.Exists(abs) || new FileInfo(abs).Length != new FileInfo(srcFile).Length || !File.ReadAllBytes(abs).AsSpan().SequenceEqual(File.ReadAllBytes(srcFile)))
            {
                File.Copy(srcFile, abs, true);
                AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceUpdate);
            }
            return dest;
        }

        /// <summary>
        /// Copies an export audio file in with the <see cref="AudioImportRules"/> and returns the clip (null when missing).
        /// While nothing is at <paramref name="dest"/>, a clip of the same kind with the same bytes the pack already has
        /// is used instead (<see cref="PackImportScope"/>).
        /// </summary>
        public static AudioClip ImportAudio(string srcFile, string dest, bool music)
        {
            if (string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)) return null;
            var path = dest + Path.GetExtension(srcFile).ToLowerInvariant();
            var existing = File.Exists(Path.GetFullPath(path)) ? null
                : PackImportScope.Current?.FindFile(File.ReadAllBytes(srcFile), Path.GetFileName(path),
                    p => AssetImporter.GetAtPath(p) is AudioImporter other && (AudioImportRules.KindOf(other) == AudioImportRules.Kind.Music) == music);
            path = existing ?? CopyIn(srcFile, path);
            if (AssetImporter.GetAtPath(path) is AudioImporter ai)
            {
                if (music && ai.defaultSampleSettings.loadType != AudioClipLoadType.Streaming)
                {
                    var ss = ai.defaultSampleSettings;
                    ss.compressionFormat = AudioCompressionFormat.Vorbis;
                    ss.quality = MusicVorbisQuality;
                    ai.defaultSampleSettings = ss;
                }
                AudioImportRules.Apply(ai, music ? AudioImportRules.Kind.Music : AudioImportRules.KindOf(ai));
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        /// <summary>Copies an export picture in as a high-quality UI texture (no mipmaps) and returns it.</summary>
        public static Texture2D ImportPicture(string srcFile, string dest)
        {
            if (string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)) return null;
            var path = CopyIn(srcFile, dest + Path.GetExtension(srcFile).ToLowerInvariant());
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && (ti.mipmapEnabled || ti.textureCompression != TextureImporterCompression.CompressedHQ))
            {
                ti.mipmapEnabled = false;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.alphaIsTransparency = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// The picture named by a JSON <paramref name="entry"/> (relative to <paramref name="dir"/>), else by
        /// <paramref name="pattern"/> (relative to the export folder, {0} = the id without its pack). Null when neither
        /// names an existing file.
        /// </summary>
        public static Texture2D ImportEntryOrPattern(string dir, string entry, string pattern, ExportImportSettings s, string id, string dest)
        {
            var file = entry != null ? Path.Combine(dir, entry)
                : !string.IsNullOrEmpty(pattern) ? Path.Combine(s.exportFolder, string.Format(pattern, ExportJson.ShortName(id))) : null;
            return file != null ? ImportPicture(Path.GetFullPath(file), dest) : null;
        }

        /// <summary>
        /// Sets the serialized identity fields of a definition (id, display name, source game). A display name already
        /// set is kept, so a reimport keeps the English names given by hand.
        /// </summary>
        public static void SetIdentity(ContentDefinition def, string id, string displayName, string sourceGame)
        {
            var so = new SerializedObject(def);
            so.FindProperty("id").stringValue = id;
            var name = so.FindProperty("displayName");
            if (string.IsNullOrEmpty(name.stringValue)) name.stringValue = displayName;
            so.FindProperty("sourceGame").stringValue = sourceGame;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Adds <paramref name="def"/> to the pack's content once (the pack only changes when its list does).</summary>
        public static void AddToPack(ContentPack pack, ContentDefinition def)
        {
            var changed = pack.content.RemoveAll(c => c == null) > 0;
            if (!pack.content.Contains(def))
            {
                pack.content.Add(def);
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(pack);
        }
    }
}
