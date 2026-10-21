using System;
using System.Linq;
using UnityEditor;
using UnityEngine.Rendering;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Keeps every lightmap and fog shader variant while it is open (the automatic stripping only knows the scenes of the
    /// game build, not those of a mod bundle). Restores and saves the graphics settings on dispose.
    /// </summary>
    sealed class LightmapFogVariantScope : IDisposable
    {
        const int CustomStripping = 1;

        static readonly string[] KeepFlags =
        {
            "m_LightmapKeepPlain", "m_LightmapKeepDirCombined", "m_LightmapKeepDynamicPlain", "m_LightmapKeepDynamicDirCombined",
            "m_LightmapKeepShadowMask", "m_LightmapKeepSubtractive", "m_FogKeepLinear", "m_FogKeepExp", "m_FogKeepExp2",
        };

        readonly SerializedObject graphics;
        readonly SerializedProperty lightmapStripping, fogStripping;
        readonly SerializedProperty[] keep;
        readonly int storedLightmapStripping, storedFogStripping;
        readonly bool[] storedKeep;

        public LightmapFogVariantScope()
        {
            graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            lightmapStripping = graphics.FindProperty("m_LightmapStripping");
            fogStripping = graphics.FindProperty("m_FogStripping");
            keep = KeepFlags.Select(graphics.FindProperty).Where(p => p != null).ToArray();
            storedLightmapStripping = lightmapStripping?.intValue ?? 0;
            storedFogStripping = fogStripping?.intValue ?? 0;
            storedKeep = keep.Select(p => p.boolValue).ToArray();
            if (lightmapStripping != null) lightmapStripping.intValue = CustomStripping;
            if (fogStripping != null) fogStripping.intValue = CustomStripping;
            foreach (var p in keep) p.boolValue = true;
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        public void Dispose()
        {
            graphics.Update();
            if (lightmapStripping != null) lightmapStripping.intValue = storedLightmapStripping;
            if (fogStripping != null) fogStripping.intValue = storedFogStripping;
            for (var i = 0; i < keep.Length; i++) keep[i].boolValue = storedKeep[i];
            graphics.ApplyModifiedPropertiesWithoutUndo();
            // The build saves the project settings with the temporary values: write the restored ones back.
            EditorUtility.SetDirty(graphics.targetObject);
            AssetDatabase.SaveAssets();
        }
    }
}
