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

        public SimEvent(SimEventKind kind, long tick, ChunkCoord chunk, int tileIndex, EditResult result, uint revision)
        {
            Kind = kind;
            Tick = tick;
            Chunk = chunk;
            TileIndex = tileIndex;
            Result = result;
            Revision = revision;
        }

        public override string ToString() =>
            Kind == SimEventKind.TileEdit
                ? $"такт {Tick}: правка {Chunk} #{TileIndex} — {Result}"
                : $"такт {Tick}: {Chunk} рев. {Revision}";
    }
}
