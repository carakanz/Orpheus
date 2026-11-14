using System;

// Пространство имён — Orpheus.Core.Sim (см. ItemTable.cs).
namespace Orpheus.Core.Sim
{
    /// <summary>Ответ на заявку изготовления (E2-3): принято или причина отказа. Едет по сети одним байтом.</summary>
    public enum CraftReply : byte
    {
        /// <summary>Принято: ингредиенты сняты, позиция в очереди.</summary>
        Accepted = 0,

        /// <summary>Нет такого рецепта.</summary>
        NoRecipe = 1,

        /// <summary>Количество ≤ 0.</summary>
        BadCount = 2,

        /// <summary>Персонаж мёртв.</summary>
        Dead = 3,

        /// <summary>Рядом нет нужного станка (и в <see cref="Simulation.CraftSearchTiles"/> тайлах тоже).</summary>
        NoStation = 4,

        /// <summary>Станок есть, но дальше <see cref="Simulation.CraftRadiusUnits"/> («слишком далеко», <c>06</c> 3.7 п. 1).</summary>
        StationFar = 5,

        /// <summary>Не хватает ингредиентов даже на одно изготовление.</summary>
        NoIngredients = 6,

        /// <summary>Очередь изготовления полна (<see cref="CraftQueue.MaxEntries"/> позиций или 99 штук в позиции).</summary>
        QueueFull = 7,

        /// <summary>Отмена: такой позиции в очереди нет.</summary>
        NoEntry = 8,

        /// <summary>Сервер на паузе — заявки не принимаются.</summary>
        Paused = 9,

        /// <summary>Слишком много заявок за такт (лимит игрока общий с сумкой) или очередь такта полна.</summary>
        Busy = 10,

        /// <summary>Нет такого персонажа в мире.</summary>
        NoEntity = 11,
    }

    /// <summary>
    /// Очередь изготовления персонажа (E2-3): до <see cref="MaxEntries"/> позиций «рецепт × сколько осталось».
    /// Ингредиенты всех оставшихся изготовлений уже сняты из сумки и «лежат в очереди»: отмена и смерть их возвращают.
    /// Делается первая позиция; <see cref="Progress"/> — сколько тактов уже идёт её текущее изготовление.
    /// <para>Значение без ссылок (как <see cref="Inventory"/>): лежит в <see cref="SimEntity"/>, входит в хэш состояния
    /// (только непустая — эталоны сценариев без крафта не меняются), пишется в сохранение (формат 8).</para>
    /// </summary>
    public struct CraftQueue : IEquatable<CraftQueue>
    {
        /// <summary>Позиций в очереди (допущение E2-3: в <c>06</c> очереди нет вовсе).</summary>
        public const int MaxEntries = 5;

        /// <summary>Наибольшее «сколько осталось» в одной позиции.</summary>
        public const int MaxLeft = 99;

        // Позиция: рецепт в младшем байте, «осталось» — в старшем; 0 — пусто. Позиции идут подряд с нулевой.
        private ushort _e0, _e1, _e2, _e3, _e4;

        /// <summary>Тактов идёт текущее изготовление первой позиции (0…время рецепта − 1).</summary>
        public ushort Progress;

        private readonly ushort Raw(int i) => i switch { 0 => _e0, 1 => _e1, 2 => _e2, 3 => _e3, 4 => _e4, _ => (ushort)0 };

        private void SetRaw(int i, ushort v)
        {
            switch (i)
            {
                case 0: _e0 = v; break;
                case 1: _e1 = v; break;
                case 2: _e2 = v; break;
                case 3: _e3 = v; break;
                case 4: _e4 = v; break;
            }
        }

        /// <summary>Позиций занято.</summary>
        public readonly int Count => _e0 == 0 ? 0 : _e1 == 0 ? 1 : _e2 == 0 ? 2 : _e3 == 0 ? 3 : _e4 == 0 ? 4 : 5;

        /// <summary>Очередь пуста.</summary>
        public readonly bool IsEmpty => _e0 == 0;

        /// <summary>Рецепт позиции; нет позиции — 0.</summary>
        public readonly byte RecipeAt(int i) => (byte)Raw(i);

        /// <summary>Сколько изготовлений осталось в позиции (вместе с текущим); нет позиции — 0.</summary>
        public readonly int LeftAt(int i) => Raw(i) >> 8;

        /// <summary>
        /// Сколько изготовлений рецепта очередь ещё примет: в последнюю позицию с тем же рецептом — до
        /// <see cref="MaxLeft"/>, иначе в новую позицию; очередь полна — 0.
        /// </summary>
        public readonly int RoomFor(byte recipe)
        {
            int n = Count;
            if (n > 0 && RecipeAt(n - 1) == recipe && LeftAt(n - 1) < MaxLeft)
            {
                return MaxLeft - LeftAt(n - 1);
            }

            return n < MaxEntries ? MaxLeft : 0;
        }

        /// <summary>Добавить изготовления: доливает последнюю позицию с тем же рецептом, иначе новая позиция. Возвращает, сколько принято.</summary>
        public int Push(byte recipe, int units)
        {
            int take = Math.Min(units, RoomFor(recipe));
            if (recipe == 0 || take <= 0)
            {
                return 0;
            }

            int n = Count;
            if (n > 0 && RecipeAt(n - 1) == recipe && LeftAt(n - 1) < MaxLeft)
            {
                SetRaw(n - 1, (ushort)(recipe | ((LeftAt(n - 1) + take) << 8)));
            }
            else
            {
                SetRaw(n, (ushort)(recipe | (take << 8)));
            }

            return take;
        }

