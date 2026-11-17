// Объекты «с высотой» (E1-7, план 1.7): деревья, кусты, валуны, руда, пни — квадраты из общего листа картинок,
// меш на строку тайлов чанка (ObjectMesh). uv — место в листе, color — оттенок, uv1.xy — опора объекта в меше,
// uv1.z — 1 у высокого объекта (дерево), uv1.w — ветер: амплитуда на тайл высоты + фаза (OrphLife.hlsl; 0 — не качается).
// E1-11: верх объекта смещается ветром и толчками (низ на месте) — в вершинном шейдере.
// Свой персонаж за кроной: у высокого объекта, чья опора ниже ног персонажа (объект ближе к зрителю), точки в круге
// _OrphHero.w вокруг персонажа становятся полупрозрачными — персонаж виден сквозь крону.
// C-027 (слово Автора 17.11 «гора не должна становиться прозрачной»): у горы (uv1.z = 2) прозрачности нет — где гора закрывает
// своего персонажа, поверх неё рисуется его силуэт (заливка и светлый контур) по маске _OrphHeroMask (камера маски, WorldFx).
Shader "Orpheus/Objects"
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

    // Задаёт WorldFx каждый кадр: xy — середина персонажа в мире, z — высота его ног, w — радиус круга, тайлов (0 — выключено).
    float4 _OrphHero;
    // x — наименьшая непрозрачность кроны над персонажем.
    float4 _OrphHeroFade;
    // C-027: маска своего персонажа (альфа) — прямоугольник мира (xy — угол, zw — 1 / размер) и шаг точки маски (xy).
    TEXTURE2D(_OrphHeroMask);
    SAMPLER(sampler_OrphHeroMask);
    float4 _OrphHeroRect;
    float4 _OrphHeroTexel;
    // rgb — цвет заливки силуэта, a — её сила; цвет контура — _OrphHeroLine.
    float4 _OrphHeroFill;
    float4 _OrphHeroLine;

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
        float3 world : TEXCOORD1; // xy — точка мира, z — 1, если объект может закрыть персонажа (крона), 2 — гора перед ним
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 wp = TransformObjectToWorld(v.positionOS);
        float2 base = TransformObjectToWorld(float3(v.info.xy, 0)).xy;
        float sway, phase;
        OrphUnpack(v.info.w, sway, phase);
        wp.xy += OrphSway(base, v.positionOS.y - v.info.y, sway, phase);
        o.positionCS = TransformWorldToHClip(wp);
        o.uv = v.uv;
        o.color = v.color;
        o.world = float3(wp.xy, (v.info.z > 0.5 && base.y < _OrphHero.z && _OrphHero.w > 0) ? (v.info.z > 1.5 ? 2 : 1) : 0);
        return o;
    }

    half4 Frag(Varyings i) : SV_Target
    {
        half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
        if (i.world.z > 1.5)
        {
            // Гора сплошная; силуэт персонажа поверх (C-027).
            float2 uv = (i.world.xy - _OrphHeroRect.xy) * _OrphHeroRect.zw;
            float2 t = _OrphHeroTexel.xy * 1.5;
            half m = SAMPLE_TEXTURE2D(_OrphHeroMask, sampler_OrphHeroMask, uv).a;
            half n = max(max(SAMPLE_TEXTURE2D(_OrphHeroMask, sampler_OrphHeroMask, uv + float2(t.x, 0)).a,
                             SAMPLE_TEXTURE2D(_OrphHeroMask, sampler_OrphHeroMask, uv - float2(t.x, 0)).a),
                         max(SAMPLE_TEXTURE2D(_OrphHeroMask, sampler_OrphHeroMask, uv + float2(0, t.y)).a,
                             SAMPLE_TEXTURE2D(_OrphHeroMask, sampler_OrphHeroMask, uv - float2(0, t.y)).a));
            half fill = smoothstep(0.35, 0.65, m);
            half edge = saturate(smoothstep(0.2, 0.5, n) - fill);
            c.rgb = lerp(c.rgb, _OrphHeroFill.rgb, fill * _OrphHeroFill.a);
            c.rgb = lerp(c.rgb, _OrphHeroLine.rgb, edge * _OrphHeroLine.a);
        }
        else if (i.world.z > 0.5)
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
