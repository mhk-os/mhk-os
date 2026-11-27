// Stripes of TV static around a jammed kart (MHK.Karts.KartGlitch): a camera-facing quad shows horizontal rows of snow,
// some rows on and some off, a new pattern for every seed. The rows fade towards the quad's oval edge.
Shader "MHK/GlitchStatic"
{
    Properties
    {
        [HideInInspector] _GlitchStatic ("Strength / seed / rows / snow columns", Vector) = (1,0,26,90)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _GlitchStatic;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            float3 Hash3(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float strength = _GlitchStatic.x;
                float seed = _GlitchStatic.y;
                float rowCoord = i.uv.y * _GlitchStatic.z;
                float row = floor(rowCoord);
                float3 r = Hash3(float3(row, seed, 2.3));
                // About a third of the rows show, each a little thinner than its slot and shifted sideways.
                half on = step(0.62, r.x) * step(abs(frac(rowCoord) - 0.5), 0.2 + 0.25 * r.y);
                float x = i.uv.x + (r.z - 0.5) * 0.3;
                half snow = Hash3(float3(floor(x * _GlitchStatic.w), row, seed)).x;
                float2 oval = (i.uv - 0.5) * 2.0;
                half edge = 1.0 - smoothstep(0.55, 1.0, length(oval * float2(1.0, 0.85)));
                half3 tint = r.y > 0.5 ? half3(0.75, 1.0, 1.0) : half3(1.0, 0.8, 1.0);
                half alpha = on * edge * strength * (0.35 + 0.65 * snow);
                return half4(lerp(0.15, 1.0, snow) * tint, alpha);
            }
            ENDHLSL
        }
    }
}
