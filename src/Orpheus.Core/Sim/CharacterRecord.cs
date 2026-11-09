using Orpheus.Core.Movement;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Персонаж мира — мировая часть (docs/07, 6.2, 7.4; 0-035): ключ и состояние, которое остаётся за ключом,
    /// пока игрока нет. Ключ: хозяин мира — по токену хозяина (<see cref="IsHost"/>; сам токен меняется от запуска
    /// к запуску, а персонаж хозяина один); остальные — по имени в мире (учётных записей нет — допущение 0-035, `[док]`).
    /// <para>
    /// В мире (<see cref="EntityId"/> &gt; 0) правда о движении — в сущности такта; вне мира (<see cref="EntityId"/> = 0) —
    /// в <see cref="Mover"/>: позиция, остаток скорости, скорость, уворот на миг ухода.
    /// </para>
    /// </summary>
    public struct CharacterRecord
    {
        /// <summary>Персонаж хозяина мира (вход с токеном хозяина).</summary>
        public bool IsHost;

        /// <summary>Имя, как его задал игрок при последнем входе (у хозяина — подпись, ключ — <see cref="IsHost"/>).</summary>
        public string Name;

        /// <summary>Номер сущности, пока персонаж в мире; 0 — вне мира.</summary>
        public int EntityId;

        /// <summary>Движение на миг ухода (значимо при <see cref="EntityId"/> = 0).</summary>
        public Mover Mover;

        /// <summary>Сумка (1-002): панель, выбранная ячейка, материалы. Значима и в мире, и вне его:
        /// вошёл — сумка сущности берётся отсюда, ушёл — она возвращается сюда (<see cref="CharacterRoster.Park"/>).</summary>
        public Inventory Inv;
    }
}
