// C-015 (слово Автора 15.11: «кружок с цифрами без мыла»): круглые части интерфейса рисуются на GPU, без растровых
// текстур круга. Четырёхугольник UiShape (uGUI-графика) несёт uv0 = -1…1 по своей стороне, TEXCOORD1 = (вид, уровень);
// форма — расстояние со знаком (SDF), край сглаживается через fwidth ровно в 1 экранный пиксель на любом разрешении
// (1080p, 1440p, 4K) и при любом масштабе холста. Виды: 0 — круг цвета вершины (точки мини-карты); 1 — сфера здоровья /
// выносливости целиком (тёмная кайма, золотой обод с объёмом, тёмное стекло, «жидкость» цвета вершины до уровня с
// линией поверхности, блик); 2 — замок закрытой способности.
// Совместим с uGUI: трафарет масок (_Stencil…), прямоугольник обрезки (_ClipRect, RectMask2D), прозрачность группы
// (альфа вершины). Цвета — в sRGB, как у картинок интерфейса; проект в линейном пространстве — переводим здесь.
// Лежит в Assets/Resources — так шейдер гарантированно попадает в сборку (UiShape.Material → Shader.Find).
Shader "Orpheus/UiShape"
{
    Properties
    {
        [PerRendererData] _MainTex ("Unused", 2D) = "white" {}
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "UiShape"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _ClipRect;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 data : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 data : TEXCOORD1;
                float2 local : TEXCOORD2;
            };

            float3 ToLinear(float3 c)
            {
            #if defined(UNITY_COLORSPACE_GAMMA)
                return c;
            #else
                return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045));
            #endif
            }

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.color = float4(ToLinear(v.color.rgb), v.color.a);
                o.uv = v.uv;
                o.data = v.data;
                o.local = v.positionOS.xy;
                return o;
            }

            // Покрытие по расстоянию со знаком d (< 0 — внутри): переход шириной в 1 экранный пиксель.
            float Cover(float d)
            {
                float w = max(fwidth(d), 1e-5);
                return saturate(0.5 - d / w);
            }

            // Слой поверх накопленного (цвет не умножен на альфу, результат — умноженный).
            void Over(inout float4 acc, float3 rgb, float a)
            {
                acc.rgb = rgb * a + acc.rgb * (1.0 - a);
                acc.a = a + acc.a * (1.0 - a);
            }

            float4 Disc(float2 p, float4 col)
            {
                float r = length(p);
                float a = Cover(r - 1.0 + 0.5 * fwidth(r)) * col.a; // внешний край сглаживания — ровно на краю четырёхугольника
                return float4(col.rgb * a, a);
            }

            float4 Orb(float2 p, float4 col, float level)
            {
                float r = length(p);
                float4 acc = 0;

                // Тёмная кайма (внешний край шара) — весь круг.
                Over(acc, ToLinear(float3(0.149, 0.110, 0.094)), Cover(r - 1.0 + 0.5 * fwidth(r)));

                // Золотой обод: светлее сверху, тонкая светлая фаска по внутреннему краю, тень по внешнему.
                const float ringOut = 0.955, ringIn = 0.865;
                float t = saturate(p.y * 0.5 + 0.5);
                float3 goldDark = ToLinear(float3(0.588, 0.408, 0.118));
                float3 gold = ToLinear(float3(1.0, 0.80, 0.282));
                float band = saturate((r - ringIn) / (ringOut - ringIn));      // 0 — внутренний край, 1 — внешний
                float bevel = 1.0 - abs(band * 2.0 - 1.0);                     // ярче посередине обода
                float3 ring = lerp(goldDark, gold, t) * (0.72 + 0.42 * bevel);
                Over(acc, ring, Cover(r - ringOut));

                // Тёмное стекло внутри обода.
                float inside = Cover(r - ringIn);
                Over(acc, ToLinear(float3(0.06, 0.05, 0.12)), inside * 0.86);

                // «Жидкость» до уровня: светлее в середине, темнее к краю и книзу; уровень — снизу вверх по кругу жидкости.
                const float liq = 0.835;
                float2 q = p / liq;
                float rq = length(q);
                float surface = -1.0 + 2.0 * level;                            // верх жидкости в координатах круга жидкости
                float inLiquid = Cover(rq - 1.0) * Cover(q.y - surface) * step(0.0005, level);
                float light = 0.62 + 0.5 * (1.0 - rq * rq) - 0.18 * (1.0 - (q.y + 1.0) * 0.5);
                Over(acc, saturate(col.rgb * light), inLiquid);

                // Линия поверхности: светлая полоска шириной ~1,5 пикселя под уровнем (не у полного шара).
                float px = max(fwidth(q.y), 1e-5);
                float lineA = saturate(1.0 - abs(q.y - surface + 1.5 * px) / (1.5 * px)) * Cover(rq - 1.0) * step(level, 0.995) * step(0.0005, level);
                Over(acc, saturate(col.rgb * 1.6 + 0.15), lineA * 0.85);

                // Тень у внутреннего края обода (объём стекла).
                float shade = saturate((r - (ringIn - 0.12)) / 0.12) * inside;
                Over(acc, float3(0, 0, 0), shade * 0.35);

                // Блик: овал слева сверху с чётким краем, внутри — плавно гаснет книзу.
                float2 e = (p - float2(-0.27, 0.40)) / float2(0.40, 0.22);
                float ed = (length(e) - 1.0) * 0.22;                          // в единицах радиуса шара
                float fade = saturate(1.0 - (1.0 - e.y) * 0.35);       // сверху 1, книзу ~0,3
                Over(acc, float3(1, 1, 1), Cover(ed) * 0.42 * fade);
                // Маленькая точка блика.
                Over(acc, float3(1, 1, 1), Cover(length(p - float2(0.18, 0.60)) - 0.055) * 0.55);

                return acc * col.a;
            }

            // Скруглённый прямоугольник: центр c, полуразмеры h, радиус rr.
            float Box(float2 p, float2 c, float2 h, float rr)
            {
                float2 d = abs(p - c) - h + rr;
                return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - rr;
            }

            float4 Lock(float2 p, float4 col)
            {
                float body = Box(p, float2(0, -0.30), float2(0.46, 0.33), 0.08);
                float ringD = abs(length(p - float2(0, 0.08)) - 0.29) - 0.075;  // кольцо дужки
                float shackle = max(ringD, -(p.y - 0.03));                       // только верхняя половина
                float hole = min(length(p - float2(0, -0.22)) - 0.09, Box(p, float2(0, -0.36), float2(0.035, 0.12), 0.02));
                float d = max(min(body, shackle), -hole);
                float a = Cover(d) * col.a;
                return float4(col.rgb * a, a);
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float kind = i.data.x;
                float4 c;
                if (kind < 0.5)
                {
                    c = Disc(i.uv, i.color);
                }
                else if (kind < 1.5)
                {
                    c = Orb(i.uv, i.color, i.data.y);
                }
                else
                {
                    c = Lock(i.uv, i.color);
                }

            #if defined(UNITY_UI_CLIP_RECT)
                float2 inside = step(_ClipRect.xy, i.local) * step(i.local, _ClipRect.zw);
                c *= inside.x * inside.y;
            #endif
                return c;
            }
            ENDHLSL
        }
    }
}
