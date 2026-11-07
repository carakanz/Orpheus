using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Свой 64-битный хэш потока слов (docs/07, 11.5). Шаг — <c>h = Mix(h ^ слово) + Step</c>, где
    /// <see cref="SplitMix64.Mix"/> — биекция: при прочих равных смена любого одного слова меняет итог;
    /// порядок слов важен. Без выделений и без системных хэшей (их зерно случайно в каждом процессе).
    /// </summary>
    public struct Hasher64
    {
        private ulong _h;

        public Hasher64(ulong seed)
        {
            _h = SplitMix64.Mix(seed ^ 0x4F52504853544154UL); // «ORPHSTAT»
        }

        /// <summary>Текущее значение.</summary>
        public readonly ulong Value => _h;

        public void Add(ulong v) => _h = unchecked(SplitMix64.Mix(_h ^ v) + SplitMix64.Step);

        public void Add(long v) => Add(unchecked((ulong)v));

        public void Add(int v) => Add(unchecked((ulong)(uint)v));

        /// <summary>Байты словами по 8 (младший байт первым) + длина: «ab» + «c» ≠ «a» + «bc».</summary>
        public void AddBytes(ReadOnlySpan<byte> data)
        {
            int i = 0;
            for (; i + 8 <= data.Length; i += 8)
            {
                Add(Bytes.ReadUInt64(data, i));
            }

            ulong tail = 0;
            for (int s = 0; i < data.Length; i++, s += 8)
            {
                tail |= (ulong)data[i] << s;
            }

            Add(tail);
            Add(data.Length);
        }
    }

    /// <summary>Хэш состояния на такте: общий, мира (изменённые чанки) и по сущностям — для поиска расхождения.</summary>
    public readonly struct StateHashResult
    {
        public readonly long Tick;

        /// <summary>Хэш всего канонического вида.</summary>
        public readonly ulong Total;

        /// <summary>Хэш изменённых чанков и флагов мира.</summary>
        public readonly ulong World;

        public StateHashResult(long tick, ulong total, ulong world)
        {
            Tick = tick;
            Total = total;
            World = world;
        }

        public override string ToString() => $"такт {Tick}: {Total:x16}";
    }

    /// <summary>
    /// Хэш состояния симуляции — канонический вид по docs/07, 11.5:
    /// <list type="number">
    /// <item>заголовок: номер такта, зерно, форма карты, версия генератора;</item>
    /// <item>сущности по номеру со всеми полями (<see cref="Entity"/>): номер, вид, позиция, остаток скорости,
    /// скорость, текущий кадр ввода;</item>
    /// <item>тайлы изменённых после генерации чанков по порядку (Y, X): координата, ревизия, плотные слои
    /// 3072 байта, записи построек по индексу тайла. Неизменённые чанки — это снимок генерации, их задают
    /// зерно и версия генератора из заголовка;</item>
    /// <item>флаги мира — пока их нет (сюжетные флаги `07` 5.2 придут с сюжетом): пишется их число 0.</item>
    /// </list>
    /// <para>
    /// Почему свой 64-битный, а не SHA-256: хэш считается каждые 10 с в такте сервера, а правило 11.6 —
    /// «в такте нет выделений»; <see cref="Hasher64"/> не выделяет и не зависит от криптобиблиотеки
    /// платформы (IL2CPP, консоль). Задача — заметить расхождение повтора, а не защита от подделки:
    /// случайное совпадение 2⁻⁶⁴. Целостность файлов (снимок генерации, сохранения) — SHA-256, как в 3.4 и 6.3.
    /// </para>
    /// </summary>
    public static class StateHash
    {
        /// <summary>Хэш всего канонического вида на текущем такте.</summary>
        public static StateHashResult Compute(Simulation sim)
        {
            if (sim == null)
            {
                throw new ArgumentNullException(nameof(sim));
            }

            return Compute(sim.World, sim.Tick, sim.Entities);
        }

        /// <summary>Хэш канонического вида из частей: мир, номер такта, сущности по номеру (для тестов полноты и инструментов).</summary>
        public static StateHashResult Compute(WorldModel world, long tick, ReadOnlySpan<SimEntity> entities)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            ulong worldHash = World(world);

            // Зерно хэшера постоянное: каждое поле заголовка входит в хэш ровно своей строкой.
            var h = new Hasher64(0);
            h.Add(tick);
            h.Add(world.Seed);
            h.Add(world.Shape.Width);
            h.Add(world.Shape.Height);
            h.Add(world.Shape.Wraps ? 1 : 0);
            h.Add(world.GeneratorVersion);

            h.Add(entities.Length);
            for (int i = 0; i < entities.Length; i++)
            {
                h.Add(Entity(entities[i]));
            }

            h.Add(worldHash);
            return new StateHashResult(tick, h.Value, worldHash);
        }

        /// <summary>Хэш одной сущности со всеми полями.</summary>
        public static ulong Entity(in SimEntity e)
        {
            var h = new Hasher64(0);
            h.Add(e.Id);
            h.Add(e.Kind);
            h.Add(e.Mover.Position.X);
            h.Add(e.Mover.Position.Y);
            h.Add(e.Mover.Carry.X);
            h.Add(e.Mover.Carry.Y);
            h.Add(e.Mover.UnitsPerTickX10);
            h.Add(e.Input.DirectionCode);
            h.Add(e.Input.Buttons);
            return h.Value;
        }

        /// <summary>
        /// Хэш снимка генерации (карточка 0-017; docs/07, 3.4): форма карты и плотные слои всех чанков по порядку (Y, X),
        /// пустой чанк — отметкой. Считается сразу после генерации или загрузки <c>world.base</c>, до правок сохранения:
        /// сервер шлёт его во «вход принят», клиент, перегенерировавший мир по зерну, сверяет свой. Совпал — у клиента
        /// тот же мир; нет — другой генератор или зерно. Без выделений; 1024² — ~3 МБ слоёв, миллисекунды.
        /// </summary>
        public static ulong Generation(WorldModel world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            var h = new Hasher64(2);
            MapShape shape = world.Shape;
            h.Add(shape.Width);
            h.Add(shape.Height);
            h.Add(shape.Wraps ? 1 : 0);
            for (int cy = 0; cy < shape.ChunksY; cy++)
            {
                for (int cx = 0; cx < shape.ChunksX; cx++)
                {
                    Chunk? chunk = world.FindChunk(new ChunkCoord(cx, cy));
                    if (chunk == null)
                    {
                        h.Add(-1);
                        continue;
                    }

                    h.AddBytes(chunk.Layers);
                }
            }

            return h.Value;
        }

        /// <summary>Хэш мира: изменённые после генерации чанки по порядку и флаги мира. Без выделений.</summary>
        public static ulong World(WorldModel world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            var h = new Hasher64(1);
            Span<ushort> builds = stackalloc ushort[Chunk.TileCount];
            ReadOnlySpan<ChunkCoord> keys = world.GetChunkCoords();
            int changed = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                Chunk chunk = world.FindChunk(keys[i])!;
                if (!chunk.ChangedSinceBase)
                {
                    continue;
                }

                changed++;
                h.Add(keys[i].X);
                h.Add(keys[i].Y);
                h.Add(chunk.Revision);
                h.AddBytes(chunk.Layers);
                int n = chunk.GetBuildIndices(builds);
                h.Add(n);
                for (int k = 0; k < n; k++)
                {
                    chunk.TryGetBuild(builds[k], out BuildRecord r);
                    h.Add(builds[k]);
                    h.Add(r.Owner);
                    h.Add(r.Integrity);
                    h.Add(r.Flags);
                    h.Add(r.NaturalFloor);
                    h.Add(r.DebrisEndTick);
                }
            }

            h.Add(changed);
            h.Add(0); // флагов мира пока нет
            return h.Value;
        }
    }
}
