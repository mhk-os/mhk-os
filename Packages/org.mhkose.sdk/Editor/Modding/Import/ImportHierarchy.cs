using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>Scene hierarchy helpers of the export import.</summary>
    public static class ImportHierarchy
    {
        /// <summary>First transform named <paramref name="name"/> in <paramref name="root"/>'s hierarchy, root included.</summary>
        public static Transform FindDeep(Transform root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>First descendant named <paramref name="name"/>, depth first, root excluded.</summary>
        public static Transform FindBelow(Transform root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Transform child in root)
            {
                var t = child.name == name ? child : FindBelow(child, name);
                if (t != null) return t;
            }
            return null;
        }

        public static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        public static Transform CreateChild(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        public static void StripAnimation(GameObject go)
        {
            foreach (var a in go.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(a);
        }

        public static T GetOrAddComponent<T>(GameObject go) where T : Component =>
            go.TryGetComponent(out T component) ? component : go.AddComponent<T>();
    }
}
