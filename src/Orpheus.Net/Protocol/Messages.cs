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

        // 0-036, пауза (BRIEF п. 11b: у любого игрока есть возможность поставить паузу), канал 2. Протокол 10.

        /// <summary>Клиент → сервер, канал 2: «поставить паузу» / «продолжить».</summary>
        PauseRequest = 22,

        /// <summary>Сервер → клиент, канал 2: пауза поставлена или снята — кто, на каком такте (всем; вошедшему в паузу — после входа).</summary>
        PauseState = 23,

        // 1-001, бой (docs/02 13.5), канал 2. Протокол 11.

        /// <summary>Сервер → клиент, канал 2: сущность умерла — жертва, убийца, урон добившего удара и его тип (всем).</summary>
        Death = 24,

        /// <summary>Сервер → клиент, канал 2: сущность возродилась у центра возрождения со полным здоровьем (всем).</summary>
        Respawn = 25,

        // 1-002, сумка (docs/06 0), канал 2. Протокол 13.

        /// <summary>Сервер → клиент, канал 2: сумка персонажа — владельцу при входе и когда содержимое изменилось (1-002).</summary>
        Inventory = 26,

        // E2-1, предметы на земле и ячейки сумки (docs/06 5.3, 5.5), канал 2. Протокол 21.

        /// <summary>Сервер → клиент, канал 2: кучи на земле рядом — появились или изменили количество (E2-1).</summary>
        Piles = 27,

        /// <summary>Сервер → клиент, канал 2: куча исчезла — подобрана (кем), истекла или вышла из обзора (E2-1).</summary>
        PileGone = 28,

        /// <summary>Клиент → сервер, канал 2: заявка на операцию с ячейками сумки — переместить, разделить, выбросить (E2-1).</summary>
        BagRequest = 29,

        // E2-3, крафт (docs/06 3.5), канал 2. Протокол 22.

        /// <summary>Клиент → сервер, канал 2: заявка изготовления — «рецепт × N» или отмена позиции очереди (E2-3).</summary>
        CraftRequest = 30,

        /// <summary>Сервер → клиент, канал 2: ответ на заявку изготовления — принято или причина отказа (E2-3).</summary>
        CraftReply = 31,

        /// <summary>Сервер → клиент, канал 2: очередь изготовления персонажа — владельцу, когда изменилась (E2-3).</summary>
        CraftState = 32,

        // E2-5, объекты с действием и сундук (docs/05 1.3, 7.3), канал 2. Протокол 23.

        /// <summary>Клиент → сервер, канал 2: действие с объектом (F): открыть сундук, назначить кровать; закрыть сундук (E2-5).</summary>
        UseRequest = 33,

        /// <summary>Сервер → клиент, канал 2: ответ на действие — принято или причина (E2-5).</summary>
        UseReply = 34,

        /// <summary>Сервер → клиент, канал 2: содержимое открытого сундука или «закрыт» (E2-5).</summary>
        ChestState = 35,

        // E2-10, крафт из сундуков рядом (docs/06 3.5), канал 2. Протокол 24.

        /// <summary>Сервер → клиент, канал 2: сколько каждого предмета в сундуках рядом — владельцу, когда изменилось (E2-10).</summary>
        NearbyStock = 36,

        // E3-1, урон цифрами (docs/02 6.8), канал 2. Протокол 30.

        /// <summary>Сервер → клиент, канал 2: урон нанесён — жертва, атакующий, сколько, тип, «сильный» (игрокам рядом с жертвой, E3-1).</summary>
        Damage = 37,

        /// <summary>Снаряд (E3-2): выстрел и конец полёта — сервер → все в игре, канал 2 (<see cref="ProjectileMessage"/>). Протокол 31.</summary>
        Projectile = 38,

        /// <summary>Взмах кирки (C-028): удары по тайлам — сервер → ударивший и игроки рядом, канал 2 (<see cref="StrikeMessage"/>). Протокол 32.</summary>
        Strike = 39,
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

        /// <summary>Имя занято (0-035, docs/07 7.3): игрок с этим именем сейчас в мире, или так зовут персонажа хозяина.</summary>
        NameTaken = 6,
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
            if (reason < (byte)LoginRejectReason.VersionMismatch || reason > (byte)LoginRejectReason.NameTaken)
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
                case LoginRejectReason.NameTaken:
                    return "Имя занято: в этом мире так зовут другого героя";
                default:
                    return "Вход не удался";
            }
        }
    }

    /// <summary>
    /// Кадр ввода по сети (docs/07, 2.10): направление и сила движения (2 байта), ячейка быстрой панели (1 байт, 1-002),
    /// угол прицела (2 байта), кнопки (2 байта). Ровно <see cref="Bytes"/> = 7 байт.
    /// Тайл под курсором при молоте — потом (задача строительства).
    /// </summary>
    public struct WireFrame : IEquatable<WireFrame>
    {
        public const int Bytes = 7;

        /// <summary>Сила движения «полностью».</summary>
        public const byte FullPower = 255;

        /// <summary>Направление: 0 — стойка, 1…8 — как <see cref="InputFrame.DirectionCode"/>.</summary>
        public byte Direction;

        /// <summary>Сила движения 0…255 (стик); клавиши — 255. Пока не влияет на скорость.</summary>
        public byte Power;

        /// <summary>Ячейка быстрой панели 0…<see cref="InputFrame.MaxSlots"/>−1 — предмет в руках (1-002).</summary>
        public byte Slot;

        /// <summary>Угол прицела, u16 (65 536 шагов на круг, 0,0055°).</summary>
        public ushort Aim;

        /// <summary>Кнопки — битовая маска.</summary>
        public ushort Buttons;

        public static WireFrame From(InputFrame f) => new WireFrame
        {
            Direction = f.DirectionCode,
            Power = f.Direction.IsMoving ? FullPower : (byte)0,
            Slot = f.Slot,
            Aim = f.Angle16,
            Buttons = f.Buttons,
        };

        /// <summary>
        /// Кадр такта: направление, кнопки, угол и ячейка (код направления вне 0…8 или ячейка вне 0…7 —
        /// <see cref="ProtocolException"/>).
        /// </summary>
        public InputFrame ToInputFrame()
        {
            if (Direction > InputFrame.MaxDirectionCode)
            {
                throw new ProtocolException($"Кадр ввода: код направления {Direction}.");
            }

            if (Slot >= InputFrame.MaxSlots)
            {
                throw new ProtocolException($"Кадр ввода: ячейка {Slot}.");
            }

            return InputFrame.FromCode(Direction, Buttons, Aim, Slot);
        }

        public void Write(ref BitWriter w)
        {
            w.WriteByte(Direction);
            w.WriteByte(Power);
            w.WriteByte(Slot);
            w.WriteUInt16(Aim);
            w.WriteUInt16(Buttons);
        }

        public static WireFrame Read(ref BitReader r) => new WireFrame
        {
            Direction = r.ReadByte(),
            Power = r.ReadByte(),
            Slot = r.ReadByte(),
            Aim = r.ReadUInt16(),
            Buttons = r.ReadUInt16(),
        };

        public bool Equals(WireFrame o) => Direction == o.Direction && Power == o.Power && Slot == o.Slot && Aim == o.Aim && Buttons == o.Buttons;
        public override bool Equals(object? obj) => obj is WireFrame o && Equals(o);
        public override int GetHashCode() => (Direction << 24) ^ (Power << 16) ^ (Slot << 11) ^ (Aim << 8) ^ Buttons;
        public override string ToString() => $"frame(dir {Direction}, power {Power}, slot {Slot}, aim {Aim}, 0x{Buttons:X4})";
    }

    /// <summary>
    /// Пакет ввода, 60 Гц, канал 0 (docs/07, 2.10): номер первого кадра, 4 кадра — три прошлых и текущий
    /// (потеря пакета не теряет нажатие), время картинки, номер последнего полученного снимка, опережение L.
    /// Ровно <see cref="Bytes"/> = 42 байта (кадр — 7, 1-002).
    /// </summary>
    public struct InputMessage
    {
        /// <summary>Кадров в пакете.</summary>
        public const int FrameCount = 4;

        /// <summary>Тип 1 + первый кадр 4 + 4 × 7 + время картинки 4 + последний снимок 4 + опережение 1 = 42.</summary>
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
    /// «Удар решён» (0-029, тип 14; 1-001 — с уроном): итог угрозы по этому игроку — попал или нет; решён ли по
    /// последнему известному (ввода кадра конца замаха не дождались за W); сколько кадров сервер ждал после конца
    /// замаха и сколько кадров ввода не хватило; кадр сервера в миг решения; урон после брони (1-001: 0 — «попал»
    /// в геометрии, но неуязвимость/труп, урон не применён).
    /// </summary>
    public struct MeleeResultMessage
    {
        /// <summary>Тип 1 + атакующий 4 + s 4 + флаги 1 + ждал 1 + не хватило 1 + кадр решения 4 + урон 1 (1-001).</summary>
        public const int Bytes = 17;

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

        /// <summary>Урон после брони, применённый сервером (1-001, `02` 6.1); 0 — не применён.</summary>
        public byte Damage;

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
            w.WriteByte(Damage);
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
            m.Damage = r.ReadByte();
            r.ExpectEnd();
            return m;
        }

        public override string ToString() =>
            $"удар #{AttackerId} s {StartFrame}: {(Hit ? "попал" : "мимо")}{(ByLastKnown ? ", по последнему известному" : "")}, ждал {WaitedFrames}, не хватило {MissingFrames}, урон {Damage}";
    }

    /// <summary>
    /// «Смерть» (1-001, тип 24; `02` 13.5): сущность (#Victim) убила сущность #Attacker (−1 — манекен/неизвестный
    /// атакующий); урон после брони, добивший удар, и его тип. Рассылка — канал 2 всем в игре; дублирует событие
    /// такта, полоска здоровья приходит снимком. E3-1 (протокол 30): + тактов до возрождения (персонаж — 150 по
    /// правилу ядра; враг — 0) и флаги (бит 0 — возродится у своей кровати, иначе у Очага) — для экрана смерти.
    /// </summary>
    public struct DeathMessage
    {
        /// <summary>Тип 1 + жертва 4 + убийца 4 + урон 1 + тип урона 1 + тактов до возрождения 2 + флаги 1.</summary>
        public const int Bytes = 14;

        /// <summary>Флаг: возродится у своей кровати (E3-1).</summary>
        public const byte FlagAtBed = 1;

        public int Victim;
        public int Attacker;
        public byte Damage;
        public byte DamageType;

        /// <summary>Тактов от смерти до возрождения (E3-1): 0 — не возрождается (враг).</summary>
        public ushort RespawnTicks;

        /// <summary>Флаги (E3-1): <see cref="FlagAtBed"/>.</summary>
        public byte Flags;

        public bool AtBed => (Flags & FlagAtBed) != 0;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Death);
            w.WriteUInt32((uint)Victim);
            w.WriteUInt32((uint)Attacker);
            w.WriteByte(Damage);
            w.WriteByte(DamageType);
            w.WriteUInt16(RespawnTicks);
            w.WriteByte(Flags);
            return w.BytesWritten;
        }

        public static DeathMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Death);
            var m = new DeathMessage
            {
                Victim = (int)r.ReadUInt32(),
                Attacker = (int)r.ReadUInt32(),
                Damage = r.ReadByte(),
                DamageType = r.ReadByte(),
                RespawnTicks = r.ReadUInt16(),
                Flags = r.ReadByte(),
            };
            if ((m.Flags & ~FlagAtBed) != 0)
            {
                throw new ProtocolException($"Смерть: неизвестные флаги 0x{m.Flags:X2}.");
            }

            r.ExpectEnd();
            return m;
        }

        public override string ToString() =>
            $"смерть #{Victim} (убийца #{Attacker}, урон {Damage}, тип {DamageType}, возрождение через {RespawnTicks} т{(AtBed ? ", у кровати" : "")})";
    }

    /// <summary>
    /// «Урон» (E3-1, тип 37; `02` 6.8): сущность #Attacker нанесла сущности #Victim урон после брони (событие такта
    /// <c>SimEventKind.Damage</c> или решение угрозы сервером). Для цифр урона над головой и полоски здоровья врага — из
    /// событий сервера, не из предсказания. Рассылка — канал 2 игрокам, чей персонаж в радиусе обзора от жертвы, и самим
    /// жертве и атакующему.
    /// </summary>
    public struct DamageMessage
    {
        /// <summary>Тип 1 + жертва 4 + атакующий 4 + урон 2 + тип урона 1 + флаги 1.</summary>
        public const int Bytes = 13;

        /// <summary>Флаг: сильный удар (добивающий серии меча, <c>Simulation.DamageTagStrong</c>) — цифра крупнее.</summary>
        public const byte FlagStrong = 1;

        public int Victim;
        public int Attacker;
        public ushort Amount;
        public byte DamageType;
        public byte Flags;

        public bool Strong => (Flags & FlagStrong) != 0;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Damage);
            w.WriteUInt32((uint)Victim);
            w.WriteUInt32((uint)Attacker);
            w.WriteUInt16(Amount);
            w.WriteByte(DamageType);
            w.WriteByte(Flags);
            return w.BytesWritten;
        }

        public static DamageMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Damage);
            var m = new DamageMessage
            {
                Victim = (int)r.ReadUInt32(),
                Attacker = (int)r.ReadUInt32(),
                Amount = r.ReadUInt16(),
                DamageType = r.ReadByte(),
                Flags = r.ReadByte(),
            };
            if (m.Amount == 0)
            {
                throw new ProtocolException("Урон: 0 не шлётся.");
            }

            if ((m.Flags & ~FlagStrong) != 0)
            {
                throw new ProtocolException($"Урон: неизвестные флаги 0x{m.Flags:X2}.");
            }

            r.ExpectEnd();
            return m;
        }

        public override string ToString() => $"урон #{Attacker}→#{Victim} {Amount} (тип {DamageType}{(Strong ? ", сильный" : "")})";
    }

    /// <summary>
    /// «Возрождение» (1-001, тип 25; `02` 13.5): сущность #Victim возродилась у центра возрождения со полным
    /// здоровьем. Рассылка — канал 2 всем в игре.
    /// </summary>
    public struct RespawnMessage
    {
        /// <summary>Тип 1 + жертва 4.</summary>
        public const int Bytes = 5;

        public int Victim;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Respawn);
            w.WriteUInt32((uint)Victim);
            return w.BytesWritten;
        }

        public static RespawnMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Respawn);
            var m = new RespawnMessage { Victim = (int)r.ReadUInt32() };
            r.ExpectEnd();
            return m;
        }

        public override string ToString() => $"возрождение #{Victim}";
    }

    /// <summary>
    /// «Сумка» (1-002, тип 26; E2-1 — ячейками; docs/06 5.5): сумка персонажа — сервер шлёт <b>владельцу</b> при входе
    /// и когда содержимое изменилось (канал 2, надёжный). Предмет в руках для чужих сущностей —
    /// 8 бит в снимке (<see cref="SnapshotEntity.HeldItem"/>), целиком сумка чужому игроку не нужна.
    /// Поля: тип 1 + сущность u32 + версия u16 + выбранная ячейка 1 + 40 ячеек × (предмет 1 + количество u16).
    /// </summary>
    public struct InventoryMessage
    {
        /// <summary>Тип 1 + сущность 4 + версия 2 + payload 121.</summary>
        public const int Bytes = 1 + 4 + 2 + Inventory.PayloadBytes;

        public int EntityId;

        /// <summary>Версия сумки на сервере — растёт при каждой отправке этому игроку (порядок хранит канал 2).</summary>
        public ushort Version;

        public Inventory Bag;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Inventory);
            w.WriteUInt32((uint)EntityId);
            w.WriteUInt16(Version);
            w.WriteByte(Bag.Selected);
            for (int i = 0; i < Inventory.Cells; i++)
            {
                w.WriteByte(Bag.ItemAt(i));
                w.WriteUInt16((ushort)Bag.CountIn(i));
            }

            return w.BytesWritten;
        }

        public static InventoryMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Inventory);
            var m = new InventoryMessage { EntityId = (int)r.ReadUInt32(), Version = r.ReadUInt16() };
            byte selected = r.ReadByte();
            if (selected >= Inventory.Slots)
            {
                throw new ProtocolException($"Сумка: выбранная ячейка {selected} >= {Inventory.Slots}.");
            }

            var bag = new Inventory();
            for (int i = 0; i < Inventory.Cells; i++)
            {
                byte item = r.ReadByte();
                int count = r.ReadUInt16();
                if ((item == Items.None) != (count == 0) || count > Items.MaxStackOf(item))
                {
                    throw new ProtocolException($"Сумка: ячейка {i} — предмет {item} × {count}.");
                }

                bag.SetCell(i, item, count);
            }

            bag.Select(selected);
            r.ExpectEnd();
            m.Bag = bag;
            return m;
        }

        public override string ToString() => $"сумка #{EntityId} в{Version} {Bag}";
    }

    /// <summary>Куча на земле, как её видит клиент (E2-1): номер, предмет, количество, позиция в unit.</summary>
    public struct NetPile
    {
        /// <summary>Номер 4 + предмет 1 + количество 2 + позиция 4 + 4.</summary>
        public const int Bytes = 15;

        public int Id;
        public byte Item;
        public ushort Count;
        public WorldPos Position;

        public override readonly string ToString() => $"куча #{Id} предмет {Item} × {Count} {Position}";
    }

    /// <summary>
    /// «Кучи» (E2-1, тип 27, канал 2): кучи на земле в обзоре игрока — новые и те, у которых изменилось количество.
    /// Поля: тип 1 + число 1 + на кучу — номер u32, предмет 1, количество u16 (≥ 1), позиция x, y u32 (unit).
    /// </summary>
    public struct PilesMessage
    {
        /// <summary>Куч в одном сообщении.</summary>
        public const int MaxPiles = 48;

        /// <summary>Наибольший размер: тип 1 + число 1 + 48 куч = 722 байта (влезает в один пакет).</summary>
        public const int MaxBytes = 2 + MaxPiles * NetPile.Bytes;

        public static int Write(Span<byte> dest, ReadOnlySpan<NetPile> piles)
        {
            if (piles.Length == 0 || piles.Length > MaxPiles)
            {
                throw new ArgumentOutOfRangeException(nameof(piles), piles.Length, $"Куч в сообщении — 1…{MaxPiles}.");
            }

            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Piles);
            w.WriteByte((byte)piles.Length);
            for (int i = 0; i < piles.Length; i++)
            {
                w.WriteUInt32((uint)piles[i].Id);
                w.WriteByte(piles[i].Item);
                w.WriteUInt16(piles[i].Count);
                w.WriteUInt32((uint)piles[i].Position.X);
                w.WriteUInt32((uint)piles[i].Position.Y);
            }

            return w.BytesWritten;
        }

        /// <summary>Прочитать в <paramref name="dest"/> (≥ <see cref="MaxPiles"/>); возвращает число куч.</summary>
        public static int Read(ReadOnlySpan<byte> src, Span<NetPile> dest)
        {
            var r = Codec.Begin(src, MessageType.Piles);
            int n = r.ReadByte();
            if (n == 0 || n > MaxPiles || n > dest.Length)
            {
                throw new ProtocolException($"Кучи: число {n} вне 1…{MaxPiles}.");
            }

            for (int i = 0; i < n; i++)
            {
                var p = new NetPile { Id = (int)r.ReadUInt32(), Item = r.ReadByte(), Count = r.ReadUInt16() };
                int x = (int)r.ReadUInt32();
                int y = (int)r.ReadUInt32();
                if (p.Id <= 0 || !Items.Exists(p.Item) || p.Count == 0)
                {
                    throw new ProtocolException($"Кучи: № {i} — номер {p.Id}, предмет {p.Item} × {p.Count}.");
                }

                p.Position = new WorldPos(x, y);
                dest[i] = p;
            }

            r.ExpectEnd();
            return n;
        }
    }

    /// <summary>Почему куча исчезла у клиента (E2-1).</summary>
    public enum PileGoneReason : byte
    {
        /// <summary>Подобрана целиком: <see cref="PileGoneMessage.Taker"/> — кто.</summary>
        Picked = 0,

        /// <summary>Истекло время жизни.</summary>
        Expired = 1,

        /// <summary>Вышла из обзора игрока (куча лежит, клиент её забывает).</summary>
        OutOfView = 2,
    }

    /// <summary>
    /// «Кучи нет» (E2-1, тип 28, канал 2): куча исчезла. Поля: тип 1 + номер u32 + причина 1 + кто поднял u32 (0 — никто).
    /// </summary>
    public struct PileGoneMessage
    {
        public const int Bytes = 1 + 4 + 1 + 4;

        public int Id;
        public PileGoneReason Reason;

        /// <summary>Сущность, поднявшая кучу (для «прыжка» к игроку); 0 — не подбор.</summary>
        public int Taker;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.PileGone);
            w.WriteUInt32((uint)Id);
            w.WriteByte((byte)Reason);
            w.WriteUInt32((uint)Taker);
            return w.BytesWritten;
        }

        public static PileGoneMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.PileGone);
            var m = new PileGoneMessage { Id = (int)r.ReadUInt32() };
            byte reason = r.ReadByte();
            if (reason > (byte)PileGoneReason.OutOfView)
            {
                throw new ProtocolException($"Кучи нет: причина {reason}.");
            }

            m.Reason = (PileGoneReason)reason;
            m.Taker = (int)r.ReadUInt32();
            r.ExpectEnd();
            return m;
        }

        public override string ToString() => $"кучи #{Id} нет: {Reason}, поднял #{Taker}";
    }

    /// <summary>
    /// «Заявка на сумку» (E2-1, тип 29, канал 2; docs/06 5.5 «Перемещение предмета — запрос клиента»): операция,
    /// ячейка-источник, ячейка-цель, количество. Поля: тип 1 + операция 1 + откуда 1 + куда 1 + количество u16.
    /// Ответа нет: исполненная заявка меняет сумку, и сервер шлёт её целиком (тип 26).
    /// </summary>
    public struct BagRequestMessage
    {
        public const int Bytes = 1 + 1 + 1 + 1 + 2;

        public BagOp Op;
        public byte From;
        public byte To;

        /// <summary>Сколько (деление, выброс); у выброса 0 — вся стопка.</summary>
        public ushort Count;

        public int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.BagRequest);
            w.WriteByte((byte)Op);
            w.WriteByte(From);
            w.WriteByte(To);
            w.WriteUInt16(Count);
            return w.BytesWritten;
        }

        /// <summary>Ячеек в заявке: сумка (0…39) и открытый сундук (40…69; E2-5).</summary>
        public const int MaxCell = Inventory.Cells + Simulation.ChestCells;

        public static BagRequestMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.BagRequest);
            var m = new BagRequestMessage { Op = (BagOp)r.ReadByte(), From = r.ReadByte(), To = r.ReadByte(), Count = r.ReadUInt16() };
            r.ExpectEnd();
            // E2-5: ячейки от Inventory.Cells — ячейки открытого сундука (их 30); какой сундук — помнит сервер.
            if (m.Op < BagOp.Move || m.Op > BagOp.Drop || m.From >= MaxCell || m.To >= MaxCell)
            {
                throw new ProtocolException($"Заявка на сумку: операция {(byte)m.Op}, ячейки {m.From} → {m.To}.");
            }

            return m;
        }

        public override string ToString() => $"сумка: {Op} {From} → {To} × {Count}";
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
        /// Известный тип сообщения: 1…14 (до MeleeResult, 0-029), 16…19 (мир, 0-026), 20…21 (правки тайлов, 0-028),
        /// 22…23 (пауза, 0-036), 24…25 (смерть/возрождение, 1-001), 26 (сумка, 1-002) и 27…29 (кучи и заявка на сумку, E2-1;
        /// затем 30…32 — крафт, E2-3; 33…35 — действие с объектом и сундук, E2-5; 36 — сундуки рядом, E2-10; 37 — урон, E3-1; 38 — снаряд, E3-2; 39 — взмах кирки, C-028; последний — Strike); 15 не занят.
        /// </summary>
        public static bool IsKnown(byte t) =>
            (t >= (byte)MessageType.Login && t <= (byte)MessageType.MeleeResult) ||
            (t >= (byte)MessageType.Chunk && t <= (byte)MessageType.Strike);

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
