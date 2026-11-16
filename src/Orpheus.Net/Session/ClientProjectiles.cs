using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>Летящая (или только что остановившаяся) стрела на клиенте (E3-2).</summary>
    public struct FlyingProjectile
    {
        /// <summary>Параметры полёта: с сервера (выстрел, тип 38) или предсказанные своим выстрелом (номер 0, пока сервер не подтвердил).</summary>
        public Projectile P;

        /// <summary>Миг «возраст 0» по часам клиента, мкс.</summary>
        public long StartUs;

        /// <summary>Свой выстрел, нарисованный предсказанием.</summary>
        public bool Own;

        /// <summary>Сервер прислал выстрел (номер известен).</summary>
        public bool Confirmed;

        /// <summary>Сервер прислал конец: стрела летит до точки конца и там останавливается.</summary>
        public bool Stopped;

        /// <summary>Путь до точки конца, unit × 256.</summary>
        public long StopTravelX256;

        public ProjectileEnd Reason;
        public int Victim;
        public bool Dropped;

        /// <summary>Миг, когда картинка дошла до точки конца (0 — ещё летит).</summary>
        public long ArrivedUs;
    }

    /// <summary>
    /// Стрелы на клиенте (E3-2, `02` 4.4): полёт по событию выстрела тем же счётом ядра (<see cref="Projectile.PositionAtTravel"/>)
    /// от мига приёма; свой выстрел — сразу по предсказанию (<see cref="LocalPrediction.TryTakeShot"/>), выстрел сервера с
    /// владельцем «я» подменяет параметры старейшей неподтверждённой своей стрелы (время полёта — своё). Конец — по пути:
    /// картинка долетает до точки конца сервера и там гаснет (воткнулась — висит <see cref="StuckUs"/>), без привязки к часам
    /// сервера. Неподтверждённая своя стрела (сервер отказал: стрел нет) гаснет через <see cref="UnconfirmedUs"/>.
    /// Чистый C#, без выделений в кадре (список переиспользуется).
    /// </summary>
    public sealed class ClientProjectiles
    {
        /// <summary>Длина такта, мкс.</summary>
        public const double TickUs = 1_000_000.0 / Projectiles.TicksPerSecond;

        /// <summary>Воткнутая стрела видна 3 с (дальше — куча, если легла).</summary>
        public const long StuckUs = 3_000_000;

        /// <summary>Своя стрела без подтверждения сервера гаснет через 1,5 с.</summary>
        public const long UnconfirmedUs = 1_500_000;

        private readonly List<FlyingProjectile> _items = new List<FlyingProjectile>(32);
        private MapShape _map;

        public ClientProjectiles(MapShape map) => _map = map;

        /// <summary>Форма мира (после входа).</summary>
        public MapShape Map
        {
            get => _map;
            set => _map = value;
        }

        /// <summary>Стрелы по порядку появления.</summary>
        public IReadOnlyList<FlyingProjectile> Items => _items;

        /// <summary>Выстрелов с сервера принято.</summary>
        public long ShotsSeen { get; private set; }

        /// <summary>Выстрел с сервера (для позы выстрела чужих).</summary>
        public Action<ProjectileMessage>? ShotSeen { get; set; }

        /// <summary>Концов снарядов с сервера принято.</summary>
        public long StopsSeen { get; private set; }

        /// <summary>Последний принятый конец снаряда.</summary>
        public ProjectileMessage LastStop { get; private set; }

        /// <summary>Своих выстрелов, подтверждённых сервером (предсказание подменено).</summary>
        public long OwnConfirmed { get; private set; }

        /// <summary>Своих выстрелов, погасших без подтверждения.</summary>
        public long OwnExpired { get; private set; }

        /// <summary>Свой выстрел по предсказанию: сила, ось, точка из <paramref name="shot"/>, числа — строка лука <paramref name="bowItem"/>.</summary>
        public void AddPredicted(in BowAction shot, byte bowItem, long nowUs)
        {
            if (!Projectiles.TryGetBow(bowItem, out BowDef def))
            {
                return;
            }

            int power = BowAction.PowerQ8(shot.ShotDraw);
            _items.Add(new FlyingProjectile
            {
                P = new Projectile
                {
                    Kind = Projectiles.KindOfBow(bowItem),
                    Origin = Torus.Wrap(_map, new WorldPos(shot.ShotX, shot.ShotY)),
                    AimRaw = shot.ShotAim,
                    SpeedX256 = def.SpeedX256At(power),
                    RangeUnits = def.RangeUnits,
                    Damage = (byte)def.DamageAt(power),
                },
                StartUs = nowUs,
                Own = true,
                Victim = -1,
            });
        }

        /// <summary>Сообщение «снаряд» (тип 38); <paramref name="ownEntity"/> — свой персонаж.</summary>
        public void OnMessage(in ProjectileMessage m, int ownEntity, long nowUs)
        {
            if (m.Op == ProjectileOp.Shot)
            {
                ShotsSeen++;
                ShotSeen?.Invoke(m);
                if (m.Owner == ownEntity)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        FlyingProjectile f = _items[i];
                        if (f.Own && !f.Confirmed)
                        {
                            f.P = m.ToProjectile();
                            f.Confirmed = true;
                            _items[i] = f;
                            OwnConfirmed++;
                            return;
                        }
                    }
                }

                _items.Add(new FlyingProjectile { P = m.ToProjectile(), StartUs = nowUs, Own = m.Owner == ownEntity, Confirmed = true, Victim = -1 });
                return;
            }

            StopsSeen++;
            LastStop = m;
            for (int i = 0; i < _items.Count; i++)
            {
                FlyingProjectile f = _items[i];
                if (f.Confirmed && f.P.Id == m.Id)
                {
                    f.Stopped = true;
                    Int2 d = Torus.Delta(_map, f.P.Origin, m.Position);
                    f.StopTravelX256 = (long)(Math.Sqrt((double)d.LengthSquared) * 256.0);
                    f.Reason = m.Reason;
                    f.Victim = m.Victim;
                    f.Dropped = m.Dropped;
                    _items[i] = f;
                    return;
                }
            }
        }

        /// <summary>Путь стрелы к мигу <paramref name="nowUs"/>, unit × 256 (с потолком: точка конца или дальность).</summary>
        public long TravelX256(in FlyingProjectile f, long nowUs)
        {
            double ticks = Math.Max(0, nowUs - f.StartUs) / TickUs;
            long t = (long)(f.P.SpeedX256 * ticks);
            long cap = f.Stopped ? f.StopTravelX256 : (long)f.P.RangeUnits << 8;
            return Math.Min(t, cap);
        }

        /// <summary>Позиция стрелы к мигу <paramref name="nowUs"/> (обёрнута).</summary>
        public WorldPos PositionOf(in FlyingProjectile f, long nowUs) =>
            Torus.Wrap(_map, f.P.PositionAtTravel(TravelX256(f, nowUs)));

        /// <summary>Стрела дошла до точки конца (стоит).</summary>
        public bool Arrived(in FlyingProjectile f, long nowUs) => f.Stopped && TravelX256(f, nowUs) >= f.StopTravelX256;

        /// <summary>Кадр: отметить долетевшие, убрать погасшие (попала, долетела, воткнулась 3 с назад, своя без подтверждения).</summary>
        public void Update(long nowUs)
        {
            int w = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                FlyingProjectile f = _items[i];
                bool keep = true;
                if (f.Own && !f.Confirmed && nowUs - f.StartUs > UnconfirmedUs)
                {
                    keep = false;
                    OwnExpired++;
                }
                else if (f.Stopped && Arrived(f, nowUs))
                {
                    if (f.ArrivedUs == 0)
                    {
                        f.ArrivedUs = nowUs;
                    }

                    keep = f.Reason == ProjectileEnd.Wall && nowUs - f.ArrivedUs < StuckUs;
                }
                else if (!f.Stopped && TravelX256(f, nowUs) >= (long)f.P.RangeUnits << 8 && nowUs - f.StartUs > UnconfirmedUs * 2)
                {
                    keep = false; // конец так и не пришёл (вошёл в игру посреди полёта) — страховка
                }

                if (keep)
                {
                    _items[w++] = f;
                }
            }

            _items.RemoveRange(w, _items.Count - w);
        }

        /// <summary>Убрать все (выход из мира).</summary>
        public void Clear() => _items.Clear();
    }
}
