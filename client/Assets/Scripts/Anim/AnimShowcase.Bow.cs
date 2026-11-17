#nullable enable
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using Items = Orpheus.Core.Sim.Items;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// E3-2: лук в ленте снимков (файлы <c>E3-2_*.png</c>). Лучник восточнее дерева стреляет каждые 2 с (натяжение 0,2…0,8 с
    /// цикла, держит до 1,0 с, выстрел) во врага-болванку в 7 т; второй лучник с половинным натяжением стреляет в ствол дерева —
    /// стрела втыкается. Стрелы рисует тот же <see cref="ArrowView"/>, что в игре, полёт — <see cref="ClientProjectiles"/> по
    /// сообщениям «снаряд» (как с сервера), кольцо силы — у «курсора» на теле цели.
    /// </summary>
    public sealed partial class AnimShowcase
    {
        const float BowStart = 18f;
        const float BowCycle = 2f;
        const float BowDrawFrom = 0.2f, BowDrawTo = 0.8f, BowRelease = 1.0f;
        const float StuckDraw = 22.2f, StuckRelease = 22.5f;

        ActorRig? _archer, _archer2, _bowTarget;
        ClientProjectiles? _bowShots;
        ArrowView? _bowView;
        int _bowCycleShot = -1, _bowId;
        bool _stuckShot;
        float _hitAt = -1f;

        // Своё дерево лучников (не то, что рубит дровосек): восточнее его ствола — лучник в дерево, южнее на 3 т — лучник по цели.
        Vector3 _bowTree;
        bool _bowTreeFound;

        Vector3 ArcherPos => _bowTree + new Vector3(4.5f, -3f, 0f);
        Vector3 BowTargetPos => ArcherPos + new Vector3(7f, 0f, 0f);
        Vector3 Archer2Pos => _bowTree + new Vector3(4.5f, 0f, 0f);

        /// <summary>Дерево в 8…30 т от дерева сцены, у которого к востоку 12 × 5 тайлов меньше всего объектов.</summary>
        void FindBowTree()
        {
            WorldModel w = _g.World;
            TileCoord best = new TileCoord(_treeTile.X, _treeTile.Y + 14);
            int clutter = int.MaxValue;
            for (int dy = -30; dy <= 30; dy++)
            {
                for (int dx = -30; dx <= 30; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) < 8)
                    {
                        continue; // мимо сцены K-001 / C-012
                    }

                    TileCoord t = Torus.Wrap(_g.Map, new TileCoord(_treeTile.X + dx, _treeTile.Y + dy));
                    if (ObjectId.IdOf(w.GetObject(t)) != ObjectId.Tree)
                    {
                        continue;
                    }

                    int n = 0;
                    for (int y = -1; y <= 4 && n < clutter; y++)
                    {
                        for (int x = 1; x <= 13; x++)
                        {
                            TileCoord q = Torus.Wrap(_g.Map, new TileCoord(t.X + x, t.Y + y)); // ядро: y вниз — южнее
                            if (w.GetObject(q) != ObjectId.Empty)
                            {
                                n++;
                            }
                        }
                    }

                    if (n < clutter)
                    {
                        clutter = n;
                        best = t;
                    }
                }
            }

            _bowTree = _g.Origin.ToUnity(_g.TileCenter(best));
            _bowTreeFound = true;
            Debug.Log($"[anim] дерево лучников {best.X},{best.Y} (объектов к востоку {clutter})");
        }

        void BuildBow()
        {
            if (!_bowTreeFound)
            {
                FindBowTree();
            }

            _archer = new ActorRig(Make("Archer", Color.white));
            _archer.SetHeld(Items.SimpleBow);
            _archer2 = new ActorRig(Make("Archer stuck", Color.white));
            _archer2.SetHeld(Items.CopperBow);
            _bowTarget = new ActorRig(Make("Bow target", ActorFactory.EnemyTint));
            _bowShots = new ClientProjectiles(_g.Map);
            _bowView = new ArrowView(_stage);
            BuildArrows(); // C-031: стрелы-предметы и прохожий
        }

        /// <summary>Кадр лука: позы лучников, выстрелы по расписанию, стрелы и кольцо силы.</summary>
        void BowFrame(float dt)
        {
            if (_archer == null || _archer2 == null || _bowTarget == null || _bowShots == null || _bowView == null)
            {
                BuildBow();
            }

            ActorRig a = _archer!, a2 = _archer2!, tg = _bowTarget!;
            a.Root.position = ArcherPos;
            a2.Root.position = Archer2Pos;
            tg.Root.position = BowTargetPos;
            long nowUs = (long)(_t * 1e6);
            Vector3 cursor = BowTargetPos + new Vector3(0f, AimMath.BodyCenterY, 0f);
            Vector2 axis = ((Vector2)(cursor - (ArcherPos + new Vector3(0f, AimMath.BodyCenterY, 0f)))).normalized;

            // Лучник 1: цикл 2 с.
            var bow = new BowAction();
            var s = new ActorInput { Held = a.Held, DrawDirX = axis.x, DrawDirY = axis.y };
            if (_t >= C031Start)
            {
                // C-031: оба лучника держат ЛКМ — непрерывная стрельба, стрелы-предметы, подбор проходом
                ArrowsFrame(dt, a, a2, nowUs, ref bow, ref cursor);
                tg.Apply(tg.Animator.Step(dt, new ActorInput()));
                _bowPiles!.Frame(_g.Origin, dt, _bowShots!.Covers);
                _bowView!.Frame(_bowShots!, nowUs, _g.Origin, bow, ArcherPos, bow.Phase == BowAction.PhaseDraw ? cursor : (Vector3?)null);
                return;
            }

            if (_t >= BowStart)
            {
                int k = (int)((_t - BowStart) / BowCycle);
                float u = (_t - BowStart) - k * BowCycle;
                if (u >= BowDrawFrom && u < BowRelease)
                {
                    int frames = Mathf.Clamp(Mathf.FloorToInt((u - BowDrawFrom) * 60f) + 1, 1, BowAction.FullDrawFrames);
                    bow.Phase = BowAction.PhaseDraw;
                    bow.Frames = (byte)frames;
                    s.Drawing = true;
                    s.DrawPower = BowAction.PowerQ8(frames) / 256f;
                    s.DrawSec = (u - BowDrawFrom);
                }
                else if (u >= BowRelease)
                {
                    s.ShotSec = Mathf.Max(1e-4f, u - BowRelease);
                    if (_bowCycleShot != k)
                    {
                        _bowCycleShot = k;
                        Fire(ArcherPos, cursor, BowAction.FullDrawFrames, Items.SimpleBow, nowUs, BowTargetPos, true);
                    }
                }
            }

            a.Apply(a.Animator.Step(dt, s));

            // Лучник 2: в ствол дерева, половина натяжения.
            Vector3 trunk = _bowTree + new Vector3(0f, AimMath.BodyCenterY, 0f);
            Vector2 axis2 = ((Vector2)(trunk - (Archer2Pos + new Vector3(0f, AimMath.BodyCenterY, 0f)))).normalized;
            var s2 = new ActorInput { Held = a2.Held, DrawDirX = axis2.x, DrawDirY = axis2.y };
            if (_t >= StuckDraw && _t < StuckRelease)
            {
                int frames = Mathf.Clamp(Mathf.FloorToInt((_t - StuckDraw) * 60f) + 1, 1, BowAction.FullDrawFrames);
                s2.Drawing = true;
                s2.DrawPower = BowAction.PowerQ8(frames) / 256f;
                s2.DrawSec = _t - StuckDraw;
            }
            else if (_t >= StuckRelease)
            {
                s2.ShotSec = Mathf.Max(1e-4f, _t - StuckRelease);
                if (!_stuckShot)
                {
                    _stuckShot = true;
                    FireStuck(Archer2Pos, _bowTree, Items.CopperBow, nowUs, ProjectileEnd.Wall, 0.42f); // C-031: воткнулась — сама предмет
                }
            }

            a2.Apply(a2.Animator.Step(dt, s2));

            if (_hitAt >= 0f && _t >= _hitAt)
            {
                tg.Animator.OnHit(axis.x, axis.y);
                _sparks.Emit((Vector2)BowTargetPos + new Vector2(-0.3f, AimMath.BodyCenterY), -axis, AnimParams.HitSparks, AnimParams.HitSparkSpeed,
                    AnimParams.HitSparkLife, 80f, false, AnimParams.SparkLight);
                _hitAt = -1f;
            }

            tg.Apply(tg.Animator.Step(dt, new ActorInput()));
            _bowPiles!.Frame(_g.Origin, dt, _bowShots!.Covers); // C-031: стрелы-предметы, пока снаряд не закрывает
            _bowView!.Frame(_bowShots!, nowUs, _g.Origin, bow, ArcherPos, bow.Phase == BowAction.PhaseDraw ? cursor : (Vector3?)null);
        }

        /// <summary>Выстрел в показе: сообщение «выстрел» и сразу «конец» (у цели — попал, у ствола — воткнулась), как с сервера.</summary>
        void Fire(Vector3 from, Vector3 aimAt, int drawFrames, byte bowItem, long nowUs, Vector3 target, bool hit)
        {
            Projectiles.TryGetBow(bowItem, out BowDef def);
            int power = BowAction.PowerQ8(drawFrames);
            var p = new Projectile
            {
                Id = ++_bowId,
                Owner = 999,
                Kind = Projectiles.KindOfBow(bowItem),
                Origin = _g.Origin.FromUnity(from),
                AimRaw = AimMath.AimRaw(from.x, from.y, aimAt.x, aimAt.y),
                SpeedX256 = def.SpeedX256At(power),
                RangeUnits = def.RangeUnits,
            };
            _bowShots!.OnMessage(ProjectileMessage.ShotOf(p), 1, nowUs);
            float dist = Vector2.Distance(from, target) - (hit ? 0.6f : 0.42f);
            long travel = (long)(dist * Torus.SubTilesPerTile * 256f);
            WorldPos stop = Torus.Wrap(_g.Map, p.PositionAtTravel(travel));
            _bowShots.OnMessage(ProjectileMessage.StopOf(new Orpheus.Core.Sim.ProjectileStop(p.Id, p.Owner, hit ? ProjectileEnd.Hit : ProjectileEnd.Wall,
                stop, hit ? 1 : -1, false)), 1, nowUs);
            if (hit)
            {
                _hitAt = _t + travel / (float)p.SpeedX256 / 30f;
            }
        }

        /// <summary>Ленты лука (после C-012, с 18 с).</summary>
        Shot[] BowShots() => new[]
        {
            // натяжение крупно: покой, четверть, половина, полное (дрожь), выстрел, отдача
            new Shot { Name = "E3-2_draw", Focus = () => ArcherPos + new Vector3(0.25f, 0.3f, 0f), Ortho = ShotsOrtho,
                Times = new[] { 18.10f, 18.40f, 18.55f, 18.90f, 19.03f, 19.12f }, CropW = 440, CropH = 560 },
            // выстрел целиком: кольцо силы у курсора на цели, вылет, полёт со следом, попадание
            new Shot { Name = "E3-2_shot", Focus = () => (ArcherPos + BowTargetPos) * 0.5f + new Vector3(0f, 0.3f, 0f), Ortho = 4f,
                Times = new[] { 20.70f, 21.05f, 21.13f, 21.24f }, CropW = 1100, CropH = 330 },
            // в дерево: натяжение, вылет, полёт, втыкание, дрожь, висит
            new Shot { Name = "E3-2_stuck", Focus = () => (Archer2Pos + _bowTree) * 0.5f + new Vector3(0f, 0.5f, 0f), Ortho = 3f,
                Times = new[] { 22.45f, 22.56f, 22.66f, 22.75f, 23.40f }, CropW = 1000, CropH = 460 },
        };
    }
}
