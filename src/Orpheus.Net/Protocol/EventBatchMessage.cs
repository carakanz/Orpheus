using System;
using Orpheus.Core.World;

namespace Orpheus.Net.Protocol
{
    /// <summary>Вид записи в «пачке событий» (40, C-035): тело — байты одиночного сообщения без байта типа.</summary>
    public enum EventRecordKind : byte
    {
        /// <summary>«Урон» (37) — тело 12 байт.</summary>
        Damage = 0,

        /// <summary>«Снаряд» (38) — тело 33 (выстрел) или 19 (конец), вид по телу[0] = оп.</summary>
        Projectile = 1,

        /// <summary>«Взмах» (39) — тело 7 + 13 × n, n = телу[6] (≤ <see cref="PickStrike.MaxTiles"/>).</summary>
        Strike = 2,
    }

    /// <summary>
    /// «Пачка событий» (C-035, тип 40, канал 2): события такта 37/38/39 идут получателю одним сообщением —
    /// тип 1 + число записей 1 + записи: вид 1 + тело. Тело — байты прежнего одиночного сообщения БЕЗ байта типа:
    /// «урон» (37) — 12 байт; «снаряд» (38) — 33 (выстрел) / 19 (конец); «взмах» (39) — 7 + 13 × n (n = телу[6]).
    /// Записи идут в порядке возникновения событий. Не больше <see cref="MaxRecords"/> записей и
    /// <see cref="MaxBodyBytes"/> байт тела на пачку — влезает в один пакет (Mtu 1232); переполнение — сервер
    /// начинает вторую пачку. «Смерть»/«возрождение» (24/25) в пачку НЕ входят: сервер сбрасывает пачку до их
    /// отправки, порядок за тактом сохраняется. Клиент разбирает записи и передаёт прежним обработчикам.
    /// </summary>
    public struct EventBatchMessage
    {
        public const int HeaderBytes = 2;
        public const int MaxRecords = 255;

        /// <summary>Предел тела одной пачки, байт; с записями (вид + тело) — ровно этот объём staging сервера.</summary>
        public const int MaxBodyBytes = 1024;

        /// <summary>Наибольший размер пачки: заголовок 2 + staging (тело + место под запись, не влезшую в лимит).</summary>
        public const int MaxBytes = HeaderBytes + MaxBodyBytes + 40;

        /// <summary>
        /// Число байт тела записи по виду и полям (снаряд — оп в теле[0], взмах — число тайлов в теле[6]);
        /// вид/оп/число вне схемы или тело короче — <see cref="ProtocolException"/>.
        /// </summary>
        public static int RecordBodyBytes(EventRecordKind kind, ReadOnlySpan<byte> body)
        {
            switch (kind)
            {
                case EventRecordKind.Damage:
                    if (body.Length < DamageMessage.Bytes - 1)
                    {
                        throw new ProtocolException($"Пачка: урон {body.Length} байт, нужно {DamageMessage.Bytes - 1}.");
                    }

                    return DamageMessage.Bytes - 1;
                case EventRecordKind.Projectile:
                    if (body.Length < 1)
                    {
                        throw new ProtocolException("Пачка: снаряд без операции.");
                    }

                    int shot = body[0] == (byte)ProjectileOp.Shot ? ProjectileMessage.ShotBytes - 1
                        : body[0] == (byte)ProjectileOp.Stop ? ProjectileMessage.StopBytes - 1 : -1;
                    if (shot < 0)
                    {
                        throw new ProtocolException($"Пачка: снаряд, операция {body[0]}.");
                    }

                    if (body.Length < shot)
                    {
                        throw new ProtocolException($"Пачка: снаряд {body.Length} байт, нужно {shot}.");
                    }

                    return shot;
                case EventRecordKind.Strike:
                    if (body.Length < StrikeMessage.HeaderBytes - 1)
                    {
                        throw new ProtocolException($"Пачка: взмах {body.Length} байт, нужно {StrikeMessage.HeaderBytes - 1}.");
                    }

                    int n = body[6];
                    if (n > PickStrike.MaxTiles)
                    {
                        throw new ProtocolException($"Пачка: взмах, тайлов {n}.");
                    }

                    int full = StrikeMessage.HeaderBytes - 1 + StrikeMessage.TileBytes * n;
                    if (body.Length < full)
                    {
                        throw new ProtocolException($"Пачка: взмах {body.Length} байт, нужно {full}.");
                    }

                    return full;
                default:
                    throw new ProtocolException($"Пачка: вид {(byte)kind}.");
            }
        }

