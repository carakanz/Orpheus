// Тени объектов и существ (E1-7, план 1.7): силуэты из листа картинок, меш на чанк (ObjectMesh) и пятна под
// персонажами. Рисуются отдельной камерой в текстуру теней (WorldFx); смешивание — «наибольшее из перекрытий»
// (BlendOp Max), поэтому тени соседних деревьев не складываются в чёрное. На землю текстура ложится один раз —
// Orpheus/ShadowComposite. Картинка берётся с размытого mip-уровня: край тени мягкий.
// E1-11: тень качается вместе с кроной — uv1.xy — опора, uv1.z — высота точки над опорой, uv1.w — ветер (OrphLife.hlsl).
// C-017: тени по солнцу и от огня. uv2.x — «солнечная высота» точки (тайлов × длина тени предмета): точка ложится на
// землю со сдвигом uv2.x × _OrphSun.xy (вектор солнца по времени суток, SunTable) — прямоугольник становится
// параллелограммом и поворачивается вместе с солнцем; uv2.y — канал: 0 — солнце (R текстуры теней), 1 — огонь (G, меш
// собран на CPU прочь от источника), 2 — пятно у ног (B, всегда). Каналы смешиваются «наибольшим» каждый сам по себе.
Shader "Orpheus/Shadow"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Soft ("Softness (mip bias)", Range(0, 4)) = 1.5
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Assets/Shaders/OrphLife.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    CBUFFER_START(UnityPerMaterial)
        float4 _MainTex_ST;
        float _Soft;
    CBUFFER_END

    struct Attributes
    {
        float3 positionOS : POSITION;
        float2 uv : TEXCOORD0;
        float4 info : TEXCOORD1;
        float2 sun : TEXCOORD2;
        half4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        half3 strength : TEXCOORD1;
    };

    float4 _OrphSun; // xy — сдвиг тени на тайл солнечной высоты (Unity: x — восток, y — север), z — сила солнца, w — сила огня

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        float sway, phase;
        OrphUnpack(v.info.w, sway, phase);
        wp.xy += OrphSway(TransformObjectToWorld(float3(v.info.xy, 0)).xy, v.info.z, sway, phase);
        wp.xy += v.sun.x * _OrphSun.xy;
        o.positionCS = TransformWorldToHClip(wp);
        o.uv = v.uv;
        half3 channel = half3(v.sun.y < 0.5, abs(v.sun.y - 1) < 0.5, v.sun.y > 1.5);
        o.strength = channel * v.color.a;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half a = SAMPLE_TEXTURE2D_BIAS(_MainTex, sampler_MainTex, i.uv, _Soft).a;
        return half4(a * i.strength, 0);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Shadow2D"
            Tags { "LightMode" = "Universal2D" }
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "ShadowForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
