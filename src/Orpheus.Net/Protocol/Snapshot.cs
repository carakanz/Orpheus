using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Protocol
{
    /// <summary>Сущность в снимке: номер, вид, позиция (квантована до 1/32 тайла), направление движения.</summary>
    public struct SnapshotEntity
    {
        public int Id;
        public byte Kind;

        /// <summary>Позиция после квантования: центр клетки 1/32 тайла (в 1/256 тайла).</summary>
        public WorldPos Position;

        /// <summary>Код направления ввода 0…8 (0 — стоит); для интерполяции и анимации.</summary>
        public byte Direction;

        /// <summary>Здоровье в процентах максимума, 0…100 (1-001): 100 — целое, 0 — труп; для полоски здоровья.</summary>
        public byte HealthPct;

        /// <summary>Предмет в руках (id <see cref="Items"/>; 1-002): 0 — пусто; для картинки в руках.</summary>
        public byte HeldItem;

        public override string ToString() => $"#{Id} k{Kind} {Position} d{Direction} hp{HealthPct} h{HeldItem}";
    }

    /// <summary>
    /// Подтверждённое состояние своего персонажа (docs/07, 2.6 п. 2; 0-019): позиция без кванта (1/256 тайла) и остаток
    /// скорости <c>Carry</c> — ровно то, с чего сервер шагнёт следующий кадр. Клиент при правке берёт его целиком: без
    /// кванта 1/32 правка не тянет за собой цепочку следующих. Идёт в каждом снимке и каждой дельте, сразу за заголовком.
    /// </summary>
    public struct SelfState : IEquatable<SelfState>
    {
        /// <summary>Наибольший остаток скорости по модулю (десятые unit: <c>carry %= 10</c>).</summary>
        public const int MaxCarry = 9;

        /// <summary>Есть ли состояние (в снимках тестов и инструментов его может не быть).</summary>
        public bool Present;

        /// <summary>Позиция, 1/256 тайла, без кванта.</summary>
        public WorldPos Position;

        /// <summary>Остаток скорости, десятые unit (−9…9 на ось).</summary>
        public Int2 Carry;

        /// <summary>
        /// Уворот (0-029, протокол 6): кадров от начала (8 бит), рывка осталось (4), откат (5), направление (4) — 21 бит.
        /// Клиент при правке берёт его вместо предсказанного: сервер мог шагнуть кадры сам или перескочить их.
        /// </summary>
        public Dodge Dodge;

        /// <summary>Бит на поля уворота.</summary>
        public const int DodgeElapsedBits = 8, DodgeDashBits = 4, DodgeCooldownBits = 5, DodgeDirectionBits = 4;

        /// <summary>Бит уворота всего: 21.</summary>
        public const int DodgeBits = DodgeElapsedBits + DodgeDashBits + DodgeCooldownBits + DodgeDirectionBits;

        /// <summary>
        /// Бой своего персонажа (1-001): здоровье (8 бит, 0…255 — HP целое, максимум известен из правил:
        /// уровень 0 — 100), масса 1…4 (2 бита — скорость отбрасывания <see cref="Mover.KnockSpeedFor"/>),
        /// отбрасывание: кадров осталось 0…9 (4 бита) и направление 1…8 (3 бита, храним код − 1; 0 — нет).
        /// Клиент при правке берёт их целиком, как уворот: нокбэк задаёт сервер, предсказание его шагает.
        /// </summary>
        public byte Health;

        /// <summary>Масса (1…4) — по ней скорость отбрасывания.</summary>
        public byte Mass;

        /// <summary>Кадров отбрасывания осталось (0…9, <see cref="Mover.KnockFrames"/>).</summary>
        public byte KnockLeft;

        /// <summary>Направление отбрасывания 0…7 (код <see cref="Input8.DirectionIndex"/>; активно только при <see cref="KnockLeft"/> &gt; 0).</summary>
        public byte KnockDir;

        /// <summary>Бит на поля боя.</summary>
        public const int HealthBits = 8, MassBits = 2, KnockLeftBits = 4, KnockDirBits = 3;

        /// <summary>Бит боя всего: 17.</summary>
        public const int CombatBits = HealthBits + MassBits + KnockLeftBits + KnockDirBits;

        /// <summary>
        /// Отчёт сервера получателю (0-033, `07` 2.7): «запас прихода кадров» — 1-й процентиль за 2 с при его нынешнем
        /// опережении, кадров (−64…63; ≥ 0 — кадры приходят к сроку решения). Не состояние персонажа: в сравнение
        /// (<see cref="Equals(SelfState)"/>) не входит, пишется и без своего состояния.
        /// </summary>
        public sbyte LeadMargin;

        /// <summary>Отчёт сервера: задержка показа D_p получателя, кадров (0…30; 2.7 «Значок высокая задержка»). Вне сравнения.</summary>
        public byte DelayFrames;

        /// <summary>Бит отчёта: запас 7 (со сдвигом 64) + D_p 5.</summary>
        public const int MarginBits = 7, DelayBits = 5, ReportBits = MarginBits + DelayBits;

        /// <summary>Сдвиг запаса в битах.</summary>
        public const int MarginBias = 64;

        public SelfState(WorldPos position, Int2 carry)
            : this(position, carry, default)
        {
        }

        public SelfState(WorldPos position, Int2 carry, Dodge dodge)
        {
            Present = true;
            Position = position;
            Carry = carry;
            Dodge = dodge;
            Health = 100; // 1-001: персонаж цел, масса 1 — сервер заполняет настоящими значениями
            Mass = 1;
            KnockLeft = 0;
            KnockDir = 0;
            LeadMargin = 0;
            DelayFrames = 0;
        }

        /// <summary>Уворот одинаков во всех полях.</summary>
        public static bool SameDodge(in Dodge a, in Dodge b) =>
            a.Elapsed == b.Elapsed && a.DashLeft == b.DashLeft && a.Cooldown == b.Cooldown && a.Direction == b.Direction;

        public bool Equals(SelfState o) => Present == o.Present && Position == o.Position && Carry == o.Carry && SameDodge(Dodge, o.Dodge) &&
            Health == o.Health && Mass == o.Mass && KnockLeft == o.KnockLeft && KnockDir == o.KnockDir;
        public override bool Equals(object? obj) => obj is SelfState o && Equals(o);
        public override int GetHashCode() => Present ? Position.X ^ (Position.Y << 7) ^ (Carry.X << 3) ^ Carry.Y ^ (Dodge.Elapsed << 11) ^ (Health << 17) ^ (KnockLeft << 21) : 0;
        public override string ToString() => Present ? $"своё {Position} c{Carry} {Dodge} hp{Health} m{Mass} knock{KnockLeft}:{KnockDir}" : "своё —";

        internal void Write(ref BitWriter w, in SnapshotLayout layout)
        {
            w.WriteBits(Present ? 1u : 0u, 1);
            SelfState s = Present ? this : default;
            w.WriteBits((uint)s.Position.X, layout.SelfBitsX);
            w.WriteBits((uint)s.Position.Y, layout.SelfBitsY);
            w.WriteBits((uint)(s.Carry.X + SnapshotLayout.CarryBias), SnapshotLayout.CarryBits);
            w.WriteBits((uint)(s.Carry.Y + SnapshotLayout.CarryBias), SnapshotLayout.CarryBits);
            w.WriteBits(s.Dodge.Elapsed, DodgeElapsedBits);
            w.WriteBits(s.Dodge.DashLeft, DodgeDashBits);
            w.WriteBits(s.Dodge.Cooldown, DodgeCooldownBits);
            w.WriteBits(s.Dodge.Direction, DodgeDirectionBits);
            w.WriteBits(s.Health, HealthBits);
            w.WriteBits((uint)Math.Max(1, Math.Min(4, (int)s.Mass)) - 1, MassBits);
            w.WriteBits(s.KnockLeft, KnockLeftBits);
            w.WriteBits((uint)(s.KnockLeft == 0 ? 0 : Math.Min(8, (int)s.KnockDir) - 1), KnockDirBits);
            w.WriteBits((uint)(Math.Max(-MarginBias, Math.Min(MarginBias - 1, (int)LeadMargin)) + MarginBias), MarginBits);
            w.WriteBits(Math.Min(DelayFrames, (byte)ThreatTiming.MaxDelayFrames), DelayBits);
        }

        internal static SelfState Read(ref BitReader r, in SnapshotLayout layout)
        {
            bool present = r.ReadBits(1) != 0;
            int x = (int)r.ReadBits(layout.SelfBitsX);
            int y = (int)r.ReadBits(layout.SelfBitsY);
            int cx = (int)r.ReadBits(SnapshotLayout.CarryBits) - SnapshotLayout.CarryBias;
            int cy = (int)r.ReadBits(SnapshotLayout.CarryBits) - SnapshotLayout.CarryBias;
            var dodge = new Dodge
            {
                Elapsed = (byte)r.ReadBits(DodgeElapsedBits),
                DashLeft = (byte)r.ReadBits(DodgeDashBits),
                Cooldown = (byte)r.ReadBits(DodgeCooldownBits),
                Direction = (byte)r.ReadBits(DodgeDirectionBits),
            };
            byte health = (byte)r.ReadBits(HealthBits);
            byte mass = (byte)(r.ReadBits(MassBits) + 1);
            byte knockLeft = (byte)r.ReadBits(KnockLeftBits);
            byte knockDir = (byte)r.ReadBits(KnockDirBits);
            sbyte margin = (sbyte)((int)r.ReadBits(MarginBits) - MarginBias);
            byte delay = (byte)r.ReadBits(DelayBits);
            if (delay > ThreatTiming.MaxDelayFrames)
            {
                throw new ProtocolException($"Снимок: задержка показа {delay} > {ThreatTiming.MaxDelayFrames} кадров.");
            }
            if (dodge.DashLeft > Dodge.DashFrames || dodge.Cooldown > Dodge.CooldownFrames || dodge.Direction > 8)
            {
                throw new ProtocolException($"Снимок: уворот вне пределов ({dodge.DashLeft}, {dodge.Cooldown}, {dodge.Direction}).");
            }
            if (knockLeft > Mover.KnockFrames || knockDir > 7)
            {
                throw new ProtocolException($"Снимок: отбрасывание вне пределов ({knockLeft}, {knockDir}).");
            }

            if (x >= layout.Shape.WidthUnits || y >= layout.Shape.HeightUnits || Math.Abs(cx) > MaxCarry || Math.Abs(cy) > MaxCarry)
            {
                throw new ProtocolException($"Снимок: своё состояние ({x}, {y}) c({cx}, {cy}) вне карты или остаток > {MaxCarry}.");
            }

            var combat = new SelfState
            {
                LeadMargin = margin,
                DelayFrames = delay,
                Health = health,
                Mass = mass,
                KnockLeft = knockLeft,
                KnockDir = (byte)(knockLeft == 0 ? 0 : knockDir + 1),
            };
            if (!present)
            {
                if (x != 0 || y != 0 || cx != 0 || cy != 0 || !SameDodge(dodge, default) ||
                    health != 0 || knockLeft != 0 || knockDir != 0)
                {
                    throw new ProtocolException("Снимок: своего состояния нет, а поля не пустые.");
                }

                return combat;
            }

            var s = new SelfState(new WorldPos(x, y), new Int2(cx, cy), dodge)
            {
                LeadMargin = margin,
                DelayFrames = delay,
                Health = health,
                Mass = mass,
                KnockLeft = knockLeft,
                KnockDir = (byte)(knockLeft == 0 ? 0 : knockDir + 1),
            };
            return s;
        }
    }

    /// <summary>
    /// Разметка снимка для формы карты (docs/07, 4.3): позиция — 1/32 тайла (младшие 3 бита из 8 отброшены),
    /// бит на ось — сколько нужно для ширины карты: 1024² — 15, 4096² — 17. Обе стороны знают форму из входа.
    /// </summary>
    public readonly struct SnapshotLayout
    {
        /// <summary>Отброшено бит позиции: 1/256 → 1/32 тайла.</summary>
        public const int QuantShift = 3;

        /// <summary>Бит на номер, вид, направление.</summary>
        public const int IdBits = 32;
        public const int KindBits = 8;
        public const int DirectionBits = 4;

        /// <summary>Бит на долю здоровья сущности (1-001): проценты максимума 0…100.</summary>
        public const int HealthPctBits = 7;

        /// <summary>Бит на предмет в руках сущности (1-002): id предмета (0…<see cref="Items.Count"/>−1).</summary>
        public const int HeldBits = 8;

        public readonly MapShape Shape;
        public readonly int BitsX;
        public readonly int BitsY;

        /// <summary>Бит на ось позиции своего состояния (<see cref="SelfState"/>) — без кванта: 1024² — 18, 4096² — 20.</summary>
        public readonly int SelfBitsX;

        /// <inheritdoc cref="SelfBitsX"/>
        public readonly int SelfBitsY;

        /// <summary>Бит на ось остатка скорости своего состояния: −9…9 со сдвигом <see cref="CarryBias"/> → 0…18.</summary>
        public const int CarryBits = 5;

        /// <summary>Сдвиг остатка скорости в поле (<see cref="CarryBits"/>).</summary>
        public const int CarryBias = 16;

        public SnapshotLayout(MapShape shape)
        {
            Shape = shape;
            BitsX = BitLength(shape.WidthUnits - 1) - QuantShift;
            BitsY = BitLength(shape.HeightUnits - 1) - QuantShift;
            SelfBitsX = BitsX + QuantShift;
            SelfBitsY = BitsY + QuantShift;
        }

        /// <summary>Бит на сущность.</summary>
        public int EntityBits => IdBits + KindBits + BitsX + BitsY + DirectionBits + HealthPctBits + HeldBits;

        /// <summary>
        /// Бит своего состояния (<see cref="SelfState"/>, 0-019): признак 1 + позиция без кванта + остаток скорости 2 × 5
        /// + уворот 21 (0-029, протокол 6) + бой 17 (1-001, протокол 11). 1024² — 85 бит (≈ 11 байт), 4096² — 89 бит.
        /// Есть в каждом снимке и каждой дельте.
        /// </summary>
        public int SelfBits => 1 + SelfBitsX + SelfBitsY + 2 * CarryBits + SelfState.DodgeBits + SelfState.CombatBits + SelfState.ReportBits;

        /// <summary>Сколько сущностей влезает в бюджет <see cref="NetSettings.SnapshotBudget"/>.</summary>
        public int MaxEntitiesInBudget => Math.Min(Snapshot.MaxEntities, ((NetSettings.SnapshotBudget - Snapshot.HeaderBytes) * 8 - SelfBits) / EntityBits);

        /// <summary>Байт снимка с <paramref name="count"/> сущностями.</summary>
        public int SizeFor(int count) => Snapshot.HeaderBytes + (SelfBits + count * EntityBits + 7) / 8;

        /// <summary>Позиция → клетка 1/32 → обратно (то, что увидит клиент).</summary>
        public static WorldPos Quantize(WorldPos p) =>
            new WorldPos(((p.X >> QuantShift) << QuantShift) | (1 << (QuantShift - 1)), ((p.Y >> QuantShift) << QuantShift) | (1 << (QuantShift - 1)));

        private static int BitLength(int v)
        {
            int n = 0;
            while (v > 0)
            {
                n++;
                v >>= 1;
            }

            return Math.Max(n, QuantShift + 1);
        }
    }

    /// <summary>
    /// Полный снимок (docs/07, 4.3; дельты — <see cref="SnapshotDeltaMessage"/>, 0-014): такт сервера, последний применённый кадр ввода
    /// этого клиента, сущности рядом. Своя сущность — первой. Объект переиспользуется: чтение не выделяет память.
    /// <para>Схема: тип 1 байт, такт u32, подтверждённый кадр u32, число сущностей u8; своё состояние
    /// (<see cref="SelfState"/>, <see cref="SnapshotLayout.SelfBits"/>); затем сущности по битам (<see cref="SnapshotLayout"/>).</para>
    /// </summary>
    public sealed class Snapshot
    {
        /// <summary>Заголовок: тип 1 + такт 4 + кадр 4 + число 1 + эпоха часов 1 (0-025).</summary>
        public const int HeaderBytes = 11;

        /// <summary>Наибольшее число сущностей в снимке (поле — u8).</summary>
        public const int MaxEntities = 255;

        private const int CountOffset = 9;

        /// <summary>Смещение байта эпохи часов.</summary>
        public const int EpochOffset = 10;

        public uint Tick;
        public uint AckFrame;
        public int Count;

        /// <summary>
        /// Эпоха часов сервера (2.4 «часы после фриза» в заголовке снимка; 0-025): +1 при каждой рассылке «часы: кадр N»
        /// (сервер отстал больше чем на 3 такта и не догоняет). Клиент, увидев новую эпоху, переставляет оценку кадра сразу —
        /// даже если сообщение «часы» по каналу 2 ещё в пути.
        /// </summary>
        public byte ClockEpoch;

        /// <summary>Подтверждённое состояние своего персонажа после кадра <see cref="AckFrame"/> (0-019).</summary>
        public SelfState Self;

        /// <summary>true — объект получен склейкой дельты (0-014), не из полного кадра.</summary>
        public bool IsDelta;

        public readonly SnapshotEntity[] Entities = new SnapshotEntity[MaxEntities];

        /// <summary>Сущность по номеру; нет — false.</summary>
        public bool TryFind(int id, out SnapshotEntity e)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Entities[i].Id == id)
                {
                    e = Entities[i];
                    return true;
                }
            }

            e = default;
            return false;
        }

        /// <summary>Скопировать в <paramref name="into"/>.</summary>
        public void CopyTo(Snapshot into)
        {
            into.Tick = Tick;
            into.AckFrame = AckFrame;
            into.Count = Count;
            into.Self = Self;
            into.IsDelta = IsDelta;
            into.ClockEpoch = ClockEpoch;
            Array.Copy(Entities, into.Entities, Count);
        }

        /// <summary>Записать снимок из этого объекта (все <see cref="Count"/> сущностей; бюджет не проверяется).</summary>
        public int Write(Span<byte> dest, in SnapshotLayout layout)
        {
            var w = new SnapshotWriter(dest, layout, Tick, AckFrame, Self, dest.Length);
            w.SetClockEpoch(ClockEpoch);
            for (int i = 0; i < Count; i++)
            {
                if (!w.TryAdd(Entities[i].Id, Entities[i].Kind, Entities[i].Position, Entities[i].Direction,
                             Entities[i].HealthPct, Entities[i].HeldItem))
                {
                    throw new ProtocolException($"Снимок: {Count} сущностей не влезли в {dest.Length} байт.");
                }
            }

            return w.Finish();
        }

        /// <summary>Прочитать снимок в этот объект.</summary>
        public void Read(ReadOnlySpan<byte> src, in SnapshotLayout layout)
        {
            BitReader r = Codec.Begin(src, MessageType.Snapshot);
            uint tick = r.ReadUInt32();
            uint ack = r.ReadUInt32();
            int count = r.ReadByte();
            byte epoch = r.ReadByte();
            SelfState self = SelfState.Read(ref r, layout);
            for (int i = 0; i < count; i++)
            {
                ref SnapshotEntity e = ref Entities[i];
                e.Id = (int)r.ReadBits(SnapshotLayout.IdBits);
                e.Kind = (byte)r.ReadBits(SnapshotLayout.KindBits);
                int qx = (int)r.ReadBits(layout.BitsX);
                int qy = (int)r.ReadBits(layout.BitsY);
                e.Position = new WorldPos(
                    (qx << SnapshotLayout.QuantShift) | (1 << (SnapshotLayout.QuantShift - 1)),
                    (qy << SnapshotLayout.QuantShift) | (1 << (SnapshotLayout.QuantShift - 1)));
                e.Direction = (byte)r.ReadBits(SnapshotLayout.DirectionBits);
                e.HealthPct = (byte)r.ReadBits(SnapshotLayout.HealthPctBits);
                e.HeldItem = (byte)r.ReadBits(SnapshotLayout.HeldBits);
                if (e.HealthPct > 100)
                {
                    throw new ProtocolException($"Снимок: доля здоровья {e.HealthPct} % > 100.");
                }
                if (e.Direction > InputFrame.MaxDirectionCode || e.Position.X >= layout.Shape.WidthUnits || e.Position.Y >= layout.Shape.HeightUnits)
                {
                    throw new ProtocolException($"Снимок: сущность {e} вне карты или с кодом направления > 8.");
                }
            }

            r.ExpectEnd();
            Tick = tick;
            AckFrame = ack;
            Count = count;
            Self = self;
            IsDelta = false;
            ClockEpoch = epoch;
        }

        /// <summary>Такт из заголовка, не читая всего снимка (для отбрасывания старых); годится и для дельты (0-014).</summary>
        public static uint PeekTick(ReadOnlySpan<byte> src)
        {
            MessageType t = Codec.TypeOf(src);
            BitReader r = Codec.Begin(src, t == MessageType.SnapshotDelta ? MessageType.SnapshotDelta : MessageType.Snapshot);
            return r.ReadUInt32();
        }

        internal static void PatchCount(Span<byte> dest, int count) => dest[CountOffset] = (byte)count;
    }

    /// <summary>
    /// Запись снимка по одной сущности без выделений: <see cref="TryAdd"/> отказывает, когда следующая сущность
    /// не влезает в лимит байт (бюджет 1200) или в 255 — сервер пишет ошибку в лог (4.3) и шлёт то, что влезло.
    /// </summary>
    public ref struct SnapshotWriter
    {
        private readonly Span<byte> _dest;
        private readonly SnapshotLayout _layout;
        private readonly int _limitBits;
        private BitWriter _w;
        private int _count;

        public SnapshotWriter(Span<byte> dest, in SnapshotLayout layout, uint tick, uint ackFrame, int limitBytes = NetSettings.SnapshotBudget)
            : this(dest, layout, tick, ackFrame, default, limitBytes)
        {
        }

        /// <param name="self">Своё подтверждённое состояние (0-019); без него — признак «нет» и нули.</param>
        public SnapshotWriter(Span<byte> dest, in SnapshotLayout layout, uint tick, uint ackFrame, in SelfState self, int limitBytes = NetSettings.SnapshotBudget)
        {
            _dest = dest;
            _layout = layout;
            _limitBits = Math.Min(limitBytes, dest.Length) * 8;
            _w = new BitWriter(dest);
            _count = 0;
            _w.WriteByte((byte)MessageType.Snapshot);
            _w.WriteUInt32(tick);
            _w.WriteUInt32(ackFrame);
            _w.WriteByte(0); // число сущностей — в Finish
            _w.WriteByte(0); // эпоха часов — SetClockEpoch (0-025)
            self.Write(ref _w, layout);
        }

        /// <summary>«Часы после фриза» в заголовке (2.4; 0-025): эпоха часов сервера — растёт при каждой рассылке «часы: кадр N».</summary>
        public void SetClockEpoch(byte epoch) => _dest[Snapshot.EpochOffset] = epoch;

        /// <summary>Сущностей записано.</summary>
        public int Count => _count;

        /// <summary>Добавить сущность; не влезает — false (ничего не записано).</summary>
        public bool TryAdd(int id, byte kind, WorldPos position, byte direction, byte healthPct = 100, byte heldItem = 0)
        {
            if (_count == Snapshot.MaxEntities || _w.BitPosition + _layout.EntityBits > _limitBits)
            {
                return false;
            }

            _w.WriteBits((uint)id, SnapshotLayout.IdBits);
            _w.WriteBits(kind, SnapshotLayout.KindBits);
            _w.WriteBits((uint)(position.X >> SnapshotLayout.QuantShift), _layout.BitsX);
            _w.WriteBits((uint)(position.Y >> SnapshotLayout.QuantShift), _layout.BitsY);
            _w.WriteBits(direction, SnapshotLayout.DirectionBits);
            _w.WriteBits(healthPct, SnapshotLayout.HealthPctBits);
            _w.WriteBits(heldItem, SnapshotLayout.HeldBits);
            _count++;
            return true;
        }

        /// <summary>Закрыть снимок: записать число сущностей; вернуть длину в байтах.</summary>
        public int Finish()
        {
            Snapshot.PatchCount(_dest, _count);
            return _w.BytesWritten;
        }
    }

    /// <summary>Поля изменившейся сущности в дельте (маска, 4.3).</summary>
    [Flags]
    public enum DeltaFields : byte
    {
        None = 0,
        Position = 1,
        Direction = 2,

        /// <summary>Доля здоровья (1-001): полоска HealthPct изменилась.</summary>
        Health = 4,

        /// <summary>Предмет в руках (1-002): HeldItem изменился.</summary>
        Held = 8,
    }

    /// <summary>
    /// Дельта снимка (docs/07, 4.3; карточка 0-014): что изменилось относительно базы — снимка, который клиент
    /// подтвердил (<c>LastSnapshot</c> ввода). База — полный склеенный снимок; сущности в ней адресуются
    /// <b>номером в базе</b> (8 бит), а не id: исчезнувшая — номером, изменившаяся — номером, маской полей и полями,
    /// новая — полной записью, как в полном снимке. Порядок сущностей после склейки задаёт <see cref="ApplyTo"/> —
    /// одна функция у сервера (кольцо «что клиент имеет») и у клиента, поэтому номера совпадают.
    /// <para>Схема: тип 1 байт, такт u32, подтверждённый кадр u32, отставание базы u8 (такт − такт базы, 1…255),
    /// исчезнувших u8, изменившихся u8, новых u8; своё состояние (<see cref="SelfState"/>, 0-019); затем исчезнувшие (номер 8 бит), изменившиеся (номер 8 бит,
    /// маска 2 бита, позиция <see cref="SnapshotLayout"/> если есть в маске, направление 4 бита если есть),
    /// новые (запись полного снимка).</para>
    /// </summary>
    public sealed class SnapshotDeltaMessage
    {
        /// <summary>Заголовок: тип 1 + такт 4 + кадр 4 + отставание базы 1 + три числа по 1 + эпоха часов 1 (0-025).</summary>
        public const int HeaderBytes = 14;

        /// <summary>Смещение байта эпохи часов (2.4 «часы после фриза» в заголовке снимка).</summary>
        public const int EpochOffset = 13;

        /// <summary>Эпоха часов сервера (см. <see cref="Snapshot.ClockEpoch"/>).</summary>
        public byte ClockEpoch;

        /// <summary>Бит на номер сущности в базе.</summary>
        public const int IndexBits = 8;

        /// <summary>Бит на маску полей (Position, Direction, Health, Held — 1-002).</summary>
        public const int MaskBits = 4;

        /// <summary>Наибольшее отставание базы (поле — u8).</summary>
        public const int MaxBaselineAge = 255;

        private const int RemovedOffset = 10;
        private const int UpdatedOffset = 11;
        private const int AddedOffset = 12;

        public uint Tick;
        public uint AckFrame;
        public uint BaselineTick;

        /// <summary>Своё подтверждённое состояние (0-019) — целиком, не дельтой к базе.</summary>
        public SelfState Self;

        public int RemovedCount;
        public readonly int[] Removed = new int[Snapshot.MaxEntities];

        public int UpdatedCount;
        public readonly int[] UpdatedIndex = new int[Snapshot.MaxEntities];
        public readonly DeltaFields[] UpdatedFields = new DeltaFields[Snapshot.MaxEntities];

        /// <summary>Новые значения изменившихся: действительны поля из <see cref="UpdatedFields"/>.</summary>
        public readonly SnapshotEntity[] Updated = new SnapshotEntity[Snapshot.MaxEntities];

        public int AddedCount;
        public readonly SnapshotEntity[] Added = new SnapshotEntity[Snapshot.MaxEntities];

        // Склейка: метка на номер базы (0 — без изменений, 1 — исчезла, 2+k — изменение k).
        private readonly int[] _mark = new int[Snapshot.MaxEntities];

        /// <summary>Бит на изменившуюся сущность с полями <paramref name="fields"/>.</summary>
        public static int UpdatedBits(DeltaFields fields, in SnapshotLayout layout) =>
            IndexBits + MaskBits + ((fields & DeltaFields.Position) != 0 ? layout.BitsX + layout.BitsY : 0) +
            ((fields & DeltaFields.Direction) != 0 ? SnapshotLayout.DirectionBits : 0) +
            ((fields & DeltaFields.Health) != 0 ? SnapshotLayout.HealthPctBits : 0) +
            ((fields & DeltaFields.Held) != 0 ? SnapshotLayout.HeldBits : 0);

        /// <summary>Прочитать дельту в этот объект (номера базы проверяет <see cref="ApplyTo"/>).</summary>
        public void Read(ReadOnlySpan<byte> src, in SnapshotLayout layout)
        {
            BitReader r = Codec.Begin(src, MessageType.SnapshotDelta);
            uint tick = r.ReadUInt32();
            uint ack = r.ReadUInt32();
            int age = r.ReadByte();
            int removed = r.ReadByte();
            int updated = r.ReadByte();
            int added = r.ReadByte();
            ClockEpoch = r.ReadByte();
            if (age == 0)
            {
                throw new ProtocolException("Дельта: отставание базы 0.");
            }

            SelfState self = SelfState.Read(ref r, layout);
            for (int i = 0; i < removed; i++)
            {
                Removed[i] = (int)r.ReadBits(IndexBits);
            }

            for (int i = 0; i < updated; i++)
            {
                UpdatedIndex[i] = (int)r.ReadBits(IndexBits);
                var f = (DeltaFields)r.ReadBits(MaskBits);
                if (f == DeltaFields.None)
                {
                    throw new ProtocolException("Дельта: изменившаяся сущность без полей.");
                }

                UpdatedFields[i] = f;
                ref SnapshotEntity e = ref Updated[i];
                if ((f & DeltaFields.Position) != 0)
                {
                    e.Position = ReadPosition(ref r, layout);
                }

                if ((f & DeltaFields.Direction) != 0)
                {
                    e.Direction = (byte)r.ReadBits(SnapshotLayout.DirectionBits);
                    if (e.Direction > InputFrame.MaxDirectionCode)
                    {
                        throw new ProtocolException($"Дельта: код направления {e.Direction} > 8.");
                    }
                }

                if ((f & DeltaFields.Health) != 0)
                {
                    e.HealthPct = (byte)r.ReadBits(SnapshotLayout.HealthPctBits);
                    if (e.HealthPct > 100)
                    {
                        throw new ProtocolException($"Дельта: доля здоровья {e.HealthPct} % > 100.");
                    }
                }

                if ((f & DeltaFields.Held) != 0)
                {
                    e.HeldItem = (byte)r.ReadBits(SnapshotLayout.HeldBits);
                }
            }

            for (int i = 0; i < added; i++)
            {
                ref SnapshotEntity e = ref Added[i];
                e.Id = (int)r.ReadBits(SnapshotLayout.IdBits);
                e.Kind = (byte)r.ReadBits(SnapshotLayout.KindBits);
                e.Position = ReadPosition(ref r, layout);
                e.Direction = (byte)r.ReadBits(SnapshotLayout.DirectionBits);
                e.HealthPct = (byte)r.ReadBits(SnapshotLayout.HealthPctBits);
                e.HeldItem = (byte)r.ReadBits(SnapshotLayout.HeldBits);
                if (e.HealthPct > 100)
                {
                    throw new ProtocolException($"Дельта: доля здоровья {e.HealthPct} % > 100.");
                }
                if (e.Direction > InputFrame.MaxDirectionCode)
                {
                    throw new ProtocolException($"Дельта: сущность {e} с кодом направления > 8.");
                }
            }

            r.ExpectEnd();
            Tick = tick;
            AckFrame = ack;
            BaselineTick = tick - (uint)age;
            Self = self;
            RemovedCount = removed;
            UpdatedCount = updated;
            AddedCount = added;
        }

        /// <summary>
        /// Склеить: база <paramref name="baseEnts"/>[0..<paramref name="baseCount"/>) → <paramref name="into"/>
        /// (не та же память). Порядок: сущности базы по порядку без исчезнувших, изменения на месте, затем новые.
        /// Своя сущность базы (номер 0) так и остаётся первой. Номер вне базы, повтор — <see cref="ProtocolException"/>.
        /// </summary>
        public int ApplyTo(SnapshotEntity[] baseEnts, int baseCount, SnapshotEntity[] into)
        {
            Array.Clear(_mark, 0, baseCount);
            for (int i = 0; i < RemovedCount; i++)
            {
                int j = Removed[i];
                if (j >= baseCount || _mark[j] != 0)
                {
                    throw new ProtocolException($"Дельта: исчезнувшая №{j} вне базы из {baseCount} или повтор.");
                }

                _mark[j] = 1;
            }

            for (int i = 0; i < UpdatedCount; i++)
            {
                int j = UpdatedIndex[i];
                if (j >= baseCount || _mark[j] != 0)
                {
                    throw new ProtocolException($"Дельта: изменившаяся №{j} вне базы из {baseCount} или повтор.");
                }

                _mark[j] = 2 + i;
            }

            int n = 0;
            for (int j = 0; j < baseCount; j++)
            {
                int m = _mark[j];
                if (m == 1)
                {
                    continue;
                }

                SnapshotEntity e = baseEnts[j];
                if (m >= 2)
                {
                    int k = m - 2;
                    if ((UpdatedFields[k] & DeltaFields.Position) != 0)
                    {
                        e.Position = Updated[k].Position;
                    }

                    if ((UpdatedFields[k] & DeltaFields.Direction) != 0)
                    {
                        e.Direction = Updated[k].Direction;
                    }

                    if ((UpdatedFields[k] & DeltaFields.Health) != 0)
                    {
                        e.HealthPct = Updated[k].HealthPct;
                    }

                    if ((UpdatedFields[k] & DeltaFields.Held) != 0)
                    {
                        e.HeldItem = Updated[k].HeldItem;
                    }
                }

                into[n++] = e;
            }

            if (n + AddedCount > Snapshot.MaxEntities)
            {
                throw new ProtocolException($"Дельта: после склейки {n + AddedCount} сущностей > {Snapshot.MaxEntities}.");
            }

            Array.Copy(Added, 0, into, n, AddedCount);
            return n + AddedCount;
        }

        private static WorldPos ReadPosition(ref BitReader r, in SnapshotLayout layout)
        {
            int qx = (int)r.ReadBits(layout.BitsX);
            int qy = (int)r.ReadBits(layout.BitsY);
            var p = new WorldPos(
                (qx << SnapshotLayout.QuantShift) | (1 << (SnapshotLayout.QuantShift - 1)),
                (qy << SnapshotLayout.QuantShift) | (1 << (SnapshotLayout.QuantShift - 1)));
            if (p.X >= layout.Shape.WidthUnits || p.Y >= layout.Shape.HeightUnits)
            {
                throw new ProtocolException($"Дельта: позиция {p} вне карты.");
            }

            return p;
        }

        internal static void PatchCounts(Span<byte> dest, int removed, int updated, int added)
        {
            dest[RemovedOffset] = (byte)removed;
            dest[UpdatedOffset] = (byte)updated;
            dest[AddedOffset] = (byte)added;
        }
    }

    /// <summary>
    /// Запись дельты без выделений. Разделы — строго по порядку: исчезнувшие, изменившиеся, новые (иначе
    /// <see cref="InvalidOperationException"/>). <c>TryAdd*</c> отказывает, когда запись не влезает в лимит байт
    /// (бюджет 1200) или в 255, — сервер шлёт то, что влезло: не влезшее изменение клиент оставит из базы,
    /// не влезшую новую сущность дошлёт следующая дельта.
    /// </summary>
    public ref struct SnapshotDeltaWriter
    {
        private readonly Span<byte> _dest;
        private readonly SnapshotLayout _layout;
        private readonly int _limitBits;
        private BitWriter _w;
        private int _section;
        private int _removed;
        private int _updated;
        private int _added;

        /// <param name="baselineTick">Такт базы: от <paramref name="tick"/> − 255 до <paramref name="tick"/> − 1.</param>
        public SnapshotDeltaWriter(Span<byte> dest, in SnapshotLayout layout, uint tick, uint ackFrame, uint baselineTick, int limitBytes = NetSettings.SnapshotBudget)
            : this(dest, layout, tick, ackFrame, baselineTick, default, limitBytes)
        {
        }

        /// <param name="self">Своё подтверждённое состояние (0-019); без него — признак «нет» и нули.</param>
        public SnapshotDeltaWriter(Span<byte> dest, in SnapshotLayout layout, uint tick, uint ackFrame, uint baselineTick, in SelfState self,
                                   int limitBytes = NetSettings.SnapshotBudget)
        {
            uint age = tick - baselineTick;
            if (age == 0 || age > SnapshotDeltaMessage.MaxBaselineAge)
            {
                throw new ArgumentOutOfRangeException(nameof(baselineTick), $"база {baselineTick} при такте {tick}: отставание 1…255");
            }

            _dest = dest;
            _layout = layout;
            _limitBits = Math.Min(limitBytes, dest.Length) * 8;
            _w = new BitWriter(dest);
            _section = 0;
            _removed = 0;
            _updated = 0;
            _added = 0;
            _w.WriteByte((byte)MessageType.SnapshotDelta);
            _w.WriteUInt32(tick);
            _w.WriteUInt32(ackFrame);
            _w.WriteByte((byte)age);
            _w.WriteByte(0); // числа разделов — в Finish
            _w.WriteByte(0);
            _w.WriteByte(0);
            _w.WriteByte(0); // эпоха часов — SetClockEpoch (0-025)
            self.Write(ref _w, layout);
        }

        /// <summary>«Часы после фриза» в заголовке (2.4; 0-025): эпоха часов сервера — растёт при каждой рассылке «часы: кадр N».</summary>
        public void SetClockEpoch(byte epoch) => _dest[SnapshotDeltaMessage.EpochOffset] = epoch;

        /// <summary>Записано: исчезнувших, изменившихся, новых.</summary>
        public int Removed => _removed;

        /// <inheritdoc cref="Removed"/>
        public int Updated => _updated;

        /// <inheritdoc cref="Removed"/>
        public int Added => _added;

        /// <summary>Исчезнувшая сущность базы (номер в базе); не влезла — false.</summary>
        public bool TryAddRemoved(int baseIndex)
        {
            Enter(0);
            if (_removed == Snapshot.MaxEntities || _w.BitPosition + SnapshotDeltaMessage.IndexBits > _limitBits)
            {
                return false;
            }

            _w.WriteBits((uint)baseIndex, SnapshotDeltaMessage.IndexBits);
            _removed++;
            return true;
        }

        /// <summary>Изменившаяся сущность базы: номер, поля по маске; не влезла — false (ничего не записано).</summary>
        public bool TryAddUpdated(int baseIndex, DeltaFields fields, WorldPos position, byte direction, byte healthPct = 100, byte heldItem = 0)
        {
            Enter(1);
            if (fields == DeltaFields.None)
            {
                throw new ArgumentException("пустая маска полей", nameof(fields));
            }

            if (_updated == Snapshot.MaxEntities || _w.BitPosition + SnapshotDeltaMessage.UpdatedBits(fields, _layout) > _limitBits)
            {
                return false;
            }

            _w.WriteBits((uint)baseIndex, SnapshotDeltaMessage.IndexBits);
            _w.WriteBits((uint)fields, SnapshotDeltaMessage.MaskBits);
            if ((fields & DeltaFields.Position) != 0)
            {
                _w.WriteBits((uint)(position.X >> SnapshotLayout.QuantShift), _layout.BitsX);
                _w.WriteBits((uint)(position.Y >> SnapshotLayout.QuantShift), _layout.BitsY);
            }

            if ((fields & DeltaFields.Direction) != 0)
            {
                _w.WriteBits(direction, SnapshotLayout.DirectionBits);
            }

            if ((fields & DeltaFields.Health) != 0)
            {
                _w.WriteBits(healthPct, SnapshotLayout.HealthPctBits);
            }

            if ((fields & DeltaFields.Held) != 0)
            {
                _w.WriteBits(heldItem, SnapshotLayout.HeldBits);
            }

            _updated++;
            return true;
        }

        /// <summary>Новая сущность — полной записью; не влезла — false.</summary>
        public bool TryAddAdded(int id, byte kind, WorldPos position, byte direction, byte healthPct = 100, byte heldItem = 0)
        {
            Enter(2);
            if (_added == Snapshot.MaxEntities || _w.BitPosition + _layout.EntityBits > _limitBits)
            {
                return false;
            }

            _w.WriteBits((uint)id, SnapshotLayout.IdBits);
            _w.WriteBits(kind, SnapshotLayout.KindBits);
            _w.WriteBits((uint)(position.X >> SnapshotLayout.QuantShift), _layout.BitsX);
            _w.WriteBits((uint)(position.Y >> SnapshotLayout.QuantShift), _layout.BitsY);
            _w.WriteBits(direction, SnapshotLayout.DirectionBits);
            _w.WriteBits(healthPct, SnapshotLayout.HealthPctBits);
            _w.WriteBits(heldItem, SnapshotLayout.HeldBits);
            _added++;
            return true;
        }

        /// <summary>Закрыть дельту: записать числа; вернуть длину в байтах.</summary>
        public int Finish()
        {
            SnapshotDeltaMessage.PatchCounts(_dest, _removed, _updated, _added);
            return _w.BytesWritten;
        }

        private void Enter(int section)
        {
            if (section < _section)
            {
                throw new InvalidOperationException("Дельта: разделы пишутся по порядку — исчезнувшие, изменившиеся, новые.");
            }

            _section = section;
        }
    }
}
