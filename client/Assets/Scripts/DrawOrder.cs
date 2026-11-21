#nullable enable
using System;

namespace Orpheus.Client
{
    /// <summary>
    /// C-049: чистая математика порядка отрисовки «с высотой» (без UnityEngine —linked в тесты).
    /// Существа получают чётные порядки по ногам (SortOrderAt), строки объектов — нечётные
    /// (RowOrderAt: существо в той же четверти — ПОЗАДИ строки), а «низкая полоса»
    /// (LowRowOrderAt: костёр — камни и низ пламени) — на два шага ПЕРЕД существом той же
    /// четверти: персонаж, стоящий у костра, не тонет в огне (ноги и торс поверх огня),
    /// а персонаж севернее костра — за ним, южнее — перед ним (слово Автора 21.11).
    /// </summary>
    public static class DrawOrder
    {
        /// <summary>Середина полосы порядков для всего «с высотой».</summary>
        public const int TallOrder = 6000;

        /// <summary>Предел полосы: ±2800 шагов по четверти тайла (полоса TallOrder ± 2·2800).</summary>
        public const int SortClamp = 2800;

        /// <summary>
        /// C-049: опора рисунка строки — на этой высоте от нижнего края тайла (в тайлах). Правило
        /// Автора («южнее середины костра — перед ним, севернее — за») держится на этом числе:
        /// полоса квантования (шаг — четверть тайла) у опоры 0,3 кончается ровно в центре тайла,
        /// поэтому переворот «перед/за» происходит в центре тайла костра. Правка числа красит
        /// C049DrawOrderTests (клиентский `ObjectTable.BaseLift` = это же число).
        /// </summary>
        public const float RowBaseLift = 0.3f;

        /// <summary>
        /// C-049: опора порядка строки ly чанка с корнем на <paramref name="rootY"/> (Unity Y):
        /// нижний край строки = rootY − ly − 1, плюс подъём объекта <see cref="RowBaseLift"/>.
        /// </summary>
        public static float RowAnchorY(float rootY, int ly, float baseLift = RowBaseLift) => rootY - ly - 1f + baseLift;

        /// <summary>Порядок существа с ногами на высоте y: ниже на экране — позже; шаг — четверть тайла.</summary>
        public static int SortOrderAt(float y)
        {
            int step = (int)Math.Floor(-y * 4f);
            if (step > SortClamp) step = SortClamp;
            if (step < -SortClamp) step = -SortClamp;
            return TallOrder + 2 * step;
        }

        /// <summary>Порядок строки объектов с опорой на высоте y: существо в той же четверти — позади строки.</summary>
        public static int RowOrderAt(float y) => SortOrderAt(y) + 1;

        /// <summary>
        /// C-049: порядок «низкой полосы» строки (костёр): на шаг ПЕРЕД существом той же четверти
        /// (персонаж у костра — поверх огня), нечётный — никогда не совпадает с порядком существа
        /// (у существ чётные), существо севернее середины костра — за ним, южнее — перед ним.
        /// </summary>
        public static int LowRowOrderAt(float y) => SortOrderAt(y) - 1;
    }
}
