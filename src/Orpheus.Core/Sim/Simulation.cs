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

        /// <summary>
        /// Добавить персонажа в позицию <paramref name="position"/> (свернётся в карту). Номер — следующий
        /// по счёту: список остаётся по возрастанию номера. Возвращает номер.
        /// </summary>
        public int AddEntity(WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            if (_entityCount == _entities.Length)
            {
                throw new InvalidOperationException($"Сущностей больше {_entities.Length} — ёмкость симуляции.");
            }

            return AddEntityWithId(_nextId, position, unitsPerTickX10);
        }

        /// <summary>
        /// Вернуть персонажа в мир с его движением (0-035, docs/07 7.4: вернувшийся игрок — тот же персонаж там же):
        /// позиция (свернётся в карту), остаток скорости, скорость, уворот — как в <paramref name="mover"/>; ввод пустой.
        /// Номер — следующий по счёту (номера не переиспользуются). Вне такта. Возвращает номер.
        /// </summary>
        public int AddEntity(in Mover mover)
        {
            int id = AddEntity(mover.Position, mover.UnitsPerTickX10);
            int slot = SlotOf(id);
            WorldPos at = _entities[slot].Mover.Position;
            _entities[slot].Mover = mover;
            _entities[slot].Mover.Position = at;
            return id;
        }

        /// <summary>
        /// Добавить врага-заглушку (<see cref="SimEntity.KindEnemy"/>; 0-028 — для правила «не ближе 2 тайлов к врагу»): стоит,
        /// пока ему не дали ввод. Здоровье архетипа «Ближний» (35), сам не замахивается (<see cref="StartAttack"/> — удар,
        /// <see cref="EnableAttackCycle"/> — манекен). Номер — следующий. Возвращает номер.
        /// </summary>
        public int AddEnemy(WorldPos position)
        {
            int id = AddEntity(position);
            int slot = SlotOf(id);
            _entities[slot].Kind = SimEntity.KindEnemy;
            _entities[slot].MaxHealth = MaxHealthMeleeEnemy;
            _entities[slot].Health = MaxHealthMeleeEnemy;
            _entities[slot].AttackRaw = (byte)StrikeRawMelee;
            return id;
        }

        /// <summary>
        /// Добавить персонажа с заданным номером (повтор журнала: номера из записи). Номер больше всех
        /// прежних — список по номеру не ломается.
        /// </summary>
        public int AddEntityWithId(int id, WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            if (_entityCount == _entities.Length)
            {
                throw new InvalidOperationException($"Сущностей больше {_entities.Length} — ёмкость симуляции.");
            }

            if (id < _nextId)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, $"Номер сущности должен быть ≥ {_nextId}.");
            }

            int slot = _entityCount++;
            _entities[slot] = new SimEntity
            {
                Id = id,
                Kind = SimEntity.KindCharacter,
                Mover = Mover.At(_shape, position, unitsPerTickX10),
                Input = InputFrame.None,
                Health = MaxHealthCharacter,
                MaxHealth = MaxHealthCharacter,
                Mass = 1,
                RegenTicks = RegenDelayTicks,
                AttackTarget = -1,
                AttackPeriod = -1,
            };
            _hasPending[slot] = false;
            _frameCount[slot] = -1;
            _grid.Insert(slot, Torus.ChunkOf(_shape, _entities[slot].Mover.Position));
            _nextId = id + 1;
            return id;
        }

        /// <summary>
        /// Убрать сущность (игрок ушёл; точка подключения сети, 0-008) — <b>вне такта</b>. Список остаётся по номеру,
        /// номер не переиспользуется. Нет такой — false.
        /// </summary>
        public bool RemoveEntity(int entityId)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return false;
            }

            int tail = _entityCount - slot - 1;
            Array.Copy(_entities, slot + 1, _entities, slot, tail);
            Array.Copy(_pending, slot + 1, _pending, slot, tail);
            Array.Copy(_hasPending, slot + 1, _hasPending, slot, tail);
            Array.Copy(_frameCount, slot + 1, _frameCount, slot, tail);
            Array.Copy(_frames, (slot + 1) * MaxFramesPerTick, _frames, slot * MaxFramesPerTick, tail * MaxFramesPerTick);
            _entityCount--;
            _entities[_entityCount] = default;
            _pending[_entityCount] = default;
            _hasPending[_entityCount] = false;
            _frameCount[_entityCount] = -1;
            _grid.Rebuild(_shape, Entities); // места после удалённой сдвинулись
            ResetPathCaches(); // 1-007: слоты сдвинулись — владельцы запросов путей протухли
            return true;
        }

        /// <summary>
        /// Загрузка сохранения (docs/07, 6.4: состояние на границе такта): номер такта и сущности со всеми
        /// полями. Только в пустую симуляцию до первого такта. Сущности — строго по возрастанию номера;
        /// следующий номер — больше всех загруженных (номера не переиспользуются). Ввод сущности — тот,
        /// по которому она шла в такте сохранения; ждущих кадров нет.
        /// </summary>
        public void Restore(long tick, ReadOnlySpan<SimEntity> entities, int nextId)
        {
            Restore(tick, entities, nextId, 0);
        }

        /// <summary>
        /// Загрузка с временем мира (1-014): то же + <see cref="WorldTime"/> (формат 6, поле МИР 10;
        /// у сохранений до 1-014 — 0).
        /// </summary>
        public void Restore(long tick, ReadOnlySpan<SimEntity> entities, int nextId, int worldTime)
        {
            if (Tick != 0 || _entityCount != 0 || _editCount != 0)
            {
                throw new InvalidOperationException("Загрузка — только в пустую симуляцию до первого такта.");
            }

            if (tick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tick), tick, "Такт не отрицательный.");
            }

            if (entities.Length > _entities.Length)
            {
                throw new ArgumentException($"Сущностей {entities.Length}, ёмкость симуляции {_entities.Length}.", nameof(entities));
            }

            int maxId = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i].Id <= maxId)
                {
                    throw new ArgumentException($"Сущность № {i}: номер {entities[i].Id} не больше предыдущего {maxId}.", nameof(entities));
                }

                maxId = entities[i].Id;
            }

            for (int i = 0; i < entities.Length; i++)
            {
                _entities[i] = RestoreDefaults(entities[i]);
                _hasPending[i] = false;
                _frameCount[i] = -1;
            }

            _entityCount = entities.Length;
            _grid.Rebuild(_shape, Entities);
            ResetPathCaches(); // 1-007: кэш путей — производный, в сейве его нет; после загрузки всё переспрашивается
            _nextId = Math.Max(nextId, maxId + 1);
            Tick = tick;
            SetWorldTime(worldTime);
        }

        /// <summary>Следующий номер сущности (пишется в сохранение: номера не переиспользуются).</summary>
        public int NextEntityId => _nextId;

        /// <summary>
        /// Здоровье и боевые таймеры едут в сейве с формата 6 (1-007). Форматы 1–5 их не пишут (0 при чтении) —
        /// сущность получает дефолты спавна: вернувшийся персонаж входит с полным здоровьем («Не трогать» 0-037,
        /// `08` 3.1). Признак старого формата — MaxHealth = 0: в формате 6 он всегда положителен.
        /// </summary>
        private static SimEntity RestoreDefaults(SimEntity e)
        {
            if (e.MaxHealth <= 0)
            {
                e.MaxHealth = (short)(e.Kind == SimEntity.KindEnemy ? MaxHealthMeleeEnemy : MaxHealthCharacter);
                e.Health = e.MaxHealth;
                e.Mass = 1;
                e.RegenTicks = RegenDelayTicks;
                e.AttackTarget = -1;
                e.AttackPeriod = -1;
            }

            if (e.Brain.Archetype != EnemyArchetype.None)
            {
                // ИИ (1-007): −1 в полях «нет цели» — строчка по умолчанию (format 5 без этих полей) это 0.
                if (e.Brain.TargetId == 0)
                {
                    e.Brain.TargetId = EnemyBrain.NoTarget;
                }

                if (e.Brain.LastHitBy == 0)
                {
                    e.Brain.LastHitBy = EnemyBrain.NoTarget;
                }

                if (e.Brain.State == EnemyState.None)
                {
                    e.Brain.State = EnemyState.Idle;
                }

                if (e.Brain.HomeX == 0 && e.Brain.HomeY == 0)
                {
                    e.Brain.HomeX = e.Brain.GoalX;
                    e.Brain.HomeY = e.Brain.GoalY;
                }
            }

            return e;
        }

        /// <summary>Место сущности в списке по номеру; нет — −1.</summary>
        public int SlotOf(int entityId)
        {
            int lo = 0;
            int hi = _entityCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int id = _entities[mid].Id;
                if (id == entityId)
                {
                    return mid;
                }

                if (id < entityId)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return -1;
        }

        /// <summary>Сущность по номеру (копия).</summary>
        public SimEntity GetEntity(int entityId)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entityId), entityId, "Нет такой сущности.");
            }

            return _entities[slot];
        }

        // ---------- Ввод ----------

        /// <summary>
        /// Кадр ввода сущности на ближайший такт. Совпадает с текущим вводом — ничего не меняется
        /// (в журнал идут только изменения ввода). Нет такой сущности — false.
        /// </summary>
        public bool SetInput(int entityId, InputFrame frame)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return false;
            }

            SetInputAt(slot, frame);
            return true;
        }

        /// <summary>Кадр ввода по месту в списке (<see cref="SlotOf"/>).</summary>
        public void SetInputAt(int slot, InputFrame frame)
        {
            if ((uint)slot >= (uint)_entityCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            if (frame == _entities[slot].Input)
            {
                _hasPending[slot] = false;
                return;
            }

            _pending[slot] = frame;
            _hasPending[slot] = true;
        }

        /// <summary>Наибольшее число кадров ввода на такт у сетевого персонажа (<see cref="SetFramesAt"/>).</summary>
        public const int MaxFramesPerTick = 8;

        /// <summary>
        /// Кадры ввода 60 Гц на ближайший такт (docs/07, 2.7 «Применение ввода»; 0-019): в такте сущность шагает каждым
        /// кадром по порядку, полшага на кадр (<see cref="Mover.StepFrame"/>), — так же, как предсказание клиента.
        /// Пусто — в этом такте сущность стоит (время персонажа ждёт кадров). Текущий ввод после такта — последний кадр.
        /// Действует один такт; не вызвали — сущность шагает тактом по своему вводу (<see cref="SetInputAt"/>).
        /// В журнал ввода кадры не пишутся (backlog [журнал]): повтор журнала — для тактового ввода.
        /// </summary>
        public void SetFramesAt(int slot, ReadOnlySpan<InputFrame> frames)
        {
            if ((uint)slot >= (uint)_entityCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            if (frames.Length > MaxFramesPerTick)
            {
                throw new ArgumentOutOfRangeException(nameof(frames), frames.Length, $"Кадров на такт не больше {MaxFramesPerTick}.");
            }

            frames.CopyTo(new Span<InputFrame>(_frames, slot * MaxFramesPerTick, MaxFramesPerTick));
            _frameCount[slot] = frames.Length;
        }

        /// <summary>Ячейка кадра ввода становится выбранной (предмет в руках); уже выбрана — ничего.</summary>
        private static void ApplySlot(ref SimEntity e, byte slot)
        {
            if (slot != e.Inv.Selected && slot < Inventory.Slots)
            {
                e.Inv.Selected = slot;
            }
        }

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

        /// <summary>
        /// Изменения ввода, ждущие ближайшего такта, по номеру сущности — то, что пишется в журнал ввода.
        /// Возвращает число записанных в <paramref name="dest"/>.
        /// </summary>
        public int CopyPendingInputs(Span<EntityInput> dest)
        {
            int n = 0;
            for (int i = 0; i < _entityCount; i++)
            {
                if (_hasPending[i])
                {
                    dest[n++] = new EntityInput(_entities[i].Id, _pending[i]);
                }
            }

            return n;
        }

        // ---------- Правки ----------

        /// <summary>
        /// Запрос правки тайла на ближайший такт: тайл становится ровно таким (пол, объект, без записи постройки).
        /// Применится в фазе 3 пачкой своего чанка. Очередь полна — false.
        /// </summary>
        public bool RequestEdit(TileCoord tile, byte floor, ushort obj) =>
            Enqueue(tile, floor, obj, false, default, 0, 0, false);

        /// <summary>Запрос правки тайла с записью постройки (см. <see cref="RequestEdit(TileCoord, byte, ushort)"/>).</summary>
        public bool RequestEdit(TileCoord tile, byte floor, ushort obj, BuildRecord record) =>
            Enqueue(tile, floor, obj, true, record, 0, 0, false);

        /// <summary>
        /// Правка игрока (docs/07, 3.7; 0-028): как <see cref="RequestEdit(TileCoord, byte, ushort)"/>, плюс метки для ответа
        /// (<paramref name="requester"/>, <paramref name="tag"/> — в событии такта) и проверка существ <b>в фазе 3, после
        /// движения</b> (`05` 1.3): непроходимое — не на тайл, который задевает сущность (в том числе своя), непроходимое и
        /// ловушка — не ближе 2 тайлов к врагу (<see cref="SimEntity.KindEnemy"/>). Иначе персонаж, шагнувший на тайл в том же
        /// такте, оказался бы в стене и вышел бы сквозь стену в 1 тайл (backlog [стройка], П по 0-004).
        /// </summary>
        public bool RequestPlayerEdit(TileCoord tile, byte floor, ushort obj, bool hasRecord, BuildRecord record, int requester, int tag) =>
            // Дверь с записью (1-012) — правка состояния (F/автозакрытие/автооткрытие): существо на тайле или
            // враг рядом не повод отказывать, выталкиванием занимается BeginDoorClose; иначе F у врага ломался бы.
            Enqueue(tile, floor, obj, hasRecord, record, requester, tag,
                EditRules.NeedsClearance(obj) && !(obj == ObjectId.Door && hasRecord));

        private bool Enqueue(TileCoord tile, byte floor, ushort obj, bool hasRecord, BuildRecord record, int requester, int tag, bool checkCreatures)
        {
            if (_editCount == _edits.Length)
            {
                return false;
            }

            if (!_shape.Wraps && ((uint)tile.X >= (uint)_shape.Width || (uint)tile.Y >= (uint)_shape.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(tile), tile, $"Тайл вне карты {_shape}.");
            }

            TileCoord t = Torus.Wrap(_shape, tile);
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            _edits[_editCount] = new EditRequest(
                Torus.ChunkOf(_shape, t),
                _editCount,
                hasRecord ? new TileEdit(index, floor, obj, record) : new TileEdit(index, floor, obj),
                requester, tag, checkCreatures);
            _editCount++;
            return true;
        }

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

        private void ApplyEditBatches(long tick)
        {
            int n = _editCount;
            if (n == 0)
            {
                return;
            }

            // Pre-pass по порядку прихода (до сортировки по чанкам, 1-011): состояние тайла до пачки, списание
            // стоимости постановки (сумка: атомарно на тайл; не хватает — «нет ресурсов», правка в пачку не идёт),
            // снос постройки, получавшей урон последние 10 с, — правка подменяется обломочной (`05` 1.3).
            for (int i = 0; i < n; i++)
            {
                PreEdit(tick, i);
            }

            SortByChunkThenArrival(n);
            int start = 0;
            while (start < n)
            {
                ChunkCoord chunk = _edits[start].Chunk;
                int end = start + 1;
                while (end < n && _edits[end].Chunk == chunk)
                {
                    end++;
                }

                // Проверка существ (0-028) — до пачки, по позициям после движения: отказ в пачку не идёт.
                // Больше правок на чанк, чем тайлов: хвост — «занято» без вызова мира
                // (на тайл в пачке проходит только одна правка, 3.7).
                int take = 0;
                for (int i = start; i < end; i++)
                {
                    EditResult pre = _preResult[i] != EditResult.Applied
                        ? _preResult[i]
                        : _edits[i].CheckCreatures ? CheckCreatures(chunk, _edits[i].Edit) : EditResult.Applied;
                    if (pre == EditResult.Applied && take < _batch.Length)
                    {
                        _batchOf[i] = take;
                        _batch[take++] = _edits[i].Edit;
                    }
                    else
                    {
                        _batchOf[i] = -1;
                        _preResult[i] = pre == EditResult.Applied ? EditResult.Busy : pre;
                    }
                }

                uint before = _world.FindChunk(chunk)?.Revision ?? 0;
                uint revision = take > 0 ? _world.ApplyEdits(chunk, new ReadOnlySpan<TileEdit>(_batch, 0, take), _results) : before;
                for (int i = start; i < end; i++)
                {
                    EditResult result = _batchOf[i] >= 0 ? _results[_batchOf[i]] : _preResult[i];
                    AddEvent(new SimEvent(SimEventKind.TileEdit, tick, chunk, _edits[i].Edit.Index, result, revision,
                        _edits[i].Requester, _edits[i].Tag));

                    // Дверь (1-012): применённая правка с объектом «дверь» — в индекс дверей (dedup); автологика
                    // видит её с этого такта. Снятие — ленивое в StepDoors (объект/запись пропали).
                    if (result == EditResult.Applied && _edits[i].Edit.Obj == ObjectId.Door)
                    {
                        AddDoor(TileOf(chunk, _edits[i].Edit.Index));
                    }

                    int req = _edits[i].Requester;
                    if (req <= 0)
                    {
                        continue;
                    }

                    // Возврат оплаченного (1-011): «занято существом»/«враг»/«занято» после пачки — сумка целая
                    // (`05` 1.4: «его ресурсы не списаны»).
                    if (_costPaid[i] != 0 && result != EditResult.Applied)
                    {
                        RefundCost(_edits[i].Edit, req);
                    }

                    // Добыча (1-010): правка «снять природный объект» применена — материал в сумку просящего в том же такте.
                    // Двое копающих один тайл: вторая правка — Busy, добыча ровно одному.
                    if (result == EditResult.Applied && _edits[i].Edit.Obj == ObjectId.Empty)
                    {
                        GiveDigLoot(chunk, _edits[i].Edit.Index, _preObj[i], req);

                        // Снос постройки (1-011, `05` 1.3): 100 % рецепта; повреждённой — ⌊кол × прочность ÷ максимум⌋;
                        // обломки ранней расчистки молотом — ничего.
                        if (_preHasBuild[i] && !_preBuild[i].IsDebris)
                        {
                            GiveBuildRefund(req, _preObj[i], _preFloor[i], _preBuild[i]);
                        }
                    }
                }

                if (revision != before)
                {
                    _editRev++;
                    AddEvent(new SimEvent(SimEventKind.ChunkRevised, tick, chunk, -1, EditResult.Applied, revision));
                }

                start = end;
            }

            _editCount = 0;
        }

        /// <summary>
        /// Pre-pass одной правки (1-011): снимает состояние тайла до пачки (<see cref="_preObj"/> и компанию),
        /// списывает стоимость постановки (<see cref="EditResult.NoResources"/> — в пачку не идёт) и подменяет
        /// снос «получавшей урон последние 10 с» постройки на обломочную правку (<c>05</c> 1.3).
        /// </summary>
        private void PreEdit(long tick, int i)
        {
            ref EditRequest e = ref _edits[i];
            Chunk? chunkData = _world.FindChunk(e.Chunk);
            ushort index = e.Edit.Index;
            _preObj[i] = chunkData?.GetObject(index) ?? 0;
            _preFloor[i] = chunkData?.GetFloor(index) ?? 0;
            _preHasBuild[i] = chunkData is not null && chunkData.TryGetBuild(index, out _preBuild[i]);
            _preResult[i] = EditResult.Applied;
            _costPaid[i] = 0;

            if (e.Requester <= 0)
            {
                return; // правки сервера (урон, истечения) — без стоимости и возврата
            }

            // Снос постройки, получавшей урон последние 10 с → обломки на 10 с: правка подменяется здесь,
            // второй ApplyEdits в такт невозможен (ревизия +2 сломала бы патч-цепочку клиента).
            if (_preHasBuild[i] && _preBuild[i].IsDamagedRecent && e.Edit.Obj == ObjectId.Empty)
            {
                BuildRecord rec = _preBuild[i];
                var debris = new BuildRecord(rec.Owner, 0, (byte)BuildFlags.Debris, rec.NaturalFloor, (ushort)(tick + BuildDefs.DebrisTicks));
                e = new EditRequest(e.Chunk, e.Arrival, new TileEdit(index, _preFloor[i], ObjectId.Empty, debris),
                    e.Requester, e.Tag, e.CheckCreatures);
                _preHasBuild[i] = false; // снос в обломки — без возврата (`05` 1.3); запись «получав урон» больше не считается сносимой
                AddExpire(tick + BuildDefs.DebrisTicks, e.Chunk, index, debris: true);
                return;
            }

            // Смена состояния двери (1-012): запись на уже существующей двери (F-закрытие) — это не постановка,
            // стоимость не списывается и не возвращается (постановка двери молотом идёт с пустого тайла).
            if (e.Edit.Obj == ObjectId.Door && e.Edit.HasRecord && _preObj[i] == ObjectId.Door)
            {
                return;
            }

            if (!CostOf(e.Edit, out BuildDef def))
            {
                return; // снос, копание, природный пол — стоимость не списывается
            }

            int slot = SlotOf(e.Requester);
            if (slot < 0 || !_entities[slot].Inv.Spend(def.Material1, def.Count1, def.Material2, def.Count2))
            {
                _preResult[i] = EditResult.NoResources; // «нет ресурсов» (`05` 1.4): линия продолжается следующими тайлами
                return;
            }

            _costPaid[i] = 1;
        }

        /// <summary>
        /// Стоимость правки-постановки (1-011): ключ — id объекта; для пола — id <b>построенного</b> пола в слое пола
        /// (природные полы 1…7 с id построек не путаются: снос и копание стоимость не списывают).
        /// </summary>
        private static bool CostOf(in TileEdit edit, out BuildDef def)
        {
            if (edit.Obj != ObjectId.Empty)
            {
                return BuildDefs.TryGet(edit.Obj, out def);
            }

            def = default;
            return FloorId.IsBuilt(edit.Floor) && BuildDefs.TryGet(edit.Floor, out def);
        }

        /// <summary>Вернуть стоимость постановки при отказе после списания (1-011, `05` 1.4).</summary>
        private void RefundCost(in TileEdit edit, int requester)
        {
            int slot = SlotOf(requester);
            if (slot < 0 || !CostOf(edit, out BuildDef def))
            {
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv; // SimEntity — структура: правим сумку в массиве, не копию
            inv.Add(def.Material1, def.Count1);
            if (def.Count2 > 0)
            {
                inv.Add(def.Material2, def.Count2);
            }
        }

        /// <summary>
        /// Возврат при сносе (1-011, `05` 1.3): неповреждённой постройки — рецепт целиком, повреждённой — по каждому
        /// материалу ⌊количество × прочность ÷ максимум⌋; сносителю. Не влезло в сумку — пропадает (россыпь — веха 2).
        /// </summary>
        private void GiveBuildRefund(int requester, ushort preObj, byte preFloor, in BuildRecord rec)
        {
            int slot = SlotOf(requester);
            ushort key = preObj != ObjectId.Empty ? preObj : preFloor; // постройка — id объекта; снос пола — id построенного пола
            if (slot < 0 || (preObj == ObjectId.Empty && !FloorId.IsBuilt(preFloor)) || !BuildDefs.TryGet(key, out BuildDef def))
            {
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv; // сумка в массиве сущностей, не копия (SimEntity — структура)
            int k1 = BuildDefs.RefundOf(def.Count1, rec.Integrity, def.MaxIntegrity);
            if (k1 > 0)
            {
                inv.Add(def.Material1, k1);
            }

            if (def.Count2 > 0)
            {
                int k2 = BuildDefs.RefundOf(def.Count2, rec.Integrity, def.MaxIntegrity);
                if (k2 > 0)
                {
                    inv.Add(def.Material2, k2);
                }
            }
        }

        /// <summary>
        /// Урон постройки (1-011, <c>05</c> 1.3 — осадные враги, взрывы; оружие игроков — нет): прочность −<paramref name="raw"/>;
        /// флаг «урон последние 10 с» на 10 с; 0 прочности → обломки на 10 с без дропа. Полы «не бьют» (<c>05</c> 1.3).
        /// Правка — через очередь такта (одна пачка на чанк, 3.7). Применится в ближайшем такте — true.
        /// </summary>
        public bool DamageBuilding(TileCoord tile, int raw)
        {
            TileCoord t = Torus.Wrap(_shape, tile);
            ChunkCoord chunk = Torus.ChunkOf(_shape, t);
            Chunk? chunkData = _world.FindChunk(chunk);
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            if (raw <= 0 || chunkData is null || !chunkData.TryGetBuild(index, out BuildRecord rec) || rec.IsDebris)
            {
                return false;
            }

            ushort obj = _world.GetObject(t);
            if (obj == ObjectId.Empty)
            {
                return false; // построенный пол не бьют (`05` 1.3)
            }

            int left = rec.Integrity - raw;
            if (left <= 0)
            {
                var debris = new BuildRecord(rec.Owner, 0, (byte)BuildFlags.Debris, rec.NaturalFloor, (ushort)(Tick + BuildDefs.DebrisTicks));
                if (!Enqueue(t, rec.NaturalFloor, ObjectId.Empty, true, debris, 0, 0, false))
                {
                    return false;
                }

                AddExpire(Tick + BuildDefs.DebrisTicks, chunk, index, debris: true);
                return true;
            }

            BuildRecord damaged = rec.WithIntegrity((ushort)left).WithFlags(BuildFlags.DamagedRecent);
            if (!Enqueue(t, _world.GetFloor(t), obj, true, damaged, 0, 0, false))
            {
                return false;
            }

            AddExpire(Tick + BuildDefs.DamageFlagTicks, chunk, index, debris: false);
            return true;
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

        private void AddExpire(long endTick, ChunkCoord chunk, ushort index, bool debris)
        {
            if (_expireCount == _expire.Length)
            {
                // Переполнение (патология): снимаем запись, которой истечь раньше всех, — её эффект слабее всего
                // (флаг урона переживёт 10 с; обломки без записи останутся навсегда — их бережём).
                int oldest = 0;
                for (int i = 1; i < _expire.Length; i++)
                {
                    if (_expire[i].EndTick < _expire[oldest].EndTick && !_expire[i].Debris)
                    {
                        oldest = i;
                    }
                }

                if (_expire[oldest].Debris)
                {
                    return;
                }

                _expire[oldest] = _expire[--_expireCount];
            }

            _expire[_expireCount++] = new ExpireEntry { EndTick = endTick, Chunk = chunk, Index = index, Debris = debris };
        }

        /// <summary>Истечения такта (1-011): правки снятия — в очередь этого же такта; очередь полна — проба в следующем.</summary>
        private void StepExpiry(long tick)
        {
            for (int i = 0; i < _expireCount; i++)
            {
                ExpireEntry e = _expire[i];
                if (e.EndTick > tick)
                {
                    continue;
                }

                TileCoord tile = TileOf(e.Chunk, e.Index);
                Chunk? chunkData = _world.FindChunk(e.Chunk);
                bool applied = true;
                if (chunkData is not null && chunkData.TryGetBuild(e.Index, out BuildRecord rec))
                {
                    if (e.Debris && rec.IsDebris)
                    {
                        // Обломки кончились: тайл остаётся как есть (пол природный), запись снимается правкой без записи.
                        applied = RequestEdit(tile, _world.GetFloor(tile), ObjectId.Empty);
                    }
                    else if (!e.Debris && rec.IsDamagedRecent)
                    {
                        applied = RequestEdit(tile, _world.GetFloor(tile), _world.GetObject(tile), rec.WithoutFlags(BuildFlags.DamagedRecent));
                    }
                }

                if (applied)
                {
                    _expire[i--] = _expire[--_expireCount]; // снято; хвост на место
                }
            }
        }

        // ---------- Двери (1-012) ----------

        /// <summary>Число дверей в индексе (тесты: мир просканирован).</summary>
        public int DoorCount => _doorCount;

        /// <summary>Индекс дверей по миру: каждый чанк, его записи построек, объект «дверь» (конструктор).</summary>
        private void IndexDoors()
        {
            ushort[] idx = new ushort[Torus.ChunkSize * Torus.ChunkSize];
            foreach (ChunkCoord c in _world.GetChunkCoords())
            {
                Chunk? chunk = _world.FindChunk(c);
                if (chunk is null)
                {
                    continue;
                }

                int n = chunk.GetBuildIndices(idx);
                for (int k = 0; k < n; k++)
                {
                    if (chunk.GetObject(idx[k]) == ObjectId.Door)
                    {
                        AddDoor(TileOf(c, idx[k]));
                    }
                }
            }
        }

        private void AddDoor(TileCoord tile)
        {
            for (int i = 0; i < _doorCount; i++)
            {
                if (_doorX[i] == tile.X && _doorY[i] == tile.Y)
                {
                    return; // дедуп: правка двери в каждом такте не размножает список
                }
            }

            if (_doorCount == _doorX.Length)
            {
                Array.Resize(ref _doorX, _doorX.Length + DoorCapacityStep);
                Array.Resize(ref _doorY, _doorY.Length + DoorCapacityStep);
                Array.Resize(ref _doorLockUntil, _doorLockUntil.Length + DoorCapacityStep);
            }

            // Порядок индекса канонический — по (Y, X): регистрация при применении правок и сканирование
            // мира при загрузке дают один и тот же индекс, детерминизм будущим состояний не расходится.
            int at = _doorCount;
            while (at > 0 && (_doorY[at - 1] > tile.Y || (_doorY[at - 1] == tile.Y && _doorX[at - 1] > tile.X)))
            {
                at--;
            }

            for (int i = _doorCount; i > at; i--)
            {
                _doorX[i] = _doorX[i - 1];
                _doorY[i] = _doorY[i - 1];
                _doorLockUntil[i] = _doorLockUntil[i - 1];
            }

            _doorX[at] = tile.X;
            _doorY[at] = tile.Y;
            _doorLockUntil[at] = 0;
            _doorCount++;
        }

        private void RemoveDoor(int i)
        {
            // Сдвиг, не swap: канонический порядок (Y, X) сохраняется (см. AddDoor).
            for (; i + 1 < _doorCount; i++)
            {
                _doorX[i] = _doorX[i + 1];
                _doorY[i] = _doorY[i + 1];
                _doorLockUntil[i] = _doorLockUntil[i + 1];
            }

            _doorCount--;
        }

        /// <summary>
        /// F (1-012): закрыть ближайшую дверь в радиусе 1 тайл (chebyshev, тор); тайл под нажавшим — отказ
        /// («в проёме F — отказ»). Ближайшая — первая в порядке индекса при равной дистанции (детерминизм).
        /// </summary>
        private void CloseDoorNearest(int slot, long tick)
        {
            TileCoord own = Torus.TileOf(_shape, _entities[slot].Mover.Position);
            int best = -1;
            int bestCheb = int.MaxValue;
            for (int d = 0; d < _doorCount; d++)
            {
                Int2 td = Torus.Delta(_shape, own, new TileCoord(_doorX[d], _doorY[d]));
                int cheb = Math.Max(Math.Abs(td.X), Math.Abs(td.Y));
                if (cheb == 0 || cheb > 1)
                {
                    continue; // свой тайл — отказ; дальше радиуса — не сюда
                }

                if (cheb < bestCheb)
                {
                    bestCheb = cheb;
                    best = d;
                    if (cheb == 1)
                    {
                        break; // дистанций 0 уже нет, 1 — минимум
                    }
                }
            }

            if (best >= 0)
            {
                BeginDoorClose(best, tick, fromF: true);
            }
        }

        /// <summary>
        /// F по запросу сервера (1-012, протокол 19): закрыть дверь на <paramref name="tile"/>. Радиус —
        /// chebyshev ≤ 1 от тайла нажавшего, тайл под нажавшим — отказ; замок и выталкивание врага — внутри
        /// <see cref="BeginDoorClose"/>. Принято — true; исход доходит до запросчика событием такта
        /// (<paramref name="requester"/> = <paramref name="entityId"/>, <paramref name="tag"/>).
        /// </summary>
        public bool RequestCloseDoor(int entityId, TileCoord tile, int tag)
        {
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].Alive)
            {
                return false;
            }

            Int2 d = Torus.Delta(_shape, Torus.TileOf(_shape, _entities[slot].Mover.Position), tile);
            int cheb = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            if (cheb == 0 || cheb > 1)
            {
                return false; // не в радиусе или тайл под нажавшим («в проёме F — отказ»)
            }

            int di = IndexOfDoor(tile);
            return di >= 0 && BeginDoorClose(di, Tick, fromF: true, requester: entityId, tag: tag);
        }

        /// <summary>Индекс двери в списке (канонический порядок (Y, X)); нет — −1.</summary>
        private int IndexOfDoor(TileCoord tile)
        {
            for (int i = 0; i < _doorCount; i++)
            {
                if (_doorX[i] == tile.X && _doorY[i] == tile.Y)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Закрытие двери (1-012): правка записи без битов открытости и без таймера в очередь такта.
        /// Персонаж на тайле — отказ; враг — выталкивание (E,S,W,N), цели нет — отказ (закрытие отложено,
        /// автозакрытия его догонят). <paramref name="fromF"/> — закрытие кнопкой F: замок
        /// <param name="fromF">закрытие кнопкой F: замок <see cref="DoorLockTicks"/> тактов (автооткрытие молчит);
        /// автозакрытие замок не ставит — замок только рантайм-состояние, в сейве его нет,
        /// и на детерминизм загрузки он не влияет.</param>
        /// <param name="requester">запросивший игрок (F по сети, 0-028) — для ответа событием такта; 0 — процесс мира.</param>
        /// <param name="tag">метка запроса (номер запроса клиента) — в событие такта.</param>
        /// </summary>
        private bool BeginDoorClose(int di, long tick, bool fromF, int requester = 0, int tag = 0)
        {
            if (fromF && tick < _doorLockUntil[di])
            {
                return false;
            }

            TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
            {
                RemoveDoor(di);
                return false;
            }

            if (!chunk.TryGetBuild(index, out BuildRecord rec))
            {
                rec = default;
            }

            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive || !EditRules.Overlaps(_shape, _entities[i].Mover.Position, tile))
                {
                    continue;
                }

                if (_entities[i].Kind != SimEntity.KindEnemy)
                {
                    return false; // персонаж в проёме — не выталкиваем, закрытие откладывается
                }

                if (!PushEnemyOffTile(i, tile))
                {
                    return false; // выталкивать некуда — закрытие откладывается
                }

                // Телепорт после фазы сетки: сетку обновляем за тем же существом сразу.
                _grid.Update(i, Torus.ChunkOf(_shape, _entities[i].Mover.Position));
            }

            if (!RequestPlayerEdit(tile, chunk.GetFloor(index), ObjectId.Door, true,
                    rec.WithoutFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen).WithDebrisEndTick(0), requester, tag))
            {
                return false; // очередь правок полна — проба в следующем такте
            }

            if (fromF)
            {
                _doorLockUntil[di] = tick + 1 + DoorLockTicks;
            }

            return true;
        }

        /// <summary>Открыть дверь (1-012): правка записи с битом DoorOpen в очередь такта (автооткрытие).</summary>
        private void BeginDoorOpen(int di, in BuildRecord rec)
        {
            TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
            {
                RemoveDoor(di);
                return;
            }

            RequestEdit(tile, chunk.GetFloor(index), ObjectId.Door, rec.WithFlags(BuildFlags.DoorOpen).WithDebrisEndTick(0));
        }

        /// <summary>
        /// Перезавести дедлайн автозакрытия двери (1-012) в байте записи: 0 — таймер снят, иначе такт
        /// (mod 65 536) закрытия. Правка состояния без смены проходимости.
        /// </summary>
        private void TouchDoorDeadline(int di, ushort endTick)
        {
            TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
            {
                RemoveDoor(di);
                return;
            }

            if (!chunk.TryGetBuild(index, out BuildRecord rec))
            {
                rec = default;
            }

            RequestEdit(tile, chunk.GetFloor(index), ObjectId.Door, rec.WithDebrisEndTick(endTick));
        }

        /// <summary>
        /// Вытолкнуть существо с тайла двери (1-012): соседние тайлы в порядке E,S,W,N; цель — непроходимый
        /// и без других существ. Телепорт в центр тайла, остаток скорости и уворот гасятся (шаг через дверь
        /// не «дотягивается» до закрытого проёма).
        /// </summary>
        private bool PushEnemyOffTile(int i, TileCoord door)
        {
            for (int k = 0; k < PushDx.Length; k++)
            {
                TileCoord t = Torus.Wrap(_shape, new TileCoord(door.X + PushDx[k], door.Y + PushDy[k]));
                if (IsBlocked(_shape, t))
                {
                    continue;
                }

                bool occupied = false;
                for (int j = 0; j < _entityCount; j++)
                {
                    if (j != i && _entities[j].Alive && EditRules.Overlaps(_shape, _entities[j].Mover.Position, t))
                    {
                        occupied = true;
                        break;
                    }
                }

                if (occupied)
                {
                    continue;
                }

                ref Mover m = ref _entities[i].Mover;
                m.Position = Torus.Wrap(_shape, new WorldPos(
                    (t.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                    (t.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2));
                m.Carry = default;
                m.Dodge = default;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Двери такта (фаза 4.5, после движения и сетки): открытая без «держать» закрывается через
        /// <see cref="DoorAutoCloseTicks"/> тактов, когда тайл и соседи свободны; закрытая открывается,
        /// когда персонаж на тайле (страховка) или шагнул к ней (скаляр смещения такта к центру двери &gt; 0),
        /// в пределах <see cref="DoorLockTicks"/> после F — молчит. Враг дверь не открывает (`05` 1.3).
        /// </summary>
        private void StepDoors(long tick)
        {
            for (int di = 0; di < _doorCount; di++)
            {
                TileCoord tile = new TileCoord(_doorX[di], _doorY[di]);
                Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
                int index = Chunk.IndexOf(tile.X, tile.Y);
                if (chunk is null || chunk.GetObject(index) != ObjectId.Door)
                {
                    RemoveDoor(di--); // снесена/разрушена — ленивое снятие с индекса
                    continue;
                }

                if (!chunk.TryGetBuild(index, out BuildRecord rec))
                {
                    rec = default;
                }

                if (rec.IsDoorOpen)
                {
                    if ((rec.Flags & (byte)BuildFlags.DoorKeepOpen) != 0)
                    {
                        continue; // «держать открытой» — автозакрытие не трогает
                    }

                    // Автозакрытие (1-012): дедлайн в байте записи (DebrisEndTick, такт mod 65 536, 0 — не заведён):
                    // таймер живёт в мире, сейв v6 не растёт, после загрузки будущее не расходится.
                    ushort tickMod = unchecked((ushort)tick);
                    if (TileAndNeighborsFree(tile))
                    {
                        if (rec.DebrisEndTick == 0)
                        {
                            TouchDoorDeadline(di, unchecked((ushort)(tickMod + DoorAutoCloseTicks))); // завести таймер
                        }
                        else if (unchecked((short)(tickMod - rec.DebrisEndTick)) >= 0)
                        {
                            BeginDoorClose(di, tick, fromF: false); // дедлайн наступил — закрываем
                        }
                    }
                    else if (rec.DebrisEndTick != 0)
                    {
                        TouchDoorDeadline(di, 0); // тайл занят — таймер снимается
                    }

                    continue;
                }

                if (tick < _doorLockUntil[di])
                {
                    continue; // замок F
                }

                for (int i = 0; i < _entityCount; i++)
                {
                    if (!_entities[i].Alive || _entities[i].Kind == SimEntity.KindEnemy)
                    {
                        continue; // враг дверь не открывает
                    }

                    WorldPos p = _entities[i].Mover.Position;
                    if (EditRules.Overlaps(_shape, p, tile))
                    {
                        BeginDoorOpen(di, rec); // страховка: персонаж на тайле двери
                        break;
                    }

                    Int2 td = Torus.Delta(_shape, Torus.TileOf(_shape, p), tile);
                    if (Math.Max(Math.Abs(td.X), Math.Abs(td.Y)) != 1)
                    {
                        continue; // не соседний тайл
                    }

                    Int2 toDoor = Torus.Delta(_shape, p, new WorldPos(
                        (tile.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                        (tile.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2));

                    // «Шагнул к двери» — скаляр смещения такта; «упёрся и жмёт к двери» — скаляр направления
                    // ввода (у стены закрытой двери смещение нуля, без этого стоящий у двери не открывал бы).
                    Int2 moved = Torus.Delta(_shape, _posBefore[i], p);
                    Int2 want = DesiredDirection(i);
                    if (moved.X * toDoor.X + moved.Y * toDoor.Y > 0
                        || want.X * toDoor.X + want.Y * toDoor.Y > 0)
                    {
                        BeginDoorOpen(di, rec);
                        break;
                    }
                }
            }
        }

        /// <summary>Направление ввода такта: у тактового существа — ввод такта, у кадрового — последний кадр такта.</summary>
        private Int2 DesiredDirection(int slot)
        {
            int frames = _frameCount[slot];
            Input8 dir = frames < 0
                ? _entities[slot].Input.Direction
                : frames > 0 ? _frames[slot * MaxFramesPerTick + frames - 1].Direction : Input8.None;
            return new Int2(dir.DX, dir.DY);
        }

        /// <summary>Тайл двери и его соседи (chebyshev ≤ 1) свободны от живых существ — закрытие безопасно.</summary>
        private bool TileAndNeighborsFree(TileCoord tile)
        {
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue;
                }

                Int2 td = Torus.Delta(_shape, Torus.TileOf(_shape, _entities[i].Mover.Position), tile);
                if (Math.Max(Math.Abs(td.X), Math.Abs(td.Y)) <= 1)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Тайл по чанку и индексу тайла в чанке.</summary>
        private static TileCoord TileOf(ChunkCoord chunk, ushort index) => new TileCoord(
            (chunk.X << Torus.ChunkBits) + (index & (Torus.ChunkSize - 1)),
            (chunk.Y << Torus.ChunkBits) + (index >> Torus.ChunkBits));

        /// <summary>
        /// Существа у тайла правки (`05` 1.3; 0-028): непроходимое на тайл, который задевает сущность, — «занято существом»;
        /// непроходимое или ловушка ближе 2 тайлов к врагу — «враг ближе 2 тайлов». Сущностей ≤ ёмкости — перебор всех.
        /// </summary>
        private EditResult CheckCreatures(ChunkCoord chunk, in TileEdit edit)
        {
            TileCoord tile = TileOf(chunk, edit.Index);
            bool blocks = EditRules.BlocksCreature(edit.Obj);
            EditResult result = EditResult.Applied;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue; // труп без коллизии (8.3; 1-001): правке не мешает
                }

                WorldPos p = _entities[i].Mover.Position;
                if (blocks && EditRules.Overlaps(_shape, p, tile))
                {
                    return EditResult.Occupied;
                }

                if (_entities[i].Kind == SimEntity.KindEnemy && EditRules.EnemyTooClose(_shape, p, tile))
                {
                    result = EditResult.EnemyNear;
                }
            }

            return result;
        }

        /// <summary>
        /// Добыча снятого природного объекта (1-010, <c>01</c> 5): число — детерминированный бросок по зерну, тайлу и id
        /// (<see cref="DigLoot.DropFor"/>) — сервер и клиент (предсказание) считают одно и то же; материал — в сумку
        /// <paramref name="requester"/>. Постройка (не природный) — ничего: возврат по доле прочности за строителем (1-011).
        /// <paramref name="old"/> — что стояло на тайле ДО пачки (<see cref="_preObj"/>): после <c>ApplyEdits</c> тайл уже пуст.
        /// </summary>
        private void GiveDigLoot(ChunkCoord chunk, ushort index, ushort old, int requester)
        {
            int slot = SlotOf(requester);
            if (slot < 0 || !ObjectId.IsNatural(old))
            {
                return;
            }

            int x = (chunk.X << Torus.ChunkBits) + (index & (Torus.ChunkSize - 1));
            int y = (chunk.Y << Torus.ChunkBits) + (index >> Torus.ChunkBits);
            int n = DigLoot.DropFor(old, x, y, _world.Seed, out Material material);
            if (n > 0)
            {
                _entities[slot].Inv.Add(material, n);
            }
        }

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

        /// <summary>
        /// Фаза боя такта (после движения): таймеры (неуязвимость, «в бою», регенерация), цикл атаки врага
        /// (замах фиксирован началом: сектор замирает — попадание по позиции цели в конце замаха), смерть и
        /// возрождение. Порядок — по номеру сущности; только целые, без выделений (повтор журнала, 11.5).
        /// </summary>
        private void StepCombat(long tick)
        {
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity e = ref _entities[i];

                if (e.DeadTicks > 0)
                {
                    e.DeadTicks--;
                    if (e.DeadTicks == 0 && e.IsCharacter)
                    {
                        Respawn(ref e, tick);
                    }

                    continue;
                }

                if (e.Health <= 0)
                {
                    continue; // труп врага в такт удаления
                }

                if (e.InvulnTicks > 0)
                {
                    e.InvulnTicks--;
                }

                if (e.CombatTicks > 0)
                {
                    e.CombatTicks--;
                }

                if (e.RegenTicks < RegenDelayTicks)
                {
                    e.RegenTicks++;
                }
                else if (e.Health < e.MaxHealth)
                {
                    e.RegenCarryX100 += e.MaxHealth;
                    if (e.RegenCarryX100 >= RegenCarryPerHealth)
                    {
                        int heal = e.RegenCarryX100 / RegenCarryPerHealth;
                        e.RegenCarryX100 -= heal * RegenCarryPerHealth;
                        e.Health = (short)Math.Min(e.MaxHealth, e.Health + heal);
                    }
                }

                if (e.Kind != SimEntity.KindEnemy)
                {
                    ResolveMeleeStrike(ref e); // удар мечом игрока решается по текущим позициям в фазе «удар» (1-004)
                    continue; // персонаж сам не замахивается на врагов кодом врага (атаки игрока — 1-004…1-006)
                }

                if (e.AttackImmune > 0)
                {
                    e.AttackImmune--;
                }

                if (e.AttackWindup > 0)
                {
                    if (e.Brain.Archetype != EnemyArchetype.None && e.AttackPeriod > 0)
                    {
                        e.AttackPeriod--; // ИИ (1-007): период считается и в замахе — замах-замах ровно PeriodTicks
                    }

                    e.AttackWindup--;
                    if (e.AttackWindup == 0)
                    {
                        var sector = new MeleeSector(e.AttackOrigin, Angle16.FromRaw(e.AttackFacingRaw),
                            EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
                        int slot = SlotOf(e.AttackTarget);
                        // Удар решается по позиции цели в конце замаха (docs/07, 4.6); уворот спасает i-кадрами (0-029, 2.2).
                        if (slot >= 0 && _entities[slot].Alive && !_entities[slot].Mover.Dodge.Invulnerable &&
                            sector.Overlaps(_shape, _entities[slot].Mover.Position, Collision.RadiusUnits))
                        {
                            // Урон ночного врага ×1,25 (8.6, 1-009): 10 → 13; днём ровно ×1.
                            ApplyStrike(_entities[slot].Id, e.Id, ScaledByNight(e.AttackRaw, NightMultX1024), e.Mover.Position, knockback: true);
                        }

                        e.AttackRecover = RecoverTicks;
                        e.AttackTarget = -1;
                    }

                    continue;
                }

                if (e.AttackRecover > 0)
                {
                    e.AttackRecover--;
                }

                if (e.Brain.Archetype != EnemyArchetype.None)
                {
                    // Враг с ИИ (1-007): цикл удара ведёт мозг — в состоянии Attack замах каждые PeriodTicks (1,1 с)
                    // от прошлого замаха; цель жива и в зоне — иначе цикл ждёт (мозг подведёт ближе).
                    if (e.AttackPeriod > 0)
                    {
                        e.AttackPeriod--;
                    }

                    if (e.Brain.State == EnemyState.Attack && e.AttackPeriod == 0 && e.AttackRecover == 0)
                    {
                        int slot = SlotOf(e.Brain.TargetId);
                        if (slot >= 0 && _entities[slot].Alive &&
                            Torus.Dist2(_shape, e.Mover.Position, _entities[slot].Mover.Position) <= MeleeRogue.StrikeRange2 &&
                            // Линия удара (1-013, 9.3): стена/скала/дерево/закрытая дверь между врагом и целью —
                            // не бьёт; забор прозрачен (бьёт через забор).
                            StrikeLineClear(e.Mover.Position, _entities[slot].Mover.Position,
                                Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position))))
                        {
                            BeginWindup(ref e, _entities[slot]);
                            e.AttackPeriod = MeleeRogue.PeriodTicks;
                        }
                    }

                    continue;
                }

                if (e.AttackPeriod > 0 && e.AttackRecover == 0)
                {
                    int target = NearestCharacterSlot(e.Mover.Position, EnemyMelee.ReachUnits + Collision.RadiusUnits);
                    if (target >= 0 && StrikeLineClear(e.Mover.Position, _entities[target].Mover.Position,
                        Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position))))
                    {
                        BeginWindup(ref e, _entities[target]);
                    }
                }
            }
        }

        // ---------- ИИ врагов (1-007: docs/02 8.1, 8.3–8.5, 9.3) ----------

        /// <summary>Скорость бродяги 1,5 тайла/с = 154 (X10) — «бродит, не спешит» (8.1).</summary>
        public const int RogueWanderSpeedX10 = 154;

        /// <summary>Скорость «домой» (Lost) 3 тайла/с = 307 (X10) — быстрее бродяги, медленнее преследования (8.3).</summary>
        public const int RogueHomeSpeedX10 = 307;

        /// <summary>Тактов на решение ИИ: 3 (10 Гц при 30 Гц такта, 9.3); фаза — по номеру сущности, чтобы не в один такт.</summary>
        public const int AiDecisionTicks = 3;

        /// <summary>
        /// Добавить «Ближнего» с мозгом (разбойник, архетип <see cref="EnemyArchetype.Melee"/>, карточка 1-007):
        /// дом — точка появления, старт в Idle; числа — <see cref="MeleeRogue"/>. Номер — следующий. Возвращает номер.
        /// </summary>
        public int AddMeleeRogue(WorldPos position, int healthMultX1024 = 1024)
        {
            int id = AddEnemy(position);
            int slot = SlotOf(id);
            ref SimEntity e = ref _entities[slot];
            short hp = (short)ScaledByNight(MeleeRogue.MaxHealth, healthMultX1024); // ночь ×1,25 → 44 (8.6, 1-009)
            e.MaxHealth = hp;
            e.Health = hp;
            e.AttackRaw = (byte)MeleeRogue.StrikeDamage; // удар ночью масштабируется в ApplyStrike (8.6)
            e.AttackPeriod = MeleeRogue.PeriodTicks;
            e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
            TileCoord home = Torus.TileOf(_shape, e.Mover.Position);
            e.Brain = new EnemyBrain
            {
                Archetype = EnemyArchetype.Melee,
                State = EnemyState.Idle,
                TargetId = EnemyBrain.NoTarget,
                HomeX = home.X,
                HomeY = home.Y,
                GoalX = home.X,
                GoalY = home.Y,
                LastHitBy = EnemyBrain.NoTarget,
                IgnoreTargetId = EnemyBrain.NoTarget,
                CauseId = EnemyBrain.NoTarget,
            };
            return id;
        }

        /// <summary>
        /// Фаза ИИ такта (после ввода, до движения): цели (зрение/агро), состояние, направление. Движение —
        /// общая фаза по <see cref="SimEntity.Input"/> (враг идёт как персонаж по своему «вводу»).
        /// Порядок — по номеру сущности; решения каждые <see cref="AiDecisionTicks"/> тактов с фазой по номеру (9.3).
        /// </summary>
        private void StepAI(long tick)
        {
            ulong seed = _world.Seed;
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity e = ref _entities[i];
                if (e.Kind != SimEntity.KindEnemy || e.Brain.Archetype == EnemyArchetype.None || !e.Alive)
                {
                    continue; // манекен (1-001) и заглушка (0-028) — Brain.Archetype = 0, стоят
                }

                ref EnemyBrain b = ref e.Brain;
                b.StateTicks++;
                UpdateTarget(ref e, tick);

                int ts = b.TargetId >= 0 ? SlotOf(b.TargetId) : -1;
                if (ts >= 0)
                {
                    ref SimEntity target = ref _entities[ts];
                    long dist2 = Torus.Dist2(_shape, e.Mover.Position, target.Mover.Position);
                    if (dist2 <= MeleeRogue.StrikeRange2)
                    {
                        // В зоне удара (8.1 «бьёт в упор»): стоит; замах ведёт фаза боя по состоянию Attack.
                        e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;
                        if (b.State != EnemyState.Attack)
                        {
                            b.State = EnemyState.Attack;
                            b.StateTicks = 0;
                            b.PathStamp = 0;
                            b.BlockedSince = 0; // дошёл — эпизод «упёрся» закрыт (1-013)
                        }

                        e.Input = InputFrame.None;
                        continue;
                    }

                    Chase(ref e, ref target, i, tick);
                    continue;
                }

                // «Интерес» (1-009, 8.5): появился из спавнера — 20 с идёт шагом в сторону игрока-причины
                // (знает направление, не позицию: прямо, без пути; скорость как «домой»). Игрок в зрении —
                // цель берёт UpdateTarget выше, дальше Преследование штатно. Истёк/игрок потерян — Ожидание.
                if (b.State == EnemyState.Interest)
                {
                    e.Mover.UnitsPerTickX10 = RogueHomeSpeedX10;
                    int cs = SlotOf(b.CauseId);
                    if (b.InterestUntil == 0 || tick >= b.InterestUntil || cs < 0 || !_entities[cs].Alive)
                    {
                        b.State = EnemyState.Idle;
                        b.StateTicks = 0;
                        b.CauseId = EnemyBrain.NoTarget;
                        b.InterestUntil = 0;
                        continue;
                    }

                    MoveToward(ref e, Torus.TileOf(_shape, _entities[cs].Mover.Position)); // прямо, без A*
                    continue;
                }

                // Цели нет: Idle/Wander у дома; после потери — Lost (домой), зрение переспрашивает целью выше (8.1, 8.3).
                TileCoord home = new TileCoord(b.HomeX, b.HomeY);
                if (b.State == EnemyState.Wander)
                {
                    e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
                    if (MoveToward(ref e, new TileCoord(b.GoalX, b.GoalY)))
                    {
                        b.State = EnemyState.Idle;
                        b.StateTicks = 0;
                    }

                    continue;
                }

                if (b.State == EnemyState.Idle)
                {
                    e.Input = InputFrame.None;
                    e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
                    long stateStart = tick - b.StateTicks;
                    int span = MeleeRogue.IdleMinTicks + EnemyRng.NextInt(
                        EnemyRng.Hash(e.Id, stateStart, seed),
                        MeleeRogue.IdleMaxTicks - MeleeRogue.IdleMinTicks + 1);
                    if (b.StateTicks < span)
                    {
                        continue;
                    }

                    // Новая точка бродяги (8.1: в 6 тайлах от дома) — шум от (номер, такт, зерно): детерминизм (11.5).
                    ulong h = EnemyRng.Hash(e.Id, tick, seed);
                    Input8 dir = Input8.FromIndex(EnemyRng.NextDir8(h));
                    int dist = 1 + EnemyRng.NextInt(h >> 16, MeleeRogue.WanderRadiusTiles);
                    TileCoord goal = Torus.Wrap(_shape, new TileCoord(home.X + dir.DX * dist, home.Y + dir.DY * dist));
                    if (IsBlocked(_shape, goal))
                    {
                        goal = home; // в стенку не идём — пауза ещё раз
                    }

                    b.GoalX = goal.X;
                    b.GoalY = goal.Y;
                    b.State = EnemyState.Wander;
                    b.StateTicks = 0;
                    e.Mover.UnitsPerTickX10 = RogueWanderSpeedX10;
                    continue;
                }

                // Lost: домой; дом рядом — Idle (8.3).
                e.Mover.UnitsPerTickX10 = RogueHomeSpeedX10;
                if (MoveToward(ref e, home))
                {
                    b.State = EnemyState.Idle;
                    b.StateTicks = 0;
                }
            }
        }

        /// <summary>
        /// Цель такта (8.3 «Агро»): 1) урон ≥ 10 % максимума — обидчик (один раз на удар); 2) зрение 14 тайлов —
        /// ближайший живой персонаж (при равенстве — меньший номер); 3) цель в 28 тайлах («чует») — держится,
        /// вне 28 тайлов 150 тактов — теряется.
        /// </summary>
        private void UpdateTarget(ref SimEntity e, long tick)
        {
            ref EnemyBrain b = ref e.Brain;
            WorldPos at = e.Mover.Position;

            // Агро от урона: обидчик жив, в радиусе потери — цель; засчитывается один раз на удар.
            if (b.LastHitBy >= 0 && b.LastHitTick != 0)
            {
                long by = b.LastHitTick;
                b.LastHitTick = 0;
                if (DamageMath.AtLeastPercent(b.LastHitAmount, e.MaxHealth, MeleeRogue.AggroPct))
                {
                    int s = SlotOf(b.LastHitBy);
                    if (s >= 0 && _entities[s].Alive &&
                        Torus.Dist2(_shape, at, _entities[s].Mover.Position) <= MeleeRogue.Lose2)
                    {
                        if (b.IgnoreTargetId == b.LastHitBy && b.IgnoreUntil != 0)
                        {
                            // Удар по «упёршемуся» (1-013, 9.3): агро сильнее игнора — игнор снимается.
                            b.IgnoreTargetId = EnemyBrain.NoTarget;
                            b.IgnoreUntil = 0;
                        }

                        if (b.TargetId != b.LastHitBy)
                        {
                            b.TargetId = b.LastHitBy;
                            b.PathStamp = 0; // цель сменилась — путь не годится (9.3)
                            b.BlockedSince = 0; // новый эпизод: «упёрся» не переносится (1-013)
                            b.ProgressTick = (int)tick;
                            b.ProgressX = at.X;
                            b.ProgressY = at.Y;
                        }

                        b.LostTicks = 0;
                        return;
                    }
                }
            }

            // Зрение (8.1 «замечает игрока в 12–16 тайлах»): 14 тайлов; ночью ×1,5 → 21 (8.6, 1-009).
            // Игнор цели после сдачи (1-013, 9.3) — цель не берётся, пока срок не истёк.
            if (b.IgnoreUntil != 0 && tick >= b.IgnoreUntil)
            {
                b.IgnoreTargetId = EnemyBrain.NoTarget;
                b.IgnoreUntil = 0;
            }

            int slot = NearestCharacterWithin(at, ScaledSight2(MeleeRogue.Sight2, NightSightMultX1024),
                b.IgnoreUntil != 0 ? b.IgnoreTargetId : EnemyBrain.NoTarget);
            if (slot >= 0)
            {
                b.LostTicks = 0;
                if (b.TargetId != _entities[slot].Id)
                {
                    b.TargetId = _entities[slot].Id;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    b.ProgressTick = (int)tick;
                    b.ProgressX = at.X;
                    b.ProgressY = at.Y;
                }

                return;
            }

            // Цель вне зрения: держится, пока в 28 тайлах (потеря — 150 тактов вне, 8.3).
            if (b.TargetId >= 0)
            {
                int s = SlotOf(b.TargetId);
                if (s < 0 || !_entities[s].Alive)
                {
                    b.TargetId = EnemyBrain.NoTarget;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    b.State = b.State == EnemyState.Chase || b.State == EnemyState.Attack || b.State == EnemyState.Blocked
                        ? EnemyState.Lost : b.State;
                    return;
                }

                if (Torus.Dist2(_shape, at, _entities[s].Mover.Position) <= MeleeRogue.Lose2)
                {
                    b.LostTicks = 0;
                    return;
                }

                b.LostTicks++;
                if (b.LostTicks >= MeleeRogue.LoseTicks)
                {
                    b.TargetId = EnemyBrain.NoTarget;
                    b.LostTicks = 0;
                    b.PathStamp = 0;
                    if (b.State == EnemyState.Chase || b.State == EnemyState.Attack || b.State == EnemyState.Blocked)
                    {
                        b.State = EnemyState.Lost;
                        b.StateTicks = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Преследование (1-007, 9.3): цель в зоне удара handled выше; тут — путь по плану A* (окно 48 × 48,
        /// планировщик такта) или прямо к цели, пока путь в работе/не готов. Переспрос: цель ушла дальше тайла,
        /// каждые 150 тактов или мир правили с запроса («изменился тайл» — сразу, 1-013). Нет пути (Enclosed) —
        /// Blocked («упёрся», 9.3): стоит; пересчёт — при правке мира (кэш Enclosed до смены тайла, з.2);
        /// 20 с — цель в игнор на 30 с, враг в Ожидание; дальше 60 тайлов от дома — то же + домой (з.3).
        /// </summary>
        private void Chase(ref SimEntity e, ref SimEntity target, int slot, long tick)
        {
            ref EnemyBrain b = ref e.Brain;
            e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;

            if (b.State == EnemyState.Blocked)
            {
                // «Упёрся» (1-013, 9.3): стоит у преграды; пересчёт — сразу при правке мира (ревизия сдвинулась);
                // через 20 с цель уходит в игнор на 30 с, враг — в Ожидание (не застревает).
                e.Input = InputFrame.None;
                long blockedFor = tick - b.BlockedSince;
                if (b.BlockedSince != 0 && blockedFor >= MeleeRogue.BlockedGiveUpTicks)
                {
                    b.IgnoreTargetId = b.TargetId;
                    b.IgnoreUntil = tick + MeleeRogue.IgnoreTargetTicks;
                    b.TargetId = EnemyBrain.NoTarget;
                    b.State = EnemyState.Idle;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.BlockedSince = 0;
                    b.EnclosedRev = 0;
                    return;
                }

                if (b.EnclosedRev != 0 && _editRev + 1 != b.EnclosedRev)
                {
                    // Тайл в окне поменялся (стена снесена, дверь открыта) — пересчёт сразу, без ожидания (9.3).
                    TileCoord start = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    TileCoord bt = Torus.Wrap(_shape, Torus.TileOf(_shape, target.Mover.Position));
                    if (_paths.Enqueue(slot, start, bt, PathMode.Walker, tick))
                    {
                        b.PathStamp = (int)tick;
                        b.PathPos = 0;
                        b.PathRev = _editRev;
                        b.EnclosedRev = 0;
                        b.ProgressTick = (int)tick;
                        b.ProgressX = e.Mover.Position.X;
                        b.ProgressY = e.Mover.Position.Y;
                        b.GoalX = bt.X;
                        b.GoalY = bt.Y;
                        b.State = EnemyState.Chase;
                        b.StateTicks = 0;
                    }
                }
                else if (b.EnclosedRev == 0 && blockedFor % MeleeRogue.BlockedRetryTicks == 0 &&
                    (tick + e.Id) % AiDecisionTicks == 0)
                {
                    // Blocked без кэша (загрузка сейва): переспрос каждые 2 с, как в 8.4.
                    b.State = EnemyState.Chase;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.ProgressTick = (int)tick;
                    b.ProgressX = e.Mover.Position.X;
                    b.ProgressY = e.Mover.Position.Y;
                }

                return;
            }

            if (b.State != EnemyState.Chase)
            {
                b.State = EnemyState.Chase;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.ProgressTick = (int)tick;
                b.ProgressX = e.Mover.Position.X;
                b.ProgressY = e.Mover.Position.Y;
            }
            else if (Torus.Dist2(_shape, e.Mover.Position, new WorldPos(b.ProgressX, b.ProgressY)) >= MeleeRogue.ProgressMove2)
            {
                // Продвинулись — контрольная точка за текущей позицией, таймер «упёрся» сброшен.
                b.ProgressTick = (int)tick;
                b.ProgressX = e.Mover.Position.X;
                b.ProgressY = e.Mover.Position.Y;
            }
            else if (b.ProgressTick != 0 && tick - b.ProgressTick >= MeleeRogue.StuckTicks)
            {
                // «Упёрся» без продвижения (1-013, 9.3): 2 с упираемся в преграду (путь Found идёт через
                // стену под ногами) — Blocked; пересчёт при правке мира (кэш), сдача через 20 с.
                b.State = EnemyState.Blocked;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.EnclosedRev = _editRev + 1;
                if (b.BlockedSince == 0)
                {
                    b.BlockedSince = tick;
                }

                e.Input = InputFrame.None;
                return;
            }

            TileCoord goal = Torus.Wrap(_shape, Torus.TileOf(_shape, target.Mover.Position));
            PathBuffer buf = _pathBuf[slot];

            // Не дальше 60 тайлов от точки появления (1-013 з.3, 0-028): увязся далеко — цель в игнор, домой (Lost).
            if (Torus.Dist2(_shape, e.Mover.Position,
                EditRules.CenterOf(Torus.Wrap(_shape, new TileCoord(b.HomeX, b.HomeY)))) > MeleeRogue.Leash2)
            {
                b.IgnoreTargetId = b.TargetId;
                b.IgnoreUntil = tick + MeleeRogue.IgnoreTargetTicks;
                b.TargetId = EnemyBrain.NoTarget;
                b.State = EnemyState.Lost;
                b.StateTicks = 0;
                b.PathStamp = 0;
                b.BlockedSince = 0;
                b.EnclosedRev = 0;
                return;
            }

            // Запрос в работе/не ставился (PathStamp = 0) — идём прямо к цели (на открытой земле путь и не нужен).
            if (b.PathStamp == 0)
            {
                if ((tick + e.Id) % AiDecisionTicks == 0)
                {
                    TileCoord start = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    if (_paths.Enqueue(slot, start, goal, PathMode.Walker, tick))
                    {
                        b.PathStamp = (int)tick;
                        b.PathPos = 0;
                        b.PathRev = _editRev;
                        b.GoalX = goal.X;
                        b.GoalY = goal.Y;
                    }
                }

                MoveToward(ref e, goal);
                return;
            }

            // Запрос в очереди (путь ещё не готов): тоже идём прямо.
            if (buf.RequestedTick != b.PathStamp)
            {
                MoveToward(ref e, goal);
                return;
            }

            // Цель ушла дальше тайла от запрошенной, путь старый (150 тактов) или мир правили с запроса
            // («изменился тайл» — 1-013, 9.3: пересчёт сразу) — новый запрос.
            bool stale = b.StateTicks % MeleeRogue.RepathTicks == 0 ||
                Torus.Dist2(_shape, EditRules.CenterOf(goal), EditRules.CenterOf(new TileCoord(b.GoalX, b.GoalY))) >
                    (long)MeleeRogue.RepathGoalDelta * MeleeRogue.RepathGoalDelta;
            bool revised = _editRev != b.PathRev;
            if (stale || revised)
            {
                if (revised || (tick + e.Id) % AiDecisionTicks == 0)
                {
                    TileCoord start = Torus.Wrap(_shape, Torus.TileOf(_shape, e.Mover.Position));
                    if (_paths.Enqueue(slot, start, goal, PathMode.Walker, tick))
                    {
                        b.PathStamp = (int)tick;
                        b.PathPos = 0;
                        b.PathRev = _editRev;
                        b.EnclosedRev = 0;
                        b.GoalX = goal.X;
                        b.GoalY = goal.Y;
                    }
                }

                MoveToward(ref e, goal);
                return;
            }

            switch (buf.Status)
            {
                case PathStatus.Enclosed:
                    // Пути нет вообще (8.4, 1-013): Blocked — стоит у стены; пересчёт — только при правке мира;
                    // 20 с — сдаётся (игнор цели 30 с). Кэш «Enclosed до смены тайла» (з.2): переспрос не дороже 1 A*.
                    b.State = EnemyState.Blocked;
                    b.StateTicks = 0;
                    b.PathStamp = 0;
                    b.EnclosedRev = _editRev + 1;
                    if (b.BlockedSince == 0)
                    {
                        b.BlockedSince = tick;
                    }

                    e.Input = InputFrame.None;
                    return;

                case PathStatus.None:
                    MoveToward(ref e, goal); // буфер сброшен (RemoveEntity/Restore) — прямо, переспросит
                    b.PathStamp = 0;
                    return;

                default:
                    b.BlockedSince = 0; // путь есть — «упёрся» не копится (1-013)
                    break; // Found / WindowEdge / Partial — идём по шагам
            }

            // Путь готов (Found — до цели; WindowEdge — до края окна; Partial — предельная длина): по шагам.
            int steps = buf.Length;
            while (b.PathPos < steps &&
                Torus.Dist2(_shape, e.Mover.Position,
                    EditRules.CenterOf(Torus.Wrap(_shape, buf.Steps[b.PathPos]))) <= MeleeRogue.NodePass2)
            {
                b.PathPos++;
            }

            if (b.PathPos < steps)
            {
                TileCoord node = Torus.Wrap(_shape, buf.Steps[b.PathPos]);
                e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;
                Int2 d = Torus.Delta(_shape, e.Mover.Position, EditRules.CenterOf(node));
                e.Input = new InputFrame(Input8.FromStick(d.X, d.Y));
                return;
            }

            // Шаги кончились (дошли до края окна/предела/цели) — прямо к цели; переспрос по правилам выше.
            MoveToward(ref e, goal);
        }

        /// <summary>Идти к центру тайла (ввод как у персонажа); пришли (ближе 3/4 тайла) — ввод пустой, true.</summary>
        private bool MoveToward(ref SimEntity e, TileCoord tile)
        {
            Int2 d = Torus.Delta(_shape, e.Mover.Position, EditRules.CenterOf(Torus.Wrap(_shape, tile)));
            if (d.LengthSquared <= MeleeRogue.NodePass2)
            {
                e.Input = InputFrame.None;
                return true;
            }

            e.Input = new InputFrame(Input8.FromStick(d.X, d.Y));
            return false;
        }

        /// <summary>Ближайший живой персонаж к <paramref name="from"/> в пределах √dist2Limit; при равенстве — меньший номер; нет — −1.
        /// <paramref name="ignoreId"/> (1-013, 9.3 «игнор 30 с») — цель, пропущенная при выборе.</summary>
        private int NearestCharacterWithin(WorldPos from, long dist2Limit, int ignoreId)
        {
            int best = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter || !_entities[i].Alive || _entities[i].Id == ignoreId)
                {
                    continue;
                }

                long d2 = Torus.Dist2(_shape, from, _entities[i].Mover.Position);
                if (d2 <= dist2Limit && d2 < bestD)
                {
                    bestD = d2;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Выталкивание (1-007, 8.5 «толкаются»): пары персонаж↔враг-с-мозгом и враг↔враг-с-мозгом, центры
        /// ближе суммы радиусов (204) — разъезжаются по 2 тайла/с (68 единиц/такт на тело) вдоль оси.
        /// Заглушки (0-028) и персонаж↔персонаж не трогает. Порядок по номерам, целочисленно — детерминизм (11.5).
        /// </summary>
        private void StepSeparation()
        {
            const int MinDist = 2 * Collision.RadiusUnits;
            const int PushUnitsPerTick = 68; // 2 тайла/с = 2048 ед/с / 30 = 68,3 — берём 68
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity a = ref _entities[i];
                if (!a.Alive || a.Brain.Archetype == EnemyArchetype.None)
                {
                    continue; // инициатор — только враг с мозгом; персонаж↔персонаж (0.x — сквозные) не трогаем
                }

                for (int j = 0; j < _entityCount; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    ref SimEntity b = ref _entities[j];
                    if (!b.Alive || (!b.IsCharacter && b.Brain.Archetype == EnemyArchetype.None))
                    {
                        continue;
                    }

                    Int2 d = Torus.Delta(_shape, a.Mover.Position, b.Mover.Position);
                    long d2 = d.LengthSquared;
                    if (d2 >= (long)MinDist * MinDist)
                    {
                        continue;
                    }

                    if (d2 == 0)
                    {
                        d = new Int2(MinDist - 1, 0); // позиции совпали — ось по X (детерминированно)
                        d2 = (long)(MinDist - 1) * (MinDist - 1);
                    }

                    int dist = ISqrt(d2);
                    int px = d.X * PushUnitsPerTick / dist;
                    int py = d.Y * PushUnitsPerTick / dist;
                    a.Mover.Position = Torus.Wrap(_shape, new WorldPos(a.Mover.Position.X - px, a.Mover.Position.Y - py));
                    b.Mover.Position = Torus.Wrap(_shape, new WorldPos(b.Mover.Position.X + px, b.Mover.Position.Y + py));
                }
            }
        }

        /// <summary>Целочисленный √ (метод по разрядам; детерминирован, без float).</summary>
        private static int ISqrt(long n)
        {
            long res = 0;
            long bit = 1L << 62;
            while (bit > n)
            {
                bit >>= 2;
            }

            while (bit != 0)
            {
                if (n >= res + bit)
                {
                    n -= res + bit;
                    res = (res >> 1) + bit;
                }
                else
                {
                    res >>= 1;
                }

                bit >>= 2;
            }

            return (int)res;
        }

        /// <summary>Новый планировщик путей (очередь по слотам; без часов — поиск ровно PathMaxPerTick за такт).</summary>
        private PathScheduler NewPathScheduler() =>
            new PathScheduler(_pathFinder, _pathTerrain, _pathBuf, PathQueueCapacity, PathMaxPerTick);

        /// <summary>
        /// Сброс кэша путей (1-007): слоты сдвинулись (<see cref="RemoveEntity"/>) или состояние заменили
        /// (<see cref="Restore"/>) — владельцы запросов протухли; буферы очищаются, пути переспрашиваются.
        /// </summary>
        private void ResetPathCaches()
        {
            for (int i = 0; i < _entities.Length; i++)
            {
                _pathBuf[i].Clear();
                _entities[i].Brain.PathStamp = 0;
                _entities[i].Brain.PathPos = 0;
            }

            _paths = NewPathScheduler();
        }

        /// <summary>
        /// Удар мечом игрока (1-004, `02` 3.1–3.2): в фазе «удар» — сектор от начала удара
        /// (<see cref="MeleeAction.OriginX"/>, фиксирован переходом «замах → удар»), ось — прицел
        /// (<see cref="MeleeAction.AimRaw"/>), радиус <see cref="SwordStrike.ReachUnits"/>, дуга 120°.
        /// Цель — ближайший живой враг, чей круг пересекает сектор и до которого линия удара чиста
        /// (стена/скала/дерево/закрытая дверь — преграда, забор — нет, `02` 3.4); в PvE players не задевает.
        /// Урон 10/10/13 по индексу серии, отбрасывание 1/1/2 тайла ÷ масса; удар решается один раз
        /// (<see cref="MeleeAction.Resolved"/>); постройки урона не получают (цели — только существа).
        /// </summary>
        private void ResolveMeleeStrike(ref SimEntity e)
        {
            ref MeleeAction m = ref e.Mover.Melee;
            if (m.Phase != MeleeAction.PhaseStrike || m.Resolved)
            {
                return;
            }

            m.Resolved = true;
            WorldPos origin = new WorldPos(m.OriginX, m.OriginY);
            var sector = new MeleeSector(origin, Angle16.FromRaw(m.AimRaw), SwordStrike.ReachUnits, SwordStrike.HalfArcSteps);
            TileCoord own = Torus.TileOf(_shape, e.Mover.Position);

            int target = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity t = ref _entities[i];
                if (t.Kind != SimEntity.KindEnemy || !t.Alive)
                {
                    continue; // PvE: меч бьёт врагов, игроков не задевает (`02` 3.4)
                }

                if (!sector.Overlaps(_shape, t.Mover.Position, Collision.RadiusUnits))
                {
                    continue;
                }

                Int2 d = Torus.Delta(_shape, origin, t.Mover.Position);
                long dist = d.LengthSquared;
                if (dist >= bestD)
                {
                    continue;
                }

                if (!StrikeLineClear(origin, t.Mover.Position, own))
                {
                    continue; // стена/забор-преграда между — удара нет (`02` 3.4)
                }

                bestD = dist;
                target = i;
            }

            if (target < 0)
            {
                return;
            }

            int damage = ApplyStrike(_entities[target].Id, e.Id, SwordStrike.DamageFor(m.Index), origin, knockback: true);
            if (damage > 0)
            {
                // Третий удар отбрасывает на 2 тайла (`02` 3.2): скорость ставит ApplyStrike (база ÷ масса), множитель удара — здесь.
                ref Mover tm = ref _entities[target].Mover;
                if (tm.KnockLeft != 0)
                {
                    tm.KnockSpeedX10 *= SwordStrike.KnockTilesFor(m.Index);
                }
            }
        }

        /// <summary>
        /// Линия удара чиста (`02` 3.1, 3.4): отрезок «начало удара — центр цели» с шагом
        /// <see cref="StrikeLosStepUnits"/> unit не пересекает непроходимых тайлов (<see cref="IsBlocked"/>:
        /// стена, скала, дерево, гранит, закрытая дверь/ворота); тайл атакующего пропускается,
        /// забор проходим — линию не прерывает. Шов тора: разность — <see cref="Torus.Delta"/>.
        /// </summary>
        private bool StrikeLineClear(WorldPos from, WorldPos to, TileCoord own)
        {
            Int2 d = Torus.Delta(_shape, from, to);
            int steps = (Math.Abs(d.X) + Math.Abs(d.Y) + StrikeLosStepUnits - 1) / StrikeLosStepUnits;
            for (int i = 1; i < steps; i++)
            {
                WorldPos p = new WorldPos(
                    from.X + (int)((long)d.X * i / steps),
                    from.Y + (int)((long)d.Y * i / steps));
                TileCoord tile = Torus.TileOf(_shape, p);
                if (tile == own)
                {
                    continue;
                }

                if (StrikeTileBlocked(tile))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Шаг выборки линии удара, unit (1/4 тайла): тайловую преграду не перепрыгнуть.</summary>
        public const int StrikeLosStepUnits = 64;

        /// <summary>
        /// Тайл прерывает линию удара (`02` 3.4): как <see cref="IsBlocked"/> для ходьбы, но
        /// деревянный забор — преграда для ходьбы, для удара нет (забор бьётся, приёмка 1-004).
        /// </summary>
        private bool StrikeTileBlocked(TileCoord tile)
        {
            if (IsBlocked(_shape, tile))
            {
                Chunk? chunk = _world.FindChunk(Torus.ChunkOf(_shape, tile));
                if (chunk != null &&
                    ObjectId.IdOf(chunk.GetObject(Chunk.IndexOf(tile.X, tile.Y))) == ObjectId.WoodFence)
                {
                    return false; // забор — линия удара проходит (`02` 3.1:157)
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Удар решён («попал», конвейер 0-029/0-033; 1-001, шаг 3): здоровье цели минусуется по формуле 6.1,
        /// неуязвимость 0,4 с (только персонаж, `02` 6.3), регенерация обнуляется, «в бою» — 8 с обеим сторонам,
        /// отбрасывание от <paramref name="knockOrigin"/> (`02` 6.5; живой цели, смерть — без него),
        /// прерывание замаха врага (≥ 15 % максимума цели, отход 0,5 с, иммунитет 1,5 с, `02` 6.6),
        /// смерть (`02` 8.3 / 13.5: персонаж — 5 с до возрождения, враг — труп 315 тактов).
        /// Нет цели / труп / персонаж в неуязвимости / урон 0 — возврат 0 без изменений.
        /// Зовётся и в такте (атака врага), и вне такта (решение сервером угрозы в <c>Decide</c>, 0-033).
        /// События «урон» и «смерть» — в буфер такта (0 нет: вне такта, сервер читает по своему счёту).
        /// Возвращает урон после брони.
        /// </summary>
        public int ApplyStrike(int targetId, int attackerId, int rawDamage, WorldPos knockOrigin = default, bool knockback = false, byte damageKind = 0)
        {
            int slot = SlotOf(targetId);
            if (slot < 0)
            {
                return 0;
            }

            ref SimEntity target = ref _entities[slot];
            if (!target.Alive)
            {
                return 0;
            }

            if (target.IsCharacter && target.InvulnTicks > 0)
            {
                return 0; // неуязвимость уворота — на уровне Mover.Dodge (проверяет решалка 0-033), здесь — после удара/снаряда
            }

            int damage = DamageMath.Apply(rawDamage, target.Armor);
            if (damage <= 0)
            {
                return 0;
            }

            target.Health = (short)(target.Health - damage);
            if (target.IsCharacter)
            {
                target.InvulnTicks = InvulnStrikeTicks; // 0,4 с (`02` 6.3; у врагов неуязвимости нет)
            }

            target.RegenTicks = 0;
            target.RegenCarryX100 = 0;
            target.CombatTicks = CombatFlagTicks;

            int attackerSlot = SlotOf(attackerId);
            if (attackerSlot >= 0)
            {
                _entities[attackerSlot].CombatTicks = CombatFlagTicks; // «в бою» и за нанесённый урон (6.3)

                // Прерывание (6.6): цель — враг в замахе, урон ≥ 15 % максимума цели, иммунитет не выставлен.
                if (target.Kind == SimEntity.KindEnemy && target.AttackWindup > 0 && target.AttackImmune == 0 &&
                    DamageMath.AtLeastPercent(damage, target.MaxHealth, InterruptPctEnemy))
                {
                    target.AttackWindup = 0;
                    target.AttackRecover = InterruptRecoverTicks;
                    target.AttackImmune = InterruptImmuneTicks;
                    target.AttackTarget = -1;
                }

                // Персонажа прерывает удар ≥ 10 % его максимума (6.6, хук 1-004): замах меча гаснет, серия сбрасывается.
                if (target.IsCharacter && target.Mover.Melee.Phase == MeleeAction.PhaseWindup &&
                    DamageMath.AtLeastPercent(damage, target.MaxHealth, InterruptPctCharacter))
                {
                    target.Mover.Melee.Cancel();
                }

                // Агро (1-007, 8.3 «получил урон — сменяет цель»): мозг запоминает обидчика и урон;
                // UpdateTarget засчитывает запись один раз (сбрасывает LastHitTick) на следующем решении ИИ.
                if (target.Kind == SimEntity.KindEnemy && target.Brain.Archetype != EnemyArchetype.None)
                {
                    target.Brain.LastHitTick = Tick + 1;
                    target.Brain.LastHitBy = attackerId;
                    target.Brain.LastHitAmount = damage;
                }
            }

            AddEvent(new SimEvent(SimEventKind.Damage, Tick, default, -1, EditResult.Applied, 0,
                requester: attackerId, victim: target.Id, damage: damage, damageType: damageKind));

            if (target.Health <= 0)
            {
                target.Health = 0;
                target.DeadTicks = target.IsCharacter ? RespawnDelayTicks : CorpseTicks;
                target.AttackWindup = 0;
                target.AttackRecover = 0;
                target.AttackImmune = 0;
                target.AttackTarget = -1;
                target.Input = InputFrame.None;
                AddEvent(new SimEvent(SimEventKind.Death, Tick, default, -1, EditResult.Applied, 0,
                    requester: attackerId, victim: target.Id, damage: damage, damageType: damageKind));
                return damage;
            }

            if (knockback)
            {
                // 6.5: рывок от источника на база ÷ масса; живая цель (смерть — падение без рывка);
                // стена останавливает без урона (Mover.StepFrame); ввод цели в рывке не действует.
                target.Mover.StartKnock(_shape, knockOrigin, target.Mass, Input8.East);
            }

            return damage;
        }

        /// <summary>
        /// Начать замах врага на персонажа (1-001, `02` 8.1): сектор фиксируется началом замаха
        /// (позиция врага + направление на цель). Враг жив, замахa нет, цель жива — иначе false.
        /// </summary>
        public bool StartAttack(int attackerId, int targetId)
        {
            int a = SlotOf(attackerId);
            int t = SlotOf(targetId);
            if (a < 0 || t < 0)
            {
                return false;
            }

            ref SimEntity e = ref _entities[a];
            if (e.Kind != SimEntity.KindEnemy || !e.Alive || e.AttackWindup > 0)
            {
                return false;
            }

            ref SimEntity target = ref _entities[t];
            if (!target.Alive)
            {
                return false;
            }

            BeginWindup(ref e, target);
            return true;
        }

        /// <summary>
        /// Цикл манекена (приёмка 1-001: «Ближний» бьёт по таймеру): замах через <paramref name="periodTicks"/>
        /// тактов после конца отхода, цель — ближайший живой персонаж; 0 / −1 — выключить. <paramref name="rawDamage"/> — сырой урон.
        /// </summary>
        public bool EnableAttackCycle(int enemyId, int periodTicks, int rawDamage = StrikeRawMelee)
        {
            int slot = SlotOf(enemyId);
            if (slot < 0 || _entities[slot].Kind != SimEntity.KindEnemy || periodTicks < -1)
            {
                return false;
            }

            _entities[slot].AttackPeriod = periodTicks;
            if (rawDamage > 0)
            {
                _entities[slot].AttackRaw = (byte)Math.Min(255, rawDamage);
            }

            return true;
        }

        /// <summary>Ближайший живой персонаж к <paramref name="from"/> (перебор по номеру, дистанция на центр ≤
        /// <paramref name="maxDistUnits"/>; при равенстве — меньший номер); нет — −1.</summary>
        private int NearestCharacterSlot(WorldPos from, int maxDistUnits)
        {
            int best = -1;
            long bestD = long.MaxValue;
            long limit = (long)maxDistUnits * maxDistUnits;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter || !_entities[i].Alive)
                {
                    continue;
                }

                Int2 d = Torus.Delta(_shape, from, _entities[i].Mover.Position);
                long dist = d.LengthSquared;
                if (dist <= limit && (best < 0 || dist < bestD))
                {
                    bestD = dist;
                    best = i;
                }
            }

            return best;
        }

        private void BeginWindup(ref SimEntity attacker, in SimEntity target)
        {
            attacker.AttackWindup = WindupTicks;
            attacker.AttackOrigin = attacker.Mover.Position;
            attacker.AttackFacingRaw = (short)(int)Angle16.FromDirection(Torus.Delta(_shape, attacker.Mover.Position, target.Mover.Position)).Raw;
            attacker.AttackTarget = target.Id;
        }

        /// <summary>Возрождение (13.5, 1-001): точка старта, полное здоровье, 3 с неуязвимости (снимается первой атакой), без потерь.</summary>
        private void Respawn(ref SimEntity e, long tick)
        {
            e.Health = e.MaxHealth;
            e.Mover = new Mover(Torus.Wrap(_shape, _respawnCenter), e.Mover.UnitsPerTickX10);
            e.InvulnTicks = InvulnRespawnTicks;
            e.RegenTicks = RegenDelayTicks;
            e.RegenCarryX100 = 0;
            e.CombatTicks = 0;
            e.Input = InputFrame.None;
            AddEvent(new SimEvent(SimEventKind.Respawn, tick, default, -1, EditResult.Applied, 0, victim: e.Id));
        }

        private void AddEvent(in SimEvent e)
        {
            if (_eventCount == _events.Length)
            {
                _eventsOverflow = true;
                return;
            }

            _events[_eventCount++] = e;
        }

        // Сортировка слиянием снизу вверх: устойчивая (порядок прихода внутри чанка сохраняется),
        // O(n log n), без выделений — второй массив заранее. Array.Sort с IComparer выделяет делегат.
        private void SortByChunkThenArrival(int n)
        {
            EditRequest[] src = _edits;
            EditRequest[] dst = _editScratch;
            for (int width = 1; width < n; width <<= 1)
            {
                for (int lo = 0; lo < n; lo += width << 1)
                {
                    int mid = Math.Min(lo + width, n);
                    int hi = Math.Min(lo + (width << 1), n);
                    int a = lo;
                    int b = mid;
                    int k = lo;
                    while (a < mid && b < hi)
                    {
                        dst[k++] = Compare(src[b].Chunk, src[a].Chunk) < 0 ? src[b++] : src[a++];
                    }

                    while (a < mid)
                    {
                        dst[k++] = src[a++];
                    }

                    while (b < hi)
                    {
                        dst[k++] = src[b++];
                    }
                }

                EditRequest[] t = src;
                src = dst;
                dst = t;
            }

            if (!ReferenceEquals(src, _edits))
            {
                Array.Copy(src, _edits, n);
            }
        }

        private static int Compare(ChunkCoord a, ChunkCoord b) =>
            a.Y != b.Y ? (a.Y < b.Y ? -1 : 1) : a.X != b.X ? (a.X < b.X ? -1 : 1) : 0;

        // ---------- Проходимость ----------

        /// <summary>
        /// Тайл мешает ходить (docs/05, 1.3): пол непроходим (лава, неизвестный) или объект непроходим;
        /// у «части» многотайлового объекта свойства — у якоря; дверь — по флагу записи постройки.
        /// Несозданный чанк — пусто, проходим.
        /// </summary>
        public bool IsBlocked(MapShape map, TileCoord tile)
        {
            Chunk? chunk = _world.FindChunk(Torus.ChunkOf(map, tile));
            if (chunk == null)
            {
                return false;
            }

            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (!FloorId.IsPassable(chunk.GetFloor(index)))
            {
                return true;
            }

            ushort obj = chunk.GetObject(index);
            if (obj == ObjectId.Empty)
            {
                return false;
            }

            BuildRecord record;
            if (ObjectId.IsPart(obj))
            {
                TileCoord anchor = _world.AnchorOf(tile);
                obj = _world.GetObject(anchor);
                if (!_world.TryGetBuild(anchor, out record))
                {
                    record = default;
                }
            }
            else if (!chunk.TryGetBuild(index, out record))
            {
                record = default;
            }

            return !ObjectId.IsPassable(obj, record.Flags);
        }

        private readonly struct EditRequest
        {
            public readonly ChunkCoord Chunk;
            public readonly int Arrival;
            public readonly TileEdit Edit;
            public readonly int Requester;
            public readonly int Tag;
            public readonly bool CheckCreatures;

            public EditRequest(ChunkCoord chunk, int arrival, TileEdit edit, int requester, int tag, bool checkCreatures)
            {
                Chunk = chunk;
                Arrival = arrival;
                Edit = edit;
                Requester = requester;
                Tag = tag;
                CheckCreatures = checkCreatures;
            }
        }
    }
}
