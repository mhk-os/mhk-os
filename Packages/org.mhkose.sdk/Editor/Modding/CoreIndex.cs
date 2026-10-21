using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// The game's and the base content's packs and definitions (ids, types, GUIDs) as <see cref="SdkPaths.CoreIndex"/>,
    /// written by the game project with the core references. Mod projects have no base content, the validator and the
    /// dependency report read it instead.
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

        /// <summary>The SDK's index, empty when it is missing or unreadable.</summary>
        public static CoreIndex Load()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SdkPaths.CoreIndex);
            if (text == null) return new CoreIndex();
            try
            {
                return JsonUtility.FromJson<CoreIndex>(text.text) ?? new CoreIndex();
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"[Mods] {SdkPaths.CoreIndex} cannot be read: {e.Message}");
                return new CoreIndex();
            }
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
