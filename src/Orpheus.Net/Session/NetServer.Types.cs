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
        private enum PlayerState : byte
        {
            AwaitingLogin,
            InGame,
        }

        private sealed class Player
        {
            public int Peer;
            public PlayerState State;
            public int EntityId;
            public string Name = string.Empty;
            /// <summary>Новейший принятый кадр (по номеру); <see cref="HasFrame"/> — принят хоть один.</summary>
            public uint Newest;
            public bool HasFrame;
            public uint LastSnapshotAck;

            /// <summary>Следующий кадр к шагу (время персонажа + 1); задаётся первым пакетом.</summary>
            public uint NextFrame;
            public bool HasNext;

            /// <summary>Последний шагнутый кадр — <c>AckFrame</c> снимка; <see cref="HasApplied"/> — шагнут хоть один.</summary>
            public uint LastApplied;
            public bool HasApplied;

            /// <summary>Ввод последнего шагнутого кадра (с ним сервер шагает сам, когда кадров нет).</summary>
            public InputFrame LastInput;

            /// <summary>Запас кадров: +2 за такт, не больше <see cref="CreditCap"/>; за такт тратится не больше <see cref="FramesPerTickLimit"/>.</summary>
            public int Credits;

            /// <summary>Такт, с которого ждёт дыра в номерах кадров; нет дыры — −1.</summary>
            public long HoleSinceTick = -1;

            /// <summary>Кольцо пришедших кадров по номеру (ячейка — номер mod <see cref="FrameRing"/>).</summary>
            public readonly uint[] RingFrame = new uint[FrameRing];
            public readonly bool[] RingValid = new bool[FrameRing];
            public readonly InputFrame[] RingInput = new InputFrame[FrameRing];

            /// <summary>Такт сервера, на котором пришёл последний новый кадр ввода (или вход).</summary>
            public long LastInputTick;

            /// <summary>Такт сервера, на котором открылось соединение (тайм-аут входа, 0-025).</summary>
            public long ConnectedTick;

            /// <summary>Ввод подменён пустым по обрыву; снимается новым кадром.</summary>
            public bool Stopped;

            /// <summary>Кольцо отправленных снимков для дельт (0-014); создаётся при входе в игру.</summary>
            public SentRing? Ring;

            /// <summary>Вошёл с токеном хозяина (0-024).</summary>
            public bool IsHost;

            /// <summary>Время последнего пришедшего сообщения (мкс по часам <see cref="Poll"/>; 0-024 — отсчёт 60 с хоста).</summary>
            public long LastHeardMicros;

            /// <summary>Обзор (4.4; 0-022); создаётся при входе в игру.</summary>
            public ViewState? View;

            /// <summary>Угрозы (0-029): решаются по игроку замахи, начатые с этого кадра сервера (вход в игру).</summary>
            public uint ThreatsFrom;

            /// <summary>Кадр начала последней решённой по игроку угрозы; <see cref="HasResolved"/> — решена хоть одна.</summary>
            public uint ResolvedStart;
            public bool HasResolved;

            /// <summary>Чанки у клиента и их ревизии (3.5, 3.7; 0-026); создаётся с первым окном.</summary>
            public HeldChunks? Held;

            /// <summary>
            /// Сумка, отправленная игроку (1-002): после такта сверяется с сумкой сущности — изменилась → «сумка» (тип 26).
            /// <see cref="HasSentInv"/> — слана хоть раз; <see cref="InvVersion"/> — версия в последнем сообщении.
            /// </summary>
            public Inventory SentInv;
            public bool HasSentInv;
            public ushort InvVersion;

            /// <summary>
            /// Опережение L из последнего нового пакета ввода, кадров (0-033, 2.7) — подсказка только для отчёта о запасе (им клиент
            /// правит своё же L); D_p от него не зависит (з. 2 проверки 0-033).
            /// </summary>
            public int Lead;

            /// <summary>
            /// Задержка прихода кадров o = кадр сервера при разборе − номер новейшего кадра пакета + L пакета (owd вверх + ожидание
            /// опроса такта − ошибка часов) — по новым пакетам за 2 с (0-033). L пакета здесь — чтобы запас W + L − o₉₉ отзывался
            /// на смену L сразу, а не через окно 2 с (без него L перелетал вдвое при входе).
            /// </summary>
            public readonly FrameWindow Arrival = new FrameWindow(OwdWindow);

            /// <summary>
            /// Задержка показа по эху снимков (з. 2 проверки 0-033): номер кадра клиента в первом пакете, подтвердившем снимок S,
            /// минус кадр сервера 2·S, когда снимок (и «замах начат» того же такта) ушёл. Это RTT, замеренный сервером (от
            /// отправки снимка до пакета с его подтверждением), минус (кадр сервера − номер кадра) того же пакета: owd вниз + ошибка
            /// часов + L, кадр сервера при разборе сокращается. Поле L сюда не входит. По новым подтверждениям за 2 с (снимок —
            /// раз в такт).
            /// </summary>
            public readonly FrameWindow Echo = new FrameWindow(EchoWindow);

            /// <summary>99-й процентиль o за окно, кадров (считается раз в такт).</summary>
            public int OwdP99;

            /// <summary>99-й процентиль задержки показа по эху за окно, кадров.</summary>
            public int EchoP99;

            /// <summary>«Запас прихода кадров» при нынешнем L — отчёт в снимке: W + L − o₉₉, кадров (1-й процентиль запаса).</summary>
            public int Margin;

            /// <summary>
            /// Задержка показа D_p (≤ 30 кадров) — по эху снимков, без поля L, с которой решаются удары по игроку и которая уходит
            /// ему в «замах начат». Пересчитывается, только когда по игроку нет нерешённых угроз: кадр решения угрозы не меняется.
            /// </summary>
            public int DelayFrames;

            /// <summary>«Липкие» нажатия (2.7 «Опоздавший кадр»): кнопки опоздавших и выброшенных кадров — в первый шагнутый кадр.</summary>
            public ushort Sticky;

            /// <summary>
            /// Часы клиента — на старой шкале (з. 1 проверки 0-033): после «часов» (фриз сервера) и до перенумерации назад кадры
            /// клиента впереди времени сервера на длину фриза — это не опережение L, и потолок кадров «из будущего» их не держит
            /// (шагаются по запасу, как до 0-033). Иначе персонаж стоит до перенумерации, а предсказание идёт — лишняя сверка.
            /// </summary>
            public bool ClockStale;

            /// <summary>Такт заголовка первого снимка после «часов» — пакет, подтвердивший его, уже знает новую шкалу.</summary>
            public uint ClockStaleSnapshot;

            /// <summary>Такт последнего снимка этому игроку (0-036: в паузе снимок такта паузы — только тем, кто его не получал).</summary>
            public uint SentTick;
            public bool HasSentTick;

            /// <summary>
            /// После паузы (0-036): на сколько кадров время персонажа отставало от кадра сервера на снятии. Кадры, пришедшие в
            /// паузе, шагаются по два за такт с тем же отставанием, а не залпом: иначе очередь кончится раньше, чем дойдут
            /// кадры, отправленные после снятия, — сервер шагнул бы сам. 0 — как обычно; обнуляется, когда очередь паузы
            /// кончилась.
            /// </summary>
            public int ResumeLag;

            /// <summary>Правки тайлов (0-028): копание, ведро постановок, ждущие ответа; создаётся с первым запросом.</summary>
            public EditState? Edits;
        }

        /// <summary>
        /// Обзор игрока (docs/07, 4.4; 0-022): окно чанков 6 × 4 с выходом 8 × 6 (<see cref="ChunkWindow"/>, то же, что
        /// у клиента, 3.5) и сущности, бывшие в обзоре прошлого такта, — по номеру, отсортированы.
        /// </summary>
        private sealed class ViewState
        {
            /// <summary>Центр блока окна (<see cref="ChunkWindow.BlockCenter"/>); окно пересчитывается, когда он сменился.</summary>
            public ChunkCoord Center;
            public bool HasWindow;
            public ChunkCoord[] Window = new ChunkCoord[ChunkWindow.MaxChunks];
            public ChunkCoord[] WindowNext = new ChunkCoord[ChunkWindow.MaxChunks];
            public int WindowCount;

            /// <summary>Блок выхода 8 × 6 вокруг центра: отсюда берутся сущности обзора.</summary>
            public readonly ChunkCoord[] Keep = new ChunkCoord[ChunkWindow.MaxChunks];
            public int KeepCount;

            /// <summary>Чанк блока 8 × 6 (по номеру в <see cref="Keep"/>) — в окне: вход в обзор.</summary>
            public readonly bool[] KeepInWindow = new bool[ChunkWindow.MaxChunks];

            /// <summary>
            /// Номера чужих сущностей в обзоре прошлого такта; сортируются при первом поиске (<see cref="IdsSorted"/>) —
            /// пока все сущности обзора в окне, поиска нет и сортировки тоже.
            /// </summary>
            public readonly int[] Ids = new int[Snapshot.MaxEntities];
            public int IdCount;
            public bool IdsSorted;
        }

        /// <summary>
        /// Кольцо «что клиент имеет»: состояние (сущности), отправленное игроку на такте tick.
        /// Дельта строится от записи с тактом = подтверждение клиента (<c>LastSnapshot</c> ввода).
        /// </summary>
        private sealed class SentRing
        {
            public const int Depth = 32; // 1 с снимков 30 Гц: клиент, подтверждая, всегда внутри

            public readonly uint[] Tick = new uint[Depth];
            public readonly bool[] Valid = new bool[Depth];
            public readonly int[] Count = new int[Depth];
            public readonly SnapshotEntity[][] Ents = new SnapshotEntity[Depth][];
            public int Head;

            public SentRing()
            {
                for (int i = 0; i < Depth; i++)
                {
                    Ents[i] = new SnapshotEntity[Snapshot.MaxEntities];
                }
            }

            /// <summary>Индекс записи с тактом <paramref name="tick"/>; нет (клиент подтвердил старое) — −1.</summary>
            public int Find(uint tick)
            {
                for (int i = 0; i < Depth; i++)
                {
                    if (Valid[i] && Tick[i] == tick)
                    {
                        return i;
                    }
                }

                return -1;
            }

            public void Push(uint tick, SnapshotEntity[] src, int count)
            {
                Tick[Head] = tick;
                Valid[Head] = true;
                Count[Head] = count;
                Array.Copy(src, Ents[Head], count);
                Head = (Head + 1) % Depth;
            }
        }

        /// <summary>Скользящее окно значений в кадрах: кольцо ячеек и гистограмма — процентиль за проход по ячейкам без выделений.</summary>
        public sealed class FrameWindow
        {
            private readonly byte[] _ring;
            private readonly short[] _hist = new short[OwdBins];
            private int _head;

            public FrameWindow(int size) => _ring = new byte[size];

            /// <summary>Значений в окне.</summary>
            public int Count { get; private set; }

            public void Record(int value)
            {
                int bin = Math.Max(0, Math.Min(OwdBins - 1, value - OwdMin));
                if (Count == _ring.Length)
                {
                    _hist[_ring[_head]]--;
                }
                else
                {
                    Count++;
                }

                _ring[_head] = (byte)bin;
                _hist[bin]++;
                _head = (_head + 1) % _ring.Length;
            }

            /// <summary>99-й процентиль (1 % окна сверху, не меньше одного значения); пустое окно — <paramref name="empty"/>.</summary>
            public int P99(int empty)
            {
                if (Count == 0)
                {
                    return empty;
                }

                int need = Math.Max(1, (Count + 99) / 100);
                int bin = OwdBins - 1;
                for (int seen = 0; bin > 0; bin--)
                {
                    seen += _hist[bin];
                    if (seen >= need)
                    {
                        break;
                    }
                }

                return bin + OwdMin;
            }
        }
    }
}
