#ifndef MHK_LIT_FORWARD_PASS_INCLUDED
#define MHK_LIT_FORWARD_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "MHKAmbient.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 uv : TEXCOORD0;
    #ifdef LIGHTMAP_ON
        float2 lightmapUV : TEXCOORD1;
    #endif
    #ifdef _VERTEX_TINT
        half4 color : COLOR;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half3 normalWS : TEXCOORD2;
    half fogFactor : TEXCOORD3;
    #ifdef LIGHTMAP_ON
        float2 lightmapUV : TEXCOORD4;
    #endif
    #ifdef _VERTEX_TINT
        half4 color : COLOR;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings vert(Attributes v)
{
    Varyings o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
    o.positionCS = pos.positionCS;
    o.positionWS = pos.positionWS;
    o.normalWS = TransformObjectToWorldNormal(v.normalOS);
    o.uv = MHKUV(v.uv);
    o.fogFactor = ComputeFogFactor(pos.positionCS.z);
    OUTPUT_LIGHTMAP_UV(v.lightmapUV, unity_LightmapST, o.lightmapUV);
    #ifdef _VERTEX_TINT
        o.color = v.color;
    #endif
    return o;
}

half3 MHKLambert(Light l, half3 n)
{
    return l.color * (saturate(dot(n, l.direction)) * l.distanceAttenuation * l.shadowAttenuation);
}

// URP binds no SH for lightmapped renderers and baked track probes hold baked light (only their occlusion is wanted),
// so baked surfaces, and all objects while baked probes are loaded, use the ambient cube. BakedIndirectLighting scenes
// use the colour lightmap (static) or the probes (everything else) instead.
half3 MHKAmbient(Varyings i, half3 n)
{
    bool bakedIndirect = MHK_ON(_MHKBakedIndirect.x);
    #ifdef LIGHTMAP_ON
        UNITY_BRANCH
        if (bakedIndirect) return max(SampleLightmap(i.lightmapUV, n) * _MHKBakedIndirect.y, _MHKBakedIndirect.z);
        return MHKAmbientCube(n);
    #else
        UNITY_BRANCH
        if (bakedIndirect) return max(SampleSH(n) * _MHKBakedIndirect.y, _MHKBakedIndirect.z);
        UNITY_BRANCH
        if (MHK_ON(_MHKProbeAmbient)) return MHKAmbientCube(n);
        return SampleSH(n);
    #endif
}

half4 MHKShadowMask(Varyings i)
{
    #ifdef LIGHTMAP_ON
        return SAMPLE_TEXTURE2D_LIGHTMAP(SHADOWMASK_NAME, SHADOWMASK_SAMPLER_NAME, i.lightmapUV SHADOWMASK_SAMPLE_EXTRA_ARGS);
    #else
        return unity_ProbesOcclusion;
    #endif
}

// Realtime sun shadow combined with the baked one (shadowmask or probe occlusion) by min, so an overlap never darkens
// twice. _RECEIVE_SHADOWS_OFF drops only the realtime part, _IGNORE_SUN_SHADOWS drops both.
Light MHKMainLight(Varyings i, half4 shadowMask)
{
    Light light = GetMainLight();
    #ifdef _IGNORE_SUN_SHADOWS
        light.shadowAttenuation = 1;
    #else
        half4 shadowParams = GetMainLightShadowParams();
        half realtime = MainLightRealtimeShadow(TransformWorldToShadowCoord(i.positionWS), shadowParams, GetMainLightShadowSamplingData());
        light.shadowAttenuation = min(realtime, BakedShadow(shadowMask, _MainLightOcclusionProbes, shadowParams));
    #endif
    return light;
}

void MHKAddAdditionalLights(Varyings i, half3 n, half4 shadowMask, inout half3 light)
{
    #if defined(_ADDITIONAL_LIGHTS)
        InputData inputData = (InputData)0;
        inputData.positionWS = i.positionWS;
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
        uint lightCount = GetAdditionalLightsCount();
        #if USE_CLUSTER_LIGHT_LOOP
            [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                light += MHKLambert(GetAdditionalLight(lightIndex, i.positionWS, shadowMask), n);
        #endif
        LIGHT_LOOP_BEGIN(lightCount)
            light += MHKLambert(GetAdditionalLight(lightIndex, i.positionWS, shadowMask), n);
        LIGHT_LOOP_END
    #endif
}

half4 MHKSurface(Varyings i)
{
    half4 tex = MHKSampleBase(i.uv) * _BaseColor;
    #ifdef _VERTEX_TINT
        tex *= i.color;
    #endif
    MHKAlphaClip(tex.a);
    return tex;
}

half4 frag(Varyings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    half4 tex = MHKSurface(i);
    half3 n = normalize(i.normalWS);
    half4 shadowMask = MHKShadowMask(i);
    half3 light = MHKAmbient(i, n) + MHKLambert(MHKMainLight(i, shadowMask), n);
    MHKAddAdditionalLights(i, n, shadowMask, light);
    light = lerp(saturate(light), half3(1, 1, 1), _Emission);
    half3 col = tex.rgb * light * _Tint.rgb;
    #ifdef _GLOW
        col += MHKGlow(i.uv, tex.rgb) * _Tint.rgb;
    #endif
    col = MixFog(col, i.fogFactor);
    return half4(col, tex.a * _Tint.a);
}

#endif
