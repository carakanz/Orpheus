using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// P-002: partial NetServer - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Тайм-аут входа: соединение без входа дольше <see cref="LoginTimeoutTicks"/> тактов — закрыть (место не держит).</summary>
        private void DropSilentConnections()
        {
            long tick = _sim.Tick;
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                Player p = _players[i];
                if (p.State == PlayerState.AwaitingLogin && tick - p.ConnectedTick >= LoginTimeoutTicks)
                {
                    _players.RemoveAt(i);
                    LoginTimeouts++;
                    Log?.Invoke(NetLogLevel.Warn, $"соединение {p.Peer}: нет входа {LoginTimeoutTicks / Simulation.TickRate} с — закрыто");
                    _transport.Disconnect(p.Peer, ReadOnlySpan<byte>.Empty);
                }
            }
        }

        private static void Advance(Player p, InputFrame input)
        {
            p.LastApplied = p.NextFrame;
            p.HasApplied = true;
            p.LastInput = input;
            p.NextFrame++;
        }

        private void OnInput(Player p, in InputMessage m)
        {
            // Перенумерация назад (0-025): пакет отправлен после всех принятых (подтверждает более новый снимок), а номера
            // не новее принятых — клиент переставил часы назад (фриз сервера: за 2 с он нумеровал вперёд). Обычные пакеты
            // так не бывают: номера клиента растут вместе с подтверждённым снимком. Очередь — заново от номеров пакета
            // (все 4 кадра: перед новым клиент шлёт его копии, предсказание шагает так же).
            if (p.HasFrame && (int)(m.LastSnapshot - p.LastSnapshotAck) > 0 && (int)(m.LastFrame - p.Newest) <= 0)
            {
                Array.Clear(p.RingValid, 0, FrameRing);
                p.NextFrame = m.FirstFrame;
                p.HasNext = true;
                p.Newest = m.FirstFrame - 1;
                p.HoleSinceTick = -1;
                p.ClockStale = false;
                InputRebases++;
            }

            bool isNew = !p.HasFrame || (int)(m.LastFrame - p.Newest) > 0;
            if (!p.HasNext)
            {
                p.NextFrame = m.FirstFrame;
                p.HasNext = true;
            }

            if ((int)(m.LastFrame - p.NextFrame) >= FrameRing)
            {
                // Клиент ушёл вперёд больше кольца (ускорение, долгий обрыв): ждавшие кадры — в отбой, время — к пакету.
                for (uint f = p.NextFrame; (int)(f - m.FirstFrame) < 0 && (int)(f - p.NextFrame) < FrameRing; f++)
                {
                    int i = (int)(f % FrameRing);
                    if (p.RingValid[i] && p.RingFrame[i] == f)
                    {
                        p.RingValid[i] = false;
                        FramesOverflow++;
                    }
                }

                p.NextFrame = m.FirstFrame;
                p.HoleSinceTick = -1;
            }

            // Все четыре кадра пакета — в кольцо по номеру (что уже есть — не трогаем: первый пришедший и есть кадр).
            for (int k = 0; k < InputMessage.FrameCount; k++)
            {
                uint f = m.FirstFrame + (uint)k;
                if ((int)(f - p.NextFrame) < 0)
                {
                    // 2.7 «Опоздавший кадр»: номер уже шагнут сервером сам или перескочен, а сам кадр не приходил — его нажатия
                    // «липкие» (в первый ещё не шагнутый кадр). Ячейка помечается номером: копии в следующих пакетах — не новые.
                    int li = (int)(f % FrameRing);
                    if (!p.RingValid[li] && p.RingFrame[li] != f && (int)(p.NextFrame - f) <= StickyHorizonFrames)
                    {
                        p.RingFrame[li] = f;
                        FramesLate++;
                        ushort buttons = m[k].ToInputFrame().Buttons;
                        if (buttons != 0)
                        {
                            p.Sticky |= buttons;
                            StickyPresses++;
                        }
                    }

                    continue;
                }

                int i = (int)(f % FrameRing);
                if (!(p.RingValid[i] && p.RingFrame[i] == f))
                {
                    p.RingFrame[i] = f;
                    p.RingValid[i] = true;
                    p.RingInput[i] = m[k].ToInputFrame();
                }
            }

            if (!isNew)
            {
                InputsDropped++; // старый, дубль или переставленный — новых кадров нет (дыры он закрывает)
                return;
            }

            // 2.7 «Опоздавший кадр»: все кадры пакета уже шагнуты сервером сам — не выбрасывается, его направление действует
            // с первого ещё не шагнутого кадра (нажатия уже «липкие» — выше). Ячейка и прицел — состояние, а не нажатие:
            // берём из кадра (1-002).
            if ((int)(m.LastFrame - p.NextFrame) < 0)
            {
                int i = (int)(p.NextFrame % FrameRing);
                if (!(p.RingValid[i] && p.RingFrame[i] == p.NextFrame))
                {
                    p.RingFrame[i] = p.NextFrame;
                    p.RingValid[i] = true;
                    InputFrame late = m.Frame3.ToInputFrame();
                    p.RingInput[i] = new InputFrame(late.Direction, 0, late.Angle16, late.Slot);
                }
            }

            if (p.ClockStale && (int)(m.LastSnapshot - p.ClockStaleSnapshot) >= 0
                && (int)(m.LastFrame - ServerFrame) <= InputMessage.MaxLeadFrames + MovementTick.FramesPerTick)
            {
                p.ClockStale = false; // клиент видел новую шкалу, номера на ней (перенумерация не понадобилась)
            }

            // Задержка прихода (0-033): o = кадр сервера сейчас − новейший кадр + L. Эхо снимка (з. 2 проверки 0-033): первый пакет,
            // подтвердивший более новый снимок S, — номер кадра клиента в миг, когда снимок S (ушедший в кадре сервера 2·S) у
            // него. Подтверждение снимка, которого сервер ещё не слал, — не эхо; на старой шкале часов (после фриза) — тоже.
            // В паузе кадр сервера стоит: приход и эхо пакетов, бывших в пути, мерили бы паузу, а не связь (0-036).
            p.Lead = m.Lead;
            if (!Paused)
            {
                p.Arrival.Record(unchecked((int)(ServerFrame - m.LastFrame)) + m.Lead);
                if ((int)(m.LastSnapshot - p.LastSnapshotAck) > 0 && (int)(m.LastSnapshot - (uint)_sim.Tick) <= 0 && !p.ClockStale)
                {
                    p.Echo.Record(unchecked((int)(m.LastFrame - m.LastSnapshot * 2)));
                }
            }
            p.Newest = m.LastFrame;
            p.HasFrame = true;
            p.LastSnapshotAck = m.LastSnapshot;
            p.LastInputTick = _sim.Tick;
            p.Stopped = false;
            InputsAccepted++;
        }

        /// <summary>
        /// 2.13 п. 6: от игрока нет нового кадра <see cref="InputStaleTicks"/> тактов — пустой ввод на ближайший такт.
        /// До первого кадра после входа ввод и так пустой — стопа нет и в <see cref="InputStops"/> он не считается.
        /// Зовётся после приёма пакетов такта: если сервер сам стоял (заморозка, п. 5), пакеты, пришедшие за это
        /// время, уже приняты и стопа не будет.
        /// </summary>
        private void StopStaleInputs()
        {
            long tick = _sim.Tick;
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State != PlayerState.InGame || !p.HasFrame || p.Stopped || tick - p.LastInputTick < InputStaleTicks)
                {
                    continue;
                }

                p.Stopped = true;
                InputStops++;
                if (p.HasNext && (int)(p.Newest - p.NextFrame) >= 0)
                {
                    TrimTo(p, p.Newest + 1, false); // стоп 2.13 п. 6: ждавшие кадры не дошагиваются после стопа
                }

                p.Sticky = 0;
                _sim.SetInput(p.EntityId, InputFrame.None);
                p.LastInput = InputFrame.None;
            }
        }

        /// <summary>
        /// Выбросить ждущие кадры до <paramref name="next"/> (не шагать): время персонажа — к нему. <paramref name="keepPresses"/> —
        /// нажатия выброшенных кадров «липкие» (2.7: ни одно нажатие не теряется); при стопе по обрыву — нет.
        /// </summary>
        private void TrimTo(Player p, uint next, bool keepPresses)
        {
            for (uint f = p.NextFrame; (int)(next - f) > 0; f++)
            {
                int i = (int)(f % FrameRing);
                if (p.RingValid[i] && p.RingFrame[i] == f)
                {
                    p.RingValid[i] = false;
                    if (keepPresses && p.RingInput[i].Buttons != 0)
                    {
                        p.Sticky |= p.RingInput[i].Buttons;
                        StickyPresses++;
                    }
                }

                FramesTrimmed++;
            }

            p.NextFrame = next;
            p.HoleSinceTick = -1;
        }

        /// <summary>
        /// Кадры ввода на ближайший такт каждому игроку (0-019): пришедшие по порядку номеров, не больше запаса; дыра — ждёт,
        /// потом перескок; кадров нет дольше W — сервер шагает сам с последним вводом (до стопа <see cref="InputStaleTicks"/>).
        /// </summary>
        private void PlanFrames()
        {
            long tick = _sim.Tick;
            StepDummy();
            for (int pi = 0; pi < _players.Count; pi++)
            {
                Player p = _players[pi];
                if (p.State != PlayerState.InGame)
                {
                    continue;
                }

                int slot = _sim.SlotOf(p.EntityId);
                if (slot < 0)
                {
                    if (p.Edits != null)
                    {
                        DropDig(p, p.Edits); // 0-028: персонажа нет — копанию «прервано»
                    }

                    continue;
                }

                p.Credits = Math.Min(p.Credits + MovementTick.FramesPerTick, CreditCap);
                int limit = Math.Min(p.Credits, FramesPerTickLimit);
                int n = 0;
                // 2.7: не дальше кадров этого такта; часы клиента на старой шкале (после фриза) — без потолка, по запасу.
                uint cap = p.ClockStale ? unchecked(p.NextFrame + FrameRing) : unchecked(ServerFrame + MovementTick.FramesPerTick - (uint)p.ResumeLag);
                while (p.HasNext && n < limit)
                {
                    uint c = p.NextFrame;
                    if ((int)(c - cap) >= 0)
                    {
                        break; // кадр «из будущего» (опережение L): ждёт своего такта
                    }

                    int i = (int)(c % FrameRing);
                    if (p.RingValid[i] && p.RingFrame[i] == c)
                    {
                        p.RingValid[i] = false;
                        InputFrame taken = TakeSticky(p, p.RingInput[i]);
                        _frameBuf[n] = taken;
                        Advance(p, taken);
                        _frameNum[n++] = p.LastApplied;
                        FramesApplied++;
                        p.HoleSinceTick = -1;
                        continue;
                    }

                    if (!p.HasFrame || (int)(p.Newest - c) < 0)
                    {
                        break; // кадра ещё нет — время персонажа ждёт
                    }

                    // Дыра: кадр c не пришёл, следующие — да. Ждём переставленный пакет, потом перескок к ближайшему.
                    if (p.HoleSinceTick < 0)
                    {
                        p.HoleSinceTick = tick;
                    }

                    if (tick - p.HoleSinceTick < HoleWaitTicks)
                    {
                        break;
                    }

                    uint next = c + 1;
                    while ((int)(p.Newest - next) > 0 && !(p.RingValid[next % FrameRing] && p.RingFrame[next % FrameRing] == next))
                    {
                        next++;
                    }

                    FramesSkipped += next - c;
                    p.NextFrame = next;
                    p.HoleSinceTick = -1;
                }

                long gap = tick - p.LastInputTick;
                if (n == 0 && p.HasApplied && !p.Stopped && gap >= SelfStepAfterTicks && gap < InputStaleTicks)
                {
                    var self = new InputFrame(p.LastInput.Direction); // 2.7 «Нет ввода»: последнее направление, кнопки отпущены
                    while (n < MovementTick.FramesPerTick && n < limit)
                    {
                        InputFrame taken = TakeSticky(p, self);
                        _frameBuf[n] = taken;
                        Advance(p, taken);
                        _frameNum[n++] = p.LastApplied;
                        FramesSelfStepped++;
                    }
                }

                // Очередь не копится (проверка 0-019, з. 1): запас +2 за такт при непустой очереди уходит весь, и всё сверх
                // догона осталось бы навсегда (задержка-пачка, рывок клиента, дрейф часов) — время персонажа отстало бы
                // больше W, а стоп 0-015 растянулся бы. После плана такта ждущих кадров больше MaxBacklogFrames — старейшие
                // перескакиваются со счётчиком. Ускорения это не даёт: кадры только выбрасываются.
                // Кадры «из будущего» (опережение L) — не очередь: считаются только применимые в этом такте.
                uint usable = (int)(p.Newest - cap) >= 0 ? cap - 1 : p.Newest;
                if (p.HasNext && p.HasFrame && p.HoleSinceTick < 0 && (int)(usable - p.NextFrame) >= MaxBacklogFrames)
                {
                    TrimTo(p, usable + 1 - MaxBacklogFrames, true);
                }

                if (n > MovementTick.FramesPerTick)
                {
                    BurstTicks++;
                }

                if (p.ResumeLag > 0 && (!p.HasFrame || (int)(p.Newest - cap) < 0))
                {
                    p.ResumeLag = 0; // 0-036: очередь паузы кончилась — дальше кадры приходят к сроку, как до паузы
                }

                p.Credits -= n;
                UpdateLeadStats(p);
                ResolveThreats(p, slot, n);
                AdvanceEdits(p, slot, n); // 0-028: копание по кадрам такта
                _sim.SetFramesAt(slot, new ReadOnlySpan<InputFrame>(_frameBuf, 0, n));
            }
        }

        /// <summary>
        /// Раз в такт (0-033, 2.7): o₉₉ за 2 с; «запас прихода кадров» W + L − o₉₉ (кадр f доходит к решению, если разобран не позже
        /// кадра сервера f + W: решение «по последнему известному» — в первом опросе после f + W − 2, кадры этого опроса уже
        /// применены); D_p — 99-й процентиль эха снимков (≤ 30), когда по игроку нет нерешённых угроз.
        /// </summary>
        private void UpdateLeadStats(Player p)
        {
            p.OwdP99 = p.Arrival.P99(0);
            p.EchoP99 = p.Echo.P99(0);
            p.Margin = InputWaitFrames + p.Lead - p.OwdP99; // поле L — только здесь: отчёт, по которому клиент правит своё L
            if (!HasPendingThreat(p))
            {
                // Добавки нет (прежняя +1 кадр «отправка после такта» ушла): кадр начала s — 2·S или 2·S + 1, «замах начат»
                // уходит со снимком S, а эхо считает от 2·S.
                p.DelayFrames = Math.Max(0, Math.Min(ThreatTiming.MaxDelayFrames, p.EchoP99));
            }
        }

        /// <summary>Кадр к шагу с «липкими» нажатиями (2.7): кнопки опоздавших кадров — в первый шагнутый кадр, один раз.</summary>
        private static InputFrame TakeSticky(Player p, InputFrame frame)
        {
            if (p.Sticky == 0)
            {
                return frame;
            }

            var withSticky = new InputFrame(frame.Direction, (ushort)(frame.Buttons | p.Sticky));
            p.Sticky = 0;
            return withSticky;
        }

    }
}
