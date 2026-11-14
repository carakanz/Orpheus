// Тени объектов и существ (E1-7, план 1.7): силуэты из листа картинок, меш на чанк (ObjectMesh) и пятна под
// персонажами. Рисуются отдельной камерой в текстуру теней (WorldFx); смешивание — «наибольшее из перекрытий»
// (BlendOp Max), поэтому тени соседних деревьев не складываются в чёрное. На землю текстура ложится один раз —
// Orpheus/ShadowComposite. Картинка берётся с размытого mip-уровня: край тени мягкий.
Shader "Orpheus/Shadow"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Soft ("Softness (mip bias)", Range(0, 4)) = 1.5
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
        half4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        half strength : TEXCOORD1;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        o.positionCS = TransformObjectToHClip(v.positionOS);
        o.uv = v.uv;
        o.strength = v.color.a;
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half a = SAMPLE_TEXTURE2D_BIAS(_MainTex, sampler_MainTex, i.uv, _Soft).a * i.strength;
        return half4(a, a, a, a);
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
