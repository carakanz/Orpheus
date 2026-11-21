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
    /// C-036: действие чужого по снимку в ленте снимков (файлы <c>C-036_*.png</c>) — после врагов E3-3/E3-4, с 42,2 с.
    /// Дальний ряд y = −32 (ниже волка E3-3 — кадры чистые): шахтёр бьёт киркой на восток окнами по
    /// <see cref="SnapshotLayout.ActionDigQuantum"/> кадров (шаг = ceil(прогресс/квант), как считает сервер), лучник натягивает
    /// лук на болванку в 7 т с силой <see cref="BowAction.PowerQ8"/>&gt;&gt;2 и стреляет (отдача — по сообщению снаряда, как
    /// _remoteShots в игре). Байт action — настоящий <see cref="SnapshotLayout.EncodeAction"/>; поза — те же формулы, что
    /// <see cref="NetAnimation.RemoteAction"/> (кирка: период от кванта, окно = шаг × квант; лук: сила = шаг/63, кадры
    /// натяжения — обратное PowerQ8).
    /// </summary>
    public sealed partial class AnimShowcase
    {
        const float ActStart = 42.2f;
        const float ActPickDigSec = 0.8f;     // прогресс 48 кадров = 3 окна по кванту 16
        const float ActPickCycle = ActPickDigSec + 0.4f; // + пауза на ответ (шахтёр C-028 стоит в это время)
        const float ActBowCycle = 2.2f;       // натяжение 0,6 с (36 кадров), выстрел, отдача, покой
        const float ActFrameSec = 1f / ActionAnim.FramesPerSecond;

        ActorRig? _actMiner, _actArcher, _actTarget;
        ClientProjectiles? _actShots;
        ArrowView? _actView;
        int _actStep = -1;            // шаг кирки в снимке; таймер окна — как RemoteAct.Age
        float _actAge;
        int _actPickImp;              // ударов посчитано (номер окна удара: прогресс 24, 48, …)
        int _actShotCycle = -1;
        float _actHitAt = -1f;
        int _actArrowId;

        Vector3 ActMinerPos => _c + new Vector3(-2f, -32f, 0f);
        Vector3 ActArcherPos => _c + new Vector3(4f, -32f, 0f);
        Vector3 ActTargetPos => ActArcherPos + new Vector3(7f, 0f, 0f);

        void BuildAction()
        {
            _actMiner = new ActorRig(Make("Remote miner", ActorFactory.EnemyTint)); // как snapshot-враг: тинт + кирка
            _actMiner.SetHeld(Items.StonePickaxe);
            _actArcher = new ActorRig(Make("Remote archer", ActorFactory.EnemyArcherTint));
            _actArcher.SetHeld(Items.SimpleBow);
            _actTarget = new ActorRig(Make("Act target", ActorFactory.EnemyTint));
            _actShots = new ClientProjectiles(_g.Map);
            _actView = new ArrowView(_stage);
        }

        /// <summary>Кадр сцены C-036: чужой с киркой (шаги прогресса) и чужой лучник (сила натяжения, выстрел, отдача).</summary>
        void ActionFrame(float dt)
        {
            if (_actMiner == null || _actShots == null || _actView == null)
            {
                BuildAction();
            }

            ActorRig miner = _actMiner!, archer = _actArcher!, tg = _actTarget!;
            miner.Root.position = ActMinerPos;
            archer.Root.position = ActArcherPos;
            tg.Root.position = ActTargetPos;

            // 1) Кирка: серверный прогресс копки (кадры) → шаг снимка → поза по формулам RemoteAction.
            int q = SnapshotLayout.ActionDigQuantum;
            bool actOn = _t >= ActStart;
            float pu = actOn ? (_t - ActStart) % ActPickCycle : 0f;
            int progress = actOn && pu < ActPickDigSec ? Mathf.FloorToInt(pu * ActionAnim.FramesPerSecond) + 1 : 0;
            byte pick = progress > 0
                ? SnapshotLayout.EncodeAction(SnapshotLayout.ActionToolPick, (progress + q - 1) / q)
                : (byte)0;
            int step = SnapshotLayout.ActionToolOf(pick) == SnapshotLayout.ActionToolPick ? SnapshotLayout.ActionProgressOf(pick) : 0;
            if (step > 0)
            {
                if (_actStep != step)
                {
                    _actStep = step;
                    _actAge = 0f;
                }
                else
                {
                    _actAge += dt;
                }

                int n = Mathf.Max(1, step); // шаг 1 — первое окно (0 только пока снимок не дошёл)
                var s = new ActorInput
                {
                    Held = miner.Held,
                    Digging = true,
                    DigDirX = 1f, // ось — взгляд на восток, как чужой в снимке (прицела чужого в снимке нет)
                    DigDirY = 0f,
                    DigPeriod = ProceduralAnimator.DigPeriodFor(q),
                    Action = ActionAnim.DigActionOf(Items.StonePickaxe),
                    ActionFrames = n * q,
                    ActionSec = (n - 1) * q * ActFrameSec + Mathf.Min(_actAge, q * ActFrameSec),
                };
                miner.Apply(miner.Animator.Step(dt, s));
            }
            else
            {
                _actStep = 0;
                _actAge = 0f;
                miner.Apply(miner.Animator.Step(dt, new ActorInput { Held = miner.Held })); // в паузе — стойка
            }

            // Удары кирки (взмах ядра 24 кадра): прогресс дошёл до 24, 48 — крошка перед шахтёром.
            int imp = actOn ? (int)((_t - ActStart) / ActPickCycle) * 2 + (progress >= 24 ? 1 : 0) + (progress >= 48 ? 1 : 0) : 0;
            if (imp > _actPickImp)
            {
                _actPickImp = imp;
                NetAnimation.EmitOnRock(_sparks, ActMinerPos + new Vector3(0.8f, 0.35f, 0f), Vector2.left);
            }

            // 2) Лук: сила в снимке (PowerQ8>>2 в фазе натяжения, вне — 0); отдача — по сообщению снаряда, как _remoteShots.
            long nowUs = (long)(_t * 1e6);
            Vector3 cursor = ActTargetPos + new Vector3(0f, AimMath.BodyCenterY, 0f);
            Vector2 axis = ((Vector2)(cursor - (ActArcherPos + new Vector3(0f, AimMath.BodyCenterY, 0f)))).normalized;
            int bk = actOn ? Mathf.FloorToInt((_t - ActStart) / ActBowCycle) : -1;
            float bu = actOn ? (_t - ActStart) - bk * ActBowCycle : 0f;
            int frames = bu < 0.6f ? Mathf.Clamp(Mathf.FloorToInt(bu * ActionAnim.FramesPerSecond) + 1, 1, BowAction.FullDrawFrames) : 0;
            byte bowAct = frames > 0 ? SnapshotLayout.EncodeAction(SnapshotLayout.ActionToolBow, BowAction.PowerQ8(frames) >> 2) : (byte)0;
            var a = new ActorInput { Held = archer.Held };
            var bow = new BowAction();
            bool drawing = SnapshotLayout.ActionToolOf(bowAct) == SnapshotLayout.ActionToolBow;
            if (drawing)
            {
                int p = SnapshotLayout.ActionProgressOf(bowAct);
                a.Drawing = true;
                a.DrawPower = p / (float)SnapshotLayout.ActionProgressMask;
                // Сила Q8 ≈ шаг × 4; кадры натяжения — обратное PowerQ8: 12…36 (та же формула, что в игре).
                a.DrawSec = (BowAction.MinDrawFrames + p * 4 * (BowAction.FullDrawFrames - BowAction.MinDrawFrames) / 256) * ActFrameSec;
                a.DrawDirX = axis.x;
                a.DrawDirY = axis.y;
                bow.Phase = BowAction.PhaseDraw;
                bow.Frames = (byte)frames;
            }
            else if (bk >= 0 && bu >= 0.6f && bu < 0.6f + ActionAnim.BowRecoilSec)
            {
                a.ShotSec = Mathf.Max(1e-4f, bu - 0.6f);
                a.DrawDirX = axis.x;
                a.DrawDirY = axis.y;
                if (_actShotCycle != bk)
                {
                    _actShotCycle = bk;
                    FireAct(ActArcherPos, cursor, BowAction.FullDrawFrames, Items.SimpleBow, nowUs);
                }
            }

            archer.Apply(archer.Animator.Step(dt, a));
            tg.Apply(tg.Animator.Step(dt, new ActorInput()));
            if (_actHitAt >= 0f && _t >= _actHitAt)
            {
                _actHitAt = -1f;
                tg.Animator.OnHit(axis.x, axis.y);
                _sparks.Emit((Vector2)ActTargetPos + new Vector2(-0.3f, AimMath.BodyCenterY), -axis, AnimParams.HitSparks, AnimParams.HitSparkSpeed,
                    AnimParams.HitSparkLife, 80f, false, AnimParams.StrikeFlash, AnimParams.SparkLight);
            }

            _actView!.Frame(_actShots!, nowUs, _g.Origin, bow, ActArcherPos, drawing ? (Vector3?)cursor : null);
        }

        /// <summary>Выстрел чужого лучника: сообщение «выстрел» и сразу «конец» (в болванку — попал), как с сервера.</summary>
        void FireAct(Vector3 from, Vector3 aimAt, int drawFrames, byte bowItem, long nowUs)
        {
            Projectiles.TryGetBow(bowItem, out BowDef def);
            int power = BowAction.PowerQ8(drawFrames);
            var p = new Projectile
            {
                Id = ++_actArrowId,
                Owner = 998,
                Kind = Projectiles.KindOfBow(bowItem),
                Origin = _g.Origin.FromUnity(from),
                AimRaw = AimMath.AimRaw(from.x, from.y, aimAt.x, aimAt.y),
                SpeedX256 = def.SpeedX256At(power),
                RangeUnits = def.RangeUnits,
            };
            _actShots!.OnMessage(ProjectileMessage.ShotOf(p), 1, nowUs);
            float dist = Vector2.Distance(from, ActTargetPos) - 0.6f;
            long travel = (long)(dist * Torus.SubTilesPerTile * 256f);
            WorldPos stop = Torus.Wrap(_g.Map, p.PositionAtTravel(travel));
            _actShots.OnMessage(ProjectileMessage.StopOf(new Orpheus.Core.Sim.ProjectileStop(p.Id, p.Owner, ProjectileEnd.Hit, stop, 1, false)), 1, nowUs);
            _actHitAt = _t + travel / (float)p.SpeedX256 / 30f;
        }

        /// <summary>
        /// Ленты C-036 после врагов E3-3/E3-4 (с 42,2 с): кирка — три окна по кванту (шаги 1, 2, 3: подъём, верх, удар
        /// с крошкой); лук — натяжение по силе (покой, малая, половина, полная с дрожью), вылет стрелы, отдача и попадание.
        /// </summary>
        Shot[] ActionShots() => new[]
        {
            new Shot { Name = "C-036_pick", Focus = () => ActMinerPos + new Vector3(0.5f, 0.3f, 0f), Ortho = ShotsOrtho,
                Times = new[] { 42.26f, 42.40f, 42.52f, 42.66f, 42.78f, 42.92f }, CropW = 640, CropH = 560 },
            new Shot { Name = "C-036_bow", Focus = () => (ActArcherPos + ActTargetPos) * 0.5f + new Vector3(0f, 0.3f, 0f), Ortho = 4f,
                Times = new[] { 44.50f, 44.72f, 44.94f, 45.03f, 45.12f, 45.30f }, CropW = 1100, CropH = 330 },
        };
    }
}
