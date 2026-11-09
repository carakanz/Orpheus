using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Platform.Pc;
using Orpheus.Save;
using Orpheus.Server;
using Orpheus.Tools.SaveTool;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Сохранения (карточка 0-009; docs/07 6.3–6.5, 6.10 п. 2, 3, 7; docs/08 3.0 строка «Сохранения»):
    /// кодек записей, контейнер, туда-обратно с хэшем состояния, поколения и проверка <c>.tmp</c>, хвосты backlog
    /// [сохранения], <c>orpheus-save dump</c>, сервер. Приёмка 0.4 (убийство процесса) — <see cref="SaveKillTests"/>.
    /// </summary>
    public class SaveTests : IDisposable
    {
        private const ulong Seed = 0x0009;

        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public SaveTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "orpheus-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        // ---------- Кодек записей ----------

        [Fact]
        public void D00_Save_Crc32_KnownVector()
        {
            Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
            Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(Crc32.Compute(data), Crc32.Append(Crc32.Compute(data.AsSpan(0, 4)), data.AsSpan(4)));
        }

        [Fact]
        public void D00_Save_Records_ValuesRoundTrip()
        {
            var w = new RecordWriter(4);
            w.WriteUInt(1, 0);
            w.WriteUInt(2, ulong.MaxValue);
            w.WriteInt(3, long.MinValue);
            w.WriteInt(4, long.MaxValue);
            w.WriteInt(5, -1);
            w.WriteText(6, "Орфей");
            w.WriteBytes(7, new byte[] { 1, 2, 3 });
            int mark = w.BeginRecord(300);
            w.WriteUInt(1, 42);
            int inner = w.BeginRecord(2);
            w.WriteText(1, "вложенная");
            w.EndRecord(inner);
            w.EndRecord(mark);
            w.WriteBool(100000, true);

            var r = new RecordReader(w.Written);
            Assert.True(r.Next());
            Assert.Equal(0UL, r.UInt());
            Assert.Equal(0, r.Length); // ноль — пустое значение
            Assert.True(r.Next());
            Assert.Equal(ulong.MaxValue, r.UInt());
            Assert.True(r.Next());
            Assert.Equal(long.MinValue, r.Int());
            Assert.True(r.Next());
            Assert.Equal(long.MaxValue, r.Int());
            Assert.True(r.Next());
            Assert.Equal(-1L, r.Int());
            Assert.Equal(1, r.Length);
            Assert.True(r.Next());
            Assert.Equal("Орфей", r.Text());
            Assert.True(r.Next());
            Assert.Equal(new byte[] { 1, 2, 3 }, r.Bytes().ToArray());
            Assert.True(r.Next());
            Assert.Equal(300, r.Field);
            RecordReader sub = r.Record();
            Assert.True(sub.Next());
            Assert.Equal(42UL, sub.UInt());
            Assert.True(sub.Next());
            RecordReader subsub = sub.Record();
            Assert.True(subsub.Next());
            Assert.Equal("вложенная", subsub.Text());
            Assert.False(subsub.Next());
            Assert.False(sub.Next());
            Assert.True(r.Next());
            Assert.Equal(100000, r.Field);
            Assert.True(r.Bool());
            Assert.False(r.Next());
        }

        /// <summary>Незнакомое поле пропускается по длине (любого типа, с вложенными), отсутствующее — умолчание (6.3).</summary>
        [Fact]
        public void D00_Save_Records_UnknownFieldSkipped_MissingFieldDefault()
        {
            var w = new RecordWriter();
            w.WriteUInt(1, 7);
            w.WriteBytes(50, new byte[1000]);
            int m = w.BeginRecord(51);
            w.WriteText(1, "из будущей версии");
            w.WriteUInt(2, 5);
            w.EndRecord(m);
            w.WriteText(52, "ещё");
            w.WriteInt(3, -9);

            // Читатель «старой версии» знает поля 1, 2, 3; поля 2 в данных нет.
            ulong a = 0;
            long b = 12345; // умолчание
            long c = 0;
            var r = new RecordReader(w.Written);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1:
                        a = r.UInt();
                        break;
                    case 2:
                        b = r.Int();
                        break;
                    case 3:
                        c = r.Int();
                        break;
                }
            }

            Assert.Equal(7UL, a);
            Assert.Equal(12345L, b);
            Assert.Equal(-9L, c);
        }

        [Fact]
        public void D00_Save_Records_BrokenData_Throws()
        {
            var w = new RecordWriter();
            w.WriteText(1, "строка подлиннее");
            int boundary = w.Length;
            w.WriteUInt(2, 99);
            byte[] data = w.ToArray();
            for (int cut = 1; cut < data.Length; cut++)
            {
                byte[] part = data.AsSpan(0, cut).ToArray();
                bool threw = false;
                try
                {
                    var r = new RecordReader(part);
                    while (r.Next())
                    {
                    }
                }
                catch (InvalidDataException)
                {
                    threw = true;
                }

                // Обрыв внутри записи — всегда ошибка; ровно на границе записи — просто меньше полей.
                Assert.True(threw || cut == boundary, $"обрыв на {cut} байте не замечен");
            }

            // Неверный тип знакомого поля.
            Assert.Throws<InvalidDataException>(() =>
            {
                var r = new RecordReader(data);
                r.Next();
                r.UInt();
            });

            // Целое длиннее 8 байт.
            byte[] longInt = { 1, (byte)RecordType.UInt, 9, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
            Assert.Throws<InvalidDataException>(() =>
            {
                var r = new RecordReader(longInt);
                r.Next();
                r.UInt();
            });
        }

        // ---------- Контейнер ----------

        [Fact]
        public void D00_Save_Container_HeaderTableSections_CrcAndSha()
        {
            var header = new SaveHeader(SaveModule.FormatVersion, SaveModule.BuildVersion, 127, 3600, 1_762_400_000_000);
            byte[] file = SaveContainer.Write(header, new[]
            {
                new SaveSection(SaveSections.World, new byte[] { 1, 2, 3 }),
                new SaveSection(SaveSections.Chunks, new byte[100]),
                new SaveSection(SaveSections.Creatures, Array.Empty<byte>()),
            });

            Assert.Equal("ORPHSAVE", Encoding.ASCII.GetString(file, 0, 8));
            Assert.Equal(SaveContainer.HeaderBytes + 3 * SaveContainer.TableEntryBytes + 103 + SaveContainer.ShaBytes, file.Length);
            byte[] sha = SHA256.HashData(file.AsSpan(0, file.Length - 32));
            Assert.Equal(sha, file.AsSpan(file.Length - 32).ToArray());

            SaveFile f = SaveContainer.Read(file);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(127UL, f.Header.Number);
            Assert.Equal(3600L, f.Header.WorldTick);
            Assert.Equal(1_762_400_000_000L, f.Header.DateUnixMs);
            Assert.Equal(new byte[] { 1, 2, 3 }, f.Section(SaveSections.World).ToArray());
            Assert.Equal(100, f.Section(SaveSections.Chunks).Length);
            Assert.True(f.HasSection(SaveSections.Creatures));
            Assert.False(f.HasSection(SaveSections.Marks));
            Assert.True(f.Section(SaveSections.Marks).IsEmpty);

            // Испорченный байт раздела ЧАНКИ: SHA и CRC именно этого раздела.
            byte[] bad = (byte[])file.Clone();
            bad[f.Sections[1].Offset + 50] ^= 0x10;
            SaveFile g = SaveContainer.Read(bad);
            Assert.Equal(SaveCheckStatus.Damaged, g.Status);
            Assert.False(g.ShaOk);
            Assert.True(g.Sections[0].CrcOk);
            Assert.False(g.Sections[1].CrcOk);
            Assert.Contains("ЧАНКИ", g.Error);

            // Каждый байт файла: порча любого замечается.
            for (int i = 0; i < file.Length; i++)
            {
                byte[] x = (byte[])file.Clone();
                x[i] ^= 0x01;
                Assert.False(SaveContainer.Read(x).IsOk, $"порча байта {i} не замечена");
            }

            // Любой обрыв — недописан.
            for (int n = 0; n < file.Length; n++)
            {
                Assert.False(SaveContainer.Read(file.AsSpan(0, n).ToArray()).IsOk, $"обрыв на {n} не замечен");
            }
        }

        [Fact]
        public void D00_Save_Container_NewerFormat_TooNew_NotDamaged()
        {
            var header = new SaveHeader(SaveModule.FormatVersion + 1, 9, 5, 0, 0);
            byte[] file = SaveContainer.Write(header, new[] { new SaveSection(SaveSections.World, new byte[] { 1 }) });
            SaveFile f = SaveContainer.Read(file);
            Assert.Equal(SaveCheckStatus.TooNew, f.Status);
            Assert.Contains("более новой версией", f.Error);
            Assert.Contains($"формат {SaveModule.FormatVersion + 1}, у тебя {SaveModule.FormatVersion}", f.Error);

            // Папка мира: новейшее — новее сборки → отказ, файлы не тронуты.
            WorldFolder folder = NewFolder("too-new");
            folder.WriteGeneration(1, ValidSave(1));
            File.WriteAllBytes(Path.Combine(folder.Dir, WorldFolder.GenerationFileName(2)), file);
            var e = Assert.Throws<InvalidDataException>(() => folder.Recover());
            Assert.Contains("более новой версией", e.Message);
            Assert.True(File.Exists(Path.Combine(folder.Dir, WorldFolder.GenerationFileName(2))));
        }

        // ---------- Мир туда-обратно ----------

        /// <summary>Чек-лист: «сохранение → загрузка даёт тот же хэш состояния». И дальше идут одинаково, и файл побайтно тот же.</summary>
        [Fact]
        public void D00_Save_RoundTrip_SameStateHash_ThenSameFuture_ByteIdentical()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 8);
            live.Run(ticks: 900, editsPerTick: 3);
            int changed = live.Sim.World.GetChangedSinceBase().Length;
            Assert.True(changed > 300, $"изменённых чанков {changed}");

            StateHashResult before = StateHash.Compute(live.Sim);
            WorldSaveData data = WorldSave.Capture(live.Sim, live.BaseSha, "тест");
            byte[] save = WorldSave.Build(data, 7, 1_762_400_000_000);
            _out.WriteLine($"1024², {changed} изменённых чанков, 8 сущностей, такт {live.Sim.Tick}: сохранение {save.Length} байт");

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            StateHashResult after = StateHash.Compute(loaded);
            Assert.Equal(before.Tick, after.Tick);
            Assert.Equal(before.World, after.World);
            Assert.Equal(before.Total, after.Total);
            Assert.Equal(live.Sim.NextEntityId, loaded.NextEntityId);

            // Побайтно: сохранение загруженного — тот же файл.
            byte[] again = WorldSave.Build(WorldSave.Capture(loaded, live.BaseSha, "тест"), 7, 1_762_400_000_000);
            Assert.Equal(save, again);

            // Будущее одно: одинаковый ввод → одинаковые хэши (остаток скорости, ввод, ревизии — всё на месте).
            for (int t = 0; t < 300; t++)
            {
                var frame = new InputFrame(Input8.FromIndex(t / 40 % 8));
                foreach (Simulation s in new[] { live.Sim, loaded })
                {
                    for (int i = 0; i < s.EntityCount; i++)
                    {
                        s.SetInputAt(i, frame);
                    }

                    s.RequestEdit(new TileCoord(t * 3, 500), FloorId.Sand, ObjectId.Empty);
                    s.Step();
                }

                Assert.Equal(StateHash.Compute(live.Sim).Total, StateHash.Compute(loaded).Total);
            }
        }

        /// <summary>Сохранение без изменённых чанков и сущностей — пустые разделы, загрузка даёт мир генерации.</summary>
        [Fact]
        public void D00_Save_RoundTrip_FreshWorld()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 0);
            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "пустой"), 1, 0);
            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(StateHash.Compute(live.Sim).Total, StateHash.Compute(loaded).Total);
            Assert.Equal(0, loaded.EntityCount);
            Assert.Empty(loaded.World.GetChangedSinceBase());
        }

        /// <summary>
        /// Близнец <see cref="D00_Save_RoundTrip_SameStateHash_ThenSameFuture_ByteIdentical"/> с уворотом (П по 0-029, з. 1):
        /// сущности жмут уворот кадрами (<see cref="Simulation.SetFramesAt"/>), на такте сохранения одна в рывке, одна в откате,
        /// у одной счётчик «с начала» упёрся в 255. Сохранение → загрузка — тот же хэш, тот же уворот, тот же файл и то же будущее.
        /// </summary>
        [Fact]
        public void D00_Save_RoundTrip_Dodge_SameStateHash_ThenSameFuture_ByteIdentical()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 8);
            live.Run(ticks: 120, editsPerTick: 2);
            for (int t = 0; t < 200; t++)
            {
                StepDodging(live.Sim, t);
            }

            Dodge[] dodges = DodgesOf(live.Sim);
            Assert.Contains(dodges, d => d.Dashing);
            Assert.Contains(dodges, d => !d.Dashing && d.Cooldown != 0);
            Assert.Contains(dodges, d => d.Elapsed == Dodge.ElapsedCap && d.Direction != 0);

            StateHashResult before = StateHash.Compute(live.Sim);
            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "уворот"), 9, 1_762_400_000_000);
            _out.WriteLine($"уворот на такте {live.Sim.Tick}: {string.Join("; ", dodges.Select(d => d.ToString()))}");

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(dodges, DodgesOf(loaded));
            Assert.Equal(before.Total, StateHash.Compute(loaded).Total);
            Assert.Equal(save, WorldSave.Build(WorldSave.Capture(loaded, live.BaseSha, "уворот"), 9, 1_762_400_000_000));

            // Будущее одно: те же кадры с нажатиями — откат и рывок посреди сохранения доигрываются одинаково.
            for (int t = 200; t < 500; t++)
            {
                StepDodging(live.Sim, t);
                StepDodging(loaded, t);
                Assert.Equal(StateHash.Compute(live.Sim).Total, StateHash.Compute(loaded).Total);
            }
        }

        /// <summary>Сохранение формата 1 (без полей уворота) грузится: уворот нулевой, остальное на месте (6.7, поле с умолчанием).</summary>
        [Fact]
        public void D00_Save_Format1_NoDodgeFields_LoadsWithZeroDodge()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 4);
            live.Run(ticks: 60, editsPerTick: 2);
            for (int t = 0; t < 30; t++)
            {
                StepDodging(live.Sim, t);
            }

            Assert.Contains(DodgesOf(live.Sim), d => d.Direction != 0);
            SaveFile f = SaveContainer.Read(WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "v1"), 4, 0));
            Assert.Equal(SaveModule.FormatVersion, f.Header.FormatVersion);

            // Формат 1: те же записи сущностей без полей 10–13, заголовок версии 1.
            var creatures = new RecordWriter();
            var r = new RecordReader(f.Section(SaveSections.Creatures));
            while (r.Next())
            {
                int em = creatures.BeginRecord(SaveSections.CreaturesField.Entity);
                RecordReader er = r.Record();
                while (er.Next())
                {
                    if (er.Field >= SaveSections.EntityField.DodgeElapsed && er.Field <= SaveSections.EntityField.DodgeDirection)
                    {
                        continue;
                    }

                    if (er.Type == RecordType.UInt)
                    {
                        creatures.WriteUInt(er.Field, er.UInt());
                    }
                    else
                    {
                        creatures.WriteInt(er.Field, er.Int());
                    }
                }

                creatures.EndRecord(em);
            }

            SaveHeader h = f.Header;
            byte[] v1 = SaveContainer.Write(new SaveHeader(1, h.BuildVersion, h.Number, h.WorldTick, h.DateUnixMs), new[]
            {
                new SaveSection(SaveSections.World, f.Section(SaveSections.World).ToArray()),
                new SaveSection(SaveSections.Chunks, f.Section(SaveSections.Chunks).ToArray()),
                new SaveSection(SaveSections.Creatures, creatures.ToArray()),
            });

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, v1);
            Assert.Equal(live.Sim.EntityCount, loaded.EntityCount);
            for (int i = 0; i < loaded.EntityCount; i++)
            {
                SimEntity a = live.Sim.Entities[i];
                SimEntity b = loaded.Entities[i];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Mover.Position, b.Mover.Position);
                Assert.Equal(a.Mover.Carry, b.Mover.Carry);
                Assert.Equal(default(Dodge), b.Mover.Dodge);
            }

            Assert.Equal(StateHash.Compute(live.Sim).World, StateHash.Compute(loaded).World);
        }

        /// <summary>
        /// Сумка персонажа (1-002, формат 4): сохранение → загрузка — та же сумка (в мире и вне его);
        /// формат 3 (без поля 13) грузится со стартовым набором (6.7: новое поле с умолчанием).
        /// </summary>
        [Fact]
        public void D06_Save_Characters_InventoryRoundTrip_Format3DefaultsToStartingKit()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, Seed, bots: 2);
            live.Run(ticks: 30, editsPerTick: 1);

            var bag = Inventory.StartingKit();
            bag.Add(Material.Wood, 70_000);
            bag.Add(Material.Torch, 9);
            bag.Select(5);
            int entityId = live.Sim.Entities[0].Id;
            var characters = new[]
            {
                new CharacterRecord { IsHost = true, Name = "Орфей", EntityId = entityId, Inv = bag },
                new CharacterRecord { Name = "Эвридика", EntityId = 0, Inv = bag },
            };

            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "сумка", characters), 3, 0);
            LoadedWorldState st = WorldSave.Load(SaveContainer.Read(save), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(2, st.Characters.Length);
            Assert.Equal(bag, st.Characters[0].Inv);
            Assert.Equal(bag, st.Characters[1].Inv);
            Assert.Equal(entityId, st.Characters[0].EntityId);

            // Формат 3: те же персонажи без поля 13 — сумка по умолчанию стартовый набор.
            SaveFile f = SaveContainer.Read(save);
            var ch = new RecordWriter();
            var r = new RecordReader(f.Section(SaveSections.Characters));
            while (r.Next())
            {
                int cm = ch.BeginRecord(SaveSections.CharactersField.Character);
                RecordReader cr = r.Record();
                while (cr.Next())
                {
                    if (cr.Field == SaveSections.CharacterField.Inv)
                    {
                        continue;
                    }

                    switch (cr.Type)
                    {
                        case RecordType.UInt:
                            ch.WriteUInt(cr.Field, cr.UInt());
                            break;
                        case RecordType.Int:
                            ch.WriteInt(cr.Field, cr.Int());
                            break;
                        case RecordType.Text:
                            ch.WriteText(cr.Field, cr.Text());
                            break;
                        case RecordType.Bytes:
                            ch.WriteBytes(cr.Field, cr.Bytes());
                            break;
                    }
                }

                ch.EndRecord(cm);
            }

            SaveHeader h = f.Header;
            byte[] v3 = SaveContainer.Write(new SaveHeader(3, h.BuildVersion, h.Number, h.WorldTick, h.DateUnixMs), new[]
            {
                new SaveSection(SaveSections.World, f.Section(SaveSections.World).ToArray()),
                new SaveSection(SaveSections.Chunks, f.Section(SaveSections.Chunks).ToArray()),
                new SaveSection(SaveSections.Creatures, f.Section(SaveSections.Creatures).ToArray()),
                new SaveSection(SaveSections.Characters, ch.ToArray()),
            });

            LoadedWorldState old = WorldSave.Load(SaveContainer.Read(v3), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha);
            Assert.Equal(2, old.Characters.Length);
            Assert.Equal(Inventory.StartingKit(), old.Characters[0].Inv);
            Assert.Equal("Орфей", old.Characters[0].Name);
            Assert.True(old.Characters[0].IsHost);
            Assert.Equal(entityId, old.Characters[0].EntityId);
        }

        /// <summary>
        /// Такт кадрами ввода с уворотом: сущность 0 жмёт один раз (в такте 0) — дальше её счётчик «с начала» доходит до 255;
        /// остальные — раз в 55 тактов (110 кадров > рывок + откат 39 и > восстановления 25 выносливости, 1-003) со
        /// сдвигом по номеру, направление крутится.
        /// </summary>
        private static void StepDodging(Simulation sim, int t)
        {
            Span<InputFrame> frames = stackalloc InputFrame[2];
            for (int i = 0; i < sim.EntityCount; i++)
            {
                bool press = i == 0 ? t == 0 : t % 55 == (4 * i) % 55;
                Input8 dir = Input8.FromIndex((t / 13 + i) % 8);
                frames[0] = new InputFrame(dir, press ? InputButtons.Dodge : (ushort)0);
                frames[1] = new InputFrame(dir);
                sim.SetFramesAt(i, frames);
            }

            sim.Step();
            sim.RefillPools();
        }

        private static Dodge[] DodgesOf(Simulation sim)
        {
            var d = new Dodge[sim.EntityCount];
            for (int i = 0; i < d.Length; i++)
            {
                d[i] = sim.Entities[i].Mover.Dodge;
            }

            return d;
        }

        /// <summary>Незнакомые поле и раздел в настоящем сохранении: загрузка их пропускает (6.3, 6.7).</summary>
        [Fact]
        public void D00_Save_UnknownFieldAndSection_Skipped()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, bots: 2);
            live.Run(60, 2);
            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "w"), 3, 0);
            SaveFile f = SaveContainer.Read(save);

            // МИР + незнакомое поле 99 (вложенная запись), СУЩЕСТВА — поле 77 у каждой сущности, плюс раздел 200.
            var world = new RecordWriter();
            world.WriteBytes(1000, new byte[] { 9, 9 });
            int m = world.BeginRecord(99);
            world.WriteText(1, "поле будущей версии");
            world.EndRecord(m);
            byte[] worldSection = f.Section(SaveSections.World).ToArray().Concat(world.ToArray()).ToArray();

            // Сущности — те же поля + незнакомое поле 77 в каждой.
            var creatures = new RecordWriter();
            var r = new RecordReader(f.Section(SaveSections.Creatures));
            while (r.Next())
            {
                int em = creatures.BeginRecord(SaveSections.CreaturesField.Entity);
                creatures.WriteText(77, "будущее");
                RecordReader er = r.Record();
                while (er.Next())
                {
                    if (er.Type == RecordType.UInt)
                    {
                        creatures.WriteUInt(er.Field, er.UInt());
                    }
                    else
                    {
                        creatures.WriteInt(er.Field, er.Int());
                    }
                }

                creatures.EndRecord(em);
            }

            byte[] patched = SaveContainer.Write(f.Header, new[]
            {
                new SaveSection(SaveSections.World, worldSection),
                new SaveSection(SaveSections.Chunks, f.Section(SaveSections.Chunks).ToArray()),
                new SaveSection(SaveSections.Creatures, creatures.ToArray()),
                new SaveSection(200, new byte[] { 0xFF, 0xFE }),
            });

            Simulation loaded = SaveFixtures.Load(live.BaseBytes, patched);
            Assert.Equal(StateHash.Compute(live.Sim).Total, StateHash.Compute(loaded).Total);
        }

        [Fact]
        public void D00_Save_ForeignWorld_Rejected()
        {
            SaveFixtures.LiveWorld a = SaveFixtures.Create(1024, Seed, 1);
            SaveFixtures.LiveWorld b = SaveFixtures.Create(1024, Seed + 1, 1);
            byte[] saveA = WorldSave.Build(WorldSave.Capture(a.Sim, a.BaseSha, "a"), 1, 0);
            var e = Assert.Throws<InvalidDataException>(() => SaveFixtures.Load(b.BaseBytes, saveA));
            Assert.Contains("другого мира", e.Message);

            // Тот же мир по зерну и форме, но другой снимок генерации.
            WorldModel world = WorldSnapshot.LoadBase(a.BaseBytes);
            var wrongSha = new byte[32];
            e = Assert.Throws<InvalidDataException>(() => WorldSave.Load(SaveContainer.Read(saveA), world, wrongSha));
            Assert.Contains("снимка генерации", e.Message);
        }

        // ---------- Хвосты backlog [сохранения] ----------

        /// <summary>backlog: заголовок снимка генерации (зерно, версия генератора, форма) — под SHA-256. Перевёрнутый бит любого поля — отказ.</summary>
        [Fact]
        public void D00_Save_Backlog_BaseHeaderUnderSha256()
        {
            WorldModel world = Gen().Generate(MapShape.World(256), Seed);
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            for (int i = 0; i < WorldSnapshot.HeaderBytes; i++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    byte[] bad = (byte[])snapshot.Clone();
                    bad[i] ^= (byte)(1 << bit);
                    Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(bad));
                }
            }

            // Зерно (байты 10…17): именно отказ по SHA-256, а не по чему-то ещё.
            byte[] seedFlip = (byte[])snapshot.Clone();
            seedFlip[10] ^= 1;
            var e = Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(seedFlip));
            Assert.Contains("SHA-256", e.Message);
            Assert.Equal(WorldSnapshot.ContentHash(world), WorldSnapshot.StoredHash(snapshot));
        }

        /// <summary>backlog: повторные чанки при загрузке отвергаются — и в снимке (с верным SHA-256), и в сохранении.</summary>
        [Fact]
        public void D00_Save_Backlog_DuplicateChunks_Rejected()
        {
            // Снимок, где чанк (1, 0) записан дважды, а SHA-256 посчитан честно.
            var world = new WorldModel(MapShape.World(64), Seed, 1);
            world.SetBaseTile(new TileCoord(33, 0), FloorId.Sand, ObjectId.Empty);
            byte[] snapshot = WorldSnapshot.WriteBase(world);
            byte[] dup = DuplicateFirstChunkInSnapshot(snapshot, world);
            var e = Assert.Throws<InvalidDataException>(() => WorldSnapshot.LoadBase(dup));
            Assert.Contains("повторён", e.Message);

            // Сохранение: тот же чанк дважды (контейнер целый — SHA и CRC сходятся).
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, 1);
            live.Run(30, 5);
            SaveFile f = SaveContainer.Read(WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "w"), 1, 0));
            var r = new RecordReader(f.Section(SaveSections.Chunks));
            Assert.True(r.Next());
            int firstLen = r.ValueOffset + r.Length;
            byte[] chunks = f.Section(SaveSections.Chunks).ToArray();
            byte[] twice = chunks.AsSpan(0, firstLen).ToArray().Concat(chunks).ToArray();
            byte[] bad = SaveContainer.Write(f.Header, new[]
            {
                new SaveSection(SaveSections.World, f.Section(SaveSections.World).ToArray()),
                new SaveSection(SaveSections.Chunks, twice),
                new SaveSection(SaveSections.Creatures, f.Section(SaveSections.Creatures).ToArray()),
            });
            Assert.True(SaveContainer.Read(bad).IsOk);
            e = Assert.Throws<InvalidDataException>(() => SaveFixtures.Load(live.BaseBytes, bad));
            Assert.Contains("повторён", e.Message);
        }

        /// <summary>backlog: предел размера при распаковке — «бомба» отвергается, не разворачиваясь.</summary>
        [Fact]
        public void D00_Save_Backlog_DecompressLimit()
        {
            byte[] bomb = Deflate(new byte[64 << 20]); // 64 МБ нулей → ~65 КБ
            _out.WriteLine($"бомба: {bomb.Length} байт сжатых → 64 МБ");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var e = Assert.Throws<InvalidDataException>(() => Chunk.Decompress(bomb));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Contains(Chunk.MaxBlobBytes.ToString(), e.Message);
            Assert.True(allocated < 1 << 20, $"выделено {allocated} байт — распаковка не остановилась на пределе");

            // Ровно предел — можно (каждый тайл с записью постройки).
            Assert.Equal(Chunk.MaxBlobBytes, Chunk.Decompress(Deflate(new byte[Chunk.MaxBlobBytes])).Length);
            Assert.Throws<InvalidDataException>(() => Chunk.Decompress(Deflate(new byte[Chunk.MaxBlobBytes + 1])));

            // Бомба внутри сохранения с верными CRC и SHA — отказ загрузки, мир не тронут.
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, 0);
            var c = new RecordWriter();
            int m = c.BeginRecord(SaveSections.ChunksField.Chunk);
            c.WriteUInt(SaveSections.ChunkField.X, 3);
            c.WriteUInt(SaveSections.ChunkField.Y, 4);
            c.WriteBytes(SaveSections.ChunkField.Data, bomb);
            c.EndRecord(m);
            SaveFile f = SaveContainer.Read(WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "w"), 1, 0));
            byte[] bad = SaveContainer.Write(f.Header, new[]
            {
                new SaveSection(SaveSections.World, f.Section(SaveSections.World).ToArray()),
                new SaveSection(SaveSections.Chunks, c.ToArray()),
            });
            WorldModel world = WorldSnapshot.LoadBase(live.BaseBytes);
            Assert.Throws<InvalidDataException>(() => WorldSave.Load(SaveContainer.Read(bad), world, live.BaseSha));
            Assert.Empty(world.GetChangedSinceBase());
        }

        // ---------- Поколения и .tmp (6.5) ----------

        [Fact]
        public void D00_Save_Generations_KeepThreeLatest()
        {
            WorldFolder folder = NewFolder("keep");
            for (ulong n = 1; n <= 5; n++)
            {
                List<string> deleted = folder.WriteGeneration(n, ValidSave(n));
                Assert.Equal(n > 3 ? 1 : 0, deleted.Count);
            }

            Assert.Equal(new ulong[] { 3, 4, 5 }, folder.ListGenerations());
            Assert.Empty(Directory.GetFiles(folder.Dir, "*.tmp"));
            Assert.Throws<ArgumentOutOfRangeException>(() => folder.WriteGeneration(5, ValidSave(5)));

            var fresh = new WorldFolder(new PcStorage(), folder.Dir);
            WorldRecovery rec = fresh.Recover();
            Assert.Equal(5UL, rec.LatestNumber);
            Assert.Equal(6UL, fresh.NextNumber);
            Assert.Equal(1, rec.Checked);
            Assert.Equal("world.000005.save", rec.LatestName);
        }

        /// <summary>Чек-лист: битый CRC/SHA — поколение отвергается, берётся предыдущее; битое — в backups/damaged (6.10 п. 2).</summary>
        [Fact]
        public void D00_Save_Generations_DamagedLatest_PreviousLoaded_DamagedMoved()
        {
            var rng = new Random(9);
            for (int trial = 0; trial < 20; trial++)
            {
                WorldFolder folder = NewFolder("damaged" + trial);
                for (ulong n = 1; n <= 3; n++)
                {
                    folder.WriteGeneration(n, ValidSave(n));
                }

                string newest = Path.Combine(folder.Dir, WorldFolder.GenerationFileName(3));
                byte[] bytes = File.ReadAllBytes(newest);
                int at = rng.Next(bytes.Length);
                bytes[at] ^= (byte)(1 + rng.Next(255));
                File.WriteAllBytes(newest, bytes);

                var again = new WorldFolder(new PcStorage(), folder.Dir);
                WorldRecovery rec = again.Recover();
                Assert.Equal(2UL, rec.LatestNumber);
                Assert.Equal(2UL, rec.Latest!.Header.Number);
                Assert.Single(rec.Damaged);
                Assert.Equal("world.000003.save", rec.Damaged[0].Name);
                Assert.False(File.Exists(newest));
                Assert.True(File.Exists(Path.Combine(folder.DamagedDir, "world.000003.save")), $"испорчен байт {at}");
                Assert.Equal(4UL, again.NextNumber); // номер битого не переиспользуется
            }
        }

        /// <summary>Целый .tmp — дописанный и сброшенный, но не переименованный — загружается и переименовывается (6.10 п. 3).</summary>
        [Fact]
        public void D00_Save_Generations_WholeTmp_LoadedAndPromoted()
        {
            WorldFolder folder = NewFolder("tmp");
            folder.WriteGeneration(1, ValidSave(1));
            folder.WriteGeneration(2, ValidSave(2));
            string tmp = Path.Combine(folder.Dir, WorldFolder.GenerationFileName(3) + WorldFolder.TmpSuffix);
            File.WriteAllBytes(tmp, ValidSave(3));

            var again = new WorldFolder(new PcStorage(), folder.Dir);
            WorldRecovery rec = again.Recover();
            Assert.True(rec.PromotedTmp);
            Assert.Equal(3UL, rec.LatestNumber);
            Assert.Equal("world.000003.save", rec.LatestName);
            Assert.False(File.Exists(tmp));
            Assert.True(File.Exists(Path.Combine(folder.Dir, "world.000003.save")));
            Assert.Empty(rec.Damaged);

            // Недописанный .tmp — в damaged, берётся предыдущее.
            byte[] partial = ValidSave(4);
            File.WriteAllBytes(Path.Combine(folder.Dir, WorldFolder.GenerationFileName(4) + WorldFolder.TmpSuffix), partial.AsSpan(0, partial.Length / 2).ToArray());
            rec = new WorldFolder(new PcStorage(), folder.Dir).Recover();
            Assert.Equal(3UL, rec.LatestNumber);
            Assert.False(rec.PromotedTmp);
            Assert.Equal("world.000004.save.tmp", Assert.Single(rec.Damaged).Name);
            Assert.True(File.Exists(Path.Combine(folder.DamagedDir, "world.000004.save.tmp")));

            // Пустой .tmp (обрыв до первого байта) — так же.
            File.WriteAllBytes(Path.Combine(folder.Dir, WorldFolder.GenerationFileName(5) + WorldFolder.TmpSuffix), Array.Empty<byte>());
            rec = new WorldFolder(new PcStorage(), folder.Dir).Recover();
            Assert.Equal(3UL, rec.LatestNumber);
        }

        /// <summary>Все поколения битые — отказ, файлы не тронуты: мир не откатывается молча к снимку генерации.</summary>
        [Fact]
        public void D00_Save_Generations_AllDamaged_RefuseWithoutMoving()
        {
            WorldFolder folder = NewFolder("all");
            folder.WriteGeneration(1, ValidSave(1));
            folder.WriteGeneration(2, ValidSave(2));
            foreach (string f in Directory.GetFiles(folder.Dir, "*.save"))
            {
                byte[] b = File.ReadAllBytes(f);
                b[b.Length - 1] ^= 1;
                File.WriteAllBytes(f, b);
            }

            var e = Assert.Throws<InvalidDataException>(() => new WorldFolder(new PcStorage(), folder.Dir).Recover());
            Assert.Contains("повреждены", e.Message);
            Assert.Equal(2, Directory.GetFiles(folder.Dir, "*.save").Length);
            Assert.False(Directory.Exists(folder.DamagedDir));

            // Пустая папка — не ошибка: мир только из снимка генерации.
            Assert.Null(NewFolder("empty").Recover().Latest);
        }

        [Fact]
        public void D00_Save_GenerationName_Parse()
        {
            Assert.Equal("world.000127.save", WorldFolder.GenerationFileName(127));
            Assert.Equal("world.1234567.save", WorldFolder.GenerationFileName(1234567));
            Assert.True(WorldFolder.TryParseGeneration("world.000127.save", out ulong n, out bool tmp));
            Assert.Equal(127UL, n);
            Assert.False(tmp);
            Assert.True(WorldFolder.TryParseGeneration("world.000128.save.tmp", out n, out tmp));
            Assert.Equal(128UL, n);
            Assert.True(tmp);
            foreach (string bad in new[] { "world.base", "world.lock", "world..save", "world.12a.save", "world.000000.save", "x.000001.save", "world.000001.save.bak" })
            {
                Assert.False(WorldFolder.TryParseGeneration(bad, out _, out _), bad);
            }
        }

        // ---------- Хранилище ПК ----------

        [Fact]
        public void D00_Save_PcStorage_AtomicWrite_ReplaceAndProbeOrder()
        {
            var storage = new PcStorage();
            string path = Path.Combine(_dir, "server.json");
            storage.WriteAtomic(path, Encoding.UTF8.GetBytes("один"));
            storage.WriteAtomic(path, Encoding.UTF8.GetBytes("два")); // постоянное имя — замена (6.5 п. 6)
            Assert.Equal("два", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));

            var stages = new List<PcWriteStage>();
            long lastOffset = -1;
            storage.Probe = (p, stage, offset, total) =>
            {
                stages.Add(stage);
                if (stage == PcWriteStage.Writing)
                {
                    Assert.True(offset > lastOffset);
                    lastOffset = offset;
                    Assert.True(File.Exists(path + ".tmp"));
                }
            };
            storage.WriteAtomic(path, new byte[1500]);
            Assert.Equal(new[] { PcWriteStage.Writing, PcWriteStage.Writing, PcWriteStage.Writing, PcWriteStage.Writing, PcWriteStage.Written, PcWriteStage.Flushed, PcWriteStage.Moved }, stages);
            Assert.Equal(1500, new FileInfo(path).Length);

            // Замок: второй не берётся, после освобождения — берётся.
            string lockPath = Path.Combine(_dir, "world.lock");
            using (IDisposable? first = storage.TryLock(lockPath))
            {
                Assert.NotNull(first);
                Assert.Null(storage.TryLock(lockPath));
            }

            using IDisposable? again = storage.TryLock(lockPath);
            Assert.NotNull(again);

            // Перенос без замены: цель есть — отказ.
            File.WriteAllText(Path.Combine(_dir, "a"), "a");
            File.WriteAllText(Path.Combine(_dir, "b"), "b");
            Assert.ThrowsAny<IOException>(() => storage.MoveDurable(Path.Combine(_dir, "a"), Path.Combine(_dir, "b"), false));
            Assert.Equal("b", File.ReadAllText(Path.Combine(_dir, "b")));
        }

        /// <summary>Чек-лист: «ядро без P/Invoke» — в Core, WorldGen, Net, Save нет ни одного extern-метода; P/Invoke сохранений — в Platform.Pc.</summary>
        [Fact]
        public void D00_Save_CoreHasNoPInvoke_PlatformPcHas()
        {
            foreach (string name in new[] { "Orpheus.Core", "Orpheus.WorldGen", "Orpheus.Net", "Orpheus.Save" })
            {
                Assert.Empty(PInvokes(Assembly.Load(new AssemblyName(name))));
            }

            string[] pc = PInvokes(typeof(PcStorage).Assembly);
            Assert.Contains("MoveFileExW", pc);
            Assert.Contains("FlushFileBuffers", pc);
            Assert.Contains("fsync", pc);
            Assert.Contains("rename", pc);
        }

        // ---------- orpheus-save dump ----------

        [Fact]
        public void D00_Save_Dump_PrintsSectionsAndFields()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, Seed, 2);
            live.Run(30, 2);
            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "Долина"), 127, 1_762_400_000_000);
            var o = new StringWriter();
            Assert.True(SaveDump.Write("world.000127.save", save, o));
            string text = o.ToString();
            _out.WriteLine(text.Length > 3000 ? text.Substring(0, 3000) + "…" : text);
            foreach (string s in new[]
                     {
                         "состояние: целый", $"ORPHSAVE, формат {SaveModule.FormatVersion}", "номер 127", "такт 30", "SHA-256: сошёлся",
                         "[МИР] id 1", "[ЧАНКИ] id 2", "[СУЩЕСТВА] id 6", "CRC32 0x", "1 имя: «Долина»",
                         $"2 зерно: {Seed}", "3 ширина: 1024", "1 чанк: запись", "4 ревизия:", "5 сжатый блок:",
                         "1 сущность: запись", "3 x, 1/256 тайла:", "7 скорость ×10: 512", "13 уворот: направление:",
                     })
            {
                Assert.Contains(s, text);
            }

            byte[] bad = (byte[])save.Clone();
            bad[SaveContainer.Read(save).Sections[1].Offset + 3] ^= 0xFF;
            o = new StringWriter();
            Assert.False(SaveDump.Write("bad", bad, o));
            Assert.Contains("ПОВРЕЖДЁН", o.ToString());
            Assert.Contains("SHA-256: НЕ СОШЁЛСЯ", o.ToString());
            Assert.Contains("[ЧАНКИ] id 2", o.ToString());
            Assert.Contains("— НЕ СОШЁЛСЯ", o.ToString());

            o = new StringWriter();
            Assert.True(SaveDump.Write("world.base", live.BaseBytes, o));
            Assert.Contains("снимок генерации", o.ToString());
            Assert.Contains("целый", o.ToString());
        }

        // ---------- Сервер ----------

        /// <summary>Сервер: автосохранение по интервалу и при остановке; перезапуск без --create — тот же мир и такт (6.10 п. 5).</summary>
        [Fact]
        public void D00_Save_Server_AutosaveAndStop_RestartLoadsSameState()
        {
            string world = Path.Combine(_dir, "w");
            ServerResult first = Run("--world", world, "--create", "--seed", "7", "--size", "1024", "--sim", "--bots", "4:wander",
                "--ticks", "1000", "--autosave", "10");
            Assert.Equal(0, first.ExitCode);
            Assert.Equal(1000, first.Ticks);
            Assert.Equal(4, first.SavesWritten); // такты 300, 600, 900 + остановка на 1000
            Assert.Equal(4UL, first.LastSavedNumber);
            Assert.Equal(new ulong[] { 2, 3, 4 }, new WorldFolder(new PcStorage(), world).ListGenerations());
            Assert.True(File.Exists(Path.Combine(world, WorldFolder.BaseFileName)));

            ServerResult second = Run("--world", world, "--sim", "--bots", "4:wander", "--ticks", "0");
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(4UL, second.LoadedNumber);
            Assert.Equal(1000, second.StartTick);
            Assert.Equal(first.FinalHash, second.FinalHash);
            Assert.Equal(0, second.SavesWritten); // без тактов новое поколение не пишется

            ServerResult third = Run("--world", world, "--sim", "--bots", "4:wander", "--ticks", "100");
            Assert.Equal(0, third.ExitCode);
            Assert.Equal(1000, third.StartTick);
            Assert.Equal(1, third.SavesWritten);
            Assert.Equal(5UL, third.LastSavedNumber);
            ServerResult fourth = Run("--world", world, "--sim", "--ticks", "0");
            Assert.Equal(third.FinalHash, fourth.FinalHash);
            Assert.Equal(1100, fourth.StartTick);

            // --create на существующий мир — отказ, мир цел.
            ServerResult again = Run("--world", world, "--create", "--sim", "--ticks", "1");
            Assert.Equal(2, again.ExitCode);
            Assert.Equal(new ulong[] { 3, 4, 5 }, new WorldFolder(new PcStorage(), world).ListGenerations());
        }

        /// <summary>Два сервера на одну папку мира — второй не стартует: «мир уже открыт» (6.10 п. 7).</summary>
        [Fact]
        public void D00_Save_Server_SecondOnSameWorld_Refused()
        {
            string world = Path.Combine(_dir, "w");
            Assert.Equal(0, Run("--world", world, "--create", "--sim", "--ticks", "1").ExitCode);
            var folder = new WorldFolder(new PcStorage(), world);
            using (folder.Lock())
            {
                var console = new StringWriter();
                ServerResult r = ServerRun.Run(ServerOptions.Parse(new[] { "--world", world, "--sim", "--ticks", "1", "--logs", Path.Combine(_dir, "logs") }), console);
                Assert.Equal(2, r.ExitCode);
                Assert.Contains("Мир уже открыт", console.ToString());
            }

            Assert.Equal(0, Run("--world", world, "--sim", "--ticks", "1").ExitCode);
        }

        [Fact]
        public void D00_Save_Server_Options()
        {
            ServerOptions o = ServerOptions.Parse(new[] { "--world", "w", "--autosave", "60", "--debug-save-crash", "5" });
            Assert.Equal(60, o.AutosaveSeconds);
            Assert.Equal(5UL, o.DebugSaveCrashSeed);
            Assert.Equal(120, ServerOptions.Parse(new[] { "--world", "w" }).AutosaveSeconds);
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--world", "w", "--autosave", "0" }));
            Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--world", "w", "--autosave", "1801" }));
        }

        // ---------- Помощники ----------

        private ServerResult Run(params string[] args) => ServerRun.Run(ServerOptions.Parse(args), null);

        private WorldFolder NewFolder(string name) => new WorldFolder(new PcStorage(), Path.Combine(_dir, name));

        private static byte[] ValidSave(ulong number)
        {
            var w = new RecordWriter();
            w.WriteUInt(1, number);
            return SaveContainer.Write(new SaveHeader(SaveModule.FormatVersion, SaveModule.BuildVersion, number, (long)number * 3600, 0),
                new[] { new SaveSection(SaveSections.World, w.ToArray()) });
        }

        private static string[] PInvokes(Assembly asm) =>
            asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                .Select(m => m.Name)
                .ToArray();

        private static IWorldGenerator Gen() => Orpheus.WorldGen.WorldGenModule.Current;

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var d = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                d.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        /// <summary>Снимок с первым чанком, записанным дважды подряд, и честным SHA-256 по алгоритму формата 2.</summary>
        private static byte[] DuplicateFirstChunkInSnapshot(byte[] snapshot, WorldModel world)
        {
            int head = WorldSnapshot.HeaderBytes + WorldSnapshot.HashBytes;
            int len = BitConverter.ToInt32(snapshot, head + 12);
            int chunkBytes = WorldSnapshot.ChunkHeaderBytes + len;
            byte[] first = snapshot.AsSpan(head, chunkBytes).ToArray();
            byte[] body = first.Concat(snapshot.AsSpan(head).ToArray()).ToArray();
            byte[] header = snapshot.AsSpan(0, WorldSnapshot.HeaderBytes).ToArray();
            int count = BitConverter.ToInt32(header, 27) + 1;
            BitConverter.GetBytes(count).CopyTo(header, 27);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header);
            for (int at = 0; at < body.Length;)
            {
                int l = BitConverter.ToInt32(body, at + 12);
                hash.AppendData(body, at, 12);
                hash.AppendData(Chunk.Decompress(body.AsSpan(at + 16, l).ToArray()));
                at += 16 + l;
            }

            return header.Concat(hash.GetHashAndReset()).Concat(body).ToArray();
        }
    }
}
