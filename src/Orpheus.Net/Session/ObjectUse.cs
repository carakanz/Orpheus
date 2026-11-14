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

        /// <summary>Верстак — окно крафта на вкладке «Верстак» (станок для рецептов ищет сервер).</summary>
        Workbench = 2,

        /// <summary>Костёр — окно крафта на вкладке «Костёр».</summary>
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
                case ObjectId.Workbench: return UseKind.Workbench;
                case ObjectId.Campfire: return UseKind.Campfire;
                case ObjectId.Chest: return UseKind.Chest;
                case ObjectId.Bed: return UseKind.Bed;
                default: return UseKind.None;
            }
        }

        /// <summary>Объект на тайле годится для F отсюда: есть действие, досягаем; дверь — только открытая и не под собой.</summary>
        public static UseKind At(WorldModel world, WorldPos self, TileCoord tile)
        {
            MapShape map = world.Shape;
            TileCoord t = Torus.Wrap(map, tile);
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

            return EditRules.InUseRadius(map, self, t) ? kind : UseKind.None;
        }

        /// <summary>
        /// Что сделает F: объект под курсором (<paramref name="cursor"/>), иначе ближайший к персонажу (по тору; равные —
        /// по порядку обхода: строки сверху вниз, слева направо). Нет — false.
        /// </summary>
        public static bool Find(WorldModel world, WorldPos self, TileCoord? cursor, out TileCoord tile, out UseKind kind)
        {
            if (cursor.HasValue && (kind = At(world, self, cursor.Value)) != UseKind.None)
            {
                tile = Torus.Wrap(world.Shape, cursor.Value);
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
                    UseKind k = At(world, self, t);
                    if (k == UseKind.None)
                    {
                        continue;
                    }

                    long d2 = Torus.Dist2(map, self, EditRules.CenterOf(t));
                    if (d2 < best)
                    {
                        best = d2;
                        tile = t;
                        kind = k;
                    }
                }
            }

            return kind != UseKind.None;
        }
    }
}
