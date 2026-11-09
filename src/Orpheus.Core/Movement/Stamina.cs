namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Выносливость персонажа (docs/02, 2.3; карточка 1-003) — состояние на <see cref="Mover.Stamina"/>,
    /// шагается тем же кадром 60 Гц, что и уворот (<see cref="Mover.StepFrame(in Geometry.MapShape, Input8, bool, TileSolid)"/>):
    /// сервер и предсказание клиента считают её одной функцией, расхождений по ней быть не может.
    /// <list type="bullet">
    /// <item>Запас 0…<see cref="Max"/> (100). Уворот стоит <see cref="DodgeCost"/> (25) — трата в кадре начала рывка.</item>
    /// <item>Восстановление 20/с: +1 за <see cref="RegenFramesPerPoint"/> (3) кадра, пауза
    /// <see cref="RegenPauseFrames"/> (30 кадров = 0,5 с) после последней траты: первый пункт — на кадре
    /// <c>трата + 31</c>, дальше каждые 3. Отсчёт паузы — от последней траты (2.3: «через 0,5 с после последней траты»).</item>
    /// <item>Во время рывка восстановление прерывается (2.2, строка «во время рывка»); во время отбрасывания — нет
    /// (2.2, строка «во время отбрасывания»): <see cref="Tick"/> шагает и там, и там.</item>
    /// <item>При <see cref="Value"/> &lt; <see cref="DodgeCost"/> уворот не начинается, а на нажатие при готовом
    /// увороте и направлении — ставится <see cref="Clicked"/> («щёлк», 2.3): звук и мигание полоски — клиент (1-019).</item>
    /// </list>
    /// Только целые; 4 байта. Идеальный уворот (+15, 2.6) — «потом», не здесь.
    /// </summary>
    public struct Stamina
    {
        /// <summary>Полный запас: 100 (2.3).</summary>
        public const byte Max = 100;

        /// <summary>Цена уворота: 25 (2.2).</summary>
        public const byte DodgeCost = 25;

        /// <summary>Кадров на 1 пункт восстановления: 60 кадров/с ÷ 20 пунктов/с = 3 (2.3).</summary>
        public const int RegenFramesPerPoint = 3;

        /// <summary>Кадров паузы восстановления после траты: 0,5 с × 60 = 30 (2.3). Первый пункт — на следующем кадре.</summary>
        public const int RegenPauseFrames = 30;

        /// <summary>Предел счётчика <see cref="SinceSpend"/> (дальше не растёт; реген при нём продолжается).</summary>
        public const int SinceSpendCap = 255;

        /// <summary>Запас, 0…<see cref="Max"/>.</summary>
        public byte Value;

        /// <summary>
        /// Кадров с последней траты (кадр траты — 0; +1 за кадр, предел <see cref="SinceSpendCap"/>).
        /// 255 — траты не было (свежий персонаж регенит сразу, запас и так полон).
        /// </summary>
        public byte SinceSpend;

        /// <summary>Аккумулятор восстановления, 0…<see cref="RegenFramesPerPoint"/> − 1. На трате ставится 2 — первый пункт на 31-м кадре.</summary>
        public byte RegenAcc;

        /// <summary>
        /// «Щёлк» в этом кадре: нажатие уворота при готовом увороте и направлении, но выносливости &lt; 25 (2.3).
        /// Ставится на кадр, гасится в начале следующего (<see cref="Tick"/>); клиент по нему — звук/мигание (1-019).
        /// </summary>
        public bool Clicked;

        /// <summary>Свежий персонаж: запас полон, трат не было.</summary>
        public static Stamina Full() => new Stamina
        {
            Value = Max,
            SinceSpend = (byte)SinceSpendCap,
            RegenAcc = 0,
            Clicked = false,
        };

        /// <summary>Хватает ли на уворот (25).</summary>
        public bool CanDodge => Value >= DodgeCost;

        /// <summary>
        /// Потратить 25 на начало рывка (вызывать только при <see cref="CanDodge"/>): запас −25,
        /// пауза регена с этого кадра, аккумулятор с фазой «первый пункт через 31 кадр».
        /// </summary>
        public void Spend()
        {
            Value -= DodgeCost;
            SinceSpend = 0;
            RegenAcc = (byte)(RegenFramesPerPoint - 1);
        }

        /// <summary>
        /// Шаг кадра 60 Гц: погасить <see cref="Clicked"/> предыдущего кадра, +1 к <see cref="SinceSpend"/>,
        /// и если пауза после траты истекла (кадр траты + 31 и дальше) и рывка нет — +1 пункт за каждые
        /// <see cref="RegenFramesPerPoint"/> кадров, до <see cref="Max"/>.
        /// </summary>
        public void Tick(bool dashing)
        {
            Clicked = false;
            if (SinceSpend < SinceSpendCap)
            {
                SinceSpend++;
            }

            if (dashing || Value >= Max || SinceSpend <= RegenPauseFrames)
            {
                return;
            }

            RegenAcc++;
            if (RegenAcc >= RegenFramesPerPoint)
            {
                RegenAcc = 0;
                Value++;
            }
        }

        public override string ToString() => $"{Value}/100";
    }
}
