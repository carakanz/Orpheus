using System;
using System.Text;

namespace Orpheus.Net.Protocol
{
    /// <summary>
    /// «Поставить паузу» / «продолжить» (0-036; BRIEF п. 11b — у любого игрока есть возможность поставить паузу). Клиент шлёт
    /// желаемое состояние, а не «переключить»: два игрока, нажавшие Esc одновременно, ставят паузу оба — и она одна.
    /// </summary>
    public struct PauseRequestMessage
    {
        /// <summary>Тип 1 + флаг 1.</summary>
        public const int Bytes = 2;

        public bool Paused;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.PauseRequest);
            w.WriteByte(Paused ? (byte)1 : (byte)0);
            return w.BytesWritten;
        }

        public static PauseRequestMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.PauseRequest);
            byte paused = r.ReadByte();
            if (paused > 1)
            {
                throw new ProtocolException($"Запрос паузы: флаг {paused}.");
            }

            r.ExpectEnd();
            return new PauseRequestMessage { Paused = paused == 1 };
        }
    }

    /// <summary>
    /// Пауза поставлена или снята (0-036): всем в игре при смене, вошедшему в паузу — сразу после «вход принят» (тот же канал 2,
    /// порядок держит транспорт). <see cref="ServerTick"/> — последний шагнутый такт: в паузе такт мира стоит на нём, после
    /// снятия следующим шагается <c>ServerTick + 1</c>. Снимок новее <see cref="ServerTick"/> паузы значит «пауза уже снята»
    /// (клиент снимает её у себя по нему, если сообщение задержала переотправка). Имя — того, кто поставил или снял
    /// (UTF-8, 0…<see cref="LoginMessage.MaxNameBytes"/> байт; пусто — сервер сам, например опустел).
    /// </summary>
    public struct PauseStateMessage : IEquatable<PauseStateMessage>
    {
        /// <summary>Наибольший размер: тип 1 + флаг 1 + такт 4 + сущность 4 + длина имени 1 + имя.</summary>
        public const int MaxBytes = 11 + LoginMessage.MaxNameBytes;

        public bool Paused;
        public uint ServerTick;

        /// <summary>Сущность того, кто поставил или снял; 0 — сервер сам.</summary>
        public int ByEntity;

        public string ByName;

        public int Write(Span<byte> dest)
        {
            byte[] name = Encoding.UTF8.GetBytes(ByName ?? string.Empty); // смена паузы — редкость, выделение не важно
            if (name.Length > LoginMessage.MaxNameBytes)
            {
                throw new ArgumentException($"Имя — 0…{LoginMessage.MaxNameBytes} байт UTF-8, а не {name.Length}.");
            }

            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.PauseState);
            w.WriteByte(Paused ? (byte)1 : (byte)0);
            w.WriteUInt32(ServerTick);
            w.WriteUInt32(unchecked((uint)ByEntity));
            w.WriteByte((byte)name.Length);
            w.WriteBytes(name);
            return w.BytesWritten;
        }

        public static PauseStateMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.PauseState);
            byte paused = r.ReadByte();
            if (paused > 1)
            {
                throw new ProtocolException($"Пауза: флаг {paused}.");
            }

            uint tick = r.ReadUInt32();
            int by = unchecked((int)r.ReadUInt32());
            int n = r.ReadByte();
            if (n > LoginMessage.MaxNameBytes)
            {
                throw new ProtocolException($"Пауза: длина имени {n} больше {LoginMessage.MaxNameBytes}.");
            }

            byte[] name = new byte[n];
            r.ReadBytes(name);
            r.ExpectEnd();
            return new PauseStateMessage { Paused = paused == 1, ServerTick = tick, ByEntity = by, ByName = Encoding.UTF8.GetString(name) };
        }

        public bool Equals(PauseStateMessage o) => Paused == o.Paused && ServerTick == o.ServerTick && ByEntity == o.ByEntity && (ByName ?? string.Empty) == (o.ByName ?? string.Empty);
        public override bool Equals(object? obj) => obj is PauseStateMessage o && Equals(o);
        public override int GetHashCode() => unchecked((int)ServerTick * 31 + ByEntity);

        public override string ToString() => $"{(Paused ? "пауза" : "продолжить")} на такте {ServerTick}: #{ByEntity} «{ByName}»";
    }
}
