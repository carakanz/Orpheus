using System;
using System.Collections.Generic;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Tests
{
    /// <summary>
    /// Трасса попаданий по персонажу на стороне сервера (C-004): на каждое событие «урон» — кто, откуда
    /// (позиция и начало замаха), карта тайлов 7 × 7 вокруг жертвы и хвост последних тактов (позиции врагов,
    /// состояние мозга, замах, двери). Вешается на <c>ServerOptions.AfterTick</c> или <c>EditRig.AfterTick</c>.
    /// </summary>
    public sealed class StrikeTrace
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Tail = 45;
        private readonly Queue<string> _tail = new Queue<string>();
        private readonly StringBuilder _log = new StringBuilder();
        private readonly StringBuilder _summary = new StringBuilder();
        private int _details;

        /// <summary>Окно тактов, в котором попадания пишутся подробно и без закрытой двери рядом.</summary>
        public long DetailFrom { get; set; } = long.MaxValue;
        public long DetailTo { get; set; } = long.MinValue;

        /// <summary>Окно тактов, строки которого пишутся подряд (без попаданий).</summary>
        public long DumpFrom { get; set; } = long.MaxValue;
        public long DumpTo { get; set; } = long.MinValue;
        private readonly StringBuilder _dump = new StringBuilder();

        /// <summary>Попаданий по персонажам записано.</summary>
        public int Hits { get; private set; }

        /// <summary>Текст трассы (пусто — попаданий не было).</summary>
        public string Text => "все попадания:" + System.Environment.NewLine + _summary + _log + _dump;

        private static string P(WorldPos p) => $"({p.X / U}:{p.X % U},{p.Y / U}:{p.Y % U})";

        /// <summary>Зовётся после каждого такта, пока события такта ещё в буфере.</summary>
        public void AfterTick(Simulation sim)
        {
            int ch = -1;
            for (int i = 0; i < sim.Entities.Length; i++)
            {
                if (sim.Entities[i].IsCharacter)
                {
                    ch = i;
                    break;
                }
            }

            if (ch < 0)
            {
                return;
            }

            SimEntity me = sim.Entities[ch];
            var line = new StringBuilder($"  т{sim.Tick} я {P(me.Mover.Position)} hp{me.Health}{(me.Alive ? string.Empty : " МЁРТВ")} неуязв{me.InvulnTicks} меч{me.Mover.Melee.Phase}");
            TileCoord mt = Torus.TileOf(sim.World.Shape, me.Mover.Position);
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    TileCoord t = Torus.Wrap(sim.World.Shape, new TileCoord(mt.X + dx, mt.Y + dy));
                    if (sim.World.GetObject(t) == ObjectId.Door)
                    {
                        bool open = sim.World.TryGetBuild(t, out BuildRecord r) && r.IsDoorOpen;
                        line.Append($" дверь({t.X},{t.Y}){(open ? "откр" : "ЗАКР")}");
                    }
                }
            }

            foreach (SimEntity e in sim.Entities)
            {
                if (e.Kind != SimEntity.KindEnemy)
                {
                    continue;
                }

                Int2 d = Torus.Delta(sim.World.Shape, me.Mover.Position, e.Mover.Position);
                if (Math.Max(Math.Abs(d.X), Math.Abs(d.Y)) <= 5 * U)
                {
                    line.Append($" | #{e.Id}{P(e.Mover.Position)} hp{e.Health} {e.Brain.State} замах{e.AttackWindup} от{P(e.AttackOrigin)} отход{e.AttackRecover} толчок{e.Mover.KnockLeft}");
                }
            }

            if (sim.Tick >= DumpFrom && sim.Tick <= DumpTo)
            {
                _dump.AppendLine(line.ToString());
            }

            _tail.Enqueue(line.ToString());
            while (_tail.Count > Tail)
            {
                _tail.Dequeue();
            }

            foreach (SimEvent ev in sim.Events)
            {
                if (ev.Kind != SimEventKind.Damage || ev.Victim != me.Id)
                {
                    continue;
                }

                Hits++;
                bool closedNear = false; // закрытая дверь в 2 тайлах от жертвы — попадание «за дверью», пишем подробно
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        TileCoord t = Torus.Wrap(sim.World.Shape, new TileCoord(mt.X + dx, mt.Y + dy));
                        closedNear |= sim.World.GetObject(t) == ObjectId.Door && sim.IsBlocked(sim.World.Shape, t);
                    }
                }

                _summary.AppendLine($"  т{sim.Tick} −{ev.Damage} от #{ev.Requester}, жертва {P(me.Mover.Position)}{(closedNear ? " [закрытая дверь рядом]" : string.Empty)}");
                if (!(closedNear || (sim.Tick >= DetailFrom && sim.Tick <= DetailTo)) || _details >= 12)
                {
                    continue;
                }

                _details++;

                _log.AppendLine($"УРОН т{sim.Tick}: {ev.Damage} (тип {ev.DamageType}) по #{ev.Victim} от #{ev.Requester}; карта 9 × 9 вокруг жертвы (# преграда, D дверь закрыта, d открыта, f забор, E враг, @ жертва):");
                for (int dy = -4; dy <= 4; dy++)
                {
                    var row = new StringBuilder("    ");
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        TileCoord t = Torus.Wrap(sim.World.Shape, new TileCoord(mt.X + dx, mt.Y + dy));
                        ushort obj = sim.World.GetObject(t);
                        char c = sim.IsBlocked(sim.World.Shape, t) ? '#' : '.';
                        if (obj == ObjectId.Door)
                        {
                            c = c == '#' ? 'D' : 'd';
                        }
                        else if (obj == ObjectId.WoodFence)
                        {
                            c = 'f';
                        }

                        foreach (SimEntity e in sim.Entities)
                        {
                            if (e.Kind == SimEntity.KindEnemy && e.Alive && Torus.TileOf(sim.World.Shape, e.Mover.Position) == t)
                            {
                                c = c == 'D' ? 'X' : 'E'; // X — враг на тайле закрытой двери
                            }
                        }

                        if (dx == 0 && dy == 0)
                        {
                            c = '@';
                        }

                        row.Append(c);
                    }

                    _log.AppendLine(row.ToString());
                }

                _log.AppendLine("  хвост тактов:");
                foreach (string s in _tail)
                {
                    _log.AppendLine(s);
                }
            }
        }
    }
}
