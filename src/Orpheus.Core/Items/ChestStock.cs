using System;

// Пространство имён — Orpheus.Core.Sim (см. ItemTable.cs).
namespace Orpheus.Core.Sim
{
    /// <summary>
    /// E2-10: «сколько каждого предмета в сундуках рядом» — отдельно для каждого вида станка (<see cref="CraftStation"/>):
    /// у рецептов «руками» сундуки считаются от персонажа, у рецептов станка — от ближайшего станка этого вида в радиусе
    /// крафта (<c>06</c> 3.5: «из сундуков в 6 тайлах от станка»). Сервер считает (<see cref="Simulation.FillChestStock"/>),
    /// клиент получает сводкой и прибавляет к сумке при расчёте «есть» (<see cref="CraftRules.Affordable(in Inventory, in RecipeDef, ChestStock?)"/>).
    /// Количество по сети ограничено <see cref="MaxCount"/> (рецепту столько не нужно).
    /// </summary>
    public sealed class ChestStock : IEquatable<ChestStock>
    {
        /// <summary>Видов станка: руки, верстак, костёр.</summary>
        public const int Stations = 3;

        /// <summary>Наибольшее число в сводке (u16 по сети); больше — обрезается.</summary>
        public const int MaxCount = ushort.MaxValue;

        private readonly int[] _n = new int[Stations * ItemTable.Count];

        /// <summary>Сколько предмета в сундуках рядом для рецептов станка <paramref name="station"/>.</summary>
        public int Count(CraftStation station, byte item) =>
            (uint)station < Stations && item < ItemTable.Count ? _n[(int)station * ItemTable.Count + item] : 0;

        public void Set(CraftStation station, byte item, int n)
        {
            if ((uint)station >= Stations || item >= ItemTable.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(item), $"Станок {station}, предмет {item}.");
            }

            _n[(int)station * ItemTable.Count + item] = Math.Clamp(n, 0, MaxCount);
        }

        /// <summary>Прибавить (с обрезкой до <see cref="MaxCount"/>).</summary>
        public void Add(CraftStation station, byte item, int n) => Set(station, item, Count(station, item) + n);

        public void Clear() => Array.Clear(_n, 0, _n.Length);

        public void CopyFrom(ChestStock other) => Array.Copy(other._n, _n, _n.Length);

        /// <summary>Ничего нет ни у одного станка.</summary>
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < _n.Length; i++)
                {
                    if (_n[i] != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public bool Equals(ChestStock? other)
        {
            if (other is null)
            {
                return false;
            }

            for (int i = 0; i < _n.Length; i++)
            {
                if (_n[i] != other._n[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ChestStock o && Equals(o);

        public override int GetHashCode()
        {
            int h = 17;
            for (int i = 0; i < _n.Length; i++)
            {
                h = h * 31 + _n[i];
            }

            return h;
        }
    }
}
