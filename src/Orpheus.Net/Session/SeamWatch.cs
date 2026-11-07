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

        /// <summary>Кадр 60 Гц по местным часам, мкс; окно «1/30 с» дольше 1,1 × 2 кадров — опоздавший кадр цикла.</summary>
        public const long FrameUs = 1_000_000 / 60;

        /// <summary>Без чужих столько мкс после того, как их видели, — «один» (бегущий ушёл): наблюдение кончено.</summary>
        public const long AloneAfterUs = 3_000_000;

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
        }

        private readonly MapShape _map;
        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private long _frame;
        private long _lastSeenUs = -1;
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

        /// <summary>Наибольший сдвиг за 1/30 с, тайла.</summary>
        public double MaxStep30Tiles { get; private set; }

        /// <summary>Местное время окна наибольшего сдвига за 1/30 с, мкс (≈ 33 333 при ровном цикле).</summary>
        public long MaxStep30WindowUs { get; private set; }

        /// <summary>Сдвигов за 1/30 с больше <see cref="LimitPer30Tiles"/>.</summary>
        public long Over30 { get; private set; }

        /// <summary>Из них — в окне дольше 1,1 × 1/30 с по местным часам (опоздал кадр цикла бота, а не показ).</summary>
        public long Over30LateWindow { get; private set; }

        /// <summary>Наибольшее окно «1/30 с» по местным часам, мкс.</summary>
        public long MaxWindowUs { get; private set; }

        /// <summary>Замеров за кадр 60 Гц.</summary>
        public long Steps60 { get; private set; }

        /// <summary>Наибольший сдвиг за кадр 60 Гц, тайла.</summary>
        public double MaxStep60Tiles { get; private set; }

        /// <summary>Сдвигов за кадр 60 Гц больше <see cref="LimitPer60Tiles"/> (для справки).</summary>
        public long Over60 { get; private set; }

        /// <summary>Чужих, бывших в показе.</summary>
        public int Seen => _tracks.Count;

        /// <summary>Видел чужих, а теперь <see cref="AloneAfterUs"/> их нет в снимках: бегущий ушёл.</summary>
        public bool Alone { get; private set; }

        /// <summary>
        /// Приёмка 0.2 у наблюдателя: кадров с превышением 0.22 нет, чужой в показе по обе стороны каждого шва, замеры есть.
        /// </summary>
        public bool Passed => Over30 == 0 && Steps30 > 0 && ViewEastOfX > 0 && ViewWestOfX > 0 && ViewSouthOfY > 0 && ViewNorthOfY > 0;

        /// <summary>Снимок принят (из <see cref="NetClient.SnapshotApplied"/>), время — местные мкс приёма.</summary>
        public void OnSnapshot(Snapshot s, long localUs) => Interp.OnSnapshot(s, localUs);

        /// <summary>«Часы после фриза» (<see cref="NetClient.ClockReset"/>).</summary>
        public void OnClockReset() => Interp.OnClockReset();

        /// <summary>Кадр рисования: время показа, позиции чужих (кроме <paramref name="selfId"/>), замер.</summary>
        public void Frame(long localUs, int selfId)
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
            for (int i = 0; i < snap.Count; i++)
            {
                othersInSnapshot |= snap.Entities[i].Id != selfId;
            }

            if (othersInSnapshot)
            {
                _lastSeenUs = localUs;
            }

            Alone = _tracks.Count > 0 && _lastSeenUs >= 0 && localUs - _lastSeenUs >= AloneAfterUs;
        }

        private void Measure(int id, WorldPos from, float dx, float dy, long localUs)
        {
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

            if (_tracks.TryGetValue(id, out Track tr) && tr.Frame == _frame - 1)
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
                    if (step30 > MaxStep30Tiles)
                    {
                        MaxStep30Tiles = step30;
                        MaxStep30WindowUs = window;
                    }

                    if (step30 > LimitPer30Tiles)
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

                tr.PrevUx = tr.Ux;
                tr.PrevUy = tr.Uy;
                tr.PrevLocalUs = tr.LocalUs;
                tr.Ux = ux;
                tr.Uy = uy;
                tr.Run++;
            }
            else
            {
                tr = new Track { Ux = from.X + (double)dx, Uy = from.Y + (double)dy, Run = 1 };
                tr.PrevUx = tr.Ux;
                tr.PrevUy = tr.Uy;
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
