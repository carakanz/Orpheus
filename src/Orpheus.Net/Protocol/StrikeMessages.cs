using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Protocol
{
    /// <summary>Тайл удара во «взмахе» (C-028): тайл, объект, ударов набрано из нужных, снят ли этим ударом.</summary>
    public struct StrikeTile
    {
        public TileCoord Tile;
        public ushort Obj;
        public byte Hits;
        public byte Needed;
        public bool Broken;
    }

    /// <summary>
    /// «Взмах кирки» (C-028, тип 39, канал 2): удар решён сервером — ударивший (сущность), номер его запроса копания и до
    /// <see cref="PickStrike.MaxTiles"/> задетых тайлов. Тип 1 + ударивший u32 + запрос u16 + число 1 + на тайл: x u32, y u32,
    /// объект u16, удары 1, нужно 1, флаги 1 (бит 0 — снят) = 8 + 13 × n байт (пустой взмах — 8). Получатели — ударивший и
    /// игроки в окне от ударившего. Клиент кладёт удары в свою <see cref="TileWear"/> (трещины), снятие тайла приходит пачкой мира.
    /// </summary>
    public struct StrikeMessage
    {
        public const int HeaderBytes = 8;
        public const int TileBytes = 13;

        public int Striker;
        public ushort RequestId;
        public int Count;
        public StrikeTile T0;
        public StrikeTile T1;
        public StrikeTile T2;

        /// <summary>Тайл № <paramref name="i"/> (0…<see cref="Count"/> − 1).</summary>
        public readonly StrikeTile Get(int i) => i == 0 ? T0 : i == 1 ? T1 : T2;

        /// <summary>Записать тайл № <paramref name="i"/>.</summary>
        public void SetTile(int i, in StrikeTile t)
        {
            if (i == 0)
            {
                T0 = t;
            }
            else if (i == 1)
            {
                T1 = t;
            }
            else if (i == 2)
            {
                T2 = t;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(i));
            }
        }

        public readonly int Write(Span<byte> dest)
        {
            if (Count < 0 || Count > PickStrike.MaxTiles)
            {
                throw new InvalidOperationException($"Взмах: тайлов {Count}.");
            }

            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Strike);
            w.WriteUInt32((uint)Striker);
            w.WriteUInt16(RequestId);
            w.WriteByte((byte)Count);
            for (int i = 0; i < Count; i++)
            {
                StrikeTile t = Get(i);
                w.WriteUInt32((uint)t.Tile.X);
                w.WriteUInt32((uint)t.Tile.Y);
                w.WriteUInt16(t.Obj);
                w.WriteByte(t.Hits);
                w.WriteByte(t.Needed);
                w.WriteByte(t.Broken ? (byte)1 : (byte)0);
            }

            return w.BytesWritten;
        }

        public static StrikeMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Strike);
            var m = new StrikeMessage { Striker = (int)r.ReadUInt32(), RequestId = r.ReadUInt16(), Count = r.ReadByte() };
            if (m.Count > PickStrike.MaxTiles)
            {
                throw new ProtocolException($"Взмах: тайлов {m.Count}.");
            }

            for (int i = 0; i < m.Count; i++)
            {
                var t = new StrikeTile
                {
                    Tile = new TileCoord((int)r.ReadUInt32(), (int)r.ReadUInt32()),
                    Obj = r.ReadUInt16(),
                    Hits = r.ReadByte(),
                    Needed = r.ReadByte(),
                };
                byte flags = r.ReadByte();
                if (flags > 1 || t.Needed == 0 || t.Hits == 0)
                {
                    throw new ProtocolException($"Взмах: флаги {flags}, удары {t.Hits}/{t.Needed}.");
                }

                t.Broken = flags == 1;
                m.SetTile(i, t);
            }

            r.ExpectEnd();
            return m;
        }

        public override readonly string ToString()
        {
            string s = $"взмах #{Striker} запрос {RequestId}:";
            for (int i = 0; i < Count; i++)
            {
                StrikeTile t = Get(i);
                s += $" {t.Tile} {t.Hits}/{t.Needed}{(t.Broken ? " снят" : "")}";
            }

            return s;
        }
    }
}
