#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Net.Protocol;
using UnityEngine;
using GroundPile = Orpheus.Core.Sim.GroundPile;
using Items = Orpheus.Core.Sim.Items;
using ProjectileStop = Orpheus.Core.Sim.ProjectileStop;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// C-031: стрелы в ленте снимков (файлы <c>C-031_*.png</c>, после E3-2, с 24 с). Оба лучника держат ЛКМ — непрерывная
    /// стрельба (полное натяжение 0,6 с → выстрел → отход 0,2 с → снова, 3 выстрела): медный лук — в ствол дерева (стрелы
    /// втыкаются в ствол), простой — в землю перед собой (как стрела в конце дальности; в показе путь короче 30 т, чтобы точка
    /// конца была в кадре). Каждая воткнувшаяся стрела — сама предмет: дрожит снарядом <see cref="ArrowView"/>, потом её место
    /// занимает предмет «воткнувшаяся стрела» <see cref="PileView"/> той же позы. Затем прохожий идёт мимо стрел в земле — они
    /// «прыгают» к нему (подбор проходом рядом, 1,5 т). Только вид: сообщения «снаряд» и «кучи» — как с сервера.
    /// </summary>
    public sealed partial class AnimShowcase
    {
        const float C031Start = 24f;
        const float C031Cycle = 0.8f, C031Draw = 0.6f;
        const int C031Shots = 3;
        const float C031WalkFrom = 27.0f, C031WalkTo = 28.8f;
        const float PickupTiles = 1.5f;

        static readonly Vector3[] GroundTargets = { new Vector3(2.6f, -1.0f, 0f), new Vector3(3.7f, -1.35f, 0f), new Vector3(4.8f, -0.95f, 0f) };
        static readonly float[] TrunkJitter = { 0.22f, -0.12f, 0.05f };

        PileView? _bowPiles;
        ActorRig? _arrowWalker;
        int _pileId;
        int _c031Fired1, _c031Fired2;
        readonly List<(int Id, WorldPos Pos)> _groundArrows = new List<(int, WorldPos)>();
        readonly HashSet<int> _pickedArrows = new HashSet<int>();

        Vector3 WalkStart => ArcherPos + new Vector3(-1.6f, -1.15f, 0f);
        Vector3 WalkEnd => ArcherPos + new Vector3(6.6f, -1.15f, 0f);

        void BuildArrows()
        {
            _bowPiles = new PileView(_stage);
            _arrowWalker = new ActorRig(Make("Arrow picker", Color.white));
            _arrowWalker.Root.gameObject.SetActive(false);
        }

        /// <summary>
        /// Удержание ЛКМ в показе: фаза цикла 0,8 с (натяжение 0,6 с, выстрел, отход 0,2 с). Возвращает вход позы и номер
        /// выстрела, который надо сделать в этом кадре (−1 — нет).
        /// </summary>
        ActorInput Hold(ActorRig rig, Vector2 axis, ref int fired, out int shoot, out BowAction bow)
        {
            var s = new ActorInput { Held = rig.Held, DrawDirX = axis.x, DrawDirY = axis.y };
            bow = new BowAction();
            shoot = -1;
            float u = _t - C031Start;
            int k = Mathf.FloorToInt(u / C031Cycle);
            float c = u - k * C031Cycle;
            if (k >= C031Shots)
            {
                s.ShotSec = Mathf.Max(1e-4f, u - (C031Shots - 1) * C031Cycle - C031Draw);
                return s;
            }

            if (c < C031Draw)
            {
                int frames = Mathf.Clamp(Mathf.FloorToInt(c * 60f) + 1, 1, BowAction.FullDrawFrames);
                bow.Phase = BowAction.PhaseDraw;
                bow.Frames = (byte)frames;
                s.Drawing = true;
                s.DrawPower = BowAction.PowerQ8(frames) / 256f;
                s.DrawSec = c;
            }
            else
            {
                s.ShotSec = Mathf.Max(1e-4f, c - C031Draw);
                if (fired <= k)
                {
                    fired = k + 1;
                    shoot = k;
                }
            }

            return s;
        }

        /// <summary>Кадр C-031: два лучника держат ЛКМ, стрелы-предметы, прохожий подбирает стрелы в земле.</summary>
        void ArrowsFrame(float dt, ActorRig a, ActorRig a2, long nowUs, ref BowAction ring, ref Vector3 cursor)
        {
            if (_bowPiles == null)
            {
                BuildArrows();
            }

            // Простой лук — в землю перед собой.
            int k1 = Mathf.Clamp(Mathf.FloorToInt((_t - C031Start) / C031Cycle), 0, C031Shots - 1);
            Vector3 ground = ArcherPos + GroundTargets[k1];
            Vector2 axis1 = ((Vector2)(ground - ArcherPos)).normalized;
            ActorInput s1 = Hold(a, axis1, ref _c031Fired1, out int shot1, out BowAction bow1);
            if (shot1 >= 0)
            {
                WorldPos stop = FireStuck(ArcherPos, ground, Items.SimpleBow, nowUs, ProjectileEnd.Range, 0f);
                _groundArrows.Add((_pileId, stop));
            }

            a.Apply(a.Animator.Step(dt, s1));
            ring = bow1;
            cursor = ground;

            // Медный лук — в ствол, чуть разная высота.
            int k2 = Mathf.Clamp(Mathf.FloorToInt((_t - C031Start) / C031Cycle), 0, C031Shots - 1);
            Vector3 trunk = _bowTree + new Vector3(0f, AimMath.BodyCenterY + TrunkJitter[k2], 0f);
            Vector2 axis2 = ((Vector2)(trunk - (Archer2Pos + new Vector3(0f, AimMath.BodyCenterY, 0f)))).normalized;
            ActorInput s2 = Hold(a2, axis2, ref _c031Fired2, out int shot2, out _);
            if (shot2 >= 0)
            {
                FireStuck(Archer2Pos, trunk - new Vector3(0f, AimMath.BodyCenterY, 0f), Items.CopperBow, nowUs, ProjectileEnd.Wall, 0.42f);
            }

            a2.Apply(a2.Animator.Step(dt, s2));

            // Прохожий: идёт мимо стрел в земле — в 1,5 т стрела «прыгает» к нему (сервер: подбор, «кучи нет» с поднявшим).
            ActorRig w = _arrowWalker!;
            if (!w.Root.gameObject.activeSelf)
            {
                w.Root.gameObject.SetActive(true);
            }

            float q = Mathf.Clamp01((_t - C031WalkFrom) / (C031WalkTo - C031WalkFrom));
            w.Root.position = Vector3.Lerp(WalkStart, WalkEnd, q);
            w.AnimateWalk(dt);
            if (_t >= C031WalkFrom)
            {
                foreach ((int id, WorldPos pos) in _groundArrows)
                {
                    if (!_pickedArrows.Contains(id) && Vector2.Distance(w.Root.position, _g.Origin.ToUnity(pos)) <= PickupTiles)
                    {
                        _pickedArrows.Add(id);
                        _bowPiles!.Remove(id, w.Root);
                        w.Animator.OnPickup(0.6f, -0.3f);
                    }
                }
            }
        }

        /// <summary>
        /// Выстрел, который кончается воткнувшейся стрелой: «выстрел» и «конец» (легла) — снаряду, и предмет «воткнувшаяся стрела»
        /// в точке конца — куче (как сервер шлёт 38, потом 27). Возвращает точку конца.
        /// </summary>
        WorldPos FireStuck(Vector3 from, Vector3 to, byte bowItem, long nowUs, ProjectileEnd reason, float shorten)
        {
            Projectiles.TryGetBow(bowItem, out BowDef def);
            var p = new Projectile
            {
                Id = ++_bowId,
                Owner = 999,
                Kind = Projectiles.KindOfBow(bowItem),
                Origin = _g.Origin.FromUnity(from),
                AimRaw = AimMath.AimRaw(from.x, from.y, to.x, to.y),
                SpeedX256 = def.SpeedX256At(256),
                RangeUnits = def.RangeUnits,
            };
            _bowShots!.OnMessage(ProjectileMessage.ShotOf(p), 1, nowUs);
            float dist = Vector2.Distance(from, to) - shorten;
            long travel = (long)(dist * Torus.SubTilesPerTile * 256f);
            WorldPos stop = Torus.Wrap(_g.Map, p.PositionAtTravel(travel));
            _bowShots.OnMessage(ProjectileMessage.StopOf(new ProjectileStop(p.Id, p.Owner, reason, stop, -1, true)), 1, nowUs);
            byte kind = reason == ProjectileEnd.Wall ? GroundPile.KindArrowWall : GroundPile.KindArrowGround;
            _bowPiles!.Set(++_pileId, Items.Arrows, 1, stop, kind, p.AimRaw);
            return stop;
        }

        /// <summary>Ленты C-031 (после E3-2, с 24 с).</summary>
        Shot[] ArrowShots() => new[]
        {
            // непрерывная стрельба двоих: натяжение, вылет, втыкание (ствол и земля), снова натяжение, все стрелы лежат
            new Shot { Name = "C-031_hold", Focus = () => _bowTree + new Vector3(4.6f, -1.7f, 0f), Ortho = 4.6f,
                Times = new[] { 24.35f, 24.66f, 25.15f, 25.50f, 26.62f }, CropW = 1240, CropH = 780 },
            // крупно: стрелы-предметы в земле и в стволе (дрожь кончилась, это уже предметы)
            new Shot { Name = "C-031_stuck", Focus = () => _bowTree + new Vector3(4.6f, -1.9f, 0f), Ortho = 2.6f,
                Times = new[] { 26.85f }, CropW = 1800, CropH = 1000 },
            // подбор проходом рядом: прохожий идёт вдоль стрел, они прыгают к нему
            new Shot { Name = "C-031_pickup", Focus = () => ArcherPos + new Vector3(2.6f, -1.0f, 0f), Ortho = 3.2f,
                Times = new[] { 27.05f, 27.70f, 27.95f, 28.20f, 28.45f, 28.85f }, CropW = 1100, CropH = 560 },
        };
    }
}
