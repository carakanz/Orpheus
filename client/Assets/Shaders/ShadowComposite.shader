// Наложение текстуры теней на землю (E1-7): один большой квадрат перед камерой, между землёй и объектами.
// Текстура теней (_OrphShadowTex) покрывает прямоугольник мира _OrphShadowRect (xy — левый нижний угол, zw — 1 / размер).
// Тень — постоянный тёмный цвет с прозрачностью «сила × маска»: где теней несколько, темнее не становится.
Shader "Orpheus/ShadowComposite"
{
    Properties
    {
        _Color ("Shadow color (a = strength)", Color) = (0.05, 0.07, 0.16, 0.42)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_OrphShadowTex);
    SAMPLER(sampler_OrphShadowTex);
    float4 _OrphShadowRect;

    CBUFFER_START(UnityPerMaterial)
        half4 _Color;
    CBUFFER_END

    struct Attributes
    {
        float3 positionOS : POSITION;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(wp);
        o.uv = (wp.xy - _OrphShadowRect.xy) * _OrphShadowRect.zw;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half m = SAMPLE_TEXTURE2D(_OrphShadowTex, sampler_OrphShadowTex, i.uv).r;
        // За краем текстуры теней (обзор шире неё) — без тени.
        float2 inside = step(0.0, i.uv) * step(i.uv, 1.0);
        return half4(_Color.rgb, _Color.a * m * inside.x * inside.y);
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
