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

    /// <summary>
    /// «Рука» окна сумки (E2-2, шаг 2.2): общий путь заявок для окна игрока и для ботов / тестов. Правил игры здесь
    /// нет — сумку меняет только сервер. У сервера нет ячейки курсора, поэтому «взять» — состояние клиента: стопка
    /// остаётся в своей ячейке, рука помнит ячейку и количество; заявка уходит, когда предмет кладут или выбрасывают:
    /// целая стопка — <see cref="BagOp.Move"/>, часть — <see cref="BagOp.Split"/>, мимо окна — <see cref="BagOp.Drop"/>.
    /// После заявки рука пуста: результат — сумка сервера (сообщение 26), предсказания нет.
    /// </summary>
    public sealed class BagHand
    {
        private readonly IBagRequests _requests;
        private Inventory _bag;

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
            _bag = bag;
            Dead = dead;
            if (!Holding)
            {
                return;
            }

            if (dead || bag.ItemAt(From) != Item)
            {
                Cancel();
                return;
            }

            Count = Math.Min(Count, bag.CountIn(From));
        }

        /// <summary>Число, которое окно показывает в ячейке: без того, что «в руке».</summary>
        public int ShownCount(int cell) => _bag.CountIn(cell) - (Holding && cell == From ? Count : 0);

        /// <summary>Опустить руку без заявки.</summary>
        public void Cancel()
        {
            Count = 0;
            Item = Items.None;
        }

        /// <summary>ЛКМ по ячейке: пустой рукой — взять всю стопку, с предметом — положить (перенос, доукладка, обмен).</summary>
        public BagClick Left(int cell)
        {
            if ((uint)cell >= Inventory.Cells || Dead)
            {
                return BagClick.None;
            }

            return Holding ? Put(cell) : Take(cell, _bag.CountIn(cell));
        }

        /// <summary>ПКМ по ячейке: пустой рукой — взять половину (с округлением вверх), с предметом — вернуть на место.</summary>
        public BagClick Right(int cell)
        {
            if ((uint)cell >= Inventory.Cells || Dead)
            {
                return BagClick.None;
            }

            if (Holding)
            {
                Cancel();
                return BagClick.Cancelled;
            }

            return Take(cell, (_bag.CountIn(cell) + 1) / 2);
        }

        /// <summary>
        /// Shift+ЛКМ: быстро перекинуть стопку между быстрой панелью (первые <see cref="Inventory.Slots"/> ячеек) и
        /// остальной сумкой — в стопку того же предмета с местом, иначе в первую пустую ячейку.
        /// </summary>
        public BagClick Quick(int cell)
        {
            if ((uint)cell >= Inventory.Cells || Dead || Holding || _bag.ItemAt(cell) == Items.None)
            {
                return BagClick.None;
            }

            int to = QuickTarget(_bag, cell);
            return to >= 0 && Send(_requests.RequestBagMove(cell, to)) ? BagClick.Moved : BagClick.None;
        }

        /// <summary>ЛКМ мимо окна с предметом в руке: выбросить на землю.</summary>
        public BagClick Outside()
        {
            if (!Holding || Dead)
            {
                return BagClick.None;
            }

            int from = From;
            int count = Count == _bag.CountIn(from) ? 0 : Count; // 0 — «всю стопку»
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
            int max = ItemTable.MaxStackOf(item);
            for (int i = lo; i < hi; i++)
            {
                if (bag.ItemAt(i) == item && bag.CountIn(i) < max)
                {
                    return i;
                }
            }

            for (int i = lo; i < hi; i++)
            {
                if (bag.ItemAt(i) == Items.None)
                {
                    return i;
                }
            }

            return -1;
        }

        private BagClick Take(int cell, int count)
        {
            byte item = _bag.ItemAt(cell);
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

            if (count >= _bag.CountIn(from))
            {
                Cancel();
                return Send(_requests.RequestBagMove(from, cell)) ? BagClick.Moved : BagClick.None;
            }

            // Часть стопки: в пустую ячейку или в стопку того же предмета — сколько влезает.
            byte there = _bag.ItemAt(cell);
            if (there != Items.None)
            {
                if (there != Item)
                {
                    return BagClick.None; // чужой предмет: часть стопки обменять нельзя — рука остаётся
                }

                count = Math.Min(count, ItemTable.MaxStackOf(Item) - _bag.CountIn(cell));
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
