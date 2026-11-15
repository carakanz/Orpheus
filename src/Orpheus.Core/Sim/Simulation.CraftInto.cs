using System;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// C-013 (слова Автора 15.11: «Крафтится он если я нажимаю на землю или в рюкзак … предмет крафтится в рюкзак»): выбранный
    /// в окне рецепт и щелчок по ячейке сумки — изготовить <b>сразу</b> в эту ячейку, без очереди и времени (как изготовление
    /// при постановке из меню, E2-16). Ингредиенты — сумка, затем сундуки рядом (E2-10), станок рецепта — рядом.
    /// Результат ложится только в названную ячейку (пустую или с той же неполной стопкой); 0xFF — куда влезет (клавиша Enter).
    /// Принимается столько изготовлений, сколько хватает ингредиентов и места (освобождённое ингредиентами место считается,
    /// <see cref="CraftRules.RunsThatFit"/>); ни одного — отказ «некуда» (<see cref="CraftReply.NoRoom"/>), ничего не снято.
    /// Масса: ингредиенты превращаются в результат, ничего не падает на землю.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>«В сумку, куда влезет» — ячейка заявки <see cref="RequestCraftInto"/>.</summary>
        public const byte AnyBagCell = 0xFF;

        /// <summary>
        /// Проверка «изготовить в ячейку» без изменения состояния: <paramref name="units"/> — сколько изготовлений примется.
        /// </summary>
        public CraftReply CheckCraftInto(int entityId, int recipe, int cell, int count, out int units)
        {
            units = 0;
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter)
            {
                return CraftReply.NoEntity;
            }

            return CheckCraftIntoAt(slot, recipe, cell, count, out units);
        }

        /// <summary>
        /// Заявка игрока «изготовить рецепт в ячейку сумки × N» (C-013): исполнится в ближайшем такте по порядку прихода.
        /// <paramref name="cell"/> — 0…39 или <see cref="AnyBagCell"/>.
        /// </summary>
        public CraftReply RequestCraftInto(int entityId, int recipe, int cell, int count)
        {
            CraftReply r = CheckCraftInto(entityId, recipe, cell, count, out _);
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
                Op = BagOp.CraftInto,
                From = (byte)recipe,
                To = (byte)cell,
                Count = (ushort)Math.Min(count, MaxCraftCount),
            };
            return CraftReply.Accepted;
        }

        private CraftReply CheckCraftIntoAt(int slot, int recipe, int cell, int count, out int units)
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

            if (count <= 0 || (cell != AnyBagCell && (uint)cell >= Inventory.Cells))
            {
                return CraftReply.BadCount;
            }

            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            CraftReply station = StationCheck(e.Mover.Position, def.Station);
            if (station != CraftReply.Accepted)
            {
                return station;
            }

            int can = Math.Min(Math.Min(count, MaxCraftCount), AffordableNear(slot, in def));
            if (can == 0)
            {
                return CraftReply.NoIngredients;
            }

            units = CraftRules.RunsThatFit(in e.Inv, in def, cell, can);
            return units > 0 ? CraftReply.Accepted : CraftReply.NoRoom;
        }

        // Такт: проверки ещё раз, ингредиенты (сумка → сундуки рядом), результат — в ячейку (или доукладкой в сумку).
        private void ApplyCraftInto(long tick, int slot, int recipe, int cell, int count)
        {
            CraftReply r = CheckCraftIntoAt(slot, recipe, cell, count, out int units);
            if (r != CraftReply.Accepted)
            {
                CraftRefusedEvent(tick, _entities[slot].Id, BagOp.CraftInto, recipe, r);
                return;
            }

            ref SimEntity e = ref _entities[slot];
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            TakeIngredients(slot, in def, units);
            int made = units * def.ResultCount;
            if (cell == AnyBagCell)
            {
                e.Inv.Add(def.Result, made); // влезет: RunsThatFit считал по той же сумке после снятия
            }
            else
            {
                e.Inv.SetCell(cell, def.Result, e.Inv.CountIn(cell) + made);
            }

            AddEvent(new SimEvent(SimEventKind.CraftMade, tick, default, 0, EditResult.Applied, 0,
                requester: e.Id, tag: cell, victim: recipe, damage: units));
        }
    }
}
