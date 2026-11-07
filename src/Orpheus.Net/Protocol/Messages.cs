using System;
using System.Text;
using Orpheus.Core.Combat;
using Orpheus.Core.Sim;
using Orpheus.Core.Geometry;

namespace Orpheus.Net.Protocol
{
    /// <summary>Тип сообщения — первый байт каждого сообщения (docs/07, 2.10). Схема — в этом файле одним местом.</summary>
    public enum MessageType : byte
    {
        /// <summary>Клиент → сервер, канал 2: вход (версия, имя, токен хозяина — если есть; 0-024).</summary>
        Login = 1,

        /// <summary>Сервер → клиент, канал 2: вход принят (сущность, такт, мир).</summary>
        LoginAccept = 2,

        /// <summary>Сервер → клиент, в данных отключения: вход отклонён (причина, версия сервера).</summary>
        LoginReject = 3,

        /// <summary>Клиент → сервер, канал 0, 60 Гц: 4 кадра ввода.</summary>
        Input = 4,

        /// <summary>Сервер → клиент, канал 0, 30 Гц: полный снимок сущностей рядом.</summary>
        Snapshot = 5,

        /// <summary>Сервер → клиент, канал 2: часы — номер такта сервера.</summary>
        Clock = 6,

        /// <summary>Сервер → клиент, 30 Гц: дельта к подтверждённому снимку (0-014, 4.3): удалённые id + изменившиеся сущности.</summary>
        SnapshotDelta = 7,

        /// <summary>Клиент-хозяин → сервер, канал 2: «Открыть для друзей» (0-024, docs/07 2.3 и 2.13 п. 9).</summary>
        OpenForFriends = 8,

        /// <summary>Сервер → клиент-хозяин, канал 2: итог «Открыть для друзей» — открыт ли и на каком порту.</summary>
        OpenResult = 9,

        /// <summary>Сервер → клиент, в данных отключения: мир закрывается — хост вышел, сервер остановлен, вход с другого места (0-024, 7.6).</summary>
        Closing = 10,

        /// <summary>Клиент → сервер, канал 0, раз в 1 с: пинг часов (2.4, 2.10) — метка времени клиента.</summary>
        Ping = 11,

        /// <summary>Сервер → клиент, канал 0: ответ на пинг — метка клиента и кадр сервера сейчас (часы клиента, 2.4).</summary>
        Pong = 12,

        /// <summary>
        /// Сервер → клиент, канал 0, каждый такт замаха: «замах начат» (0-029; `02` 15.1 — событие с серверным временем
        /// начала): кто, кадр начала s, длина замаха d, сектор. Повтор каждый такт — потеря пакета не теряет замах.
        /// </summary>
        SwingStart = 13,

        /// <summary>Сервер → клиент, канал 2: удар врага по этому игроку решён (0-029; `07` 2.7 «Решение»): попал / нет, ожидание.</summary>
        MeleeResult = 14,

        // 15 не занят: неизвестный тип. Новые типы — в Codec.IsKnown.

        // 0-026, мир по сети (docs/07, 3.5–3.7), канал 1. Номера с 16 (13–14 — у 0-029, 15 не занят): так их и слали ветки до
        // слияния с 0-025, «золотые» байты те же. Последний тип — ChunkRequest.

        /// <summary>Сервер → клиент, канал 1: чанк целиком — заголовок и сжатая копия (3.6).</summary>
        Chunk = 16,

        /// <summary>Сервер → клиент, канал 1: пачка правок чанка за такт (3.7).</summary>
        ChunkPatch = 17,

        /// <summary>Сервер → клиент, канал 1: новый центр окна — клиент выбрасывает чанки вне 8 × 6 вокруг него (3.5).</summary>
        ChunkWindow = 18,

        /// <summary>Клиент → сервер, канал 1: «пришли чанк целиком» — пачка не к той ревизии (3.7 «Порядок»).</summary>
        ChunkRequest = 19,

        // 0-028, правки тайлов (docs/07, 3.7), канал 2. Номера с 20 (протокол 7).

        /// <summary>Клиент → сервер, канал 2: запрос правки — копать (начало, конец) или поставить линию (3.7).</summary>
        TileRequest = 20,

