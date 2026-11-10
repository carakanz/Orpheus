namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Автомат действия персонажа «удар мечом» (1-004, `02` 3.2 строка «Меч», 1.2 буфер нажатий):
    /// покой → замах 9 кадров → удар 9 → отход 21 (после третьего удара 31), всё в кадрах ввода 60 Гц.
    /// Серия из трёх ударов ×1,0 / ×1,0 / ×1,3: индекс продвигается в конце отхода; пауза больше
    /// <see cref="IdleResetFrames"/> кадров в покое — серия сначала. Нажатие за ≤ <see cref="BufferWindowFrames"/>
    /// кадров до конца отхода продолжает серию без паузы; удержание кнопки — повтор.
    /// <para>
    /// Ось удара (<see cref="AimRaw"/>) фиксируется началом замаха, начало удара (<see cref="OriginX"/>,
    /// <see cref="OriginY"/>) — переходом «замах → удар» (как сектор врага фиксирован началом замаха, 1-001):
    /// сервер и предсказание считают удар по этим координатам, шов тора — в них же.
    /// </para>
    /// Шагает только покадрово (<see cref="Mover.StepFrame(in MapShape, Input8, bool, bool, ushort, Movement.TileSolid)"/>);
    /// без выделений, только целые.
    /// </summary>
    public struct MeleeAction
    {
        /// <summary>Покой: удара нет.</summary>
        public const byte PhaseIdle = 0;

        /// <summary>Замах: 9 кадров (0,15 с), скорость бега 70 %, отмена уворотом — удар пропадает.</summary>
        public const byte PhaseWindup = 1;

        /// <summary>Удар: 9 кадров (0,15 с) окно поражения; сектор фиксирован началом фазы.</summary>
        public const byte PhaseStrike = 2;

        /// <summary>Отход: 21 кадр (0,35 с), после третьего удара — 31 (×1,5).</summary>
        public const byte PhaseRecover = 3;

        /// <summary>Замах, кадров.</summary>
        public const int WindupFrames = 9;

        /// <summary>Удар, кадров.</summary>
        public const int StrikeFrames = 9;

        /// <summary>Отход, кадров (первый и второй удар).</summary>
        public const int RecoverFrames = 21;

        /// <summary>Отход после третьего удара: ×1,5 = 31 кадр (0,52 с).</summary>
        public const int LastRecoverFrames = 31;

        /// <summary>Буфер нажатия (`02` 1.2): не раньше 12 кадров (0,2 с) до конца отхода.</summary>
        public const int BufferWindowFrames = 12;

        /// <summary>Пауза в покое, после которой серия начинается сначала (`02` 3.2: 0,8 с).</summary>
        public const int IdleResetFrames = 48;

        /// <summary>Ударов в серии.</summary>
        public const int SeriesSize = 3;

        /// <summary>Фаза (<see cref="PhaseIdle"/>…<see cref="PhaseRecover"/>).</summary>
        public byte Phase;

        /// <summary>Кадров до конца фазы; 0 в покое.</summary>
        public byte FramesLeft;

        /// <summary>Индекс удара в серии: 0…2 (урон и отбрасывание — по нему).</summary>
        public byte Index;

        /// <summary>Нажатие в буфере: было в окне ≤ <see cref="BufferWindowFrames"/> кадров отхода — начало следующего удара в конце отхода.</summary>
        public bool Buffered;

        /// <summary>Кадров покоя с конца отхода (счётчик паузы серии, до <see cref="IdleResetFrames"/>).</summary>
        public byte IdleFrames;

        /// <summary>Текущий удар решён (урон нанесён): ставит решалка сервера, сбрасывает начало удара.</summary>
        public bool Resolved;

        /// <summary>Ось удара, шагов угла u16: фиксирована началом замаха (прицел кадра нажатия).</summary>
        public ushort AimRaw;

        /// <summary>Начало удара, X в 1/256 тайла: фиксировано началом фазы «удар».</summary>
        public int OriginX;

        /// <summary>Начало удара, Y в 1/256 тайла: фиксировано началом фазы «удар».</summary>
        public int OriginY;

        /// <summary>Удар идёт: замах, удар или отход.</summary>
        public readonly bool Active => Phase != PhaseIdle;

        /// <summary>Скорость бега в этой фазе, десятых долей: замах и удар — 70 % (`02` 3.2), остальные — 100 %.</summary>
        public readonly int SpeedX10 =>
            Phase == PhaseWindup || Phase == PhaseStrike ? 700 : 1000;

        /// <summary>
        /// Один кадр 60 Гц: фаза идёт — ubывает кадр, на границе переход
        /// (замах → удар: фиксация начала удара вызывающим; удар → отход: 21/31 кадра по индексу;
        /// отход → покой, с буфером — сразу следующий замах серии). Покой и нажатие — начало замаха
        /// (ось — прицел кадра). Без нажатия в покое растёт счётчик паузы серии.
        /// </summary>
        public void StepFrame(bool attackPressed, ushort aimRaw)
        {
            if (Phase == PhaseIdle)
            {
                if (attackPressed)
                {
                    if (IdleFrames >= IdleResetFrames)
                    {
                        Index = 0; // пауза > 0,8 с — серия сначала
                    }

                    Phase = PhaseWindup;
                    FramesLeft = WindupFrames;
                    AimRaw = aimRaw;
                    Buffered = false;
                    Resolved = false;
                    IdleFrames = 0;
                }
                else if (IdleFrames < byte.MaxValue)
                {
                    IdleFrames++;
                }

                return;
            }

            if (FramesLeft > 0)
            {
                FramesLeft--;
            }

            if (FramesLeft != 0)
            {
                // Буфер: нажатие в окне последних 12 кадров отхода (`02` 1.2/3.2: за 0,2 с — серия, раньше — нет).
                if (Phase == PhaseRecover && attackPressed && FramesLeft <= BufferWindowFrames)
                {
                    Buffered = true;
                }

                return;
            }

            switch (Phase)
            {
                case PhaseWindup:
                    Phase = PhaseStrike;
                    FramesLeft = StrikeFrames;
                    Resolved = false;
                    break;

                case PhaseStrike:
                    Phase = PhaseRecover;
                    FramesLeft = (byte)(Index == SeriesSize - 1 ? LastRecoverFrames : RecoverFrames);
                    break;

                case PhaseRecover:
                    if (Buffered)
                    {
                        Buffered = false;
                        Index = (byte)((Index + 1) % SeriesSize);
                        Phase = PhaseWindup;
                        FramesLeft = WindupFrames;
                        Resolved = false;
                        IdleFrames = 0;
                    }
                    else
                    {
                        Phase = PhaseIdle;
                        Index = (byte)((Index + 1) % SeriesSize);
                        IdleFrames = 0;
                    }

                    break;
            }
        }

        /// <summary>
        /// Отмена уворотом (`02` 2.2 «Отмена действий», 2.7 п. 3; флаг рывка 1-003): из замаха — удар пропадает,
        /// серия сбрасывается; из удара — удар досчитывается (фаза идёт, решалка нанесёт урон от зафиксированного
        /// начала); из отхода — удар засчитан, серия продолжается по индексу.
        /// </summary>
        public void CancelByDash()
        {
            switch (Phase)
            {
                case PhaseWindup:
                    Phase = PhaseIdle;
                    FramesLeft = 0;
                    Index = 0;
                    Buffered = false;
                    IdleFrames = 0;
                    break;

                case PhaseRecover:
                    Phase = PhaseIdle;
                    FramesLeft = 0;
                    Buffered = false;
                    IdleFrames = 0;
                    break;
            }
        }

        /// <summary>Прерывание удара полученным уроном (`02` 6.6, хук 1-004): замах гаснет, серия сбрасывается.</summary>
        public void Cancel()
        {
            if (Phase == PhaseWindup)
            {
                Phase = PhaseIdle;
                FramesLeft = 0;
                Index = 0;
                Buffered = false;
                IdleFrames = 0;
            }
        }

        public readonly override string ToString() => $"melee {Phase} f{FramesLeft} i{Index}{(Buffered ? " buf" : "")}";
    }
}
