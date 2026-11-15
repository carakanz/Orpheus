using System;
using Orpheus.Core.Sim;

namespace Orpheus.Net.Session
{
    /// <summary>Куда уходят заявки на сумку (E2-2): клиент сети (<see cref="NetClient"/>) или показ без сервера.</summary>
    public interface IBagRequests
    {
        /// <summary>Переместить ячейку в другую (перенос, доукладка, обмен — решает сервер).</summary>
        bool RequestBagMove(int from, int to);

        /// <summary>Отделить часть стопки в другую ячейку.</summary>
        bool RequestBagSplit(int from, int to, int count);

        /// <summary>Выбросить из ячейки на землю (0 — всю стопку).</summary>
        bool RequestBagDrop(int cell, int count = 0);
    }

    public sealed partial class NetClient : IBagRequests
    {
    }

    /// <summary>Что сделал щелчок по окну сумки (<see cref="BagHand"/>).</summary>
    public enum BagClick : byte
    {
        /// <summary>Ничего (пустая ячейка, мёртвый, не влезает).</summary>
        None = 0,

        /// <summary>Стопка (или половина) взята в руку — заявки нет.</summary>
        Taken = 1,

        /// <summary>Рука опустела без заявки (положил обратно, отмена).</summary>
        Cancelled = 2,

        /// <summary>Ушла заявка «переместить».</summary>
        Moved = 3,

        /// <summary>Ушла заявка «разделить».</summary>
        Split = 4,

        /// <summary>Ушла заявка «выбросить».</summary>
        Dropped = 5,
    }

    /// <summary>Чем кончился «Взять всё» / «Положить всё» (C-008).</summary>
    public enum MoveAllStop : byte
    {
        /// <summary>Ещё идёт или не начинался.</summary>
        None = 0,

        /// <summary>Перенесено всё.</summary>
        Done,

        /// <summary>Осталось, но некуда положить.</summary>
        NoRoom,

        /// <summary>Сундук закрылся посреди переноса.</summary>
        ChestClosed,

        /// <summary>Персонаж умер.</summary>
        Dead,

        /// <summary>Сервер не ответил на пачку несколько раз подряд (пауза, лимит занят) или заявка не ушла.</summary>
        NotAccepted,
    }

    /// <summary>
    /// «Рука» окна сумки (E2-2, шаг 2.2): общий путь заявок для окна игрока и для ботов / тестов. Правил игры здесь
    /// нет — сумку меняет только сервер. У сервера нет ячейки курсора, поэтому «взять» — состояние клиента: стопка
    /// остаётся в своей ячейке, рука помнит ячейку и количество; заявка уходит, когда предмет кладут или выбрасывают:
    /// целая стопка — <see cref="BagOp.Move"/>, часть — <see cref="BagOp.Split"/>, мимо окна — <see cref="BagOp.Drop"/>.
    /// После заявки рука пуста: результат — сумка сервера (сообщение 26), предсказания нет.
    /// <para>
    /// E2-5: открыт сундук (<see cref="SyncChest"/>) — ячейки <see cref="ChestBase"/>…+29 — его ячейки; перенос сумка ↔
    /// сундук теми же заявками (номера ячеек 40…69 уходят серверу как есть), Shift+ЛКМ перекидывает между сумкой и сундуком.
    /// </para>
    /// </summary>
    public sealed class BagHand
    {
        private readonly IBagRequests _requests;
        private Inventory _bag;
        private Inventory _chest;

        /// <summary>Первая ячейка открытого сундука в номерах руки (E2-5): 40.</summary>
        public const int ChestBase = Inventory.Cells;

        /// <summary>Сундук открыт — ячейки 40…69 доступны.</summary>
        public bool ChestOpen { get; private set; }

        /// <summary>Ячеек доступно руке сейчас: сумка, с открытым сундуком — и он.</summary>
        public int CellCount => ChestOpen ? ChestBase + Simulation.ChestCells : Inventory.Cells;

