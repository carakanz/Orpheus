// Объекты «с высотой» (E1-7, план 1.7): деревья, кусты, валуны, руда, пни — квадраты из общего листа картинок,
// меш на строку тайлов чанка (ObjectMesh). uv — место в листе, color — оттенок, uv1.x — высота опоры объекта в меше,
// uv1.y — 1 у высокого объекта (дерево).
// Свой персонаж за кроной: у высокого объекта, чья опора ниже ног персонажа (объект ближе к зрителю), точки в круге
// _OrphHero.w вокруг персонажа становятся полупрозрачными — персонаж виден сквозь крону.
Shader "Orpheus/Objects"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    CBUFFER_START(UnityPerMaterial)
        float4 _MainTex_ST;
    CBUFFER_END

    // Задаёт WorldFx каждый кадр: xy — середина персонажа в мире, z — высота его ног, w — радиус круга, тайлов (0 — выключено).
    float4 _OrphHero;
    // x — наименьшая непрозрачность кроны над персонажем.
    float4 _OrphHeroFade;

    struct Attributes
    {
        float3 positionOS : POSITION;
        float2 uv : TEXCOORD0;
        float2 info : TEXCOORD1;
        half4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        half4 color : COLOR;
        float3 world : TEXCOORD1; // xy — точка мира, z — 1, если объект может закрыть персонажа
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(wp);
        o.uv = v.uv;
        o.color = v.color;
        float baseY = TransformObjectToWorld(float3(0, v.info.x, 0)).y;
        o.world = float3(wp.xy, (v.info.y > 0.5 && baseY < _OrphHero.z && _OrphHero.w > 0) ? 1 : 0);
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
        if (i.world.z > 0.5)
        {
            float d = distance(i.world.xy, _OrphHero.xy);
            c.a *= lerp(_OrphHeroFade.x, 1.0, smoothstep(_OrphHero.w * 0.55, _OrphHero.w, d));
        }

        return c;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Objects2D"
            Tags { "LightMode" = "Universal2D" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "ObjectsForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
