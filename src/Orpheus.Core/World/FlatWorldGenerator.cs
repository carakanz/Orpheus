using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Плоский мир до генератора 0-007: все чанки созданы, пол — трава, объектов нет.
    /// Версия 0 — «генератора ещё нет» (как <c>WorldGenModule.GeneratorVersion</c>).
    /// Зерно в мир пишется, но на содержимое не влияет.
    /// </summary>
    public sealed class FlatWorldGenerator : IWorldGenerator
    {
        /// <summary>Единственный экземпляр (состояния нет).</summary>
        public static readonly FlatWorldGenerator Instance = new FlatWorldGenerator();

        /// <inheritdoc/>
        public int Version => 0;

        /// <inheritdoc/>
        public WorldModel Generate(MapShape shape, ulong seed)
        {
            var world = new WorldModel(shape, seed, Version);
            world.CreateAllChunks();
            ReadOnlySpan<ChunkCoord> keys = world.GetChunkCoords();
            for (int i = 0; i < keys.Length; i++)
            {
                Chunk chunk = world.FindChunk(keys[i])!;
                for (int index = 0; index < Chunk.TileCount; index++)
                {
                    chunk.SetBaseTile(index, FloorId.Grass, ObjectId.Empty);
                }
            }

            return world;
        }
    }
}
