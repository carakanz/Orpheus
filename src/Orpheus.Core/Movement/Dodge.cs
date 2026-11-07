namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Уворот персонажа (docs/02, 2.2 и 2.4; карточка 0-029) — состояние на <see cref="Mover.Dodge"/>, шаг — покадровый
    /// (<see cref="Mover.StepFrame(in Geometry.MapShape, Input8, bool, TileSolid)"/>, кадр ввода 60 Гц). Сервер и предсказание
    /// клиента шагают его одной функцией по одним кадрам — рывок переигрывается при сверке так же, как бег.
    /// <list type="bullet">
    /// <item>Рывок: 3,0 тайла за 0,25 с (12 т/с) = <see cref="DashFrames"/> кадров по <see cref="SpeedX10"/> unit × 10
    /// (51,2 unit за кадр; диагональ — 362 на ось, как у бега), в направлении ввода кадра нажатия; ввод в рывке не действует.</item>
    /// <item>Неуязвимость: первые <see cref="InvulnFrames"/> кадров от начала (0,00–0,20 с) — <see cref="Invulnerable"/>;
    /// от стены не укорачивается (2.4).</item>
    /// <item>Стена: кадр, в котором рывок упёрся по всем осям своего направления, — последний (2.4: «упёрся — остановился»,
    /// время пропорционально); диагональный рывок вдоль стены скользит, как бег.</item>
    /// <item>Откат: <see cref="CooldownFrames"/> кадров после последнего кадра рывка (0,40 с); самое частое — раз в
    /// <see cref="DashFrames"/> + <see cref="CooldownFrames"/> = 39 кадров (0,65 с).</item>
    /// </list>
    /// Выносливость, идеальный уворот, отмена действий и статусы — не здесь (не в шипе). Только целые; 4 байта.
    /// </summary>
    public struct Dodge
    {
        /// <summary>Кадров рывка: 0,25 с × 60 = 15.</summary>
        public const int DashFrames = 15;

        /// <summary>Кадров неуязвимости от начала рывка: 0,20 с × 60 = 12 (80 % рывка).</summary>
        public const int InvulnFrames = 12;

        /// <summary>Кадров отката после конца рывка: 0,40 с × 60 = 24.</summary>
        public const int CooldownFrames = 24;

        /// <summary>Скорость рывка на прямой ось за кадр в unit × 10: 12 т/с ÷ 60 = 0,2 т = 51,2 unit → 512; 15 кадров — 768 unit = 3,0 т.</summary>
        public const int SpeedX10 = 512;

        /// <summary>Предел счётчика <see cref="Elapsed"/> (дальше не растёт).</summary>
        public const int ElapsedCap = 255;

        /// <summary>
        /// Кадров от начала последнего рывка, включая кадр нажатия: после кадра нажатия — 1, дальше +1 за кадр до
        /// <see cref="ElapsedCap"/>. 0 — уворота ещё не было.
        /// </summary>
        public byte Elapsed;

        /// <summary>Кадров рывка осталось (0 — рывка нет).</summary>
        public byte DashLeft;

        /// <summary>Кадров отката осталось (идёт после рывка; 0 — уворот доступен, если нет рывка).</summary>
        public byte Cooldown;

        /// <summary>Направление рывка: код 1…8 (<see cref="Input8.DirectionIndex"/> + 1), 0 — не было.</summary>
        public byte Direction;

        /// <summary>Идёт рывок.</summary>
        public bool Dashing => DashLeft != 0;

        /// <summary>Неуязвим после этого кадра: с кадра нажатия по <see cref="InvulnFrames"/>-й включительно.</summary>
        public bool Invulnerable => Elapsed >= 1 && Elapsed <= InvulnFrames;

        /// <summary>Можно начать уворот в этом кадре: нет рывка и отката.</summary>
        public bool Ready => DashLeft == 0 && Cooldown == 0;

        /// <summary>Направление рывка как <see cref="Input8"/>.</summary>
        public Input8 DashDirection => Direction == 0 ? Input8.None : Input8.FromIndex(Direction - 1);

        public override string ToString() =>
            Dashing ? $"рывок {DashDirection} ещё {DashLeft}, с начала {Elapsed}" : Cooldown != 0 ? $"откат {Cooldown}" : "готов";
    }
}
