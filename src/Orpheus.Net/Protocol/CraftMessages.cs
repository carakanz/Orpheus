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
    }

    /// <summary>
    /// «Заявка изготовления» (E2-3, тип 30, канал 2; docs/06 3.5 «рецепт R × n»): операция, рецепт или номер позиции
    /// очереди, количество. Поля: тип 1 + операция 1 + аргумент 1 + количество u16. Станок в заявке не едет — сервер
    /// сам ищет его рядом с персонажем. Ответ — тип 31; очередь — тип 32; сумка — тип 26.
    /// У отмены в поле количества едет рецепт позиции — сверка против сдвига очереди.
    /// </summary>
    public struct CraftRequestMessage
    {
        public const int Bytes = 1 + 1 + 1 + 2;

        public CraftOp Op;

        /// <summary>Номер рецепта (<see cref="CraftOp.Make"/>) или позиции очереди (<see cref="CraftOp.Cancel"/>).</summary>
        public byte Arg;

        /// <summary>Сколько раз изготовить; у отмены — рецепт, который клиент видел в этой позиции (сверка на сервере:
        /// очередь могла сдвинуться, пока заявка шла; не совпал — отказ «нет позиции»).</summary>
        public ushort Count;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.CraftRequest);
            w.WriteByte((byte)Op);
            w.WriteByte(Arg);
            w.WriteUInt16(Count);
            return w.BytesWritten;
        }

        public static CraftRequestMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.CraftRequest);
            var m = new CraftRequestMessage { Op = (CraftOp)r.ReadByte(), Arg = r.ReadByte(), Count = r.ReadUInt16() };
            r.ExpectEnd();
            if (m.Op < CraftOp.Make || m.Op > CraftOp.Cancel
                || (m.Op == CraftOp.Cancel && (m.Arg >= CraftQueue.MaxEntries || !RecipeTable.Exists(m.Count))))
            {
                throw new ProtocolException($"Заявка изготовления: операция {(byte)m.Op}, аргумент {m.Arg}.");
            }

            return m;
        }

        public override string ToString() => $"изготовление: {Op} {Arg} × {Count}";
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
            if (m.Op < CraftOp.Make || m.Op > CraftOp.Cancel || m.Reply > CraftReply.NoEntity)
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
}
