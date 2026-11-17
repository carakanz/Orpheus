namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Вход лука на кадр (E3-2): биты — лук в руках, ЛКМ зажата, стрелы в сумке. Считает владелец кадра
    /// (такт сервера, предсказание клиента, решалка угроз) одной функцией <see cref="Of"/> — одни входы, одна фаза.
    /// </summary>
    public static class BowInput
    {
        /// <summary>В руках лук (вид <c>Items.KindRanged</c>).</summary>
        public const byte InHand = 1;

        /// <summary>ЛКМ зажата в этом кадре (бит удара кадра ввода).</summary>
        public const byte Pressed = 2;

        /// <summary>В сумке есть хотя бы одна стрела — без неё натяжение не начинается (отказ, подсказка клиента).</summary>
        public const byte HasArrows = 4;

        /// <summary>Вход лука кадра: <paramref name="bowHeld"/> — лук в руках, <paramref name="pressed"/> — бит удара, <paramref name="arrows"/> — стрелы есть.</summary>
        public static byte Of(bool bowHeld, bool pressed, bool arrows) =>
            !bowHeld ? (byte)0 : (byte)(InHand | (pressed ? Pressed : 0) | (arrows ? HasArrows : 0));
    }

    /// <summary>
    /// Автомат лука персонажа (E3-2, `02` 4.1 строка «Лук», 1.2 «удержание — натяжение», 2.2 «отмена действий»):
    /// покой → натяжение (ЛКМ держится; бег 60 %) → отпускание — выстрел → отход 12 кадров (0,2 с) → покой. Кадры 60 Гц.
    /// <para>
    /// Сила — кадры натяжения: урон и скорость линейно от <see cref="MinDrawFrames"/> (0,2 с) до <see cref="FullDrawFrames"/>
    /// (0,6 с). C-031 (`BRIEF` 11n «зажатие выстрела — бесконечная стрельба»): ЛКМ держится до полного натяжения — выстрел
    /// сам, отход, и пока ЛКМ держится и есть стрелы — натяжение снова (темп 0,6 + 0,2 с = 1,25/с, как раньше на полном
    /// натяжении); отпустил раньше — выстрел на отпускании. Отпустил раньше 0,2 с — тетива дотягивается сама
    /// и стрела уходит на 0,2 с: темп выстрелов не больше 1,25/с на полном натяжении (0,6 + 0,2 с) и урон в секунду
    /// одинаков на любом натяжении (`02` 4.1: 1,25 B). Ось — прицел кадра, пока ЛКМ держится (C-012: от середины тела).
    /// </para>
    /// <para>
    /// Выстрел — флаг <see cref="Fired"/> с силой, осью и точкой (позиция кадра выстрела): его забирает владелец кадра —
    /// такт создаёт снаряд и снимает стрелу, предсказание — свою картинку стрелы — и снимает флаг. Без стрел натяжение
    /// не начинается; лук убран из рук в натяжении — сброс без выстрела, стрела не тратится (`02` 4.3); уворот
    /// сбрасывает натяжение (2.2), ЛКМ, удержанная после рывка, — натяжение заново.
    /// </para>
    /// </summary>
    public struct BowAction
    {
        /// <summary>Покой.</summary>
        public const byte PhaseIdle = 0;

        /// <summary>Натяжение: бег 60 % (`02` 4.1).</summary>
        public const byte PhaseDraw = 1;

        /// <summary>Отход после выстрела: 12 кадров (0,2 с, `02` 4.1).</summary>
        public const byte PhaseRecover = 2;

        /// <summary>Наименьшее натяжение — 12 кадров (0,2 с): урон 0,5 B, 18 т/с.</summary>
        public const int MinDrawFrames = 12;

        /// <summary>Полное натяжение — 36 кадров (0,6 с): урон 1,0 B, 28 т/с.</summary>
        public const int FullDrawFrames = 36;

        /// <summary>Отход после выстрела, кадров (0,2 с).</summary>
        public const int RecoverFrames = 12;

        /// <summary>Скорость бега при натяжении, тысячных: 60 % (`02` 4.1, 1.2 «натяжение замедляет»).</summary>
        public const int DrawSpeedX1000 = 600;

        /// <summary>Фаза.</summary>
        public byte Phase;

        /// <summary>Натяжение: кадров натяжения (1…<see cref="FullDrawFrames"/>); отход: кадров до покоя.</summary>
        public byte Frames;

        /// <summary>ЛКМ отпущена раньше <see cref="MinDrawFrames"/> — выстрел, как только натяжение дойдёт до 0,2 с.</summary>
        public bool Released;

        /// <summary>Ось натяжения — прицел последнего кадра с зажатой ЛКМ (u16).</summary>
        public ushort AimRaw;

        /// <summary>Выстрел в этом кадре: флаг держится, пока владелец кадра его не заберёт (<see cref="TakeShot"/>).</summary>
        public bool Fired;

        /// <summary>Натяжение выстрела, кадров (<see cref="MinDrawFrames"/>…<see cref="FullDrawFrames"/>).</summary>
        public byte ShotDraw;

        /// <summary>Ось выстрела (u16).</summary>
        public ushort ShotAim;

        /// <summary>Точка выстрела — позиция кадра выстрела, X (1/256 тайла).</summary>
        public int ShotX;

        /// <summary>Точка выстрела, Y.</summary>
        public int ShotY;

        /// <summary>Лук занят: натяжение или отход.</summary>
        public readonly bool Active => Phase != PhaseIdle;

        /// <summary>Натяжение идёт.</summary>
        public readonly bool Drawing => Phase == PhaseDraw;

        /// <summary>Скорость бега в этой фазе, тысячных: натяжение — 60 %, иначе 100 %.</summary>
        public readonly int SpeedX1000 => Phase == PhaseDraw ? DrawSpeedX1000 : 1000;

        /// <summary>Доля натяжения 0…256 (0 — 0,2 с и меньше, 256 — полное 0,6 с): для силы выстрела и кольца у курсора.</summary>
        public static int PowerQ8(int drawFrames)
        {
            int d = drawFrames < MinDrawFrames ? MinDrawFrames : drawFrames > FullDrawFrames ? FullDrawFrames : drawFrames;
            return (d - MinDrawFrames) * 256 / (FullDrawFrames - MinDrawFrames);
        }

        /// <summary>
        /// Кадр 60 Гц. <paramref name="input"/> — биты <see cref="BowInput"/>; <paramref name="aimRaw"/> — прицел кадра;
        /// (<paramref name="x"/>, <paramref name="y"/>) — позиция кадра (точка выстрела).
        /// </summary>
        public void StepFrame(byte input, ushort aimRaw, int x, int y)
        {
            bool inHand = (input & BowInput.InHand) != 0;
            bool pressed = (input & BowInput.Pressed) != 0;
            switch (Phase)
            {
                case PhaseIdle:
                    if (inHand && pressed && (input & BowInput.HasArrows) != 0)
                    {
                        Phase = PhaseDraw;
                        Frames = 1;
                        Released = false;
                        AimRaw = aimRaw;
                    }

                    return;

                case PhaseDraw:
                    if (!inHand)
                    {
                        Reset(); // смена предмета — без выстрела, стрела цела (`02` 4.3)
                        return;
                    }

                    if (pressed && !Released)
                    {
                        AimRaw = aimRaw;
                        if (Frames < FullDrawFrames)
                        {
                            Frames++;
                        }

                        if (Frames >= FullDrawFrames)
                        {
                            Fire(x, y); // C-031: зажатая ЛКМ — непрерывная стрельба: полное натяжение — выстрел, отход, натяжение снова
                        }

                        return;
                    }

                    Released = true;
                    if (Frames < MinDrawFrames)
                    {
                        Frames++; // отпустил раньше 0,2 с — тетива дотягивается
                    }

                    if (Frames >= MinDrawFrames)
                    {
                        Fire(x, y);
                    }

                    return;

                case PhaseRecover:
                    if (Frames > 0)
                    {
                        Frames--;
                    }

                    if (Frames == 0)
                    {
                        Phase = PhaseIdle;
                    }

                    return;
            }
        }

        private void Fire(int x, int y)
        {
            Fired = true;
            ShotDraw = Frames;
            ShotAim = AimRaw;
            ShotX = x;
            ShotY = y;
            Phase = PhaseRecover;
            Frames = RecoverFrames;
            Released = false;
        }

        /// <summary>Забрать выстрел кадра: true — был (флаг снят).</summary>
        public bool TakeShot()
        {
            bool f = Fired;
            Fired = false;
            return f;
        }

        /// <summary>Уворот (`02` 2.2): натяжение сброшено без выстрела; отход идёт дальше.</summary>
        public void CancelByDash()
        {
            if (Phase == PhaseDraw)
            {
                Reset();
            }
        }

        private void Reset()
        {
            Phase = PhaseIdle;
            Frames = 0;
            Released = false;
        }

        public readonly override string ToString() => $"bow {Phase} f{Frames}{(Released ? " rel" : "")}{(Fired ? " fired" : "")}";
    }
}
