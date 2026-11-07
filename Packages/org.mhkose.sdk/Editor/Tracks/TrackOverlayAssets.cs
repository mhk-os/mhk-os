using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Editor.Modding.Import;
using MHK.Modding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Creates and finds <see cref="TrackOverlayDefinition"/>s and their overlay scenes. Naming: the definition
    /// '&lt;track id&gt;.&lt;option short name&gt;' in '&lt;track folder&gt;/Variants/', the scene '&lt;track scene&gt;.&lt;option short
    /// name&gt;.unity' next to the track scene.
    /// </summary>
    public static class TrackOverlayAssets
    {
        /// <summary>Folder of a track's overlay definitions, inside the track folder.</summary>
        public const string VariantsFolder = "Variants";

        /// <summary>Short name of an option: its id without the pack namespace ('core.rain' gives 'rain').</summary>
        public static string ShortName(string optionId) => ExportJson.ShortName(optionId);

        /// <summary>Id of the overlay that makes <paramref name="track"/> offer <paramref name="optionId"/>.</summary>
        public static string Id(TrackDefinition track, string optionId) => track.Id + "." + ShortName(optionId);

        /// <summary>Folder of a track's files: the folder of its scene, else of its definition.</summary>
        public static string TrackFolder(TrackDefinition track) =>
            track.scene.IsSet ? AssetPaths.DirectoryOf(track.scene.Path) : AssetPaths.DirectoryOf(track);

        /// <summary>Asset path of the overlay definition <paramref name="id"/> of the track in <paramref name="trackFolder"/>.</summary>
        public static string DefinitionPath(string trackFolder, string id) => $"{trackFolder}/{VariantsFolder}/{AssetPaths.SafeFileName(id)}.asset";

        /// <summary>Path of the overlay scene of <paramref name="optionId"/>: '&lt;track scene&gt;.&lt;short name&gt;.unity' next to it.</summary>
        public static string ScenePath(string trackScenePath, string optionId) =>
            $"{AssetPaths.DirectoryOf(trackScenePath)}/{Path.GetFileNameWithoutExtension(trackScenePath)}.{AssetPaths.SafeFileName(ShortName(optionId))}.unity";

        /// <summary>Every overlay definition asset in the project for <paramref name="track"/>.</summary>
        public static List<TrackOverlayDefinition> ForTrack(TrackDefinition track) =>
            EditorAssets.FindAll<TrackOverlayDefinition>().Where(o => string.Equals(o.trackId, track.Id, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>Every track option definition asset in the project, by modifier and option order.</summary>
        public static List<TrackOptionDefinition> AllOptions()
        {
            var orders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in EditorAssets.FindAll<TrackModifierDefinition>()) orders.TryAdd(m.Id, m.order);
            return EditorAssets.FindAll<TrackOptionDefinition>().GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(o => o.modifierId != null && orders.TryGetValue(o.modifierId, out var order) ? order : int.MaxValue)
                .ThenBy(o => o.modifierId, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.order).ToList();
        }

        /// <summary>The modifier definition asset <paramref name="modifierId"/> in the project, null when there is none.</summary>
        public static TrackModifierDefinition Modifier(string modifierId) =>
            EditorAssets.FindAll<TrackModifierDefinition>().FirstOrDefault(m => string.Equals(m.Id, modifierId, StringComparison.OrdinalIgnoreCase));

        /// <summary>The option definition asset <paramref name="optionId"/> in the project, null when there is none.</summary>
        public static TrackOptionDefinition Option(string optionId) =>
            EditorAssets.FindAll<TrackOptionDefinition>().FirstOrDefault(o => string.Equals(o.Id, optionId, StringComparison.OrdinalIgnoreCase));

        /// <summary>The pack in the project whose content lists <paramref name="def"/>, null when none does.</summary>
        public static ContentPack PackOf(ContentDefinition def) =>
            EditorAssets.FindAll<ContentPack>().FirstOrDefault(p => p.content.Contains(def));

        /// <summary>
        /// The overlay definition making <paramref name="track"/> offer <paramref name="option"/>: an existing one in the
        /// folder of <paramref name="pack"/> (else of <paramref name="trackFolder"/>), else a new asset at
        /// <see cref="DefinitionPath"/> under <paramref name="trackFolder"/>. Sets its identity, track, option and default flag
        /// and adds it to <paramref name="pack"/> (when given).
        /// </summary>
        public static TrackOverlayDefinition CreateOrUpdate(TrackDefinition track, TrackOptionDefinition option, bool isDefault, string trackFolder, ContentPack pack)
        {
            var id = Id(track, option.Id);
            var scope = (pack != null ? AssetPaths.DirectoryOf(pack) : trackFolder) + "/";
            var def = ForTrack(track).FirstOrDefault(o => string.Equals(o.optionId, option.Id, StringComparison.OrdinalIgnoreCase)
                                                        && AssetDatabase.GetAssetPath(o).StartsWith(scope, StringComparison.Ordinal))
                      ?? AssetPaths.LoadOrCreate<TrackOverlayDefinition>(DefinitionPath(trackFolder, id));
            ImportAssets.SetIdentity(def, string.IsNullOrEmpty(def.trackId) ? id : def.Id, $"{track.DisplayName}: {option.DisplayName}", track.SourceGame);
            def.trackId = track.Id;
            def.optionId = option.Id;
            def.isDefault = isDefault;
            EditorUtility.SetDirty(def);
            if (pack != null) ImportAssets.AddToPack(pack, def);
            return def;
        }

        /// <summary>
        /// Opens the overlay scene of <paramref name="def"/> additively, created when missing: a definition without a scene
        /// gets '&lt;track scene&gt;.&lt;option short name&gt;.unity' next to the loaded track scene <paramref name="trackScene"/>
        /// (next to the definition when that lies outside the track's folder: an overlay for another mod's track). Gives it
        /// the overlay layout (<see cref="TrackSceneLayout.EnsureOverlayLayout"/>). A new scene gets the track scene's ambient
        /// light, fog and skybox, so the track looks as in a race while the overlay is the active scene; a race never uses
        /// an overlay scene's own lighting settings. A new scene is saved once, the scene is left loaded.
        /// </summary>
        public static Scene OpenOrCreateScene(TrackOverlayDefinition def, Scene trackScene)
        {
            var defaultPath = ScenePath(trackScene.path, def.optionId);
            var defFolder = AssetPaths.DirectoryOf(def);
            var path = def.scene.IsSet ? def.scene.Path
                : defFolder.StartsWith(AssetPaths.DirectoryOf(trackScene.path) + "/", StringComparison.Ordinal) ? defaultPath
                : $"{defFolder}/{Path.GetFileName(defaultPath)}";
            var scene = SceneManager.GetSceneByPath(path);
            if (!scene.isLoaded)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                else scene = CreateScene(path, trackScene);
            }
            TrackSceneLayout.EnsureOverlayLayout(scene);
            if (!def.scene.IsSet || def.scene.Path != path)
            {
                TrackScenes.AssignScene(def, "scene", path);
                EditorUtility.SetDirty(def);
            }
            return scene;
        }

        /// <summary>
        /// Takes the overlay scene away from <paramref name="def"/>: unassigns it and deletes the scene it named and a scene at
        /// the default <see cref="ScenePath"/> next to <paramref name="trackScenePath"/>, each only when it lies inside the
        /// folder <paramref name="scope"/>. Returns the deleted paths.
        /// </summary>
        public static List<string> RemoveScene(TrackOverlayDefinition def, string trackScenePath, string scope)
        {
            var paths = new List<string> { ScenePath(trackScenePath, def.optionId) };
            if (def.scene.IsSet)
            {
                paths.Add(def.scene.Path);
                TrackScenes.AssignScene(def, "scene", "");
                EditorUtility.SetDirty(def);
            }
            var deleted = new List<string>();
            foreach (var path in paths.Distinct())
                if (DeleteScene(path, scope)) deleted.Add(path);
            return deleted;
        }

        /// <summary>
        /// Deletes the scene at <paramref name="path"/> (closed first when loaded) when it exists inside the folder
        /// <paramref name="scope"/>. Returns whether it did.
        /// </summary>
        public static bool DeleteScene(string path, string scope)
        {
            if (!path.StartsWith(scope + "/", StringComparison.Ordinal) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) return false;
            var loaded = SceneManager.GetSceneByPath(path);
            if (loaded.isLoaded) EditorSceneManager.CloseScene(loaded, true);
            return AssetDatabase.DeleteAsset(path);
        }

        /// <summary>
        /// Deletes <paramref name="def"/>: removes it from the content of <paramref name="pack"/>, deletes its overlay scenes
        /// inside the pack's folder (<see cref="RemoveScene"/>) and the definition asset.
        /// </summary>
        public static void Delete(TrackOverlayDefinition def, ContentPack pack, string trackScenePath)
        {
            RemoveScene(def, trackScenePath, AssetPaths.DirectoryOf(pack));
            if (pack.content.Remove(def)) EditorUtility.SetDirty(pack);
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(def));
        }

        static Scene CreateScene(string path, Scene trackScene)
        {
            var look = SceneLook.Of(trackScene);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            look.ApplyTo(scene);
            TrackSceneLayout.EnsureOverlayLayout(scene);
            AssetPaths.EnsureFolder(AssetPaths.DirectoryOf(path));
            if (!EditorSceneManager.SaveScene(scene, path)) throw new IOException("cannot save " + path);
            return scene;
        }
    }
}
