using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
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
        /// пока ему не дали ввод. Номер — следующий. Возвращает номер.
        /// </summary>
        public int AddEnemy(WorldPos position)
        {
            int id = AddEntity(position);
            _entities[SlotOf(id)].Kind = SimEntity.KindEnemy;
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
                _entities[i] = entities[i];
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
                }
            }

            // 2. Движение — по номеру сущности: тактом по вводу или кадрами ввода (сетевой персонаж, 0-019).
            for (int i = 0; i < _entityCount; i++)
            {
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
                    _entities[i].Mover.StepFrame(_shape, f.Direction, f.DodgePressed, _solid); // с уворотом (0-029)
                }

                _frameCount[i] = -1;
            }

            // Сетка по чанкам: перешедшие границу чанка переносятся (4.4).
            for (int i = 0; i < _entityCount; i++)
            {
                _grid.Update(i, Torus.ChunkOf(_shape, _entities[i].Mover.Position));
            }

            // 3. Правки мира: одна пачка на чанк за такт.
            ApplyEditBatches(tick);

            // 4. События: буфер такта закрыт; переполнение — ошибка ёмкости, а не тихая потеря.
            if (_eventsOverflow)
            {
                throw new InvalidOperationException($"Такт {tick}: событий больше {_events.Length}.");
            }

            Tick = tick;
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
