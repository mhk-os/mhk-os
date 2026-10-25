using System.IO;
using System.Linq;
using MHK.Modding;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Writes the <see cref="TrackDefinition"/> of an imported track scene (laps, layout, variant, music, physics,
    /// gravity, the preview only while the track has none, so in-game captures stay) and adds it to the pack.
    /// Fields the import does not set are kept.
    /// </summary>
    public static class TrackDefinitionWriter
    {
        public static TrackDefinition Write(TrackImportContext c)
        {
            var s = c.Settings;
            var json = c.Json;
            var packId = s.pack.packId;
            var def = AssetPaths.LoadOrCreate<TrackDefinition>($"{c.Folder}/{c.BaseName}.asset");
            var (music, preview) = ImportMedia(c, def.previewImage == null);
            ImportAssets.SetIdentity(def, c.Id, ExportJson.DisplayName(json, c.ShortName), packId);
            SetScene(def, c.ScenePath);
            def.defaultLaps = Mathf.Max(1, ExportJson.I(json["laps"], s.defaultLaps));
            def.layout = LayoutOf(json);
            def.variant = ExportJson.Str(json["variant"]);
            if (music != null) def.music = music;
            if (preview != null) def.previewImage = preview;
            def.physics = JsonUtility.FromJson<TrackPhysicsSettings>(JsonUtility.ToJson(s.trackPhysics ?? new TrackPhysicsSettings()));
            if (json["gravity"] != null) def.gravity = ExportJson.F(json["gravity"]);
            EditorUtility.SetDirty(def);
            ImportAssets.AddToPack(s.pack, def);
            return def;
        }

        static void SetScene(TrackDefinition def, string scenePath)
        {
            var so = new SerializedObject(def);
            var scene = so.FindProperty("scene");
            scene.FindPropertyRelative("asset").objectReferenceValue = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            scene.FindPropertyRelative("path").stringValue = scenePath;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static TrackLayout LayoutOf(JObject json)
        {
            var modes = (json["modes"] as JArray ?? json["extras"]?["modes"] as JArray)?.Select(m => (string)m).ToList();
            return modes is { Count: > 0 } && modes.All(m => m is "lastmanstanding" or "battle") ? TrackLayout.Arena : TrackLayout.Circuit;
        }

        static (AudioClip music, Texture2D preview) ImportMedia(TrackImportContext c, bool importPreview)
        {
            var music = ExportJson.Str(c.Json["music"]);
            var clip = music != null ? ImportAssets.ImportAudio(Path.GetFullPath(Path.Combine(c.Dir, music)), $"{c.Folder}/{c.BaseName}_music", true) : null;
            var preview = importPreview
                ? ImportAssets.ImportEntryOrPattern(c.Dir, ExportJson.Str(c.Json["preview"]), c.Settings.previewPattern, c.Settings, c.Id,
                    $"{c.Folder}/{AssetPaths.SafeFileName(c.Id)}_preview")
                : null;
            return (clip, preview);
        }
    }
}