        /// <summary>Сервер → клиент, канал 2: ответ на запрос правки — «принято» или «отказ: причина» (3.7 «Ответ»).</summary>
        TileReply = 21,
    }

    /// <summary>
    /// Версия сборки = версия протокола (docs/07, 2.10): клиент и сервер разных сборок не соединяются,
    /// игрок видит «Версии не совпадают: у сервера 0.4.2, у тебя 0.4.1».
    /// </summary>
    public readonly struct NetVersion : IEquatable<NetVersion>
    {
        public const int WireBytes = 6;

        public readonly ushort Major;
        public readonly ushort Minor;
        public readonly ushort Patch;

        public NetVersion(ushort major, ushort minor, ushort patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public void Write(ref BitWriter w)
        {
            w.WriteUInt16(Major);
            w.WriteUInt16(Minor);
            w.WriteUInt16(Patch);
        }

        public static NetVersion Read(ref BitReader r) => new NetVersion(r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());

        public bool Equals(NetVersion other) => Major == other.Major && Minor == other.Minor && Patch == other.Patch;
        public override bool Equals(object? obj) => obj is NetVersion other && Equals(other);
        public override int GetHashCode() => (Major << 20) ^ (Minor << 10) ^ Patch;
        public static bool operator ==(NetVersion a, NetVersion b) => a.Equals(b);
        public static bool operator !=(NetVersion a, NetVersion b) => !a.Equals(b);
        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    /// <summary>
    /// Вход: версия, имя игрока (UTF-8, 1…<see cref="MaxNameBytes"/> байт) и токен хозяина (0-024, docs/07 2.3): игра,
    /// запустившая сервер, входит с одноразовым токеном из ключей сервера и получает флаг «хозяин»; остальные — без токена.
    /// </summary>
    public struct LoginMessage
    {
        public const int MaxNameBytes = 32;

        public NetVersion Version;
        public string Name;

        /// <summary>Токен хозяина; пустой — обычный игрок.</summary>
        public HostToken Token;

        /// <summary>Наибольший размер: тип 1 + версия 6 + длина 1 + имя 32 + флаг токена 1 + токен 16.</summary>
        public const int MaxBytes = 1 + NetVersion.WireBytes + 1 + MaxNameBytes + 1 + HostToken.Bytes;

        /// <summary>
        /// Проверка имени до отправки (0-017, замечание 6 проверки 0-012): null — годится; иначе текст для игрока.
        /// Предел — байты UTF-8, как в протоколе, а не символы: «Орфей» — 10 байт, кириллицей входит 16 букв.
        /// </summary>
        public static string? NameError(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Имя пустое: нужно 1…" + MaxNameBytes + " байт UTF-8.";
            }

            int n = Encoding.UTF8.GetByteCount(name);
            return n > MaxNameBytes
                ? $"Имя «{name}» — {n} байт UTF-8, а можно не больше {MaxNameBytes} (кириллица — 2 байта на букву: до 16 букв)."
                : null;
        }

        public int Write(Span<byte> dest)
        {
            byte[] name = Encoding.UTF8.GetBytes(Name ?? string.Empty); // вход — раз за сессию, выделение не важно
            int n = name.Length;
            if (n == 0 || n > MaxNameBytes)
            {
                throw new ArgumentException($"Имя — 1…{MaxNameBytes} байт UTF-8, а не {n}.");
            }

            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Login);
            Version.Write(ref w);
            w.WriteByte((byte)n);
            w.WriteBytes(name);
            w.WriteByte(Token.IsEmpty ? (byte)0 : (byte)1);
            if (!Token.IsEmpty)
            {
                Token.Write(ref w);
            }

            return w.BytesWritten;
        }

        /// <summary>
        /// Только версия из входа — до разбора остального (замечание 5 проверки 0-024): формат входа меняется от
        /// протокола к протоколу (в 3 — флаг токена), и клиент другой сборки должен видеть «Версии не совпадают»,
        /// а не «Вход не удался». Тип и версия (первые 7 байт) — неизменны во всех протоколах.
        /// </summary>
        public static NetVersion ReadVersion(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Login);
            return NetVersion.Read(ref r);
        }

