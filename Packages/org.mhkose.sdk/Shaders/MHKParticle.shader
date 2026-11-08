// Alpha-blended particles of Particle Systems (billboards, stretched billboards) lit like the weather (MHKParticleLight.hlsl:
// scene light, a share of the point and spot lights such as headlight beams, the lightning flash), so they dim at night
// and glow in a beam instead of shining in their unlit colour. Vertex colour x texture; fogged; particles closer to the
// camera than _NearFade fade out, so a cloud drifting over the camera does not fill the screen.
Shader "MHK/Particle"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1,1,1,1)
        _SceneLight ("Scene light (0 = unlit colour, 1 = colour x ambient + sun)", Range(0, 1)) = 1
        _LampLight ("Lamp light (share of point and spot light colour x attenuation added: headlight beams)", Range(0, 2)) = 0.6
        _NearFade ("Near fade (m): transparent at the camera, opaque from this distance on", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "MHKAmbient.hlsl"
            #include "MHKParticleLight.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _SceneLight;
                half _LampLight;
                float _NearFade;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float3 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                half fogFactor : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 world = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(world);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                half near = saturate(distance(world, _WorldSpaceCameraPos) / max(_NearFade, 1e-3));
                half3 light = MHKParticleLight(world, o.positionCS, _SceneLight, _LampLight);
                o.color = half4(v.color.rgb * _BaseColor.rgb * light, v.color.a * _BaseColor.a * near * near);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * i.color;
                col.rgb = MixFog(col.rgb, i.fogFactor);
                return col;
            }
            ENDHLSL
        }
    }
}
