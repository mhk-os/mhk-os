#ifndef MHK_LIT_INPUT_INCLUDED
#define MHK_LIT_INPUT_INCLUDED

// Material properties and texture sampling shared by every MHK/Lit pass (one UnityPerMaterial layout for the SRP Batcher).
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _Tint;
    half _Emission;
    half4 _GlowColor;
    half _GlowBake;
    float4 _UVScroll;
    half _Cutoff;
    float4 _Flipbook;
CBUFFER_END

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D_ARRAY(_FlipbookArray);
SAMPLER(sampler_FlipbookArray);
TEXTURE2D(_GlowMap);
SAMPLER(sampler_GlowMap);

half3 MHKGlow(float2 uv, half3 albedo)
{
    return albedo * SAMPLE_TEXTURE2D(_GlowMap, sampler_GlowMap, uv).rgb * _GlowColor.rgb;
}

float2 MHKUV(float2 uv)
{
    return TRANSFORM_TEX(uv, _BaseMap) + _UVScroll.xy * _Time.y;
}

// Flipbook frames come from the shader clock: no per-frame CPU work, materials stay shared and batched.
half4 MHKSampleBase(float2 uv)
{
    #if defined(_FLIPBOOK)
        float frame = fmod(floor(_Time.y * _Flipbook.y), max(_Flipbook.x, 1.0));
        return SAMPLE_TEXTURE2D_ARRAY(_FlipbookArray, sampler_FlipbookArray, uv, frame);
    #else
        return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    #endif
}

void MHKAlphaClip(half alpha)
{
    #ifdef _ALPHATEST_ON
        clip(alpha - _Cutoff);
    #endif
}

#endif
