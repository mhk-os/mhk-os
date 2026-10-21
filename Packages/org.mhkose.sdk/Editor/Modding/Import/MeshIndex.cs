using UnityEngine.Rendering;

namespace MHK.Editor.Modding.Import
{
    /// <summary>Index buffer format of generated meshes.</summary>
    public static class MeshIndex
    {
        public const int UInt16MaxVertices = 65535;

        /// <summary>16-bit indices when every vertex fits, else 32-bit.</summary>
        public static IndexFormat FormatFor(int vertexCount) => vertexCount > UInt16MaxVertices ? IndexFormat.UInt32 : IndexFormat.UInt16;
    }
}
