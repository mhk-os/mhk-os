using UnityEngine;

namespace MHK.Rendering
{
    /// <summary>Shader name, keywords and cached property ids of MHK/Lit and its global inputs.</summary>
    public static class MHKLitIds
    {
        public const string ShaderName = "MHK/Lit";

        public const string GlowKeyword = "_GLOW";
        public const string FlipbookKeyword = "_FLIPBOOK";
        public const string VertexTintKeyword = "_VERTEX_TINT";
        public const string AlphaTestKeyword = "_ALPHATEST_ON";
        public const string ReceiveShadowsOffKeyword = "_RECEIVE_SHADOWS_OFF";
        public const string IgnoreSunShadowsKeyword = "_IGNORE_SUN_SHADOWS";

        public static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        public static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        public static readonly int Tint = Shader.PropertyToID("_Tint");
        public static readonly int Emission = Shader.PropertyToID("_Emission");
        public static readonly int GlowOn = Shader.PropertyToID("_GlowOn");
        public static readonly int GlowMap = Shader.PropertyToID("_GlowMap");
        public static readonly int GlowColor = Shader.PropertyToID("_GlowColor");
        public static readonly int GlowBake = Shader.PropertyToID("_GlowBake");
        public static readonly int UVScroll = Shader.PropertyToID("_UVScroll");
        public static readonly int FlipbookOn = Shader.PropertyToID("_FlipbookOn");
        public static readonly int FlipbookArray = Shader.PropertyToID("_FlipbookArray");
        public static readonly int Flipbook = Shader.PropertyToID("_Flipbook");
        public static readonly int VertexTint = Shader.PropertyToID("_VertexTint");
        public static readonly int AlphaClip = Shader.PropertyToID("_AlphaClip");
        public static readonly int Cutoff = Shader.PropertyToID("_Cutoff");
        public static readonly int Fog = Shader.PropertyToID("_Fog");
        public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        public static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
        public static readonly int Cull = Shader.PropertyToID("_Cull");
        public static readonly int MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int Color = Shader.PropertyToID("_Color");

        public static readonly int BakedIndirect = Shader.PropertyToID("_MHKBakedIndirect");
        public static readonly int AmbientCube = Shader.PropertyToID("_MHKAmbientCube");
        public static readonly int ProbeAmbient = Shader.PropertyToID("_MHKProbeAmbient");
    }
}
