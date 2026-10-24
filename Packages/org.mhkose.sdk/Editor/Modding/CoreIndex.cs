using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// The game's and the base content's packs and definitions (ids, types, GUIDs): <see cref="SdkPaths.CoreIndex"/>
    /// (the game and its core, written by the game project with the core references) plus the index of every reference
    /// package of a base mod (<see cref="PackReferences"/>). The validator and the dependency report read it.
    /// </summary>
    [Serializable]
    public class CoreIndex
    {
        /// <summary>One pack: its id, the top-level pack it belongs to and whether it is compiled into the game.</summary>
        [Serializable]
        public class Pack
        {
            public string packId;
            public string root;
            public bool builtin;
        }

        /// <summary>One definition and the asset that holds it.</summary>
        [Serializable]
        public class Definition
        {
            public string id;
            public string type;
            public string pack;
            public string guid;
            public long fileId;
            public string displayName;
        }

        public List<Pack> packs = new();
        public List<Definition> definitions = new();

        /// <summary>The SDK's index merged with the indexes of the SDK's reference packages, empty when none is readable.</summary>
        public static CoreIndex Load()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SdkPaths.CoreIndex);
            var index = text != null ? Parse(text.text, SdkPaths.CoreIndex) : new CoreIndex();
            var root = SdkPaths.ResolvedRoot;
            var refs = root != null ? Path.Combine(root, PackReferences.FolderName) : null;
            if (refs == null || !Directory.Exists(refs)) return index;
            foreach (var dir in Directory.GetDirectories(refs))
            {
                var file = Path.Combine(dir, PackReferences.IndexFile);
                if (!File.Exists(file)) continue;
                var part = Parse(File.ReadAllText(file), file);
                index.packs.AddRange(part.packs);
                index.definitions.AddRange(part.definitions);
            }
            return index;
        }

        static CoreIndex Parse(string json, string source)
        {
            try
            {
                return JsonUtility.FromJson<CoreIndex>(json) ?? new CoreIndex();
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"[Mods] {source} cannot be read: {e.Message}");
                return new CoreIndex();
            }
        }

        /// <summary>Adds <paramref name="def"/> of <paramref name="pack"/> with the GUID and fileID of its asset.</summary>
        public void Add(ContentPack pack, ContentDefinition def)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(def, out var guid, out long fileId);
            definitions.Add(new Definition { id = def.Id, type = def.GetType().FullName, pack = pack.packId, guid = guid, fileId = fileId, displayName = def.DisplayName });
        }

        /// <summary>Pack ids of the index, without the packs of the top-level pack <paramref name="exceptRoot"/>.</summary>
        public IEnumerable<string> PackIds(string exceptRoot) => packs.Where(p => p.root != exceptRoot).Select(p => p.packId);

        /// <summary>Definition ids of the index, without those of the top-level pack <paramref name="exceptRoot"/>.</summary>
        public IEnumerable<string> Ids(string exceptRoot)
        {
            var excluded = new HashSet<string>(packs.Where(p => p.root == exceptRoot).Select(p => p.packId));
            return definitions.Where(d => !excluded.Contains(d.pack)).Select(d => d.id);
        }

        /// <summary>Whether the asset <paramref name="guid"/> holds a definition of the index.</summary>
        public bool HasGuid(string guid) => definitions.Any(d => d.guid == guid);
    }
}
