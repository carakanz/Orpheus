using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// E2-1 (шаг 2.1): предметы на земле («кучи») и заявки игрока на операции с ячейками сумки.
    /// <para>
    /// Порядок детерминирован (11.5): кучи — по номеру, персонажи — по месту в списке сущностей, заявки — по приходу.
    /// Случайность разброса — <see cref="SplitMix64.Hash(int,int,int,ulong)"/> от тайла, номера кучи и зерна мира.
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Время жизни кучи: 10 мин = 18 000 тактов (<c>06</c> 5.3; для добычи копания — допущение E2-1).</summary>
        public const int PileLifetimeTicks = 10 * 60 * TicksPerSecond;

        /// <summary>Срок кучи «не исчезает» (остаток старой сумки при переносе сохранения формата 6 → 7 — ничего не теряется).</summary>
        public const long PileNeverExpires = long.MaxValue;

        /// <summary>Радиус подбора: 1,5 тайла = 384 unit (<c>06</c> 5.3 «касание и притяжение с 1,5 тайла»).</summary>
        public const int PickupRadiusUnits = 384;

        /// <summary>Задержка подбора добычи копания и возврата при сносе: 10 тактов (≈ 0,33 с) — добыча успевает «упасть»,
        /// а стоящий рядом не ждёт её заметно (допущение E2-1; 20 тактов вдвое замедляли добычу бота <c>dig_line</c>).</summary>
        public const int LootPickupDelayTicks = 10;

        /// <summary>Задержка подбора выброшенного из сумки: 90 тактов (3 с) — успеть отойти (допущение E2-1).</summary>
        public const int DropPickupDelayTicks = 90;

        /// <summary>Одинаковые кучи ближе этого (по каждой оси) сливаются: 1 тайл = 256 unit.</summary>
        public const int PileMergeUnits = Torus.SubTilesPerTile;

        /// <summary>Потолок количества в одной куче (слияние сверх него не идёт — рядом ложится вторая).</summary>
        public const int PileMaxCount = 9999;

        /// <summary>Разброс добычи вокруг центра тайла: ± 48 unit (≈ 0,19 тайла).</summary>
        public const int PileScatterUnits = 48;

        /// <summary>Заявок на операции с сумкой за такт (лишние — отказ).</summary>
        public const int MaxBagOpsPerTick = 256;

        private const int TicksPerSecond = 30;

        /// <summary>
        /// C-031 (`BRIEF` 11n): в мире не больше 10 000 непропавших воткнувшихся стрел — новая сверх предела убирает самую старую
        /// (меньший номер). Тесты уменьшают предел через <see cref="StuckArrowLimit"/>.
        /// </summary>
        public const int MaxStuckArrows = 10_000;

        /// <summary>
        /// Пол-экрана самого дальнего обзора по X, тайлов: камера 96 × 54 т (`GameBoot.FarViewTiles`, 16:9) плюс запас на
        /// другие пропорции экрана и сдвиг камеры — 52 × 32 *(прикидка C-031)*. Стрелу старше 10 мин в этом окне вокруг любого
        /// персонажа в мире не убираем: «в границах экрана никого нет» (`BRIEF` 11n).
        /// </summary>
        public const int ScreenHalfTilesX = 52;

        /// <summary>Пол-экрана по Y, тайлов (см. <see cref="ScreenHalfTilesX"/>).</summary>
        public const int ScreenHalfTilesY = 32;

        private int _stuckArrowLimit = MaxStuckArrows;
        private int _stuckArrowCount;
        private int[] _pickers = new int[16];
        private int _pickerCount;

        /// <summary>Предел воткнувшихся стрел (по умолчанию <see cref="MaxStuckArrows"/>; меньше — только тесты).</summary>
        internal int StuckArrowLimit
        {
            get => _stuckArrowLimit;
            set => _stuckArrowLimit = Math.Max(1, value);
        }

        /// <summary>Воткнувшихся стрел в мире (C-031).</summary>
        public int StuckArrowCount => _stuckArrowCount;

        private struct BagRequest
        {
            public int EntityId;
            public BagOp Op;
            public byte From;
            public byte To;
            public ushort Count;

            /// <summary>Заявка с открытым сундуком (E2-5): ячейки от <see cref="ChestCellBase"/> — ячейки сундука на этом тайле.</summary>
            public bool HasChest;
            public TileCoord Chest;
        }

        private GroundPile[] _piles = new GroundPile[64];
        private int _pileCount;
        private int _nextPileId = 1;
        private readonly BagRequest[] _bagOps = new BagRequest[MaxBagOpsPerTick];
        private int _bagOpCount;

        /// <summary>Кучи на земле по возрастанию номера.</summary>
        public ReadOnlySpan<GroundPile> Piles => new ReadOnlySpan<GroundPile>(_piles, 0, _pileCount);

        /// <summary>Следующий номер кучи (пишется в сохранение: номера не переиспользуются).</summary>
        public int NextPileId => _nextPileId;

        /// <summary>
        /// Положить кучу на землю (тесты, выдача вне такта): позиция сдвигается с непроходимого тайла на соседний,
        /// одинаковая куча рядом принимает добавку. Возвращает номер кучи (новой или принявшей); 0 — нечего класть.
        /// </summary>
        public int AddPile(byte item, int count, WorldPos position, int pickupDelayTicks = 0) =>
            SpawnPile(Tick, item, count, position, pickupDelayTicks);

        /// <summary>Загрузка куч из сохранения: только до первой кучи; строго по возрастанию номера.</summary>
        public void RestorePiles(ReadOnlySpan<GroundPile> piles, int nextPileId)
        {
            if (_pileCount != 0)
            {
                throw new InvalidOperationException("Загрузка куч — только в симуляцию без куч.");
            }

            int maxId = 0;
            for (int i = 0; i < piles.Length; i++)
            {
                if (piles[i].Id <= maxId || !ItemTable.Exists(piles[i].Item) || piles[i].Count == 0 || piles[i].Kind > GroundPile.MaxKind)
                {
                    throw new ArgumentException($"Куча № {i}: {piles[i]} — номер не растёт или предмет/количество неверны.", nameof(piles));
                }

                maxId = piles[i].Id;
            }

            if (piles.Length > _piles.Length)
            {
                _piles = new GroundPile[piles.Length + 64];
            }

            piles.CopyTo(_piles);
            _pileCount = piles.Length;
            _nextPileId = Math.Max(nextPileId, maxId + 1);
            _stuckArrowCount = 0;
            for (int i = 0; i < piles.Length; i++)
            {
                if (piles[i].IsStuckArrow)
                {
                    _stuckArrowCount++;
                }
            }
        }

        /// <summary>
        /// Заявка игрока на операцию с сумкой (E2-1; клиент просит, сервер решает): исполнится в ближайшем такте
        /// по порядку прихода. Неверная заявка молча не исполняется (сумка не меняется). Очередь полна, персонаж мёртв
        /// сущности нет или количество отрицательное — false.
        /// </summary>
        public bool RequestBagOp(int entityId, BagOp op, int from, int to, int count)
        {
            // Мёртвый сумкой не распоряжается — то же правило, что у правок мира (C-004): отказ сразу. Умер между заявкой
            // и тактом — заявка не исполнится (ApplyBagOp). Нет такой сущности или не персонаж — тоже отказ.
            int slot = SlotOf(entityId);
            if (slot < 0 || !_entities[slot].IsCharacter || !_entities[slot].Alive || count < 0 || _bagOpCount == _bagOps.Length)
            {
                return false;
            }

            _bagOps[_bagOpCount++] = new BagRequest
            {
                EntityId = entityId,
                Op = op,
                From = (byte)Math.Clamp(from, 0, 255),
                To = (byte)Math.Clamp(to, 0, 255),
                Count = (ushort)Math.Clamp(count, 0, ushort.MaxValue),
            };
            return true;
        }

        /// <summary>Фаза предметов такта: заявки на сумку → истечение куч → подбор.</summary>
        private void StepItems(long tick)
        {
            for (int i = 0; i < _bagOpCount; i++)
            {
                ApplyBagOp(tick, _bagOps[i]);
            }

            _bagOpCount = 0;
            StepCraft(tick); // E2-3: очереди изготовления — после заявок, до подбора
            if (_pileCount == 0)
            {
                return;
            }

            // Истечение и подбор одним проходом; список сжимается на месте, порядок по номеру сохраняется.
            CollectPickers();
            int kept = 0;
            for (int i = 0; i < _pileCount; i++)
            {
                GroundPile p = _piles[i];
                if (p.ExpireTick <= tick && (!p.IsStuckArrow || !OnAnyScreen(p.Position)))
                {
                    if (p.IsStuckArrow)
                    {
                        _stuckArrowCount--; // C-031: 10 мин с выстрела и ни у кого на экране
                    }

                    continue;
                }

                if (p.PickupTick <= tick)
                {
                    int taker = NearestPicker(p.Position, p.Item);
                    if (taker >= 0)
                    {
                        int put = _entities[taker].Inv.Add(p.Item, p.Count);
                        if (put > 0)
                        {
                            p.Count = (ushort)(p.Count - put);
                            AddEvent(new SimEvent(SimEventKind.PilePickup, tick, default, 0, EditResult.Applied, 0,
                                requester: _entities[taker].Id, tag: p.Id, victim: p.Item, damage: put));
                        }
                    }
                }

                if (p.Count > 0)
                {
                    _piles[kept++] = p;
                }
                else if (p.IsStuckArrow)
                {
                    _stuckArrowCount--; // подобрали — стрела в сумке
                }
            }

            _pileCount = kept;
        }

        /// <summary>Персонажи в мире по месту в списке сущностей — кандидаты подбора и «экраны» (один раз за такт).</summary>
        private void CollectPickers()
        {
            _pickerCount = 0;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter)
                {
                    continue;
                }

                if (_pickerCount == _pickers.Length)
                {
                    Array.Resize(ref _pickers, _pickers.Length * 2);
                }

                _pickers[_pickerCount++] = i;
            }
        }

        /// <summary>
        /// Точка в окне экрана (<see cref="ScreenHalfTilesX"/> × <see cref="ScreenHalfTilesY"/>) вокруг какого-нибудь персонажа
        /// в мире (живого или мёртвого — камера стоит над телом), по тору. Только после <see cref="CollectPickers"/>.
        /// </summary>
        private bool OnAnyScreen(WorldPos pos)
        {
            const int hx = ScreenHalfTilesX * Torus.SubTilesPerTile, hy = ScreenHalfTilesY * Torus.SubTilesPerTile;
            for (int k = 0; k < _pickerCount; k++)
            {
                Int2 d = Torus.Delta(_shape, _entities[_pickers[k]].Mover.Position, pos);
                if (Math.Abs(d.X) <= hx && Math.Abs(d.Y) <= hy)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Воткнувшаяся стрела (C-031, `BRIEF` 11n «стрела не должна около себя оставлять предмет»): сама — предмет на земле
        /// ровно в точке остановки (не сдвигается на соседний тайл, не сливается), вид <paramref name="kind"/>, ось
        /// <paramref name="aim"/>. Срок — 10 мин от такта выстрела <paramref name="shotTick"/>, дальше пропадает, только когда
        /// её нет ни на одном экране. Сверх <see cref="StuckArrowLimit"/> пропадает самая старая. В воде (и на непроходимом
        /// полу) стрела тонет — 0. Возвращает номер.
        /// </summary>
        private int SpawnStuckArrow(long tick, long shotTick, byte item, WorldPos at, ushort aim, byte kind)
        {
            WorldPos pos = Torus.Wrap(_shape, at);
            byte floor = _world.GetFloor(Torus.TileOf(_shape, pos));
            if (!ItemTable.Exists(item) || !FloorId.IsPassable(floor)) // вода (C-018: проходима только замедлением), лава, пустота
            {
                return 0;
            }

            if (_stuckArrowCount >= _stuckArrowLimit)
            {
                EvictOldestStuckArrow();
            }

            if (_pileCount == _piles.Length)
            {
                Array.Resize(ref _piles, _piles.Length * 2);
            }

            int id = _nextPileId++;
            _piles[_pileCount++] = new GroundPile
            {
                Id = id,
                Item = item,
                Count = 1,
                Position = pos,
                ExpireTick = shotTick + PileLifetimeTicks,
                PickupTick = tick + LootPickupDelayTicks,
                Kind = kind,
                Aim = aim,
            };
            _stuckArrowCount++;
            return id;
        }

        /// <summary>Только для тестов (C-031): воткнуть стрелу без выстрела — как <see cref="SpawnStuckArrow"/> в этом такте.</summary>
        internal int AddStuckArrowForTests(WorldPos at, ushort aim, byte kind, long shotTick) =>
            SpawnStuckArrow(Tick, shotTick, Items.Arrows, at, aim, kind);

        /// <summary>Убрать самую старую воткнувшуюся стрелу (меньший номер — первая в списке); порядок остальных цел.</summary>
        private void EvictOldestStuckArrow()
        {
            for (int i = 0; i < _pileCount; i++)
            {
                if (_piles[i].IsStuckArrow)
                {
                    Array.Copy(_piles, i + 1, _piles, i, _pileCount - i - 1);
                    _pileCount--;
                    _stuckArrowCount--;
                    return;
                }
            }
        }

        /// <summary>
        /// Ближайший живой персонаж в радиусе подбора, у которого есть место под предмет; равная дальность — меньший
        /// номер сущности (первый в списке). Нет такого — −1.
        /// </summary>
        private int NearestPicker(WorldPos pos, byte item)
        {
            int best = -1;
            long bestD2 = (long)PickupRadiusUnits * PickupRadiusUnits + 1;
            for (int k = 0; k < _pickerCount; k++)
            {
                int i = _pickers[k];
                if (!_entities[i].Alive)
                {
                    continue;
                }

                long d2 = Torus.Dist2(_shape, _entities[i].Mover.Position, pos);
                if (d2 < bestD2 && _entities[i].Inv.RoomFor(item) > 0)
                {
                    best = i;
                    bestD2 = d2;
                }
            }

            return best;
        }

        private void ApplyBagOp(long tick, in BagRequest r)
        {
            int slot = SlotOf(r.EntityId);
            if (slot < 0 || !_entities[slot].IsCharacter || !_entities[slot].Alive)
            {
                if (slot >= 0 && (r.Op == BagOp.Craft || r.Op == BagOp.CraftCancel || r.Op == BagOp.CraftInto))
                {
                    CraftRefusedEvent(tick, r.EntityId, r.Op, r.From, CraftReply.Dead); // умер между заявкой и тактом — ответ всё равно один
                }

                return;
            }

            if (r.HasChest)
            {
                ApplyChestOp(tick, slot, r); // E2-5: сумка ↔ сундук
                return;
            }

            ref Inventory inv = ref _entities[slot].Inv; // сумка в массиве сущностей, не копия
            switch (r.Op)
            {
                case BagOp.Move:
                    inv.Move(r.From, r.To);
                    break;
                case BagOp.Split:
                    inv.Split(r.From, r.To, r.Count);
                    break;
                case BagOp.Drop:
                    {
                        byte item = inv.ItemAt(r.From);
                        int n = inv.TakeFrom(r.From, r.Count == 0 ? int.MaxValue : r.Count);
                        if (n > 0)
                        {
                            SpawnPile(tick, item, n, _entities[slot].Mover.Position, DropPickupDelayTicks);
                        }

                        break;
                    }

                case BagOp.Craft:
                    ApplyCraft(tick, slot, r.From, r.Count);
                    break;
                case BagOp.CraftCancel:
                    ApplyCraftCancel(tick, slot, r.From, r.Count);
                    break;
                case BagOp.CraftInto:
                    ApplyCraftInto(tick, slot, r.From, r.To, r.Count); // C-013
                    break;
            }
        }

        /// <summary>Добыча и возврат материалов — кучей на тайле объекта с небольшим разбросом (E2-1).</summary>
        private void DropOnTile(long tick, TileCoord tile, byte item, int count)
        {
            if (count <= 0 || !ItemTable.Exists(item))
            {
                return;
            }

            ulong h = SplitMix64.Hash(tile.X, tile.Y, _nextPileId, _world.Seed);
            int span = 2 * PileScatterUnits + 1;
            int dx = (int)(h % (ulong)span) - PileScatterUnits;
            int dy = (int)((h >> 20) % (ulong)span) - PileScatterUnits;
            WorldPos center = Torus.CornerOf(tile);
            var pos = new WorldPos(center.X + Torus.SubTilesPerTile / 2 + dx, center.Y + Torus.SubTilesPerTile / 2 + dy);
            SpawnPile(tick, item, count, pos, LootPickupDelayTicks);
        }

        private int SpawnPile(long tick, byte item, int count, WorldPos position, int pickupDelayTicks, bool forever = false)
        {
            if (count <= 0 || !ItemTable.Exists(item))
            {
                return 0;
            }

            WorldPos pos = FreeGroundNear(Torus.Wrap(_shape, position));
            long expire = forever ? PileNeverExpires : tick + PileLifetimeTicks;
            long pickup = tick + Math.Max(0, pickupDelayTicks);
            int first = 0;
            while (count > 0)
            {
                // Слияние: первая по номеру одинаковая куча в пределах тайла, пока в ней есть место.
                int into = -1;
                for (int i = 0; i < _pileCount; i++)
                {
                    if (_piles[i].Item != item || _piles[i].Count >= PileMaxCount || _piles[i].IsStuckArrow)
                    {
                        continue;
                    }

                    Int2 d = Torus.Delta(_shape, _piles[i].Position, pos);
                    if (Math.Abs(d.X) <= PileMergeUnits && Math.Abs(d.Y) <= PileMergeUnits)
                    {
                        into = i;
                        break;
                    }
                }

                if (into < 0)
                {
                    if (_pileCount == _piles.Length)
                    {
                        Array.Resize(ref _piles, _piles.Length * 2);
                    }

                    into = _pileCount++;
                    _piles[into] = new GroundPile { Id = _nextPileId++, Item = item, Position = pos };
                }

                ref GroundPile p = ref _piles[into];
                int put = Math.Min(count, PileMaxCount - p.Count);
                p.Count = (ushort)(p.Count + put);
                p.ExpireTick = Math.Max(p.ExpireTick, expire);
                p.PickupTick = Math.Max(p.PickupTick, pickup);
                count -= put;
                if (first == 0)
                {
                    first = p.Id;
                }
            }

            return first;
        }

        // Порядок поиска свободного тайла: сам тайл, 4 стороны, 4 угла, затем кольцо на расстоянии 2 (по часовой от востока).
        private static readonly sbyte[] NearDx = { 0, 1, 0, -1, 0, 1, -1, -1, 1, 2, 2, 2, 1, 0, -1, -2, -2, -2, -2, -2, -1, 0, 1, 2, 2 };
        private static readonly sbyte[] NearDy = { 0, 0, 1, 0, -1, 1, 1, -1, -1, 0, 1, 2, 2, 2, 2, 2, 1, 0, -1, -2, -2, -2, -2, -2, -1 };

        /// <summary>Тайл годен для кучи: проходим и не вода (C-018: вода проходима, но куча в ней не лежит — как в E2-1).</summary>
        private bool PileGroundOk(TileCoord t) =>
            !IsBlocked(_shape, t) && TerrainSlow.FloorQ8(_world.GetFloor(t)) == TerrainSlow.One;

        /// <summary>
        /// Куча не лежит в воде, на обрыве и внутри твёрдого объекта: позиция на непроходимом тайле сдвигается в центр
        /// ближайшего проходимого (порядок — <see cref="NearDx"/>). Вокруг всё занято — остаётся как есть.
        /// </summary>
        private WorldPos FreeGroundNear(WorldPos pos)
        {
            TileCoord tile = Torus.TileOf(_shape, pos);
            if (PileGroundOk(Torus.Wrap(_shape, tile)))
            {
                return pos;
            }

            for (int k = 1; k < NearDx.Length; k++)
            {
                var t = new TileCoord(tile.X + NearDx[k], tile.Y + NearDy[k]);
                if (!_shape.Wraps && ((uint)t.X >= (uint)_shape.Width || (uint)t.Y >= (uint)_shape.Height))
                {
                    continue;
                }

                t = Torus.Wrap(_shape, t);
                if (PileGroundOk(t))
                {
                    WorldPos c = Torus.CornerOf(t);
                    return new WorldPos(c.X + Torus.SubTilesPerTile / 2, c.Y + Torus.SubTilesPerTile / 2);
                }
            }

            return pos;
        }
    }
}
