// Наложение текстуры теней на землю (E1-7): один большой квадрат перед камерой, между землёй и объектами.
// Текстура теней (_OrphShadowTex) покрывает прямоугольник мира _OrphShadowRect (xy — левый нижний угол, zw — 1 / размер).
// Тень — постоянный тёмный цвет с прозрачностью «сила × маска»: где теней несколько, темнее не становится.
// C-017: каналы текстуры теней — R солнце, G огонь, B пятно у ног. Солнечная тень × сила солнца и не ложится в закрытой
// комнате (B сетки света DayTintView, по ячейкам 0,5 — без размытия: край ровно по стене); тень огня × сила огня и
// только там, куда свет источника доходит с учётом стен (G сетки) — за стену не попадает.
Shader "Orpheus/ShadowComposite"
{
    Properties
    {
        _Color ("Shadow color (a = strength)", Color) = (0.05, 0.07, 0.16, 0.42)
        _Fire ("Fire shadow strength", Range(0, 1)) = 0.6
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_OrphShadowTex);
    SAMPLER(sampler_OrphShadowTex);
    float4 _OrphShadowRect;
    TEXTURE2D(_OrphLightGrid);
    SAMPLER(sampler_OrphLightGrid);
    float4 _OrphLightRect; // xy — левый нижний угол сетки в мире, zw — 1 / размер
    float4 _OrphLightSize; // xy — точек сетки, zw — 1 / xy
    float4 _OrphSun;       // z — сила солнца, w — сила огня

    CBUFFER_START(UnityPerMaterial)
        half4 _Color;
        half _Fire;
    CBUFFER_END

    struct Attributes
    {
        float3 positionOS : POSITION;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        float2 grid : TEXCOORD1;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(wp);
        o.uv = (wp.xy - _OrphShadowRect.xy) * _OrphShadowRect.zw;
        o.grid = (wp.xy - _OrphLightRect.xy) * _OrphLightRect.zw;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half3 m = SAMPLE_TEXTURE2D(_OrphShadowTex, sampler_OrphShadowTex, i.uv).rgb;
        // Комната — точка сетки без смешивания (середина своей ячейки): край маски ровно по стене.
        float2 cell = (floor(i.grid * _OrphLightSize.xy) + 0.5) * _OrphLightSize.zw;
        half room = SAMPLE_TEXTURE2D_LOD(_OrphLightGrid, sampler_OrphLightGrid, cell, 0).b;
        half light = SAMPLE_TEXTURE2D_LOD(_OrphLightGrid, sampler_OrphLightGrid, i.grid, 0).g;
        // C-049: тень огня дышит вместе со светом — та же формула мерцания, что в DayTint (синхронны,
        // несоизмеримые частоты, без анимации по кадрам симуляции).
        light *= 1.0 + 0.05 * sin(_Time.y * 9.0) + 0.035 * sin(_Time.y * 14.7 + 1.3);
        half sun = m.r * _OrphSun.z * (1 - room);
        half fire = m.g * _OrphSun.w * saturate(light * 4) * (_Fire / max(_Color.a, 0.01));
        half a = max(max(sun, fire), m.b);
        // За краем текстуры теней (обзор шире неё) — без тени.
        float2 inside = step(0.0, i.uv) * step(i.uv, 1.0);
        return half4(_Color.rgb, saturate(_Color.a * a) * inside.x * inside.y);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ShadowComposite2D"
            Tags { "LightMode" = "Universal2D" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha, Zero One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCompositeForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha, Zero One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
