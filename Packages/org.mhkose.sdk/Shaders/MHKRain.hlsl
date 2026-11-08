#ifndef MHK_RAIN_INCLUDED
#define MHK_RAIN_INCLUDED

// Rain of the running race, from globals set by MHK.Race.TrackWeather (unset = dry), for any shader: where rain falls
// (the cover map) and the rings its drops spread on standing water. MHKWet.hlsl builds MHK/Lit's wet look on it,
// MHK/Water its rain rings.
// _MHKWet x = wetness 0..1 (0 = off), y = puddle amount 0..1. _MHKWetCover R16 heights of the topmost surfaces,
// _MHKWetCoverRect (minX, minZ, 1 / sizeX, 1 / sizeZ), _MHKWetCoverTexels (width, height, metres per texel) of the map,
// _MHKWetCoverHeight (minY, range, bias, on): a texel covers a point when the point's height + bias lies below the cover
// height there (0 = nothing there), as the physics decides it at the point's own texel. The rain blends over the cover's
// edge instead of stopping at it (MHKWetUncovered).
float4 _MHKWet;
float4 _MHKWetCoverRect;
float4 _MHKWetCoverTexels;
float4 _MHKWetCoverHeight;
TEXTURE2D(_MHKWetCover);

#define MHK_PUDDLE_GROUND 0.35    // puddles lie only this close (m) to the cover map's topmost surface: on the ground, not karts
#define MHK_RIPPLE_SCALE 2.0      // ripple cells per metre
#define MHK_RIPPLE_SLOPE 0.6      // normal tilt of a ripple ring
#define MHK_RIPPLE_CREST 0.25     // extra reflection on a ripple's crest
#define MHK_SHELTER_FADE 3.0      // radius (m, at most a texel) of the filter blending the rain over the cover's edge
// The filtered spot wanders by two octaves of noise (bays and patches this many metres across) up to a reach of
// x texels, clamped to y..z metres: coarse maps lose their texel lines, precise ones keep small shelters whole.
#define MHK_SHELTER_BAY 9.0
#define MHK_SHELTER_PATCH 3.5
#define MHK_SHELTER_REACH float3(0.8, 1.5, 3.0)
// A noise threshold between x and y (shares of rain) then breaks the blend into patches, softened by z: wet tongues and
// islands under the cover's edge, dry ones beside it.
#define MHK_SHELTER_CUT float3(0.12, 0.8, 0.12)
#define MHK_PUDDLE_REACH 0.35     // filtered share of puddle ground from which puddles grow fully: they reach under the edge

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

// Two independent noise values around 0 (-0.5..0.5), for warping.
float2 MHKWetNoise2(float2 p)
{
    return float2(MHKWetNoise(p), MHKWetNoise(p + float2(23.7, 31.3))) - 0.5;
}

// Cover test of one texel for a point at height y: x = uncovered, y = ground (its topmost surface lies within
// MHK_PUDDLE_GROUND of y), each 0 or 1. Texels outside the map and empty ones cover nothing and count as ground.
half2 MHKWetCoverTexel(int2 texel, float y)
{
    if (any(texel < 0) || any(texel >= int2(_MHKWetCoverTexels.xy))) return 1;
    float value = LOAD_TEXTURE2D_LOD(_MHKWetCover, texel, 0).r;
    if (value <= 0.0) return 1;
    float top = _MHKWetCoverHeight.x + value * _MHKWetCoverHeight.y;
    return half2(y + _MHKWetCoverHeight.z < top ? 0 : 1, abs(top - y) < MHK_PUDDLE_GROUND ? 1 : 0);
}

// Tent filter weights of a texel's left neighbour, itself and its right neighbour along one axis for a point at f
// (-0.5..0.5) from its centre: the share of a tent of radius r (texels, at most 1) around the point lying over each.
float3 MHKShelterWeights(float f, float r)
{
    float left = 0.5 * Sq(max(r - (0.5 + f), 0.0) / r);
    float right = 0.5 * Sq(max(r - (0.5 - f), 0.0) / r);
    return float3(left, 1.0 - left - right, right);
}

// Share of the rain reaching positionWS: the cover tests of the texels around it, tent filtered over MHK_SHELTER_FADE at
// a noise-warped spot, then cut at a noise threshold, so the cover's edge breaks into ragged bays, wet islands under it
// and dry ones beside it instead of following the map's texels. ground: the share of puddle ground filtered the same way
// (1 on the open ground or without a map, 0 on karts), saturating early so the puddles reach under the edge.
half MHKWetUncovered(float3 positionWS, out half ground)
{
    ground = 1;
    UNITY_BRANCH
    if (_MHKWetCoverHeight.w < 0.5) return 1;
    float texel = _MHKWetCoverTexels.z;
    float2 xz = positionWS.xz;
    // Texel space with the texel centres on whole numbers.
    float2 t = (xz - _MHKWetCoverRect.xy) * _MHKWetCoverRect.zw * _MHKWetCoverTexels.xy - 0.5;
    float reach = clamp(texel * MHK_SHELTER_REACH.x, MHK_SHELTER_REACH.y, MHK_SHELTER_REACH.z);
    float2 warp = MHKWetNoise2(xz / MHK_SHELTER_BAY) + MHKWetNoise2(xz / MHK_SHELTER_PATCH + 5.1) * 1.1;
    t += warp * (2.0 * reach / texel);
    int2 c = int2(round(t));
    float r = min(MHK_SHELTER_FADE / texel, 1.0);
    float3 wx = MHKShelterWeights(t.x - c.x, r);
    float3 wz = MHKShelterWeights(t.y - c.y, r);
    half2 open = 0;
    [unroll] for (int z = 0; z < 3; z++)
    {
        [unroll] for (int x = 0; x < 3; x++)
            open += MHKWetCoverTexel(c + int2(x - 1, z - 1), positionWS.y) * half(wx[x] * wz[z]);
    }
    // The threshold keeps clear of 0 and 1, so the open ground stays wet and the deep cover dry.
    float patch = MHKWetNoise(xz / 3.0 + float2(7.7, 3.1)) * 0.65 + MHKWetNoise(xz / 1.05 + float2(1.9, 11.3)) * 0.35;
    float cut = lerp(MHK_SHELTER_CUT.x, MHK_SHELTER_CUT.y, smoothstep(0.25, 0.75, patch));
    ground = smoothstep(0.0, MHK_PUDDLE_REACH * lerp(0.6, 1.4, patch), open.y);
    return smoothstep(cut - MHK_SHELTER_CUT.z, cut + MHK_SHELTER_CUT.z, open.x);
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

// Two ripple layers at world xz, faded out where the rings get finer than the pixels (rippleWidth: fwidth of
// xz * MHK_RIPPLE_SCALE, taken before any per-fragment branch). xy = slope, z = crest.
half3 MHKRipples(float2 xz, float rippleWidth)
{
    float2 rp = xz * MHK_RIPPLE_SCALE;
    return (MHKRippleLayer(rp, _Time.y) + MHKRippleLayer(rp * 1.37 + 0.5, _Time.y * 1.13 + 0.37))
        * (1.0 - smoothstep(0.03, 0.09, rippleWidth));
}

#endif
