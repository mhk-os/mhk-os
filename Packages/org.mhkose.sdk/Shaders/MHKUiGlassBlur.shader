// Dual-filter blur of MHK.UI.UiGlassBlur: Down halves the picture with a 5-tap tent, Up doubles it with an 8-tap ring.
// A few levels down and back up give a wide, smooth blur for the menus' glass plates at a fraction of the screen size.
Shader "Hidden/MHK/UiGlassBlur"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float2 SourceTexel()
        {
            float w, h;
            _BlitTexture.GetDimensions(w, h);
            return 1.0 / float2(w, h);
        }

        half3 Tap(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
        }
        ENDHLSL

        Pass
        {
            Name "Down"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDown

            half4 FragDown(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 t = SourceTexel();
                half3 sum = Tap(uv) * 4.0;
                sum += Tap(uv + float2(-t.x, -t.y));
                sum += Tap(uv + float2(t.x, -t.y));
                sum += Tap(uv + float2(-t.x, t.y));
                sum += Tap(uv + float2(t.x, t.y));
                return half4(sum * 0.125, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Up"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUp

            half4 FragUp(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 t = SourceTexel() * 0.5;
                half3 sum = Tap(uv + float2(-t.x * 2.0, 0.0));
                sum += Tap(uv + float2(t.x * 2.0, 0.0));
                sum += Tap(uv + float2(0.0, -t.y * 2.0));
                sum += Tap(uv + float2(0.0, t.y * 2.0));
                sum += Tap(uv + float2(-t.x, t.y)) * 2.0;
                sum += Tap(uv + float2(t.x, t.y)) * 2.0;
                sum += Tap(uv + float2(-t.x, -t.y)) * 2.0;
                sum += Tap(uv + float2(t.x, -t.y)) * 2.0;
                return half4(sum / 12.0, 1.0);
            }
            ENDHLSL
        }
    }
}
