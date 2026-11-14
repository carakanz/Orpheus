using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// E2-1: бот подбирает добычу с земли. Добыча копания падает кучей на тайл объекта и первые 20 тактов не
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

        /// <summary>Куч брошено по времени.</summary>
        public int Abandoned => _abandoned.Count;

        /// <summary>
        /// Есть куча, за которой стоит идти: <paramref name="dir"/> — куда шагнуть (<see cref="Input8.None"/> — стоять и ждать).
        /// <paramref name="wants"/> — фильтр по предмету (null — любые). Нет кучи — false.
        /// </summary>
        public bool TrySeek(NetClient client, WorldPos pos, Func<byte, bool>? wants, out Input8 dir)
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
            }

            if (++_frames > ChaseFrames)
            {
                _abandoned.Add(target.Id);
                _pileId = 0;
                return false;
            }

            if (bestD2 > (long)StandUnits * StandUnits)
            {
                int dx = Torus.DeltaX(_shape, pos.X, target.Position.X);
                int dy = Torus.DeltaY(_shape, pos.Y, target.Position.Y);
                int dead = Torus.SubTilesPerTile / 4;
                dir = new Input8(Math.Abs(dx) > dead ? Math.Sign(dx) : 0, Math.Abs(dy) > dead ? Math.Sign(dy) : 0);
            }

            return true;
        }
    }
}