        public static LoginMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Login);
            NetVersion v = NetVersion.Read(ref r);
            int n = r.ReadByte();
            if (n == 0 || n > MaxNameBytes)
            {
                throw new ProtocolException($"Вход: длина имени {n} вне 1…{MaxNameBytes}.");
            }

            byte[] name = new byte[n];
            r.ReadBytes(name);
            byte hasToken = r.ReadByte();
            if (hasToken > 1)
            {
                throw new ProtocolException($"Вход: флаг токена {hasToken}.");
            }

            HostToken token = hasToken == 1 ? HostToken.Read(ref r) : default;
            if (hasToken == 1 && token.IsEmpty)
            {
                throw new ProtocolException("Вход: токен хозяина из нулей.");
            }

            r.ExpectEnd();
            return new LoginMessage { Version = v, Name = Encoding.UTF8.GetString(name), Token = token };
        }
    }

    /// <summary>
    /// Вход принят: своя сущность, такт сервера, форма и зерно мира, хэш снимка генерации (0-017: клиент, генерирующий
    /// мир по зерну, сверяет свой — <c>StateHash.Generation</c>; docs/07, 3.4, backlog [док]).
    /// </summary>
    public struct LoginAcceptMessage
    {
        /// <summary>Тип 1 + сущность 4 + такт 4 + ширина 4 + высота 4 + тор 1 + зерно 8 + частота 1 + хэш генерации 8.</summary>
        public const int Bytes = 35;

        public int EntityId;
        public uint ServerTick;
        public int MapWidth;
        public int MapHeight;
        public bool Wraps;
        public ulong WorldSeed;
        public byte TickRate;

        /// <summary>Хэш снимка генерации мира сервера (до правок): <c>StateHash.Generation</c>.</summary>
        public ulong GenerationHash;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.LoginAccept);
            w.WriteUInt32((uint)EntityId);
            w.WriteUInt32(ServerTick);
            w.WriteUInt32((uint)MapWidth);
            w.WriteUInt32((uint)MapHeight);
            w.WriteByte(Wraps ? (byte)1 : (byte)0);
            w.WriteUInt64(WorldSeed);
            w.WriteByte(TickRate);
            w.WriteUInt64(GenerationHash);
            return w.BytesWritten;
        }

        public static LoginAcceptMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.LoginAccept);
            var m = new LoginAcceptMessage
            {
                EntityId = (int)r.ReadUInt32(),
                ServerTick = r.ReadUInt32(),
                MapWidth = (int)r.ReadUInt32(),
                MapHeight = (int)r.ReadUInt32(),
            };
            byte wraps = r.ReadByte();
            if (wraps > 1)
            {
                throw new ProtocolException($"Вход принят: флаг тора {wraps}.");
            }

            m.Wraps = wraps == 1;
            m.WorldSeed = r.ReadUInt64();
            m.TickRate = r.ReadByte();
            m.GenerationHash = r.ReadUInt64();
            r.ExpectEnd();
            if (m.MapWidth <= 0 || m.MapHeight <= 0 || m.EntityId <= 0)
            {
                throw new ProtocolException($"Вход принят: мир {m.MapWidth}×{m.MapHeight}, сущность {m.EntityId}.");
            }

            return m;
        }
    }

    /// <summary>Почему вход отклонён.</summary>
    public enum LoginRejectReason : byte
    {
        /// <summary>Версии сборок не совпадают.</summary>
        VersionMismatch = 1,

        /// <summary>Мест нет.</summary>
        ServerFull = 2,

        /// <summary>Имя пустое или длиннее 32 байт.</summary>
        BadName = 3,

        /// <summary>Сообщение входа испорчено или пришло не первым.</summary>
        BadLogin = 4,

        /// <summary>Токен хозяина не тот (0-024).</summary>
        BadToken = 5,
    }

    /// <summary>Вход отклонён: причина и версия сервера — для «Версии не совпадают: у сервера …, у тебя …».</summary>
    public struct LoginRejectMessage
    {
        /// <summary>Тип 1 + причина 1 + версия 6.</summary>
        public const int Bytes = 8;

        public LoginRejectReason Reason;
        public NetVersion ServerVersion;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.LoginReject);
            w.WriteByte((byte)Reason);
            ServerVersion.Write(ref w);
            return w.BytesWritten;
        }

        public static LoginRejectMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.LoginReject);
            byte reason = r.ReadByte();
            if (reason < (byte)LoginRejectReason.VersionMismatch || reason > (byte)LoginRejectReason.BadToken)
            {
                throw new ProtocolException($"Отказ входа: причина {reason}.");
            }

            var m = new LoginRejectMessage { Reason = (LoginRejectReason)reason, ServerVersion = NetVersion.Read(ref r) };
            r.ExpectEnd();
            return m;
        }

        /// <summary>Текст для игрока.</summary>
        public string Describe(NetVersion mine)
        {
            // Сервер другой сборки мог не разобрать наш вход и ответить «вход испорчен» (сервер протокола 2 не знает флага
            // токена) — версия в отказе всё равно его: о ней игроку и сказать.
            if (ServerVersion != mine)
            {
                return $"Версии не совпадают: у сервера {ServerVersion}, у тебя {mine}";
            }

            switch (Reason)
            {
                case LoginRejectReason.VersionMismatch:
                    return $"Версии не совпадают: у сервера {ServerVersion}, у тебя {mine}";
                case LoginRejectReason.ServerFull:
                    return "Сервер полон";
                case LoginRejectReason.BadName:
                    return "Имя не подходит: 1–32 байта";
                case LoginRejectReason.BadToken:
                    return "Токен хозяина не подходит: мир открыт другой игрой";
                default:
                    return "Вход не удался";
            }
        }
    }

    /// <summary>
    /// Кадр ввода по сети (docs/07, 2.10): направление и сила движения (2 байта), угол прицела (2 байта),
    /// кнопки (2 байта). Тайл под курсором при молоте — потом (задача строительства).
    /// </summary>
    public struct WireFrame : IEquatable<WireFrame>
    {
        public const int Bytes = 6;

        /// <summary>Сила движения «полностью».</summary>
        public const byte FullPower = 255;

        /// <summary>Направление: 0 — стойка, 1…8 — как <see cref="InputFrame.DirectionCode"/>.</summary>
        public byte Direction;

        /// <summary>Сила движения 0…255 (стик); клавиши — 255. Пока не влияет на скорость.</summary>
        public byte Power;

        /// <summary>Угол прицела, u16 (65 536 шагов на круг, 0,0055°).</summary>
        public ushort Aim;

        /// <summary>Кнопки — битовая маска.</summary>
        public ushort Buttons;

        public static WireFrame From(InputFrame f, ushort aim = 0) => new WireFrame
        {
            Direction = f.DirectionCode,
            Power = f.Direction.IsMoving ? FullPower : (byte)0,
            Aim = aim,
            Buttons = f.Buttons,
        };

        /// <summary>Кадр такта: направление и кнопки (код направления вне 0…8 — <see cref="ProtocolException"/>).</summary>
        public InputFrame ToInputFrame()
        {
            if (Direction > InputFrame.MaxDirectionCode)
            {
                throw new ProtocolException($"Кадр ввода: код направления {Direction}.");
            }

            return InputFrame.FromCode(Direction, Buttons);
        }

        public void Write(ref BitWriter w)
        {
            w.WriteByte(Direction);
            w.WriteByte(Power);
            w.WriteUInt16(Aim);
            w.WriteUInt16(Buttons);
        }

        public static WireFrame Read(ref BitReader r) => new WireFrame
        {
            Direction = r.ReadByte(),
            Power = r.ReadByte(),
            Aim = r.ReadUInt16(),
            Buttons = r.ReadUInt16(),
        };

        public bool Equals(WireFrame o) => Direction == o.Direction && Power == o.Power && Aim == o.Aim && Buttons == o.Buttons;
        public override bool Equals(object? obj) => obj is WireFrame o && Equals(o);
        public override int GetHashCode() => (Direction << 24) ^ (Power << 16) ^ (Aim << 8) ^ Buttons;
        public override string ToString() => $"frame(dir {Direction}, power {Power}, aim {Aim}, 0x{Buttons:X4})";
    }

    /// <summary>
    /// Пакет ввода, 60 Гц, канал 0 (docs/07, 2.10): номер первого кадра, 4 кадра — три прошлых и текущий
    /// (потеря пакета не теряет нажатие), время картинки, номер последнего полученного снимка, опережение L.
    /// Ровно <see cref="Bytes"/> = 38 байт.
    /// </summary>
    public struct InputMessage
    {
        /// <summary>Кадров в пакете.</summary>
        public const int FrameCount = 4;

        /// <summary>Тип 1 + первый кадр 4 + 4 × 6 + время картинки 4 + последний снимок 4 + опережение 1 = 38.</summary>
        public const int Bytes = 1 + 4 + FrameCount * WireFrame.Bytes + 4 + 4 + 1;

        /// <summary>Номер самого старого кадра в пакете; текущий — <c>FirstFrame + 3</c>.</summary>
        public uint FirstFrame;

        public WireFrame Frame0;
        public WireFrame Frame1;
        public WireFrame Frame2;

        /// <summary>Текущий (новейший) кадр.</summary>
        public WireFrame Frame3;

        /// <summary>Время картинки клиента — в кадрах сервера (60 Гц).</summary>
        public uint PictureTime;

        /// <summary>Номер (такт) последнего полученного снимка.</summary>
        public uint LastSnapshot;

        /// <summary>Опережение L в кадрах (2.7; 0-033): 0…<see cref="MaxLeadFrames"/>.</summary>
        public sbyte Lead;

        /// <summary>Опережение не больше 400 мс = 24 кадра (2.7, 4.6).</summary>
        public const int MaxLeadFrames = 24;

        /// <summary>Номер текущего кадра.</summary>
        public uint LastFrame => FirstFrame + FrameCount - 1;

        /// <summary>Кадр № <paramref name="i"/> (0 — старейший, 3 — текущий).</summary>
        public WireFrame this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0: return Frame0;
                    case 1: return Frame1;
                    case 2: return Frame2;
                    case 3: return Frame3;
                    default: throw new ArgumentOutOfRangeException(nameof(i));
                }
            }

            set
            {
                switch (i)
                {
                    case 0: Frame0 = value; break;
                    case 1: Frame1 = value; break;
                    case 2: Frame2 = value; break;
                    case 3: Frame3 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(i));
                }
            }
        }

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Input);
            w.WriteUInt32(FirstFrame);
            Frame0.Write(ref w);
            Frame1.Write(ref w);
            Frame2.Write(ref w);
            Frame3.Write(ref w);
            w.WriteUInt32(PictureTime);
            w.WriteUInt32(LastSnapshot);
            w.WriteSByte(Lead);
            return w.BytesWritten;
        }

        public static InputMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Input);
            var m = new InputMessage
            {
                FirstFrame = r.ReadUInt32(),
                Frame0 = WireFrame.Read(ref r),
                Frame1 = WireFrame.Read(ref r),
                Frame2 = WireFrame.Read(ref r),
                Frame3 = WireFrame.Read(ref r),
                PictureTime = r.ReadUInt32(),
                LastSnapshot = r.ReadUInt32(),
                Lead = r.ReadSByte(),
            };
            r.ExpectEnd();
            if (m.Lead < 0 || m.Lead > MaxLeadFrames)
            {
                throw new ProtocolException($"Ввод: опережение {m.Lead} вне 0…{MaxLeadFrames} кадров.");
            }

            return m;
        }
    }

    /// <summary>Часы (docs/07, 2.4): номер такта сервера — при входе и после перегрузки (такты не догнаны).</summary>
    public struct ClockMessage
    {
        /// <summary>Тип 1 + такт 4.</summary>
        public const int Bytes = 5;

        public uint ServerTick;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Clock);
            w.WriteUInt32(ServerTick);
            return w.BytesWritten;
        }

        public static ClockMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Clock);
            var m = new ClockMessage { ServerTick = r.ReadUInt32() };
            r.ExpectEnd();
            return m;
        }
    }

    /// <summary>
    /// Пинг часов (docs/07, 2.4 «Часы клиента», 2.10 <c>PingInterval</c> 1 с; 0-025): клиент шлёт свою метку времени (младшие
    /// 32 бита мкс), сервер отвечает <see cref="PongMessage"/> сразу при разборе — RTT и кадр сервера.
    /// </summary>
    public struct PingMessage
    {
        /// <summary>Тип 1 + метка 4.</summary>
        public const int Bytes = 5;

        public uint ClientUs;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Ping);
            w.WriteUInt32(ClientUs);
            return w.BytesWritten;
        }

        public static PingMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Ping);
            var m = new PingMessage { ClientUs = r.ReadUInt32() };
            r.ExpectEnd();
            return m;
        }
    }

    /// <summary>Ответ на пинг (0-025): метка клиента как пришла и кадр сервера сейчас — такт, который идёт (последний шагнутый + 1).</summary>
    public struct PongMessage
    {
        /// <summary>Тип 1 + метка 4 + такт 4.</summary>
        public const int Bytes = 9;

        public uint ClientUs;
        public uint ServerTick;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Pong);
            w.WriteUInt32(ClientUs);
            w.WriteUInt32(ServerTick);
            return w.BytesWritten;
        }

        public static PongMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Pong);
            var m = new PongMessage { ClientUs = r.ReadUInt32(), ServerTick = r.ReadUInt32() };
            r.ExpectEnd();
            return m;
        }
    }

    /// <summary>
    /// «Замах начат» (0-029, тип 13): ближний удар врага — номер атакующего, кадр начала s (время сервера, кадры 60 Гц),
    /// длина замаха d, сектор (центр без кванта, ось, досягаемость, половина дуги). Клиент показывает замах «сейчас»:
    /// конец — в его кадре s + d (`07` 2.9, в шипе без сдвига D_p).
    /// </summary>
    public struct SwingStartMessage
    {
        /// <summary>Тип 1 + атакующий 4 + s 4 + d 1 + центр 8 + ось 2 + досягаемость 2 + полудуга 2 + D_p 1 (0-033).</summary>
        public const int Bytes = 25;

        public int AttackerId;
        public uint StartFrame;
        public byte WindupFrames;
        public WorldPos Origin;
        public ushort Axis;
        public ushort ReachUnits;
        public ushort HalfArcSteps;

        /// <summary>
        /// Задержка показа D_p получателя, кадров (0-033, `07` 2.7; ≤ <see cref="ThreatTiming.MaxDelayFrames"/>): своя у
        /// каждого игрока, с ней сервер решает удар по нему.
        /// </summary>
        public byte DelayFrames;

        /// <summary>Кадр конца замаха s + d.</summary>
        public uint ContactFrame => unchecked(StartFrame + WindupFrames);

        /// <summary>
        /// Кадр решения удара по получателю — и конец замаха на его экране (2.7, 2.9): s + max(d, D_p + min(d; 0,3 с)),
        /// во времени его персонажа (номер кадра ввода).
        /// </summary>
        public uint ResolveFrame => ThreatTiming.ResolveFrame(StartFrame, WindupFrames, DelayFrames);

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.SwingStart);
            w.WriteUInt32((uint)AttackerId);
            w.WriteUInt32(StartFrame);
            w.WriteByte(WindupFrames);
            w.WriteUInt32((uint)Origin.X);
            w.WriteUInt32((uint)Origin.Y);
            w.WriteUInt16(Axis);
            w.WriteUInt16(ReachUnits);
            w.WriteUInt16(HalfArcSteps);
            w.WriteByte(DelayFrames);
            return w.BytesWritten;
        }

        public static SwingStartMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.SwingStart);
            var m = new SwingStartMessage
            {
                AttackerId = (int)r.ReadUInt32(),
                StartFrame = r.ReadUInt32(),
                WindupFrames = r.ReadByte(),
                Origin = new WorldPos((int)r.ReadUInt32(), (int)r.ReadUInt32()),
                Axis = r.ReadUInt16(),
                ReachUnits = r.ReadUInt16(),
                HalfArcSteps = r.ReadUInt16(),
                DelayFrames = r.ReadByte(),
            };
            r.ExpectEnd();
            if (m.Origin.X < 0 || m.Origin.Y < 0 || m.WindupFrames == 0 || m.DelayFrames > ThreatTiming.MaxDelayFrames)
            {
                throw new ProtocolException($"Замах: центр {m.Origin}, замах {m.WindupFrames} или D_p {m.DelayFrames} вне пределов.");
            }

            return m;
        }

        public override string ToString() => $"замах #{AttackerId}: s {StartFrame}, d {WindupFrames}, {Origin} ось {Axis}, D {DelayFrames}";
    }

    /// <summary>
    /// «Удар решён» (0-029, тип 14): итог угрозы по этому игроку — попал или нет; решён ли по последнему известному
    /// (ввода кадра конца замаха не дождались за W); сколько кадров сервер ждал после конца замаха и сколько кадров ввода
    /// не хватило; кадр сервера в миг решения.
    /// </summary>
    public struct MeleeResultMessage
    {
        /// <summary>Тип 1 + атакующий 4 + s 4 + флаги 1 + ждал 1 + не хватило 1 + кадр решения 4.</summary>
        public const int Bytes = 16;

        public int AttackerId;
        public uint StartFrame;
        public bool Hit;

        /// <summary>Решён по последнему известному: ввод кадра конца замаха не пришёл за W.</summary>
        public bool ByLastKnown;

        /// <summary>Кадров сервер ждал ввода после конца замаха (по времени сервера), 0…W.</summary>
        public byte WaitedFrames;

        /// <summary>Кадров ввода до конца замаха не хватило в миг решения (0 — дождался).</summary>
        public byte MissingFrames;

        /// <summary>Кадр сервера в миг решения.</summary>
        public uint DecidedAtFrame;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.MeleeResult);
            w.WriteUInt32((uint)AttackerId);
            w.WriteUInt32(StartFrame);
            w.WriteByte((byte)((Hit ? 1 : 0) | (ByLastKnown ? 2 : 0)));
            w.WriteByte(WaitedFrames);
            w.WriteByte(MissingFrames);
            w.WriteUInt32(DecidedAtFrame);
            return w.BytesWritten;
        }

        public static MeleeResultMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.MeleeResult);
            var m = new MeleeResultMessage { AttackerId = (int)r.ReadUInt32(), StartFrame = r.ReadUInt32() };
            byte flags = r.ReadByte();
            if (flags > 3)
            {
                throw new ProtocolException($"Удар решён: флаги {flags}.");
            }

            m.Hit = (flags & 1) != 0;
            m.ByLastKnown = (flags & 2) != 0;
            m.WaitedFrames = r.ReadByte();
            m.MissingFrames = r.ReadByte();
            m.DecidedAtFrame = r.ReadUInt32();
            r.ExpectEnd();
            return m;
        }

        public override string ToString() =>
            $"удар #{AttackerId} s {StartFrame}: {(Hit ? "попал" : "мимо")}{(ByLastKnown ? ", по последнему известному" : "")}, ждал {WaitedFrames}, не хватило {MissingFrames}";
    }

    /// <summary>Общее для кодека: тип сообщения.</summary>
    public static class Codec
    {
        /// <summary>Тип сообщения по первому байту; пусто или неизвестный — <see cref="ProtocolException"/>.</summary>
        public static MessageType TypeOf(ReadOnlySpan<byte> src)
        {
            if (src.Length == 0)
            {
                throw new ProtocolException("Пустое сообщение.");
            }

            byte t = src[0];
            if (!IsKnown(t))
            {
                throw new ProtocolException($"Неизвестный тип сообщения {t}.");
            }

            return (MessageType)t;
        }

        /// <summary>
        /// Известный тип сообщения: 1…14 (до MeleeResult, 0-029), 16…19 (мир, 0-026) и 20…21 (правки тайлов, 0-028; последний —
        /// TileReply); 15 не занят.
        /// </summary>
        public static bool IsKnown(byte t) =>
            (t >= (byte)MessageType.Login && t <= (byte)MessageType.MeleeResult) ||
            (t >= (byte)MessageType.Chunk && t <= (byte)MessageType.TileReply);

        /// <summary>Читатель после байта типа; тип не тот — <see cref="ProtocolException"/>.</summary>
        public static BitReader Begin(ReadOnlySpan<byte> src, MessageType expected)
        {
            MessageType t = TypeOf(src);
            if (t != expected)
            {
                throw new ProtocolException($"Ждали {expected}, пришло {t}.");
            }

            var r = new BitReader(src);
            r.ReadByte();
            return r;
        }
    }
}