        /// <summary>Предмет и количество в ячейке руки (сумка или сундук).</summary>
        public byte ItemAt(int cell) => cell >= ChestBase ? _chest.ItemAt(cell - ChestBase) : _bag.ItemAt(cell);

        public int CountIn(int cell) => cell >= ChestBase ? _chest.CountIn(cell - ChestBase) : _bag.CountIn(cell);

        /// <summary>
        /// Свежий сундук сервера (E2-5) — звать каждый кадр после <see cref="Sync"/>. Сундук закрылся или взятая из него
        /// стопка изменилась — рука подстраивается, как у сумки.
        /// </summary>
        public void SyncChest(bool open, in Inventory chest)
        {
            ChestOpen = open;
            _chest = open ? chest : default;
            if (Holding && From >= ChestBase)
            {
                if (!open || ItemAt(From) != Item)
                {
                    Cancel();
                    return;
                }

                Count = Math.Min(Count, CountIn(From));
            }
        }

        public BagHand(IBagRequests requests)
        {
            _requests = requests;
        }

        /// <summary>В руке есть предмет.</summary>
        public bool Holding => Count > 0;

        /// <summary>Ячейка, из которой взято (смысл есть при <see cref="Holding"/>).</summary>
        public int From { get; private set; }

        /// <summary>Предмет в руке.</summary>
        public byte Item { get; private set; }

        /// <summary>Сколько штук в руке; 0 — рука пуста.</summary>
        public int Count { get; private set; }

        /// <summary>Мёртвому рука не берёт и заявок не шлёт (сервер ответил бы отказом).</summary>
        public bool Dead { get; private set; }

        /// <summary>Заявок отправлено.</summary>
        public int RequestsSent { get; private set; }

        /// <summary>
        /// Свежая сумка сервера и «жив ли» — звать каждый кадр до щелчков. Если взятая стопка изменилась
        /// (подобрал, потратил, сервер переложил) — рука подстраивается: предмет другой или ячейка пуста — отмена,
        /// стало меньше — количество урезается.
        /// </summary>
        public void Sync(in Inventory bag, bool dead)
        {
            // C-013: стопка ячейки руки уменьшилась (постройку с курсора поставили, сервер снял предмет) — уходят штуки из руки:
            // в руке остаётся остаток, а не прежнее число (рука могла держать часть стопки).
            int spent = Holding && From < ChestBase && _bag.ItemAt(From) == Item && bag.ItemAt(From) == Item
                ? Math.Max(0, _bag.CountIn(From) - bag.CountIn(From)) : 0;
            _bag = bag;
            Dead = dead;
            if (!Holding)
            {
                return;
            }

            if (From >= ChestBase)
            {
                if (dead)
                {
                    Cancel();
                }

                return; // стопка из сундука — сверяет SyncChest
            }

            if (dead || bag.ItemAt(From) != Item)
            {
                Cancel();
                return;
            }

            Count = Math.Min(Count - spent, bag.CountIn(From));
            if (Count <= 0)
            {
                Cancel();
            }
        }

        /// <summary>
        /// C-013 (приёмка Автора 15.11: «установка с инвентаря не работает. Только с панели внизу»): в руке курсора постройка
        /// из сумки (не из сундука) — щелчок в мир её ставит (из ячейки <see cref="From"/>), а не выбрасывает.
        /// </summary>
        public bool HoldsPlaceable => Holding && !Dead && From < ChestBase && BuildHand.IsBuildItem(Item);

        /// <summary>Число, которое окно показывает в ячейке: без того, что «в руке».</summary>
        public int ShownCount(int cell) => CountIn(cell) - (Holding && cell == From ? Count : 0);

        /// <summary>Опустить руку без заявки.</summary>
        public void Cancel()
        {
            Count = 0;
            Item = Items.None;
        }

