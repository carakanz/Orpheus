using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Core.Pathing
{
    /// <summary>
    /// Карта поиска пути по миру симуляции (только чтение). Пеший — то же правило, что в такте:
    /// <see cref="Simulation.IsBlocked"/> (пол и объекты, `05` 1.3). Летун (9.2) — непроходимы только постройки
    /// игрока и стены данжа: объект твёрдый и не природный (дверь — по флагу записи, как в такте); пол не мешает.
    /// Замедляющих полов в таблице <see cref="FloorId"/> пока нет — множитель всегда <see cref="PathCost.Normal"/>.
    /// <para>
    /// Быстрый путь пешего: чанк берётся из кэша на 4 чанка (сброс в <see cref="BeginSearch"/>), пол и пустой объект
    /// читаются из чанка тем же кодом, что в <see cref="Simulation.IsBlocked"/>; тайл с объектом — сам
    /// <see cref="Simulation.IsBlocked"/> (части, записи построек, двери). Совпадение на всей карте — тест
    /// <c>D02_S9_2_WalkerUsesSimulationIsBlocked_FlyerOnlyBuildings</c>.
    /// </para>
    /// </summary>
    public sealed class SimulationPathTerrain : IPathTerrain
    {
        private const int CacheSize = 4;

        private readonly Simulation _sim;
        private readonly ChunkCoord[] _cacheCoord = new ChunkCoord[CacheSize];
        private readonly Chunk?[] _cacheChunk = new Chunk?[CacheSize];
        private readonly bool[] _cacheValid = new bool[CacheSize];

        public SimulationPathTerrain(Simulation sim)
        {
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
        }

        /// <inheritdoc />
        public MapShape Shape => _sim.Shape;

        /// <inheritdoc />
        public void BeginSearch()
        {
            for (int i = 0; i < CacheSize; i++)
            {
                _cacheValid[i] = false;
                _cacheChunk[i] = null;
            }
        }

        /// <inheritdoc />
        public byte CostOf(TileCoord tile, PathMode mode)
        {
            bool blocked = mode == PathMode.Flyer ? FlyerBlocked(tile) : WalkerBlocked(tile);
            return blocked ? PathCost.Blocked : PathCost.Normal;
        }

        private bool WalkerBlocked(TileCoord tile)
        {
            MapShape map = _sim.Shape;
            Chunk? chunk = FindChunk(Torus.ChunkOf(map, tile));
            if (chunk == null)
            {
                return false;
            }

            int index = Chunk.IndexOf(tile.X, tile.Y);
            if (!FloorId.IsPassable(chunk.GetFloor(index)))
            {
                return true;
            }

            if (chunk.GetObject(index) == ObjectId.Empty)
            {
                return false;
            }

            return _sim.IsBlocked(map, tile);
        }

        private Chunk? FindChunk(ChunkCoord coord)
        {
            int slot = (coord.X & 1) | ((coord.Y & 1) << 1);
            if (_cacheValid[slot] && _cacheCoord[slot] == coord)
            {
                return _cacheChunk[slot];
            }

            Chunk? chunk = _sim.World.FindChunk(coord);
            _cacheCoord[slot] = coord;
            _cacheChunk[slot] = chunk;
            _cacheValid[slot] = true;
            return chunk;
        }

        private bool FlyerBlocked(TileCoord tile)
        {
            WorldModel world = _sim.World;
            ushort obj = world.GetObject(tile);
            if (obj == ObjectId.Empty)
            {
                return false;
            }

            BuildRecord record;
            if (ObjectId.IsPart(obj))
            {
                TileCoord anchor = world.AnchorOf(tile);
                obj = world.GetObject(anchor);
                if (!world.TryGetBuild(anchor, out record))
                {
                    record = default;
                }
            }
            else if (!world.TryGetBuild(tile, out record))
            {
                record = default;
            }

            return !ObjectId.IsNatural(obj) && !ObjectId.IsPassable(obj, record.Flags);
        }
    }
}
