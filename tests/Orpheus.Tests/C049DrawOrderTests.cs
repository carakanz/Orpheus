using Orpheus.Client;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-049: «почему костёр надо мной оказался» (слово Автора 21.11). Поставленный костёр рисовался
    /// в общем меше строки объектов (RowOrderAt = SortOrderAt + 1) — существо в той же четверти тайла
    /// оказывался ПОЗАДИ строки, и огонь перекрывал ноги и торс персонажа. Починка: «низкая полоса»
    /// (LowRowOrderAt = SortOrderAt − 1, нечётный) — перед существом той же четверти.
    /// Проверка чистая (DrawOrder без UnityEngine, linked в csproj).
    /// </summary>
    public class C049DrawOrderTests
    {
        // Опора строки объектов — нижний край тайла + BaseLift (0,3). Персонаж в тайле костра —
        // ноги у центра тайла (+0,5 от края); севернее — на тайл выше (+1,5); южнее — на тайл ниже (−0,5).
        const float Edge = 100f;          // нижний край тайла костра (условная высота Unity)
        static readonly float Row = Edge + ObjectTableBaseLift; // опора рисунка строки
        const float ObjectTableBaseLift = 0.3f; // ObjectTable.BaseLift — синхронизировано с клиентом

        [Fact]
        public static void Standing_Actors_In_Front_Of_Fire()
        {
            // Персонаж стоит в тайле костра (ноги в центре): должен рисоваться ПЕРЕД огнём.
            Assert.True(DrawOrder.LowRowOrderAt(Row) < DrawOrder.SortOrderAt(Edge + 0.5f),
                "огонь костра перекрывает персонажа, стоящего в его тайле");
        }

        [Fact]
        public static void North_Actors_Behind_Fire()
        {
            // Персонаж севернее костра (на тайл выше): остаётся за ним.
            Assert.True(DrawOrder.SortOrderAt(Edge + 1.5f) < DrawOrder.LowRowOrderAt(Row),
                "персонаж севернее костра рисуются перед огнём");
        }

        [Fact]
        public static void South_Actors_In_Front_Of_Fire()
        {
            // Персонаж южнее костра: перед ним.
            Assert.True(DrawOrder.LowRowOrderAt(Row) < DrawOrder.SortOrderAt(Edge - 0.5f),
                "персонаж южнее костра рисуются за огнём");
        }

        [Fact]
        public static void Mid_Quarter_North_Of_Fire_Centre_Behind()
        {
            // Ноги на 3/4 тайла костра (севернее середины огня): по правилу Автора — за костром.
            Assert.True(DrawOrder.SortOrderAt(Edge + 0.75f) < DrawOrder.LowRowOrderAt(Row),
                "персонаж севернее середины костра рисуются перед огнём");
        }

        [Fact]
        public static void Low_Order_Never_Ties_A_Actor()
        {
            // Порядки существ чётные; «низкая полоса» — нечётная: ничьих (порядок рисовки двух
            // рендереров с равным порядком не определён) быть не должно ни в одной четверти.
            for (int q = -40; q <= 40; q++)
            {
                float y = Edge + q * 0.25f;
                int low = DrawOrder.LowRowOrderAt(Row);
                Assert.NotEqual(low, DrawOrder.SortOrderAt(y));
                Assert.NotEqual(low, DrawOrder.RowOrderAt(Row)); // и со своей строкой не пересекается
            }
        }

        [Fact]
        public static void Old_Row_Order_Reproduces_Defect()
        {
            // Прежний порядок (общая строка): существо той же четверти — позади строки → огонь поверх ног.
            Assert.True(DrawOrder.RowOrderAt(Row) > DrawOrder.SortOrderAt(Edge + 0.5f),
                "общая строка больше не перекрывает стоящего — тест дефекта надо пересматривать");
        }
    }
}
