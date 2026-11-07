using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот-уворачивающийся шипа 0-029 (сценарий <c>dodge</c>; docs/07, 2.13 п. 3; `02` 2.7 п. 5): стоит дома (там, где
    /// вошёл, — в секторе манекена), видит замах по сообщению «замах начат» и жмёт уворот вбок от оси удара в случайный
    /// кадр окна [конец показанного замаха − 0,2 с; конец − 17 мс] на своём экране; после рывка идёт домой.
    /// <para>
    /// Экран бота — его кадры ввода (номер = оценка кадра сервера + опережение L, 0-025, 0-033). Замах показывается
    /// «сейчас» (`07` 2.9: анимация сжимается в оставшееся время) честно: с кадра прихода сообщения A и не короче
    /// min(d; 0,3 с) — конец показа E = max(C, A + min(d; 18)), где C = s + max(d, D_p + min(d; 18)) — кадр решения
    /// сервера по боту (D_p — из сообщения). E > C — показ длиннее решения (<see cref="ShownPastResolve"/>), нажатие
    /// может опоздать. Пришло позже начала окна — окно с кадра прихода; позже E − 1 — «не успел увидеть» (не в счёт).
    /// </para>
    /// Учёт по каждому замаху: нажат ли вовремя, дома ли, итог сервера (засчитан / попал), решён ли по последнему
    /// известному, сколько кадров ввода не хватило и дошло ли само нажатие к решению, сколько сервер ждал.
    /// </summary>
    public sealed class DodgeBot
    {
        /// <summary>Имя сценария.</summary>
        public const string Name = "dodge";

        /// <summary>Окно нажатия: с E − 12 кадров (0,2 с) …</summary>
        public const int EarliestLeadFrames = 12;

        /// <summary>… по E − 1 кадр (17 мс, один кадр ввода).</summary>
        public const int LatestLeadFrames = 1;

        /// <summary>Дома — ближе этого по каждой оси (полшага бега на кадр ≈ 26 unit).</summary>
        public const int HomeToleranceUnits = 32;

        private const int RecordRing = 16;

        private struct Record
        {
            public bool Used;
            public int Attacker;
            public uint Start;
            public uint Contact;
            public uint SeenFrame;
            public bool TooLate;
            public bool Pressed;
            public uint PressFrame;
            public bool HomeAtPress;
            public bool Resolved;
        }

        private readonly MapShape _map;
        private readonly Record[] _records = new Record[RecordRing];
        private DetRandom _rng;
        private int _next;
        private WorldPos _home;
        private bool _hasHome;
        private int _dodges;
        private uint _lastFrame;
        private bool _hasFrame;
        private uint _cooldownUntil;

        public DodgeBot(MapShape map, ulong seed, int latestLeadFrames = LatestLeadFrames)
        {
            LatestLead = Math.Max(LatestLeadFrames, Math.Min(EarliestLeadFrames, latestLeadFrames));
            _map = map;
            _rng = new DetRandom(seed);
        }

        /// <summary>Конец окна нажатия до конца показа, кадров: 1 (17 мс, 2.13 п. 3) или 6 (0,1 с, 2.13 п. 4 — RTT 600).</summary>
        public int LatestLead { get; }

        /// <summary>Замахов увидено (без повторов).</summary>
        public int SwingsSeen { get; private set; }

        /// <summary>Увидено позже E − 1: нажать вовремя нельзя (не в счёт).</summary>
        public int TooLate { get; private set; }

        /// <summary>Нажато вовремя (в окне).</summary>
        public int PressedInTime { get; private set; }

        /// <summary>Нажато вовремя, дома, итог пришёл: в счёт.</summary>
        public int Scored { get; private set; }

        /// <summary>Из счёта — засчитан (удар мимо).</summary>
        public int Counted { get; private set; }

        /// <summary>Из счёта — попадание.</summary>
        public int Hits { get; private set; }

        /// <summary>Из счёта — решено по последнему известному.</summary>
        public int ByLastKnown { get; private set; }

        /// <summary>Из счёта — нажатие не дошло к решению (кадр нажатия новее последнего шагнутого).</summary>
        public int PressNotArrived { get; private set; }

        /// <summary>Из счёта — наибольшая нехватка кадров ввода в миг решения.</summary>
        public int MaxMissingFrames { get; private set; }

        /// <summary>Из счёта — сумма нехваток (среднее — на <see cref="ByLastKnown"/>).</summary>
        public long MissingFramesTotal { get; private set; }

        /// <summary>Наибольшее ожидание сервера (по всем итогам), кадров.</summary>
        public int MaxWaitedFrames { get; private set; }

        /// <summary>Итог пришёл, а нажатия не было или бот был не дома (не в счёт).</summary>
        public int NotScored { get; private set; }

        /// <summary>Итог по замаху, которого бот не видел.</summary>
        public int UnknownResults { get; private set; }

        /// <summary>Конец показа позже кадра решения сервера (D_p меньше настоящей задержки показа): замахов.</summary>
        public int ShownPastResolve { get; private set; }

        /// <summary>Насколько конец показа позже кадра решения, наибольшее, кадров.</summary>
        public int MaxShownPastFrames { get; private set; }

        /// <summary>Наибольшая D_p из сообщений, кадров.</summary>
        public int MaxDelayFrames { get; private set; }

        /// <summary>Наименьший показ замаха (E − A), кадров; нет — 0.</summary>
        public int MinShownFrames { get; private set; }

        /// <summary>Попадания по номеру кадра нажатия до конца замаха (C − P, 0…15): где теряются.</summary>
        public int[] HitsByLead { get; } = new int[16];

        /// <summary>Нажатия в счёт по C − P.</summary>
        public int[] ScoredByLead { get; } = new int[16];

        /// <summary>Пришло «замах начат» (с повторами).</summary>
        public void OnSwing(in SwingStartMessage m)
        {
            for (int i = 0; i < RecordRing; i++)
            {
                if (_records[i].Used && _records[i].Attacker == m.AttackerId && _records[i].Start == m.StartFrame)
                {
                    return; // повтор
                }
            }

            ref Record r = ref _records[_next];
            _next = (_next + 1) % RecordRing;
            uint resolve = m.ResolveFrame;
            r = new Record { Used = true, Attacker = m.AttackerId, Start = m.StartFrame, Contact = resolve };
            SwingsSeen++;
            uint seen = _hasFrame ? _lastFrame + 1 : m.StartFrame;
            r.SeenFrame = seen;
            uint shownEnd = seen + (uint)Math.Min((int)m.WindupFrames, ThreatTiming.MinShowFrames); // 2.9: не короче min(d; 0,3 с)
            if ((int)(shownEnd - resolve) > 0)
            {
                ShownPastResolve++;
                MaxShownPastFrames = Math.Max(MaxShownPastFrames, (int)(shownEnd - resolve));
            }
            else
            {
                shownEnd = resolve;
            }

            MaxDelayFrames = Math.Max(MaxDelayFrames, m.DelayFrames);
            int shown = (int)(shownEnd - seen);
            MinShownFrames = SwingsSeen == 1 ? shown : Math.Min(MinShownFrames, shown);
            uint lo = shownEnd - EarliestLeadFrames;
            uint hi = shownEnd - (uint)LatestLead;
            if ((int)(seen - lo) > 0)
            {
                lo = seen;
            }

            if ((int)(hi - lo) < 0)
            {
                r.TooLate = true;
                TooLate++;
                return;
            }

            r.PressFrame = lo + (uint)_rng.NextInt((int)(hi - lo) + 1);
        }

        /// <summary>Пришло «удар решён».</summary>
        public void OnResult(in MeleeResultMessage m)
        {
            MaxWaitedFrames = Math.Max(MaxWaitedFrames, m.WaitedFrames);
            for (int i = 0; i < RecordRing; i++)
            {
                ref Record r = ref _records[i];
                if (!r.Used || r.Attacker != m.AttackerId || r.Start != m.StartFrame || r.Resolved)
                {
                    continue;
                }

                r.Resolved = true;
                if (r.TooLate)
                {
                    return;
                }

                if (!r.Pressed || !r.HomeAtPress)
                {
                    NotScored++;
                    return;
                }

                Scored++;
                int lead = Math.Min(15, Math.Max(0, (int)(r.Contact - r.PressFrame)));
                ScoredByLead[lead]++;
                if (m.Hit)
                {
                    Hits++;
                    HitsByLead[lead]++;
                }
                else
                {
                    Counted++;
                }

                if (m.ByLastKnown)
                {
                    ByLastKnown++;
                    MissingFramesTotal += m.MissingFrames;
                    MaxMissingFrames = Math.Max(MaxMissingFrames, m.MissingFrames);
                    if ((int)(r.PressFrame - (r.Contact - m.MissingFrames)) > 0)
                    {
                        PressNotArrived++;
                    }
                }

                return;
            }

            UnknownResults++;
        }

        /// <summary>
        /// Ввод кадра <paramref name="frame"/> (номер, с которым он уйдёт); <paramref name="self"/> — где персонаж на своём
        /// экране (предсказание), null — неизвестно.
        /// </summary>
        public InputFrame Next(uint frame, WorldPos? self)
        {
            _lastFrame = frame;
            _hasFrame = true;
            if (self.HasValue && !_hasHome)
            {
                _home = self.Value;
                _hasHome = true;
            }

            Int2 toHome = self.HasValue && _hasHome ? Torus.Delta(_map, self.Value, _home) : default;
            bool home = Math.Abs(toHome.X) <= HomeToleranceUnits && Math.Abs(toHome.Y) <= HomeToleranceUnits;

            for (int i = 0; i < RecordRing; i++)
            {
                ref Record r = ref _records[i];
                if (!r.Used || r.TooLate || r.Pressed || r.PressFrame != frame)
                {
                    continue;
                }

                r.Pressed = true;
                r.HomeAtPress = home && (int)(frame - _cooldownUntil) >= 0;
                PressedInTime++;
                _cooldownUntil = frame + Dodge.DashFrames + Dodge.CooldownFrames;
                // Вбок от оси удара (манекен бьёт по горизонтали — вбок это север / юг), по очереди.
                Input8 dir = (_dodges++ & 1) == 0 ? Input8.North : Input8.South;
                return new InputFrame(dir, InputButtons.Dodge);
            }

            if (!home && (int)(frame - (_cooldownUntil - Dodge.CooldownFrames)) >= 0)
            {
                int hx = Math.Abs(toHome.X) > HomeToleranceUnits / 2 ? Math.Sign(toHome.X) : 0;
                int hy = Math.Abs(toHome.Y) > HomeToleranceUnits / 2 ? Math.Sign(toHome.Y) : 0;
                return new InputFrame(Input8.FromKeys(hx, hy));
            }

            return InputFrame.None;
        }

        /// <summary>Строка итога.</summary>
        public string Summary() =>
            $"засчитано {Counted} из {Scored} (попаданий {Hits}, по последнему известному {ByLastKnown}, нажатие не дошло {PressNotArrived}, " +
            $"показ ≥ {MinShownFrames} кадров, D ≤ {MaxDelayFrames}, показ позже решения {ShownPastResolve} (≤ {MaxShownPastFrames}), " +
            $"нехватка кадров ≤ {MaxMissingFrames}, ожидание ≤ {MaxWaitedFrames}); замахов {SwingsSeen}, не успел увидеть {TooLate}, не в счёт {NotScored}, неизвестных итогов {UnknownResults}";
    }
}
