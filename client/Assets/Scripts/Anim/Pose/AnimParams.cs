#nullable enable

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Все числа процедурной анимации (карточка K-001, шаг 7) — одним местом: амплитуды, частоты, длительности, цвета.
    /// Расстояния — в тайлах (1 тайл = 48 px экрана при 1080p и зуме ×1,0), углы — в градусах, время — в секундах.
    /// Числа предварительные, подбирались по кадрам <c>work/screens/K-001_*.png</c>.
    /// Цвета — из палитры стиля Б (<c>art/style/STYLE.md</c>, раздел 5), RGBA 0…1.
    /// </summary>
    public static class AnimParams
    {
        // ---------- ходьба и дыхание ----------

        /// <summary>Скорость, при которой ходьба «полная» (базовая скорость персонажа 6 т/с, `02` 2.1).</summary>
        public const float WalkFullSpeed = 6f;

        /// <summary>Ниже этой скорости персонаж считается стоящим (зеркало не меняется, шаг не идёт).</summary>
        public const float WalkMinSpeed = 0.4f;

        /// <summary>Шагов в секунду на полной скорости; частота растёт со скоростью.</summary>
        public const float StepHz = 4.2f;

        /// <summary>Подскок на шаге, тайлов (0,07 т ≈ 3,4 px при 48 px на тайл).</summary>
        public const float BobHeight = 0.07f;

        /// <summary>Наклон по направлению движения на полной скорости, градусов.</summary>
        public const float LeanDeg = 6f;

        /// <summary>Покачивание с ноги на ногу, градусов.</summary>
        public const float SwayDeg = 3f;

        /// <summary>Приседание в момент касания ногой земли (доля высоты).</summary>
        public const float StepSquash = 0.04f;

        /// <summary>Сглаживание скорости для ходьбы, 1/с (больше — резче).</summary>
        public const float SpeedSmoothing = 14f;

        /// <summary>Дыхание на месте: доля высоты и частота.</summary>
        public const float BreathAmp = 0.018f;
        public const float BreathHz = 0.45f;

        /// <summary>Исходный спрайт смотрит вправо (заглушка Орфея повёрнута вправо) — зеркалим, когда движение влево.</summary>
        public static readonly bool SpriteFacesRight = true;

        /// <summary>Горизонтальная составляющая прицела/направления, ниже которой зеркало не меняется.</summary>
        public const float FacingDeadZone = 0.2f;

        // ---------- уворот (`02` 2.2: 3 т за 0,25 с, неуязвимость 0,20 с) ----------

        /// <summary>Вытягивание по направлению рывка (доля) и сужение поперёк.</summary>
        public const float DodgeStretch = 0.28f;
        public const float DodgeThin = 0.12f;

        /// <summary>Наклон в рывке, градусов.</summary>
        public const float DodgeLeanDeg = 14f;

        /// <summary>«Призраки» — следы за персонажем в рывке: число, шаг между ними (тайлов), прозрачность первого.</summary>
        public const int GhostCount = 3;
        public const float GhostSpacing = 0.42f;
        public const float GhostAlpha = 0.45f;

        /// <summary>След гаснет после конца рывка за столько секунд.</summary>
        public const float GhostFadeSec = 0.12f;

        /// <summary>Цвет призраков — светлая бирюза палитры (#71D9A7).</summary>
        public static readonly float[] GhostTint = { 0.443f, 0.851f, 0.655f, 1f };

        /// <summary>Неуязвимость: мигание (Гц) между двумя уровнями прозрачности (`02` 2.2 — полупрозрачность на это время).</summary>
        public const float InvulnBlinkHz = 16f;
        public const float InvulnAlphaLow = 0.35f;
        public const float InvulnAlphaHigh = 0.75f;

        /// <summary>Чужие: уворот не приходит в снимке — считается рывком, если скорость выше этой (ходьба 6, рывок 12 т/с).</summary>
        public const float RemoteDashSpeed = 9f;

        // ---------- замах, удар, отход (`02` 3.1, враг: 0,40 / 0,10 / 0,60 с) ----------

        /// <summary>Отклонение назад к концу замаха, тайлов, и наклон назад, градусов.</summary>
        public const float WindupBack = 0.16f;
        public const float WindupLeanDeg = 12f;

        /// <summary>Приседание в замахе (доля высоты).</summary>
        public const float WindupSquash = 0.07f;

        /// <summary>Дрожь в последней четверти замаха — «вот-вот ударит»: амплитуда (тайлов) и частота.</summary>
        public const float WindupShake = 0.018f;
        public const float WindupShakeHz = 28f;

        /// <summary>Рывок вперёд в момент удара, тайлов, и наклон вперёд, градусов.</summary>
        public const float StrikeLunge = 0.32f;
        public const float StrikeLeanDeg = 16f;

        /// <summary>Длительности удара и отхода — как у врага в ядре (`EnemyMelee`: 6 и 36 кадров 60 Гц).</summary>
        public const float StrikeSec = 0.10f;
        public const float RecoverSec = 0.60f;

        /// <summary>Сектор удара на земле: доля прозрачности заливки и обода, толщина обода (доля радиуса).</summary>
        public const float SectorFillAlpha = 0.42f;
        public const float SectorRimAlpha = 0.9f;
        public const float SectorRimWidth = 0.07f;

        /// <summary>Сектор и обод — тёплый красный (#953920), рамка лент кадров — контурный сливовый (#381125), вспышка удара — золото (#EBB229).</summary>
        public static readonly float[] SectorFill = { 0.584f, 0.224f, 0.125f, 1f };
        public static readonly float[] SectorRim = { 0.584f, 0.224f, 0.125f, 1f };
        public static readonly float[] Outline = { 0.220f, 0.067f, 0.145f, 1f };
        public static readonly float[] StrikeFlash = { 0.922f, 0.698f, 0.161f, 1f };

        /// <summary>Сектор лежит на земле: центр — у ступней (спрайт опирается на 0,4 т ниже центра существа), тайлов.</summary>
        public const float SectorGroundY = -0.32f;

        /// <summary>Вспышка сектора в момент удара гаснет за столько секунд.</summary>
        public const float StrikeFlashSec = 0.18f;

        // ---------- попадание ----------

        /// <summary>Красная вспышка у получившего удар, секунд; цвет-множитель (оставляет красный канал).</summary>
        public const float HitFlashSec = 0.22f;
        public static readonly float[] HitTint = { 1f, 0.28f, 0.24f, 1f };

        /// <summary>Отдача: сдвиг спрайта от атакующего (тайлов: 0,08 т ≈ 4 px при 48 px на тайл) и время возврата.</summary>
        public const float HitKnock = 0.08f;
        public const float HitKnockSec = 0.14f;

        /// <summary>Искры попадания: число, скорость (т/с), жизнь (с); цвет — золото и светлый известняк (#DEE9C3).</summary>
        public const int HitSparks = 10;
        public const float HitSparkSpeed = 3.2f;
        public const float HitSparkLife = 0.28f;
        public static readonly float[] SparkLight = { 0.871f, 0.914f, 0.765f, 1f };

        // ---------- копание (`05` 1.5) ----------

        /// <summary>Толчков инструментом на один тайл и пределы периода толчка, секунд.</summary>
        public const int DigPokesPerTile = 3;
        public const float DigPokeMinSec = 0.18f;
        public const float DigPokeMaxSec = 0.42f;

        /// <summary>Длина толчка к тайлу, тайлов, наклон, градусов, и доля периода на выпад (остальное — возврат).</summary>
        public const float DigPoke = 0.18f;
        public const float DigLeanDeg = 9f;
        public const float DigThrustShare = 0.3f;

        /// <summary>Пыль и щепки на каждый толчок: число, скорость (т/с), жизнь (с).</summary>
        public const int DigDust = 7;
        public const float DigDustSpeed = 1.8f;
        public const float DigDustLife = 0.45f;

        /// <summary>Щепки (кора #A17E62), каменная крошка (бирюза #3B9385), пыль (светлый известняк #DEE9C3).</summary>
        public static readonly float[] DustWood = { 0.631f, 0.494f, 0.384f, 1f };
        public static readonly float[] DustStone = { 0.231f, 0.576f, 0.522f, 1f };
        public static readonly float[] DustLight = { 0.871f, 0.914f, 0.765f, 1f };

        /// <summary>Частицы «падают» вниз экрана (вид 3/4), т/с².</summary>
        public const float ParticleGravity = 7f;

        /// <summary>Размер частицы, тайлов.</summary>
        public const float ParticleSize = 0.14f;
    }
}
