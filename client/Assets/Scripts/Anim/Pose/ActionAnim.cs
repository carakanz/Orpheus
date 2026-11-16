#nullable enable
using System;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Client.Anim
{
    /// <summary>Действие руками (E2-7): что играет анимация тела и предмета в руке.</summary>
    public enum ActionKind : byte
    {
        None = 0,

        /// <summary>Копание без инструмента — прежний толчок к тайлу (K-001).</summary>
        Poke = 1,

        /// <summary>Рубка топором: замах из-за головы сверху вниз.</summary>
        Chop = 2,

        /// <summary>Кирка: замах с пробивом вниз-вперёд.</summary>
        Mine = 3,

        /// <summary>Молот (снос): короткие удары.</summary>
        Hammer = 4,
    }

    /// <summary>Как предмет держится в руке (E2-7): инструменты и оружие — за рукоять по диагонали значка, прочее — в ладони.</summary>
    public enum HeldLook : byte
    {
        None = 0,
        Axe = 1,
        Pick = 2,
        Hammer = 3,
        Sword = 4,
        Bow = 5,
        Torch = 6,

        /// <summary>Ресурс, еда, постройка — несётся перед собой.</summary>
        Item = 7,
    }

    /// <summary>
    /// Чистые функции анимации действий (E2-7), без Unity: выбор анимации по предмету, такт ударов по времени копания ядра,
    /// углы руки с предметом по фазе. Время — секунды; углы — градусы, против часовой, для персонажа, смотрящего вправо
    /// (0 — значок как нарисован: рукоять внизу слева, головка вверху справа, т. е. ось предмета под 45°).
    /// <para>
    /// Такт: копание тайла длится <c>digFrames</c> кадров ядра (60 Гц); оно делится на целое число замахов
    /// <see cref="BeatsFor"/> длиной около <see cref="BeatTargetSec"/>; удар — в конце каждого замаха, последний — ровно
    /// в кадр, когда ядро снимает объект. Так удар на экране совпадает с шагом копания, а не идёт своим таймером.
    /// </para>
    /// </summary>
    public static class ActionAnim
    {
        /// <summary>Кадров ядра в секунду (кадр ввода 60 Гц).</summary>
        public const int FramesPerSecond = 60;

        /// <summary>Желаемая длина одного замаха, секунд; фактическая — время копания / целое число замахов.</summary>
        public const float BeatTargetSec = 0.42f;

        /// <summary>Длина замаха, когда время копания неизвестно (0 — отказ, «нужен инструмент»).</summary>
        public const float BeatFallbackSec = 0.42f;

        /// <summary>Доли замаха: возврат после удара (отдача) до <see cref="RecoverShare"/>, подъём до <see cref="StrikeShare"/>, дальше удар.</summary>
        public const float RecoverShare = 0.2f;
        public const float StrikeShare = 0.8f;

        /// <summary>Жест стройки и наклон подбора, секунд.</summary>
        public const float BuildSec = 0.34f;
        public const float PickupSec = 0.3f;

        // --- углы руки (градусы) ---

        /// <summary>Несёт инструмент: головка чуть вперёд-вверх.</summary>
        public const float CarryToolDeg = -20f;

        /// <summary>Несёт предмет в ладони — значок прямо.</summary>
        public const float CarryItemDeg = 0f;

        /// <summary>Рубка: верх замаха (головка за головой) и удар (ось вперёд-вниз).</summary>
        public const float ChopTopDeg = 115f;
        public const float ChopHitDeg = -80f;

        /// <summary>Кирка: верх и удар (ниже и дальше вперёд, чем топор).</summary>
        public const float MineTopDeg = 100f;
        public const float MineHitDeg = -100f;

        /// <summary>Молот: короткий замах.</summary>
        public const float HammerTopDeg = 45f;
        public const float HammerHitDeg = -70f;

        /// <summary>Меч: верх замаха и конец удара.</summary>
        public const float SwordTopDeg = 110f;
        public const float SwordHitDeg = -105f; // C-012: клинок (рука + 45°) кончает удар на −60° — южная кромка сектора 120°

        /// <summary>Отдача после удара: подскок предмета, градусов, и отскок тела назад, тайлов.</summary>
        public const float RecoilDeg = 16f;
        public const float RecoilBack = 0.045f;

        /// <summary>Сколько вертикали цели (к тайлу выше/ниже) добавляется к углу руки, доля угла цели.</summary>
        public const float AimFollow = 0.45f;

        /// <summary>Угол руки выше этого — предмет за головой, рисуется позади тела.</summary>
        public const float BehindDeg = 75f;

        /// <summary>Возврат руки из действия к «несёт», 1/с (больше — резче).</summary>
        public const float HandBlend = 14f;

        /// <summary>Предмет в руке качается в такт шагу, градусов.</summary>
        public const float WalkSwingDeg = 7f;

        /// <summary>Как держится предмет (по свойствам из таблицы предметов ядра).</summary>
        public static HeldLook LookOf(byte item)
        {
            if (item == Items.None || !Items.Exists(item))
            {
                return HeldLook.None;
            }

            switch (Items.KindOf(item))
            {
                case Items.KindTool:
                    return Items.ToolKindOf(item) == Items.ToolAxe ? HeldLook.Axe : HeldLook.Pick;
                case Items.KindHammer:
                    return HeldLook.Hammer;
                case Items.KindMelee:
                    return HeldLook.Sword;
                case Items.KindRanged:
                    return HeldLook.Bow;
            }

            return item == Items.Torch ? HeldLook.Torch : HeldLook.Item;
        }

        /// <summary>Инструмент держится за рукоять по диагонали значка (иначе — в ладони, значок прямо).</summary>
        public static bool IsGripped(HeldLook look) => look != HeldLook.None && look != HeldLook.Item;

        /// <summary>Анимация копания по предмету в руке: топор — рубка, кирка — пробив, молот — удары, прочее — толчок.</summary>
        public static ActionKind DigActionOf(byte item)
        {
            if (item == Items.None || !Items.Exists(item))
            {
                return ActionKind.Poke;
            }

            switch (Items.DigToolOf(item))
            {
                case ToolKind.Axe:
                    return ActionKind.Chop;
                case ToolKind.Pick:
                    return ActionKind.Mine;
                case ToolKind.Hammer:
                    return ActionKind.Hammer;
            }

            return ActionKind.Poke;
        }

        /// <summary>Замахов на тайл: время копания / <see cref="BeatTargetSec"/>, округлённо, не меньше одного.</summary>
        public static int BeatsFor(int digFrames)
        {
            if (digFrames <= 0)
            {
                return 1;
            }

            double sec = digFrames / (double)FramesPerSecond;
            return Math.Max(1, (int)Math.Round(sec / BeatTargetSec));
        }

        /// <summary>Длина одного замаха, секунд.</summary>
        public static float BeatSec(int digFrames) =>
            digFrames <= 0 ? BeatFallbackSec : (float)(digFrames / (double)FramesPerSecond / BeatsFor(digFrames));

        /// <summary>
        /// Фаза замаха на миг <paramref name="elapsedSec"/> от начала копания: доля 0…1 и номер замаха. После последнего удара
        /// (ядро уже сняло объект, ответ сервера в пути) — замах <c>beats</c>, доля растёт до <see cref="RecoverShare"/> и стоит:
        /// отдача доигрывается, новый замах без нового копания не начинается.
        /// </summary>
        public static float BeatPhase(float elapsedSec, int digFrames, out int beat)
        {
            float len = BeatSec(digFrames);
            int beats = digFrames <= 0 ? int.MaxValue : BeatsFor(digFrames);
            if (elapsedSec <= 0f)
            {
                beat = 0;
                return 0f;
            }

            double x = elapsedSec / (double)len;
            long b = (long)Math.Floor(x);
            if (b >= beats)
            {
                beat = beats;
                return (float)Math.Min(RecoverShare, x - beats);
            }

            beat = (int)b;
            return (float)(x - b);
        }

        /// <summary>
        /// Ударов (концов замаха) на отрезке времени копания (<paramref name="fromSec"/>; <paramref name="toSec"/>]: столько
        /// раз за кадр показать отклик. Не больше числа замахов на тайл; при неизвестном времени — по длине запасного замаха.
        /// Не зависит от того, каким шагом идут кадры.
        /// </summary>
        public static int ImpactsBetween(float fromSec, float toSec, int digFrames)
        {
            if (toSec <= fromSec)
            {
                return 0;
            }

            float len = BeatSec(digFrames);
            long cap = digFrames <= 0 ? long.MaxValue : BeatsFor(digFrames);
            long a = Math.Min(cap, (long)Math.Floor(Math.Max(0f, fromSec) / (double)len + 1e-6));
            long b = Math.Min(cap, (long)Math.Floor(toSec / (double)len + 1e-6));
            return (int)Math.Max(0, b - a);
        }

        /// <summary>Угол руки, когда действия нет: инструмент чуть вперёд, предмет в ладони — прямо; качание в такт шагу.</summary>
        public static float CarryDeg(HeldLook look, float stepPhase, float walk)
        {
            float baseDeg = look == HeldLook.Item ? CarryItemDeg : CarryToolDeg;
            return baseDeg + WalkSwingDeg * (float)Math.Sin(Math.PI * 2 * stepPhase) * walk;
        }

        /// <summary>Поза действия в доле замаха: угол руки, наклон тела, сдвиг к цели, приседание.</summary>
        public struct Swing
        {
            public float HandDeg;
            public float LeanDeg;    // наклон тела к цели (+ — вперёд), градусов
            public float Forward;    // сдвиг тела к цели, тайлов (отрицательный — назад)
            public float Squash;     // доля высоты
            public float ReachX;     // рука от плеча к цели: вперёд, тайлов
            public float ReachY;     // и вверх, тайлов
        }

        /// <summary>
        /// Поза замаха инструментом в доле <paramref name="u"/> (0…1) замаха: удар — в u = 1 (угол <c>Hit</c>), дальше — отдача
        /// в начале следующего замаха. <paramref name="first"/> — первый замах копания: начинается от угла «несёт», а не от удара.
        /// </summary>
        public static Swing ToolSwing(ActionKind kind, float u, bool first)
        {
            TopHit(kind, out float top, out float hit, out float lunge);
            u = u < 0f ? 0f : u > 1f ? 1f : u;
            var s = new Swing();
            if (u < RecoverShare)
            {
                // Отдача: предмет подскакивает от удара и идёт назад к середине; тело отскакивает.
                float r = u / RecoverShare;
                float from = first ? CarryToolDeg : hit;
                float mid = Lerp(from, (top + hit) * 0.5f, Smooth(r));
                float bounce = first ? 0f : RecoilDeg * (float)Math.Sin(Math.PI * r) * (1f - r);
                s.HandDeg = mid + bounce;
                s.Forward = first ? 0f : Lerp(lunge, 0f, r) - RecoilBack * (float)Math.Sin(Math.PI * r);
                s.LeanDeg = first ? 0f : Lerp(8f, 0f, r);
                return s;
            }

            if (u < StrikeShare)
            {
                // Подъём: быстро вверх, у верха замирает; тело откидывается назад и приседает.
                float q = (u - RecoverShare) / (StrikeShare - RecoverShare);
                float e = 1f - (1f - q) * (1f - q);
                float start = (top + hit) * 0.5f; // там, где кончилась отдача (у первого замаха — подъём от «несёт»)
                s.HandDeg = Lerp(start, top, e);
                s.LeanDeg = -7f * e;
                s.Forward = -0.05f * e;
                s.Squash = 0.05f * e;
                return s;
            }

            // Удар: разгон (ease-in) к углу удара ровно в u = 1, тело выпадает к цели.
            float w = (u - StrikeShare) / (1f - StrikeShare);
            float k = w * w;
            s.HandDeg = Lerp(top, hit, k);
            s.LeanDeg = Lerp(-7f, 8f, k);
            s.Forward = Lerp(-0.05f, lunge, k);
            s.Squash = Lerp(0.05f, -0.03f, k);
            return s;
        }

        /// <summary>Угол руки с мечом по фазе удара (<see cref="SwingPhase"/>): замах вверх, удар вниз, отход к «несёт».</summary>
        public static float SwordDeg(SwingPhase phase, float q) => SwordDeg(phase, q, 0f);

        /// <summary>C-012: то же с прицелом <paramref name="aimDeg"/> без геометрии руки (предмет не меч — оценка по углу руки).</summary>
        public static float SwordDeg(SwingPhase phase, float q, float aimDeg)
        {
            q = q < 0f ? 0f : q > 1f ? 1f : q;
            float top = Math.Min(SwordTopDeg + aimDeg, 150f);
            float hit = SwordHitDeg + aimDeg;
            switch (phase)
            {
                case SwingPhase.Windup:
                    return Lerp(CarryToolDeg, top, 1f - (1f - q) * (1f - q));
                case SwingPhase.Strike:
                    return Lerp(top, hit, Math.Min(1f, q * 1.6f));
                case SwingPhase.Recover:
                    return Lerp(hit, CarryToolDeg, Smooth(q));
            }

            return CarryToolDeg;
        }

        /// <summary>
        /// C-012: угол руки с мечом так, чтобы кончик клинка (как его рисует <c>ActorRig</c>, <see cref="HeldPoint"/>) шёл по дуге
        /// зоны удара ядра. Вид сверху: плоскость экрана — плоскость земли, поэтому взмах «сверху вниз» у персонажа, смотрящего
        /// вбок, — дуга по земле. Кончик от точки ядра: верх замаха — прицел + <see cref="SwordTipTopDeg"/>, удар — от него до
        /// прицел − <see cref="SwordTipEndDeg"/> (кромка сектора 120° с запасом), отход — к «несёт». <paramref name="aimDeg"/> —
        /// <see cref="AimDeg"/> (по взгляду: 0 — вперёд, +90 — север, −90 — юг).
        /// </summary>
        public static float SwordHandDeg(in Pose p, SwingPhase phase, float q, float aimDeg)
        {
            q = q < 0f ? 0f : q > 1f ? 1f : q;
            float top = Math.Min(aimDeg + SwordTipTopDeg, SwordTipTopMaxDeg);
            float end = aimDeg - SwordTipEndDeg;
            switch (phase)
            {
                case SwingPhase.Windup:
                    return Lerp(CarryToolDeg, SolveSwordHand(p, top), 1f - (1f - q) * (1f - q));
                case SwingPhase.Strike:
                    return SolveSwordHand(p, Lerp(top, end, Math.Min(1f, q * 1.6f)));
                case SwingPhase.Recover:
                    return Lerp(SolveSwordHand(p, end), CarryToolDeg, Smooth(q));
            }

            return CarryToolDeg;
        }

        /// <summary>
        /// C-012: выпад тела при ударе мечом, тайлов. Кисть выше точки ядра на 0,22 т: удар на юг без добавки не доставал до обода
        /// зоны удара (кончик ≈ 0,9 т при 1,4), на север — перелетал. Выпад длиннее к югу (к зрителю), короче к северу.
        /// </summary>
        public static float SwordLunge(float dirY) => SwordLungeBase - SwordLungeSouth * Math.Min(0f, dirY) - SwordLungeNorth * Math.Max(0f, dirY);

        public const float SwordLungeBase = 0.22f;
        public const float SwordLungeSouth = 0.55f;
        public const float SwordLungeNorth = 0.1f;

        /// <summary>Кончик меча в верху замаха — на столько градусов за прицелом (над плечом назад).</summary>
        public const float SwordTipTopDeg = 115f;

        /// <summary>Верх замаха при прицеле на север — не дальше (иначе меч уходит за спину вниз).</summary>
        public const float SwordTipTopMaxDeg = 175f;

        /// <summary>Конец удара: кончик на столько градусов по другую сторону прицела (дуга ядра ±60° с запасом 5°).</summary>
        public const float SwordTipEndDeg = 65f;

        /// <summary>Угол руки, при котором кончик меча от точки ядра смотрит на <paramref name="tipDeg"/> (по взгляду), три шага уточнения.</summary>
        public static float SolveSwordHand(in Pose pose, float tipDeg)
        {
            Pose p = pose;
            bool right = p.FlipX != AnimParams.SpriteFacesRight;
            float hand = tipDeg - 47f; // остриё от рукояти — ≈ 47° к оси руки
            for (int i = 0; i < 4; i++)
            {
                p.HandDeg = hand;
                HeldPoint(p, HeldLook.Sword, SwordTipX, SwordTipY, out float x, out float y);
                float now = (float)(Math.Atan2(y, right ? x : -x) * 180.0 / Math.PI);
                float d = tipDeg - now;
                d -= 360f * (float)Math.Floor((d + 180f) / 360f);
                hand += d;
            }

            return hand;
        }

        /// <summary>
        /// Кончик клинка на значке меча, тайлов от опоры значка при масштабе 1: `TEMP_item_wooden_sword.png` 64 × 64, 128 px на
        /// тайл, опора — низ по центру (импорт), остриё — пиксель (53, 56) снизу слева. Тест сверяет размер и импорт с файлами.
        /// </summary>
        public const float SwordTipX = (53.5f - 32f) / 128f;
        public const float SwordTipY = 56.5f / 128f;

        /// <summary>Середина рукояти меча на значке (пиксель 13, 13), тайлов от опоры значка при масштабе 1.</summary>
        public const float SwordGripX = (13.5f - 32f) / 128f;
        public const float SwordGripY = 13.5f / 128f;

        /// <summary>Масштаб значка в руке (C-012: меч — свой, под досягаемость удара ядра). Тот же, что рисует <c>ActorRig</c>.</summary>
        public static float HeldScale(HeldLook look) =>
            look == HeldLook.Sword ? AnimParams.SwordScale : IsGripped(look) ? AnimParams.ToolScale : AnimParams.ItemScale;

        /// <summary>Сдвиг значка от кисти (в осях кисти, до масштаба): инструмент — за рукоять, прочее — в ладони.</summary>
        public static void IconLocal(HeldLook look, out float x, out float y)
        {
            if (look == HeldLook.Sword)
            {
                // C-012: кисть — на рукояти меча (пиксель 13, 13 значка), а не в стороне от неё: при крупном мече и руке, опущенной
                // в конце удара, общий сдвиг GripX/GripY (под опору в центре значка) отрывал меч от кисти на 0,4 т.
                x = -SwordGripX;
                y = -SwordGripY;
                return;
            }

            bool grip = IsGripped(look);
            x = grip ? AnimParams.GripX : 0f;
            y = grip ? AnimParams.GripY : AnimParams.PalmY;
        }

        /// <summary>
        /// Кисть в осях спрайта для взгляда вправо (C-010: при замахе за голову кисть поднимается к макушке). Тот же расчёт рисует
        /// <c>ActorRig.ApplyHand</c> (там x зеркалится по взгляду).
        /// </summary>
        public static void HandLocal(in Pose p, out float x, out float y)
        {
            float u = (p.HandDeg - AnimParams.OverheadLiftFromDeg) / (AnimParams.OverheadLiftFullDeg - AnimParams.OverheadLiftFromDeg);
            u = u < 0f ? 0f : u > 1f ? 1f : u;
            float lift = AnimParams.OverheadLiftY * u * u * (3f - 2f * u);
            x = AnimParams.HandX + p.HandReachX;
            y = AnimParams.HandY + p.HandReachY + lift;
        }

        /// <summary>
        /// Точка значка в руке (<paramref name="lx"/>, <paramref name="ly"/> — от опоры значка, тайлов при масштабе 1) относительно корня
        /// существа (точки ядра), экранные оси: та же цепочка, что в Unity у <c>ActorRig</c> — корень → спрайт (опора
        /// <see cref="SpriteFootY"/> + сдвиг позы, поворот, масштаб) → кисть (поворот, масштаб с зеркалом) → значок.
        /// </summary>
        public static void HeldPoint(in Pose p, HeldLook look, float lx, float ly, out float x, out float y)
        {
            bool right = p.FlipX != AnimParams.SpriteFacesRight;
            float sign = right ? 1f : -1f;
            float k = HeldScale(look);
            IconLocal(look, out float ix, out float iy);
            // значок → кисть: сдвиг значка, потом масштаб кисти (k·sign, k) и поворот HandDeg·sign
            float hx = (ix + lx) * k * sign, hy = (iy + ly) * k;
            double a = p.HandDeg * sign * Math.PI / 180.0;
            float rx = (float)(hx * Math.Cos(a) - hy * Math.Sin(a)), ry = (float)(hx * Math.Sin(a) + hy * Math.Cos(a));
            HandLocal(p, out float hlx, out float hly);
            float sx = hlx * sign + rx, sy = hly + ry;
            // кисть → спрайт: масштаб и поворот спрайта, сдвиг опоры и позы
            sx *= p.ScaleX;
            sy *= p.ScaleY;
            double b = p.RotationDeg * Math.PI / 180.0;
            x = (float)(sx * Math.Cos(b) - sy * Math.Sin(b)) + p.OffsetX;
            y = (float)(sx * Math.Sin(b) + sy * Math.Cos(b)) + SpriteFootY + p.OffsetY;
        }

        /// <summary>Опора спрайта существа ниже точки ядра, тайлов (<c>ActorFactory.SpriteFootY</c>, сцена <c>ProjectSetup</c>).</summary>
        public const float SpriteFootY = -0.4f;

        /// <summary>
        /// Жест стройки в доле <paramref name="u"/> (0…1): рука тянется к цели (угол цели <paramref name="aimDeg"/> от
        /// горизонтали вперёд) и возвращается; молот — короткий удар; тело подаётся вперёд.
        /// </summary>
        public static Swing BuildGesture(HeldLook look, float u, float aimDeg)
        {
            u = u < 0f ? 0f : u > 1f ? 1f : u;
            var s = new Swing();
            float bell = (float)Math.Sin(Math.PI * u); // туда и обратно
            if (look == HeldLook.Hammer)
            {
                // Замах до 0,45, удар к 0,6, возврат.
                float hit = HammerHitDeg + aimDeg * AimFollow;
                s.HandDeg = u < 0.45f ? Lerp(CarryToolDeg, HammerTopDeg, Smooth(u / 0.45f))
                    : u < 0.6f ? Lerp(HammerTopDeg, hit, (u - 0.45f) / 0.15f * ((u - 0.45f) / 0.15f))
                    : Lerp(hit, CarryToolDeg, Smooth((u - 0.6f) / 0.4f));
            }
            else
            {
                // Предмет протягивается к месту постановки: рука вытягивается к цели, предмет чуть поворачивается к ней.
                float carry = look == HeldLook.Item ? CarryItemDeg : CarryToolDeg;
                s.HandDeg = Lerp(carry, carry + aimDeg * 0.5f, bell);
                double a = aimDeg * Math.PI / 180.0;
                s.ReachX = 0.22f * bell * (float)Math.Cos(a);
                s.ReachY = 0.22f * bell * (float)Math.Sin(a);
            }

            s.LeanDeg = 9f * bell;
            s.Forward = 0.08f * bell;
            s.Squash = 0.02f * bell;
            return s;
        }

        /// <summary>Наклон подбора в доле <paramref name="u"/> (0…1): быстро вниз, медленнее вверх.</summary>
        public static Swing PickupBend(float u)
        {
            u = u < 0f ? 0f : u > 1f ? 1f : u;
            float e = u < 0.4f ? Smooth(u / 0.4f) : 1f - Smooth((u - 0.4f) / 0.6f);
            return new Swing { HandDeg = Lerp(CarryItemDeg, -60f, e), LeanDeg = 20f * e, Forward = 0.05f * e, Squash = 0.12f * e, ReachX = 0.06f * e, ReachY = -0.28f * e };
        }

        /// <summary>Отдача лука после выстрела, секунд (E3-2).</summary>
        public const float BowRecoilSec = 0.18f;

        /// <summary>Рука с луком вытянута к прицелу, тайлов.</summary>
        public const float BowReach = 0.16f;

        /// <summary>
        /// Поза натяжения лука (E3-2, `02` 4 «как играется»): лук вытянут к прицелу, корпус откидывается назад и чуть
        /// приседает по силе натяжения <paramref name="power"/> (0…1); на полном натяжении — мелкая дрожь (держит тетиву).
        /// Угол руки — добавка к оси прицела.
        /// </summary>
        public static Swing BowDraw(float power, float sec)
        {
            power = power < 0f ? 0f : power > 1f ? 1f : power;
            float ease = Smooth(Math.Min(1f, sec / 0.12f)); // подъём лука к прицелу за 0,12 с
            float tremble = power >= 1f ? (float)Math.Sin(sec * 46.0) * 0.6f : 0f;
            return new Swing
            {
                HandDeg = tremble,
                LeanDeg = -10f * power * ease,
                Forward = -0.1f * power * ease,
                Squash = 0.035f * power * ease,
                ReachX = BowReach * ease,
                ReachY = 0f,
            };
        }

        /// <summary>Отдача выстрела в доле <paramref name="u"/> (0…1) от <see cref="BowRecoilSec"/>: лук подскакивает, корпус возвращается.</summary>
        public static Swing BowRelease(float u)
        {
            u = u < 0f ? 0f : u > 1f ? 1f : u;
            float kick = (float)Math.Sin(Math.PI * u) * (1f - u * 0.5f);
            return new Swing
            {
                HandDeg = 10f * kick,
                LeanDeg = -10f * (1f - Smooth(u)) + 3f * kick,
                Forward = -0.1f * (1f - Smooth(u)) + 0.04f * kick,
                Squash = 0f,
                ReachX = BowReach * (1f - u * u),
                ReachY = 0.02f * kick,
            };
        }

        /// <summary>Угол цели над горизонталью «вперёд», градусов (−90…90): цель выше — плюс.</summary>
        public static float AimDeg(float dirX, float dirY)
        {
            if (Math.Abs(dirX) < 1e-6f && Math.Abs(dirY) < 1e-6f)
            {
                return 0f;
            }

            return (float)(Math.Atan2(dirY, Math.Abs(dirX)) * 180.0 / Math.PI);
        }

        static void TopHit(ActionKind kind, out float top, out float hit, out float lunge)
        {
            switch (kind)
            {
                case ActionKind.Mine:
                    top = MineTopDeg;
                    hit = MineHitDeg;
                    lunge = 0.12f;
                    return;
                case ActionKind.Hammer:
                    top = HammerTopDeg;
                    hit = HammerHitDeg;
                    lunge = 0.05f;
                    return;
                default:
                    top = ChopTopDeg;
                    hit = ChopHitDeg;
                    lunge = 0.08f;
                    return;
            }
        }

        static float Smooth(float x) => x * x * (3f - 2f * x);
        static float Lerp(float a, float b, float f) => a + (b - a) * f;
    }
}
