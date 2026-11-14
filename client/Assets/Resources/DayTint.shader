// Тон суток (план 1.8, карточка E1-8; правка E1-7): полноэкранный слой над миром, кадр УМНОЖАЕТСЯ на тон.
// _MainTex — сетка тайлов камеры: R = k биома (по полу), G = свет источников (обе в 1/255), билинейная —
// свет факела и костра кругом, без клеточных границ.
// Тон = _Sky (цвет неба по времени суток: день — белый, сумерки — тёплый, ночь — тёмно-синий, рассвет —
// холодно-розовый) × k (поправка леса; днём выключена: _Day = 1 → множитель ровно 1, день без изменений);
// в круге света источника тон плавно уходит в тёплый _Fire (не темнее неба).
// Шейдер лежит в Assets/Resources — так он гарантированно попадает в сборку.
Shader "Orpheus/DayTint"
{
    Properties
    {
        _MainTex ("L grid (R = k, G = sources)", 2D) = "white" {}
        _Day ("Daylight (0 night .. 1 day)", Range(0, 1)) = 1
        _Sky ("Sky tone (multiplier)", Vector) = (1, 1, 1, 1)
        _Fire ("Fire light tone (multiplier)", Vector) = (1, 0.88, 0.66, 1)
        _GridRect ("Grid rect in world", Vector) = (0, 0, 1, 1)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    CBUFFER_START(UnityPerMaterial)
        float _Day;
        float4 _Sky;
        float4 _Fire;
        float4 _GridRect; // xy — левый нижний угол сетки в мире, zw — 1 / размер
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS.xyz);
        o.positionCS = TransformWorldToHClip(wp);
        o.uv = (wp.xy - _GridRect.xy) * _GridRect.zw;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half2 g = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rg;
        half k = lerp(g.r, 1.0, _Day);
        half3 tone = _Sky.rgb * k;
        half lit = saturate(g.g * 1.15);
        lit = lit * lit * (3.0 - 2.0 * lit);
        return half4(lerp(tone, max(tone, _Fire.rgb), lit), 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

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
