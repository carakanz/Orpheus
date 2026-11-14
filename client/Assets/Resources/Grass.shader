// Пучки травы и листья-частицы (E1-11, план 1.11–1.12): квадраты из листа, нарисованного кодом (GrassLayer.BuildAtlas).
// uv — место в листе, color — цвет пучка, uv1.xy — опора пучка в меше, uv1.z — случайная фаза 0…1, uv1.w — 1 у пучка
// (0 — частица: вершины не трогаются).
// Верх пучка смещается ветром (низ на месте) и ложится по следу существ: текстура следа _OrphTrail (LifeFx) — куда и
// насколько примята трава в этой точке мира; после ухода существа след гаснет, пучок распрямляется.
// Лежит в Resources: так шейдер попадает в сборку без ссылок из сцены.
Shader "Orpheus/Grass"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Assets/Shaders/OrphLife.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    CBUFFER_START(UnityPerMaterial)
        float4 _MainTex_ST;
    CBUFFER_END

    struct Attributes
    {
        float3 positionOS : POSITION;
        float2 uv : TEXCOORD0;
        float4 info : TEXCOORD1;
        half4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        half4 color : COLOR;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        half4 color = v.color;
        if (v.info.w > 0.5)
        {
            float2 base = TransformObjectToWorld(float3(v.info.xy, 0)).xy;
            float h = v.positionOS.y - v.info.y;
            float4 tr = SAMPLE_TEXTURE2D_LOD(_OrphTrail, sampler_OrphTrail, (base + _OrphOrigin.xy) / 128.0, 0);
            float a = smoothstep(0.0, 0.7, tr.b);
            color.rgb *= 1.0 - 0.3 * a; // примятая трава темнее: след читается и издали
            if (v.info.z < _OrphGrass.x)
            {
                wp.xy = base; // дальний обзор: часть пучков скрыта (квадрат схлопнут в точку)
            }
            else if (h > 0.01)
            {
                float2 dir = tr.rg * 2.0 - 1.0;
                float2 wind = OrphWind(base, v.info.z) * (0.30 * h);
                // Примятый пучок: верх уходит в сторону от существа и опускается к земле; ветер его почти не качает.
                wp.xy += wind * (1.0 - 0.8 * a) + dir * (a * h * 0.85);
                wp.y -= a * h * 0.45;
            }
        }

        o.positionCS = TransformWorldToHClip(wp);
        o.uv = v.uv;
        o.color = color;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Grass2D"
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
            Name "GrassForward"
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
