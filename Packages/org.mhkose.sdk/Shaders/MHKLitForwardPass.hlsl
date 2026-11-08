#ifndef MHK_LIT_FORWARD_PASS_INCLUDED
#define MHK_LIT_FORWARD_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "MHKAmbient.hlsl"
#include "MHKWet.hlsl"

// Share of the lightning flash reaching covered surfaces (the wet cover map: tunnels, under roofs).
#define MHK_FLASH_COVERED 0.15

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
    o.positionWS = MHKMeltPosition(TransformObjectToWorld(v.positionOS.xyz));
    o.positionCS = TransformWorldToHClip(o.positionWS);
    o.normalWS = MHKMeltNormal(TransformObjectToWorldNormal(v.normalOS));
    o.uv = MHKUV(v.uv);
    o.fogFactor = ComputeFogFactor(o.positionCS.z);
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

void MHKAddLight(Light l, half3 n, MHKWetSurface wet, inout half3 light, inout half3 specular)
{
    light += MHKLambert(l, n);
    UNITY_BRANCH
    if (wet.shine > 0.0) specular += MHKWetSpecular(l, n, wet);
}

// Lambert of the additional lights into light and, on a wet surface, their highlights into specular.
void MHKAddAdditionalLights(Varyings i, half3 n, half4 shadowMask, MHKWetSurface wet, inout half3 light, inout half3 specular)
{
    #if defined(_ADDITIONAL_LIGHTS)
        InputData inputData = (InputData)0;
        inputData.positionWS = i.positionWS;
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
        uint lightCount = GetAdditionalLightsCount();
        #if USE_CLUSTER_LIGHT_LOOP
            [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                MHKAddLight(GetAdditionalLight(lightIndex, i.positionWS, shadowMask), n, wet, light, specular);
        #endif
        LIGHT_LOOP_BEGIN(lightCount)
            MHKAddLight(GetAdditionalLight(lightIndex, i.positionWS, shadowMask), n, wet, light, specular);
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

float3 MHKMeltHash(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx);
}

// One layer of boiling bubbles in melt-local cells: each cell may hold a bubble at a random spot that swells, holds
// and pops on its own random cycle. A bubble stays inside its cell, so only that cell is evaluated. x = bubble film
// (brighter rim), y = specular glint.
half2 MHKMeltBubbles(float3 p, float time)
{
    float3 cell = floor(p);
    float3 h = MHKMeltHash(cell);
    float3 g = MHKMeltHash(cell + 17.31);
    float life = frac(time / (0.55 + 0.75 * g.x) + g.y);
    float maxRadius = 0.22 + 0.2 * g.z;
    float3 centre = 0.5 + (h - 0.5) * (1.0 - 2.0 * maxRadius);
    // Swell, then burst slightly wider while fading; a fifth of the cells stay empty.
    float radius = maxRadius * (smoothstep(0.0, 0.7, life) + 0.15 * smoothstep(0.82, 1.0, life));
    half alive = step(0.2, h.x) * smoothstep(0.0, 0.08, life) * (1.0 - smoothstep(0.82, 0.95, life));
    float3 offset = p - cell - centre;
    float d = length(offset);
    float aa = max(fwidth(d), 1e-4);
    half disc = 1.0 - smoothstep(radius - aa, radius + aa, d);
    half rim = smoothstep(radius * 0.55, radius, d) * disc;
    half glint = 1.0 - smoothstep(0.0, radius * 0.3 + aa, length(offset - float3(-0.35, 0.35, 0.35) * radius));
    return half2(disc * 0.25 + rim * 0.75, glint) * alive;
}

// Shading normal: the vertex normal, or the face's own normal where the vertex normal is broken. Ground meshes of some
// ported tracks (MHK Extra Docklands, MHK 2 Island) carry normals lying flat in the road or pointing down, the originals
// never lit them; the sun, the headlights and the wet look then miss the road. The face normal is the one of the drawn
// side (facing the camera). Normals within 60° of it keep their smooth shading, beyond 75° the face's replaces them.
half3 MHKShadingNormal(float3 positionWS, half3 n)
{
    float3 face = cross(ddy(positionWS), ddx(positionWS));
    face = SafeNormalize(face * sign(dot(face, GetWorldSpaceViewDir(positionWS))));
    return normalize(lerp(half3(face), n, smoothstep(0.25, 0.5, dot(float3(n), face))));
}

half4 frag(Varyings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    half4 tex = MHKSurface(i);
    half3 n = MHKShadingNormal(i.positionWS, normalize(i.normalWS));
    MHKWetSurface wet = MHKWetSurfaceAt(i.positionWS, n);
    half4 shadowMask = MHKShadowMask(i);
    Light mainLight = MHKMainLight(i, shadowMask);
    half3 light = MHKAmbient(i, n) + MHKLambert(mainLight, n);
    half3 specular = 0;
    MHKAddAdditionalLights(i, n, shadowMask, wet, light, specular);
    // The lightning flash lights everything outdoors (under cover only a glimmer); self-illuminated surfaces only
    // brighten with it when unfogged (sky domes).
    half3 flash = _MHKFlash.rgb;
    UNITY_BRANCH
    if (any(flash > 0.0))
    {
        half ground;
        flash *= lerp(MHK_FLASH_COVERED, 1.0, MHKWetUncovered(i.positionWS, ground));
    }
    light = lerp(saturate(light + flash), 1.0 + flash * (1.0 - _Fog), _Emission);
    half3 col = tex.rgb * MHKWetAlbedo(wet) * light * _Tint.rgb;
    UNITY_BRANCH
    if (wet.shine > 0.0) col = MHKWetShine(col, n, wet, mainLight, specular);
    #ifdef _GLOW
        col += MHKGlow(i.uv, tex.rgb) * _Tint.rgb;
    #endif
    UNITY_BRANCH
    if (_MeltOrigin.w > 0.0)
    {
        half heat = _MeltOrigin.w * (0.85 + 0.15 * sin(_MeltHeat.w * 10.0));
        col = lerp(col, col * half3(1.1, 0.72, 0.45) + _MeltHeat.rgb, heat);
        float3 delta = (i.positionWS - _MeltOrigin.xyz) / max(_MeltUp.w, 0.001);
        float3 local = float3(dot(delta, _MeltRight.xyz), dot(delta, _MeltUp.xyz), dot(delta, _MeltForward.xyz));
        half2 bubbles = max(MHKMeltBubbles(local * 3.0, _MeltHeat.w),
            MHKMeltBubbles(local * 5.0 + 5.3, _MeltHeat.w * 1.3));
        half rim = 1.0 - saturate(dot(n, SafeNormalize(GetWorldSpaceViewDir(i.positionWS))));
        rim *= rim;
        half water = _MeltOrigin.w * _MeltWater.a;
        col = lerp(col, _MeltWater.rgb, saturate(water * (0.3 + 0.3 * rim)));
        // Bubble films read clearly over the thin sheen; a sheen with zero strength has no bubbles either.
        half bubbleStrength = _MeltOrigin.w * saturate(_MeltWater.a * 2.0);
        col = lerp(col, _MeltWater.rgb, saturate(bubbles.x * bubbleStrength * 0.85)) + bubbles.y * bubbleStrength * 0.5;
    }
    col = lerp(col, MixFog(col, i.fogFactor), _Fog);
    return half4(col, tex.a * _Tint.a);
}

#endif
