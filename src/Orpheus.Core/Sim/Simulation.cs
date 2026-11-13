using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Trig;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>Ввод одной сущности на такт: номер + кадр (для журнала ввода).</summary>
    public readonly struct EntityInput
    {
        public readonly int EntityId;
        public readonly InputFrame Frame;

        public EntityInput(int entityId, InputFrame frame)
        {
            EntityId = entityId;
            Frame = frame;
        }

        public override string ToString() => $"#{EntityId} {Frame}";
    }

    /// <summary>
    /// Такт сервера 30 Гц над миром (docs/07, 2.4 и 11.5): мир — <see cref="WorldModel"/>, сущности —
    /// список по возрастанию номера. Часов здесь нет: такт — номер, а не время; в обычном режиме его
    /// зовут по часам, в режиме симуляции — подряд (сервер, <c>--sim</c>).
    /// <para><b>Порядок такта</b> (<see cref="Step"/>), фиксирован — часть правил повторяемости:</para>
    /// <list type="number">
    /// <item><b>Ввод</b>: кадры, пришедшие до такта (<see cref="SetInput"/>), становятся текущим вводом сущностей;
    /// сущность без нового кадра держит прежний.</item>
    /// <item><b>ИИ врагов</b> (1-007): решения по номеру сущности — цель, состояние, направление (10 Гц:
    /// каждый третий такт); путь — планировщик <see cref="PathScheduler"/> (2 поиска за такт, без часов — детерминизм).</item>
    /// <item><b>Движение</b>: сущности по номеру, каждая — <see cref="Mover.Step"/> по своему вводу
    /// (коллизия с тайлами мира, каким он был в начале такта); перешедшие границу чанка переносятся в сетке
    /// <see cref="Grid"/> (docs/07, 4.4). После — <b>выталкивание</b> пар персонаж↔враг и враг↔враг (1-007, 8.5).</item>
    /// <item><b>Бой</b> (1-001, `02` 6, 8.3, 13.5): таймеры здоровья (неуязвимость, «в бою», регенерация),
    /// детерминированный цикл атаки врага (замах фиксирует сектор началом, удар — в конце замаха по позиции цели),
    /// смерть и возрождение у <see cref="Simulation.RespawnCenter"/>; трупы удалённых враг — в конце такта.</item>
    /// <item><b>Правки мира пачками</b>: запросы такта (<see cref="RequestEdit(TileCoord, byte, ushort)"/>)
    /// группируются по чанку — <b>одна пачка на чанк за такт</b> (3.7, backlog [такт сервера]): чанки по
    /// порядку (Y, затем X), внутри чанка — по порядку прихода; на чанк один вызов
    /// <see cref="WorldModel.ApplyEdits"/>, ревизия +1. Кто держит такт, тот держит и правило — вызывающему
    /// не надо помнить о нём.</item>
    /// <item><b>События</b>: исходы правок и новые ревизии чанков — в буфер событий такта (<see cref="Events"/>),
    /// он живёт до следующего такта.</item>
    /// </list>
    /// <para>
    /// В такте нет выделений памяти (11.6): сущности, запросы правок, исходы и события — в заранее
    /// выделенных массивах; ёмкости задаются в конструкторе, переполнение — отказ, а не рост.
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Тактов в секунду (docs/07, 2.4).</summary>
        public const int TickRate = 30;

        /// <summary>Хэш состояния — на каждом 300-м такте (docs/07, 11.2 и 11.5): раз в 10 с.</summary>
        public const int HashInterval = 300;

        private readonly WorldModel _world;
        private readonly MapShape _shape;
        private readonly TileSolid _solid;

        private readonly SimEntity[] _entities;
        private readonly InputFrame[] _pending;
        private readonly bool[] _hasPending;

        // Кадры ввода на ближайший такт у сетевых персонажей (0-019): MaxFramesPerTick на место; −1 — шаг тактом по Input.
        private readonly InputFrame[] _frames;
        private readonly int[] _frameCount;
        private int _entityCount;
        private int _nextId = 1;
        private readonly EntityChunkGrid _grid;

        private readonly EditRequest[] _edits;
        private readonly EditRequest[] _editScratch;
        private readonly TileEdit[] _batch = new TileEdit[TilePatch.MaxEdits];
        private readonly int[] _batchOf;          // место правки очереди в пачке чанка; −1 — отказ до пачки (0-028)
        private readonly EditResult[] _preResult; // исход отказа до пачки: существо, враг, лишняя сверх тайлов, нет ресурсов (1-011)
        private readonly ushort[] _preObj;        // что стояло на тайле до пачки (добыча копания — 1-010)
        private readonly byte[] _preFloor;        // пол тайла до пачки (ключ возврата пола — 1-011)
        private readonly BuildRecord[] _preBuild; // запись постройки до тайла (плюс _preHasBuild — 1-011)
        private readonly bool[] _preHasBuild;
        private readonly byte[] _costPaid;        // 1 — стоимость постановки списана (1-011): отказ после пачки — вернуть
        private readonly EditResult[] _results = new EditResult[TilePatch.MaxEdits];
        private int _editCount;

        // Истечения (1-011): обломки (10 с) и флаг «урон последние 10 с» — снимаются правкой своего тайла
        // в начале такта через очередь (ревизия чанка за такт растёт ровно на 1 — патч-цепочка клиента).
        private readonly ExpireEntry[] _expire;
        private int _expireCount;

        // ---------- Двери (1-012) ----------
        // Состояние двери — флаги её записи постройки (DoorOpen / DoorKeepOpen); список дверей —
        // производный индекс (в конструкторе по миру, дальше — регистрация в ApplyEditBatches и ленивое
        // снятие в StepDoors). Порядок обхода детерминирован (11.5): порядок списка + порядок сущностей.
        public const int DoorAutoCloseTicks = 30; // дверь сама закроется через 1 с свободы тайла и соседей
        public const int DoorLockTicks = 30;      // после F автооткрытие молчит 1 с

        // Индекс без потолка и с каноническим порядком (Y, X): мир с любым числом дверей даёт живой и
        // загруженный копии один и тот же индекс (потолок при переполнении отбрасывал бы в живой и в
        // загруженной копии разные наборы — автологика по ним молчала бы в одной из копий).
        private const int DoorCapacityStep = 64;
        private int[] _doorX = new int[DoorCapacityStep];
        private int[] _doorY = new int[DoorCapacityStep];
        private long[] _doorLockUntil = new long[DoorCapacityStep];
        private int _doorCount;

        // Защёлка переднего фронта F (кнопка CloseDoor) и позиция до движения такта (вектор «движется к двери»).
        private readonly bool[] _closeDoorPrev;
        private readonly WorldPos[] _posBefore;

        // Порядок выталкивания врага при закрытии: E, S, W, N (Input8 0=E по часовой).
        private static readonly int[] PushDx = { 1, 0, -1, 0 };
        private static readonly int[] PushDy = { 0, 1, 0, -1 };

        private readonly int _buildMapReserve;
        private readonly SimEvent[] _events;
        private int _eventCount;
        private bool _eventsOverflow;

        // ---------- ИИ врагов (1-007) ----------
        // Пути — планировщик Pathing (окно 48 × 48, очередь). Без часов: ровно maxPerTick поисков за такт —
        // детерминизм (11.5), бюджет по тактам, не по времени. Владелец запроса — СЛОТ в _entities.
        private readonly PathFinder _pathFinder = new PathFinder();
        private readonly SimulationPathTerrain _pathTerrain;
        private readonly PathBuffer[] _pathBuf;
        private PathScheduler _paths;

        /// <summary>Счётчик правок мира (растёт на каждой пачке, изменившей чанк): «изменился тайл» —
        /// враги пересчитывают путь сразу (1-013, 9.3); кэш Enclosed живёт до следующей ревизии (з.2).</summary>
        private long _editRev;

        /// <summary>Ревизия правок мира (для тестов и кэшей пути).</summary>
        public long EditRevision => _editRev;

        /// <summary>Поисков пути выполнено всего (счётчик PathFinder; для тестов бюджета и кэша кармана, 1-013).</summary>
        public long PathSearches => _pathFinder.Searches;

        /// <summary>Поисков завершено в последний такт планировщика (предел — PathMaxPerTick; 1-013 з.1).</summary>
        public int PathLastTickCompleted => _paths.LastTickCompleted;

        /// <summary>Запросов в очереди планировщика (число преследующих одновременно; очередь полна — переспрос в следующий такт решений).</summary>
        public const int PathQueueCapacity = 64;

        /// <summary>Поисков A* за такт (2 × 7–30 мс укладывается в бюджет такта; при 40 врагах очередь — за 20 тактов).</summary>
        public const int PathMaxPerTick = 2;

        /// <param name="world">Мир; такт меняет его только правками фазы 3.</param>
        /// <param name="maxEntities">Ёмкость списка сущностей.</param>
        /// <param name="maxEditsPerTick">Ёмкость очереди правок на такт; лишние запросы получают отказ.</param>
        /// <param name="reserveBuildMaps">Запас словарей записей построек (<see cref="BuildMapPool"/>): столько чанков
        /// могут получить первую постройку без выделения в такте; пополняет <see cref="RefillPools"/>.</param>
        public Simulation(WorldModel world, int maxEntities = 64, int maxEditsPerTick = 4096, int reserveBuildMaps = DefaultBuildMapReserve)
        {
            if (maxEntities <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntities));
            }

            if (maxEditsPerTick <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEditsPerTick));
            }

            _world = world ?? throw new ArgumentNullException(nameof(world));
            _shape = world.Shape;
            _solid = IsBlocked;
            _entities = new SimEntity[maxEntities];
            _pending = new InputFrame[maxEntities];
            _hasPending = new bool[maxEntities];
            _frames = new InputFrame[maxEntities * MaxFramesPerTick];
            _frameCount = new int[maxEntities];
            _grid = new EntityChunkGrid(maxEntities);
            _pathTerrain = new SimulationPathTerrain(this);
            _pathBuf = new PathBuffer[maxEntities];
            for (int i = 0; i < maxEntities; i++)
            {
                _frameCount[i] = -1;
                _pathBuf[i] = new PathBuffer();
            }

            _paths = NewPathScheduler();
            _edits = new EditRequest[maxEditsPerTick];
            _editScratch = new EditRequest[maxEditsPerTick];
            _batchOf = new int[maxEditsPerTick];
            _preResult = new EditResult[maxEditsPerTick];
            _preObj = new ushort[maxEditsPerTick];
            _preFloor = new byte[maxEditsPerTick];
            _preBuild = new BuildRecord[maxEditsPerTick];
            _preHasBuild = new bool[maxEditsPerTick];
            _costPaid = new byte[maxEditsPerTick];
            _expire = new ExpireEntry[maxEditsPerTick]; // истечений за игру больше, чем правок за такт, не бывает (1-011)
            _closeDoorPrev = new bool[maxEntities];
            _posBefore = new WorldPos[maxEntities];

            // Точка возрождения по умолчанию — центр карты (SpawnCenter ботов, ServerHost.DefaultSpawnCenter).
            _respawnCenter = new WorldPos(
                (_shape.Width / 2 << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                (_shape.Height / 2 << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);

            // Событий на такт: исход каждой правки + ревизия каждого чанка — не больше 2 × правок.
            _events = new SimEvent[maxEditsPerTick * 2];
            _buildMapReserve = reserveBuildMaps;
            RefillPools();
            IndexDoors(); // двери из мира (сейв/генерация) — индекс строится до первого такта (1-012)
        }

        /// <summary>Запас словарей построек по умолчанию: 16 чанков с первой постройкой подряд *(прикидка)*.</summary>
        public const int DefaultBuildMapReserve = 16;

        /// <summary>
        /// Пополнить пулы до запаса — <b>вне такта</b> (между тактами; сервер зовёт после каждого <see cref="Step"/>):
        /// здесь выделяется память, если в такте пул отдал словари. Полон — ничего не делает.
        /// </summary>
        public void RefillPools() => _world.BuildMaps.Reserve(_buildMapReserve);

        /// <summary>Мир.</summary>
        public WorldModel World => _world;

        /// <summary>Форма карты.</summary>
        public MapShape Shape => _shape;

        /// <summary>Сколько тактов прошло; следующий <see cref="Step"/> выполнит такт <c>Tick + 1</c>.</summary>
        public long Tick { get; private set; }

        /// <summary>
        /// Время мира в тактах суток, [0; <see cref="DayClock.TicksPerDay"/>) (1-014, <c>01</c> 6).
        /// Двигается каждым <see cref="Step"/> на <see cref="DayScale"/>; в паузе <see cref="Step"/> не вызывается — часы стоят (0-036).
        /// </summary>
        public int WorldTime { get; private set; }

        /// <summary>Дефолт <see cref="DayScale"/>: 1 такт часов за такт симуляции.</summary>
        public const int DayScaleDefault = 1;

        /// <summary>
        /// Сколько тактов часы идут за такт симуляции (отладка времени суток — <c>--debug-day-scale</c>; ≥ 1).
        /// На детерминизм не влияет: сервер задаст одинаково на всех узлах.
        /// </summary>
        public int DayScale { get; set; } = DayScaleDefault;

        /// <summary>Положить время мира (старт по <c>--debug-time</c>; значение нормализуется в сутки). Только до первого такта — вместе с загрузкой.</summary>
        public void SetWorldTime(int ticks)
        {
            WorldTime = DayClock.Wrap(ticks);
        }

        /// <summary>Число сущностей.</summary>
        public int EntityCount => _entityCount;

        /// <summary>Ёмкость списка сущностей.</summary>
        public int Capacity => _entities.Length;

        /// <summary>
        /// Сетка сущностей по чанкам (docs/07, 4.4): места в <see cref="Entities"/> по чанку. Верна между тактами
        /// (после <see cref="Step"/>, добавления, удаления, загрузки).
        /// </summary>
        public EntityChunkGrid Grid => _grid;

        /// <summary>Сущности по возрастанию номера.</summary>
        public ReadOnlySpan<SimEntity> Entities => new ReadOnlySpan<SimEntity>(_entities, 0, _entityCount);

        /// <summary>События последнего такта.</summary>
        public ReadOnlySpan<SimEvent> Events => new ReadOnlySpan<SimEvent>(_events, 0, _eventCount);

        /// <summary>Правок в очереди на ближайший такт.</summary>
        public int PendingEditCount => _editCount;

        /// <summary>Проходимость тайлов мира для движения (тот же делегат, что в такте).</summary>
        public TileSolid Solid => _solid;

        // ---------- Сущности ----------



        // ---------- Ввод ----------


        /// <summary>Наибольшее число кадров ввода на такт у сетевого персонажа (<see cref="SetFramesAt"/>).</summary>
        public const int MaxFramesPerTick = 8;


        // ---------- Правки ----------


        // ---------- Такт ----------

        /// <summary>Один такт: ввод → движение → правки мира пачками (одна на чанк) → события.</summary>
        public void Step()
        {
            long tick = Tick + 1;
            _eventCount = 0;
            _eventsOverflow = false;

            // 0. Истечения (1-011): обломки (10 с) и флаг «урон последние 10 с» — правки в очередь этого же такта.
            StepExpiry(tick);

            // 1. Ввод.
            for (int i = 0; i < _entityCount; i++)
            {
                if (_hasPending[i])
                {
                    _entities[i].Input = _pending[i];
                    _hasPending[i] = false;
                    ApplySlot(ref _entities[i], _pending[i].Slot); // смена ячейки — тем же кадром, что и ввод (1-002)
                }
            }

            // 2. ИИ врагов (1-007): решения — цель, состояние, направление; движение — фаза 3 по тем же правилам.
            StepAI(tick);

            // 3. Движение — по номеру сущности: тактом по вводу или кадрами ввода (сетевой персонаж, 0-019).
            // Позиция до движения — для автооткрытия дверей («движется к двери», 1-012).
            for (int i = 0; i < _entityCount; i++)
            {
                _posBefore[i] = _entities[i].Mover.Position;
            }

            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue; // мёртвый (труп, ждёт возрождения) не движется (1-001, 13.5, 8.3)
                }

                int frames = _frameCount[i];

                // Дверь (1-012): передний фронт F (CloseDoor — бит «нажатие в этом кадре», как уворот) — запрос
                // закрытия в очередь этого же такта; сработает ровно один раз за такт.
                if (frames < 0)
                {
                    bool held = (_entities[i].Input.Buttons & InputButtons.CloseDoor) != 0;
                    if (held && !_closeDoorPrev[i])
                    {
                        CloseDoorNearest(i, tick);
                    }
                    _closeDoorPrev[i] = held;
                }
                else
                {
                    bool latched = _closeDoorPrev[i], fired = false;
                    int baseIdx = i * MaxFramesPerTick;
                    for (int k = 0; k < frames; k++)
                    {
                        bool pressed = (_frames[baseIdx + k].Buttons & InputButtons.CloseDoor) != 0;
                        if (pressed && !latched && !fired)
                        {
                            CloseDoorNearest(i, tick);
                            fired = true;
                        }
                        latched = pressed;
                    }
                    _closeDoorPrev[i] = latched;
                }

                if (frames < 0)
                {
                    _entities[i].Mover.Step(_shape, _entities[i].Input.Direction, _solid);
                    continue;
                }

                for (int k = 0; k < frames; k++)
                {
                    InputFrame f = _frames[i * MaxFramesPerTick + k];
                    _entities[i].Input = f;
                    ApplySlot(ref _entities[i], f.Slot); // ячейка меняется в том же кадре, что и предсказание клиента (1-002)
                    bool swing = f.AttackPressed && Items.KindOf(_entities[i].Inv.Held) == Items.KindMelee; // удар — с ближним оружием в руках (1-004)
                    _entities[i].Mover.StepFrame(_shape, f.Direction, f.DodgePressed, swing, f.Angle16, _solid); // с уворотом (0-029) и мечом (1-004)
                }

                _frameCount[i] = -1;
            }

            // 3.5. Выталкивание (1-007, 8.5 «толкаются»): персонаж↔враг и враг↔враг — расталкивание 2 тайла/с
            // после движения, целочисленное и по порядку номеров — детерминизм (11.5).
            StepSeparation();

            // 4. Бой (1-001): таймеры здоровья, атаки врагов, смерть и возрождение — после движения,
            // сетка подхватывает teleport возрождения ниже.
            StepCombat(tick);

            // Сетка по чанкам: перешедшие границу чанка переносятся (4.4).
            for (int i = 0; i < _entityCount; i++)
            {
                _grid.Update(i, Torus.ChunkOf(_shape, _entities[i].Mover.Position));
            }

            // 4.5. Двери (1-012): после движения — автооткрытие («кто-то идёт к двери»), автозакрытие (30 тактов
            // свободы тайла и соседей); правки — в очередь, применятся фазой 5 этого же такта.
            StepDoors(tick);

            // 5. Правки мира: одна пачка на чанк за такт.
            ApplyEditBatches(tick);

            // 5.5. Пути (1-007): поиски такта — после правок, по миру текущего такта; ровно PathMaxPerTick — детерминизм.
            _paths.RunTick(tick);

            // 6. События: буфер такта закрыт; переполнение — ошибка ёмкости, а не тихая потеря.
            if (_eventsOverflow)
            {
                throw new InvalidOperationException($"Такт {tick}: событий больше {_events.Length}.");
            }

            Tick = tick;

            // 6.5. Часы мира (1-014, 01 6): такт суток — каждый такт, целыми шагами (детерминизм 11.5).
            // Пауза (0-036) — Step не вызывается, часы стоят сами.
            WorldTime = (WorldTime + DayScale) % DayClock.TicksPerDay;

            // 7. Трупы (8.3; 1-001): враг с истёкшим тайром трупа удаляется (RemoveEntity — сдвиг списка,
            // Rebuild сетки; в конце такта — «вне такта»). Порядок: от хвоста (по убыванию номера) — сдвиги не мешают.
            for (int i = _entityCount - 1; i >= 0; i--)
            {
                if (_entities[i].Kind == SimEntity.KindEnemy && _entities[i].Health <= 0 && _entities[i].DeadTicks == 0)
                {
                    RemoveEntity(_entities[i].Id);
                }
            }

            // 8. Спавнер и исчезновение (1-009, 8.5/8.8): после трупов, «вне такта» — добавления и удаления
            // сущностей (сдвиг списка) не искажают фазы. Только сервер: SpawnEnabled (клиент — снимки).
            StepSpawnAndDespawn(tick);
        }


        // ---------- Истечения обломков и флага урона (1-011) ----------

        private struct ExpireEntry
        {
            public long EndTick;
            public ChunkCoord Chunk;
            public ushort Index;

            /// <summary>Снимаем обломки (иначе — флаг «урон последние 10 с»).</summary>
            public bool Debris;
        }


        // ---------- Двери (1-012) ----------

        /// <summary>Число дверей в индексе (тесты: мир просканирован).</summary>
        public int DoorCount => _doorCount;


        // ---------- Бой (1-001: `02` 6.1–6.6, 8.1–8.3, 13.5) ----------

        /// <summary>Здоровье персонажа (уровень 0, `02` 6.3: 100 + 10 × уровень, уровень в вехе 1 — 0).</summary>
        public const int MaxHealthCharacter = 100;

        /// <summary>Здоровье врага архетипа «Ближний» (манекен, разбойник — `02` 8.2).</summary>
        public const int MaxHealthMeleeEnemy = 35;

        /// <summary>Сырой урон удара «Ближний» (`02` 8.2: 10 ударов убивают персонажа 100 HP без брони).</summary>
        public const int StrikeRawMelee = 10;

        /// <summary>Неуязвимость после урона: 0,4 с = 12 тактов (`02` 6.3; только у персонажа, у врагов нет).</summary>
        public const int InvulnStrikeTicks = 12;

        /// <summary>Неуязвимость после возрождения: 3 с = 90 тактов, снимается первой атакой (13.5).</summary>
        public const int InvulnRespawnTicks = 90;

        /// <summary>Регенерация через 6 с = 180 тактов после последнего полученного урона (`02` 6.3; приёмка 6.10 п. 3).</summary>
        public const int RegenDelayTicks = 180;

        /// <summary>Сотых накопителя регенерации на +1 HP: +MaxHealth за такт → 3000 = 1 %/с (без float).</summary>
        public const int RegenCarryPerHealth = 3000;

        /// <summary>Флаг «в бою»: 8 с = 240 тактов после нанесённого или полученного урона (`02` 6.3).</summary>
        public const int CombatFlagTicks = 240;

        /// <summary>Смерть персонажа → возрождение через 5 с = 150 тактов (13.5; приёмка 13.6 п. 1).</summary>
        public const int RespawnDelayTicks = 150;

        /// <summary>Враг: 0,5 с анимации смерти + 10 с трупа без коллизии = 315 тактов, затем удаление (`02` 8.3).</summary>
        public const int CorpseTicks = 315;

        /// <summary>Замах «Ближний»: 0,4 с = 12 тактов (`02` 8.1).</summary>
        public const int WindupTicks = 12;

        /// <summary>Отход после удара: 0,6 с = 18 тактов (`02` 8.1).</summary>
        public const int RecoverTicks = 18;

        /// <summary>Отход после прерывания: 0,5 с = 15 тактов (`02` 6.6).</summary>
        public const int InterruptRecoverTicks = 15;

        /// <summary>Иммунитет к повторному прерыванию: 1,5 с = 45 тактов (`02` 6.6; приёмка 6.10 п. 4).</summary>
        public const int InterruptImmuneTicks = 45;

        /// <summary>Прерывание замаха врага: урон ≥ 15 % максимума цели (`02` 6.6).</summary>
        public const int InterruptPctEnemy = 15;

        /// <summary>Прерывание действия персонажа: удар ≥ 10 % его максимума (`02` 6.6; хук натяжения лука — 1-006).</summary>
        public const int InterruptPctCharacter = 10;

        /// <summary>Цикл манекена 1-001: замах через этот период после конца отхода (≈ период <see cref="MeleeDummy"/>, 1,1 с).</summary>
        public const int DummyPeriodTicks = 33;

        private WorldPos _respawnCenter;

        /// <summary>
        /// Точка возрождения (13.5; решение 1-001: в вехе 1 — точка появления персонажа, <c>SpawnCenter</c> ботов):
        /// по умолчанию — центр карты (<see cref="ServerHost" /><c>.DefaultSpawnCenter</c>), сервер ставит свою (шов 0-017).
        /// </summary>
        public WorldPos RespawnCenter
        {
            get => _respawnCenter;
            set => _respawnCenter = Torus.Wrap(_shape, value);
        }


        // ---------- ИИ врагов (1-007: docs/02 8.1, 8.3–8.5, 9.3) ----------

        /// <summary>Скорость бродяги 1,5 тайла/с = 154 (X10) — «бродит, не спешит» (8.1).</summary>
        public const int RogueWanderSpeedX10 = 154;

        /// <summary>Скорость «домой» (Lost) 3 тайла/с = 307 (X10) — быстрее бродяги, медленнее преследования (8.3).</summary>
        public const int RogueHomeSpeedX10 = 307;

        /// <summary>Тактов на решение ИИ: 3 (10 Гц при 30 Гц такта, 9.3); фаза — по номеру сущности, чтобы не в один такт.</summary>
        public const int AiDecisionTicks = 3;


        /// <summary>Шаг выборки линии удара, unit (1/4 тайла): тайловую преграду не перепрыгнуть.</summary>
        public const int StrikeLosStepUnits = 64;


        private void AddEvent(in SimEvent e)
        {
            if (_eventCount == _events.Length)
            {
                _eventsOverflow = true;
                return;
            }

            _events[_eventCount++] = e;
        }

    }
}
