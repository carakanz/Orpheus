using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// E2-1: бот подбирает добычу с земли. Добыча копания падает кучей на тайл объекта и первые 10 тактов не
    /// подбирается, а подбор — только в 1,5 тайла: бот, который сразу идёт дальше, оставляет добычу лежать.
    /// Помощник выбирает ближайшую известную клиенту кучу (<see cref="NetClient.Piles"/>), под которую в сумке есть
    /// место, и ведёт к ней; вплотную — стоит и ждёт подбора. Кучу, до которой не дошёл за отведённые кадры
    /// (за стеной, за водой), бот бросает и больше к ней не идёт.
    /// </summary>
    public sealed class BotPiles
    {
        /// <summary>Дальше этого от бота кучи не интересуют, unit (4 тайла).</summary>
        public const int SeekUnits = 4 * Torus.SubTilesPerTile;

        /// <summary>Ближе этого бот стоит и ждёт подбора, unit (0,75 тайла — внутри радиуса подбора 1,5).</summary>
        public const int StandUnits = 3 * Torus.SubTilesPerTile / 4;

        /// <summary>
        /// Ход по линии (бот <c>dig_line</c> не сходит со своей строки): куча годится, если лежит не дальше этого от линии,
        /// unit (1,2 тайла: соседняя строка тайлов + разброс; с линии до неё — меньше радиуса подбора 1,5).
        /// </summary>
        public const int OffLineUnits = 307;

        /// <summary>Ход по линии: вдоль линии бот подходит к куче на это расстояние и ждёт, unit (0,375 тайла).</summary>
        public const int StandAlongUnits = 96;

        /// <summary>Свободный ход: к куче идут напрямую.</summary>
        public const int AxisFree = 0;

        /// <summary>Ход только вдоль X: линия — горизонталь y = <c>line</c>.</summary>
        public const int AxisX = 1;

        /// <summary>Ход только вдоль Y: линия — вертикаль x = <c>line</c>.</summary>
        public const int AxisY = 2;

        /// <summary>Кадров на одну кучу (идти и ждать); вышли — куча брошена.</summary>
        public const int ChaseFrames = 150;

        private readonly MapShape _shape;
        private readonly HashSet<int> _abandoned = new HashSet<int>();
        private int _pileId;
        private int _frames;

        public BotPiles(MapShape shape)
        {
            _shape = shape;
        }

        /// <summary>Куч подобрано, пока бот к ним шёл или ждал.</summary>
        public int Collected { get; private set; }

        /// <summary>Куча захвачена как цель (диагностика 20.11: видит ли клиент кучи вовсе).</summary>
        public int Latched { get; private set; }

        /// <summary>Куч брошено по времени.</summary>
        public int Abandoned => _abandoned.Count;

        /// <summary>
        /// Есть куча, за которой стоит идти: <paramref name="dir"/> — куда шагнуть (<see cref="Input8.None"/> — стоять и ждать).
        /// <paramref name="wants"/> — фильтр по предмету (null — любые). Нет кучи — false.
        /// <paramref name="axis"/> — <see cref="AxisFree"/> или ход только вдоль линии (<see cref="AxisX"/> / <see cref="AxisY"/>,
        /// <paramref name="line"/> — её координата, unit): кучи дальше <see cref="OffLineUnits"/> от линии не интересуют,
        /// поперёк линии бот не шагает.
        /// </summary>
        /// <param name="zone">Полоса копания (как MechTourBot._digZone): <see cref="InDigBand"/>;
        /// кучи из чужой полосы не берём (dropped чужим ботом — его добыча). −1 — без фильтра.</param>
        /// <summary>Полоса копания: полосы по 4 тайла ((x &gt;&gt; 3) &amp; 1). Границы полос далёки от
        /// целевых тайлов (InDigZone) — боты не толкаются у стыка (in-proc соак 20.11: noKnock 76@4005).</summary>
        public static bool InDigBand(int tileX, int zone) => ((tileX >> 3) & 1) == zone;

        /// <summary>Целевые тайлы копания — середина полосы (x%8 = 1..2): до границы полосы ≥ 2 тайла,
        /// до середины соседней полосы ≥ 3 тайла — standing-места ботов не соприкасаются.</summary>
        public static bool InDigZone(int tileX, int zone) => InDigBand(tileX, zone) && ((tileX & 3) == 1 || (tileX & 3) == 2);

        public bool TrySeek(NetClient client, WorldPos pos, Func<byte, bool>? wants, out Input8 dir, int axis = AxisFree, int line = 0, int zone = -1)
        {
            dir = Input8.None;
            IReadOnlyList<NetPile> piles = client.Piles;
            int best = -1;
            long bestD2 = (long)SeekUnits * SeekUnits + 1;
            bool chasedAlive = false;
            for (int i = 0; i < piles.Count; i++)
            {
                NetPile p = piles[i];
                chasedAlive |= p.Id == _pileId;
                if (_abandoned.Contains(p.Id) || (wants != null && !wants(p.Item)) || client.Inv.RoomFor(p.Item) <= 0)
                {
                    continue;
                }

                if (zone >= 0 && !InDigBand(p.Position.X / Torus.SubTilesPerTile, zone))
                {
                    continue; // куча в полосе напарника — он её и собирает
                }

                if (axis != AxisFree)
                {
                    int off = axis == AxisX ? Torus.DeltaY(_shape, line, p.Position.Y) : Torus.DeltaX(_shape, line, p.Position.X);
                    if (Math.Abs(off) > OffLineUnits)
                    {
                        continue; // с линии не достать — не наша
                    }
                }

                long d2 = Torus.Dist2(_shape, pos, p.Position);
                if (d2 < bestD2)
                {
                    best = i;
                    bestD2 = d2;
                }
            }

            if (_pileId != 0 && !chasedAlive)
            {
                Collected++; // куча, к которой шли, исчезла — подобрана
                _pileId = 0;
            }

            if (best < 0)
            {
                _pileId = 0;
                return false;
            }

            NetPile target = piles[best];
            if (target.Id != _pileId)
            {
                _pileId = target.Id;
                _frames = 0;
                Latched++;
            }

            if (++_frames > ChaseFrames)
            {
                _abandoned.Add(target.Id);
                _pileId = 0;
                return false;
            }

            int dx = Torus.DeltaX(_shape, pos.X, target.Position.X);
            int dy = Torus.DeltaY(_shape, pos.Y, target.Position.Y);
            if (axis == AxisX)
            {
                dir = new Input8(Math.Abs(dx) > StandAlongUnits ? Math.Sign(dx) : 0, 0);
            }
            else if (axis == AxisY)
            {
                dir = new Input8(0, Math.Abs(dy) > StandAlongUnits ? Math.Sign(dy) : 0);
            }
            else if (bestD2 > (long)StandUnits * StandUnits)
            {
                int dead = Torus.SubTilesPerTile / 4;
                dir = new Input8(Math.Abs(dx) > dead ? Math.Sign(dx) : 0, Math.Abs(dy) > dead ? Math.Sign(dy) : 0);
            }

            return true;
        }
    }
}
