#nullable enable
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Net.Session;
using UnityEngine;
using Items = Orpheus.Core.Sim.Items;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// E3-4: враг в ленте снимков (файлы <c>E3-4_*.png</c>) — после кирки C-028, с 34 с. Красный враг-«ближний» с мечом в руке
    /// (вид, как у snapshot-врага: тёплый красный тинт + деревянный меч) раз в цикл заходит на белого героя: сектор-телеграф,
    /// удар с вспышкой на герое; герой контрит мечом — красная вспышка на враге; враг падает и растворяется
    /// (<see cref="DeathAnim"/>, как по снимку с HealthPct 0). Затем враг-лучник стреляет в героя (тот же <see cref="ArrowView"/>,
    /// полёт — <see cref="ClientProjectiles"/> по сообщению «выстрел»).
    /// E3-3: с 39,5 с — волк (серый тинт архетипа) бежит рыском с 4 тайлов быстрее шага, кусает сектором; герой контрит, волк
    /// падает и растворяется (файлы <c>E3-3_*.png</c>).
    /// </summary>
    public sealed partial class AnimShowcase
    {
        const float FoeStart = 34f;      // замах врага на героя (цикл SwingCycle)
        const float FoeHeroStart = 35.6f; // ответный удар героя мечом
        const float FoeArcherDraw = 37.2f, FoeArcherFire = 37.8f; // враг-лучник: натяжение и выстрел в героя
        const float FoeDeathAt = 38.2f;  // смерть врага: падение и растворение
        const float FoeWolfDashStart = 39.5f, WolfDashSec = 0.5f;  // E3-3: рывок волка (8.2): 4 тайла за полсекунды
        const float FoeWolfBiteStart = 40f, FoeWolfHeroStart = 40.7f, FoeWolfDeathAt = 41.2f; // укус, ответ героя, смерть

        SwingActor? _foe, _foeWolf;
        ActorRig? _foeHero, _foeArcher, _foeWolfHero;
        bool _foeHeroHit, _foeShot, _foeWolfHit;
        float _foeArrowHitAt = -1f;

        // Далеко на юге: красные болванки не попадают ни в кадры лука (E3-2, его ряд y ≈ -6…-8), ни в кадры
        // стрел C-031 (низ кадра y ≈ -12) и кирки C-028.
        Vector3 FoeHome => _c + new Vector3(0.8f, -16f, 0f);
        Vector3 FoeHeroPos => FoeHome + new Vector3(-1.5f, 0f, 0f);
        Vector3 FoeArcherPos => _c + new Vector3(5f, -16f, 0f);
        Vector3 FoeWolfStartPos => _c + new Vector3(8.5f, -24f, 0f);   // волк: восток, 4 тайла от жертвы; ряд y=-24 — ниже ряда врага E3-4 (y=-16), кадры чистые
        Vector3 FoeWolfPos => _c + new Vector3(4.5f, -24f, 0f);
        Vector3 FoeWolfHeroPos => _c + new Vector3(3.2f, -24f, 0f);

        void BuildFoe()
        {
            _foe = new SwingActor(Make("Foe", ActorFactory.EnemyTint), _sectorMat);
            _foe.Rig.SetHeld(Items.WoodenSword); // как snapshot-враг в игре (NetAnimation.Remote): мечник с мечом
            _foeHero = new ActorRig(Make("Foe hero", Color.white));
            _foeHero.SetHeld(Items.WoodenSword);
            _foeArcher = new ActorRig(Make("Foe archer", ActorFactory.EnemyArcherTint)); // E3-3: тон по архетипу, как в игре
            _foeArcher.SetHeld(Items.SimpleBow);
            _foeWolf = new SwingActor(Make("Foe wolf", ActorFactory.EnemyBeastTint), _sectorMat);
            _foeWolf.Axis = Vector2.left;
            _foeWolfHero = new ActorRig(Make("Wolf hero", Color.white));
            _foeWolfHero.SetHeld(Items.WoodenSword);
        }

        /// <summary>Кадр сцены врага: фазы замахов, попадания со вспышками, смерть, выстрел лучника.</summary>
        void FoeFrame(float dt)
        {
            if (_foe == null || _foeHero == null || _foeArcher == null)
            {
                BuildFoe();
            }

            SwingActor foe = _foe!;
            ActorRig hero = _foeHero!, archer = _foeArcher!;
            Vector3 foePos = FoeHome, heroPos = FoeHeroPos, archerPos = FoeArcherPos;
            foe.Rig.Root.position = foePos;
            hero.Root.position = heroPos;
            archer.Root.position = archerPos;

            // 1) Замах врага (сектор-телеграф наполняется, в ударе — вспышка, попадание по герою).
            if (_t < FoeDeathAt)
            {
                float q = 0f;
                SwingPhase ph = _t >= FoeStart && _t < FoeStart + SwingCycle ? PhaseAt(_t - FoeStart, out q) : SwingPhase.None;
                bool struck = foe.Animate(dt, ph, q);
                if (struck && foe.InSector(heroPos))
                {
                    Vector2 dir = ((Vector2)(heroPos - foePos)).normalized;
                    hero.Animator.OnHit(dir.x, dir.y);
                    _sparks.Emit((Vector2)heroPos + new Vector2(0f, 0.35f), dir, AnimParams.HitSparks, AnimParams.HitSparkSpeed,
                        AnimParams.HitSparkLife, 80f, false, AnimParams.StrikeFlash, AnimParams.SparkLight);
                }
            }

            // 2) Герой контрит: в миг удара — отдача и красная вспышка на враге; через паузу — смерть.
            float sh = _t - FoeHeroStart;
            float hq = 0f;
            SwingPhase hph = sh >= 0f && sh < SwingCycle ? PhaseAt(sh, out hq) : SwingPhase.None;
            hero.Apply(hero.Animator.Step(dt, new ActorInput
            {
                Held = hero.Held, Swing = hph, SwingProgress = hq, SwingDirX = 1f, SwingDirY = 0f,
            }));
            if (sh >= MeleeWindupSec && !_foeHeroHit)
            {
                _foeHeroHit = true;
                foe.Rig.Animator.OnHit(1f, 0f); // удар пришёл с запада
                _sparks.Emit((Vector2)foePos + new Vector2(-0.35f, 0.35f), Vector2.right, AnimParams.HitSparks, AnimParams.HitSparkSpeed,
                    AnimParams.HitSparkLife, 80f, false, AnimParams.StrikeFlash, AnimParams.SparkLight);
            }

            // 3) Смерть врага: падает от героя и растворяется (E3-1) — сектор гаснет (фаза None).
            if (_t >= FoeDeathAt)
            {
                foe.Animate(dt, SwingPhase.None, 0f);
                foe.Rig.Apply(DeathAnim.Apply(foe.Rig.Animator.Step(dt, new ActorInput()), _t - FoeDeathAt,
                    fallRight: foePos.x >= heroPos.x, dissolve: true));
            }

            // 4) Враг-лучник: натяжение, выстрел на запад в героя, попадание со вспышкой.
            bool foeDrawing = _t >= FoeArcherDraw && _t < FoeArcherFire && _bowShots != null;
            var a = new ActorInput { Held = archer.Held, DrawDirX = -1f, DrawDirY = 0f };
            if (foeDrawing)
            {
                int frames = Mathf.Clamp(Mathf.FloorToInt((_t - FoeArcherDraw) * 60f) + 1, 1, BowAction.FullDrawFrames);
                a.Drawing = true;
                a.DrawPower = BowAction.PowerQ8(frames) / 256f;
                a.DrawSec = _t - FoeArcherDraw;
            }
            else if (_t >= FoeArcherFire && _bowShots != null)
            {
                a.ShotSec = Mathf.Max(1e-4f, _t - FoeArcherFire);
                if (!_foeShot)
                {
                    _foeShot = true;
                    Vector3 aim = heroPos + new Vector3(0f, AimMath.BodyCenterY, 0f);
                    Fire(archerPos, aim, BowAction.FullDrawFrames, Items.SimpleBow, (long)(_t * 1e6), heroPos, true);
                    _foeArrowHitAt = _hitAt; // вспышку на герое рисует эта сцена, лучники E3-2 — у своей цели
                    _hitAt = -1f;
                }
            }

            archer.Apply(archer.Animator.Step(dt, a));
            if (_foeArrowHitAt >= 0f && _t >= _foeArrowHitAt)
            {
                _foeArrowHitAt = -1f;
                hero.Animator.OnHit(-1f, 0f);
                _sparks.Emit((Vector2)heroPos + new Vector2(-0.3f, AimMath.BodyCenterY), Vector2.left, AnimParams.HitSparks,
                    AnimParams.HitSparkSpeed, AnimParams.HitSparkLife, 80f, false, AnimParams.SparkLight);
            }

            // 5) E3-3: волк — рывок с 4 тайлов (вдвое быстрее шага), укус сектором, ответ героя, смерть (8.2).
            SwingActor wolf = _foeWolf!;
            ActorRig wolfHero = _foeWolfHero!;
            Vector3 wolfHeroPos = FoeWolfHeroPos;
            float wp = Mathf.Clamp01((_t - FoeWolfDashStart) / WolfDashSec);
            Vector3 wolfPos = Vector3.Lerp(FoeWolfStartPos, FoeWolfPos, wp);
            wolf.Rig.Root.position = wolfPos;
            wolfHero.Root.position = wolfHeroPos;

            if (_t >= FoeWolfDeathAt)
            {
                wolf.Animate(dt, SwingPhase.None, 0f);
                wolf.Rig.Apply(DeathAnim.Apply(wolf.Rig.Animator.Step(dt, new ActorInput()), _t - FoeWolfDeathAt,
                    fallRight: false, dissolve: true));
            }
            else
            {
                float wq = 0f;
                SwingPhase wph = _t >= FoeWolfBiteStart && _t < FoeWolfBiteStart + SwingCycle ? PhaseAt(_t - FoeWolfBiteStart, out wq) : SwingPhase.None;
                bool wolfStruck = wolf.Animate(dt, wph, wq);
                if (wolfStruck && wolf.InSector(wolfHeroPos))
                {
                    Vector2 dir = ((Vector2)(wolfHeroPos - wolfPos)).normalized;
                    wolfHero.Animator.OnHit(dir.x, dir.y);
                    _sparks.Emit((Vector2)wolfHeroPos + new Vector2(0f, 0.35f), dir, AnimParams.HitSparks, AnimParams.HitSparkSpeed,
                        AnimParams.HitSparkLife, 80f, false, AnimParams.StrikeFlash, AnimParams.SparkLight);
                }
            }

            // Ответ героя мечом: в миг удара — красная вспышка на волке; через паузу — смерть.
            float wsh = _t - FoeWolfHeroStart;
            float whq = 0f;
            SwingPhase whph = wsh >= 0f && wsh < SwingCycle ? PhaseAt(wsh, out whq) : SwingPhase.None;
            wolfHero.Apply(wolfHero.Animator.Step(dt, new ActorInput
            {
                Held = wolfHero.Held, Swing = whph, SwingProgress = whq, SwingDirX = -1f, SwingDirY = 0f,
            }));
            if (wsh >= MeleeWindupSec && !_foeWolfHit)
            {
                _foeWolfHit = true;
                wolf.Rig.Animator.OnHit(-1f, 0f); // удар пришёл с запада
                _sparks.Emit((Vector2)wolfPos + new Vector2(0.35f, 0.35f), Vector2.left, AnimParams.HitSparks, AnimParams.HitSparkSpeed,
                    AnimParams.HitSparkLife, 80f, false, AnimParams.StrikeFlash, AnimParams.SparkLight);
            }

            // Стрелу рисует тот же ArrowView, что у лука (список выстрелов общий): перерисовываем после BowFrame — якорь наш лучник.
            if (_bowView != null && _bowShots != null && _t >= FoeArcherDraw)
            {
                var b = new BowAction();
                if (foeDrawing)
                {
                    b.Phase = BowAction.PhaseDraw;
                    b.Frames = (byte)Mathf.Clamp(Mathf.FloorToInt((_t - FoeArcherDraw) * 60f) + 1, 1, BowAction.FullDrawFrames);
                }

                Vector3 aim = heroPos + new Vector3(0f, AimMath.BodyCenterY, 0f);
                _bowView.Frame(_bowShots, (long)(_t * 1e6), _g.Origin, b, archerPos, foeDrawing ? (Vector3?)aim : null);
            }
        }

        /// <summary>Фаз замаха: ветер меча — столько же, сколько <see cref="PhaseAt"/> доводит до удара (0,4 с).</summary>
        const float MeleeWindupSec = 0.4f;

        /// <summary>Ленты врага (после кирки C-028, с 34 с): замах с сектором, ответ героя, выстрел лучника, смерть.</summary>
        Shot[] FoeShots() => new[]
        {
            // замах врага: телеграф растёт, удар (сектор золотой), вспышка на герое, отход
            new Shot { Name = "E3-4_foe_swing", Focus = () => (FoeHome + FoeHeroPos) * 0.5f, Ortho = ShotsOrtho,
                Times = new[] { 34.12f, 34.30f, 34.44f, 34.52f, 34.66f, 34.90f }, CropW = 640, CropH = 560 },
            // герой мечом: замах, удар — красная вспышка и отдача на враге
            new Shot { Name = "E3-4_hero_hit", Focus = () => (FoeHome + FoeHeroPos) * 0.5f, Ortho = ShotsOrtho,
                Times = new[] { 35.80f, 35.95f, 36.03f, 36.10f, 36.25f, 36.50f }, CropW = 640, CropH = 560 },
            // стрела врага-лучника: натяжение, вылет, полёт, попадание в героя
            new Shot { Name = "E3-4_foe_arrow", Focus = () => (FoeArcherPos + FoeHeroPos) * 0.5f + new Vector3(0f, 0.3f, 0f), Ortho = 4f,
                Times = new[] { 37.35f, 37.62f, 37.83f, 37.90f, 37.96f, 38.10f }, CropW = 1100, CropH = 330 },
            // смерть: начало падения, лежание, растворение (кадры с шагом по фазам DeathAnim: падение 0,35 с, раствор 0,9–1,7 с)
            new Shot { Name = "E3-4_foe_death", Focus = () => (FoeHome + FoeHeroPos) * 0.5f + new Vector3(0.8f, 0f, 0f), Ortho = 3f,
                Times = new[] { 38.24f, 38.32f, 38.42f, 38.62f, 38.90f, 39.30f }, CropW = 1000, CropH = 560 },
            // E3-3: волк — рывок с 4 тайлов (серый тинт архетипа), сектор-телеграф, укус в героя.
            new Shot { Name = "E3-3_wolf_pounce", Focus = () => (FoeWolfStartPos + FoeWolfHeroPos) * 0.5f, Ortho = 4f,
                Times = new[] { 39.55f, 39.70f, 39.85f, 39.98f, 40.15f, 40.38f }, CropW = 1100, CropH = 330 },
            // E3-3: укус со вспышкой на герое, ответ героя (красная вспышка на волке), падение и растворение.
            new Shot { Name = "E3-3_wolf_bite", Focus = () => (FoeWolfPos + FoeWolfHeroPos) * 0.5f, Ortho = ShotsOrtho,
                Times = new[] { 40.44f, 40.56f, 40.80f, 41.10f, 41.30f, 41.75f }, CropW = 640, CropH = 560 },
        };
    }
}
