using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Мини-карта (C-008, слово Автора 15.11: «А справа вверху мини-карту», как на снимке Diablo IV): картинка квадрата
    /// мира вокруг персонажа из чанков, которые уже есть у клиента. Цвет тайла — таблица данными: объект (дерево, камень,
    /// гряда, стена, мебель) поверх пола (трава, песок, вода…); несозданный чанк — тёмный полупрозрачный. Север вверху,
    /// через шов мира — по тору (<see cref="Torus"/>). Чистый C# без Unity: клиент кладёт пиксели в текстуру, тесты
    /// <c>C008MiniMapTests</c> проверяют цвета и шов. Тумана войны и большой карты пока нет (`docs/01` раздел 7).
    /// </summary>
    public static class MiniMap
    {
        /// <summary>Цвет как в памяти Unity <c>Color32</c>: r | g ≪ 8 | b ≪ 16 | a ≪ 24.</summary>
        public static uint Rgba(byte r, byte g, byte b, byte a = 255) => (uint)(r | (g << 8) | (b << 16) | (a << 24));

        /// <summary>Несозданный чанк (мир ещё не пришёл) — тёмный полупрозрачный.</summary>
        public static readonly uint Unknown = Rgba(14, 14, 24, 150);

        /// <summary>Постройка без объекта в тайле (тонкая стена, E2-12) и стены / забор / дверь — светлые.</summary>
        public static readonly uint Building = Rgba(244, 226, 178);

        /// <summary>Мебель и станки — золотые.</summary>
        public static readonly uint Furniture = Rgba(255, 196, 72);

        /// <summary>Цвета полов по <see cref="FloorId"/> (0…15).</summary>
        public static readonly uint[] FloorColors = BuildFloors();

        /// <summary>Цвета объектов по <see cref="ObjectId"/> (0 — нет цвета: виден пол).</summary>
        public static readonly uint[] ObjectColors = BuildObjects();

        private static uint[] BuildFloors()
        {
            var c = new uint[16];
            c[FloorId.Empty] = Unknown;
            c[FloorId.Grass] = Rgba(100, 168, 64);
            c[FloorId.Dirt] = Rgba(146, 106, 64);
            c[FloorId.Stone] = Rgba(132, 132, 140);
            c[FloorId.Sand] = Rgba(226, 204, 132);
            c[FloorId.Snow] = Rgba(236, 242, 252);
            c[FloorId.Water] = Rgba(52, 122, 214);
            c[FloorId.Lava] = Rgba(236, 92, 30);
            c[FloorId.WoodFloor] = Rgba(186, 136, 80);
            c[FloorId.StoneFloor] = Rgba(178, 178, 188);
            c[FloorId.Platform] = Rgba(156, 114, 68);
            c[FloorId.ForestFloor] = Rgba(66, 116, 52);
            c[FloorId.DryGrass] = Rgba(176, 172, 92);
            for (int i = FloorId.MaxKnown + 1; i < c.Length; i++)
            {
                c[i] = Unknown;
            }

            return c;
        }

        private static uint[] BuildObjects()
        {
            var c = new uint[ObjectId.MaxKnownId + 1];
            c[ObjectId.Tree] = Rgba(34, 86, 40);
            c[ObjectId.Bush] = Rgba(58, 134, 60);
            c[ObjectId.Stump] = Rgba(110, 80, 50);
            c[ObjectId.Rock] = Rgba(112, 112, 120);
            c[ObjectId.Granite] = Rgba(96, 92, 104);
            c[ObjectId.Ore] = Rgba(206, 124, 60);
            c[ObjectId.Cliff] = Rgba(72, 64, 60);
            c[ObjectId.WoodWall] = Building;
            c[ObjectId.StoneWall] = Building;
            c[ObjectId.WoodFence] = Building;
            c[ObjectId.Door] = Building;
            c[ObjectId.Gate] = Building;
            foreach (ushort f in new[] { ObjectId.Torch, ObjectId.Chest, ObjectId.Bed, ObjectId.Workbench, ObjectId.Table, ObjectId.Forge, ObjectId.Campfire })
            {
                c[f] = Furniture;
            }

            return c;
        }

        /// <summary>Цвет тайла: объект поверх пола, постройка без объекта — светлая; вне карты без шва — как несозданный.</summary>
        public static uint ColorOf(WorldModel world, TileCoord tile)
        {
            MapShape shape = world.Shape;
            if (!shape.Wraps && ((uint)tile.X >= (uint)shape.Width || (uint)tile.Y >= (uint)shape.Height))
            {
                return Unknown;
            }

            if (world.FindChunk(Torus.ChunkOf(shape, Torus.Wrap(shape, tile))) == null)
            {
                return Unknown;
            }

            ushort raw = world.GetObject(tile);
            if (ObjectId.IsPart(raw))
            {
                raw = world.GetObject(world.AnchorOf(tile));
            }

            ushort id = ObjectId.IdOf(raw);
            if (id != ObjectId.Empty && id < ObjectColors.Length && ObjectColors[id] != 0)
            {
                return ObjectColors[id];
            }

            if (world.TryGetBuild(tile, out _))
            {
                return Building;
            }

            byte floor = world.GetFloor(tile);
            return FloorColors[floor & 15];
        }

        /// <summary>
        /// Нарисовать квадрат <paramref name="side"/> × <paramref name="side"/> пикселей вокруг тайла <paramref name="center"/>
        /// радиусом <paramref name="radius"/> тайлов: пиксель (столбец, строка снизу) → тайл по тору. Строка 0 — юг
        /// (больший Y мира), верх картинки — север.
        /// </summary>
        public static void Paint(WorldModel world, TileCoord center, int radius, int side, uint[] pixels)
        {
            if (pixels.Length < side * side)
            {
                throw new ArgumentException("мало пикселей", nameof(pixels));
            }

            for (int row = 0; row < side; row++)
            {
                int dy = TileOffset(side - 1 - row, radius, side);
                for (int col = 0; col < side; col++)
                {
                    int dx = TileOffset(col, radius, side);
                    pixels[row * side + col] = ColorOf(world, new TileCoord(center.X + dx, center.Y + dy));
                }
            }
        }

        /// <summary>Смещение тайла от центра для пикселя <paramref name="i"/> (0…side−1) при радиусе <paramref name="radius"/>.</summary>
        public static int TileOffset(int i, int radius, int side) => (int)Math.Floor((i + 0.5) * 2.0 * radius / side) - radius;

        /// <summary>
        /// Точка на карте для сущности: смещение от персонажа по тору в долях радиуса (−1…1 по осям, ось Y — на север
        /// вверх); дальше радиуса — false (точку не рисовать).
        /// </summary>
        public static bool Marker(in MapShape shape, WorldPos self, WorldPos other, int radius, out float x, out float y)
        {
            Int2 d = Torus.Delta(shape, self, other);
            float r = radius * (float)Torus.SubTilesPerTile;
            x = d.X / r;
            y = -d.Y / r;
            return x >= -1f && x <= 1f && y >= -1f && y <= 1f;
        }
    }
}
