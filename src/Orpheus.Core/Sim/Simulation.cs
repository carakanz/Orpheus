using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
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
    /// <item><b>Движение</b>: сущности по номеру, каждая — <see cref="Mover.Step"/> по своему вводу
    /// (коллизия с тайлами мира, каким он был в начале такта); перешедшие границу чанка переносятся в сетке
    /// <see cref="Grid"/> (docs/07, 4.4).</item>
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
    public sealed class Simulation
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
        private readonly EditResult[] _preResult; // исход отказа до пачки: существо, враг, лишняя сверх тайлов
        private readonly EditResult[] _results = new EditResult[TilePatch.MaxEdits];
        private int _editCount;

        private readonly int _buildMapReserve;
        private readonly SimEvent[] _events;
        private int _eventCount;
        private bool _eventsOverflow;

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
            for (int i = 0; i < maxEntities; i++)
            {
                _frameCount[i] = -1;
            }

            _edits = new EditRequest[maxEditsPerTick];
            _editScratch = new EditRequest[maxEditsPerTick];
            _batchOf = new int[maxEditsPerTick];
            _preResult = new EditResult[maxEditsPerTick];

            // Точка возрождения по умолчанию — центр карты (SpawnCenter ботов, ServerHost.DefaultSpawnCenter).
            _respawnCenter = new WorldPos(
                (_shape.Width / 2 << Torus.SubTileBits) + Torus.SubTilesPerTile / 2,
                (_shape.Height / 2 << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);

            // Событий на такт: исход каждой правки + ревизия каждого чанка — не больше 2 × правок.
            _events = new SimEvent[maxEditsPerTick * 2];
            _buildMapReserve = reserveBuildMaps;
            RefillPools();
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
            _nextId = Math.Max(nextId, maxId + 1);
            Tick = tick;
        }

        /// <summary>Следующий номер сущности (пишется в сохранение: номера не переиспользуются).</summary>
        public int NextEntityId => _nextId;

        /// <summary>
        /// Здоровье и боевые таймеры (1-001) в формат сохранений 0-037 не входят («Не трогать» карточки):
        /// при загрузке сущность получает полные значения — вернувшийся персонаж входит с полным здоровьем
        /// (решение Продюсера, `08` 3.1: сохранения персонажей сверх позиции и сумки отложены).
        /// </summary>
        private static SimEntity RestoreDefaults(SimEntity e)
        {
            if (e.MaxHealth <= 0)
            {
                e.MaxHealth = (short)(e.Kind == SimEntity.KindEnemy ? MaxHealthMeleeEnemy : MaxHealthCharacter);
            }

            if (e.Health <= 0)
            {
                e.Health = e.MaxHealth;
            }

            if (e.Mass == 0)
            {
                e.Mass = 1;
            }

            if (e.RegenTicks == 0)
            {
                e.RegenTicks = RegenDelayTicks;
            }

            if (e.AttackTarget == 0)
            {
                e.AttackTarget = -1;
            }

            if (e.AttackPeriod == 0)
            {
                e.AttackPeriod = -1;
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
            Enqueue(tile, floor, obj, hasRecord, record, requester, tag, EditRules.NeedsClearance(obj));

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

            // 2. Движение — по номеру сущности: тактом по вводу или кадрами ввода (сетевой персонаж, 0-019).
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].Alive)
                {
                    continue; // мёртвый (труп, ждёт возрождения) не движется (1-001, 13.5, 8.3)
                }

                int frames = _frameCount[i];
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

            // 3. Бой (1-001): таймеры здоровья, атаки врагов, смерть и возрождение — после движения,
            // сетка подхватывает teleport возрождения ниже.
            StepCombat(tick);

            // Сетка по чанкам: перешедшие границу чанка переносятся (4.4).
            for (int i = 0; i < _entityCount; i++)
            {
                _grid.Update(i, Torus.ChunkOf(_shape, _entities[i].Mover.Position));
            }

            // 4. Правки мира: одна пачка на чанк за такт.
            ApplyEditBatches(tick);

            // 5. События: буфер такта закрыт; переполнение — ошибка ёмкости, а не тихая потеря.
            if (_eventsOverflow)
            {
                throw new InvalidOperationException($"Такт {tick}: событий больше {_events.Length}.");
            }

            Tick = tick;

            // 6. Трупы (8.3; 1-001): враг с истёкшим тайром трупа удаляется (RemoveEntity — сдвиг списка,
            // Rebuild сетки; в конце такта — «вне такта»). Порядок: от хвоста (по убыванию номера) — сдвиги не мешают.
            for (int i = _entityCount - 1; i >= 0; i--)
            {
                if (_entities[i].Kind == SimEntity.KindEnemy && _entities[i].Health <= 0 && _entities[i].DeadTicks == 0)
                {
                    RemoveEntity(_entities[i].Id);
                }
            }
        }

        private void ApplyEditBatches(long tick)
        {
            int n = _editCount;
            if (n == 0)
            {
                return;
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
                    EditResult pre = _edits[i].CheckCreatures ? CheckCreatures(chunk, _edits[i].Edit) : EditResult.Applied;
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
                }

                if (revision != before)
                {
                    AddEvent(new SimEvent(SimEventKind.ChunkRevised, tick, chunk, -1, EditResult.Applied, revision));
                }

                start = end;
            }

            _editCount = 0;
        }

        /// <summary>
        /// Существа у тайла правки (`05` 1.3; 0-028): непроходимое на тайл, который задевает сущность, — «занято существом»;
        /// непроходимое или ловушка ближе 2 тайлов к врагу — «враг ближе 2 тайлов». Сущностей ≤ ёмкости — перебор всех.
        /// </summary>
        private EditResult CheckCreatures(ChunkCoord chunk, in TileEdit edit)
        {
            var tile = new TileCoord(
                (chunk.X << Torus.ChunkBits) + (edit.Index & (Torus.ChunkSize - 1)),
                (chunk.Y << Torus.ChunkBits) + (edit.Index >> Torus.ChunkBits));
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
                    e.AttackWindup--;
                    if (e.AttackWindup == 0)
                    {
                        var sector = new MeleeSector(e.AttackOrigin, Angle16.FromRaw(e.AttackFacingRaw),
                            EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
                        int slot = SlotOf(e.AttackTarget);
                        if (slot >= 0 && _entities[slot].Alive &&
                            sector.Overlaps(_shape, _entities[slot].Mover.Position, Collision.RadiusUnits))
                        {
                            ApplyStrike(_entities[slot].Id, e.Id, e.AttackRaw, e.Mover.Position, knockback: true);
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

                if (e.AttackPeriod > 0 && e.AttackRecover == 0)
                {
                    int target = NearestCharacterSlot(e.Mover.Position, EnemyMelee.ReachUnits + Collision.RadiusUnits);
                    if (target >= 0)
                    {
                        BeginWindup(ref e, _entities[target]);
                    }
                }
            }
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
