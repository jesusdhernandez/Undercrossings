// Whole-camera assistance redistributes hard-to-distinguish colors; HUD text stays unfiltered.
Shader "Worldsides/Color Assistance"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            // A fullscreen blit covers the image without reading or modifying scene depth.
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Mode 0 is an identity copy; 1/2/3 assist protanopia/deuteranopia/tritanopia.
            int _AccessibilityMode;
            float _AccessibilityStrength;

            // Compare the original image with an approximate dichromacy model to find lost contrast.
            half4 Frag(Varyings input) : SV_Target
            {
                half4 original = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
                float3 c = original.rgb;

                // Approximate dichromacy, then redistribute the lost channel into
                // distinguishable channels. Assistance, rather than simulation.
                float3 simulated = c;

                // Each matrix approximates which color differences are lost for one vision type.
                if (_AccessibilityMode == 1)
                    simulated = float3(
                        0.567 * c.r + 0.433 * c.g,
                        0.558 * c.r + 0.442 * c.g,
                        0.242 * c.g + 0.758 * c.b
                    );

                if (_AccessibilityMode == 2)
                    simulated = float3(
                        0.625 * c.r + 0.375 * c.g,
                        0.7 * c.r + 0.3 * c.g,
                        0.3 * c.g + 0.7 * c.b
                    );

                if (_AccessibilityMode == 3)
                    simulated = float3(
                        0.95 * c.r + 0.05 * c.g,
                        0.433 * c.g + 0.567 * c.b,
                        0.475 * c.g + 0.525 * c.b
                    );

                // Shift lost contrast into other channels, then blend it by the user's strength slider.
                float3 error = c - simulated;
                float3 correction = _AccessibilityMode == 3
                    ? float3(error.b * 0.7, error.b * 0.7, 0)
                    : float3(0, error.r * 0.7 + error.g, error.r * 0.7 + error.b);

                // Off forces zero correction so toggling a mode never permanently recolors the source.
                return half4(
                    lerp(c, saturate(c + correction), _AccessibilityMode == 0 ? 0 : saturate(_AccessibilityStrength)),
                    original.a
                );
            }
            ENDHLSL
        }
    }
}
