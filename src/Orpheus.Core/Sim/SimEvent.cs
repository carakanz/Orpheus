using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>Вид события такта.</summary>
    public enum SimEventKind : byte
    {
        /// <summary>Правка тайла рассмотрена: <see cref="SimEvent.Result"/> — принята или отказ.</summary>
        TileEdit = 1,

        /// <summary>Пачка правок чанка применена: ревизия чанка +1 (<see cref="SimEvent.Revision"/>).</summary>
        ChunkRevised = 2,

        /// <summary>Урон нанесён (1-001): <see cref="SimEvent.Requester"/> — кто, <see cref="SimEvent.Victim"/> — кому,
        /// <see cref="SimEvent.Damage"/> — сколько (после брони), <see cref="SimEvent.DamageType"/> — тип.</summary>
        Damage = 3,

        /// <summary>Смерть (1-001, 13.5/8.3): <see cref="SimEvent.Victim"/> умер (<see cref="SimEvent.Requester"/> — убийца).</summary>
        Death = 4,

        /// <summary>Возрождение (1-001, 13.5): <see cref="SimEvent.Victim"/> возродился у точки старта с полным здоровьем.</summary>
        Respawn = 5,

        /// <summary>Подбор с земли (E2-1): <see cref="SimEvent.Requester"/> — кто поднял, <see cref="SimEvent.Tag"/> — номер кучи,
        /// <see cref="SimEvent.Victim"/> — id предмета, <see cref="SimEvent.Damage"/> — сколько легло в сумку.</summary>
        PilePickup = 6,

        /// <summary>Изготовлено (E2-3): <see cref="SimEvent.Requester"/> — кто, <see cref="SimEvent.Tag"/> — рецепт,
        /// <see cref="SimEvent.Victim"/> — id предмета, <see cref="SimEvent.Damage"/> — сколько.</summary>
        CraftDone = 7,

        /// <summary>Заявка изготовления или отмены отклонена в такте (E2-3): <see cref="SimEvent.Requester"/> — кто,
        /// <see cref="SimEvent.Tag"/> — причина (<see cref="CraftReply"/>), <see cref="SimEvent.Victim"/> — рецепт (изготовить)
        /// или номер позиции (отменить), <see cref="SimEvent.Damage"/> — операция (<see cref="BagOp.Craft"/> / <see cref="BagOp.CraftCancel"/>).</summary>
        CraftRefused = 8,

        /// <summary>Заявка изготовления исполнена — позиция в очереди (E2-3): <see cref="SimEvent.Requester"/> — кто,
        /// <see cref="SimEvent.Victim"/> — рецепт, <see cref="SimEvent.Damage"/> — сколько изготовлений принято.</summary>
        CraftQueued = 9,

        /// <summary>Позиция очереди изготовления отменена, ингредиенты возвращены (E2-3): <see cref="SimEvent.Requester"/> — кто,
        /// <see cref="SimEvent.Victim"/> — номер позиции, <see cref="SimEvent.Tag"/> — рецепт.</summary>
        CraftCancelled = 10,
    }

    /// <summary>
    /// Событие такта (фаза «события», <see cref="Simulation.Step"/>): что изменилось за такт — для
    /// рассылки клиентам и логов. Буфер событий заранее выделен и живёт до следующего такта.
    /// </summary>
    public readonly struct SimEvent
    {
        public readonly SimEventKind Kind;

        /// <summary>Такт, в котором событие случилось.</summary>
        public readonly long Tick;

        /// <summary>Чанк правки.</summary>
        public readonly ChunkCoord Chunk;

        /// <summary>Индекс тайла в чанке (для <see cref="SimEventKind.TileEdit"/>).</summary>
        public readonly int TileIndex;

        /// <summary>Исход правки (для <see cref="SimEventKind.TileEdit"/>).</summary>
        public readonly EditResult Result;

        /// <summary>Ревизия чанка после пачки.</summary>
        public readonly uint Revision;

        /// <summary>Кто просил правку (номер сущности; 0 — не игрок: тесты, процессы мира) — ответ «принято» / «отказ» (0-028).</summary>
        public readonly int Requester;

        /// <summary>Метка запроса от вызывающего (у сервера — номер запроса клиента и место в линии; 0-028).</summary>
        public readonly int Tag;

        /// <summary>Кому нанесено / кто умер / кто возродился (бои 1-001; для правок — 0).</summary>
        public readonly int Victim;

        /// <summary>Урон после брони (<see cref="SimEventKind.Damage"/>, <see cref="SimEventKind.Death"/>); для прочих — 0.</summary>
        public readonly int Damage;

        /// <summary>Тип урона (`02` 6.9: 1 рубящий, 2 колющий, 3 огонь; 0 — не урон).</summary>
        public readonly byte DamageType;

        public SimEvent(SimEventKind kind, long tick, ChunkCoord chunk, int tileIndex, EditResult result, uint revision,
            int requester = 0, int tag = 0, int victim = 0, int damage = 0, byte damageType = 0)
        {
            Kind = kind;
            Tick = tick;
            Chunk = chunk;
            TileIndex = tileIndex;
            Result = result;
            Revision = revision;
            Requester = requester;
            Tag = tag;
            Victim = victim;
            Damage = damage;
            DamageType = damageType;
        }

        public override string ToString() =>
            Kind switch
            {
                SimEventKind.TileEdit => $"такт {Tick}: правка {Chunk} #{TileIndex} — {Result}",
                SimEventKind.Damage => $"такт {Tick}: урон #{Requester}→#{Victim} {Damage} (тип {DamageType})",
                SimEventKind.Death => $"такт {Tick}: смерть #{Victim} (убийца #{Requester})",
                SimEventKind.PilePickup => $"такт {Tick}: #{Requester} поднял {Damage} × предмет {Victim} (куча {Tag})",
                SimEventKind.CraftDone => $"такт {Tick}: #{Requester} изготовил {Damage} × предмет {Victim} (рецепт {Tag})",
                SimEventKind.CraftRefused => $"такт {Tick}: #{Requester} — отказ крафта ({(BagOp)Damage} {Victim}): {(CraftReply)Tag}",
                SimEventKind.CraftQueued => $"такт {Tick}: #{Requester} поставил в очередь рецепт {Victim} × {Damage}",
                SimEventKind.CraftCancelled => $"такт {Tick}: #{Requester} отменил позицию {Victim} (рецепт {Tag})",
                SimEventKind.Respawn => $"такт {Tick}: возрождение #{Victim}",
                _ => $"такт {Tick}: {Chunk} рев. {Revision}",
            };
    }
}
