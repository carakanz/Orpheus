using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Генератор «v1» сервера (ключ <c>--gen v1</c>, карточка 1-017; docs/01, 2): мир-тор W × W из генератора v2 вместо
    /// заглушки. Этапы по порядку `01` 2:
    /// <list type="number">
    /// <item>1–3 — макрокарта (<see cref="MacroGenerator"/>, набор «срез»: луга, лес, горы, пещеры);</item>
    /// <item>7 — Очаг на макрокарте: луга Ø 300, лес в 200 (<see cref="HearthStage.RunMacro"/>);</item>
    /// <item>11 — тайлы: пол, скальная масса, объекты, граница с деталью (<see cref="TileStage"/>), поверх — штамп меди в 600
    /// (<see cref="HearthStage.StampCopper"/>);</item>
    /// <item>12 — связность от Очага (<see cref="ConnectStage"/>);</item>
    /// <item>запись в чанки как содержимое генерации (ревизия 0).</item>
    /// </list>
    /// Этапы 4–6, 8–10, 13 (полисы, вода, темп, данжи, форпосты, интересное, проверка гарантий) — не в вехе 1.
    /// Версия мира — <see cref="GeneratorVersion"/> (общая с макрокартой); номер не пересекается с заглушкой (1).
    /// </summary>
    public sealed class GeneratorV1 : IWorldGenerator
    {
        /// <summary>
        /// Версия генератора — пишется в снимок генерации (docs/07, 3.4). 4 — мир 1-017 (общая с макрокартой); 5 — E1-2:
        /// вода, берег, обрывы, тропы, лес куртинами, сухая трава, без скальной массы гор. 6 — E1-6b: обрывы — плавные гряды
        /// толщиной 2–3 тайла вокруг гор вместо линий уровней в один тайл. Макрокарта (этапы 1–3, 7) при
        /// этом не менялась, её версия осталась <see cref="MacroGenerator.GeneratorVersion"/>.
        /// </summary>
        public const int GeneratorVersion = 6;

        /// <summary>Ключ сервера <c>--gen</c>.</summary>
        public const string Key = "v1";

        /// <summary>Стартовая зона 32 × 32 у Очага — трава без объектов (как у заглушки: там появляются игроки и боты).</summary>
        public const int StartZoneSize = 32;

        /// <inheritdoc />
        public int Version => GeneratorVersion;

        /// <inheritdoc />
        public WorldModel Generate(MapShape shape, ulong seed) => ToModel(BuildTiles(shape, seed));

        /// <summary>Этапы 1–12 на тайлах (без записи в чанки): для тестов, картинок и замеров.</summary>
        public static TileWorld BuildTiles(MapShape shape, ulong seed)
        {
            if (!shape.Wraps || shape.Width != shape.Height)
            {
                throw new ArgumentException("Генератор v1 строит только квадратный мир-тор.", nameof(shape));
            }

            return BuildTiles(MacroGenerator.Generate(shape.Width, seed, BiomeSet.Slice));
        }

        /// <summary>Этапы 7, 11, 12 по готовой макрокарте этапов 1–3 (тест может подменить биомы до вызова).</summary>
        public static TileWorld BuildTiles(MacroWorld macro)
        {
            var t = new TileWorld(macro);
            t.ForestStamped = HearthStage.RunMacro(macro);
            TileStage.Run(t);
            HearthStage.StampCopper(t);
            ConnectStage.Run(t);
            CliffStage.DropStubs(t);
            return t;
        }

        /// <summary>Мир в памяти из тайлов: все чанки, содержимое генерации.</summary>
        public static WorldModel ToModel(TileWorld t)
        {
            int w = t.WorldSize;
            var shape = MapShape.World(w);
            var model = new WorldModel(shape, t.Macro.Map.Seed, GeneratorVersion);
            model.CreateAllChunks();
            int size = Torus.ChunkSize;
            for (int cy = 0; cy < shape.ChunksY; cy++)
            {
                for (int cx = 0; cx < shape.ChunksX; cx++)
                {
                    Chunk chunk = model.FindChunk(new ChunkCoord(cx, cy))!;
                    for (int ly = 0; ly < size; ly++)
                    {
                        int row = (((cy << Torus.ChunkBits) + ly) * w) + (cx << Torus.ChunkBits);
                        for (int lx = 0; lx < size; lx++)
                        {
                            chunk.SetBaseTile(Chunk.IndexOf(lx, ly), t.Floor[row + lx], t.Objects[row + lx]);
                        }
                    }
                }
            }

            return model;
        }
    }
}
