using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Protocol
{
    /// <summary>Что просит запрос правки (0-028; docs/07, 3.7 — таблица запросов вехи 0).</summary>
    public enum TileOp : byte
    {
        /// <summary>Копать: начало — тайл; прогресс копится, пока кнопка копания зажата в кадрах ввода.</summary>
        DigStart = 1,

        /// <summary>Копать: конец — кнопку отпустили раньше, чем докопали (ответ на начало — «прервано»).</summary>
        DigStop = 2,

        /// <summary>Поставить: линия от тайла до тайла, до 32 тайлов (`05` 1.5), объект.</summary>
        Place = 3,

        /// <summary>Закрыть дверь (1-012, F): тайл двери в From (To не используется) — сервер проверит радиус,
        /// замок и проём, смена состояния — record-only правка. Ответ — общий («принято»/причина).</summary>
        CloseDoor = 4,

        /// <summary>Починить постройку (E2-5, `05` 1.3, протокол 23): тайл в From — цена по недостающей доле прочности, молот в руках.
        /// Ответ — общий («принято»/причина: цело, под ударом, нет ресурсов…), предсказания нет.</summary>
        Repair = 5,
    }

    /// <summary>
    /// Клиент → сервер, канал 2 (надёжный, по порядку): запрос правки тайлов (0-028, тип 20; docs/07, 3.7). Номер запроса —
    /// u16 клиента по кругу, им же помечен ответ. Один вид на все операции: для копания конец = начало, объект 0.
    /// <para>
    /// <see cref="Frame"/> — кадр ввода, с которого действует запрос (следующий кадр клиента): кадры раньше него копят прогресс
    /// прежнего копания, с него — нового. Иначе «начало» по надёжному каналу обгоняет последние кадры прежнего копания по
    /// ненадёжному, и докопанное своим счётом сервер прерывает.
    /// </para>
    /// </summary>
    public struct TileRequestMessage
    {
        /// <summary>Тип 1 + номер 2 + операция 1 + кадр 4 + начало x, y 2 + 2 + конец x, y 2 + 2 + объект 2 + ячейка 1 + четверти 1 (протокол 26).</summary>
        public const int Bytes = 20;

        public ushort RequestId;

        /// <summary>Кадр ввода, с которого действует запрос (копание; для постановки — справочно).</summary>
        public uint Frame;
        public TileOp Op;
        public TileCoord From;
        public TileCoord To;
        public ushort Obj;

        /// <summary>
        /// Постановка (E2-11, протокол 25): ячейка быстрой панели (0…7), из которой берётся предмет-постройка, — по предмету
        /// на тайл линии. Остальные операции — 0 (не читается).
        /// </summary>
        public byte Cell;

        /// <summary>
        /// Четверти (E2-12, протокол 26): биты 0–1 — ячейка 0,5 × 0,5 в тайле <see cref="From"/> (бит 0 — восточная половина,
        /// бит 1 — южная), биты 2–3 — в тайле <see cref="To"/>. Постановка стены и забора — линия ячеек от одной до другой;
        /// двери — пара ячеек тайла по ячейке начала (<c>BuildCells.DoorMaskFor</c>). Иначе 0.
        /// </summary>
        public byte Quad;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest.Slice(0, Bytes));
            w.WriteByte((byte)MessageType.TileRequest);
            w.WriteUInt16(RequestId);
            w.WriteByte((byte)Op);
            w.WriteUInt32(Frame);
            w.WriteUInt16((ushort)From.X);
            w.WriteUInt16((ushort)From.Y);
            w.WriteUInt16((ushort)To.X);
            w.WriteUInt16((ushort)To.Y);
            w.WriteUInt16(Obj);
            w.WriteByte(Cell);
            w.WriteByte(Quad);
            return w.BytesWritten;
        }

        /// <summary>Прочитать; тайл вне карты или операция неизвестна — <see cref="ProtocolException"/>.</summary>
        public static TileRequestMessage Read(ReadOnlySpan<byte> src, in MapShape shape)
        {
            var r = Codec.Begin(src, MessageType.TileRequest);
            var m = new TileRequestMessage { RequestId = r.ReadUInt16() };
            byte op = r.ReadByte();
            m.Frame = r.ReadUInt32();
            m.From = new TileCoord(r.ReadUInt16(), r.ReadUInt16());
            m.To = new TileCoord(r.ReadUInt16(), r.ReadUInt16());
            m.Obj = r.ReadUInt16();
            m.Cell = r.ReadByte();
            m.Quad = r.ReadByte();
            r.ExpectEnd();
            if (m.Quad > 15)
            {
                throw new ProtocolException($"Запрос правки: четверти {m.Quad}.");
            }

            if (op < (byte)TileOp.DigStart || op > (byte)TileOp.Repair)
            {
                throw new ProtocolException($"Запрос правки: операция {op}.");
            }

            m.Op = (TileOp)op;
            if (m.Op == TileOp.Place && m.Cell >= Orpheus.Core.Sim.Inventory.Slots)
            {
                throw new ProtocolException($"Запрос правки: ячейка {m.Cell} вне быстрой панели.");
            }

            if (m.From.X >= shape.Width || m.From.Y >= shape.Height || m.To.X >= shape.Width || m.To.Y >= shape.Height)
            {
                throw new ProtocolException($"Запрос правки: тайл {m.From}–{m.To} вне карты {shape.Width}×{shape.Height}.");
            }

            return m;
        }

        public override string ToString() => $"запрос {RequestId}: {Op} с кадра {Frame} {From}–{To} obj {Obj} ячейка {Cell} четверти {Quad}";
    }

    /// <summary>
    /// Сервер → клиент, канал 2: ответ на запрос правки (0-028, тип 21; docs/07, 3.7 «Ответ»): «принято» или «отказ: причина»
    /// (<see cref="EditReply"/>, строки `05` 1.3). Для линии — маска принятых тайлов (бит i — i-й тайл линии) и причина
    /// первого отказа среди остальных; для копания — бит 0.
    /// </summary>
    public struct TileReplyMessage
    {
        /// <summary>Тип 1 + номер 2 + ответ 1 + первый отказ 1 + маска 4.</summary>
        public const int Bytes = 9;

        public ushort RequestId;

        /// <summary>Итог: <see cref="EditReply.Accepted"/> — принят хоть один тайл; иначе причина отказа.</summary>
        public EditReply Reply;

        /// <summary>Причина первого отказа среди тайлов линии (принято целиком — <see cref="EditReply.Accepted"/>).</summary>
        public EditReply FirstRefusal;

        /// <summary>Принятые тайлы линии: бит i — i-й тайл (<see cref="EditRules.Line"/>).</summary>
        public uint Mask;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest.Slice(0, Bytes));
            w.WriteByte((byte)MessageType.TileReply);
            w.WriteUInt16(RequestId);
            w.WriteByte((byte)Reply);
            w.WriteByte((byte)FirstRefusal);
            w.WriteUInt32(Mask);
            return w.BytesWritten;
        }

        public static TileReplyMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.TileReply);
            var m = new TileReplyMessage { RequestId = r.ReadUInt16() };
            byte reply = r.ReadByte();
            byte first = r.ReadByte();
            m.Mask = r.ReadUInt32();
            r.ExpectEnd();
            if (reply > (byte)EditRules.MaxReply || first > (byte)EditRules.MaxReply)
            {
                throw new ProtocolException($"Ответ на правку: код {reply} / {first}.");
            }

            m.Reply = (EditReply)reply;
            m.FirstRefusal = (EditReply)first;
            if ((m.Reply == EditReply.Accepted) != (m.Mask != 0))
            {
                throw new ProtocolException($"Ответ на правку: «{m.Reply}» с маской {m.Mask:X8}.");
            }

            return m;
        }

        public override string ToString() => $"ответ {RequestId}: {Reply} (первый отказ {FirstRefusal}) маска {Mask:X8}";
    }
}
