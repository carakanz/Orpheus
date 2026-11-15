using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// E2-10 (шаг 2.10; <c>BRIEF</c> 11e (2), <c>06</c> 3.5): изготовление из сумки и сундуков поблизости.
    /// <para>
    /// «Поблизости» — сундуки, центр тайла которых не дальше <see cref="CraftRules.ChestRadiusUnits"/> (6 тайлов) от центра
    /// ближайшего станка рецепта в радиусе крафта; у рецептов «руками» — от центра персонажа (<see cref="CraftRules.SourceCenter"/>).
    /// Расстояние — по тору (через шов). Порядок снятия: сначала сумка, затем сундуки по возрастанию расстояния, при
    /// равенстве — по номеру сундука (место в <see cref="ChestTiles"/>: по Y, затем X). Снятие — в такте постановки в очередь,
    /// заявки такта исполняются по порядку прихода: второму у того же сундука достаётся только остаток.
    /// Отмена и смерть возвращают ингредиенты в сумку (не в сундук), не влезшее — кучей у ног.
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        private TileCoord[] _nearChests = new TileCoord[8];
        private long[] _nearD2 = new long[8];
        private int[] _nearIdx = new int[8];

        /// <summary>
        /// Сундуки рядом с центром <paramref name="center"/> в порядке снятия; возвращает их число, тайлы — в
        /// <see cref="_nearChests"/>. Учитываются только сундуки с записью (непустые) и с объектом «сундук» на тайле.
        /// </summary>
        private int CollectNearChests(WorldPos center)
        {
            int n = 0;
            long r2 = (long)CraftRules.ChestRadiusUnits * CraftRules.ChestRadiusUnits;
            for (int i = 0; i < _chestCount; i++)
            {
                TileCoord t = _chestTiles[i];
                if (ObjectId.IdOf(_world.GetObject(t)) != ObjectId.Chest)
                {
                    continue;
                }

                long d2 = Torus.Dist2(_shape, center, BuildLayers.CenterAt(_world, t));
                if (d2 > r2)
                {
                    continue;
                }

                if (n == _nearChests.Length)
                {
                    Array.Resize(ref _nearChests, n * 2);
                    Array.Resize(ref _nearD2, n * 2);
                    Array.Resize(ref _nearIdx, n * 2);
                }

                // Вставка по (расстояние, номер): номера идут по возрастанию, поэтому при равенстве новый — после.
                int at = n;
                while (at > 0 && _nearD2[at - 1] > d2)
                {
                    _nearChests[at] = _nearChests[at - 1];
                    _nearD2[at] = _nearD2[at - 1];
                    _nearIdx[at] = _nearIdx[at - 1];
                    at--;
                }

                _nearChests[at] = t;
                _nearD2[at] = d2;
                _nearIdx[at] = i;
                n++;
            }

            return n;
        }

        /// <summary>Сколько предмета в сундуках рядом для рецептов станка <paramref name="station"/> персонажа в слоте.</summary>
        private int NearChestCount(int slot, CraftStation station, byte item)
        {
            if (!CraftRules.SourceCenter(_world, _entities[slot].Mover.Position, station, out WorldPos center))
            {
                return 0;
            }

            int n = CollectNearChests(center), sum = 0;
            for (int k = 0; k < n; k++)
            {
                sum += _chests[_nearIdx[k]].Count(item);
            }

            return sum;
        }

        /// <summary>На сколько изготовлений рецепта хватает сумки и сундуков рядом.</summary>
        private int AffordableNear(int slot, in RecipeDef def)
        {
            int can = int.MaxValue;
            for (int i = 0; i < def.IngredientCount; i++)
            {
                byte item = def.IngredientItem(i);
                int have = _entities[slot].Inv.Count(item) + NearChestCount(slot, def.Station, item);
                can = Math.Min(can, have / def.IngredientCountOf(i));
            }

            return can == int.MaxValue ? 0 : can;
        }

        /// <summary>
        /// Снять ингредиенты <paramref name="units"/> изготовлений: сумка, затем сундуки по порядку. Вызывать только после
        /// проверки <see cref="AffordableNear"/> ≥ units в этом же такте (иначе недостающее молча не снимется).
        /// </summary>
        private void TakeIngredients(int slot, in RecipeDef def, int units)
        {
            Span<int> rem = stackalloc int[3];
            bool any = false;
            for (int i = 0; i < def.IngredientCount; i++)
            {
                byte item = def.IngredientItem(i);
                int need = def.IngredientCountOf(i) * units;
                int fromBag = Math.Min(need, _entities[slot].Inv.Count(item));
                _entities[slot].Inv.Remove(item, fromBag);
                rem[i] = need - fromBag;
                any |= rem[i] > 0;
            }

            if (!any || !CraftRules.SourceCenter(_world, _entities[slot].Mover.Position, def.Station, out WorldPos center))
            {
                return;
            }

            int n = CollectNearChests(center);
            for (int k = 0; k < n && any; k++)
            {
                TileCoord t = _nearChests[k];
                int ci = IndexOfChest(t); // номера сдвигаются, когда опустевший сундук теряет запись, — ищем по тайлу
                if (ci < 0)
                {
                    continue;
                }

                Inventory chest = _chests[ci];
                bool changed = false;
                any = false;
                for (int i = 0; i < def.IngredientCount; i++)
                {
                    if (rem[i] == 0)
                    {
                        continue;
                    }

                    byte item = def.IngredientItem(i);
                    int take = Math.Min(rem[i], chest.Count(item));
                    if (take > 0)
                    {
                        chest.Remove(item, take);
                        rem[i] -= take;
                        changed = true;
                    }

                    any |= rem[i] > 0;
                }

                if (changed)
                {
                    StoreChest(t, chest);
                }
            }
        }

        /// <summary>
        /// Сводка «сколько каждого предмета в сундуках рядом» для персонажа — по каждому виду станка (для окна крафта
        /// клиента; сервер шлёт её владельцу, когда изменилась). Нет сущности или мёртв — пусто.
        /// </summary>
        public void FillChestStock(int entityId, ChestStock into)
        {
            into.Clear();
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter || !_entities[slot].Alive || _chestCount == 0)
            {
                return;
            }

            for (int s = 0; s < ChestStock.Stations; s++)
            {
                if (!CraftRules.SourceCenter(_world, _entities[slot].Mover.Position, (CraftStation)s, out WorldPos center))
                {
                    continue;
                }

                int n = CollectNearChests(center);
                for (int k = 0; k < n; k++)
                {
                    ref readonly Inventory chest = ref _chests[_nearIdx[k]];
                    for (int c = 0; c < ChestCells; c++)
                    {
                        int cnt = chest.CountIn(c);
                        if (cnt > 0)
                        {
                            into.Add((CraftStation)s, chest.ItemAt(c), cnt);
                        }
                    }
                }
            }
        }
    }
}
