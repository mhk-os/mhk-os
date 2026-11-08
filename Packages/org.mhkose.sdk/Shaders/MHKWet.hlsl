#ifndef MHK_WET_INCLUDED
#define MHK_WET_INCLUDED

#include "MHKRain.hlsl"

// Wet look of MHK/Lit's forward pass for rain options, from the rain globals (MHKRain.hlsl): darker, glossy uncovered
// surfaces with puddles. Only materials MHKWetMaterial allows get wet (not sky domes, water or lava), up-facing surfaces
// fully, walls a little, ceilings not.
#define MHK_WET_WALL 0.3          // wetness of vertical surfaces (up-facing 1, facing down 0)
#define MHK_WET_ALBEDO 0.62       // albedo multiplier of wet surfaces...
#define MHK_PUDDLE_ALBEDO 0.4     // ...and under a puddle
#define MHK_WET_GLOSS 40.0        // Blinn-Phong exponent of wet surfaces...
#define MHK_PUDDLE_GLOSS 360.0    // ...and of a puddle's mirror
#define MHK_WET_SHINE 0.22        // reflection and highlight strength of wet surfaces (puddles: MHK_PUDDLE_SHINE)
#define MHK_WET_SKY 0.55          // brightness of the sky a wet film (not a puddle) reflects: rough ground mirrors it dimly
#define MHK_PUDDLE_SCALE 0.22     // puddle noise frequency (1/m): puddles a metre or two across
#define MHK_PUDDLE_SHINE 0.6      // reflection strength of a puddle's mirror (1 = a perfect mirror)

struct MHKWetSurface
{
    half amount;    // 0 = dry, the other fields are unset
    half puddle;    // 0..1
    half3 normal;   // shading normal of the water film: the surface's, flat and rippled in puddles
    half3 view;     // towards the camera
    float gloss;
    half shine;
    half crest;
};

// Wet look share of the material, 0 = never wet: sky domes (_Fog 0), water and lava animated by frame animation or UV
// scroll, blended surfaces (water planes, glass, decals) and materials with _WetMask 0 (TrackWeather sets it on race
// copies of water and lava materials it finds by name; authors may set it on any material).
half MHKWetMaterial()
{
    #if defined(_FLIPBOOK)
        return 0;
    #else
        return any(_UVScroll.xy != 0.0) || _DstBlend != 0.0 || _Fog < 0.5 ? 0 : saturate(_WetMask);
    #endif
}

half MHKWetFacing(half3 n)
{
    return saturate(n.y * 4.0 + 1.0) * lerp(MHK_WET_WALL, 1.0, smoothstep(0.35, 0.85, n.y));
}

// World-space puddles: where two octaves of value noise pass a threshold that falls with the puddle amount, on flat ground
// (up = the surface's upward facing, 1 = level): none from about 15° of slope on. Less puddle ground (towards cover) raises
// the threshold steadily to 1, which the noise never reaches: the puddles shrink to scattered small ones and away instead
// of being cut off.
half MHKPuddle(float2 xz, half up, half ground)
{
    float field = MHKWetNoise(xz * MHK_PUDDLE_SCALE) * 0.7 + MHKWetNoise(xz * (MHK_PUDDLE_SCALE * 3.7) + 19.3) * 0.3;
    float threshold = lerp(1.0, lerp(0.98, 0.68, _MHKWet.y), ground);
    return smoothstep(threshold, threshold + 0.05, field) * smoothstep(0.965, 0.99, up) * step(0.001, _MHKWet.y);
}

// The water film at a fragment. Branches on the global wetness first, so dry tracks pay one comparison.
MHKWetSurface MHKWetSurfaceAt(float3 positionWS, half3 n)
{
    MHKWetSurface w = (MHKWetSurface)0;
    UNITY_BRANCH
    if (_MHKWet.x <= 0.0) return w;
    half material = MHKWetMaterial() * (1.0 - _Emission);
    UNITY_BRANCH
    if (material <= 0.0) return w;
    // Derivatives before any per-fragment branch: the ripple filter width, and the facing of the triangle itself, since
    // smoothed vertex normals can call a steep slope level near the bottom and top of a hill.
    float2 rp = positionWS.xz * MHK_RIPPLE_SCALE;
    float rippleWidth = max(fwidth(rp.x), fwidth(rp.y));
    half faceUp = abs(normalize(cross(ddy(positionWS), ddx(positionWS))).y);
    half ground;
    half uncovered = MHKWetUncovered(positionWS, ground);
    w.amount = _MHKWet.x * MHKWetFacing(n) * uncovered * material;
    // Puddles reach a little under the cover, past the rain: still water there, without rings.
    UNITY_BRANCH
    if (w.amount <= 0.0 && ground * material <= 0.0) return w;
    w.puddle = MHKPuddle(positionWS.xz, min(n.y, faceUp), ground) * material;
    w.view = SafeNormalize(GetWorldSpaceViewDir(positionWS));
    half3 ripple = MHKRipples(positionWS.xz, rippleWidth) * (w.puddle * uncovered);
    half3 level = normalize(lerp(n, half3(0, 1, 0), w.puddle));
    w.normal = normalize(level + half3(ripple.x, 0, ripple.y) * MHK_RIPPLE_SLOPE);
    w.gloss = lerp(MHK_WET_GLOSS, MHK_PUDDLE_GLOSS, w.puddle);
    w.shine = lerp(MHK_WET_SHINE * w.amount, MHK_PUDDLE_SHINE, w.puddle);
    w.crest = ripple.z;
    return w;
}

// Albedo multiplier: wet surfaces darker, puddles darker still. 1 when dry.
half MHKWetAlbedo(MHKWetSurface w)
{
    return lerp(lerp(1.0, MHK_WET_ALBEDO, w.amount), MHK_PUDDLE_ALBEDO, w.puddle);
}

// Glossy Blinn-Phong highlight of light l on the water film (n = the geometric normal, for the light's side).
half3 MHKWetSpecular(Light l, half3 n, MHKWetSurface w)
{
    float3 h = SafeNormalize(float3(l.direction) + float3(w.view));
    float highlight = pow(saturate(dot(float3(w.normal), h)), w.gloss);
    half lit = l.distanceAttenuation * l.shadowAttenuation * step(0.0, dot(n, l.direction)) * w.shine;
    return l.color * (half(highlight) * lit);
}

// Lit colour col with the water film's fresnel reflection of the sky (the ambient light) fading into the fog colour at the
// horizon, plus the sun's and the additional lights' (specular) highlights. Tinted like the surface.
half3 MHKWetShine(half3 col, half3 n, MHKWetSurface w, Light mainLight, half3 specular)
{
    half3 r = reflect(-w.view, w.normal);
    half3 sky = lerp(MHKAmbientCube(r), unity_FogColor.rgb, 1.0 - saturate(r.y * 2.5)) * lerp(MHK_WET_SKY, 1.0, w.puddle);
    half fresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(w.normal, w.view)), 5.0);
    half reflection = saturate((fresnel + w.crest * MHK_RIPPLE_CREST) * w.shine);
    half3 highlights = MHKWetSpecular(mainLight, n, w) + specular;
    return lerp(col, sky * _Tint.rgb, reflection) + highlights * _Tint.rgb;
}

#endif
