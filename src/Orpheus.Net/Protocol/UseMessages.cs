using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Protocol
{
    /// <summary>Что просит заявка действия с объектом (E2-5, F).</summary>
    public enum UseOp : byte
    {
        /// <summary>Действие с объектом на тайле: сундук — открыть (сервер шлёт содержимое, тип 35), кровать — точка возрождения.</summary>
        Use = 1,

        /// <summary>Закрыть открытый сундук (окно закрыто).</summary>
        Close = 2,
    }

    /// <summary>
    /// «Действие с объектом» (E2-5, тип 33, канал 2; `05` 7.3, F): операция и тайл. Поля: тип 1 + операция 1 + x u16 + y u16.
    /// Верстак и костёр сюда не идут — их окно крафта открывает клиент, станок ищет сервер при заявке крафта.
    /// Ответ — тип 34 (на «использовать»; на «закрыть» ответа нет).
    /// </summary>
    public struct UseRequestMessage
    {
        public const int Bytes = 1 + 1 + 2 + 2;

        public UseOp Op;
        public TileCoord Tile;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.UseRequest);
            w.WriteByte((byte)Op);
            w.WriteUInt16((ushort)Tile.X);
            w.WriteUInt16((ushort)Tile.Y);
            return w.BytesWritten;
        }

        public static UseRequestMessage Read(ReadOnlySpan<byte> src, in MapShape shape)
        {
            var r = Codec.Begin(src, MessageType.UseRequest);
            var m = new UseRequestMessage { Op = (UseOp)r.ReadByte(), Tile = new TileCoord(r.ReadUInt16(), r.ReadUInt16()) };
            r.ExpectEnd();
            if (m.Op < UseOp.Use || m.Op > UseOp.Close || m.Tile.X >= shape.Width || m.Tile.Y >= shape.Height)
            {
                throw new ProtocolException($"Действие с объектом: операция {(byte)m.Op}, тайл {m.Tile}.");
            }

            return m;
        }

        public override string ToString() => $"действие {Op} {Tile}";
    }

    /// <summary>
    /// «Ответ на действие» (E2-5, тип 34, канал 2): тайл, объект на нём (id ядра) и итог — принято или причина
    /// (<see cref="EditReply"/>: далеко, пусто, ты мёртв, пауза…). Поля: тип 1 + x u16 + y u16 + объект u16 + ответ 1.
    /// </summary>
    public struct UseReplyMessage
    {
        public const int Bytes = 1 + 2 + 2 + 2 + 1;

        public TileCoord Tile;
        public ushort Obj;
        public EditReply Reply;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.UseReply);
            w.WriteUInt16((ushort)Tile.X);
            w.WriteUInt16((ushort)Tile.Y);
            w.WriteUInt16(Obj);
            w.WriteByte((byte)Reply);
            return w.BytesWritten;
        }

        public static UseReplyMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.UseReply);
            var m = new UseReplyMessage { Tile = new TileCoord(r.ReadUInt16(), r.ReadUInt16()), Obj = r.ReadUInt16(), Reply = (EditReply)r.ReadByte() };
            r.ExpectEnd();
            if (m.Reply > EditRules.MaxReply)
            {
                throw new ProtocolException($"Ответ на действие: код {(byte)m.Reply}.");
            }

            return m;
        }

        public override string ToString() => $"действие {Tile} ({Obj}): {Reply}";
    }

    /// <summary>
    /// «Сундук» (E2-5, тип 35, канал 2): содержимое открытого игроком сундука — при открытии и когда изменилось; «закрыт»
    /// (<see cref="Open"/> = false, ячейки пустые) — когда сундук снесли, игрок отошёл, умер или закрыл окно сам.
    /// Поля: тип 1 + x u16 + y u16 + открыт 1 + 30 × (предмет 1 + количество u16).
    /// </summary>
    public struct ChestStateMessage
    {
        public const int Bytes = 1 + 2 + 2 + 1 + Simulation.ChestCells * 3;

        public TileCoord Tile;
        public bool Open;

        /// <summary>Ячейки сундука: заняты первые <see cref="Simulation.ChestCells"/>.</summary>
        public Inventory Cells;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.ChestState);
            w.WriteUInt16((ushort)Tile.X);
            w.WriteUInt16((ushort)Tile.Y);
            w.WriteByte(Open ? (byte)1 : (byte)0);
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                w.WriteByte(Cells.ItemAt(i));
                w.WriteUInt16((ushort)Cells.CountIn(i));
            }

            return w.BytesWritten;
        }

        public static ChestStateMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.ChestState);
            var m = new ChestStateMessage { Tile = new TileCoord(r.ReadUInt16(), r.ReadUInt16()) };
            byte open = r.ReadByte();
            if (open > 1)
            {
                throw new ProtocolException($"Сундук: признак «открыт» {open}.");
            }

            m.Open = open == 1;
            for (int i = 0; i < Simulation.ChestCells; i++)
            {
                byte item = r.ReadByte();
                ushort count = r.ReadUInt16();
                if ((item == Items.None) != (count == 0) || (item != Items.None && (!ItemTable.Exists(item) || count > ItemTable.MaxStackOf(item))))
                {
                    throw new ProtocolException($"Сундук: ячейка {i} — предмет {item} × {count}.");
                }

                if (item != Items.None)
                {
                    m.Cells.SetCell(i, item, count);
                }
            }

            r.ExpectEnd();
            return m;
        }

        public override string ToString() => $"сундук {Tile}: {(Open ? "открыт" : "закрыт")}, занято {Cells.UsedCells}";
    }
}
