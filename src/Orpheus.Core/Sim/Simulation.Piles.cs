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

        /// <summary>Радиус подбора: 1,5 тайла = 384 unit (<c>06</c> 5.3 «касание и притяжение с 1,5 тайла»).</summary>
        public const int PickupRadiusUnits = 384;

        /// <summary>Задержка подбора добычи копания и возврата при сносе: 20 тактов (≈ 0,67 с) — добыча успевает «упасть» (допущение E2-1).</summary>
        public const int LootPickupDelayTicks = 20;

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

        private struct BagRequest
        {
            public int EntityId;
            public BagOp Op;
            public byte From;
            public byte To;
            public ushort Count;
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
                if (piles[i].Id <= maxId || !ItemTable.Exists(piles[i].Item) || piles[i].Count == 0)
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
        }

        /// <summary>
        /// Заявка игрока на операцию с сумкой (E2-1; клиент просит, сервер решает): исполнится в ближайшем такте
        /// по порядку прихода. Неверная заявка молча не исполняется (сумка не меняется). Очередь полна — false.
        /// </summary>
        public bool RequestBagOp(int entityId, BagOp op, int from, int to, int count)
        {
            if (_bagOpCount == _bagOps.Length)
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
            if (_pileCount == 0)
            {
                return;
            }

            // Истечение и подбор одним проходом; список сжимается на месте, порядок по номеру сохраняется.
            int kept = 0;
            for (int i = 0; i < _pileCount; i++)
            {
                GroundPile p = _piles[i];
                if (p.ExpireTick <= tick)
                {
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
            }

            _pileCount = kept;
        }

        /// <summary>
        /// Ближайший живой персонаж в радиусе подбора, у которого есть место под предмет; равная дальность — меньший
        /// номер сущности (первый в списке). Нет такого — −1.
        /// </summary>
        private int NearestPicker(WorldPos pos, byte item)
        {
            int best = -1;
            long bestD2 = (long)PickupRadiusUnits * PickupRadiusUnits + 1;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter || !_entities[i].Alive)
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

        private int SpawnPile(long tick, byte item, int count, WorldPos position, int pickupDelayTicks)
        {
            if (count <= 0 || !ItemTable.Exists(item))
            {
                return 0;
            }

            WorldPos pos = FreeGroundNear(Torus.Wrap(_shape, position));
            long expire = tick + PileLifetimeTicks;
            long pickup = tick + Math.Max(0, pickupDelayTicks);
            int first = 0;
            while (count > 0)
            {
                // Слияние: первая по номеру одинаковая куча в пределах тайла, пока в ней есть место.
                int into = -1;
                for (int i = 0; i < _pileCount; i++)
                {
                    if (_piles[i].Item != item || _piles[i].Count >= PileMaxCount)
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

        /// <summary>
        /// Куча не лежит в воде, на обрыве и внутри твёрдого объекта: позиция на непроходимом тайле сдвигается в центр
        /// ближайшего проходимого (порядок — <see cref="NearDx"/>). Вокруг всё занято — остаётся как есть.
        /// </summary>
        private WorldPos FreeGroundNear(WorldPos pos)
        {
            TileCoord tile = Torus.TileOf(_shape, pos);
            if (!IsBlocked(_shape, tile))
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
                if (!IsBlocked(_shape, t))
                {
                    WorldPos c = Torus.CornerOf(t);
                    return new WorldPos(c.X + Torus.SubTilesPerTile / 2, c.Y + Torus.SubTilesPerTile / 2);
                }
            }

            return pos;
        }
    }
}
