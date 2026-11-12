using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Save
{
    /// <summary>
    /// Состояние мира, снятое на границе такта (docs/07, 6.4): не ссылается на живой мир, поэтому сжатие
    /// и запись идут в фоновом потоке, пока такт работает дальше. Чанк со свежей сжатой копией берётся
    /// ссылкой на копию (она неизменна: пересборка создаёт новый массив), остальные — копией сырого блока
    /// (3 КБ + записи построек), сжимаются при сборке файла.
    /// </summary>
    public sealed class WorldSaveData
    {
        internal WorldSaveData(
            string name, ulong seed, MapShape shape, int generatorVersion, byte[] baseSha256, long tick, int nextEntityId,
            ChunkCoord[] coords, uint[] revisions, byte[]?[] compressed, byte[]?[] raw, SimEntity[] entities, CharacterRecord[] characters,
            int worldTime)
        {
            Characters = characters;
            Name = name;
            Seed = seed;
            Shape = shape;
            GeneratorVersion = generatorVersion;
            BaseSha256 = baseSha256;
            Tick = tick;
            NextEntityId = nextEntityId;
            ChunkCoords = coords;
            Revisions = revisions;
            Compressed = compressed;
            RawBlobs = raw;
            Entities = entities;
            WorldTime = worldTime;
        }

        public string Name { get; }
        public ulong Seed { get; }
        public MapShape Shape { get; }
        public int GeneratorVersion { get; }
        public byte[] BaseSha256 { get; }

        /// <summary>Такт, на границе которого снято состояние.</summary>
        public long Tick { get; }

        /// <summary>Время мира в тактах суток, [0; 36 000) (1-014); у сохранений до 1-014 — 0.</summary>
        public int WorldTime { get; }

        public int NextEntityId { get; }

        /// <summary>Изменённые после генерации чанки по порядку (Y, X).</summary>
        public ChunkCoord[] ChunkCoords { get; }

        internal uint[] Revisions { get; }
        internal byte[]?[] Compressed { get; }
        internal byte[]?[] RawBlobs { get; }

        /// <summary>Сущности по номеру (копии).</summary>
        public SimEntity[] Entities { get; }

        /// <summary>Персонажи мира — мировая часть (копии; формат 3, 0-035).</summary>
        public CharacterRecord[] Characters { get; }
    }

    /// <summary>Загруженное из сохранения состояние такта (мир уже положен в <see cref="WorldModel"/>).</summary>
    public sealed class LoadedWorldState
    {
        internal LoadedWorldState(ulong number, long tick, string name, int nextEntityId, SimEntity[] entities, int chunks, CharacterRecord[] characters, uint format, int worldTime)
        {
            Characters = characters;
            FormatVersion = format;
            Number = number;
            Tick = tick;
            Name = name;
            NextEntityId = nextEntityId;
            Entities = entities;
            ChunkCount = chunks;
            WorldTime = worldTime;
        }

        /// <summary>Номер сохранения.</summary>
        public ulong Number { get; }

        public long Tick { get; }

        public string Name { get; }

        public int NextEntityId { get; }

        public SimEntity[] Entities { get; }

        /// <summary>Сколько изменённых чанков положено поверх снимка генерации.</summary>
        public int ChunkCount { get; }

        /// <summary>
        /// Персонажи мира (формат 3, 0-035): у бывших в мире на такте сохранения <see cref="CharacterRecord.EntityId"/> —
        /// номер сущности из <see cref="Entities"/>, у вышедших — движение на миг ухода. Формат 1–2 — пусто: кто из сущностей
        /// игрок, там не записано.
        /// </summary>
        public CharacterRecord[] Characters { get; }

        /// <summary>Версия формата прочитанного файла.</summary>
        public uint FormatVersion { get; }

        /// <summary>Время мира в тактах суток, [0; 36 000) (1-014); у файлов до 1-014 поля нет — 0.</summary>
        public int WorldTime { get; }

        /// <summary>Положить сущности, такт и время мира в пустую симуляцию этого мира.</summary>
        public void RestoreInto(Simulation sim)
        {
            if (sim == null)
            {
                throw new ArgumentNullException(nameof(sim));
            }

            sim.Restore(Tick, Entities, NextEntityId, WorldTime);
        }
    }

    /// <summary>
    /// Мир в контейнер и обратно (docs/07, 6.2–6.3; версия формата 3, читаются и 1–2). Разделы:
    /// <list type="bullet">
    /// <item>МИР — имя, зерно, форма, версия генератора, SHA-256 снимка генерации (<c>world.base</c>), следующий номер сущности;</item>
    /// <item>ЧАНКИ — изменённые после генерации чанки (0-005: <see cref="Chunk.ChangedSinceBase"/>): карта, x, y, ревизия, сжатый блок;</item>
    /// <item>СУЩЕСТВА — минимально: сущности такта со всеми полями хэша состояния (0-006; уворот — с формата 2, 0-029);</item>
    /// <item>ПЕРСОНАЖИ — с формата 3 (0-035): ключ персонажа (хозяин или имя) и где он — сущность в мире или движение на миг ухода.</item>
    /// </list>
    /// Загрузка: снимок генерации → поверх изменённые чанки → сущности и такт. Хэш состояния после загрузки
    /// равен хэшу на такте сохранения (тест <c>D00_Save_RoundTrip_*</c>).
    /// </summary>
    public static class WorldSave
    {
        /// <summary>
        /// Снять состояние на границе такта — между <see cref="Simulation.Step"/> (основной поток, быстро:
        /// ссылки на свежие копии, копии сырых блоков остальных изменённых чанков, копия списка сущностей).
        /// </summary>
        /// <param name="characters">Персонажи мира (0-035) на ту же границу такта; null — нет.</param>
        public static WorldSaveData Capture(Simulation sim, byte[] baseSha256, string name, CharacterRecord[]? characters = null)
        {
            if (sim == null)
            {
                throw new ArgumentNullException(nameof(sim));
            }

            if (baseSha256 == null || baseSha256.Length != 32)
            {
                throw new ArgumentException("Нужен SHA-256 снимка генерации (32 байта).", nameof(baseSha256));
            }

            WorldModel world = sim.World;
            ChunkCoord[] coords = world.GetChangedSinceBase();
            var revisions = new uint[coords.Length];
            var compressed = new byte[]?[coords.Length];
            var raw = new byte[]?[coords.Length];
            for (int i = 0; i < coords.Length; i++)
            {
                Chunk chunk = world.FindChunk(coords[i])!;
                revisions[i] = chunk.Revision;
                compressed[i] = chunk.FreshCopy;
                if (compressed[i] == null)
                {
                    var blob = new byte[chunk.BlobBytes];
                    chunk.WriteBlob(blob);
                    raw[i] = blob;
                }
            }

            return new WorldSaveData(
                name ?? string.Empty, world.Seed, world.Shape, world.GeneratorVersion, (byte[])baseSha256.Clone(),
                sim.Tick, sim.NextEntityId, coords, revisions, compressed, raw, sim.Entities.ToArray(),
                characters != null ? (CharacterRecord[])characters.Clone() : Array.Empty<CharacterRecord>(),
                sim.WorldTime);
        }

        /// <summary>
        /// Собрать файл сохранения (любой поток). Дата — мс Unix UTC от вызывающего.
        /// <paramref name="padKb"/> — отладочное наполнение раздела «ОТЛАДКА» в КБ (приёмка 0.4, ключ сервера
        /// <c>--debug-save-pad</c>): грузчик его пропускает, в обычной игре 0.
        /// <paramref name="replaces"/> — поколение, которое это сохранение заменяет (0-037, поле МИР
        /// <see cref="SaveSections.WorldField.Replaces"/>); 0 — поле не пишется, файл тот же, что и до 0-037.
        /// </summary>
        public static byte[] Build(WorldSaveData d, ulong number, long dateUnixMs, int padKb = 0, ulong replaces = 0)
        {
            if (d == null)
            {
                throw new ArgumentNullException(nameof(d));
            }

            var w = new RecordWriter(256);
            w.WriteText(SaveSections.WorldField.Name, d.Name);
            w.WriteUInt(SaveSections.WorldField.Seed, d.Seed);
            w.WriteUInt(SaveSections.WorldField.Width, (ulong)d.Shape.Width);
            w.WriteUInt(SaveSections.WorldField.Height, (ulong)d.Shape.Height);
            w.WriteBool(SaveSections.WorldField.Wraps, d.Shape.Wraps);
            w.WriteUInt(SaveSections.WorldField.GeneratorVersion, (ulong)d.GeneratorVersion);
            w.WriteBytes(SaveSections.WorldField.BaseSha256, d.BaseSha256);
            w.WriteUInt(SaveSections.WorldField.NextEntityId, (ulong)d.NextEntityId);
            if (replaces != 0)
            {
                w.WriteUInt(SaveSections.WorldField.Replaces, replaces);
            }

            if (d.WorldTime != 0)
            {
                // 1-014: nonzero — пишем; t = 0 (полный день) — файл байт в байт как до 1-014.
                w.WriteUInt(SaveSections.WorldField.WorldTime, (ulong)d.WorldTime);
            }

            byte[] worldSection = w.ToArray();

            var c = new RecordWriter(d.ChunkCoords.Length * 320 + 16);
            for (int i = 0; i < d.ChunkCoords.Length; i++)
            {
                byte[] data = d.Compressed[i] ?? Chunk.CompressBlob(d.RawBlobs[i]!);
                int mark = c.BeginRecord(SaveSections.ChunksField.Chunk);
                c.WriteUInt(SaveSections.ChunkField.Map, 0);
                c.WriteUInt(SaveSections.ChunkField.X, (ulong)d.ChunkCoords[i].X);
                c.WriteUInt(SaveSections.ChunkField.Y, (ulong)d.ChunkCoords[i].Y);
                c.WriteUInt(SaveSections.ChunkField.Revision, d.Revisions[i]);
                c.WriteBytes(SaveSections.ChunkField.Data, data);
                c.EndRecord(mark);
            }

            var e = new RecordWriter(d.Entities.Length * 80 + 16);
            for (int i = 0; i < d.Entities.Length; i++)
            {
                SimEntity x = d.Entities[i];
                int mark = e.BeginRecord(SaveSections.CreaturesField.Entity);
                e.WriteUInt(SaveSections.EntityField.Id, (ulong)(uint)x.Id);
                e.WriteUInt(SaveSections.EntityField.Kind, x.Kind);
                e.WriteInt(SaveSections.EntityField.X, x.Mover.Position.X);
                e.WriteInt(SaveSections.EntityField.Y, x.Mover.Position.Y);
                e.WriteInt(SaveSections.EntityField.CarryX, x.Mover.Carry.X);
                e.WriteInt(SaveSections.EntityField.CarryY, x.Mover.Carry.Y);
                e.WriteInt(SaveSections.EntityField.SpeedX10, x.Mover.UnitsPerTickX10);
                e.WriteUInt(SaveSections.EntityField.Direction, x.Input.DirectionCode);
                e.WriteUInt(SaveSections.EntityField.Buttons, x.Input.Buttons);
                e.WriteUInt(SaveSections.EntityField.DodgeElapsed, x.Mover.Dodge.Elapsed);
                e.WriteUInt(SaveSections.EntityField.DodgeDashLeft, x.Mover.Dodge.DashLeft);
                e.WriteUInt(SaveSections.EntityField.DodgeCooldown, x.Mover.Dodge.Cooldown);
                e.WriteUInt(SaveSections.EntityField.DodgeDirection, x.Mover.Dodge.Direction);
                e.WriteUInt(SaveSections.EntityField.MeleePhase, x.Mover.Melee.Phase);
                e.WriteUInt(SaveSections.EntityField.MeleeFramesLeft, x.Mover.Melee.FramesLeft);
                e.WriteUInt(SaveSections.EntityField.MeleeIndex, x.Mover.Melee.Index);
                e.WriteUInt(SaveSections.EntityField.MeleeBuffered, x.Mover.Melee.Buffered ? 1u : 0u);
                e.WriteUInt(SaveSections.EntityField.MeleeIdleFrames, x.Mover.Melee.IdleFrames);
                e.WriteUInt(SaveSections.EntityField.MeleeResolved, x.Mover.Melee.Resolved ? 1u : 0u);
                e.WriteUInt(SaveSections.EntityField.MeleeAimRaw, x.Mover.Melee.AimRaw);
                e.WriteInt(SaveSections.EntityField.MeleeOriginX, x.Mover.Melee.OriginX);
                e.WriteInt(SaveSections.EntityField.MeleeOriginY, x.Mover.Melee.OriginY);
                var bag = new byte[Inventory.PayloadBytes]; // сумка — всегда (1-004): StateHash её считает, round-trip обязан сходиться
                x.Inv.WritePayload(bag);
                e.WriteBytes(SaveSections.EntityField.Inv, bag);
                // Бой (формат 6, 1-007): StateHash считает здоровье/замах/регенерацию/отбрасывание/прицел — пишем всегда.
                e.WriteInt(SaveSections.EntityField.Health, x.Health);
                e.WriteInt(SaveSections.EntityField.MaxHealth, x.MaxHealth);
                e.WriteUInt(SaveSections.EntityField.Armor, x.Armor);
                e.WriteUInt(SaveSections.EntityField.Mass, x.Mass);
                e.WriteUInt(SaveSections.EntityField.InvulnTicks, (ulong)x.InvulnTicks);
                e.WriteUInt(SaveSections.EntityField.RegenTicks, (ulong)x.RegenTicks);
                e.WriteInt(SaveSections.EntityField.RegenCarryX100, x.RegenCarryX100);
                e.WriteUInt(SaveSections.EntityField.CombatTicks, (ulong)x.CombatTicks);
                e.WriteUInt(SaveSections.EntityField.DeadTicks, (ulong)x.DeadTicks);
                e.WriteUInt(SaveSections.EntityField.AttackWindup, (ulong)x.AttackWindup);
                e.WriteUInt(SaveSections.EntityField.AttackRecover, (ulong)x.AttackRecover);
                e.WriteUInt(SaveSections.EntityField.AttackImmune, (ulong)x.AttackImmune);
                e.WriteInt(SaveSections.EntityField.AttackTarget, x.AttackTarget);
                e.WriteUInt(SaveSections.EntityField.AttackRaw, x.AttackRaw);
                e.WriteInt(SaveSections.EntityField.AttackPeriod, x.AttackPeriod);
                e.WriteInt(SaveSections.EntityField.AttackOriginX, x.AttackOrigin.X);
                e.WriteInt(SaveSections.EntityField.AttackOriginY, x.AttackOrigin.Y);
                e.WriteInt(SaveSections.EntityField.AttackFacingRaw, x.AttackFacingRaw);
                e.WriteUInt(SaveSections.EntityField.KnockLeft, x.Mover.KnockLeft);
                e.WriteInt(SaveSections.EntityField.KnockSpeedX10, x.Mover.KnockSpeedX10);
                e.WriteUInt(SaveSections.EntityField.KnockDir, x.Mover.KnockDir);
                e.WriteUInt(SaveSections.EntityField.AimAngle16, x.Input.Angle16);
                e.WriteUInt(SaveSections.EntityField.InputSlot, x.Input.Slot);
                if (x.Brain.Archetype != EnemyArchetype.None)
                {
                    // Мозг ИИ (1-007, формат 6) — только у врага с мозгом: у персонажа и манекена полей нет.
                    e.WriteUInt(SaveSections.EntityField.BrainArchetype, x.Brain.Archetype);
                    e.WriteUInt(SaveSections.EntityField.BrainState, (uint)(uint)x.Brain.State);
                    e.WriteInt(SaveSections.EntityField.BrainTargetId, x.Brain.TargetId);
                    e.WriteInt(SaveSections.EntityField.BrainStateTicks, x.Brain.StateTicks);
                    e.WriteInt(SaveSections.EntityField.BrainLostTicks, x.Brain.LostTicks);
                    e.WriteInt(SaveSections.EntityField.BrainLastHitTick, x.Brain.LastHitTick);
                    e.WriteInt(SaveSections.EntityField.BrainLastHitBy, x.Brain.LastHitBy);
                    e.WriteInt(SaveSections.EntityField.BrainLastHitAmount, x.Brain.LastHitAmount);
                    e.WriteInt(SaveSections.EntityField.BrainHomeX, x.Brain.HomeX);
                    e.WriteInt(SaveSections.EntityField.BrainHomeY, x.Brain.HomeY);
                    e.WriteInt(SaveSections.EntityField.BrainGoalX, x.Brain.GoalX);
                    e.WriteInt(SaveSections.EntityField.BrainGoalY, x.Brain.GoalY);

                    // «Упёрся» (1-013 9.3): в StateHash — едут в сейв; у старых сохранений дефолт 0 (игнор не активен).
                    e.WriteInt(SaveSections.EntityField.BrainBlockedSince, x.Brain.BlockedSince);
                    e.WriteInt(SaveSections.EntityField.BrainIgnoreTargetId, x.Brain.IgnoreTargetId);
                    e.WriteInt(SaveSections.EntityField.BrainIgnoreUntil, x.Brain.IgnoreUntil);

                    // Спавнер (1-009 8.5/8.8): причина появления, срок «Интереса», серия «дальше 80 т».
                    e.WriteInt(SaveSections.EntityField.BrainCauseId, x.Brain.CauseId);
                    e.WriteInt(SaveSections.EntityField.BrainInterestUntil, x.Brain.InterestUntil);
                    e.WriteInt(SaveSections.EntityField.BrainFarSince, x.Brain.FarSince);
                }
                e.EndRecord(mark);
            }

            var ch = new RecordWriter(d.Characters.Length * 80 + 16);
            for (int i = 0; i < d.Characters.Length; i++)
            {
                CharacterRecord x = d.Characters[i];
                int mark = ch.BeginRecord(SaveSections.CharactersField.Character);
                ch.WriteBool(SaveSections.CharacterField.Host, x.IsHost);
                ch.WriteText(SaveSections.CharacterField.Name, x.Name ?? string.Empty);
                var payload = new byte[Inventory.PayloadBytes]; // сумка — всегда (1-002, формат 4): и в мире, и вне его
                x.Inv.WritePayload(payload);
                ch.WriteBytes(SaveSections.CharacterField.Inv, payload);
                if (x.EntityId > 0)
                {
                    ch.WriteUInt(SaveSections.CharacterField.EntityId, (ulong)(uint)x.EntityId);
                }
                else
                {
                    ch.WriteInt(SaveSections.CharacterField.X, x.Mover.Position.X);
                    ch.WriteInt(SaveSections.CharacterField.Y, x.Mover.Position.Y);
                    ch.WriteInt(SaveSections.CharacterField.CarryX, x.Mover.Carry.X);
                    ch.WriteInt(SaveSections.CharacterField.CarryY, x.Mover.Carry.Y);
                    ch.WriteInt(SaveSections.CharacterField.SpeedX10, x.Mover.UnitsPerTickX10);
                    ch.WriteUInt(SaveSections.CharacterField.DodgeElapsed, x.Mover.Dodge.Elapsed);
                    ch.WriteUInt(SaveSections.CharacterField.DodgeDashLeft, x.Mover.Dodge.DashLeft);
                    ch.WriteUInt(SaveSections.CharacterField.DodgeCooldown, x.Mover.Dodge.Cooldown);
                    ch.WriteUInt(SaveSections.CharacterField.DodgeDirection, x.Mover.Dodge.Direction);
                    ch.WriteUInt(SaveSections.CharacterField.MeleePhase, x.Mover.Melee.Phase);
                    ch.WriteUInt(SaveSections.CharacterField.MeleeFramesLeft, x.Mover.Melee.FramesLeft);
                    ch.WriteUInt(SaveSections.CharacterField.MeleeIndex, x.Mover.Melee.Index);
                    ch.WriteUInt(SaveSections.CharacterField.MeleeBuffered, x.Mover.Melee.Buffered ? 1u : 0u);
                    ch.WriteUInt(SaveSections.CharacterField.MeleeIdleFrames, x.Mover.Melee.IdleFrames);
                    ch.WriteUInt(SaveSections.CharacterField.MeleeResolved, x.Mover.Melee.Resolved ? 1u : 0u);
                    ch.WriteUInt(SaveSections.CharacterField.MeleeAimRaw, x.Mover.Melee.AimRaw);
                    ch.WriteInt(SaveSections.CharacterField.MeleeOriginX, x.Mover.Melee.OriginX);
                    ch.WriteInt(SaveSections.CharacterField.MeleeOriginY, x.Mover.Melee.OriginY);
                }

                ch.EndRecord(mark);
            }

            var header = new SaveHeader(SaveModule.FormatVersion, SaveModule.BuildVersion, number, d.Tick, dateUnixMs);
            var sections = new List<SaveSection>
            {
                new SaveSection(SaveSections.World, worldSection),
                new SaveSection(SaveSections.Chunks, c.ToArray()),
                new SaveSection(SaveSections.Creatures, e.ToArray()),
                new SaveSection(SaveSections.Characters, ch.ToArray()),
            };

            if (padKb > 0)
            {
                sections.Add(new SaveSection(SaveSections.DebugPad, PadRecord(padKb, number)));
            }

            return SaveContainer.Write(header, sections.ToArray());
        }

        /// <summary>
        /// Какое поколение заменяет целый файл <paramref name="file"/> (поле МИР <see cref="SaveSections.WorldField.Replaces"/>,
        /// 0-037); не заменяет или файл не целый — 0. Разбирается только раздел МИР — без снимка генерации.
        /// </summary>
        public static ulong ReadReplaces(SaveFile file)
        {
            if (file == null || !file.IsOk || !file.HasSection(SaveSections.World))
            {
                return 0;
            }

            var r = new RecordReader(file.Section(SaveSections.World));
            while (r.Next())
            {
                if (r.Field == SaveSections.WorldField.Replaces)
                {
                    return r.UInt();
                }
            }

            return 0;
        }

        /// <summary>Записи раздела «ОТЛАДКА»: байты детерминированного узора по номеру поколения (приёмка 0.4).</summary>
        private static byte[] PadRecord(int padKb, ulong number)
        {
            var w = new RecordWriter(padKb * 1024 + 16);
            var bytes = new byte[padKb * 1024];
            ulong s = number * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < bytes.Length; i++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                bytes[i] = (byte)(s >> 33);
            }

            w.WriteBytes(1, bytes);
            return w.ToArray();
        }

        /// <summary>
        /// Положить сохранение поверх мира, загруженного из снимка генерации. Сначала всё разбирается
        /// и проверяется, потом трогается мир: ошибка не оставляет мир наполовину загруженным.
        /// Несовпадение с миром (зерно, форма, версия генератора, SHA-256 снимка) — <see cref="InvalidDataException"/>.
        /// </summary>
        public static LoadedWorldState Load(SaveFile file, WorldModel world, byte[] baseSha256)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            file.EnsureOk();

            // МИР.
            string name = string.Empty;
            ulong seed = 0;
            int width = 0;
            int height = 0;
            bool wraps = false;
            int generatorVersion = 0;
            byte[] sha = Array.Empty<byte>();
            int nextId = 1;
            int worldTime = 0;
            var r = new RecordReader(file.Section(SaveSections.World));
            while (r.Next())
            {
                switch (r.Field)
                {
                    case SaveSections.WorldField.Name:
                        name = r.Text();
                        break;
                    case SaveSections.WorldField.Seed:
                        seed = r.UInt();
                        break;
                    case SaveSections.WorldField.Width:
                        width = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.Height:
                        height = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.Wraps:
                        wraps = r.Bool();
                        break;
                    case SaveSections.WorldField.GeneratorVersion:
                        generatorVersion = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.BaseSha256:
                        sha = r.Bytes().ToArray();
                        break;
                    case SaveSections.WorldField.NextEntityId:
                        nextId = r.UIntAsInt();
                        break;
                    case SaveSections.WorldField.WorldTime:
                    {
                        // 1-014: такты суток; вне диапазона — грузим как 0 (часы перезапускаются с полного дня).
                        ulong wt = r.UInt();
                        worldTime = wt < 36_000 ? (int)wt : 0;
                        break;
                    }
                }
            }

            MapShape shape = world.Shape;
            if (seed != world.Seed || width != shape.Width || height != shape.Height || wraps != shape.Wraps || generatorVersion != world.GeneratorVersion)
            {
                throw new InvalidDataException(
                    $"Сохранение от другого мира: зерно {seed}, {width}×{height}{(wraps ? " тор" : "")}, генератор v{generatorVersion}; " +
                    $"снимок генерации — зерно {world.Seed}, {shape}, генератор v{world.GeneratorVersion}.");
            }

            if (baseSha256 != null && !((ReadOnlySpan<byte>)sha).SequenceEqual(baseSha256))
            {
                throw new InvalidDataException("Сохранение от другого снимка генерации: SHA-256 world.base не совпал.");
            }

            // ЧАНКИ.
            var coords = new List<ChunkCoord>();
            var revisions = new List<uint>();
            var blobs = new List<byte[]>();
            var copies = new List<byte[]>();
            var check = new Chunk();
            int total = shape.ChunksX * shape.ChunksY;
            r = new RecordReader(file.Section(SaveSections.Chunks));
            while (r.Next())
            {
                if (r.Field != SaveSections.ChunksField.Chunk)
                {
                    continue;
                }

                int map = 0;
                int x = -1;
                int y = -1;
                uint revision = 0;
                byte[]? data = null;
                RecordReader cr = r.Record();
                while (cr.Next())
                {
                    switch (cr.Field)
                    {
                        case SaveSections.ChunkField.Map:
                            map = cr.UIntAsInt();
                            break;
                        case SaveSections.ChunkField.X:
                            x = cr.UIntAsInt();
                            break;
                        case SaveSections.ChunkField.Y:
                            y = cr.UIntAsInt();
                            break;
                        case SaveSections.ChunkField.Revision:
                            ulong rv = cr.UInt();
                            if (rv > uint.MaxValue)
                            {
                                throw new InvalidDataException($"Чанк № {coords.Count}: ревизия {rv} больше 32 бит.");
                            }

                            revision = (uint)rv;
                            break;
                        case SaveSections.ChunkField.Data:
                            data = cr.Bytes().ToArray();
                            break;
                    }
                }

                int index = coords.Count;
                if (map != 0)
                {
                    throw new InvalidDataException($"Чанк № {index}: карта {map} — отдельных карт в формате {SaveModule.FormatVersion} нет.");
                }

                if ((uint)x >= (uint)shape.ChunksX || (uint)y >= (uint)shape.ChunksY || data == null)
                {
                    throw new InvalidDataException($"Чанк № {index}: ({x}, {y}) вне карты {shape} или без данных.");
                }

                var c = new ChunkCoord(x, y);
                if (index > 0 && ChunkCoordOrder.Compare(coords[index - 1], c) >= 0)
                {
                    throw new InvalidDataException($"Чанк № {index} {c} повторён или не по порядку (после {coords[index - 1]}).");
                }

                if (index >= total)
                {
                    throw new InvalidDataException($"Чанков больше, чем на карте ({total}).");
                }

                byte[] blob = Chunk.Decompress(data);
                check.ReadBlob(blob); // проверка блока до того, как трогать мир
                coords.Add(c);
                revisions.Add(revision);
                blobs.Add(blob);
                copies.Add(data);
            }

            // СУЩЕСТВА.
            var entities = new List<SimEntity>();
            r = new RecordReader(file.Section(SaveSections.Creatures));
            while (r.Next())
            {
                if (r.Field != SaveSections.CreaturesField.Entity)
                {
                    continue;
                }

                SimEntity e = ReadEntity(r.Record(), shape);
                if (entities.Count > 0 && e.Id <= entities[entities.Count - 1].Id)
                {
                    throw new InvalidDataException($"Сущность #{e.Id}: номер не больше предыдущего #{entities[entities.Count - 1].Id}.");
                }

                entities.Add(e);
            }

            // ПЕРСОНАЖИ (формат 3; в формате 1–2 раздела нет — пусто).
            var characters = new List<CharacterRecord>();
            if (file.HasSection(SaveSections.Characters))
            {
                r = new RecordReader(file.Section(SaveSections.Characters));
                while (r.Next())
                {
                    if (r.Field != SaveSections.CharactersField.Character)
                    {
                        continue;
                    }

                    CharacterRecord ch = ReadCharacter(r.Record(), shape, characters.Count);
                    CheckCharacter(ch, characters, entities);
                    characters.Add(ch);
                }
            }

            // Всё разобрано — кладём чанки.
            for (int i = 0; i < coords.Count; i++)
            {
                world.GetOrCreateChunk(coords[i]).Load(blobs[i], copies[i], revisions[i], changedSinceBase: true);
            }

            return new LoadedWorldState(
                file.Header.Number, file.Header.WorldTick, name, nextId, entities.ToArray(), coords.Count, characters.ToArray(),
                file.Header.FormatVersion, worldTime);
        }

        private static CharacterRecord ReadCharacter(RecordReader r, MapShape shape, int index)
        {
            var c = new CharacterRecord { Name = string.Empty, Inv = Inventory.StartingKit() }; // сумки в формате 3 нет — стартовый набор (1-002)
            int x = 0;
            int y = 0;
            int carryX = 0;
            int carryY = 0;
            int speed = MovementTick.BaseSpeedX10;
            Dodge dodge = default;
            MeleeAction melee = default;
            while (r.Next())
            {
                switch (r.Field)
                {
                    case SaveSections.CharacterField.Host:
                        c.IsHost = r.Bool();
                        break;
                    case SaveSections.CharacterField.Name:
                        c.Name = r.Text();
                        break;
                    case SaveSections.CharacterField.Inv:
                        {
                            ReadOnlySpan<byte> bag = r.Bytes();
                            if (bag.Length != Inventory.PayloadBytes)
                            {
                                throw new InvalidDataException($"Персонаж № {index}: сумка {bag.Length} байт, ждали {Inventory.PayloadBytes}.");
                            }

                            c.Inv = Inventory.ReadPayload(bag);
                        }

                        break;
                    case SaveSections.CharacterField.EntityId:
                        c.EntityId = r.UIntAsInt();
                        break;
                    case SaveSections.CharacterField.X:
                        x = r.IntAsInt();
                        break;
                    case SaveSections.CharacterField.Y:
                        y = r.IntAsInt();
                        break;
                    case SaveSections.CharacterField.CarryX:
                        carryX = r.IntAsInt();
                        break;
                    case SaveSections.CharacterField.CarryY:
                        carryY = r.IntAsInt();
                        break;
                    case SaveSections.CharacterField.SpeedX10:
                        speed = r.IntAsInt();
                        break;
                    case SaveSections.CharacterField.DodgeElapsed:
                        dodge.Elapsed = (byte)r.UIntAsInt(Dodge.ElapsedCap);
                        break;
                    case SaveSections.CharacterField.DodgeDashLeft:
                        dodge.DashLeft = (byte)r.UIntAsInt(Dodge.DashFrames);
                        break;
                    case SaveSections.CharacterField.DodgeCooldown:
                        dodge.Cooldown = (byte)r.UIntAsInt(Dodge.CooldownFrames);
                        break;
                    case SaveSections.CharacterField.DodgeDirection:
                        dodge.Direction = (byte)r.UIntAsInt(InputFrame.MaxDirectionCode);
                        break;
                    case SaveSections.CharacterField.MeleePhase:
                        melee.Phase = (byte)r.UIntAsInt(MeleeAction.PhaseRecover);
                        break;
                    case SaveSections.CharacterField.MeleeFramesLeft:
                        melee.FramesLeft = (byte)r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.CharacterField.MeleeIndex:
                        melee.Index = (byte)r.UIntAsInt(MeleeAction.SeriesSize - 1);
                        break;
                    case SaveSections.CharacterField.MeleeBuffered:
                        melee.Buffered = r.UIntAsInt(1) != 0;
                        break;
                    case SaveSections.CharacterField.MeleeIdleFrames:
                        melee.IdleFrames = (byte)r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.CharacterField.MeleeResolved:
                        melee.Resolved = r.UIntAsInt(1) != 0;
                        break;
                    case SaveSections.CharacterField.MeleeAimRaw:
                        melee.AimRaw = (ushort)r.UIntAsInt(ushort.MaxValue);
                        break;
                    case SaveSections.CharacterField.MeleeOriginX:
                        melee.OriginX = r.IntAsInt();
                        break;
                    case SaveSections.CharacterField.MeleeOriginY:
                        melee.OriginY = r.IntAsInt();
                        break;
                }
            }

            // Длина имени не проверяется: имя проверил вход, а из-за подписи мир не должен перестать грузиться.
            if (c.EntityId < 0)
            {
                throw new InvalidDataException($"Персонаж № {index} «{c.Name}»: номер сущности {c.EntityId}.");
            }

            if (c.EntityId == 0 && ((uint)x >= (uint)shape.WidthUnits || (uint)y >= (uint)shape.HeightUnits))
            {
                throw new InvalidDataException($"Персонаж «{c.Name}»: позиция ({x}, {y}) вне карты {shape}.");
            }

            c.Mover = new Mover(new WorldPos(x, y), speed) { Carry = new Int2(carryX, carryY), Dodge = dodge, Melee = melee };
            return c;
        }

        /// <summary>Ключи не повторяются (один хозяин; имена — по <see cref="CharacterNames.Same"/>); сущность персонажа в мире есть и она — персонаж.</summary>
        private static void CheckCharacter(in CharacterRecord c, List<CharacterRecord> before, List<SimEntity> entities)
        {
            for (int i = 0; i < before.Count; i++)
            {
                CharacterRecord o = before[i];
                if (c.IsHost && o.IsHost)
                {
                    throw new InvalidDataException($"Два персонажа хозяина: «{o.Name}» и «{c.Name}».");
                }

                if (!c.IsHost && !o.IsHost && CharacterNames.Same(c.Name, o.Name))
                {
                    throw new InvalidDataException($"Два персонажа с именем «{c.Name}».");
                }

                if (c.EntityId > 0 && c.EntityId == o.EntityId)
                {
                    throw new InvalidDataException($"Персонажи «{o.Name}» и «{c.Name}» — одна сущность #{c.EntityId}.");
                }
            }

            if (c.EntityId > 0)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    if (entities[i].Id == c.EntityId && entities[i].Kind == SimEntity.KindCharacter)
                    {
                        return;
                    }
                }

                throw new InvalidDataException($"Персонаж «{c.Name}»: сущности-персонажа #{c.EntityId} в разделе СУЩЕСТВА нет.");
            }
        }

        private static SimEntity ReadEntity(RecordReader r, MapShape shape)
        {
            int id = 0;
            int kind = 0;
            int x = 0;
            int y = 0;
            int carryX = 0;
            int carryY = 0;
            int speed = MovementTick.BaseSpeedX10;
            int direction = 0;
            int buttons = 0;
            MeleeAction melee = default;
            Dodge dodge = default; // формат 1 полей уворота не пишет — уворот нулевой («ещё не было»)
            Inventory inv = default; // сумку в раздел сущностей пишет только формат 5 (1-004); в форматах 1–4 её нет
            EnemyBrain brain = default; // мозг (формат 6, 1-007): в форматах 1–5 поля нет — Archetype 0, враг стоит
            bool hasBrain = false;
            // Бой (формат 6, 1-007): в форматах 1–5 полей нет — нули, Simulation.RestoreDefaults подставит дефолты спавна.
            int health = 0, maxHealth = 0, armor = 0, mass = 0, invuln = 0, regen = 0, regenCarry = 0, combat = 0, dead = 0;
            int windup = 0, recover = 0, immune = 0, atkTarget = 0, atkRaw = 0, period = 0, atkOriginX = 0, atkOriginY = 0, atkFacing = 0;
            int knockLeft = 0, knockSpeed = 0, knockDir = 0, aimAngle = 0, inputSlot = 0;
            while (r.Next())
            {
                switch (r.Field)
                {
                    case SaveSections.EntityField.Id:
                        id = r.UIntAsInt();
                        break;
                    case SaveSections.EntityField.Kind:
                        kind = r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.X:
                        x = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Y:
                        y = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.CarryX:
                        carryX = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.CarryY:
                        carryY = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.SpeedX10:
                        speed = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Direction:
                        direction = r.UIntAsInt(InputFrame.MaxDirectionCode);
                        break;
                    case SaveSections.EntityField.Buttons:
                        buttons = r.UIntAsInt(ushort.MaxValue);
                        break;
                    case SaveSections.EntityField.DodgeElapsed:
                        dodge.Elapsed = (byte)r.UIntAsInt(Dodge.ElapsedCap);
                        break;
                    case SaveSections.EntityField.DodgeDashLeft:
                        dodge.DashLeft = (byte)r.UIntAsInt(Dodge.DashFrames);
                        break;
                    case SaveSections.EntityField.DodgeCooldown:
                        dodge.Cooldown = (byte)r.UIntAsInt(Dodge.CooldownFrames);
                        break;
                    case SaveSections.EntityField.DodgeDirection:
                        dodge.Direction = (byte)r.UIntAsInt(InputFrame.MaxDirectionCode);
                        break;
                    case SaveSections.EntityField.MeleePhase:
                        melee.Phase = (byte)r.UIntAsInt(MeleeAction.PhaseRecover);
                        break;
                    case SaveSections.EntityField.MeleeFramesLeft:
                        melee.FramesLeft = (byte)r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.MeleeIndex:
                        melee.Index = (byte)r.UIntAsInt(MeleeAction.SeriesSize - 1);
                        break;
                    case SaveSections.EntityField.MeleeBuffered:
                        melee.Buffered = r.UIntAsInt(1) != 0;
                        break;
                    case SaveSections.EntityField.MeleeIdleFrames:
                        melee.IdleFrames = (byte)r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.MeleeResolved:
                        melee.Resolved = r.UIntAsInt(1) != 0;
                        break;
                    case SaveSections.EntityField.MeleeAimRaw:
                        melee.AimRaw = (ushort)r.UIntAsInt(ushort.MaxValue);
                        break;
                    case SaveSections.EntityField.MeleeOriginX:
                        melee.OriginX = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.MeleeOriginY:
                        melee.OriginY = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Inv:
                        {
                            ReadOnlySpan<byte> bag = r.Bytes();
                            if (bag.Length != Inventory.PayloadBytes)
                            {
                                throw new InvalidDataException($"Сущность #{id}: сумка {bag.Length} байт, ждали {Inventory.PayloadBytes}.");
                            }

                            inv = Inventory.ReadPayload(bag);
                        }

                        break;
                    case SaveSections.EntityField.BrainArchetype:
                        brain.Archetype = (byte)r.UIntAsInt(byte.MaxValue);
                        hasBrain = true;
                        break;
                    case SaveSections.EntityField.BrainState:
                        brain.State = (EnemyState)r.UIntAsInt((int)EnemyState.Interest); // 1-009: «Интерес» = 7 — верх границы
                        break;
                    case SaveSections.EntityField.BrainTargetId:
                        brain.TargetId = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainStateTicks:
                        brain.StateTicks = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainLostTicks:
                        brain.LostTicks = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainLastHitTick:
                        brain.LastHitTick = r.Int();
                        break;
                    case SaveSections.EntityField.BrainLastHitBy:
                        brain.LastHitBy = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainLastHitAmount:
                        brain.LastHitAmount = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainHomeX:
                        brain.HomeX = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainHomeY:
                        brain.HomeY = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainGoalX:
                        brain.GoalX = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainGoalY:
                        brain.GoalY = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainBlockedSince:
                        brain.BlockedSince = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainIgnoreTargetId:
                        brain.IgnoreTargetId = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainIgnoreUntil:
                        brain.IgnoreUntil = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainCauseId:
                        brain.CauseId = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainInterestUntil:
                        brain.InterestUntil = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.BrainFarSince:
                        brain.FarSince = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Health:
                        health = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.MaxHealth:
                        maxHealth = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.Armor:
                        armor = r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.Mass:
                        mass = r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.InvulnTicks:
                        invuln = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.RegenTicks:
                        regen = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.RegenCarryX100:
                        regenCarry = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.CombatTicks:
                        combat = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.DeadTicks:
                        dead = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.AttackWindup:
                        windup = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.AttackRecover:
                        recover = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.AttackImmune:
                        immune = r.UIntAsInt(int.MaxValue);
                        break;
                    case SaveSections.EntityField.AttackTarget:
                        atkTarget = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.AttackRaw:
                        atkRaw = r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.AttackPeriod:
                        period = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.AttackOriginX:
                        atkOriginX = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.AttackOriginY:
                        atkOriginY = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.AttackFacingRaw:
                        atkFacing = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.KnockLeft:
                        knockLeft = r.UIntAsInt(byte.MaxValue);
                        break;
                    case SaveSections.EntityField.KnockSpeedX10:
                        knockSpeed = r.IntAsInt();
                        break;
                    case SaveSections.EntityField.KnockDir:
                        knockDir = r.UIntAsInt(InputFrame.MaxDirectionCode);
                        break;
                    case SaveSections.EntityField.AimAngle16:
                        aimAngle = r.UIntAsInt(ushort.MaxValue);
                        break;
                    case SaveSections.EntityField.InputSlot:
                        inputSlot = r.UIntAsInt(InputFrame.MaxSlots - 1);
                        break;
                }
            }

            if (id <= 0)
            {
                throw new InvalidDataException($"Сущность без номера (номер {id}).");
            }

            if ((uint)x >= (uint)shape.WidthUnits || (uint)y >= (uint)shape.HeightUnits)
            {
                throw new InvalidDataException($"Сущность #{id}: позиция ({x}, {y}) вне карты {shape}.");
            }

            return new SimEntity
            {
                Id = id,
                Kind = (byte)kind,
                Mover = new Mover(new WorldPos(x, y), speed)
                {
                    Carry = new Int2(carryX, carryY),
                    Dodge = dodge,
                    Melee = melee,
                    KnockLeft = (byte)knockLeft,
                    KnockSpeedX10 = knockSpeed,
                    KnockDir = (byte)knockDir,
                },
                Input = InputFrame.FromCode((byte)direction, (ushort)buttons, (ushort)aimAngle, (byte)inputSlot),
                Inv = inv,
                Brain = hasBrain ? brain : default,
                Health = (short)health,
                MaxHealth = (short)maxHealth,
                Armor = (byte)armor,
                Mass = (byte)mass,
                InvulnTicks = invuln,
                RegenTicks = regen,
                RegenCarryX100 = regenCarry,
                CombatTicks = combat,
                DeadTicks = dead,
                AttackWindup = windup,
                AttackRecover = recover,
                AttackImmune = immune,
                AttackTarget = atkTarget,
                AttackRaw = (byte)atkRaw,
                AttackPeriod = period,
                AttackOrigin = new WorldPos(atkOriginX, atkOriginY),
                AttackFacingRaw = (short)atkFacing,
            };
        }
    }
}
