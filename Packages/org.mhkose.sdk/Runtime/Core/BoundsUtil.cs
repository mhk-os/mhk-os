using UnityEngine;

namespace MHK.Core
{
    /// <summary>Bounds corners and the model space of mesh renderers.</summary>
    public static class BoundsUtil
    {
        /// <summary>Corner <paramref name="i"/> (0..7) of <paramref name="b"/>: bit 0 = +x, bit 1 = +y, bit 2 = +z.</summary>
        public static Vector3 Corner(Bounds b, int i) =>
            b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));

        /// <summary>
        /// Space of <see cref="Renderer.localBounds"/> for model renderers: the transform of a MeshRenderer, the root bone
        /// of a SkinnedMeshRenderer. False for particles, trails and other non-model renderers.
        /// </summary>
        public static bool ModelSpace(Renderer r, out Matrix4x4 space)
        {
            switch (r)
            {
                case MeshRenderer:
                    space = r.localToWorldMatrix;
                    return true;
                case SkinnedMeshRenderer skinned:
                    space = skinned.rootBone != null ? skinned.rootBone.localToWorldMatrix : r.localToWorldMatrix;
                    return true;
                default:
                    space = default;
                    return false;
            }
        }
    }
}
