using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Creates mod <see cref="ContentPack"/> assets and sorts the project's packs: the game's own (built-in catalog and
    /// the SDK's packs), the shipped mods (base content under <see cref="ShippedFolder"/>) and the project's other mods.
    /// </summary>
    public static class ContentPackAssets
    {
        /// <summary>Load order of new mod packs (after the built-in packs).</summary>
        public const int DefaultModLoadOrder = 100;

        /// <summary>Top-level packs in this folder ship with the game as mods (player builds put them into StreamingAssets/Mods).</summary>
        public const string ShippedFolder = "Assets/Content";

        /// <summary>A new pack asset at <paramref name="path"/> titled after its file name.</summary>
        public static ContentPack Create(string path, string packId)
        {
            AssetPaths.EnsureFolder(AssetPaths.DirectoryOf(path));
            var pack = ScriptableObject.CreateInstance<ContentPack>();
            pack.packId = packId;
            pack.title = Path.GetFileNameWithoutExtension(path);
            pack.loadOrder = DefaultModLoadOrder;
            AssetDatabase.CreateAsset(pack, path);
            return pack;
        }

        /// <summary>
        /// The game's own packs and the packs they include: those of the built-in catalog (Resources/BuiltinContent, game
        /// project only) and the packs inside the SDK package (the shared reactions, also in mod projects).
        /// </summary>
        public static List<ContentPack> BuiltinPacks()
        {
            var catalog = Resources.Load<BuiltinContentCatalog>(ModLoader.BuiltinCatalogResource);
            var roots = catalog != null ? catalog.RootPacks() : Enumerable.Empty<ContentPack>();
            return roots.Concat(EditorAssets.FindAll<ContentPack>(SdkPaths.Root)).SelectMany(p => p.SelfAndIncluded()).Distinct().ToList();
        }

        /// <summary>The mods that ship with the game: top-level packs under <see cref="ShippedFolder"/> (the base content 'core', none in mod projects).</summary>
        public static List<ContentPack> ShippedPacks() =>
            AssetDatabase.IsValidFolder(ShippedFolder)
                ? TopLevel(EditorAssets.FindAll<ContentPack>(ShippedFolder).Except(BuiltinPacks()))
                : new List<ContentPack>();

        /// <summary>Top-level packs of <paramref name="packs"/> (those no other of them includes), by load order.</summary>
        public static List<ContentPack> TopLevel(IEnumerable<ContentPack> packs)
        {
            var list = packs.Where(p => p != null).Distinct().ToList();
            var included = new HashSet<ContentPack>(list.SelectMany(p => p.SelfAndIncluded().Where(x => x != p)));
            return list.Where(p => !included.Contains(p)).OrderBy(p => p.loadOrder).ToList();
        }
    }
}