        /// <summary>ЛКМ по ячейке: пустой рукой — взять всю стопку, с предметом — положить (перенос, доукладка, обмен).</summary>
        public BagClick Left(int cell)
        {
            if ((uint)cell >= CellCount || Dead)
            {
                return BagClick.None;
            }

            return Holding ? Put(cell) : Take(cell, CountIn(cell));
        }

        /// <summary>
        /// ПКМ по ячейке (слово Автора 15.11, как в Terraria): пустой рукой — взять 1; по той же ячейке с предметом в руке —
        /// ещё 1 (пока есть в стопке); по другой ячейке с предметом в руке — вернуть на место.
        /// </summary>
        public BagClick Right(int cell)
        {
            if ((uint)cell >= CellCount || Dead)
            {
                return BagClick.None;
            }

            if (Holding)
            {
                if (cell == From && Count < CountIn(cell))
                {
                    Count++;
                    return BagClick.Taken;
                }

                if (cell == From)
                {
                    return BagClick.None; // вся стопка уже в руке
                }

                Cancel();
                return BagClick.Cancelled;
            }

            return Take(cell, 1);
        }

        /// <summary>Shift+ЛКМ по ячейке пустой рукой (слово Автора 15.11): взять половину (с округлением вверх).</summary>
        public BagClick Half(int cell)
        {
            if ((uint)cell >= CellCount || Dead || Holding)
            {
                return BagClick.None;
            }

            return Take(cell, (CountIn(cell) + 1) / 2);
        }

        /// <summary>
        /// Ctrl+ЛКМ (слово Автора 15.11: было Shift+ЛКМ): быстро перекинуть стопку между быстрой панелью (первые <see cref="Inventory.Slots"/> ячеек) и
        /// остальной сумкой — в стопку того же предмета с местом, иначе в первую пустую ячейку.
        /// </summary>
        public BagClick Quick(int cell)
        {
            if ((uint)cell >= CellCount || Dead || Holding || ItemAt(cell) == Items.None)
            {
                return BagClick.None;
            }

            int to = !ChestOpen ? QuickTarget(_bag, cell)
                : cell >= ChestBase ? FitTarget(_bag, ItemAt(cell), 0, Inventory.Cells)      // сундук → сумка
                : FitTarget(_chest, ItemAt(cell), 0, Simulation.ChestCells, ChestBase);   // сумка → сундук
            return to >= 0 && Send(_requests.RequestBagMove(cell, to)) ? BagClick.Moved : BagClick.None;
        }

        /// <summary>Заявок пачки «Взять / Положить всё» за раз: лимит сервера на игрока за такт (больше — отказ без ответа).</summary>
        public const int MoveAllBatch = NetServer.MaxBagRequestsPerPlayerTick;

        /// <summary>Сколько ждать ответа на пачку (сумка и сундук не изменились), прежде чем повторить, секунд.</summary>
        public const double MoveAllWaitSeconds = 0.3;

        /// <summary>Повторов пачки без ответа подряд, после которых перенос останавливается («сервер не принял»).</summary>
        public const int MoveAllRetries = 2;

        private bool _moveAll, _moveAllFromChest, _moveAllWaiting;
        private Inventory _sentBag, _sentChest;
        private double _now, _sentAt;
        private int _silentRetries;

        /// <summary>Идёт «Взять всё» / «Положить всё» (пачки по <see cref="MoveAllBatch"/>).</summary>
        public bool MovingAll => _moveAll;

        /// <summary>Чем кончился последний «Взять / Положить всё»; <see cref="MoveAllStop.None"/> — ещё идёт или не было.</summary>
        public MoveAllStop LastStop { get; private set; }

