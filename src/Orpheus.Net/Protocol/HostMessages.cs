using System;
using System.Globalization;

namespace Orpheus.Net.Protocol
{
    /// <summary>
    /// Одноразовый токен хозяина (0-024, docs/07 2.3): 128 бит, игра создаёт его при запуске сервера и передаёт ключом
    /// <c>--host-token</c>; вход с ним даёт флаг «хозяин». В файле-замке и в ключах — 32 шестнадцатеричные цифры.
    /// Нули — «токена нет».
    /// </summary>
    public readonly struct HostToken : IEquatable<HostToken>
    {
        /// <summary>Байт на проводе.</summary>
        public const int Bytes = 16;

        /// <summary>Знаков в записи.</summary>
        public const int HexLength = 32;

        public readonly ulong Hi;
        public readonly ulong Lo;

        public HostToken(ulong hi, ulong lo)
        {
            Hi = hi;
            Lo = lo;
        }

        public bool IsEmpty => Hi == 0 && Lo == 0;

        /// <summary>Из 16 байт (например, от криптографического генератора платформы).</summary>
        public static HostToken FromBytes(ReadOnlySpan<byte> b)
        {
            if (b.Length != Bytes)
            {
                throw new ArgumentException($"Токен — {Bytes} байт, а не {b.Length}.", nameof(b));
            }

            ulong hi = 0;
            ulong lo = 0;
            for (int i = 0; i < 8; i++)
            {
                hi = (hi << 8) | b[i];
                lo = (lo << 8) | b[8 + i];
            }

            return new HostToken(hi, lo);
        }

        /// <summary>32 шестнадцатеричные цифры, не нули.</summary>
        public static bool TryParse(string? s, out HostToken token)
        {
            token = default;
            if (s == null || s.Length != HexLength)
            {
                return false;
            }

            if (!ulong.TryParse(s.Substring(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hi)
                || !ulong.TryParse(s.Substring(16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong lo))
            {
                return false;
            }

            token = new HostToken(hi, lo);
            return !token.IsEmpty;
        }

        public void Write(ref BitWriter w)
        {
            w.WriteUInt64(Hi);
            w.WriteUInt64(Lo);
        }

        public static HostToken Read(ref BitReader r)
        {
            ulong hi = r.ReadUInt64();
            ulong lo = r.ReadUInt64();
            return new HostToken(hi, lo);
        }

        public bool Equals(HostToken other) => Hi == other.Hi && Lo == other.Lo;
        public override bool Equals(object? obj) => obj is HostToken other && Equals(other);
        public override int GetHashCode() => unchecked((int)(Hi ^ Lo ^ ((Hi ^ Lo) >> 32)));
        public static bool operator ==(HostToken a, HostToken b) => a.Equals(b);
        public static bool operator !=(HostToken a, HostToken b) => !a.Equals(b);
        public override string ToString() => Hi.ToString("x16", CultureInfo.InvariantCulture) + Lo.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>«Открыть для друзей» (0-024): только тип; принимается только от хозяина, остальным — молча мимо.</summary>
    public struct OpenForFriendsMessage
    {
        public const int Bytes = 1;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.OpenForFriends);
            return w.BytesWritten;
        }

        public static OpenForFriendsMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.OpenForFriends);
            r.ExpectEnd();
            return default;
        }
    }

    /// <summary>Итог «Открыть для друзей»: открыт ли сервер для других машин и на каком UDP-порту.</summary>
    public struct OpenResultMessage
    {
        /// <summary>Тип 1 + открыт 1 + порт 2.</summary>
        public const int Bytes = 4;

        public bool Opened;
        public ushort Port;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.OpenResult);
            w.WriteByte(Opened ? (byte)1 : (byte)0);
            w.WriteUInt16(Port);
            return w.BytesWritten;
        }

        public static OpenResultMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.OpenResult);
            byte opened = r.ReadByte();
            if (opened > 1)
            {
                throw new ProtocolException($"Итог открытия: флаг {opened}.");
            }

            var m = new OpenResultMessage { Opened = opened == 1, Port = r.ReadUInt16() };
            r.ExpectEnd();
            return m;
        }
    }

    /// <summary>Почему сервер закрывает соединение (0-024, docs/07 7.6).</summary>
    public enum ClosingReason : byte
    {
        /// <summary>Хост вышел (не вернулся за 60 с после падения игры или вышел сам): мир сохранён у него.</summary>
        HostLeft = 1,

        /// <summary>Сервер остановлен (команда, Ctrl+C).</summary>
        Stopped = 2,

        /// <summary>Вход с тем же токеном с другого места вытеснил это соединение (7.7).</summary>
        Replaced = 3,
    }

    /// <summary>Мир закрывается: причина — в данных отключения, клиент показывает текст вместо «связь потеряна».</summary>
    public struct ClosingMessage
    {
        /// <summary>Тип 1 + причина 1.</summary>
        public const int Bytes = 2;

        public ClosingReason Reason;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Closing);
            w.WriteByte((byte)Reason);
            return w.BytesWritten;
        }

        public static ClosingMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Closing);
            byte reason = r.ReadByte();
            if (reason < (byte)ClosingReason.HostLeft || reason > (byte)ClosingReason.Replaced)
            {
                throw new ProtocolException($"Мир закрывается: причина {reason}.");
            }

            r.ExpectEnd();
            return new ClosingMessage { Reason = (ClosingReason)reason };
        }

        /// <summary>
        /// Текст для игрока (7.6). «Хост вышел» — по документу; остальные — допущение 0-024 до ответа на вопрос
        /// <c>[док]</c> карточки (тексты падений).
        /// </summary>
        public string Describe()
        {
            switch (Reason)
            {
                case ClosingReason.HostLeft:
                    return "Хост вышел. Мир сохранён у него";
                case ClosingReason.Replaced:
                    return "Вы вошли с другого компьютера";
                default:
                    return "Сервер остановлен. Мир сохранён";
            }
        }
    }
}
