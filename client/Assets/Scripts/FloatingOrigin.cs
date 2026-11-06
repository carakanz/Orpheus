#nullable enable
using Orpheus.Core.Geometry;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// «Плавающее начало» клиента (docs/07, 3.2, строка «Рендер»): координаты Unity считаются от угла
    /// чанка-якоря рядом с персонажем. Узел чанка стоит в Δ(якорь, чанк), сущность — в Δ(угол якоря, позиция);
    /// любая разность — только через <see cref="Torus.Delta(in MapShape, WorldPos, WorldPos)"/>, поэтому у шва
    /// каждый чанк встаёт в ближайшую копию и числа Unity остаются малыми (≤ нескольких чанков).
    /// <para>
    /// Оси: в ядре Y смотрит вниз (юг = +Y), в Unity — вверх, поэтому Unity.y = −Δy. Тайл ядра (x, y) —
    /// клетка Unity (x, −y − 1) от угла якоря; 1 тайл = 1 единица Unity.
    /// </para>
    /// <para>
    /// Якорь переезжает (<see cref="Rebase"/>) к чанку персонажа, когда тот ушёл от якоря дальше чанка по любой
    /// оси или перешёл шов (якорь и персонаж по разные стороны шва). Сдвиг — целое число чанков; всё, что
    /// стоит в координатах Unity (узлы чанков, персонаж, камера), сдвигается на тот же вектор в том же кадре,
    /// поэтому на экране рывка нет.
    /// </para>
    /// </summary>
    public sealed class FloatingOrigin
    {
        public const float UnitsPerTile = Torus.SubTilesPerTile;

        readonly MapShape _map;

        public FloatingOrigin(MapShape map, ChunkCoord anchor)
        {
            _map = map;
            Anchor = Torus.Wrap(map, anchor);
        }

        /// <summary>Чанк-якорь: его левый верхний угол — (0, 0) в Unity.</summary>
        public ChunkCoord Anchor { get; private set; }

        /// <summary>Сколько раз начало переезжало (для лога smoke).</summary>
        public int Rebases { get; private set; }

        /// <summary>Из них — при переходе шва.</summary>
        public int SeamRebases { get; private set; }

        /// <summary>Угол якоря как позиция существа.</summary>
        public WorldPos AnchorCorner => Torus.CornerOf(Torus.CornerOf(Anchor));

        /// <summary>Позиция ядра → точка Unity (z = 0).</summary>
        public Vector3 ToUnity(WorldPos p)
        {
            Int2 d = Torus.Delta(_map, AnchorCorner, p);
            return new Vector3(d.X / UnitsPerTile, -d.Y / UnitsPerTile, 0f);
        }

        /// <summary>Левый верхний угол чанка → точка Unity.</summary>
        public Vector3 ChunkToUnity(ChunkCoord c)
        {
            Int2 d = Torus.Delta(_map, Anchor, c);
            return new Vector3(d.X * Torus.ChunkSize, -d.Y * Torus.ChunkSize, 0f);
        }

        /// <summary>
        /// Точка Unity → позиция ядра (1/256 тайла с округлением). Нужна проверке «рывка»: восстановить, где
        /// персонаж и камера стоят на экране относительно нарисованных тайлов.
        /// </summary>
        public WorldPos FromUnity(Vector3 v) => FromUnity(AnchorCorner, Vector3.zero, v);

        /// <summary>Точка Unity → позиция ядра относительно узла, стоящего в <paramref name="nodeUnity"/> с углом <paramref name="nodeCorner"/>.</summary>
        public WorldPos FromUnity(WorldPos nodeCorner, Vector3 nodeUnity, Vector3 v) =>
            Torus.Offset(_map, nodeCorner, new Int2(Torus.ToUnits(v.x - nodeUnity.x), Torus.ToUnits(-(v.y - nodeUnity.y))));

        /// <summary>
        /// Нужен ли переезд: персонаж ушёл от якоря дальше чанка или якорь по другую сторону шва
        /// (прямая разность номеров чанков не совпала с Δ по тору).
        /// </summary>
        public bool NeedsRebase(ChunkCoord characterChunk, out bool seam)
        {
            Int2 d = Torus.Delta(_map, Anchor, characterChunk);
            seam = SeamBetween(Anchor.X, characterChunk.X, d.X) || SeamBetween(Anchor.Y, characterChunk.Y, d.Y);
            return seam || d.X > 1 || d.X < -1 || d.Y > 1 || d.Y < -1;
        }

        // Шов между a и b: кратчайшая дельта по тору не равна прямой разности номеров (b − a).
        // Номера чанков — индексы массива [0, ChunksX), не позиции: вычитание здесь — про индексы, а не про координаты.
        static bool SeamBetween(int a, int b, int torusDelta) => a + torusDelta != b;

        /// <summary>
        /// Переезд якоря в <paramref name="newAnchor"/>. Возвращает сдвиг Unity, который надо прибавить ко всему,
        /// что стоит в координатах Unity (камера; узлы чанков и персонаж пересчитываются от нового якоря сами).
        /// </summary>
        public Vector3 Rebase(ChunkCoord newAnchor, bool seam)
        {
            newAnchor = Torus.Wrap(_map, newAnchor);
            Vector3 shift = -ChunkToUnity(newAnchor);
            Anchor = newAnchor;
            Rebases++;
            if (seam)
            {
                SeamRebases++;
            }

            return shift;
        }
    }
}
