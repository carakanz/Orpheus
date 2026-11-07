using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Наблюдатель у шва (карточка 0-027; docs/08, 3.0 приёмка 0.2): показывает чужих так же, как клиент Unity —
    /// <see cref="RemoteInterpolation"/> на время «сервер сейчас − 100 мс», — и меряет, насколько показанная позиция чужого
    /// сдвинулась за 1/30 с (два кадра 60 Гц) и за кадр 60 Гц. Предел приёмки — 6 т/с × 1/30 с × 1,1 = 0,22 тайла
    /// (60 Гц — 0,11, для справки). Сдвиг — разность по тору между соседними кадрами (<see cref="Torus.Delta(in MapShape, WorldPos, WorldPos)"/>),
    /// сложенная в непрерывную траекторию: без заворота переход шва дал бы скачок ≈ ширины карты.
    /// <para>
    /// Скачок сверх движения (то, что ловит шов): показ сравнивается с траекторией бегущего по снимкам, развёрнутой отдельно —
    /// по приходу каждого снимка позиция складывается из разностей по тору с прошлым снимком, — на то же время показа.
    /// Сдвиг показа за 1/30 с минус сдвиг этой траектории за то же время показа — <see cref="MaxExcess30Tiles"/>; предел
    /// <see cref="LimitExcessTiles"/>. Неровный ход самого бегущего на сервере (кадры ввода шагаются пачкой по приходу,
    /// 07 2.7: при разбросе σ 10 мс 0…5 кадров за такт) двигает обе одинаково и сюда не попадает; скачок показа на шве
    /// (пара без тора, заворот в показе) — попадает.
    /// </para>
    /// <para>
    /// Стороны шва — по показанной позиции: x &lt; W/2 — «восток» шва x = 0, иначе «запад»; y &lt; H/2 — «юг» шва y = 0,
    /// иначе «север». Приёмка: чужой виден по обе стороны каждого шва.
    /// </para>
    /// </summary>
    public sealed class SeamWatch
    {
        /// <summary>Предел сдвига за 1/30 с: 6 т/с × 1/30 с × 1,1, тайла.</summary>
        public const double LimitPer30Tiles = 6.0 / 30 * 1.1;

        /// <summary>Предел сдвига за кадр 60 Гц (для справки): 6 т/с × 1/60 с × 1,1, тайла.</summary>
        public const double LimitPer60Tiles = 6.0 / 60 * 1.1;

        /// <summary>Предел скачка показа сверх траектории снимков за 1/30 с: 10 % от 0,22 тайла.</summary>
        public const double LimitExcessTiles = LimitPer30Tiles * 0.1;

        /// <summary>Снимков в истории траектории одной сущности (≈ 1 с при 30 Гц).</summary>
        public const int RefRing = 32;

        /// <summary>Разрыв в снимках больше стольких тактов — траектория начинается заново (сущность уходила из обзора).</summary>
        public const int RefGapTicks = 15;

        /// <summary>Кадр 60 Гц по местным часам, мкс; окно «1/30 с» дольше 1,1 × 2 кадров — опоздавший кадр цикла.</summary>
        public const long FrameUs = 1_000_000 / 60;

        /// <summary>
        /// Чужой пропал из снимков рядом с наблюдателем (не ушёл за край обзора, а вышел из игры) и не вернулся столько мкс —
        /// «один»: наблюдение кончено. Уход за край обзора (бегущий на круге) — не конец.
        /// </summary>
        public const long AloneAfterUs = 3_000_000;

        /// <summary>
        /// Первая секунда показа чужого (от первого кадра, где он виден, по местным часам) — не в счёт буквального сдвига
        /// (<see cref="Over30"/>, <see cref="MaxStep30Tiles"/>): на входе первые кадры ввода бегущего приходят на сервер пачкой
        /// (проверка 0-027: 3 окна на чистой сети — такты 17–18 после входа, не шов). Они — в <see cref="Over30Warmup"/>.
        /// </summary>
        public const long WarmupUs = 1_000_000;

        /// <summary>«Рядом» для <see cref="AloneAfterUs"/>: чужой в последнем снимке ближе стольких тайлов по каждой оси (окно обзора — 6 × 4 чанка).</summary>
        public const int NearTiles = 24;

        private const double U = Torus.SubTilesPerTile;

        private struct Track
        {
            public long Frame;
            public WorldPos From;
            public float Dx;
            public float Dy;
            public double Ux;      // непрерывная траектория (без заворота), unit
            public double Uy;
            public double PrevUx;  // кадр назад
            public double PrevUy;
            public long PrevLocalUs;
            public long LocalUs;
            public int Run;        // кадров подряд в показе
            public bool EastOfX;   // сторона шва x = 0 в прошлом кадре
            public bool SouthOfY;
            public double Rx;      // траектория снимков на время показа, unit
            public double Ry;
            public double PrevRx;
            public double PrevRy;
            public int RefEpoch;   // траектория начата заново — сравнивать нельзя
            public double Base0X;  // начало отрезка: показ и траектория (для расхождения)
            public double Base0Y;
            public double Base0RX;
            public double Base0RY;
        }

        // Траектория по снимкам: развёрнута по тору при приходе снимка, независимо от показа.
        private sealed class RefTrack
        {
            public readonly uint[] Tick = new uint[RefRing];
            public readonly double[] X = new double[RefRing];
            public readonly double[] Y = new double[RefRing];
            public WorldPos Raw;
            public int Head;
            public int Count;
            public int Epoch;
        }

        private readonly MapShape _map;
        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private readonly Dictionary<int, RefTrack> _refs = new Dictionary<int, RefTrack>();
        private readonly Dictionary<int, long> _firstShownUs = new Dictionary<int, long>();
        private long _frame;
        private long _lastSeenUs = -1;
        private bool _lastNear;
        private long _firstFrameUs = -1;

        public SeamWatch(in MapShape map)
        {
            _map = map;
            Interp = new RemoteInterpolation(map);
        }

        /// <summary>Показ чужих — как у клиента Unity.</summary>
        public RemoteInterpolation Interp { get; }

        /// <summary>Кадров рисования (с временем показа).</summary>
        public long Frames { get; private set; }

        /// <summary>Кадров, где хоть один чужой в показе.</summary>
        public long FramesInView { get; private set; }

        /// <summary>Кадров в показе по сторонам: восток / запад шва x = 0, юг / север шва y = 0.</summary>
        public long ViewEastOfX { get; private set; }

        /// <inheritdoc cref="ViewEastOfX"/>
        public long ViewWestOfX { get; private set; }

        /// <inheritdoc cref="ViewEastOfX"/>
        public long ViewSouthOfY { get; private set; }

        /// <inheritdoc cref="ViewEastOfX"/>
        public long ViewNorthOfY { get; private set; }

        /// <summary>Переходов шва x = 0 / y = 0 в показе (соседние кадры по разные стороны, рядом со швом).</summary>
        public long CrossingsX { get; private set; }

        /// <inheritdoc cref="CrossingsX"/>
        public long CrossingsY { get; private set; }

        /// <summary>Замеров сдвига за 1/30 с (окно из трёх кадров подряд в показе).</summary>
        public long Steps30 { get; private set; }

        /// <summary>Наибольший сдвиг за 1/30 с (кроме первой секунды показа чужого), тайла.</summary>
        public double MaxStep30Tiles { get; private set; }

        /// <summary>Местное время окна наибольшего сдвига за 1/30 с, мкс (≈ 33 333 при ровном цикле).</summary>
        public long MaxStep30WindowUs { get; private set; }

        /// <summary>Сдвигов за 1/30 с больше <see cref="LimitPer30Tiles"/> (кроме первой секунды показа чужого, <see cref="WarmupUs"/>).</summary>
        public long Over30 { get; private set; }

        /// <summary>Сдвигов за 1/30 с больше <see cref="LimitPer30Tiles"/> в первую секунду показа чужого — не в счёт.</summary>
        public long Over30Warmup { get; private set; }

        /// <summary>Из них — в окне дольше 1,1 × 1/30 с по местным часам (опоздал кадр цикла бота, а не показ).</summary>
        public long Over30LateWindow { get; private set; }

        /// <summary>Наибольшее окно «1/30 с» по местным часам, мкс.</summary>
        public long MaxWindowUs { get; private set; }

        /// <summary>Наибольший сдвиг показа за 1/30 с сверх сдвига траектории снимков за то же время показа, тайла.</summary>
        public double MaxExcess30Tiles { get; private set; }

        /// <summary>Окон 1/30 с со скачком сверх траектории больше <see cref="LimitExcessTiles"/>.</summary>
        public long OverExcess { get; private set; }

        /// <summary>Наибольшее расхождение показа с траекторией снимков (с поправкой на начало), тайла.</summary>
        public double MaxDeviationTiles { get; private set; }

        /// <summary>Замеров за кадр 60 Гц.</summary>
        public long Steps60 { get; private set; }

        /// <summary>Наибольший сдвиг за кадр 60 Гц, тайла.</summary>
        public double MaxStep60Tiles { get; private set; }

        /// <summary>Сдвигов за кадр 60 Гц больше <see cref="LimitPer60Tiles"/> (для справки).</summary>
        public long Over60 { get; private set; }

        /// <summary>Чужих, бывших в показе.</summary>
        public int Seen => _tracks.Count;

        /// <summary>Видел чужих, последний пропал из снимков рядом и <see cref="AloneAfterUs"/> их нет: бегущий вышел из игры.</summary>
        public bool Alone { get; private set; }

        /// <summary>
        /// Приёмка 0.2 у наблюдателя (решение 0-027, вопрос [док]): скачков показа сверх траектории снимков нет
        /// (<see cref="OverExcess"/> = 0), чужой в показе по обе стороны каждого шва, замеры есть. Сдвиг за 1/30 с против
        /// 0,22 тайла (<see cref="Over30"/>) — в отчёте, но при разбросе он ловит неровный ход бегущего на сервере, а не шов.
        /// </summary>
        public bool Passed => OverExcess == 0 && Steps30 > 0 && ViewEastOfX > 0 && ViewWestOfX > 0 && ViewSouthOfY > 0 && ViewNorthOfY > 0;

        /// <summary>То же, но и по сдвигу за 1/30 с ≤ 0,22 тайла буквально (08 3.0, 0.2).</summary>
        public bool PassedLiteral => Passed && Over30 == 0;

        /// <summary>Снимок принят (из <see cref="NetClient.SnapshotApplied"/>), время — местные мкс приёма.</summary>
        public void OnSnapshot(Snapshot s, long localUs)
        {
            bool newer = !Interp.HasSnapshots || (int)(s.Tick - Interp.Latest.Tick) > 0;
            Interp.OnSnapshot(s, localUs);
            if (!newer)
            {
                return;
            }

            for (int i = 0; i < s.Count; i++)
            {
                SnapshotEntity e = s.Entities[i];
                if (!_refs.TryGetValue(e.Id, out RefTrack? r))
                {
                    r = new RefTrack();
                    _refs[e.Id] = r;
                }

                int last = (r.Head + RefRing - 1) % RefRing;
                double x;
                double y;
                if (r.Count > 0 && (int)(s.Tick - r.Tick[last]) > 0 && (int)(s.Tick - r.Tick[last]) <= RefGapTicks)
                {
                    Int2 d = Torus.Delta(_map, r.Raw, e.Position);
                    x = r.X[last] + d.X;
                    y = r.Y[last] + d.Y;
                }
                else
                {
                    r.Count = 0;
                    r.Epoch++;
                    x = e.Position.X;
                    y = e.Position.Y;
                }

                r.Tick[r.Head] = s.Tick;
                r.X[r.Head] = x;
                r.Y[r.Head] = y;
                r.Head = (r.Head + 1) % RefRing;
                r.Count = Math.Min(RefRing, r.Count + 1);
                r.Raw = e.Position;
            }
        }

        // Траектория снимков на время показа (то же правило доли, что в RemoteInterpolation: по времени тактов, мкс).
        private bool RefAt(int id, long renderUs, out double x, out double y, out int epoch)
        {
            x = y = 0;
            epoch = 0;
            if (!_refs.TryGetValue(id, out RefTrack? r) || r.Count == 0)
            {
                return false;
            }

            epoch = r.Epoch;
            int newest = (r.Head + RefRing - 1) % RefRing;
            if (renderUs >= RemoteInterpolation.TickTimeUs(r.Tick[newest]))
            {
                x = r.X[newest];
                y = r.Y[newest];
                return true;
            }

            int next = newest;
            for (int k = 1; k < r.Count; k++)
            {
                int i = (newest - k + RefRing) % RefRing;
                long ti = RemoteInterpolation.TickTimeUs(r.Tick[i]);
                if (ti <= renderUs)
                {
                    long tn = RemoteInterpolation.TickTimeUs(r.Tick[next]);
                    double f = tn > ti ? (renderUs - ti) / (double)(tn - ti) : 1.0;
                    x = r.X[i] + (r.X[next] - r.X[i]) * f;
                    y = r.Y[i] + (r.Y[next] - r.Y[i]) * f;
                    return true;
                }

                next = i;
            }

            x = r.X[next];
            y = r.Y[next];
            return true;
        }

        /// <summary>«Часы после фриза» (<see cref="NetClient.ClockReset"/>).</summary>
        public void OnClockReset() => Interp.OnClockReset();

        /// <summary>Кадр рисования: время показа, позиции чужих (кроме <paramref name="selfId"/>, стоящего в <paramref name="self"/>), замер.</summary>
        public void Frame(long localUs, int selfId, WorldPos self)
        {
            if (_firstFrameUs < 0)
            {
                _firstFrameUs = localUs;
            }

            if (!Interp.BeginFrame(localUs))
            {
                return;
            }

            _frame++;
            Frames++;
            Snapshot snap = Interp.Latest;
            bool any = false;
            for (int i = 0; i < snap.Count; i++)
            {
                int id = snap.Entities[i].Id;
                if (id == selfId || !Interp.TryGet(id, out WorldPos from, out float dx, out float dy))
                {
                    continue;
                }

                any = true;
                Measure(id, from, dx, dy, localUs);
            }

            if (any)
            {
                FramesInView++;
                _lastSeenUs = localUs;
            }

            bool othersInSnapshot = false;
            bool near = false;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Entities[i].Id == selfId)
                {
                    continue;
                }

                othersInSnapshot = true;
                Int2 d = Torus.Delta(_map, self, snap.Entities[i].Position);
                near |= Math.Abs(d.X) < NearTiles * Torus.SubTilesPerTile && Math.Abs(d.Y) < NearTiles * Torus.SubTilesPerTile;
            }

            if (othersInSnapshot)
            {
                _lastSeenUs = localUs;
                _lastNear = near;
            }

            Alone = _tracks.Count > 0 && _lastNear && _lastSeenUs >= 0 && localUs - _lastSeenUs >= AloneAfterUs;
        }

        private void Measure(int id, WorldPos from, float dx, float dy, long localUs)
        {
            bool hasRef = RefAt(id, Interp.RenderTimeUs, out double rx, out double ry, out int epoch);
            if (!_firstShownUs.TryGetValue(id, out long shownUs))
            {
                shownUs = localUs;
                _firstShownUs[id] = shownUs;
            }

            bool warmup = localUs - shownUs < WarmupUs;
            // Показанная позиция (по тору, в пределах карты) — для сторон шва.
            double px = Wrap(from.X + (double)dx, _map.WidthUnits);
            double py = Wrap(from.Y + (double)dy, _map.HeightUnits);
            bool east = px < _map.WidthUnits / 2.0;
            bool south = py < _map.HeightUnits / 2.0;
            if (east)
            {
                ViewEastOfX++;
            }
            else
            {
                ViewWestOfX++;
            }

            if (south)
            {
                ViewSouthOfY++;
            }
            else
            {
                ViewNorthOfY++;
            }

            if (_tracks.TryGetValue(id, out Track tr) && tr.Frame == _frame - 1 && hasRef && tr.RefEpoch == epoch)
            {
                // Сдвиг за кадр — по тору: точка пары могла смениться и перейти шов.
                Int2 d = Torus.Delta(_map, tr.From, from);
                double sx = d.X + (double)dx - tr.Dx;
                double sy = d.Y + (double)dy - tr.Dy;
                double step60 = Math.Sqrt(sx * sx + sy * sy) / U;
                Steps60++;
                MaxStep60Tiles = Math.Max(MaxStep60Tiles, step60);
                if (step60 > LimitPer60Tiles)
                {
                    Over60++;
                }

                double ux = tr.Ux + sx;
                double uy = tr.Uy + sy;
                if (tr.Run >= 2)
                {
                    double wx = ux - tr.PrevUx;
                    double wy = uy - tr.PrevUy;
                    double step30 = Math.Sqrt(wx * wx + wy * wy) / U;
                    long window = localUs - tr.PrevLocalUs;
                    Steps30++;
                    MaxWindowUs = Math.Max(MaxWindowUs, window);
                    if (warmup)
                    {
                        if (step30 > LimitPer30Tiles)
                        {
                            Over30Warmup++;
                        }
                    }
                    else if (step30 > MaxStep30Tiles)
                    {
                        MaxStep30Tiles = step30;
                        MaxStep30WindowUs = window;
                    }

                    double ex = wx - (rx - tr.PrevRx);
                    double ey = wy - (ry - tr.PrevRy);
                    double excess = Math.Sqrt(ex * ex + ey * ey) / U;
                    MaxExcess30Tiles = Math.Max(MaxExcess30Tiles, excess);
                    if (excess > LimitExcessTiles)
                    {
                        OverExcess++;
                    }

                    if (!warmup && step30 > LimitPer30Tiles)
                    {
                        Over30++;
                        if (window * 10 > 2 * FrameUs * 11)
                        {
                            Over30LateWindow++;
                        }
                    }
                }

                // Переход шва: соседние кадры по разные стороны и рядом со швом (а не с противоположной кромкой карты).
                if (east != tr.EastOfX && (Math.Min(px, _map.WidthUnits - px) < _map.WidthUnits / 4.0))
                {
                    CrossingsX++;
                }

                if (south != tr.SouthOfY && (Math.Min(py, _map.HeightUnits - py) < _map.HeightUnits / 4.0))
                {
                    CrossingsY++;
                }

                double devX = (ux - tr.Base0X) - (rx - tr.Base0RX);
                double devY = (uy - tr.Base0Y) - (ry - tr.Base0RY);
                MaxDeviationTiles = Math.Max(MaxDeviationTiles, Math.Sqrt(devX * devX + devY * devY) / U);
                tr.PrevUx = tr.Ux;
                tr.PrevUy = tr.Uy;
                tr.PrevRx = tr.Rx;
                tr.PrevRy = tr.Ry;
                tr.PrevLocalUs = tr.LocalUs;
                tr.Ux = ux;
                tr.Uy = uy;
                tr.Rx = rx;
                tr.Ry = ry;
                tr.Run++;
            }
            else
            {
                tr = new Track { Ux = from.X + (double)dx, Uy = from.Y + (double)dy, Rx = rx, Ry = ry, RefEpoch = epoch, Run = 1 };
                tr.PrevUx = tr.Ux;
                tr.PrevUy = tr.Uy;
                tr.PrevRx = rx;
                tr.PrevRy = ry;
                tr.Base0X = tr.Ux;
                tr.Base0Y = tr.Uy;
                tr.Base0RX = rx;
                tr.Base0RY = ry;
                tr.PrevLocalUs = localUs;
            }

            tr.Frame = _frame;
            tr.From = from;
            tr.Dx = dx;
            tr.Dy = dy;
            tr.LocalUs = localUs;
            tr.EastOfX = east;
            tr.SouthOfY = south;
            _tracks[id] = tr;
        }

        private static double Wrap(double v, int size)
        {
            double r = v % size;
            return r < 0 ? r + size : r;
        }
    }
}
