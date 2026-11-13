// Земля и вода одним мешем на чанк (E1-4, план 1.4–1.5).
// Меш — «двойная сетка»: квадрат между центрами четырёх тайлов; в вершинах — номера слоёв этих тайлов (ids),
// uv.zw — место внутри квадрата (веса углов), uv.xy — координата в тайлах мира (Y вниз) для картинок и шума.
// Номер слоя = приоритет: 0 глубина, 1 отмель, дальше суша (GroundTable.Layers).
// Граница: у каждого слоя вес (билинейный) + шум своего слоя; рисуется слой с наибольшей суммой — граница рваная,
// без «мыла»; по краю верхнего слоя — тонкая тёмная кромка; у воды рядом с сушей — светлая линия берега.
Shader "Orpheus/Ground"
{
    Properties
    {
        _Layers ("Layers", 2DArray) = "" {}
        _Noise ("Noise (R edge, G mix, B tone)", 2D) = "gray" {}
        _TexTiles ("Tiles per texture", Float) = 4
        _EdgeNoise ("Edge noise", Range(0, 0.8)) = 0.7
        _Rim ("Rim width", Range(0, 0.5)) = 0.11
        _RimDark ("Rim darkness", Range(0, 1)) = 0.62
        _Foam ("Shore line width", Range(0, 0.6)) = 0.2
        _FoamColor ("Shore line color", Color) = (0.86, 0.97, 0.95, 1)
        _WaterLayers ("Water layers", Float) = 2
        _Variation ("Tone variation", Range(0, 0.3)) = 0.06
        _Mix ("Anti-repeat mix", Range(0, 1)) = 1
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D_ARRAY(_Layers);
    SAMPLER(sampler_Layers);
    TEXTURE2D(_Noise);
    SAMPLER(sampler_Noise);

    CBUFFER_START(UnityPerMaterial)
        float _TexTiles;
        float _EdgeNoise;
        float _Rim;
        float _RimDark;
        float _Foam;
        half4 _FoamColor;
        float _WaterLayers;
        float _Variation;
        float _Mix;
    CBUFFER_END

    struct Attributes
    {
        float3 positionOS : POSITION;
        float4 uv : TEXCOORD0;
        float4 ids : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float4 uv : TEXCOORD0;
        nointerpolation float4 ids : TEXCOORD1;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        o.positionCS = TransformObjectToHClip(v.positionOS);
        o.uv = v.uv;
        o.ids = v.ids;
        return o;
    }

    // Картинка слоя в точке мира. Вода колышется; суша против повтора 4 × 4 смешивается со своей же копией
    // (оси переставлены, сдвиг) по крупным пятнам шума и слегка меняет тон.
    half3 Layer(float id, float2 wp, float2 dx, float2 dy, half3 low)
    {
        float2 uv = float2(wp.x, -wp.y) / _TexTiles;
        if (id < _WaterLayers)
        {
            float t = _Time.y;
            uv += float2(sin(t * 0.9 + wp.y * 1.7), cos(t * 0.7 + wp.x * 1.3)) * 0.018 + t * float2(0.012, 0.007);
            return SAMPLE_TEXTURE2D_ARRAY_GRAD(_Layers, sampler_Layers, uv, id, dx, dy).rgb;
        }

        half3 a = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Layers, sampler_Layers, uv, id, dx, dy).rgb;
        // Копия: вдвое крупнее (8 × 8 тайлов на картинку), зеркало по X и сдвиг — пятна того же наклона, но другого
        // размера и в других местах; области копии — крупные пятна шума с неровным краем.
        half3 b = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Layers, sampler_Layers, float2(-uv.x, uv.y) * 0.5 + float2(0.37, 0.61), id, dx * 0.5, dy * 0.5).rgb;
        half edge = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_Noise, wp / 16.0, 0).r;
        half m = smoothstep(0.49, 0.51, low.g + (edge - 0.5) * 0.14) * _Mix;
        half3 col = lerp(a, b, m);
        return col * (1.0 + (low.b - 0.5) * 2.0 * _Variation);
    }

    half4 Frag(Varyings i) : SV_Target
    {
        float2 wp = i.uv.xy;
        float2 c = i.uv.zw;
        float4 id = round(i.ids * 255.0);
        float2 dx = ddx(wp) / _TexTiles;
        float2 dy = ddy(wp) / _TexTiles;
        dx.y = -dx.y;
        dy.y = -dy.y;
        half3 low = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_Noise, wp / 64.0, 0).rgb;

        // Все четыре тайла одного вида — середина пятна, границы нет.
        if (id.x == id.y && id.x == id.z && id.x == id.w)
        {
            return half4(Layer(id.x, wp, dx, dy, low), 1);
        }

        float4 w = float4((1 - c.x) * (1 - c.y), c.x * (1 - c.y), (1 - c.x) * c.y, c.x * c.y);
        // Сумма весов слоя каждого угла (одинаковые слои складываются) + шум этого слоя + малая прибавка по приоритету.
        float4 s;
        s.x = dot(w, 1.0 - step(0.5, abs(id - id.x)));
        s.y = dot(w, 1.0 - step(0.5, abs(id - id.y)));
        s.z = dot(w, 1.0 - step(0.5, abs(id - id.z)));
        s.w = dot(w, 1.0 - step(0.5, abs(id - id.w)));
        float2 nuv = wp / 16.0;
        float4 n;
        n.x = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_Noise, nuv + id.x * float2(0.37, 0.61), 0).r;
        n.y = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_Noise, nuv + id.y * float2(0.37, 0.61), 0).r;
        n.z = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_Noise, nuv + id.z * float2(0.37, 0.61), 0).r;
        n.w = SAMPLE_TEXTURE2D_LOD(_Noise, sampler_Noise, nuv + id.w * float2(0.37, 0.61), 0).r;
        s += (n - 0.5) * _EdgeNoise + id * 0.012;

        float best = max(max(s.x, s.y), max(s.z, s.w));
        float bid = s.x >= best ? id.x : (s.y >= best ? id.y : (s.z >= best ? id.z : id.w));
        float4 s2 = lerp(float4(-10, -10, -10, -10), s, step(0.5, abs(id - bid)));
        float second = max(max(s2.x, s2.y), max(s2.z, s2.w));
        float sid = s2.x >= second ? id.x : (s2.y >= second ? id.y : (s2.z >= second ? id.z : id.w));
        float d = best - second; // 0 на границе, растёт вглубь своего слоя (≈ 2 на тайл)

        half3 colB = Layer(bid, wp, dx, dy, low);
        half3 colS = Layer(sid, wp, dx, dy, low);

        float aa = max(2.0 * fwidth(wp.x), 1e-4); // ширина пикселя в единицах d
        bool bWater = bid < _WaterLayers;
        bool sWater = sid < _WaterLayers;
        // Глубина ↔ отмель: мягче, без кромки. Остальное — жёсткий край в один пиксель.
        float soft = (bWater && sWater) ? 0.3 : aa;
        half3 col = lerp(colS, colB, saturate(0.5 + 0.5 * d / soft));

        if (!bWater && bid > sid)
        {
            // Тёмная кромка по краю слоя, лежащего сверху.
            float r = 1.0 - smoothstep(_Rim - aa, _Rim + aa, d);
            col = lerp(col, col * _RimDark, r);
        }
        else if (bWater && !sWater)
        {
            // Светлая линия берега на воде; ширина слегка дышит.
            float fw = _Foam * (0.8 + 0.3 * sin(_Time.y * 1.3 + low.r * 25.0));
            float f = 1.0 - smoothstep(fw - aa, fw + aa, d);
            col = lerp(col, _FoamColor.rgb, f * 0.85);
        }

        return half4(col, 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Ground2D"
            Tags { "LightMode" = "Universal2D" }
            Cull Off
            ZWrite Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "GroundForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
