#ifndef MHK_WET_INCLUDED
#define MHK_WET_INCLUDED

// Wet look of MHK/Lit's forward pass for rain options, from globals set by MHK.Race.TrackWeather (unset = dry):
// _MHKWet x = wetness 0..1 (0 = off), y = puddle amount 0..1. _MHKWetCover R16 heights of the topmost surfaces,
// _MHKWetCoverRect (minX, minZ, 1 / sizeX, 1 / sizeZ), _MHKWetCoverHeight (minY, range, bias, on): a point is covered when
// its height + bias lies below the cover height at its texel (nearest, 0 = nothing there), as the physics decides it.
// Only materials MHKWetMaterial allows get wet (not sky domes, water or lava), up-facing surfaces fully, walls a little,
// ceilings not.
float4 _MHKWet;
float4 _MHKWetCoverRect;
float4 _MHKWetCoverHeight;
TEXTURE2D(_MHKWetCover);

#define MHK_WET_WALL 0.3          // wetness of vertical surfaces (up-facing 1, facing down 0)
#define MHK_WET_ALBEDO 0.62       // albedo multiplier of wet surfaces...
#define MHK_PUDDLE_ALBEDO 0.4     // ...and under a puddle
#define MHK_WET_GLOSS 40.0        // Blinn-Phong exponent of wet surfaces...
#define MHK_PUDDLE_GLOSS 360.0    // ...and of a puddle's mirror
#define MHK_WET_SHINE 0.22        // reflection and highlight strength of wet surfaces (puddles: MHK_PUDDLE_SHINE)
#define MHK_WET_SKY 0.55          // brightness of the sky a wet film (not a puddle) reflects: rough ground mirrors it dimly
#define MHK_PUDDLE_SCALE 0.22     // puddle noise frequency (1/m): puddles a metre or two across
#define MHK_PUDDLE_SHINE 0.6      // reflection strength of a puddle's mirror (1 = a perfect mirror)
#define MHK_PUDDLE_GROUND 0.35    // puddles lie only this close (m) to the cover map's topmost surface: on the ground, not karts
#define MHK_RIPPLE_SCALE 2.0      // ripple cells per metre
#define MHK_RIPPLE_SLOPE 0.6      // normal tilt of a ripple ring
#define MHK_RIPPLE_CREST 0.25     // extra reflection on a ripple's crest

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

float MHKWetHash(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float MHKWetNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = MHKWetHash(cell);
    float b = MHKWetHash(cell + float2(1, 0));
    float c = MHKWetHash(cell + float2(0, 1));
    float d = MHKWetHash(cell + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// 1 where nothing covers positionWS, 0 under cover. ground = 1 near the topmost surface (or without a map), else 0.
half MHKWetUncovered(float3 positionWS, out half ground)
{
    ground = 1;
    UNITY_BRANCH
    if (_MHKWetCoverHeight.w < 0.5) return 1;
    float2 uv = (positionWS.xz - _MHKWetCoverRect.xy) * _MHKWetCoverRect.zw;
    if (any(uv < 0.0) || any(uv >= 1.0)) return 1;
    float value = SAMPLE_TEXTURE2D_LOD(_MHKWetCover, sampler_PointClamp, uv, 0).r;
    if (value <= 0.0) return 1;
    float top = _MHKWetCoverHeight.x + value * _MHKWetCoverHeight.y;
    ground = abs(top - positionWS.y) < MHK_PUDDLE_GROUND ? 1 : 0;
    return positionWS.y + _MHKWetCoverHeight.z < top ? 0 : 1;
}

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
// (up = the surface's upward facing, 1 = level): none from about 15° of slope on.
half MHKPuddle(float2 xz, half up, half ground)
{
    float field = MHKWetNoise(xz * MHK_PUDDLE_SCALE) * 0.7 + MHKWetNoise(xz * (MHK_PUDDLE_SCALE * 3.7) + 19.3) * 0.3;
    float threshold = lerp(0.98, 0.68, _MHKWet.y);
    return smoothstep(threshold, threshold + 0.05, field) * smoothstep(0.965, 0.99, up) * ground * step(0.001, _MHKWet.y);
}

// One layer of rain ripples in unit cells: each cell has a drop at a random spot whose ring spreads and fades on its own
// cycle, staying inside the cell, so only that cell is evaluated. xy = ring slope (normal tilt along x/z), z = crest.
half3 MHKRippleLayer(float2 p, float time)
{
    float2 cell = floor(p);
    float2 spot = float2(MHKWetHash(cell), MHKWetHash(cell + 37.1));
    float life = frac(time * (0.7 + 0.6 * MHKWetHash(cell + 71.7)) + spot.x);
    float2 offset = p - cell - (0.3 + 0.4 * spot);
    float d = length(offset);
    float x = d - life * 0.3;
    half band = saturate(1.0 - abs(x) * 14.0) * (1.0 - life) * smoothstep(0.0, 0.1, life);
    half2 dir = offset / max(d, 1e-4);
    return half3(dir * (sin(x * 45.0) * band), band);
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
    w.amount = _MHKWet.x * MHKWetFacing(n) * MHKWetUncovered(positionWS, ground) * material;
    UNITY_BRANCH
    if (w.amount <= 0.0) return w;
    w.puddle = MHKPuddle(positionWS.xz, min(n.y, faceUp), ground) * material;
    w.view = SafeNormalize(GetWorldSpaceViewDir(positionWS));
    // Rings finer than the pixels fade out instead of shimmering.
    half3 ripple = (MHKRippleLayer(rp, _Time.y) + MHKRippleLayer(rp * 1.37 + 0.5, _Time.y * 1.13 + 0.37))
        * (1.0 - smoothstep(0.03, 0.09, rippleWidth)) * w.puddle;
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
