using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Lists the packs compiled into the game build (Resources/BuiltinContent): the game's own definitions such as the
    /// shared kart reactions. They always load first, cannot be disabled and are not listed in the Mods screen.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Builtin Content Catalog", fileName = "BuiltinContent")]
    public class BuiltinContentCatalog : ScriptableObject
    {
        public List<ContentPack> packs = new();

        /// <summary>The listed packs (nulls skipped).</summary>
        public IEnumerable<ContentPack> RootPacks()
        {
            foreach (var p in packs)
                if (p != null) yield return p;
        }

        /// <summary>Every listed pack and every pack it includes.</summary>
        public IEnumerable<ContentPack> AllPacks()
        {
            foreach (var root in RootPacks())
            foreach (var p in root.SelfAndIncluded())
                yield return p;
        }
    }
}
