using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Editor.Content;
using MHK.Modding;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Builds a kart that differs from an existing one only by textures (another paint or number of the same model):
    /// a prefab variant with swapped materials that shares the base kart's meshes, clips and physics profile.
    /// </summary>
    public static class KartVariantBuilder
    {
        /// <summary>One texture swap: every material that samples <see cref="baseTexture"/> gets <see cref="sourceFile"/>.</summary>
        public readonly struct TextureSwap
        {
            public readonly Texture2D baseTexture;
            public readonly string sourceFile;

            public TextureSwap(Texture2D baseTexture, string sourceFile)
            {
                this.baseTexture = baseTexture;
                this.sourceFile = sourceFile;
            }
        }

        /// <summary>
        /// Creates or updates the kart <paramref name="id"/> in <paramref name="pack"/> ("&lt;pack&gt;/Karts/&lt;id&gt;/"):
        /// the swap textures are copied in with the base texture's import settings, the base kart's materials that use
        /// them are cloned, and the definition copies the base kart's physics, animations, ratings and sound.
        /// </summary>
        public static KartDefinition Create(ContentPack pack, KartDefinition baseKart, string id, string displayName,
                                            IReadOnlyList<TextureSwap> swaps)
        {
            var folder = $"{ImportAssets.PackFolder(pack)}/Karts/{AssetPaths.SafeFileName(id.Replace('.', '_'))}";
            AssetPaths.EnsureFolder(folder);
            var textures = swaps.ToDictionary(s => s.baseTexture, s => ImportTexture(s, folder));
            var materials = CloneMaterials(baseKart.kartPrefab, textures, folder);
            var prefab = SaveVariant(baseKart.kartPrefab, materials, $"{folder}/{id}.prefab", id);
            var def = AssetPaths.LoadOrCreate<KartDefinition>($"{folder}/{Path.GetFileName(folder)}.asset");
            ImportAssets.SetIdentity(def, id, displayName, baseKart.SourceGame);
            CopyKartFields(baseKart, def);
            def.kartPrefab = prefab;
            EditorUtility.SetDirty(def);
            ImportAssets.AddToPack(pack, def);
            AssetDatabase.SaveAssets();
            return def;
        }

        static Texture2D ImportTexture(TextureSwap swap, string folder)
        {
            var path = ImportAssets.CopyIn(swap.sourceFile, $"{folder}/Textures/{Path.GetFileName(swap.sourceFile).ToLowerInvariant()}");
            var baseImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(swap.baseTexture));
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var settings = new TextureImporterSettings();
            baseImporter.ReadTextureSettings(settings);
            importer.SetTextureSettings(settings);
            importer.SetPlatformTextureSettings(baseImporter.GetDefaultPlatformTextureSettings());
            foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
                importer.SetPlatformTextureSettings(baseImporter.GetPlatformTextureSettings(platform));
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Dictionary<Material, Material> CloneMaterials(GameObject prefab, Dictionary<Texture2D, Texture2D> textures, string folder)
        {
            var map = new Dictionary<Material, Material>();
            foreach (var mat in prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Distinct())
            {
                if (mat == null) continue;
                var props = mat.GetTexturePropertyNames().Where(p => mat.GetTexture(p) is Texture2D t && textures.ContainsKey(t)).ToList();
                if (props.Count == 0) continue;
                var path = $"{folder}/Materials/{mat.name}.mat";
                AssetPaths.EnsureFolder(AssetPaths.DirectoryOf(path));
                var clone = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (clone == null)
                {
                    clone = new Material(mat);
                    AssetDatabase.CreateAsset(clone, path);
                }
                else clone.CopyPropertiesFromMaterial(mat);
                foreach (var p in props) clone.SetTexture(p, textures[(Texture2D)mat.GetTexture(p)]);
                EditorUtility.SetDirty(clone);
                map[mat] = clone;
            }
            return map;
        }

        static GameObject SaveVariant(GameObject basePrefab, Dictionary<Material, Material> materials, string path, string name)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                instance.name = name;
                foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    var changed = false;
                    for (var i = 0; i < mats.Length; i++)
                        if (mats[i] != null && materials.TryGetValue(mats[i], out var swap)) { mats[i] = swap; changed = true; }
                    if (changed) r.sharedMaterials = mats;
                }
                return PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>Copies the gameplay, menu and sound fields of <paramref name="from"/> (not identity, prefab or icon).</summary>
        public static void CopyKartFields(KartDefinition from, KartDefinition to)
        {
            to.physics = from.physics;
            to.modelScale = from.modelScale;
            to.animations = from.animations.Select(a => new CharacterAnimBinding { role = a.role, clip = a.clip, playback = a.playback, speed = a.speed }).ToList();
            to.speedRating = from.speedRating;
            to.accelerationRating = from.accelerationRating;
            to.handlingRating = from.handlingRating;
            to.engineLoop = from.engineLoop;
            to.enginePitchRange = from.enginePitchRange;
        }
    }
}
