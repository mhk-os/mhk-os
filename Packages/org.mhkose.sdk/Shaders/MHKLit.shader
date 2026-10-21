// Lambert lighting for all ported content: ambient plus N.L of the sun and additional lights, saturated, times texture,
// colour and tint, plus glow. Of the bake only the shadowmask and probe occlusion are used.
Shader "MHK/Lit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1,1,1,1)
        _Tint ("Tint (effects)", Color) = (1,1,1,1)
        _Emission ("Emission (self illumination)", Range(0, 1)) = 0
        [Toggle(_GLOW)] _GlowOn ("Glow (lamps / windows painted in the texture)", Float) = 0
        [NoScaleOffset] _GlowMap ("Glow mask (RGB, times texture: white = glows)", 2D) = "white" {}
        [HDR] _GlowColor ("Glow colour (HDR)", Color) = (1,1,1,1)
        _GlowBake ("Glow light in the bake (multiplier, BakedIndirectLighting scenes)", Float) = 1
        _UVScroll ("UV scroll (xy)", Vector) = (0,0,0,0)
        [Toggle(_FLIPBOOK)] _FlipbookOn ("Texture frame animation", Float) = 0
        [NoScaleOffset] _FlipbookArray ("Animation frames (2D array)", 2DArray) = "" {}
        _Flipbook ("Animation (x frames, y fps)", Vector) = (1,0,0,0)
        [Toggle(_VERTEX_TINT)] _VertexTint ("Vertex colour tint (effect geometry)", Float) = 0
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha clip", Float) = 0
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        [ToggleOff(_RECEIVE_SHADOWS_OFF)] _ReceiveShadows ("Receive realtime shadows", Float) = 1
        [Toggle(_IGNORE_SUN_SHADOWS)] _IgnoreSunShadows ("Ignore sun shadows (realtime and baked)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        // The lightmapper reads these for cutout transparency (it ignores _BaseMap). MHK > Lighting fills them.
        [HideInInspector] _MainTex ("Lightmapper texture", 2D) = "white" {}
        [HideInInspector] _Color ("Lightmapper colour", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "MHKLitInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma shader_feature_local_fragment _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _IGNORE_SUN_SHADOWS
            #pragma shader_feature_local _VERTEX_TINT
            #pragma shader_feature_local_fragment _GLOW
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "MHKLitKeywords.hlsl"
            #include "MHKLitForwardPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            // Both faces cast: level geometry is single-sided (ceilings face down into tunnels and caves).
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include_with_pragmas "MHKLitKeywords.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif
                o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir)));
                o.uv = MHKUV(v.uv);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                MHKAlphaClip(MHKSampleBase(i.uv).a * _BaseColor.a);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include_with_pragmas "MHKLitKeywords.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = MHKUV(v.uv);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                MHKAlphaClip(MHKSampleBase(i.uv).a * _BaseColor.a);
                return i.positionCS.z;
            }
            ENDHLSL
        }

        // Lightmapper input: albedo for bounce light, self illumination and glow (on materials flagged Baked Emissive).
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature EDITOR_VISUALIZATION
            #pragma shader_feature_local_fragment _GLOW
            #include_with_pragmas "MHKLitKeywords.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                #ifdef EDITOR_VISUALIZATION
                    float2 VizUV : TEXCOORD1;
                    float4 LightCoord : TEXCOORD2;
                #endif
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                o.positionCS = UnityMetaVertexPosition(v.positionOS.xyz, v.uv1, v.uv2);
                o.uv = MHKUV(v.uv0);
                #ifdef EDITOR_VISUALIZATION
                    UnityEditorVizData(v.positionOS.xyz, v.uv0, v.uv1, v.uv2, o.VizUV, o.LightCoord);
                #endif
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 tex = MHKSampleBase(i.uv) * _BaseColor;
                MHKAlphaClip(tex.a);
                UnityMetaInput meta = (UnityMetaInput)0;
                meta.Albedo = tex.rgb;
                meta.Emission = tex.rgb * _Emission;
                #ifdef _GLOW
                    meta.Emission += MHKGlow(i.uv, tex.rgb) * _GlowBake;
                #endif
                #ifdef EDITOR_VISUALIZATION
                    meta.VizUV = i.VizUV;
                    meta.LightCoord = i.LightCoord;
                #endif
                return UnityMetaFragment(meta);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
