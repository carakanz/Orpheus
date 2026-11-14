// «Живой мир» (E1-11, план 1.11–1.12): общий ветер, толчки деревьев и след в траве. Только вид.
// Задаёт LifeFx каждый кадр (глобальные параметры шейдеров):
//   _OrphWind      xy — куда дует (единичный вектор, плавно гуляет), z — сила 0…1,5, w — время ветра, с
//   _OrphOrigin    xy — сдвиг: точка Unity + сдвиг = непрерывная координата мира (не скачет при переезде начала)
//   _OrphPush[i]   xy — опора дерева/куста (Unity), z — секунд с толчка, w — сила (тайлов на тайл высоты; 0 — пусто)
//   _OrphPushDir[i] xy — направление толчка (от существа)
//   _OrphTrail     след в траве: 256 × 256, полтайла на точку, повтор каждые 128 тайлов; rg — куда легла трава, b — насколько
#ifndef ORPH_LIFE_INCLUDED
#define ORPH_LIFE_INCLUDED

#define ORPH_PUSH_COUNT 24

float4 _OrphWind;
float4 _OrphOrigin;
float4 _OrphPush[ORPH_PUSH_COUNT];
float4 _OrphPushDir[ORPH_PUSH_COUNT];
float4 _OrphGrass; // x — доля пучков, скрытых на дальнем обзоре (0…1), y — число активных толчков (первые в массиве)

TEXTURE2D(_OrphTrail);
SAMPLER(sampler_OrphTrail);

// Ветер в точке опоры: смещение верха на единицу амплитуды. Порыв — волна, бегущая по миру (фаза от координаты),
// поверх — мелкая дрожь со своей фазой у каждого растения.
float2 OrphWind(float2 baseUnity, float phase)
{
    float2 p = baseUnity + _OrphOrigin.xy;
    float t = _OrphWind.w;
    float2 d = _OrphWind.xy;
    float2 side = float2(-d.y, d.x);
    // Фронт порыва идёт с северо-запада на юго-восток (~7 тайлов/с), кромка фронта неровная.
    float along = p.x * 0.8 - p.y * 0.6;
    float across = p.x * 0.6 + p.y * 0.8;
    float gust = 0.5 + 0.5 * sin(along * 0.10 - t * 0.7 + 1.6 * sin(across * 0.06 + t * 0.11));
    gust *= gust;
    float ph = phase * 6.2831853;
    float flutter = 0.6 * sin(t * 2.1 + ph + along * 0.45) + 0.4 * sin(t * 3.4 + ph * 2.7);
    float lean = _OrphWind.z * (0.3 + 0.7 * gust);
    return d * (lean * (0.55 + 0.45 * flutter)) + side * (lean * 0.22 * sin(t * 1.6 + ph * 1.9));
}

// Толчки дерева с этой опорой: затухающее колебание от существа (сумма активных толчков), на тайл высоты.
float2 OrphPush(float2 baseUnity)
{
    float2 o = 0;
    int count = min((int)_OrphGrass.y, ORPH_PUSH_COUNT);
    for (int i = 0; i < count; i++)
    {
        float4 q = _OrphPush[i];
        float2 dd = baseUnity - q.xy;
        if (q.w > 0 && dot(dd, dd) < 0.0064)
        {
            o += _OrphPushDir[i].xy * (q.w * exp(-q.z * 3.6) * sin(q.z * 13.0 + 0.5));
        }
    }

    return o;
}

// Смещение точки объекта на высоте h над опорой: sway — амплитуда ветра на тайл высоты (0 — не качается).
float2 OrphSway(float2 baseUnity, float h, float sway, float phase)
{
    if (sway <= 0 || h <= 0.01)
    {
        return 0;
    }

    return (OrphWind(baseUnity, phase) * sway + OrphPush(baseUnity)) * h;
}

// Упаковка в вершине: целая часть — фаза × 64, дробная — амплитуда (< 1).
void OrphUnpack(float packed, out float sway, out float phase)
{
    sway = frac(packed);
    phase = floor(packed) / 64.0;
}

#endif
