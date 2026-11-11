using System;
using System.Collections.Generic;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Добыча копания (1-010, <c>01</c> 5 «Разрушаемость рельефа», таблица «что даёт»; <c>06</c> 1.4): объект в строке
    /// таблицы даёт материал в диапазоне [min, max]. Строки — данные (<see cref="SetDrop"/>), рядом с <see cref="DigTimes"/>.
    /// <para>
    /// Бросок детерминирован по зерну мира, тайлу и id объекта (<see cref="SplitMix64.Hash(int,int,int,ulong)"/>): сервер,
    /// клиент и бот считают одно и то же число на одном тайле — клиент предсказывает сумку до подтверждения, сервер
    /// подтверждает сумкой (тип 26), отказ откатывает предсказание. Двое копающих один тайл: правку принимает
    /// <see cref="Simulation"/>, добыча — в том же такте и ровно одному.
    /// </para>
    /// </summary>
    public static class DigLoot
    {
        private sealed class Drop
        {
            public Material Material;
            public int Min;
            public int Max;
        }

        private static readonly Dictionary<ushort, Drop> _drops = new()
        {
            // (прикидка карточки 1-010; `01` 5: скала — камень, залежь — руда, дерево — древесина.)
            [ObjectId.Rock] = new Drop { Material = Material.Stone, Min = 1, Max = 2 },
            [ObjectId.Granite] = new Drop { Material = Material.Stone, Min = 1, Max = 2 },
            [ObjectId.Ore] = new Drop { Material = Material.Copper, Min = 2, Max = 3 },
            [ObjectId.Tree] = new Drop { Material = Material.Wood, Min = 3, Max = 3 },
            [ObjectId.Stump] = new Drop { Material = Material.Wood, Min = 1, Max = 1 },
            [ObjectId.Bush] = new Drop { Material = Material.Fiber, Min = 1, Max = 1 },
        };

        /// <summary>Сколько материала даёт объект id на тайле (x, y) мира с зерном <paramref name="seed"/>; материал — out; 0 — не даёт.</summary>
        public static int DropFor(ushort raw, int x, int y, ulong seed, out Material material)
        {
            material = default;
            ushort id = ObjectId.IdOf(raw);
            if (!_drops.TryGetValue(id, out var drop))
            {
                return 0;
            }

            material = drop.Material;
            ulong h = SplitMix64.Hash(x, y, id, seed);
            return drop.Min == drop.Max
                ? drop.Min
                : drop.Min + (int)(h % (ulong)(drop.Max - drop.Min + 1));
        }

        /// <summary>Задать строку добычи: id даёт <paramref name="material"/> в количестве min…max (max &lt; min — считается min).</summary>
        public static void SetDrop(ushort id, Material material, int min, int max)
        {
            _drops[id] = new Drop { Material = material, Min = Math.Max(1, min), Max = Math.Max(Math.Max(1, min), max) };
        }

        /// <summary>Сколько даёт id (детерминированный бросок по тайлу) — материал и число; false — объект не даёт.</summary>
        public static bool TryGetDrop(ushort id, out Material material, out int min, out int max)
        {
            if (_drops.TryGetValue(id, out var drop))
            {
                material = drop.Material;
                min = drop.Min;
                max = drop.Max;
                return true;
            }

            material = default;
            min = 0;
            max = 0;
            return false;
        }
    }
}