        /// <summary>Одно изготовление первой позиции готово: «осталось» − 1, ноль — позиция уходит. Прогресс — в 0.</summary>
        public void CompleteOne()
        {
            int left = LeftAt(0) - 1;
            Progress = 0;
            if (left > 0)
            {
                SetRaw(0, (ushort)(RecipeAt(0) | (left << 8)));
            }
            else
            {
                RemoveAt(0);
            }
        }

        /// <summary>Убрать позицию (следующие сдвигаются); первую — вместе с прогрессом.</summary>
        public void RemoveAt(int index)
        {
            int n = Count;
            if ((uint)index >= (uint)n)
            {
                return;
            }

            for (int i = index; i < MaxEntries - 1; i++)
            {
                SetRaw(i, Raw(i + 1));
            }

            SetRaw(MaxEntries - 1, 0);
            if (index == 0)
            {
                Progress = 0;
            }
        }

        public readonly bool Equals(CraftQueue o) =>
            _e0 == o._e0 && _e1 == o._e1 && _e2 == o._e2 && _e3 == o._e3 && _e4 == o._e4 && Progress == o.Progress;

        public override readonly bool Equals(object? obj) => obj is CraftQueue o && Equals(o);

        public override readonly int GetHashCode() => _e0 | (_e1 << 16) ^ (_e2 * 31) ^ (_e3 * 131) ^ (_e4 * 1031) ^ (Progress << 8);

        public static bool operator ==(CraftQueue a, CraftQueue b) => a.Equals(b);
        public static bool operator !=(CraftQueue a, CraftQueue b) => !a.Equals(b);

        /// <summary>Тот же состав позиций (без прогресса) — для рассылки: прогресс клиент считает сам.</summary>
        public readonly bool SameEntries(in CraftQueue o) =>
            _e0 == o._e0 && _e1 == o._e1 && _e2 == o._e2 && _e3 == o._e3 && _e4 == o._e4;

        /// <summary>Слова очереди в хэш состояния: позиции и прогресс.</summary>
        public readonly void AddTo(ref Hasher64 h)
        {
            h.Add(_e0 | (_e1 << 16));
            h.Add(_e2 | (_e3 << 16));
            h.Add(_e4 | (Progress << 16));
        }

        /// <summary>Размер payload: прогресс u16 + 5 позиций × (рецепт 1 + осталось 1) = 12 байт.</summary>
        public const int PayloadBytes = 2 + 2 * MaxEntries;

        /// <summary>Записать payload (сохранение формата 8 и сообщение «очередь изготовления»).</summary>
        public readonly void WritePayload(Span<byte> dest)
        {
            if (dest.Length < PayloadBytes)
            {
                throw new ArgumentException($"payload очереди изготовления — {PayloadBytes} байт, дали {dest.Length}.", nameof(dest));
            }

            dest[0] = (byte)Progress;
            dest[1] = (byte)(Progress >> 8);
            for (int i = 0; i < MaxEntries; i++)
            {
                dest[2 + 2 * i] = RecipeAt(i);
                dest[3 + 2 * i] = (byte)LeftAt(i);
            }
        }

        /// <summary>
        /// Прочитать payload. Неизвестный рецепт, «осталось» вне 1…<see cref="MaxLeft"/>, дырка между позициями,
        /// прогресс не меньше времени рецепта (или без позиций) — <see cref="ArgumentException"/>.
        /// </summary>
        public static CraftQueue ReadPayload(ReadOnlySpan<byte> src)
        {
            if (src.Length < PayloadBytes)
            {
                throw new ArgumentException($"payload очереди изготовления — {PayloadBytes} байт, дали {src.Length}.", nameof(src));
            }

            var q = new CraftQueue();
            bool ended = false;
            for (int i = 0; i < MaxEntries; i++)
            {
                byte recipe = src[2 + 2 * i];
                int left = src[3 + 2 * i];
                if (recipe == 0 && left == 0)
                {
                    ended = true;
                    continue;
                }

                if (ended || !RecipeTable.Exists(recipe) || left < 1 || left > MaxLeft)
                {
                    throw new ArgumentException($"Очередь изготовления: позиция {i} — рецепт {recipe} × {left}.", nameof(src));
                }

                q.SetRaw(i, (ushort)(recipe | (left << 8)));
            }

            int progress = src[0] | (src[1] << 8);
            if (progress != 0 && (q.IsEmpty || progress >= RecipeTable.Get(q.RecipeAt(0)).Ticks))
            {
                throw new ArgumentException($"Очередь изготовления: прогресс {progress} при рецепте {q.RecipeAt(0)}.", nameof(src));
            }

            q.Progress = (ushort)progress;
            return q;
        }

        public override readonly string ToString()
        {
            var sb = new System.Text.StringBuilder("craft[");
            for (int i = 0; i < Count; i++)
            {
                sb.Append(i > 0 ? ", " : string.Empty).Append(RecipeAt(i)).Append('×').Append(LeftAt(i));
            }

            return sb.Append("] +").Append(Progress).ToString();
        }
    }
}
