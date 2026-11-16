using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;

namespace Orpheus.Net.Protocol
{
    /// <summary>Что сообщает «снаряд» (тип 38).</summary>
    public enum ProjectileOp : byte
    {
        /// <summary>Выстрел: всё, что нужно для полёта на клиенте (<see cref="Projectile.PositionAt"/>).</summary>
        Shot = 1,

        /// <summary>Снаряд кончился: попал, воткнулся, долетел.</summary>
        Stop = 2,
    }

    /// <summary>
    /// «Снаряд» (E3-2, тип 38, канал 2 всем в игре; `02` 4.4): снаряды не в снимке — выстрел и конец событиями, полёт
    /// между ними клиент считает сам тем же счётом ядра. Выстрел: тип 1 + оп 1 + номер u32 + владелец u32 + вид 1 +
    /// флаги 1 (бит 0 — снаряд врага) + такт выстрела u32 + точка x u32, y u32 + ось u16 + скорость u32 (unit × 256 за
    /// такт) + дальность u32 (unit) = 34 байта. Конец: тип 1 + оп 1 + номер u32 + причина 1 + точка x u32, y u32 +
    /// жертва u32 (−1 — нет) + воткнулась кучей 1 = 20 байт. Попадания решает только сервер.
    /// </summary>
    public struct ProjectileMessage
    {
        public const int ShotBytes = 34;
        public const int StopBytes = 20;

        public ProjectileOp Op;
        public int Id;
        public int Owner;
        public byte Kind;
        public bool Hostile;
        public uint SpawnTick;
        public WorldPos Position;
        public ushort AimRaw;
        public int SpeedX256;
        public int RangeUnits;
        public ProjectileEnd Reason;
        public int Victim;
        public bool Dropped;

        /// <summary>Выстрел из снаряда ядра.</summary>
        public static ProjectileMessage ShotOf(in Projectile p) => new ProjectileMessage
        {
            Op = ProjectileOp.Shot,
            Id = p.Id,
            Owner = p.Owner,
            Kind = p.Kind,
            Hostile = p.Hostile,
            SpawnTick = (uint)p.SpawnTick,
            Position = p.Origin,
            AimRaw = p.AimRaw,
            SpeedX256 = p.SpeedX256,
            RangeUnits = p.RangeUnits,
        };

        /// <summary>Конец из записи ядра.</summary>
        public static ProjectileMessage StopOf(in ProjectileStop s) => new ProjectileMessage
        {
            Op = ProjectileOp.Stop,
            Id = s.Id,
            Owner = s.Owner,
            Reason = s.Reason,
            Position = s.Position,
            Victim = s.Victim,
            Dropped = s.Dropped,
        };

        /// <summary>Снаряд ядра по выстрелу (для полёта на клиенте).</summary>
        public readonly Projectile ToProjectile() => new Projectile
        {
            Id = Id,
            Owner = Owner,
            Hostile = Hostile,
            Kind = Kind,
            SpawnTick = SpawnTick,
            Origin = Position,
            AimRaw = AimRaw,
            SpeedX256 = SpeedX256,
            RangeUnits = RangeUnits,
        };

        public readonly int Write(Span<byte> dest)
        {
            var w = new BitWriter(dest);
            w.WriteByte((byte)MessageType.Projectile);
            w.WriteByte((byte)Op);
            w.WriteUInt32((uint)Id);
            if (Op == ProjectileOp.Shot)
            {
                w.WriteUInt32((uint)Owner);
                w.WriteByte(Kind);
                w.WriteByte(Hostile ? (byte)1 : (byte)0);
                w.WriteUInt32(SpawnTick);
                w.WriteUInt32((uint)Position.X);
                w.WriteUInt32((uint)Position.Y);
                w.WriteUInt16(AimRaw);
                w.WriteUInt32((uint)SpeedX256);
                w.WriteUInt32((uint)RangeUnits);
            }
            else
            {
                w.WriteByte((byte)Reason);
                w.WriteUInt32((uint)Position.X);
                w.WriteUInt32((uint)Position.Y);
                w.WriteUInt32((uint)Victim);
                w.WriteByte(Dropped ? (byte)1 : (byte)0);
            }

            return w.BytesWritten;
        }

        public static ProjectileMessage Read(ReadOnlySpan<byte> src)
        {
            var r = Codec.Begin(src, MessageType.Projectile);
            var m = new ProjectileMessage { Op = (ProjectileOp)r.ReadByte(), Id = (int)r.ReadUInt32(), Victim = -1 };
            if (m.Op == ProjectileOp.Shot)
            {
                m.Owner = (int)r.ReadUInt32();
                m.Kind = r.ReadByte();
                byte flags = r.ReadByte();
                m.Hostile = (flags & 1) != 0;
                m.SpawnTick = r.ReadUInt32();
                m.Position = new WorldPos((int)r.ReadUInt32(), (int)r.ReadUInt32());
                m.AimRaw = r.ReadUInt16();
                m.SpeedX256 = (int)r.ReadUInt32();
                m.RangeUnits = (int)r.ReadUInt32();
                if (flags > 1 || m.SpeedX256 <= 0 || m.RangeUnits <= 0)
                {
                    throw new ProtocolException($"Снаряд #{m.Id}: флаги {flags}, скорость {m.SpeedX256}, дальность {m.RangeUnits}.");
                }
            }
            else if (m.Op == ProjectileOp.Stop)
            {
                m.Reason = (ProjectileEnd)r.ReadByte();
                m.Position = new WorldPos((int)r.ReadUInt32(), (int)r.ReadUInt32());
                m.Victim = (int)r.ReadUInt32();
                byte dropped = r.ReadByte();
                m.Dropped = dropped == 1;
                if (m.Reason < ProjectileEnd.Hit || m.Reason > ProjectileEnd.Range || dropped > 1)
                {
                    throw new ProtocolException($"Снаряд #{m.Id}: причина {(byte)m.Reason}, куча {dropped}.");
                }
            }
            else
            {
                throw new ProtocolException($"Снаряд: операция {(byte)m.Op}.");
            }

            r.ExpectEnd();
            return m;
        }

        public override readonly string ToString() => Op == ProjectileOp.Shot
            ? $"выстрел #{Id} ({Owner}) вид {Kind} от {Position} ось {AimRaw} v {SpeedX256} дальн. {RangeUnits} такт {SpawnTick}"
            : $"снаряд #{Id}: {Reason} {Position}{(Victim >= 0 ? $" → #{Victim}" : "")}{(Dropped ? ", лежит" : "")}";
    }
}
