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
    half _WetMask;
    half _DstBlend;
    float4 _MeltOrigin;
    float4 _MeltUp;
    float4 _MeltRight;
    float4 _MeltForward;
    float4 _MeltHeat;
    float4 _MeltWater;
    float4 _Glitch;
    float4 _GlitchOrigin;
CBUFFER_END

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D_ARRAY(_FlipbookArray);
SAMPLER(sampler_FlipbookArray);
TEXTURE2D(_GlowMap);
SAMPLER(sampler_GlowMap);

// One shared frame for the kart and skinned driver: vertices settle into the same pool, not individual mesh pivots.
// Origin.w is strength; Up.w is full height; Right.w is spread; Forward.w is wave amplitude; Heat.w is elapsed time.
// The fully melted pool keeps 1 - MHK_MELT_SAG of its height: with the Extra boil's two-thirds visual scale the kart
// ends at half its normal height instead of flat on the road.
#define MHK_MELT_SAG 0.25
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
    float pooledY = y * (1.0 - MHK_MELT_SAG * strength);
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
    return normalize(horizontal / spread + _MeltUp.xyz * dot(normalWS, _MeltUp.xyz) / (1.0 - MHK_MELT_SAG * _MeltOrigin.w));
}

float3 MHKGlitchHash(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx);
}

// Jammed TV picture (KartGlitch). _Glitch.x is strength, y the seed of the shown glitch frame, z bands per metre, w the
// widest tear (m). _GlitchOrigin.xyz is the kart's position. Horizontal bands of the model tear sideways on screen, and
// the whole model stretches a little up or sideways about the origin, a new pattern for every seed.
float3 MHKGlitchPosition(float3 positionWS)
{
    UNITY_BRANCH
    if (_Glitch.x <= 0.0) return positionWS;
    float3 right = UNITY_MATRIX_V[0].xyz;
    float3 frame = MHKGlitchHash(float3(_Glitch.y, 1.7, 9.2));
    float3 delta = positionWS - _GlitchOrigin.xyz;
    float up = delta.y * (1.0 + (frame.x - 0.5) * 0.6 * _Glitch.x);
    float across = dot(delta, right) * ((frame.y - 0.5) * 0.5 * _Glitch.x);
    float3 band = MHKGlitchHash(float3(floor(delta.y * _Glitch.z), _Glitch.y, 4.1));
    float tear = step(0.5, band.x) * (band.y * 2.0 - 1.0) * _Glitch.w * _Glitch.x;
    return positionWS + float3(0.0, up - delta.y, 0.0) + right * (across + tear);
}

// Static and colour fringes of the torn bands, scanlines over the rest of the jammed model. The band of a fragment is
// taken before the stretch of MHKGlitchPosition, so fringes line up with the tears.
half3 MHKGlitchColor(half3 col, float4 positionCS, float3 positionWS)
{
    UNITY_BRANCH
    if (_Glitch.x <= 0.0) return col;
    float3 frame = MHKGlitchHash(float3(_Glitch.y, 1.7, 9.2));
    float y = (positionWS.y - _GlitchOrigin.y) / (1.0 + (frame.x - 0.5) * 0.6 * _Glitch.x);
    float3 band = MHKGlitchHash(float3(floor(y * _Glitch.z), _Glitch.y, 4.1));
    half torn = step(0.5, band.x) * _Glitch.x;
    float2 pixel = positionCS.xy;
    half snow = MHKGlitchHash(float3(floor(pixel * 0.5), _Glitch.y)).x;
    half3 fringe = band.z > 0.5 ? half3(0.55, 1.25, 1.3) : half3(1.3, 0.55, 1.25);
    col = lerp(col, col * fringe, torn);
    col = lerp(col, snow.xxx, torn * step(0.35, band.y) * 0.75);
    return col * (1.0 - 0.3 * _Glitch.x * step(0.5, frac(pixel.y * 0.25)));
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
