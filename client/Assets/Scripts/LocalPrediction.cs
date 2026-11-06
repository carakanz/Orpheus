#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;

namespace Orpheus.Client
{
    /// <summary>
    /// Предсказание и сверка своего персонажа (docs/07, 2.6; карточка 0-013).
    /// <list type="number">
    /// <item>Клиент шагает персонаж <see cref="Mover"/> ядра на каждый кадр ввода (60 Гц; шаг — половина
    /// такта 30 Гц, т.е. та же скорость, что у сервера) и кладёт (кадр, ввод, позиция) в кольцевой буфер
    /// на <see cref="BufferSize"/> кадров. Тот же ввод летит на сервер.</item>
    /// <item>Сервер в снимке шлёт <c>AckFrame</c> — последний применённый кадр ввода этого клиента
    /// (позиция своей сущности в снимке — уже «после» него; квант 1/32 тайла). Клиент сравнивает свою
    /// позицию на этот кадр с серверной: ближе <see cref="MatchUnits"/> — ничего не делает.</item>
    /// <item>Дальше — принимает серверную позицию и заново шагает сохранённые кадры новее ack.</item>
    /// <item>Картинка не прыгает: смещение «картинка → новая предсказанная» гасится за 0,1 с
    /// (до 0,5 тайла), 0,3 с (до 2 тайлов), мгновенно (больше).</item>
    /// </list>
    /// Рисование — <see cref="RenderDelta"/>: предсказанная позиция + остаток гашения; персонаж живёт
    /// в своём «сейчас», не во времени интерполяции чужих (2.9).
    /// </summary>
    public sealed class LocalPrediction
    {
        /// <summary>Кадров в буфере (2.6: 64 кадра = 1 с при 60 Гц).</summary>
        public const int BufferSize = 64;

        /// <summary>Порог совпадения: 0,02 тайла = 5,12 unit (1/256); квант снимка ±4 unit входит в порог.</summary>
        public const int MatchUnits = 5;

        /// <summary>Границы гашения: 0,5 и 2 тайла — по 2.6.</summary>
        const int NearUnits = 128;
        const int FarUnits = 512;
        const long NearUs = 100_000;
        const long FarUs = 300_000;

        /// <summary>Шаг на кадр ввода при 60 Гц: половина такта 30 Гц — скорость совпадает с серверной.</summary>
        public const int HalfTickSpeedX10 = MovementTick.BaseSpeedX10 / 2;

        struct Slot
        {
            public uint Frame;
            public InputFrame Input;
            public WorldPos After;
        }

        readonly MapShape _map;
        readonly TileSolid _solid;
        Mover _mover;

        readonly Slot[] _slots = new Slot[BufferSize];
        readonly Slot[] _replay = new Slot[BufferSize];
        int _slotsHead;
        int _slotsCount;

        readonly uint[] _sentFrame = new uint[BufferSize];
        readonly InputFrame[] _sentInput = new InputFrame[BufferSize];
        int _sentHead;
        int _sentCount;

        // Гашение: смещение картинки от предсказанной позиции, затухает к нулю за _errDurUs.
        Int2 _err;
        long _errStartUs;
        long _errDurUs;

        public LocalPrediction(MapShape map, TileSolid solid, WorldPos start)
        {
            _map = map;
            _solid = solid;
            _mover = new Mover(start, HalfTickSpeedX10);
        }

        /// <summary>Сверок выполнено (каждый снимок со своей сущностью).</summary>
        public long Reconciled { get; private set; }

        /// <summary>Сверок, закончившихся приёмом серверной позиции и переигровкой.</summary>
        public long Resynced { get; private set; }

        /// <summary>Наибольшее расхождение при правке, unit (1/256 тайла); первый снимок не считается.</summary>
        public int MaxDevUnits { get; private set; }

        /// <summary>Предсказанная позиция после последнего шага.</summary>
        public WorldPos Position => _mover.Position;

        /// <summary>
        /// Кадр ввода отправлен на сервер под номером <paramref name="frame"/> (60 Гц) — тем же шагом
        /// персонаж идёт вперёд и кадр попадает в буфер предсказания.
        /// </summary>
        public void Sent(uint frame, InputFrame input)
        {
            _sentFrame[_sentHead] = frame;
            _sentInput[_sentHead] = input;
            _sentHead = (_sentHead + 1) % BufferSize;
            if (_sentCount < BufferSize)
            {
                _sentCount++;
            }

            _mover.Step(_map, input.Direction, _solid);
            _slots[_slotsHead] = new Slot { Frame = frame, Input = input, After = _mover.Position };
            _slotsHead = (_slotsHead + 1) % BufferSize;
            if (_slotsCount < BufferSize)
            {
                _slotsCount++;
            }
        }

        /// <summary>Поставить персонажа в точку без пути (телепорт; буферы и гашение — чисто).</summary>
        public void Teleport(WorldPos p)
        {
            _mover = new Mover(p, HalfTickSpeedX10);
            _slotsHead = _slotsCount = 0;
            _sentHead = _sentCount = 0;
            _err = default;
            _errDurUs = 0;
        }

