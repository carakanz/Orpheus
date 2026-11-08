using Orpheus.Core.Movement;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Сущность в такте (docs/07, 4.2): номер, вид, движение, текущий кадр ввода.
    /// Сущности живут в списке по возрастанию номера (11.5: «списки по номеру сущности»);
    /// хэш состояния берёт все поля по порядку (<see cref="StateHash"/>).
    /// </summary>
    public struct SimEntity
    {
        /// <summary>Персонаж (игрок или бот — для такта одно и то же: сущность с вводом).</summary>
        public const byte KindCharacter = 1;

        /// <summary>Враг (0-028: заглушка для правила постановки «не ближе 2 тайлов к врагу», `05` 1.3).</summary>
        public const byte KindEnemy = 2;

        /// <summary>Номер сущности: растёт, не переиспользуется.</summary>
        public int Id;

        /// <summary>Вид сущности.</summary>
        public byte Kind;

        /// <summary>Позиция, остаток скорости, скорость.</summary>
        public Mover Mover;

        /// <summary>Кадр ввода, по которому сущность шла в последнем такте (держится, пока не придёт новый).</summary>
        public InputFrame Input;
    }
}
