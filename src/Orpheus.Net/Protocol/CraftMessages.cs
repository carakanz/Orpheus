using System;
using Orpheus.Core.Sim;

namespace Orpheus.Net.Protocol
{
    /// <summary>Что просит заявка изготовления (E2-3).</summary>
    public enum CraftOp : byte
    {
        /// <summary>Изготовить рецепт <see cref="CraftRequestMessage.Arg"/> × <see cref="CraftRequestMessage.Count"/>.</summary>
        Make = 1,

        /// <summary>Отменить позицию очереди № <see cref="CraftRequestMessage.Arg"/> (ингредиенты вернутся).</summary>
        Cancel = 2,

        /// <summary>C-013: изготовить рецепт <see cref="CraftRequestMessage.Arg"/> × <see cref="CraftRequestMessage.Count"/>
        /// сразу в ячейку сумки <see cref="CraftRequestMessage.Cell"/> (0xFF — куда влезет), без очереди.</summary>
        MakeInto = 3,
    }

    /// <summary>
    /// «Заявка изготовления» (E2-3, тип 30, канал 2; docs/06 3.5 «рецепт R × n»): операция, рецепт или номер позиции
    /// очереди, количество. Поля: тип 1 + операция 1 + аргумент 1 + количество u16. Станок в заявке не едет — сервер
    /// сам ищет его рядом с персонажем. Ответ — тип 31; очередь — тип 32; сумка — тип 26.
    /// У отмены в поле количества едет рецепт позиции — сверка против сдвига очереди.
    /// C-013 (протокол 29): + ячейка сумки 1 (у <see cref="CraftOp.MakeInto"/> — куда изготовить, у прочих — 0xFF).
    /// </summary>
    public struct CraftRequestMessage
    {
        public const int Bytes = 1 + 1 + 1 + 2 + 1;

        public CraftOp Op;

        /// <summary>Номер рецепта (<see cref="CraftOp.Make"/>) или позиции очереди (<see cref="CraftOp.Cancel"/>).</summary>
        public byte Arg;

        /// <summary>Сколько раз изготовить; у отмены — рецепт, который клиент видел в этой позиции (сверка на сервере:
        /// очередь могла сдвинуться, пока заявка шла; не совпал — отказ «нет позиции»).</summary>
        public ushort Count;

