using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Orders scene objects by their hierarchy path (scene, then names and sibling indices from the root), which is the
    /// same on every client that loaded the same track: online ids of item boxes and bridges are indexes in this order.
    /// Builds strings: call it once per race load.
    /// </summary>
    public static class StableOrder
    {
        /// <summary>Fills <paramref name="into"/> with every <typeparamref name="T"/> in the loaded scenes (inactive ones too), in path order.</summary>
        public static void Collect<T>(List<T> into) where T : Component
        {
            into.Clear();
            into.AddRange(UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include));
            var paths = new Dictionary<T, string>(into.Count);
            var b = new StringBuilder();
            foreach (var c in into) paths[c] = PathOf(c.transform, b);
            into.Sort((x, y) => string.CompareOrdinal(paths[x], paths[y]));
        }

        static string PathOf(Transform t, StringBuilder b)
        {
            b.Clear();
            Append(t, b);
            return b.Insert(0, t.gameObject.scene.name).ToString();
        }

        static void Append(Transform t, StringBuilder b)
        {
            if (t.parent != null) Append(t.parent, b);
            b.Append('/').Append(t.name).Append('#').Append(t.GetSiblingIndex().ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
