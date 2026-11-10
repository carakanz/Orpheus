using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Угрозы и решение удара врага по игроку (docs/07, 2.7 «Угроза», «Решение», 4.6 W; `02` 2.5, 15.1; карточка 0-029, шип).
    /// <para>
    /// Манекен (<see cref="MeleeDummy"/>, ключ отладки) замахивается по таймеру в кадрах сервера; замах — угроза в очереди
    /// (1 с) и сообщение «замах начат» всем в игре каждый такт замаха. Удар по игроку решается в кадре конца замаха
    /// C = s + d <b>его времени</b> (номер кадра ввода; без сдвига D_p и без опережения L — шип, вопрос `[док]` карточки):
    /// по позиции и увороту персонажа после кадра C, когда сервер шагнул этот кадр. Ждёт его не дольше W
    /// (<see cref="InputWaitFrames"/>, 6 кадров = 100 мс) после C по времени сервера; не дождался — решает по последнему
    /// известному: состояние после последнего шагнутого кадра, дошагнутое до C с последним направлением без нажатий
    /// (начатый рывок идёт дальше, новый не начинается).
    /// </para>
    /// <para>
    /// Время сервера в кадрах — <see cref="ServerFrame"/>: такт × 2 в нумерации часов клиента (0-025: «сейчас» клиента —
    /// такт заголовка снимка, отправленного после такта, т. е. <c>Tick + 1</c> во время <see cref="Poll"/>). Решение — в
    /// плане кадров такта (<see cref="Poll"/>): кадры такта шагаются копией персонажа той же функцией, что и в
    /// <c>Simulation.Step</c> (движение от сущностей не зависит), и состояние после кадра C берётся из копии.
    /// </para>
    /// В шипе удар решается по всем игрокам в игре (один бот); урона нет — только «попал / нет» (`Не трогать`).
    /// </summary>
    public sealed partial class NetServer
    {
        private readonly ThreatQueue _threats = new ThreatQueue();
        private MeleeDummy _dummy;
        private bool _hasDummy;

        /// <summary>Тип урона ближнего удара манекена — рубящий (`02` 6.9: 1), поле <c>MeleeResultMessage.Damage</c> и события такта.</summary>
        public const byte DamageTypeMelee = 1;

        /// <summary>
        /// Время сервера в кадрах ввода 60 Гц во время <see cref="Poll"/>: (такт + 1) × 2 — первый кадр такта, который
        /// шагается после него. Та же нумерация, что у кадров ввода клиента: «сейчас» клиента — такт заголовка снимка,
        /// отправленного после шага, плюс owd (0-025), т. е. в миг <see cref="Poll"/> — <c>Tick + 1</c>.
        /// </summary>
        public uint ServerFrame => unchecked((uint)(_sim.Tick + 1) * 2);

        /// <summary>
        /// Ожидание ввода W перед решением угрозы по игроку, кадров (docs/07, 4.6: ≤ 100 мс = 6). Решение — не позже чем через
        /// W кадров сервера после кадра конца замаха. 0 — «без ожидания ввода» (мутация шипа 0-029).
        /// </summary>
        public int InputWaitFrames { get; set; } = 6;

        /// <summary>Манекен включён.</summary>
        public bool HasDummy => _hasDummy;

        /// <summary>Манекен (копия состояния).</summary>
        public MeleeDummy Dummy => _dummy;

        /// <summary>Очередь угроз (для тестов и счётчиков).</summary>
        public ThreatQueue Threats => _threats;

        /// <summary>Замахов начато.</summary>
        public long SwingsStarted { get; private set; }

        /// <summary>Сообщений «замах начат» отправлено.</summary>
        public long SwingMessagesSent { get; private set; }

        /// <summary>Угроз решено (по игрокам).</summary>
        public long ThreatsResolved { get; private set; }

        /// <summary>Из них попаданий.</summary>
        public long ThreatHits { get; private set; }

        /// <summary>Из них решено по последнему известному (ввод кадра конца замаха не дождались за W).</summary>
        public long ThreatsByLastKnown { get; private set; }

        /// <summary>Наибольшее ожидание сервера после кадра конца замаха, кадров.</summary>
        public int MaxThreatWaitFrames { get; private set; }

        /// <summary>Сумма ожиданий, кадров (среднее = сумма / <see cref="ThreatsResolved"/>).</summary>
        public long ThreatWaitFramesTotal { get; private set; }

        /// <summary>Для тестов: удар по игроку (номер сущности) решён — в потоке такта.</summary>
        public Action<int, MeleeResultMessage>? MeleeResolved { get; set; }

        /// <summary>Включить манекен (ключ отладки, шип 0-029): замахи с кадра <see cref="MeleeDummy.NextSwingFrame"/>.</summary>
        public void SetDummy(in MeleeDummy dummy)
        {
            _dummy = dummy;
            _hasDummy = true;
            _threats.Clear();
        }

        /// <summary>Кадры сервера этого такта: замах манекена — угроза; старые угрозы — из очереди.</summary>
        private void StepDummy()
        {
            uint now = ServerFrame;
            _threats.Expire(now);
            if (!_hasDummy)
            {
                return;
            }

            for (uint f = now; f != now + MovementTick.FramesPerTick; f++)
            {
                if (_dummy.Step(f, out Threat swing))
                {
                    if (_threats.Add(swing))
                    {
                        SwingsStarted++;
                    }
                    else
                    {
                        Log?.Invoke(NetLogLevel.Error, $"очередь угроз полна ({_threats.Capacity}): замах {swing} потерян");
                    }
                }
            }
        }

        /// <summary>
        /// Угрозы по игроку <paramref name="p"/> после плана такта (<paramref name="n"/> кадров в <see cref="_frameBuf"/>,
        /// номера — в <see cref="_frameNum"/>): решить те, чей кадр конца замаха шагнут, и те, чьё ожидание кончается.
        /// </summary>
        private void ResolveThreats(Player p, int slot, int n)
        {
            if (_threats.Count == 0)
            {
                return;
            }

            int ti = 0;
            SkipUnresolved(p, ref ti);
            if (ti == _threats.Count)
            {
                return;
            }

            // Копия персонажа до кадров такта — шагается теми же кадрами, что шагнёт Simulation.Step
            // (с ударом: скорость 70 % в фазах мечу зависит от бита атаки и оружия в руках, 1-004).
            Mover m = _sim.Entities[slot].Mover;
            bool swordHeld = Items.KindOf(_sim.Entities[slot].Inv.Held) == Items.KindMelee;
            for (int k = 0; k < n && ti < _threats.Count; k++)
            {
                m.StepFrame(_sim.Shape, _frameBuf[k].Direction, _frameBuf[k].DodgePressed,
                            _frameBuf[k].AttackPressed && swordHeld, _frameBuf[k].Angle16, _sim.Solid);
                while (ti < _threats.Count && unchecked((int)(_frameNum[k] - ResolveFrame(p, _threats[ti]))) >= 0)
                {
                    Decide(p, _threats[ti], m, false, 0);
                    ti++;
                    SkipUnresolved(p, ref ti); // 1-007: телеграфы врагов с ИИ решалка пропускает
                }
            }

            // Ожидание: следующий такт ушёл бы за W после кадра решения — решить сейчас по последнему известному.
            uint nextTick = unchecked(ServerFrame + MovementTick.FramesPerTick);
            while (ti < _threats.Count && unchecked((int)(nextTick - ResolveFrame(p, _threats[ti]))) > InputWaitFrames)
            {
                ref readonly Threat t = ref _threats[ti];
                Mover known = _sim.Entities[slot].Mover;
                for (int k = 0; k < n; k++)
                {
                    known.StepFrame(_sim.Shape, _frameBuf[k].Direction, _frameBuf[k].DodgePressed,
                                    _frameBuf[k].AttackPressed && swordHeld, _frameBuf[k].Angle16, _sim.Solid);
                }

                int missing = p.HasApplied ? unchecked((int)(ResolveFrame(p, t) - p.LastApplied)) : 0;
                missing = Math.Max(0, Math.Min(missing, ThreatQueue.LifetimeFrames));
                for (int k = 0; k < missing; k++)
                {
                    // «Нет ввода»: последнее направление, кнопки отпущены (2.7) — удара в этих кадрах нет.
                    known.StepFrame(_sim.Shape, p.Stopped ? Input8.None : p.LastInput.Direction, false, false, 0, _sim.Solid);
                }

                Decide(p, t, known, true, missing);
                ti++;
                SkipUnresolved(p, ref ti); // 1-007: телеграфы врагов с ИИ решалка пропускает
            }
        }

        /// <summary>
        /// Кадр решения угрозы по игроку (2.7, 0-033): s + max(d, D_p + min(d; 0,3 с)) — время его персонажа. D_p игрока не
        /// меняется, пока по нему есть нерешённые угрозы (<see cref="UpdateLeadStats"/>), поэтому кадр решения постоянен.
        /// </summary>
        private static uint ResolveFrame(Player p, in Threat t) => ThreatTiming.ResolveFrame(t.StartFrame, t.WindupFrames, p.DelayFrames);

        /// <summary>По игроку есть нерешённая угроза.</summary>
        private bool HasPendingThreat(Player p)
        {
            for (int ti = 0; ti < _threats.Count; ti++)
            {
                if (Concerns(p, _threats[ti]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Замахи врагов с ИИ (карточка 1-007), после такта: удар решает сама симуляция (фаза «бой», `02` 6.1),
        /// поэтому угроза врага — только телеграф клиенту: сектор из <c>AttackOrigin</c>/<c>AttackFacingRaw</c>
        /// в очереди угроз и «замах начат» (<see cref="SendSwings"/>, повтор каждый такт замаха от потерь).
        /// Признак «замах начался этим тактом»: <c>AttackWindup == WindupTicks</c> — фаза боя сбрасывает его со следующего такта.
        /// Решалка 0-033 такие угрозы не трогает (<see cref="Concerns"/>): урона дважды не будет.
        /// </summary>
        private void StepEnemyThreats()
        {
            ReadOnlySpan<SimEntity> ents = _sim.Entities;
            uint start = unchecked((uint)_sim.Tick * 2); // первый кадр такта, в котором замах начался
            for (int i = 0; i < ents.Length; i++)
            {
                SimEntity e = ents[i];
                if (e.Kind != SimEntity.KindEnemy || e.Brain.Archetype == EnemyArchetype.None ||
                    e.AttackWindup != Simulation.WindupTicks)
                {
                    continue;
                }

                var sector = new MeleeSector(e.AttackOrigin, Angle16.FromRaw(e.AttackFacingRaw),
                    EnemyMelee.ReachUnits, EnemyMelee.HalfArcSteps);
                if (_threats.Add(new Threat(ThreatKind.Melee, e.Id, start, EnemyMelee.WindupFrames, sector)))
                {
                    SwingsStarted++;
                }
                else
                {
                    Log?.Invoke(NetLogLevel.Error, $"очередь угроз полна ({_threats.Capacity}): замах врага #{e.Id} потерян");
                }
            }
        }

        /// <summary>
        /// Угроза врага с ИИ (1-007): телеграф, удар решает симуляция. Атакующий — сущность с
        /// <c>Brain.Archetype ≠ 0</c>; манекен (1-001) живёт вне симуляции, у него мозга нет — его решает сеть.
        /// </summary>
        private bool IsEnemyAiThreat(in Threat t)
        {
            int slot = _sim.SlotOf(t.AttackerId);
            return slot >= 0 && _sim.Entities[slot].Brain.Archetype != EnemyArchetype.None;
        }

        /// <summary>
        /// Угроза ещё не решена по игроку и начата после его входа; угрозы врагов с ИИ (1-007) решалку
        /// не касаются — их удар уже решён симуляцией.
        /// </summary>
        private bool Concerns(Player p, in Threat t) =>
            !IsEnemyAiThreat(t) && unchecked((int)(t.StartFrame - p.ThreatsFrom)) >= 0 &&
            (!p.HasResolved || unchecked((int)(t.StartFrame - p.ResolvedStart)) > 0);

        /// <summary>Пропустить угрозы, которые этот игрок не касаются (чужие до входа, телеграфы врагов с ИИ).</summary>
        private void SkipUnresolved(Player p, ref int ti)
        {
            while (ti < _threats.Count && !Concerns(p, _threats[ti]))
            {
                ti++;
            }
        }

        private void Decide(Player p, in Threat t, in Mover state, bool byLastKnown, int missing)
        {
            bool hit = MeleeHit.Lands(_sim.Shape, t, state);
            int damage = 0;
            if (hit)
            {
                // 1-001: «попал» = удар решён сервером (конвейер 0-029/0-033 не перестроен): здоровье по
                // формуле 6.1, отбрасывание от центра сектора, прерывание и смерть — в Simulation.ApplyStrike.
                // Неуязвимость (уворота здесь уже нет — её снял MeleeHit; послераневая 0,4 с и труп) — урон 0.
                int raw = AttackRawOf(t.AttackerId);
                damage = _sim.ApplyStrike(p.EntityId, t.AttackerId, raw, t.Sector.Origin, knockback: true, DamageTypeMelee);
                if (damage > 0)
                {
                    int target = _sim.SlotOf(p.EntityId);
                    if (target >= 0 && !_sim.Entities[target].Alive)
                    {
                        BroadcastDeath(p.EntityId, t.AttackerId, damage, DamageTypeMelee); // событие такта уже разослано
                    }
                }
            }

            int waited = Math.Max(0, unchecked((int)(ServerFrame - ResolveFrame(p, t)))); // «сейчас» сервера в плане такта — ServerFrame
            p.ResolvedStart = t.StartFrame;
            p.HasResolved = true;
            ThreatsResolved++;
            ThreatWaitFramesTotal += waited;
            MaxThreatWaitFrames = Math.Max(MaxThreatWaitFrames, waited);
            if (hit)
            {
                ThreatHits++;
            }

            if (byLastKnown)
            {
                ThreatsByLastKnown++;
            }

            var msg = new MeleeResultMessage
            {
                AttackerId = t.AttackerId,
                StartFrame = t.StartFrame,
                Hit = hit,
                ByLastKnown = byLastKnown,
                WaitedFrames = (byte)Math.Min(waited, 255),
                MissingFrames = (byte)Math.Min(missing, 255),
                DecidedAtFrame = ServerFrame,
                Damage = (byte)Math.Min(255, damage),
            };
            int len = msg.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            MeleeResolved?.Invoke(p.EntityId, msg);
        }

        /// <summary>Сырой урон атакующего (1-001): у сущности в симуляции — её <c>AttackRaw</c>; манекен вне
        /// симуляции (нет такой сущности) — урон «Ближнего» (<see cref="Simulation.StrikeRawMelee"/>).</summary>
        private int AttackRawOf(int attackerId)
        {
            var ents = _sim.Entities;
            for (int i = 0; i < ents.Length; i++)
            {
                if (ents[i].Id == attackerId)
                {
                    return ents[i].AttackRaw;
                }
            }

            return Simulation.StrikeRawMelee;
        }

        /// <summary>После такта: «замах начат» каждому в игре по каждой угрозе, чей замах ещё идёт (повтор — от потерь).</summary>
        private void SendSwings()
        {
            uint now = ServerFrame;
            for (int ti = 0; ti < _threats.Count; ti++)
            {
                ref readonly Threat t = ref _threats[ti];
                if (unchecked((int)(t.ContactFrame - now)) < 0)
                {
                    continue;
                }

                MeleeSector sec = t.Sector;
                var msg = new SwingStartMessage
                {
                    AttackerId = t.AttackerId,
                    StartFrame = t.StartFrame,
                    WindupFrames = (byte)t.WindupFrames,
                    Origin = sec.Origin,
                    Axis = sec.Axis.Raw,
                    ReachUnits = (ushort)sec.ReachUnits,
                    HalfArcSteps = (ushort)sec.HalfArcSteps,
                };
                for (int pi = 0; pi < _players.Count; pi++)
                {
                    if (_players[pi].State == PlayerState.InGame)
                    {
                        msg.DelayFrames = (byte)_players[pi].DelayFrames; // своя D_p: кадр решения по получателю (0-033)
                        int len = msg.Write(_buf);
                        _transport.Send(_players[pi].Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                        SwingMessagesSent++;
                    }
                }
            }
        }
    }
}