        /// <summary>
        /// C-008: кнопка «Взять всё» у сундука — всё из сундука в сумку обычными заявками переноса (сообщение 29, новых
        /// заявок нет). Сервер берёт не больше <see cref="NetServer.MaxBagRequestsPerPlayerTick"/> заявок игрока за такт,
        /// остальные отклоняет молча (проверка C-008, п. 1), поэтому рука шлёт пачку не больше 8, а следующую — когда
        /// сервер ответил (сумка и сундук изменились, <see cref="Pump"/>). Цели пачки считаются по копиям сумки и сундука
        /// той же функцией <see cref="Inventory.Move(ref Inventory, int, ref Inventory, int)"/>, что у сервера, — заявки
        /// пачки не наступают друг на друга; каждая пачка — по свежему состоянию сервера, так что отклонённые (лимит такта
        /// занят другими заявками) уходят снова. Возвращает число заявок первой пачки.
        /// </summary>
        public int TakeAll() => StartMoveAll(fromChest: true);

        /// <summary>C-008: кнопка «Положить всё» — сумка без быстрой панели (ячейки 8…39) в сундук, как в Terraria.</summary>
        public int PutAll() => StartMoveAll(fromChest: false);

        /// <summary>
        /// Кадр «Взять / Положить всё» — звать каждый кадр после <see cref="Sync"/> и <see cref="SyncChest"/>
        /// (<paramref name="now"/> — секунды, растут). Ответ на пачку пришёл (сумка и сундук изменились) — следующая пачка;
        /// ответа нет дольше <see cref="MoveAllWaitSeconds"/> — пачка снова по свежему состоянию (отказ «лимит»); после
        /// <see cref="MoveAllRetries"/> таких повторов — стоп <see cref="MoveAllStop.NotAccepted"/>. Сундук закрылся,
        /// персонаж умер, переносить нечего или некуда — стоп с причиной (<see cref="LastStop"/>).
        /// </summary>
        public void Pump(double now)
        {
            _now = now;
            if (!_moveAll)
            {
                return;
            }

            if (!ChestOpen)
            {
                StopMoveAll(MoveAllStop.ChestClosed);
                return;
            }

            if (Dead)
            {
                StopMoveAll(MoveAllStop.Dead);
                return;
            }

            if (_moveAllWaiting)
            {
                if (_bag != _sentBag && _chest != _sentChest)
                {
                    _silentRetries = 0; // ответ пришёл: перенос меняет и сумку, и сундук
                }
                else if (now - _sentAt >= MoveAllWaitSeconds)
                {
                    if (++_silentRetries > MoveAllRetries)
                    {
                        StopMoveAll(MoveAllStop.NotAccepted);
                        return;
                    }
                }
                else
                {
                    return;
                }

                _moveAllWaiting = false;
            }

            MoveAllBatchSend();
        }

        private int StartMoveAll(bool fromChest)
        {
            if (!ChestOpen || Dead || Holding)
            {
                return 0;
            }

            _moveAll = true;
            _moveAllFromChest = fromChest;
            _moveAllWaiting = false;
            _silentRetries = 0;
            LastStop = MoveAllStop.None;
            return MoveAllBatchSend();
        }

        private void StopMoveAll(MoveAllStop why)
        {
            _moveAll = false;
            _moveAllWaiting = false;
            LastStop = why;
        }

        // Пачка: не больше MoveAllBatch заявок по копиям сумки и сундука. Нечего — «готово»; есть, но некуда — «нет места».
        private int MoveAllBatchSend()
        {
            if (Holding)
            {
                return 0; // взял предмет в руку посреди переноса — подождать
            }

            bool fromChest = _moveAllFromChest;
            Inventory bag = _bag, chest = _chest;
            int sent = 0;
            bool left = false;
            int lo = fromChest ? 0 : Inventory.Slots, hi = fromChest ? Simulation.ChestCells : Inventory.Cells;
            for (int i = lo; i < hi && sent < MoveAllBatch; i++)
            {
                for (int tries = 0; tries < 4 && sent < MoveAllBatch; tries++)
                {
                    byte item = fromChest ? chest.ItemAt(i) : bag.ItemAt(i);
                    if (item == Items.None)
                    {
                        break;
                    }

                    int to = fromChest ? FitTarget(bag, item, 0, Inventory.Cells) : FitTarget(chest, item, 0, Simulation.ChestCells);
                    if (to < 0 || !(fromChest ? Inventory.Move(ref chest, i, ref bag, to) : Inventory.Move(ref bag, i, ref chest, to)))
                    {
                        left = true;
                        break;
                    }

                    if (!Send(_requests.RequestBagMove(fromChest ? ChestBase + i : i, fromChest ? to : ChestBase + to)))
                    {
                        StopMoveAll(MoveAllStop.NotAccepted);
                        return sent;
                    }

                    sent++;
                }
            }

            if (sent == 0)
            {
                StopMoveAll(left ? MoveAllStop.NoRoom : MoveAllStop.Done);
                return 0;
            }

            _moveAllWaiting = true;
            _sentBag = _bag;
            _sentChest = _chest;
            _sentAt = _now;

            return sent;
        }

