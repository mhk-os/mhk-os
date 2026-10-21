#ifndef MHK_AMBIENT_INCLUDED
#define MHK_AMBIENT_INCLUDED

// Globals set by MHK.Lighting.AmbientCube (the scene's ambient probe as six axis colours, baked probes loaded) and
// BakedIndirectLighting (x = on, y = intensity, z = minimum).
float4 _MHKAmbientCube[6];
float _MHKProbeAmbient;
float4 _MHKBakedIndirect;

#define MHK_ON(value) ((value) > 0.5)

half3 MHKAmbientCube(half3 n)
{
    half3 w = n * n;
    return w.x * (n.x >= 0 ? _MHKAmbientCube[0].rgb : _MHKAmbientCube[1].rgb)
         + w.y * (n.y >= 0 ? _MHKAmbientCube[2].rgb : _MHKAmbientCube[3].rgb)
         + w.z * (n.z >= 0 ? _MHKAmbientCube[4].rgb : _MHKAmbientCube[5].rgb);
}

// Sky-weighted average for unlit-normal effects: up counts twice, down not at all.
half3 MHKAmbientAverage()
{
    return (_MHKAmbientCube[0].rgb + _MHKAmbientCube[1].rgb + _MHKAmbientCube[2].rgb * 2 + _MHKAmbientCube[4].rgb + _MHKAmbientCube[5].rgb) / 6;
}

#endif
