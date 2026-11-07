#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;

namespace Orpheus.Client
{
    /// <summary>
    /// Свой персонаж без сети (карточка 0-010, шаг 5): кадр ввода каждый кадр Unity (60 Гц и чаще) →
    /// <see cref="Mover"/> ядра в локальном такте 30 Гц (docs/07, 2.4 и 4.2) → позиция для рисования —
    /// интерполяция между двумя последними тактами по доле прошедшего такта (docs/07, 2.9).
    /// <para>
    /// Такт берёт последний кадр ввода перед ним. Отставание больше <see cref="MaxTicksPerFrame"/> тактов
    /// (окно свернули, отладчик) не догоняется — лишнее время отбрасывается, чтобы не «прыгать».
    /// Сеть подключит следующая задача клиента: тот же такт станет предсказанием (2.6).
    /// </para>
    /// </summary>
    public sealed class LocalCharacter
    {
        public const int TickRate = Simulation.TickRate;
        public const double TickSeconds = 1.0 / TickRate;
        public const int MaxTicksPerFrame = 4;

        readonly MapShape _map;
        Mover _mover;
        WorldPos _prev;
        double _acc;

        public LocalCharacter(MapShape map, TileSolid solid, WorldPos start)
        {
            _map = map;
            Solid = solid;
            Teleport(start);
        }

        /// <summary>Проходимость тайлов (smoke может поставить <see cref="Collision.Empty"/>, если на шве нет чистой полосы).</summary>
        public TileSolid Solid { get; set; }

        /// <summary>Последний кадр ввода (его возьмёт ближайший такт).</summary>
        public InputFrame Input { get; set; }

        /// <summary>Позиция по ядру после последнего такта.</summary>
        public WorldPos Position => _mover.Position;

        /// <summary>Позиция после предпоследнего такта (начало отрезка интерполяции).</summary>
        public WorldPos Previous => _prev;

        /// <summary>Тактов выполнено.</summary>
        public long Ticks { get; private set; }

        /// <summary>Доля такта, прошедшая после последнего такта, 0…1.</summary>
        public float Alpha => _acc <= 0 ? 0f : _acc >= TickSeconds ? 1f : (float)(_acc / TickSeconds);

        // Кадр 1/60 с дважды даёт ровно такт; без допуска сумма двух double чуть меньше 1/30 и такт съезжает на кадр.
        const double Epsilon = 1e-9;

        /// <summary>Поставить персонажа в точку без пути (телепорт; остаток скорости и время такта — с нуля).</summary>
        public void Teleport(WorldPos p)
        {
            _mover = Mover.At(_map, p);
            _prev = _mover.Position;
            _acc = 0;
        }

        /// <summary>Прошло <paramref name="dt"/> секунд кадра: выполнить созревшие такты. Возвращает их число.</summary>
        public int Advance(double dt)
        {
            _acc += dt;
            int ticks = 0;
            while (_acc >= TickSeconds - Epsilon)
            {
                if (ticks == MaxTicksPerFrame)
                {
                    _acc = 0;
                    break;
                }

                _prev = _mover.Position;
                _mover.Step(_map, Input.Direction, Solid);
                _acc -= TickSeconds;
                ticks++;
                Ticks++;
            }

            return ticks;
        }

        /// <summary>
        /// Позиция для рисования в 1/256 тайла (float — дробь внутри unit не теряется): предыдущий такт +
        /// доля × Δ(предыдущий, последний) по тору. Возвращает начало отрезка и сдвиг от него.
        /// </summary>
        public void Interpolated(out WorldPos from, out float dxUnits, out float dyUnits)
        {
            Int2 d = Torus.Delta(_map, _prev, _mover.Position);
            float a = Alpha;
            from = _prev;
            dxUnits = d.X * a;
            dyUnits = d.Y * a;
        }
    }
}
