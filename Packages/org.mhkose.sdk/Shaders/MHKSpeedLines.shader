// Screen-edge speed effect of MHK.Effects.SpeedLines: an elliptical edge mask zoom-blurs the picture towards the centre
// and lays outward-moving streak dashes over it. Pixels outside the mask return the source with no further reads.
Shader "Hidden/MHK/SpeedLines"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "SpeedLines"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #define SPEED_MIN_STRENGTH 0.002
            #define STREAK_WIDTH_MIN 0.5
            #define STREAK_LENGTH_MIN 0.6
            #define STREAK_LENGTH_VARIATION 0.8
            #define DASH_FADE_IN 0.25
            #define DASH_FADE_OUT 0.6

            // Set by SpeedLines.UploadParameters. Intensity: 0 off, 1 full, more while boosting.
            // Mask x inner, y outer radius (1 = left / right edge), z top / bottom weight, w aspect. Blur x length, y samples.
            // Streaks x lanes, y share drawn, z half-width (share of a lane), w dash length. Motion x dashes per screen
            // height, y phase, z phase period. Color rgb, a opacity at full strength.
            float _SpeedIntensity;
            float4 _SpeedMask;
            float4 _SpeedBlur;
            float4 _SpeedStreaks;
            float4 _SpeedStreakMotion;
            half4 _SpeedStreakColor;

            float3 Hash3(float2 p)
            {
                float3 q = frac(p.xyx * float3(0.1031, 0.1030, 0.0973));
                q += dot(q, q.yxz + 33.33);
                return frac((q.xxy + q.yzz) * q.zyx);
            }

            float SpeedEdgeMask(float2 c)
            {
                float edgeDistance = length(float2(c.x * 2.0, c.y * 2.0 * _SpeedMask.z));
                return smoothstep(_SpeedMask.x, _SpeedMask.y, edgeDistance);
            }

            half3 ZoomBlur(float2 uv, float2 c, float k, half3 color)
            {
                int samples = max(1, (int)_SpeedBlur.y);
                float2 stepUv = -c * (_SpeedBlur.x * k / samples);
                half3 sum = color;
                UNITY_LOOP
                for (int i = 1; i < samples; i++)
                    sum += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + stepUv * i, 0).rgb;
                return sum / samples;
            }

            float Streak(float2 c, float pixel, float k)
            {
                float2 p = float2(c.x * _SpeedMask.w, c.y);
                float r = max(length(p), 1e-4);
                float lanes = _SpeedStreaks.x;
                float s = (atan2(p.y, p.x) * (0.5 * INV_PI) + 0.5) * lanes;
                float lane = fmod(floor(s), lanes);
                float across = abs(frac(s) - 0.5);
                float along = r * _SpeedStreakMotion.x - _SpeedStreakMotion.y;
                float dash = floor(along);
                float3 h = Hash3(float2(lane, fmod(dash + _SpeedStreakMotion.z, _SpeedStreakMotion.z)));
                float aa = pixel / r * lanes * (0.5 * INV_PI);
                float halfWidth = _SpeedStreaks.z * (STREAK_WIDTH_MIN + h.y);
                float profile = 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, across);
                float f = frac(along);
                float len = _SpeedStreaks.w * (STREAK_LENGTH_MIN + STREAK_LENGTH_VARIATION * h.z);
                float dashShape = smoothstep(0.0, DASH_FADE_IN * len, f) * (1.0 - smoothstep(DASH_FADE_OUT * len, len, f));
                float drawn = step(h.x, _SpeedStreaks.y * _SpeedIntensity);
                return drawn * profile * dashShape * saturate(k) * _SpeedStreakColor.a;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half3 color = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
                float2 c = uv - 0.5;
                // One pixel in screen-height units, taken before the branch (derivatives are undefined inside it).
                float pixel = fwidth(uv.y);
                float k = SpeedEdgeMask(c) * _SpeedIntensity;
                UNITY_BRANCH
                if (k <= SPEED_MIN_STRENGTH)
                    return half4(color, 1);
                color = ZoomBlur(uv, c, k, color);
                color = lerp(color, _SpeedStreakColor.rgb, saturate(Streak(c, pixel, k)));
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
}
