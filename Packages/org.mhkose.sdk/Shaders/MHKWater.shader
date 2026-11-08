// Water bodies of the tracks (MHK.Race.TrackWater swaps their MHK/Lit materials for copies of this one at race start):
// the original texture's average colour with some of its detail, lit like MHK/Lit, under two layers of scrolling wave
// normals (one texture, world-space, no vertex work) that reflect the sky (ambient light into the fog colour at the
// horizon) by fresnel and catch the sun and lamp lights in sharp highlights; rain rings on uncovered water in rain
// (MHKRain.hlsl). Only level faces get the waves: walls and falls drawn with the same material keep the plain lit
// texture. No depth or opaque texture reads, one pass.
Shader "MHK/Water"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture (the original water)", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1,1,1,1)
        _Tint ("Tint (effects)", Color) = (1,1,1,1)
        _UVScroll ("UV scroll (xy)", Vector) = (0,0,0,0)
        [Toggle(_FLIPBOOK)] _FlipbookOn ("Texture frame animation", Float) = 0
        [NoScaleOffset] _FlipbookArray ("Animation frames (2D array)", 2DArray) = "" {}
        _Flipbook ("Animation (x frames, y fps)", Vector) = (1,0,0,0)
        [NoScaleOffset][Normal] _WaveMap ("Wave normals (tiling)", 2D) = "bump" {}
        _WaveScale ("Wave tile sizes (m): x large, y small", Vector) = (11, 3.7, 0, 0)
        _WaveSpeed ("Wave drift (m/s)", Float) = 0.45
        _WaveStrength ("Wave strength", Range(0, 2)) = 0.35
        _Detail ("Share of the texture's detail (the rest is its average colour)", Range(0, 1)) = 0.7
        _Body ("Brightness of the water body under the reflection", Range(0, 1)) = 0.8
        _Reflection ("Sky reflection", Range(0, 1)) = 0.65
        _SkyBrightness ("Brightness of the reflected sky", Range(0, 3)) = 1
        _Gloss ("Highlight sharpness (Blinn-Phong exponent)", Float) = 700
        _Specular ("Highlight strength", Float) = 1.5
        _Fog ("Fog (0 = never fogged)", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _Tint;
            float4 _UVScroll;
            float4 _Flipbook;
            float4 _WaveScale;
            float _WaveSpeed;
            half _WaveStrength;
            half _Detail;
            half _Body;
            half _Reflection;
            half _SkyBrightness;
            float _Gloss;
            half _Specular;
            half _Fog;
        CBUFFER_END

        float2 MHKWaterUV(float2 uv) { return TRANSFORM_TEX(uv, _BaseMap) + _UVScroll.xy * _Time.y; }
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
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_local _ _FLIPBOOK
            #pragma require 2darray
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "MHKAmbient.hlsl"
            #include "MHKRain.hlsl"

            // Level faces (upward facing of the triangle) get the waves from 0.8 on, fully from 0.95.
            #define MHK_WATER_LEVEL_FROM 0.8
            #define MHK_WATER_LEVEL_FULL 0.95
            // Texture distortion by the wave slope, mip of the texture's average colour, broader highlights of lamps.
            #define MHK_WATER_DISTORT 0.03
            #define MHK_WATER_AVERAGE_MIP 12
            #define MHK_WATER_LAMP_GLOSS 0.15
            // Wave layers flatten while a pixel spans this share of their tile and more; the small layer counts this much
            // of the large one; the sky reflection takes this share of the slopes.
            #define MHK_WATER_WAVE_FADE_FROM 0.015
            #define MHK_WATER_WAVE_FADE_TO 0.1
            #define MHK_WATER_SMALL_WAVES 0.6
            #define MHK_WATER_REFLECTION_SLOPE 0.4

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D_ARRAY(_FlipbookArray);
            SAMPLER(sampler_FlipbookArray);
            TEXTURE2D(_WaveMap);
            SAMPLER(sampler_WaveMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                #ifdef LIGHTMAP_ON
                    float2 lightmapUV : TEXCOORD1;
                #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                #ifdef LIGHTMAP_ON
                    float2 lightmapUV : TEXCOORD3;
                #endif
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = MHKWaterUV(v.uv);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                OUTPUT_LIGHTMAP_UV(v.lightmapUV, unity_LightmapST, o.lightmapUV);
                return o;
            }

            half4 SampleBase(float2 uv)
            {
                #if defined(_FLIPBOOK)
                    float frame = fmod(floor(_Time.y * _Flipbook.y), max(_Flipbook.x, 1.0));
                    return SAMPLE_TEXTURE2D_ARRAY(_FlipbookArray, sampler_FlipbookArray, uv, frame);
                #else
                    return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
                #endif
            }

            // The texture's average colour: its smallest mip.
            half3 SampleAverage(float2 uv)
            {
                #if defined(_FLIPBOOK)
                    return SAMPLE_TEXTURE2D_ARRAY_LOD(_FlipbookArray, sampler_FlipbookArray, uv, 0, MHK_WATER_AVERAGE_MIP).rgb;
                #else
                    return SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, uv, MHK_WATER_AVERAGE_MIP).rgb;
                #endif
            }

            // The sun with its realtime shadow and the baked one (shadowmask or probe occlusion), as MHK/Lit combines them.
            Light WaterMainLight(Varyings i)
            {
                Light light = GetMainLight();
                #ifdef LIGHTMAP_ON
                    half4 shadowMask = SAMPLE_TEXTURE2D_LIGHTMAP(SHADOWMASK_NAME, SHADOWMASK_SAMPLER_NAME, i.lightmapUV SHADOWMASK_SAMPLE_EXTRA_ARGS);
                #else
                    half4 shadowMask = unity_ProbesOcclusion;
                #endif
                half4 shadowParams = GetMainLightShadowParams();
                half realtime = MainLightRealtimeShadow(TransformWorldToShadowCoord(i.positionWS), shadowParams, GetMainLightShadowSamplingData());
                light.shadowAttenuation = min(realtime, BakedShadow(shadowMask, _MainLightOcclusionProbes, shadowParams));
                return light;
            }

            half Highlight(Light l, half3 n, half3 view, float gloss)
            {
                float3 h = SafeNormalize(float3(l.direction) + float3(view));
                return half(pow(saturate(dot(float3(n), h)), gloss)) * l.distanceAttenuation * l.shadowAttenuation;
            }

            // Lambert of the additional lights (on the level surface) into light, their highlights (on the waves) into
            // specular.
            void AddLamps(Varyings i, half3 up, half3 n, half3 view, inout half3 light, inout half3 specular)
            {
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = i.positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                    half4 shadowMask = unity_ProbesOcclusion;
                    uint lightCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                        [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                        {
                            Light l = GetAdditionalLight(lightIndex, i.positionWS, shadowMask);
                            light += l.color * (saturate(dot(up, l.direction)) * l.distanceAttenuation * l.shadowAttenuation);
                            specular += l.color * Highlight(l, n, view, _Gloss * MHK_WATER_LAMP_GLOSS);
                        }
                    #endif
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light l = GetAdditionalLight(lightIndex, i.positionWS, shadowMask);
                        light += l.color * (saturate(dot(up, l.direction)) * l.distanceAttenuation * l.shadowAttenuation);
                        specular += l.color * Highlight(l, n, view, _Gloss * MHK_WATER_LAMP_GLOSS);
                    LIGHT_LOOP_END
                #endif
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 p = i.positionWS;
                half3 view = SafeNormalize(GetWorldSpaceViewDir(p));
                // Derivatives first (uniform control flow): the drawn face's own normal and the rain rings' filter width.
                float3 face = cross(ddy(p), ddx(p));
                half3 up = half3(SafeNormalize(face * sign(dot(face, float3(view)))));
                float2 rp = p.xz * MHK_RIPPLE_SCALE;
                float rippleWidth = max(fwidth(rp.x), fwidth(rp.y));
                half level = smoothstep(MHK_WATER_LEVEL_FROM, MHK_WATER_LEVEL_FULL, up.y);

                // Two wave layers drifting across each other, the second turned and smaller, so the tiling never shows.
                // A layer flattens where a pixel spans a good part of its tile (far away, at grazing angles): there its
                // normals would only sparkle and show the tiling.
                float drift = _Time.y * _WaveSpeed;
                float2 uv1 = (p.xz + float2(drift, drift * 0.35)) / _WaveScale.x;
                float2 turned = float2(p.x * 0.8 - p.z * 0.6, p.x * 0.6 + p.z * 0.8);
                float2 uv2 = (turned + float2(-drift * 0.7, drift)) / _WaveScale.y;
                half fade1 = 1.0 - smoothstep(MHK_WATER_WAVE_FADE_FROM, MHK_WATER_WAVE_FADE_TO, max(length(ddx(uv1)), length(ddy(uv1))));
                half fade2 = 1.0 - smoothstep(MHK_WATER_WAVE_FADE_FROM, MHK_WATER_WAVE_FADE_TO, max(length(ddx(uv2)), length(ddy(uv2))));
                half3 w1 = UnpackNormal(SAMPLE_TEXTURE2D(_WaveMap, sampler_WaveMap, uv1));
                half3 w2 = UnpackNormal(SAMPLE_TEXTURE2D(_WaveMap, sampler_WaveMap, uv2));
                half2 slope = (w1.xy * fade1 + w2.xy * (fade2 * MHK_WATER_SMALL_WAVES)) * (_WaveStrength * level);
                half crest = 0;
                UNITY_BRANCH
                if (_MHKWet.x > 0.0 && level > 0.0)
                {
                    half ground;
                    half3 rings = MHKRipples(p.xz, rippleWidth) * (MHKWetUncovered(p, ground) * _MHKWet.x * level);
                    slope += rings.xy * MHK_RIPPLE_SLOPE;
                    crest = rings.z;
                }
                half3 n = normalize(up + half3(slope.x, 0, slope.y));

                half4 tex = SampleBase(i.uv + slope * MHK_WATER_DISTORT);
                half3 albedo = lerp(tex.rgb, lerp(SampleAverage(i.uv), tex.rgb, _Detail), level) * _BaseColor.rgb;

                Light sun = WaterMainLight(i);
                half3 light = MHKAmbientCube(up) + sun.color * (saturate(dot(up, sun.direction)) * sun.distanceAttenuation * sun.shadowAttenuation);
                half3 specular = sun.color * Highlight(sun, n, view, _Gloss);
                AddLamps(i, up, n, view, light, specular);
                light = saturate(light + _MHKFlash.rgb);
                half3 col = albedo * light * lerp(1.0, _Body, level);

                // Fresnel reflection of the sky, fading into the fog colour at the horizon. It takes a calmer normal than
                // the highlights: at grazing angles the full slopes would turn it into salt-and-pepper speckles.
                half3 calm = normalize(up + half3(slope.x, 0, slope.y) * MHK_WATER_REFLECTION_SLOPE);
                half3 r = reflect(-view, calm);
                half3 sky = lerp(MHKAmbientCube(r) * _SkyBrightness, unity_FogColor.rgb, 1.0 - saturate(r.y * 2.5));
                half fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(calm, view)), 5.0);
                half reflection = saturate((fresnel + crest * MHK_RIPPLE_CREST) * _Reflection) * level;
                col = lerp(col, sky, reflection) + specular * (_Specular * level);
                col *= _Tint.rgb;
                col = lerp(col, MixFog(col, i.fogFactor), _Fog);
                // Blended water grows opaque where it mirrors the sky, as real water does.
                half alpha = lerp(tex.a * _BaseColor.a, 1.0, reflection) * _Tint.a;
                return half4(col, alpha);
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

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target { return i.positionCS.z; }
            ENDHLSL
        }
    }
}
