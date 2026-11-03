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
    half _Fog;
    float4 _MeltOrigin;
    float4 _MeltUp;
    float4 _MeltRight;
    float4 _MeltForward;
    float4 _MeltHeat;
    float4 _MeltWater;
CBUFFER_END

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D_ARRAY(_FlipbookArray);
SAMPLER(sampler_FlipbookArray);
TEXTURE2D(_GlowMap);
SAMPLER(sampler_GlowMap);

// One shared frame for the kart and skinned driver: vertices settle into the same pool, not individual mesh pivots.
// Origin.w is strength; Up.w is full height; Right.w is spread; Forward.w is wave amplitude; Heat.w is elapsed time.
float3 MHKMeltPosition(float3 positionWS)
{
    UNITY_BRANCH
    if (_MeltOrigin.w <= 0.0) return positionWS;
    float3 delta = positionWS - _MeltOrigin.xyz;
    float x = dot(delta, _MeltRight.xyz);
    float y = dot(delta, _MeltUp.xyz);
    float z = dot(delta, _MeltForward.xyz);
    float height = max(_MeltUp.w, 0.001);
    float h = saturate(y / height);
    float strength = _MeltOrigin.w;
    float spread = 1.0 + strength * _MeltRight.w * (1.0 - 0.65 * h);
    float phase = _MeltHeat.w * 13.0;
    float wave = sin(x / height * 9.0 + phase) * sin(z / height * 8.0 - phase * 0.73);
    float wobble = _MeltForward.w * strength * (0.4 + 0.6 * (1.0 - strength));
    float pooledY = y * (1.0 - 0.9 * strength);
    pooledY += wave * wobble * saturate(h * 5.0);
    float sway = sin(phase * 0.47 + z / height * 6.0) * wobble * h;
    return _MeltOrigin.xyz + _MeltRight.xyz * (x * spread + sway)
        + _MeltUp.xyz * max(pooledY, 0.0) + _MeltForward.xyz * (z * spread);
}

float3 MHKMeltNormal(float3 normalWS)
{
    UNITY_BRANCH
    if (_MeltOrigin.w <= 0.0) return normalWS;
    // Inverse-transpose of the main pooling/sag scale. The small boiling ripples keep the mesh's smooth normals.
    float spread = 1.0 + _MeltOrigin.w * _MeltRight.w;
    float3 horizontal = normalWS - _MeltUp.xyz * dot(normalWS, _MeltUp.xyz);
    return normalize(horizontal / spread + _MeltUp.xyz * dot(normalWS, _MeltUp.xyz) / (1.0 - 0.9 * _MeltOrigin.w));
}

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