        /// <summary>C-013: ячейка сумки 0…39 для <see cref="CraftOp.MakeInto"/>; 0xFF — любая (и у прочих операций).</summary>
        public byte Cell;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.CraftRequest);
            w.WriteByte((byte)Op);
            w.WriteByte(Arg);
            w.WriteUInt16(Count);
            w.WriteByte(Op == CraftOp.MakeInto ? Cell : (byte)0xFF);
            return w.BytesWritten;
        }

        public static CraftRequestMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.CraftRequest);
            var m = new CraftRequestMessage { Op = (CraftOp)r.ReadByte(), Arg = r.ReadByte(), Count = r.ReadUInt16(), Cell = r.ReadByte() };
            r.ExpectEnd();
            if (m.Op < CraftOp.Make || m.Op > CraftOp.MakeInto
                || (m.Op == CraftOp.Cancel && (m.Arg >= CraftQueue.MaxEntries || !RecipeTable.Exists(m.Count)))
                || (m.Cell != 0xFF && (m.Op != CraftOp.MakeInto || m.Cell >= Inventory.Cells)))
            {
                throw new ProtocolException($"Заявка изготовления: операция {(byte)m.Op}, аргумент {m.Arg}.");
            }

            return m;
        }

        public override string ToString() => Op == CraftOp.MakeInto ? $"изготовление: {Op} {Arg} × {Count} в ячейку {Cell}" : $"изготовление: {Op} {Arg} × {Count}";
    }

    /// <summary>
    /// «Ответ на заявку изготовления» (E2-3, тип 31, канал 2): принято или причина отказа. Поля: тип 1 + операция 1 +
    /// аргумент 1 + ответ 1. На каждую заявку — ровно один ответ: отказ — сразу, итог заявки, взятой в очередь такта
    /// («принято» или причина), — после такта; ответы идут в порядке решения.
    /// </summary>
    public struct CraftReplyMessage
    {
        public const int Bytes = 1 + 1 + 1 + 1;

        public CraftOp Op;
        public byte Arg;
        public CraftReply Reply;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.CraftReply);
            w.WriteByte((byte)Op);
            w.WriteByte(Arg);
            w.WriteByte((byte)Reply);
            return w.BytesWritten;
        }

        public static CraftReplyMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.CraftReply);
            var m = new CraftReplyMessage { Op = (CraftOp)r.ReadByte(), Arg = r.ReadByte(), Reply = (CraftReply)r.ReadByte() };
            r.ExpectEnd();
            if (m.Op < CraftOp.Make || m.Op > CraftOp.MakeInto || m.Reply > CraftReply.NoRoom)
            {
                throw new ProtocolException($"Ответ изготовления: операция {(byte)m.Op}, ответ {(byte)m.Reply}.");
            }

            return m;
        }

        public override string ToString() => $"изготовление {Op} {Arg}: {Reply}";
    }

    /// <summary>
    /// «Очередь изготовления» (E2-3, тип 32, канал 2): очередь персонажа — сервер шлёт <b>владельцу</b>, когда изменился
    /// состав позиций или признак «идёт» (рядом ли станок первой позиции); при входе — если очередь не пуста.
    /// Прогресс текущего изготовления клиент ведёт сам от пришедшего значения (+1 за такт, пока «идёт»).
    /// Поля: тип 1 + сущность u32 + такт u32 + признаки 1 + очередь 12 (прогресс u16, 5 × (рецепт 1 + осталось 1)).
    /// </summary>
    public struct CraftStateMessage
    {
        public const int Bytes = 1 + 4 + 4 + 1 + CraftQueue.PayloadBytes;

        public int EntityId;

        /// <summary>Такт сервера, на который верны очередь и прогресс.</summary>
        public uint Tick;

        /// <summary>Первая позиция делается (персонаж жив, станок рядом); false — стоит.</summary>
        public bool Running;

        public CraftQueue Queue;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.CraftState);
            w.WriteUInt32((uint)EntityId);
            w.WriteUInt32(Tick);
            w.WriteByte(Running ? (byte)1 : (byte)0);
            Span<byte> q = stackalloc byte[CraftQueue.PayloadBytes];
            Queue.WritePayload(q);
            for (int i = 0; i < q.Length; i++)
            {
                w.WriteByte(q[i]);
            }

            return w.BytesWritten;
        }

        public static CraftStateMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.CraftState);
            var m = new CraftStateMessage { EntityId = (int)r.ReadUInt32(), Tick = r.ReadUInt32() };
            byte flags = r.ReadByte();
            if (flags > 1)
            {
                throw new ProtocolException($"Очередь изготовления: признаки {flags}.");
            }

            m.Running = flags == 1;
            Span<byte> q = stackalloc byte[CraftQueue.PayloadBytes];
            for (int i = 0; i < q.Length; i++)
            {
                q[i] = r.ReadByte();
            }

            r.ExpectEnd();
            try
            {
                m.Queue = CraftQueue.ReadPayload(q);
            }
            catch (ArgumentException ex)
            {
                throw new ProtocolException(ex.Message);
            }

            return m;
        }

        public override string ToString() => $"очередь #{EntityId} такт {Tick}: {Queue}{(Running ? string.Empty : " (стоит)")}";
    }

    /// <summary>
    /// «Сундуки рядом» (E2-10, тип 36, канал 2; <c>06</c> 3.5): сколько каждого предмета в сундуках рядом — для каждого
    /// вида станка (руки — от персонажа, верстак и костёр — от ближайшего такого станка в радиусе крафта). Сервер шлёт
    /// владельцу, когда сводка изменилась (подошёл, отошёл, сундук пополнили или опустошили); окно крафта прибавляет её к
    /// сумке при расчёте «есть». Не предсказывается. Поля: тип 1 + 3 × (число строк 1 + строки × (предмет 1 + количество
    /// u16)); строки — по возрастанию предмета, количество 1…65 535 (больше — обрезано). Пустая сводка — 4 байта.
    /// </summary>
    public struct NearbyStockMessage
    {
        /// <summary>Наибольший размер: все предметы таблицы у всех трёх видов станка.</summary>
        public static int MaxBytes => 1 + ChestStock.Stations * (1 + ItemTable.Count * 3);

        public ChestStock Stock;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.NearbyStock);
            for (int s = 0; s < ChestStock.Stations; s++)
            {
                int n = 0;
                for (int item = 1; item < ItemTable.Count; item++)
                {
                    n += Stock.Count((CraftStation)s, (byte)item) > 0 ? 1 : 0;
                }

                w.WriteByte((byte)n);
                for (int item = 1; item < ItemTable.Count; item++)
                {
                    int c = Stock.Count((CraftStation)s, (byte)item);
                    if (c > 0)
                    {
                        w.WriteByte((byte)item);
                        w.WriteUInt16((ushort)c);
                    }
                }
            }

            return w.BytesWritten;
        }

        /// <summary>Прочитать в <paramref name="into"/> (очищается).</summary>
        public static void Read(ReadOnlySpan<byte> src, ChestStock into)
        {
            var r = Codec.Begin(src, MessageType.NearbyStock);
            into.Clear();
            for (int s = 0; s < ChestStock.Stations; s++)
            {
                int n = r.ReadByte();
                if (n >= ItemTable.Count)
                {
                    throw new ProtocolException($"Сундуки рядом: {n} строк у станка {s}.");
                }

                int last = 0;
                for (int k = 0; k < n; k++)
                {
                    int item = r.ReadByte();
                    int c = r.ReadUInt16();
                    if (item <= last || item >= ItemTable.Count || c == 0)
                    {
                        throw new ProtocolException($"Сундуки рядом: предмет {item} × {c} у станка {s}.");
                    }

                    last = item;
                    into.Set((CraftStation)s, (byte)item, c);
                }
            }

            r.ExpectEnd();
        }

        public override string ToString() => "сундуки рядом";
    }
}