        /// <summary>
        /// Пачка из готовых записей (каждая: вид 1 + тело, в порядке событий): тип + число + записи.
        /// Запись короче схемы или лишние байты в хвосте — <see cref="InvalidOperationException"/> (ошибка в коде сервера).
        /// </summary>
        public static int Write(Span<byte> dest, ReadOnlySpan<byte> records)
        {
            int count = 0;
            for (int i = 0; i < records.Length;)
            {
                byte kind = records[i];
                int body;
                try
                {
                    body = RecordBodyBytes((EventRecordKind)kind, records.Slice(i + 1));
                }
                catch (ProtocolException e)
                {
                    throw new InvalidOperationException($"Пачка: запись {i} — битая ({e.Message}).", e);
                }

                if (records.Slice(i + 1).Length < body)
                {
                    throw new InvalidOperationException($"Пачка: запись {kind} — тело {body} байт, в staging недобор.");
                }

                i += 1 + body;
                count++;
            }

            if (count == 0 || count > MaxRecords)
            {
                throw new InvalidOperationException($"Пачка: записей {count}.");
            }

            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.EventBatch);
            w.WriteByte((byte)count);
            w.WriteBytes(records);
            return w.BytesWritten;
        }

        /// <summary>Проверка заголовка; читатель записей — <see cref="Open"/>.</summary>
        public static int ReadCount(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.EventBatch);
            int count = r.ReadByte();
            if (count == 0)
            {
                throw new ProtocolException("Пачка: ноль записей.");
            }

            return count;
        }

        public static EventBatchReader Open(ReadOnlySpan<byte> src) => new EventBatchReader(src, ReadCount(src));
    }

    /// <summary>
    /// Читатель записей «пачки событий» (40): <see cref="TryNext"/> по порядку; <see cref="ExpectEnd"/> — счётчик
    /// сошёлся и хвоста нет. Вид вне 0…2, оп снаряда вне 1…2, телец меньше схемы, недобор по числу записей,
    /// лишние байты — <see cref="ProtocolException"/>.
    /// </summary>
    public ref struct EventBatchReader
    {
        private readonly ReadOnlySpan<byte> _src;
        private int _pos;
        private int _left;

        internal EventBatchReader(ReadOnlySpan<byte> src, int count)
        {
            _src = src;
            _pos = EventBatchMessage.HeaderBytes; // после типа и числа: все поля пачки байт-выровнены
            _left = count;
        }

        /// <summary>Следующая запись; их больше нет — false (проверить <see cref="ExpectEnd"/>).</summary>
        public bool TryNext(out EventRecordKind kind, out ReadOnlySpan<byte> body)
        {
            if (_left == 0)
            {
                kind = default;
                body = default;
                return false;
            }

            _left--;
            if (_pos >= _src.Length)
            {
                throw new ProtocolException("Пачка: короче числа записей.");
            }

            kind = (EventRecordKind)_src[_pos];
            if ((byte)kind > (byte)EventRecordKind.Strike)
            {
                throw new ProtocolException($"Пачка: вид {(byte)kind}.");
            }

            int n = EventBatchMessage.RecordBodyBytes(kind, _src.Slice(_pos + 1));
            if (_pos + 1 + n > _src.Length)
            {
                throw new ProtocolException($"Пачка: запись {(byte)kind} оборвана.");
            }

            body = _src.Slice(_pos + 1, n);
            _pos += 1 + n;
            return true;
        }

        /// <summary>Записи кончились ровно на конце сообщения.</summary>
        public void ExpectEnd()
        {
            if (_left != 0)
            {
                throw new ProtocolException($"Пачка: не хватает {_left} записей.");
            }

            if (_pos != _src.Length)
            {
                throw new ProtocolException($"Пачка: хвост {_src.Length - _pos} байт.");
            }
        }
    }
}
