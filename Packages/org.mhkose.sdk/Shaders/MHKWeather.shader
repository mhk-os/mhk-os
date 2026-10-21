// Weather particles (snow, rain) of MHK.Effects.WeatherEffect: one static quad mesh animated on the GPU. Seeds fill a box
// that wraps around the camera, so no CPU simulation is needed and particles never pop. The cover map hides particles under roofs.
Shader "MHK/Weather"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture (white, soft alpha)", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1,1,1,0.8)
        _SceneLight ("Scene light (0 = unlit colour, 1 = colour x ambient + sun)", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _SceneLight;
        CBUFFER_END
        ENDHLSL

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
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "MHKAmbient.hlsl"

            #define WEATHER_SWAY_STEPS 8.0
            #define WEATHER_NEAR_FADE_START 0.5
            #define WEATHER_SUN_LIGHT_SHARE 0.5
            #define WEATHER_MIN_ALPHA 0.002
            #define WEATHER_CULLED_POSITION float4(0, 0, -2, 1)

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MHKWeatherCover);
            SAMPLER(sampler_MHKWeatherCover);

            // Set every frame by WeatherEffect (MaterialPropertyBlock):
            // Area xyz box size, w 1 / edge fade width. Center xyz box offset in the camera's yaw frame, w near fade (m).
            // Offset xyz fall + wind displacement, w sway phase. Velocity xyz fall + wind (m/s), w sway amplitude (m).
            // CameraVelocity xyz camera velocity x stretch. Size x width, y length, z size variation, w streaks.
            // CoverRect xy map minimum, zw 1 / size. CoverHeight x height of 0, y range, z bias, w 1 / fade height.
            float4 _WeatherArea;
            float4 _WeatherCenter;
            float4 _WeatherOffset;
            float4 _WeatherVelocity;
            float4 _WeatherCameraVelocity;
            float4 _WeatherSize;
            float4 _WeatherCoverRect;
            float4 _WeatherCoverHeight;
            float _WeatherCoverOn;

            struct Attributes
            {
                float3 seedInBox : POSITION;
                float4 cornerSpeedRandom : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                half fogFactor : TEXCOORD1;
            };

            float3 Wrap(float3 p, float3 size) { return p - floor(p / size) * size; }

            // The box reaches further ahead of the camera's yaw than behind it (WeatherEffect repeats this for its bounds).
            float3 WeatherBoxCenter(float3 camPos)
            {
                float2 f2 = -UNITY_MATRIX_V[2].xz;
                f2 = dot(f2, f2) > 1e-6 ? normalize(f2) : float2(0, 1);
                return camPos + float3(f2.y, 0, -f2.x) * _WeatherCenter.x + float3(0, _WeatherCenter.y, 0) + float3(f2.x, 0, f2.y) * _WeatherCenter.z;
            }

            // Sway frequencies are multiples of 1 / WEATHER_SWAY_STEPS so WeatherEffect can wrap the phase without a jump.
            float3 WeatherParticlePosition(Attributes v, float3 center, float3 size)
            {
                float k = v.cornerSpeedRandom.z;
                float r = v.cornerSpeedRandom.w;
                float3 p = v.seedInBox * size + _WeatherOffset.xyz * k;
                float swayMul = (6.0 + floor(frac(r * 7.13) * 5.0)) / WEATHER_SWAY_STEPS;
                float a = _WeatherOffset.w * swayMul + r * TWO_PI;
                p.xz += float2(sin(a), cos(a)) * _WeatherVelocity.w;
                float3 boxMin = center - size * 0.5;
                return boxMin + Wrap(p - boxMin, size);
            }

            half WeatherCover(float3 world)
            {
                float2 cuv = (world.xz - _WeatherCoverRect.xy) * _WeatherCoverRect.zw;
                if (any(cuv <= 0.0) || any(cuv >= 1.0)) return 1;
                float h = SAMPLE_TEXTURE2D_LOD(_MHKWeatherCover, sampler_MHKWeatherCover, cuv, 0).r * _WeatherCoverHeight.y + _WeatherCoverHeight.x;
                return saturate((world.y - h - _WeatherCoverHeight.z) * _WeatherCoverHeight.w);
            }

            half WeatherFade(float3 world, float3 center, float3 size, float dist)
            {
                float3 d = abs(world - center) / (size * 0.5);
                half alpha = saturate((1.0 - max(d.x, max(d.y, d.z))) * _WeatherArea.w);
                alpha *= saturate(dist / max(_WeatherCenter.w, 1e-3) - WEATHER_NEAR_FADE_START);
                UNITY_BRANCH
                if (_WeatherCoverOn > 0.5) alpha *= WeatherCover(world);
                return alpha;
            }

            void WeatherQuadAxes(float k, float3 viewDir, out float3 right, out float3 up)
            {
                right = UNITY_MATRIX_V[0].xyz;
                up = UNITY_MATRIX_V[1].xyz;
                UNITY_BRANCH
                if (_WeatherSize.w > 0.5)
                {
                    float3 vel = _WeatherVelocity.xyz * k - _WeatherCameraVelocity.xyz;
                    float3 axis = vel - dot(vel, viewDir) * viewDir;
                    float len = length(axis);
                    if (len > 1e-4)
                    {
                        up = axis / len;
                        right = normalize(cross(up, viewDir));
                    }
                }
            }

            half3 WeatherSceneLight()
            {
                half3 light = 1;
                UNITY_BRANCH
                if (_SceneLight > 0)
                    light = lerp(half3(1, 1, 1), saturate(MHKAmbientAverage() + _MainLightColor.rgb * WEATHER_SUN_LIGHT_SHARE), _SceneLight);
                return light;
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 camPos = _WorldSpaceCameraPos;
                float3 center = WeatherBoxCenter(camPos);
                float3 size = _WeatherArea.xyz;
                float3 world = WeatherParticlePosition(v, center, size);
                float3 toCam = camPos - world;
                float dist = length(toCam);
                half alpha = WeatherFade(world, center, size, dist);

                float r = v.cornerSpeedRandom.w;
                float sizeMul = 1.0 + _WeatherSize.z * (frac(r * 13.37) * 2.0 - 1.0);
                float3 right, up;
                WeatherQuadAxes(v.cornerSpeedRandom.z, toCam / max(dist, 1e-4), right, up);
                float2 corner = v.cornerSpeedRandom.xy;
                world += right * (corner.x * 0.5 * _WeatherSize.x * sizeMul) + up * (corner.y * 0.5 * _WeatherSize.y * sizeMul);

                o.positionCS = alpha > WEATHER_MIN_ALPHA ? TransformWorldToHClip(world) : WEATHER_CULLED_POSITION;
                float2 quadUV = corner * 0.5 + 0.5;
                o.uv = TRANSFORM_TEX(quadUV, _BaseMap);
                o.color = half4(_BaseColor.rgb * WeatherSceneLight(), _BaseColor.a * alpha);
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

        Pass
        {
            Name "Cover"
            Tags { "LightMode" = "MHKWeatherCover" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 _WeatherCoverEncode;

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float height : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 world = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(world);
                o.height = saturate((world.y - _WeatherCoverEncode.x) * _WeatherCoverEncode.y);
                return o;
            }

            float4 frag(Varyings i) : SV_Target { return i.height; }
            ENDHLSL
        }
    }
}
