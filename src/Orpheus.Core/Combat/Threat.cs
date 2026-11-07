using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Ближний удар врага (docs/02, 8.1, архетип «Ближний»; 3.1 — только размер сектора): дуга 90°, досягаемость 1,5 т,
    /// замах / удар / отход 0,40 / 0,10 / 0,60 с — в кадрах ввода 60 Гц. Числа — предварительные, как в документе.
    /// </summary>
    public static class EnemyMelee
    {
        /// <summary>Досягаемость 1,5 т = 384 unit.</summary>
        public const int ReachUnits = 384;

        /// <summary>Половина дуги 90°: 45° = 8192 шага угла.</summary>
        public const int HalfArcSteps = 8192;

        /// <summary>Замах d = 0,40 с = 24 кадра.</summary>
        public const int WindupFrames = 24;

        /// <summary>Удар 0,10 с = 6 кадров (окно поражения; в шипе удар решается в одном кадре — конце замаха).</summary>
        public const int StrikeFrames = 6;

        /// <summary>Отход 0,60 с = 36 кадров.</summary>
        public const int RecoverFrames = 36;

        /// <summary>Полный цикл удара: 1,10 с = 66 кадров — чаще враг бить не может.</summary>
        public const int CycleFrames = WindupFrames + StrikeFrames + RecoverFrames;
    }

    /// <summary>Вид угрозы (docs/07, 2.7 «Угроза»): в шипе — только ближний удар; зона, снаряд, рывок — потом.</summary>
    public enum ThreatKind : byte
    {
        /// <summary>Ближний удар: сектор, кадр начала замаха, длина замаха.</summary>
        Melee = 1,
    }

    /// <summary>
    /// Угроза — атака врага по игрокам (docs/07, 2.7): ближний удар — сектор, кадр начала замаха s (время сервера, кадры
    /// 60 Гц) и длина замаха d. Это же — событие «замах начат» для снимка (`02` 15.1: «событие с серверным временем начала»).
    /// </summary>
    public readonly struct Threat
    {
        public readonly ThreatKind Kind;

        /// <summary>Номер атакующей сущности.</summary>
        public readonly int AttackerId;

        /// <summary>Кадр начала замаха s (время сервера, кадры ввода 60 Гц).</summary>
        public readonly uint StartFrame;

        /// <summary>Длина замаха d, кадров.</summary>
        public readonly int WindupFrames;

        /// <summary>Сектор удара.</summary>
        public readonly MeleeSector Sector;

        public Threat(ThreatKind kind, int attackerId, uint startFrame, int windupFrames, in MeleeSector sector)
        {
            Kind = kind;
            AttackerId = attackerId;
            StartFrame = startFrame;
            WindupFrames = windupFrames;
            Sector = sector;
        }

        /// <summary>Кадр конца замаха s + d — касание. В шипе удар решается в нём (без сдвига D_p; карточка 0-029, вопрос `[док]`).</summary>
        public uint ContactFrame => unchecked(StartFrame + (uint)WindupFrames);

        public override string ToString() => $"угроза {Kind} от #{AttackerId}: s {StartFrame}, d {WindupFrames}, {Sector}";
    }

    /// <summary>Решение ближнего удара по персонажу в одном кадре (docs/07, 2.7 «Решение»; `02` 2.2, 2.4, 3.1).</summary>
    public static class MeleeHit
    {
        /// <summary>
        /// Попал ли удар по персонажу, стоящему так, как <paramref name="target"/> после кадра решения: коллизия
        /// (круг <see cref="Collision.RadiusUnits"/>) пересекает сектор, и персонаж не в кадрах неуязвимости уворота.
        /// </summary>
        public static bool Lands(in MapShape map, in Threat threat, in Mover target) =>
            !target.Dodge.Invulnerable && threat.Sector.Overlaps(map, target.Position, Collision.RadiusUnits);
    }

    /// <summary>
    /// Очередь угроз (docs/07, 2.7 «Угроза»): атаки врагов по игрокам живут в ней 1 с (<see cref="LifetimeFrames"/>) от
    /// начала замаха — сервер решает по ним удар во времени каждого игрока. Ёмкость фиксирована, в такте не выделяет:
    /// полна — отказ. Порядок — по добавлению (кадры начала не убывают).
    /// </summary>
    public sealed class ThreatQueue
    {
        /// <summary>Срок жизни угрозы: 1 с = 60 кадров от начала замаха.</summary>
        public const int LifetimeFrames = 60;

        private readonly Threat[] _items;
        private int _count;

        public ThreatQueue(int capacity = 128)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _items = new Threat[capacity];
        }

        /// <summary>Угроз в очереди.</summary>
        public int Count => _count;

        /// <summary>Ёмкость.</summary>
        public int Capacity => _items.Length;

        /// <summary>Угроза по порядку добавления.</summary>
        public ref readonly Threat this[int i]
        {
            get
            {
                if ((uint)i >= (uint)_count)
                {
                    throw new ArgumentOutOfRangeException(nameof(i));
                }

                return ref _items[i];
            }
        }

        /// <summary>Добавить угрозу; очередь полна — false.</summary>
        public bool Add(in Threat threat)
        {
            if (_count == _items.Length)
            {
                return false;
            }

            _items[_count++] = threat;
            return true;
        }

        /// <summary>Убрать угрозы старше 1 с к кадру <paramref name="nowFrame"/> (кадр начала + 60 ≤ сейчас). Возвращает, сколько убрано.</summary>
        public int Expire(uint nowFrame)
        {
            int keep = 0;
            for (int i = 0; i < _count; i++)
            {
                if (unchecked((int)(nowFrame - _items[i].StartFrame)) < LifetimeFrames)
                {
                    _items[keep++] = _items[i];
                }
            }

            int removed = _count - keep;
            for (int i = keep; i < _count; i++)
            {
                _items[i] = default;
            }

            _count = keep;
            return removed;
        }

        /// <summary>Очистить.</summary>
        public void Clear()
        {
            Array.Clear(_items, 0, _count);
            _count = 0;
        }
    }
}
