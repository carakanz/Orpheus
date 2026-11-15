using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// E2-3 (шаг 2.3): крафт руками и у станков. Рецепты — данные (<see cref="RecipeTable"/>).
    /// <para>
    /// Заявка «изготовить рецепт × N» встаёт в очередь такта вместе с заявками на сумку (порядок — по приходу) и
    /// исполняется фазой предметов: проверки → ингредиенты всех принятых изготовлений снимаются из сумки и сундуков рядом → позиция в
    /// очереди изготовления персонажа (<see cref="CraftQueue"/>). Дальше каждый такт первая позиция продвигается, пока
    /// персонаж жив и рядом нужный станок (ручные рецепты — всегда); готовое кладётся в сумку, не влезшее — кучей у ног.
    /// Отмена возвращает ингредиенты оставшихся изготовлений в сумку (не влезшее — кучей); смерть сбрасывает очередь
    /// и тоже возвращает ингредиенты в сумку (E2-10; не влезшее — кучей у тела). Ингредиенты берутся и из сундуков рядом —
    /// <c>Simulation.CraftChests.cs</c>. Масса сохраняется: ингредиенты до = результат + возврат.
    /// </para>
    /// <para>Детерминизм (11.5): сущности — по списку, станок ищется обходом тайлов в фиксированном порядке, без дробей.</para>
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Станок работает не дальше 3 тайлов от центра персонажа до центра тайла станка (<c>06</c> 3.3) = 768 unit.</summary>
        public const int CraftRadiusUnits = CraftRules.RadiusUnits;

        /// <summary>Дальше радиуса станок ищется ещё до 8 тайлов — чтобы отличить «слишком далеко» от «станка нет».</summary>
        public const int CraftSearchTiles = CraftRules.SearchTiles;

        /// <summary>Наибольшее N в заявке «изготовить × N»; больше — обрезается (не отказ).</summary>
        public const int MaxCraftCount = CraftQueue.MaxLeft;

        /// <summary>Очередь изготовления персонажа; нет такой сущности — пустая.</summary>
        public CraftQueue CraftQueueOf(int entityId)
        {
            int slot = SlotOf(entityId);
            return slot < 0 ? default : _entities[slot].Craft;
        }

        /// <summary>Нужный станок рядом с персонажем прямо сейчас (ручной рецепт — всегда true).</summary>
        public bool CraftStationNear(int entityId, CraftStation station)
        {
            int slot = SlotOf(entityId);
            return slot >= 0 && StationCheck(_entities[slot].Mover.Position, station) == CraftReply.Accepted;
        }

        /// <summary>
        /// Проверка заявки «изготовить» без изменения состояния (сервер отвечает причиной сразу; такт проверит ещё раз).
        /// <paramref name="units"/> — сколько изготовлений будет принято: min(N, 99, на сколько хватает ингредиентов,
        /// место в очереди).
        /// </summary>
        public CraftReply CheckCraft(int entityId, int recipe, int count, out int units)
        {
            units = 0;
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter)
            {
                return CraftReply.NoEntity;
            }

            return CheckCraftAt(slot, recipe, count, out units);
        }

        /// <summary>
        /// Заявка игрока «изготовить рецепт × N» (клиент просит, сервер решает): исполнится в ближайшем такте по порядку
        /// прихода. Возвращает <see cref="CraftReply.Accepted"/> или причину, по которой заявка не взята.
        /// </summary>
        public CraftReply RequestCraft(int entityId, int recipe, int count)
        {
            CraftReply r = CheckCraft(entityId, recipe, count, out _);
            if (r != CraftReply.Accepted)
            {
                return r;
            }

            if (_bagOpCount == _bagOps.Length)
            {
                return CraftReply.Busy;
            }

            _bagOps[_bagOpCount++] = new BagRequest
            {
                EntityId = entityId,
                Op = BagOp.Craft,
                From = (byte)recipe,
                Count = (ushort)Math.Min(count, MaxCraftCount),
            };
            return CraftReply.Accepted;
        }

        /// <summary>
        /// Заявка «отменить позицию очереди изготовления»: ингредиенты оставшихся изготовлений вернутся в такте.
        /// <paramref name="recipe"/> — рецепт, который заявитель видел в этой позиции (сверка: пока заявка шла, очередь могла
        /// сдвинуться — тогда отменилась бы соседняя позиция); 0 — без сверки (тесты ядра).
        /// </summary>
        public CraftReply RequestCraftCancel(int entityId, int index, int recipe = 0)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter)
            {
                return CraftReply.NoEntity;
            }

            if (!_entities[slot].Alive)
            {
                return CraftReply.Dead;
            }

            if ((uint)index >= (uint)_entities[slot].Craft.Count || (recipe != 0 && _entities[slot].Craft.RecipeAt(index) != recipe))
            {
                return CraftReply.NoEntry;
            }

            if (_bagOpCount == _bagOps.Length)
            {
                return CraftReply.Busy;
            }

            _bagOps[_bagOpCount++] = new BagRequest
            {
                EntityId = entityId,
                Op = BagOp.CraftCancel,
                From = (byte)index,
                Count = (ushort)Math.Clamp(recipe, 0, 255),
            };
            return CraftReply.Accepted;
        }

        /// <summary>
        /// Вернуть всю очередь изготовления в сумку персонажа, не влезшее — кучей у ног, которая <b>не исчезает</b>
        /// («вне такта»: игрок уходит из мира — очередь с ним не уходит и не теряется; вернуться он может нескоро).
        /// </summary>
        public void RefundCraftQueue(int entityId)
        {
            int slot = SlotOf(entityId);
            if (slot >= 0)
            {
                RefundAll(Tick, slot, toBag: true, forever: true);
            }
        }

        /// <summary>
        /// То же, но ингредиенты кладутся в <paramref name="bag"/> (сумка из записи персонажа — загрузка сохранения:
        /// персонаж, бывший в мире, после перезапуска сервера «вне мира», а его сумка — в записи). Не влезшее — кучей у
        /// позиции сущности, не исчезает. Очередь сущности очищается.
        /// </summary>
        public void RefundCraftQueueTo(int entityId, ref Inventory bag)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return;
            }

            ref SimEntity e = ref _entities[slot];
            int n = e.Craft.Count;
            for (int i = 0; i < n; i++)
            {
                ref readonly RecipeDef def = ref RecipeTable.Get(e.Craft.RecipeAt(i));
                for (int k = 0; k < def.IngredientCount; k++)
                {
                    byte item = def.IngredientItem(k);
                    int count = def.IngredientCountOf(k) * e.Craft.LeftAt(i);
                    int put = bag.Add(item, count);
                    if (count > put)
                    {
                        SpawnPile(Tick, item, count - put, e.Mover.Position, 0, forever: true);
                    }
                }
            }

            e.Craft = default;
        }

        private CraftReply CheckCraftAt(int slot, int recipe, int count, out int units)
        {
            units = 0;
            ref SimEntity e = ref _entities[slot];
            if (!e.Alive)
            {
                return CraftReply.Dead;
            }

            if (!RecipeTable.Exists(recipe))
            {
                return CraftReply.NoRecipe;
            }

            if (count <= 0)
            {
                return CraftReply.BadCount;
            }

            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            int room = e.Craft.RoomFor(def.Id);
            if (room == 0)
            {
                return CraftReply.QueueFull;
            }

            CraftReply station = StationCheck(e.Mover.Position, def.Station);
            if (station != CraftReply.Accepted)
            {
                return station;
            }

            int can = AffordableNear(slot, in def); // E2-10: сумка + сундуки рядом
            if (can == 0)
            {
                return CraftReply.NoIngredients;
            }

            units = Math.Min(Math.Min(count, MaxCraftCount), Math.Min(can, room));
            return CraftReply.Accepted;
        }

        /// <inheritdoc cref="CraftRules.Affordable"/>
        public static int Affordable(in Inventory inv, in RecipeDef def) => CraftRules.Affordable(in inv, in def);

        private CraftReply StationCheck(WorldPos pos, CraftStation station) => CraftRules.StationCheck(_world, pos, station);

        private void CraftRefusedEvent(long tick, int entityId, BagOp op, int arg, CraftReply reason)
        {
            AddEvent(new SimEvent(SimEventKind.CraftRefused, tick, default, 0, EditResult.Applied, 0,
                requester: entityId, tag: (int)reason, victim: arg, damage: (int)op));
        }

        // Заявка «изготовить» в такте: проверки ещё раз (сумка и место могли измениться с момента заявки). Итог — событием:
        // «в очереди» или «отказ: причина» — сервер шлёт по нему ответ (ровно один на заявку).
        private void ApplyCraft(long tick, int slot, int recipe, int count)
        {
            CraftReply r = CheckCraftAt(slot, recipe, count, out int units);
            if (r != CraftReply.Accepted)
            {
                CraftRefusedEvent(tick, _entities[slot].Id, BagOp.Craft, recipe, r);
                return;
            }

            ref SimEntity e = ref _entities[slot];
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            TakeIngredients(slot, in def, units); // хватает: units ≤ AffordableNear; сумка, потом сундуки рядом (E2-10)
            e.Craft.Push(def.Id, units);
            AddEvent(new SimEvent(SimEventKind.CraftQueued, tick, default, 0, EditResult.Applied, 0,
                requester: e.Id, victim: recipe, damage: units));
        }

        private void ApplyCraftCancel(long tick, int slot, int index, int recipe)
        {
            ref SimEntity e = ref _entities[slot];
            if ((uint)index >= (uint)e.Craft.Count || (recipe != 0 && e.Craft.RecipeAt(index) != recipe))
            {
                CraftRefusedEvent(tick, e.Id, BagOp.CraftCancel, index, CraftReply.NoEntry); // позиции уже нет или в ней другой рецепт
                return;
            }

            byte was = e.Craft.RecipeAt(index);
            Refund(tick, slot, was, e.Craft.LeftAt(index), toBag: true, forever: false);
            e.Craft.RemoveAt(index);
            AddEvent(new SimEvent(SimEventKind.CraftCancelled, tick, default, 0, EditResult.Applied, 0,
                requester: e.Id, tag: was, victim: index));
        }

        // Ингредиенты units изготовлений рецепта — обратно: в сумку (не влезшее — кучей у ног) или сразу кучей.
        private void Refund(long tick, int slot, int recipe, int units, bool toBag, bool forever)
        {
            ref SimEntity e = ref _entities[slot];
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            for (int i = 0; i < def.IngredientCount; i++)
            {
                byte item = def.IngredientItem(i);
                int n = def.IngredientCountOf(i) * units;
                int put = toBag ? e.Inv.Add(item, n) : 0;
                if (n > put)
                {
                    SpawnPile(tick, item, n - put, e.Mover.Position, LootPickupDelayTicks, forever);
                }
            }
        }

        private void RefundAll(long tick, int slot, bool toBag, bool forever)
        {
            ref SimEntity e = ref _entities[slot];
            int n = e.Craft.Count;
            for (int i = 0; i < n; i++)
            {
                Refund(tick, slot, e.Craft.RecipeAt(i), e.Craft.LeftAt(i), toBag, forever);
            }

            e.Craft = default;
        }

        /// <summary>Фаза изготовления такта (после заявок на сумку, до подбора куч).</summary>
        private void StepCraft(long tick)
        {
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i].Craft.IsEmpty)
                {
                    continue;
                }

                if (!_entities[i].Alive)
                {
                    RefundAll(tick, i, toBag: true, forever: true); // смерть: очередь сброшена, ингредиенты — в сумку (E2-10), не влезшее — кучей у тела, не исчезает («потерь при смерти в срезе нет», `06` 10)
                    continue;
                }

                ref SimEntity e = ref _entities[i];
                ref readonly RecipeDef def = ref RecipeTable.Get(e.Craft.RecipeAt(0));
                if (StationCheck(e.Mover.Position, def.Station) != CraftReply.Accepted)
                {
                    continue; // отошёл от станка (или станок снесли) — изготовление стоит, прогресс не теряется
                }

                e.Craft.Progress++;
                if (e.Craft.Progress < def.Ticks)
                {
                    continue;
                }

                e.Craft.CompleteOne();
                int put = e.Inv.Add(def.Result, def.ResultCount);
                if (put < def.ResultCount)
                {
                    SpawnPile(tick, def.Result, def.ResultCount - put, e.Mover.Position, LootPickupDelayTicks);
                }

                AddEvent(new SimEvent(SimEventKind.CraftDone, tick, default, 0, EditResult.Applied, 0,
                    requester: e.Id, tag: def.Id, victim: def.Result, damage: def.ResultCount));
            }
        }
    }
}
