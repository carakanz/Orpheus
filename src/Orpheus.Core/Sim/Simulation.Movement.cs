using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Trig;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// P-002: partial Simulation - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Целочисленный √ (метод по разрядам; детерминирован, без float).</summary>
        private static int ISqrt(long n)
        {
            long res = 0;
            long bit = 1L << 62;
            while (bit > n)
            {
                bit >>= 2;
            }

            while (bit != 0)
            {
                if (n >= res + bit)
                {
                    n -= res + bit;
                    res = (res >> 1) + bit;
                }
                else
                {
                    res >>= 1;
                }

                bit >>= 2;
            }

            return (int)res;
        }

        /// <summary>
        /// Выталкивание (1-007, 8.5 «толкаются»): пары персонаж↔враг-с-мозгом и враг↔враг-с-мозгом, центры
        /// ближе суммы радиусов (204) — разъезжаются по 2 тайла/с (68 единиц/такт на тело) вдоль оси.
        /// Заглушки (0-028) и персонаж↔персонаж не трогает. Порядок по номерам, целочисленно — детерминизм (11.5).
        /// </summary>
        private void StepSeparation()
        {
            const int MinDist = 2 * Collision.RadiusUnits;
            const int PushUnitsPerTick = 68; // 2 тайла/с = 2048 ед/с / 30 = 68,3 — берём 68
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity a = ref _entities[i];
                if (!a.Alive || a.Brain.Archetype == EnemyArchetype.None)
                {
                    continue; // инициатор — только враг с мозгом; персонаж↔персонаж (0.x — сквозные) не трогаем
                }

                for (int j = 0; j < _entityCount; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    ref SimEntity b = ref _entities[j];
                    if (!b.Alive || (!b.IsCharacter && b.Brain.Archetype == EnemyArchetype.None))
                    {
                        continue;
                    }

                    Int2 d = Torus.Delta(_shape, a.Mover.Position, b.Mover.Position);
                    long d2 = d.LengthSquared;
                    if (d2 >= (long)MinDist * MinDist)
                    {
                        continue;
                    }

                    if (d2 == 0)
                    {
                        d = new Int2(MinDist - 1, 0); // позиции совпали — ось по X (детерминированно)
                        d2 = (long)(MinDist - 1) * (MinDist - 1);
                    }

                    int dist = ISqrt(d2);
                    int px = d.X * PushUnitsPerTick / dist;
                    int py = d.Y * PushUnitsPerTick / dist;
                    a.Mover.Position = Torus.Wrap(_shape, new WorldPos(a.Mover.Position.X - px, a.Mover.Position.Y - py));
                    b.Mover.Position = Torus.Wrap(_shape, new WorldPos(b.Mover.Position.X + px, b.Mover.Position.Y + py));
                }
            }
        }

        /// <summary>Идти к центру тайла (ввод как у персонажа); пришли (ближе 3/4 тайла) — ввод пустой, true.</summary>
        private bool MoveToward(ref SimEntity e, TileCoord tile)
        {
            Int2 d = Torus.Delta(_shape, e.Mover.Position, EditRules.CenterOf(Torus.Wrap(_shape, tile)));
            if (d.LengthSquared <= MeleeRogue.NodePass2)
            {
                e.Input = InputFrame.None;
                return true;
            }

            e.Input = new InputFrame(Input8.FromStick(d.X, d.Y));
            return false;
        }

        /// <summary>Новый планировщик путей (очередь по слотам; без часов — поиск ровно PathMaxPerTick за такт).</summary>
        private PathScheduler NewPathScheduler() =>
            new PathScheduler(_pathFinder, _pathTerrain, _pathBuf, PathQueueCapacity, PathMaxPerTick);

        /// <summary>
        /// Сброс кэша путей (1-007): слоты сдвинулись (<see cref="RemoveEntity"/>) или состояние заменили
        /// (<see cref="Restore"/>) — владельцы запросов протухли; буферы очищаются, пути переспрашиваются.
        /// </summary>
        private void ResetPathCaches()
        {
            for (int i = 0; i < _entities.Length; i++)
            {
                _pathBuf[i].Clear();
                _entities[i].Brain.PathStamp = 0;
                _entities[i].Brain.PathPos = 0;
            }

            _paths = NewPathScheduler();
        }

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
    }
}
