using System.Collections.Generic;

namespace Orpheus.Net.Session
{
    /// <summary>Куда идёт щелчок мыши (C-008).</summary>
    public enum PointerRoute : byte
    {
        /// <summary>В мир: копать, бить, ставить постройку, линейка — тем же путём, что при закрытых окнах.</summary>
        World = 0,

        /// <summary>В интерфейс: ячейка, кнопка, значок рецепта (или ЛКМ ещё держится после щелчка по интерфейсу).</summary>
        Ui,

        /// <summary>Мимо интерфейса с предметом «в руке курсора» — выбросить его (как в Terraria; допущение C-008).</summary>
        Drop,
    }

    /// <summary>Прямоугольник интерфейса в единицах холста (левый нижний угол, ширина, высота).</summary>
    public readonly struct UiZone
    {
        public readonly float X, Y, W, H;

        public UiZone(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public bool Contains(float px, float py) => px >= X && py >= Y && px < X + W && py < Y + H;
    }

    /// <summary>
    /// «Куда идёт щелчок» (C-008, слова Автора 15.11: «как в Террарии … позволяет что-то делать в мире с открытым меню»).
    /// Окна больше не закрывают мир целиком: интерфейс — только то, что нарисовано (ячейки, кнопки, значки), набранное
    /// здесь прямоугольниками за кадр. Курсор вне них — мир, даже если сумка и изготовление открыты. Чистый C# без Unity:
    /// клиент (<c>GameUi.Frame</c>) зовёт эти же функции, тесты <c>C008PointerTests</c> — тоже.
    /// </summary>
    public sealed class UiPointer
    {
        readonly List<UiZone> _zones = new List<UiZone>();

        /// <summary>Сколько прямоугольников набрано в этом кадре.</summary>
        public int Count => _zones.Count;

        /// <summary>Начать кадр: прямоугольники прошлого кадра забыть.</summary>
        public void Clear() => _zones.Clear();

        /// <summary>Добавить видимую часть интерфейса (сетку, кнопку, ряд значков).</summary>
        public void Add(float x, float y, float w, float h) => _zones.Add(new UiZone(x, y, w, h));

        /// <summary>Точка холста над интерфейсом.</summary>
        public bool Over(float x, float y)
        {
            for (int i = 0; i < _zones.Count; i++)
            {
                if (_zones[i].Contains(x, y))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Куда идёт щелчок: над интерфейсом или пока держится ЛКМ, нажатая на интерфейсе (<paramref name="captured"/>), —
        /// в интерфейс; иначе с предметом «в руке курсора» — выбросить; иначе — в мир. Открыты ли окна, не важно.
        /// </summary>
        public static PointerRoute Route(bool overUi, bool holding, bool captured) =>
            overUi || captured ? PointerRoute.Ui : holding ? PointerRoute.Drop : PointerRoute.World;

        /// <summary><see cref="Route(bool, bool, bool)"/> для точки холста по набранным прямоугольникам.</summary>
        public PointerRoute RouteAt(float x, float y, bool holding, bool captured) => Route(Over(x, y), holding, captured);

        /// <summary>
        /// Мышь занята интерфейсом — мир на неё не отвечает (<c>GameUi.PointerBlocked</c>): всё, кроме <see cref="PointerRoute.World"/>.
        /// </summary>
        public static bool Blocks(PointerRoute route) => route != PointerRoute.World;
    }
}
