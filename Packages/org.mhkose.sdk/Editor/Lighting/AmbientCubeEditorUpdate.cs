using MHK.Lighting;
using UnityEditor;

namespace MHK.Editor.Lighting
{
    /// <summary>
    /// Keeps <see cref="AmbientCube"/> current outside play mode, so baked (lightmapped) MHK/Lit surfaces get their
    /// ambient light in the Scene and Game views too. In play mode GraphicsQuality.Tick does it.
    /// </summary>
    [InitializeOnLoad]
    static class AmbientCubeEditorUpdate
    {
        static AmbientCubeEditorUpdate() => EditorApplication.update += () =>
        {
            if (!EditorApplication.isPlaying) AmbientCube.Update();
        };
    }
}
