// Тон суток (план 1.8, карточка E1-8): полноэкранный тёмный слой над миром.
// _MainTex — сетка тайлов камеры: R = k биома (по полу), G = свет источников (обе в 1/255), билинейная —
// свет факела и костра кругом, без клеточных границ. L = _Bg × R + G (всё в 0..1); альфа = (1 − L) × _Max.
// _Tint — цвет темноты: ночью холодный синий, в сумерках и на рассвете — тёплый (выбирает код по фазе).
// Шейдер лежит в Assets/Resources — так он гарантированно попадает в сборку (GROUND-шейдер включён
// ссылкой из GroundSet.asset, у тона такой ссылки нет).
Shader "Orpheus/DayTint"
{
    Properties
    {
        _MainTex ("L grid (R = k, G = sources)", 2D) = "white" {}
        _Bg ("Background (0..1)", Range(0, 1)) = 1
        _Tint ("Darkness color", Color) = (0.03, 0.06, 0.2, 1)
        _Max ("Max darkness alpha", Range(0, 1)) = 0.78
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    CBUFFER_START(UnityPerMaterial)
        float _Bg;
        half4 _Tint;
        float _Max;
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
        o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
        o.uv = v.uv;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half2 g = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rg;
        half l = saturate(g.r * _Bg + g.g);
        return half4(_Tint.rgb, (1.0 - l) * _Max);
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
            Blend SrcAlpha OneMinusSrcAlpha

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
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
