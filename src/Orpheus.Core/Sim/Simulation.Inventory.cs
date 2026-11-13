using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Trig;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// P-002: partial Simulation - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// Сумка сущности целиком (выдача стартового набора при входе, 1-002; возврат из записи персонажа).
        /// Нет такой сущности — false. Вне такта.
        /// </summary>
        public bool SetInventory(int entityId, Inventory inv)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return false;
            }

            _entities[slot].Inv = inv;
            return true;
        }

    }
}
