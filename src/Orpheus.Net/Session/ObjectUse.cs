using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Что делает F с объектом (E2-5).</summary>
    public enum UseKind : byte
    {
        None = 0,

        /// <summary>Открытая дверь — закрыть (1-012; сервер проверяет радиус 1, замок, проём).</summary>
        Door = 1,

        /// <summary>Верстак — окно крафта (E2-5). C-013: не выдаётся (<see cref="ObjectUse.KindOf"/>) — «F — верстак не нужно».</summary>
        Workbench = 2,

        /// <summary>Костёр — окно крафта (E2-5). C-013: не выдаётся, как верстак.</summary>
        Campfire = 3,

        /// <summary>Сундук — открыть (заявка 33, содержимое — 35).</summary>
        Chest = 4,

        /// <summary>Кровать — точка возрождения (заявка 33).</summary>
        Bed = 5,
    }

    /// <summary>
    /// Общее правило клавиши F (E2-5): объект под курсором, если он с действием и в досягаемости, иначе ближайший такой
    /// объект вокруг персонажа. Досягаемость — функции ядра: дверь — соседний тайл (как у <c>RequestCloseDoor</c>), прочее —
    /// <see cref="EditRules.InUseRadius"/> (3 тайла). Решает всё равно сервер; здесь только «что нажали».
    /// </summary>
    public static class ObjectUse
    {
        /// <summary>Действие объекта по id (таблица, не ветки по коду вызывающих).</summary>
        public static UseKind KindOf(ushort raw)
        {
            switch (ObjectId.IdOf(raw))
            {
                case ObjectId.Door: return UseKind.Door;
                // C-013 (слово Автора 15.11: «F — верстак не нужно»): у станков (верстак, костёр) действия F нет — их рецепты
                // и так доступны в окне изготовления, когда станок рядом (CraftRules.StationCheck, 3 тайла); F по ним не
                // открывает окно, подсказки «F — верстак» нет, F берёт ближайший другой объект с действием.
                case ObjectId.Chest: return UseKind.Chest;
                case ObjectId.Bed: return UseKind.Bed;
                default: return UseKind.None;
            }
        }

        /// <summary>Объект на тайле годится для F отсюда: есть действие, досягаем; дверь — только открытая и не под собой.</summary>
        public static UseKind At(WorldModel world, WorldPos self, TileCoord tile) => At(world, self, tile, out _);

        /// <summary>
        /// Как <see cref="At(WorldModel, WorldPos, TileCoord)"/>; C-011: тайл с хвостом мебели (кровать 1 × 2, верстак 2 × 2,
        /// сундук со сдвигом) — действие её якоря (<see cref="BuildLayers.TryFurnitureAt"/>), досягаемость — до любого её тайла
        /// (<see cref="BuildLayers.InUseReach"/>, как у сервера). <paramref name="target"/> — тайл для заявки (якорь).
        /// </summary>
        public static UseKind At(WorldModel world, WorldPos self, TileCoord tile, out TileCoord target)
        {
            MapShape map = world.Shape;
            TileCoord t = Torus.Wrap(map, tile);
            if (KindOf(world.GetObject(t)) == UseKind.None && BuildLayers.TryFurnitureAt(world, t, out TileCoord anchor))
            {
                t = anchor;
            }

            target = t;
            UseKind kind = KindOf(world.GetObject(t));
            if (kind == UseKind.None)
            {
                return UseKind.None;
            }

            if (kind == UseKind.Door)
            {
                Int2 d = Torus.Delta(map, Torus.TileOf(map, self), t);
                bool near = Math.Max(Math.Abs(d.X), Math.Abs(d.Y)) == 1;
                return near && world.TryGetBuild(t, out BuildRecord rec) && rec.IsDoorOpen ? UseKind.Door : UseKind.None;
            }

            return BuildLayers.InUseReach(world, self, t) ? kind : UseKind.None;
        }

        /// <summary>
        /// Что сделает F: объект под курсором (<paramref name="cursor"/>), иначе ближайший к персонажу (по тору; равные —
        /// по порядку обхода: строки сверху вниз, слева направо). Нет — false.
        /// </summary>
        public static bool Find(WorldModel world, WorldPos self, TileCoord? cursor, out TileCoord tile, out UseKind kind)
        {
            if (cursor.HasValue && (kind = At(world, self, cursor.Value, out tile)) != UseKind.None)
            {
                return true;
            }

            MapShape map = world.Shape;
            TileCoord me = Torus.TileOf(map, self);
            const int r = EditRules.UseRadiusTiles;
            long best = long.MaxValue;
            tile = default;
            kind = UseKind.None;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    var t = Torus.Wrap(map, new TileCoord(me.X + dx, me.Y + dy));
                    UseKind k = At(world, self, t, out TileCoord target);
                    if (k == UseKind.None)
                    {
                        continue;
                    }

                    long d2 = Torus.Dist2(map, self, EditRules.CenterOf(t)); // до тайла (хвост ближе якоря — ближе и предмет)
                    if (d2 < best)
                    {
                        best = d2;
                        tile = target;
                        kind = k;
                    }
                }
            }

            return kind != UseKind.None;
        }
    }
}