        /// <summary>ЛКМ мимо окна с предметом в руке: выбросить на землю.</summary>
        public BagClick Outside()
        {
            if (!Holding || Dead)
            {
                return BagClick.None;
            }

            int from = From;
            int count = Count == CountIn(from) ? 0 : Count; // 0 — «всю стопку»
            Cancel();
            return Send(_requests.RequestBagDrop(from, count)) ? BagClick.Dropped : BagClick.None;
        }

        /// <summary>Цель быстрого переноса из ячейки; некуда — −1.</summary>
        public static int QuickTarget(in Inventory bag, int cell)
        {
            byte item = bag.ItemAt(cell);
            if (item == Items.None)
            {
                return -1;
            }

            bool fromBar = cell < Inventory.Slots;
            int lo = fromBar ? Inventory.Slots : 0, hi = fromBar ? Inventory.Cells : Inventory.Slots;
            return FitTarget(bag, item, lo, hi);
        }

        /// <summary>Куда положить предмет в ячейках [lo; hi): в стопку того же предмета с местом, иначе в первую пустую;
        /// некуда — −1. Номер — плюс <paramref name="offset"/> (ячейки сундука в номерах руки).</summary>
        public static int FitTarget(in Inventory inv, byte item, int lo, int hi, int offset = 0)
        {
            int max = ItemTable.MaxStackOf(item);
            for (int i = lo; i < hi; i++)
            {
                if (inv.ItemAt(i) == item && inv.CountIn(i) < max)
                {
                    return i + offset;
                }
            }

            for (int i = lo; i < hi; i++)
            {
                if (inv.ItemAt(i) == Items.None)
                {
                    return i + offset;
                }
            }

            return -1;
        }

        private BagClick Take(int cell, int count)
        {
            byte item = ItemAt(cell);
            if (item == Items.None || count <= 0)
            {
                return BagClick.None;
            }

            From = cell;
            Item = item;
            Count = count;
            return BagClick.Taken;
        }

        private BagClick Put(int cell)
        {
            int from = From, count = Count;
            if (cell == from)
            {
                Cancel();
                return BagClick.Cancelled;
            }

            if (count >= CountIn(from))
            {
                Cancel();
                return Send(_requests.RequestBagMove(from, cell)) ? BagClick.Moved : BagClick.None;
            }

            // Часть стопки: в пустую ячейку или в стопку того же предмета — сколько влезает.
            byte there = ItemAt(cell);
            if (there != Items.None)
            {
                if (there != Item)
                {
                    return BagClick.None; // чужой предмет: часть стопки обменять нельзя — рука остаётся
                }

                count = Math.Min(count, ItemTable.MaxStackOf(Item) - CountIn(cell));
                if (count <= 0)
                {
                    return BagClick.None;
                }
            }

            Cancel();
            return Send(_requests.RequestBagSplit(from, cell, count)) ? BagClick.Split : BagClick.None;
        }

        private bool Send(bool sent)
        {
            if (sent)
            {
                RequestsSent++;
            }

            return sent;
        }
    }
}
