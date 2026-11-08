#ifndef MHK_PARTICLE_LIGHT_INCLUDED
#define MHK_PARTICLE_LIGHT_INCLUDED

// Light of small particles without normals (MHK/Weather, MHK/Particle), per vertex: the scene light (MHK/Lit's ambient
// average and a share of the sun), a share of the point and spot lights (headlight beams, lamps; no shadows) and the
// lightning flash (_MHKFlash). Include after URP's Lighting.hlsl and MHKAmbient.hlsl.

#define MHK_PARTICLE_SUN_SHARE 0.5
#define MHK_PARTICLE_FLASH_SHARE 0.6

// Scene light: 1 (the unlit colour) at amount 0, ambient + half the sun at 1.
half3 MHKParticleSceneLight(half amount)
{
    half3 light = 1;
    UNITY_BRANCH
    if (amount > 0)
        light = lerp(half3(1, 1, 1), saturate(MHKAmbientAverage() + _MainLightColor.rgb * MHK_PARTICLE_SUN_SHARE), amount);
    return light;
}

// Normalized screen position of a clip-space vertex, as GetNormalizedScreenSpaceUV gives it in a fragment (clamped onto
// the screen: the cluster tiles of a vertex just outside it are its edge's).
float2 MHKParticleScreenUV(float4 positionCS)
{
    float2 uv = float2(positionCS.x, positionCS.y * _ProjectionParams.x) / max(positionCS.w, 1e-4) * 0.5 + 0.5;
    return clamp(uv, 0.0, 0.999);
}

// Point and spot lights at a particle (colour x distance and cone attenuation, no shadows, no normal). The directional
// fills stay out: the scene light holds the sun.
half3 MHKParticleLampLight(float3 world, float4 positionCS)
{
    half3 light = 0;
    #if defined(_ADDITIONAL_LIGHTS)
        InputData inputData = (InputData)0;
        inputData.positionWS = world;
        inputData.normalizedScreenSpaceUV = MHKParticleScreenUV(positionCS);
        uint lightCount = GetAdditionalLightsCount();
        LIGHT_LOOP_BEGIN(lightCount)
            Light l = GetAdditionalLight(lightIndex, world);
            light += l.color * l.distanceAttenuation;
        LIGHT_LOOP_END
    #endif
    return light;
}

// Light of a visible particle vertex: the scene light (sceneAmount), the lamps' share (lampShare, saturated: a particle
// in a beam glows, it does not burn white) and the lightning flash.
half3 MHKParticleLight(float3 world, float4 positionCS, half sceneAmount, half lampShare)
{
    half3 lamps = 0;
    UNITY_BRANCH
    if (lampShare > 0) lamps = saturate(MHKParticleLampLight(world, positionCS) * lampShare);
    return MHKParticleSceneLight(sceneAmount) + lamps + _MHKFlash.rgb * MHK_PARTICLE_FLASH_SHARE;
}

#endif
