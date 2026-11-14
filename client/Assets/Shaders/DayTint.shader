// Тон суток и свет источников (E1-8, план 1.8): один большой квадрат перед камерой поверх всего мира, цвет кадра
// умножается на тон. Тон = цвет неба (_OrphAmbient: день — белый, сумерки — тёплый, ночь — тёмно-синий, рассвет —
// холодно-розовый), а в круге света источника (факел, костёр — _OrphLights: xy — место в мире, z — радиус, w — сила)
// плавно переходит в тёплый свет _OrphLightColor. Днём свет источников не виден: тон и так белый.
Shader "Orpheus/DayTint"
{
    Properties
    {
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    #define ORPH_MAX_LIGHTS 32

    half4 _OrphAmbient;
    half4 _OrphLightColor;
    float4 _OrphLights[ORPH_MAX_LIGHTS];
    float _OrphLightCount;

    struct Attributes
    {
        float3 positionOS : POSITION;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 world : TEXCOORD0;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(wp);
        o.world = wp.xy;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half lit = 0;
        int n = (int)_OrphLightCount;
        for (int k = 0; k < ORPH_MAX_LIGHTS; k++)
        {
            if (k >= n)
            {
                break;
            }

            float4 l = _OrphLights[k];
            // Земля видна под наклоном: круг света чуть сплюснут по вертикали.
            float2 d = (i.world - l.xy) * float2(1.0, 1.25);
            half f = saturate(1.0 - length(d) / l.z);
            f = f * f * (3.0 - 2.0 * f);
            lit = max(lit, f * l.w);
        }

        half3 warm = max(_OrphAmbient.rgb, _OrphLightColor.rgb);
        return half4(lerp(_OrphAmbient.rgb, warm, lit), 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "DayTint2D"
            Tags { "LightMode" = "Universal2D" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend DstColor Zero, Zero One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "DayTintForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend DstColor Zero, Zero One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
