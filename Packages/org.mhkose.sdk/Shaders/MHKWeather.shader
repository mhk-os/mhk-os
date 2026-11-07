// Weather particles (snow, rain, sand) of MHK.Effects.WeatherEffect: one static quad mesh animated on the GPU. Seeds fill a
// box that wraps around the camera, so no CPU simulation is needed and particles never pop. The cover map hides particles
// under roofs. With _WEATHER_SPLASH (WeatherEffect's splash material) the quads are drop splashes landing on the cover
// map's surfaces. Particles are lit per vertex (they are small): scene light, a share of the point and spot lights
// (headlight beams, lamps; no shadows) and the lightning flash (_MHKFlash).
Shader "MHK/Weather"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture (white, soft alpha)", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1,1,1,0.8)
        _SceneLight ("Scene light (0 = unlit colour, 1 = colour x ambient + sun)", Range(0, 1)) = 0.5
        _LampLight ("Lamp light (share of point and spot light colour x attenuation added: headlight beams)", Range(0, 2)) = 0.5
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
            half _LampLight;
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
            #pragma multi_compile_local _ _WEATHER_SPLASH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "MHKAmbient.hlsl"

            #define WEATHER_SWAY_STEPS 8.0
            #define WEATHER_NEAR_FADE_START 0.5
            #define WEATHER_SUN_LIGHT_SHARE 0.5
            #define WEATHER_FLASH_SHARE 0.6
            #define WEATHER_MIN_ALPHA 0.002
            #define WEATHER_CULLED_POSITION float4(0, 0, -2, 1)
            #define WEATHER_GOLDEN_RATIO 0.6180339
            // Splashes: landings per splash before its spot sequence repeats (WeatherEffect wraps its clock there), height
            // step (m) between the nearest and the filtered cover height that marks an edge without splashes, sink (m) of
            // the quad's foot below the surface, crown droplets.
            #define WEATHER_SPLASH_CYCLES 4096.0
            #define WEATHER_SPLASH_STEP 0.25
            #define WEATHER_SPLASH_SINK 0.03
            #define WEATHER_SPLASH_DROPS 6

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MHKWeatherCover);
            SAMPLER(sampler_MHKWeatherCover);

            // Set every frame by WeatherEffect (MaterialPropertyBlock):
            // Area xyz box size, w 1 / edge fade width. Center xyz box offset in the camera's yaw frame, w near fade (m).
            // Offset xyz fall + wind displacement, w sway phase. Velocity xyz fall + wind (m/s), w sway amplitude (m).
            // CameraVelocity xyz camera velocity x stretch. Size x width, y length, z size variation, w streaks.
            // Near x share of the particles in the near box, y its scale (1 / whole number: the motion wraps seamlessly).
            // CoverRect xy map minimum, zw 1 / size. CoverHeight x height of 0, y range, z bias, w 1 / fade height.
            // Splash x side of the square (m), y its centre ahead of the camera (m), z life (s), w clock (s).
            // SplashSize x width, y height (m).
            float4 _WeatherArea;
            float4 _WeatherCenter;
            float4 _WeatherOffset;
            float4 _WeatherVelocity;
            float4 _WeatherCameraVelocity;
            float4 _WeatherSize;
            float4 _WeatherNear;
            float4 _WeatherCoverRect;
            float4 _WeatherCoverHeight;
            float _WeatherCoverOn;
            float4 _WeatherSplash;
            float4 _WeatherSplashSize;

            struct Attributes
            {
                float3 seedInBox : POSITION;
                float4 cornerSpeedRandom : TEXCOORD0;
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                // xy texture uv (splash: x across -1..1, y up 0..1), z splash life 0..1.
                float3 uv : TEXCOORD0;
                half4 color : COLOR;
                half fogFactor : TEXCOORD1;
            };

            float3 Wrap(float3 p, float3 size) { return p - floor(p / size) * size; }

            // The camera's heading on the ground plane.
            float2 WeatherHeading()
            {
                float2 f2 = -UNITY_MATRIX_V[2].xz;
                return dot(f2, f2) > 1e-6 ? normalize(f2) : float2(0, 1);
            }

            // The box reaches further ahead of the camera's yaw than behind it (WeatherEffect repeats this for its bounds).
            // The near box is the volume scaled by scale around the camera.
            float3 WeatherBoxCenter(float3 camPos, float scale)
            {
                float2 f2 = WeatherHeading();
                float3 c = _WeatherCenter.xyz * scale;
                return camPos + float3(f2.y, 0, -f2.x) * c.x + float3(0, c.y, 0) + float3(f2.x, 0, f2.y) * c.z;
            }

            half3 WeatherSceneLight()
            {
                half3 light = 1;
                UNITY_BRANCH
                if (_SceneLight > 0)
                    light = lerp(half3(1, 1, 1), saturate(MHKAmbientAverage() + _MainLightColor.rgb * WEATHER_SUN_LIGHT_SHARE), _SceneLight);
                return light;
            }

            // Normalized screen position of a clip-space vertex, as GetNormalizedScreenSpaceUV gives it in a fragment
            // (clamped onto the screen: the cluster tiles of a vertex just outside it are its edge's).
            float2 WeatherScreenUV(float4 positionCS)
            {
                float2 uv = float2(positionCS.x, positionCS.y * _ProjectionParams.x) / max(positionCS.w, 1e-4) * 0.5 + 0.5;
                return clamp(uv, 0.0, 0.999);
            }

            // Point and spot lights at a particle (colour x distance and cone attenuation, no shadows, no normal). The
            // directional fills stay out: the scene light holds the sun.
            half3 WeatherLampLight(float3 world, float4 positionCS)
            {
                half3 light = 0;
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = world;
                    inputData.normalizedScreenSpaceUV = WeatherScreenUV(positionCS);
                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light l = GetAdditionalLight(lightIndex, world);
                        light += l.color * l.distanceAttenuation;
                    LIGHT_LOOP_END
                #endif
                return light;
            }

            // Light of a visible particle vertex: the scene light, the lamps' share (saturated: a particle in a beam
            // glows, it does not burn white) and the lightning flash.
            half3 WeatherLight(float3 world, float4 positionCS)
            {
                half3 lamps = 0;
                UNITY_BRANCH
                if (_LampLight > 0) lamps = saturate(WeatherLampLight(world, positionCS) * _LampLight);
                return WeatherSceneLight() + lamps + _MHKFlash.rgb * WEATHER_FLASH_SHARE;
            }

            #if defined(_WEATHER_SPLASH)

            // Height of the topmost surface under xz (the nearest cover texel). 0 outside the map, where nothing is and at
            // a height step (walls, roof edges), where the filtered height leaves the nearest one.
            half SplashGround(float2 xz, out float height)
            {
                height = 0;
                UNITY_BRANCH
                if (_WeatherCoverOn < 0.5) return 0;
                float2 cuv = (xz - _WeatherCoverRect.xy) * _WeatherCoverRect.zw;
                if (any(cuv <= 0.0) || any(cuv >= 1.0)) return 0;
                float nearest = SAMPLE_TEXTURE2D_LOD(_MHKWeatherCover, sampler_PointClamp, cuv, 0).r;
                float filtered = SAMPLE_TEXTURE2D_LOD(_MHKWeatherCover, sampler_MHKWeatherCover, cuv, 0).r;
                height = nearest * _WeatherCoverHeight.y + _WeatherCoverHeight.x;
                return nearest > 0.0 && abs(nearest - filtered) * _WeatherCoverHeight.y < WEATHER_SPLASH_STEP ? 1 : 0;
            }

            // Each landing hops to another spot of the splash's world-anchored tile, and tiles repeat around the camera,
            // so a splash stays where it landed while it lives.
            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 camPos = _WorldSpaceCameraPos;
                float r = v.cornerSpeedRandom.w;
                float cycle = _WeatherSplash.w / _WeatherSplash.z + r;
                float life = frac(cycle);
                float2 spot = frac(v.seedInBox.xz + float2(WEATHER_GOLDEN_RATIO, 0.4142136) * fmod(floor(cycle), WEATHER_SPLASH_CYCLES));
                float tile = _WeatherSplash.x;
                float2 boxCenter = camPos.xz + WeatherHeading() * _WeatherSplash.y;
                float2 boxMin = boxCenter - tile * 0.5;
                float2 p = spot * tile - boxMin;
                float2 xz = boxMin + p - floor(p / tile) * tile;

                float height;
                half alpha = SplashGround(xz, height);
                float3 world = float3(xz.x, height, xz.y);
                float2 d = abs(xz - boxCenter) / (tile * 0.5);
                alpha *= saturate((1.0 - max(d.x, d.y)) * _WeatherArea.w);
                float3 toCam = camPos - world;
                alpha *= saturate(length(toCam) / max(_WeatherCenter.w, 1e-3) - WEATHER_NEAR_FADE_START);
                alpha *= saturate((1.0 - abs(toCam.y) / (_WeatherArea.y * 0.5)) * _WeatherArea.w);

                float sizeMul = 1.0 + _WeatherSize.z * (frac(r * 13.37) * 2.0 - 1.0);
                float3 right = normalize(float3(UNITY_MATRIX_V[0].x, 0, UNITY_MATRIX_V[0].z) + float3(1e-5, 0, 0));
                float2 corner = v.cornerSpeedRandom.xy;
                float up = corner.y * 0.5 + 0.5;
                world += right * (corner.x * 0.5 * _WeatherSplashSize.x * sizeMul);
                world.y += up * _WeatherSplashSize.y * sizeMul - WEATHER_SPLASH_SINK;

                bool shown = alpha > WEATHER_MIN_ALPHA;
                o.positionCS = shown ? TransformWorldToHClip(world) : WEATHER_CULLED_POSITION;
                o.uv = float3(corner.x, up, life);
                half3 light = 0;
                UNITY_BRANCH
                if (shown) light = WeatherLight(world, o.positionCS);
                o.color = half4(_BaseColor.rgb * light, _BaseColor.a * alpha);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            // A drop's crown: droplets thrown out and up along arcs that spread and fall over the splash's life, above a
            // ring spreading on the ground. q: x across -1..1, y up 0..1.
            half WeatherSplashShape(float2 q, float life)
            {
                float spread = 0.25 + 0.75 * sqrt(life);
                float rise = 4.0 * life * (1.0 - life);
                half ring = saturate(1.0 - abs(length(float2(q.x, (q.y - 0.1) * 5.0)) - spread) * 8.0) * (1.0 - life);
                half drops = 0;
                [unroll] for (int k = 0; k < WEATHER_SPLASH_DROPS; k++)
                {
                    float side = (k + 0.5) / WEATHER_SPLASH_DROPS * 2.0 - 1.0;
                    float2 c = float2(side * spread, 0.1 + 0.8 * rise * (1.0 - side * side * 0.6));
                    drops = max(drops, saturate(1.0 - length((q - c) * float2(1.0, 1.25)) * 7.0));
                }
                return max(ring, drops) * (1.0 - life * life);
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 col = half4(i.color.rgb, i.color.a * WeatherSplashShape(i.uv.xy, i.uv.z));
                col.rgb = MixFog(col.rgb, i.fogFactor);
                return col;
            }

            #else

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

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 camPos = _WorldSpaceCameraPos;
                // Golden-ratio spacing over the particle index: any drawn prefix (quality tiers) keeps the near share.
                float scale = frac((v.vertexID >> 2) * WEATHER_GOLDEN_RATIO) < _WeatherNear.x ? _WeatherNear.y : 1.0;
                float3 center = WeatherBoxCenter(camPos, scale);
                float3 size = _WeatherArea.xyz * scale;
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

                bool shown = alpha > WEATHER_MIN_ALPHA;
                o.positionCS = shown ? TransformWorldToHClip(world) : WEATHER_CULLED_POSITION;
                float2 quadUV = corner * 0.5 + 0.5;
                o.uv = float3(TRANSFORM_TEX(quadUV, _BaseMap), 0);
                half3 light = 0;
                UNITY_BRANCH
                if (shown) light = WeatherLight(world, o.positionCS);
                o.color = half4(_BaseColor.rgb * light, _BaseColor.a * alpha);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv.xy) * i.color;
                col.rgb = MixFog(col.rgb, i.fogFactor);
                return col;
            }

            #endif
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