        /// <summary>
        /// Сверка с снимком: <paramref name="ackFrame"/> — последний применённый сервером кадр ввода
        /// (серверная позиция — уже после него), <paramref name="serverPos"/> — позиция своей сущности
        /// в снимке (квант 1/32), время — монотонные мкс.
        /// </summary>
        public void Reconcile(uint ackFrame, WorldPos serverPos, long nowUs)
        {
            Reconciled++;

            int idx = SlotAtOrBefore(ackFrame);
            if (idx >= 0)
            {
                WorldPos pred = PredictedAt(_slots[idx], ackFrame);
                Int2 d = Torus.Delta(_map, pred, serverPos);
                int dev = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
                if (dev <= MatchUnits)
                {
                    return; // совпало (2.6, п.3)
                }

                MaxDevUnits = Math.Max(MaxDevUnits, dev);
            }

            Resynced++;

            // Точка, где картинка сейчас (предсказание + остаток гашения) — от неё и гасим новый скачок.
            RenderDelta(nowUs, out WorldPos renderFrom, out float rdx, out float rdy);
            var render = new WorldPos((int)(renderFrom.X + rdx), (int)(renderFrom.Y + rdy));

            // Сохранить кадры новее ack (старые — с ними сервер уже разобрался), затем очистка буфера.
            int n = 0;
            for (int steps = 0; steps < _slotsCount; steps++)
            {
                int i = Mod(_slotsHead - 1 - steps);
                if ((int)(_slots[i].Frame - ackFrame) <= 0)
                {
                    break;
                }

                _replay[n++] = _slots[i];
            }

            // Серверная позиция уже «после» ack — шаг самим ack не нужен; переигровка — только кадры новее.
            _mover = new Mover(serverPos, HalfTickSpeedX10);
            _slotsHead = _slotsCount = 0;
            for (int k = n - 1; k >= 0; k--)
            {
                Slot s = _replay[k];
                _mover.Step(_map, s.Input.Direction, _solid);
                _slots[_slotsHead] = new Slot { Frame = s.Frame, Input = s.Input, After = _mover.Position };
                _slotsHead = (_slotsHead + 1) % BufferSize;
                if (_slotsCount < BufferSize)
                {
                    _slotsCount++;
                }
            }

            Int2 e = Torus.Delta(_map, _mover.Position, render);
            int edev = Math.Max(Math.Abs(e.X), Math.Abs(e.Y));
            _errDurUs = edev == 0 ? 0 : edev <= NearUnits ? NearUs : edev <= FarUnits ? FarUs : 0;
            _err = _errDurUs == 0 ? default : e;
            _errStartUs = nowUs;
        }

        /// <summary>
        /// Позиция для рисования в форме «точка + дельта» (как <c>LocalCharacter.Interpolated</c>):
        /// предсказанная позиция и остаток гашения в unit (1/256 тайла).
        /// </summary>
        public void RenderDelta(long nowUs, out WorldPos from, out float dxUnits, out float dyUnits)
        {
            from = _mover.Position;
            if (_errDurUs <= 0)
            {
                dxUnits = dyUnits = 0f;
                return;
            }

            long t = nowUs - _errStartUs;
            if (t >= _errDurUs)
            {
                _err = default;
                _errDurUs = 0;
                dxUnits = dyUnits = 0f;
                return;
            }

            float k = 1f - t / (float)_errDurUs;
            dxUnits = _err.X * k;
            dyUnits = _err.Y * k;
        }

        /// <summary>Текущее гасящее смещение, unit по максимальной оси (для HUD).</summary>
        public int ErrorUnits(long nowUs)
        {
            RenderDelta(nowUs, out _, out float dx, out float dy);
            return Math.Max(Math.Abs((int)dx), Math.Abs((int)dy));
        }

        // ---------- внутреннее ----------

        /// <summary>Индекс новейшего слота с кадром ≤ ack; нет (ack старше буфера) — −1.</summary>
        int SlotAtOrBefore(uint ackFrame)
        {
            for (int steps = 0; steps < _slotsCount; steps++)
            {
                int i = Mod(_slotsHead - 1 - steps);
                if ((int)(_slots[i].Frame - ackFrame) <= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Предсказанная позиция на момент ack: позиция слота (шаг этим кадром); если слот старше ack
        /// (кадрack не шагал — пришёл между нашими тактами), сверху шаг input'ом ack, как сделал сервер.
        /// </summary>
        WorldPos PredictedAt(Slot slot, uint ackFrame)
        {
            if (slot.Frame == ackFrame || !TrySent(ackFrame, out InputFrame input))
            {
                return slot.After;
            }

            var m = new Mover(slot.After, HalfTickSpeedX10);
            m.Step(_map, input.Direction, _solid);
            return m.Position;
        }

        bool TrySent(uint frame, out InputFrame input)
        {
            for (int steps = 0; steps < _sentCount; steps++)
            {
                int i = Mod(_sentHead - 1 - steps);
                if (_sentFrame[i] == frame)
                {
                    input = _sentInput[i];
                    return true;
                }
            }

            input = default;
            return false;
        }

        int Mod(int i)
        {
            int r = i % BufferSize;
            return r < 0 ? r + BufferSize : r;
        }
    }
}
