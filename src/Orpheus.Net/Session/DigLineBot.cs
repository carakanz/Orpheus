using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот <c>dig_line</c> (docs/07, 11.3; приёмка `08` 0.7; 0-028): идёт линиями поперёк шва и снимает скалы — тем же протоколом,
    /// что игрок: «копать: начало», кнопка копания в кадрах ввода, предсказание и ответ сервера.
    /// <para>
    /// Скал на одной линии в мире заглушки мало (вопрос <c>[док]</c> карточки), поэтому линий много: полоса шириной
    /// 2 × <see cref="HalfLength"/> тайлов с центром на шве (x = 0), строки через <see cref="RowStep"/> тайла, змейкой —
    /// на восток, сдвиг на юг, на запад и т. д. На строке бот копает скалы в досягаемости (строки y − 1 … y + 1) и всё
    /// природное, что мешает идти (деревья — тоже, но в счёт идут только скалы). До полосы — идёт к шву, копая только помехи.
    /// </para>
    /// Решает по своему миру (<see cref="ClientWorld"/>) и своей позиции; сервер он не видит.
    /// </summary>
    public sealed class DigLineBot
    {
        public const string Name = "dig_line";

        /// <summary>Шаг строк: 3 — копание берёт строки y − 1 … y + 1, полосы не перекрываются.</summary>
        public const int RowStep = 3;

        private const int U = Torus.SubTilesPerTile;
        private const int RowTolerance = U / 8;
        private const int StuckFrames = 90;

        private readonly MapShape _shape;
        private readonly Dictionary<ushort, bool> _digs = new Dictionary<ushort, bool>(); // запрос → копали скалу в полосе
        private readonly Dictionary<TileCoord, int> _skipUntil = new Dictionary<TileCoord, int>(); // «далеко» — пауза тайла
        private int _row = int.MinValue;
        private int _dir = 1;
        private bool _shifting;
        private bool _entered;
        private int _frame;
        private WorldPos _lastPos;
        private int _stillFrames;

        /// <param name="shape">Форма карты.</param>
        /// <param name="halfLength">Полуширина полосы, тайлов по обе стороны шва.</param>
        /// <param name="target">Сколько скал снять.</param>
        public DigLineBot(MapShape shape, int halfLength = 48, int target = 1000)
        {
            _shape = shape;
            HalfLength = halfLength;
            Target = target;
        }

        public int HalfLength { get; }
        public int Target { get; }

        /// <summary>Скал снято в полосе (ответ «принято»).</summary>
        public int RocksDug { get; private set; }

        /// <summary>Прочего природного снято (помехи на пути, скалы вне полосы).</summary>
        public int OtherDug { get; private set; }

        /// <summary>Отказов на копание (любая причина).</summary>
        public int Refused { get; private set; }

        /// <summary>Отказов по причине (индекс — <see cref="EditReply"/>).</summary>
        public int[] RefusedBy { get; } = new int[(int)EditReply.NotAllowed + 1];

        /// <summary>Отказы строкой: «далеко 3, прервано 1».</summary>
        public string RefusalsText => string.Join(", ", Enumerable.Range(0, RefusedBy.Length).Where(i => RefusedBy[i] > 0)
            .Select(i => $"{EditRules.Describe((EditReply)i)} {RefusedBy[i]}"));

        /// <summary>Строк пройдено (сдвигов на следующую).</summary>
        public int Rows { get; private set; }

        /// <summary>Тайлы снятых скал в полосе — для сверки у наблюдателя и после перезапуска.</summary>
        public List<TileCoord> RockTiles { get; } = new List<TileCoord>();

        /// <summary>Сняли <see cref="Target"/> скал.</summary>
        public bool Done => RocksDug >= Target;

        private readonly Dictionary<ushort, TileCoord> _digTile = new Dictionary<ushort, TileCoord>();

        /// <summary>Ответ сервера (подключить к <see cref="NetClient.EditReplied"/>).</summary>
        public void OnReply(TileReplyMessage m)
        {
            if (!_digs.TryGetValue(m.RequestId, out bool rock))
            {
                return;
            }

            TileCoord t = _digTile[m.RequestId];
            _digs.Remove(m.RequestId);
            _digTile.Remove(m.RequestId);
            if (m.Reply != EditReply.Accepted)
            {
                Refused++;
                RefusedBy[(int)m.Reply]++;
                if (m.Reply == EditReply.OutOfReach)
                {
                    _skipUntil[t] = _frame + 30;
                }

                return;
            }

            if (rock)
            {
                RocksDug++;
                RockTiles.Add(t);
            }
            else
            {
                OtherDug++;
            }
        }

        /// <summary>Кадр ввода (60 Гц): копать, идти или стоять. <paramref name="pos"/> — своя позиция (снимок или предсказание).</summary>
        public InputFrame Next(NetClient client, WorldPos pos)
        {
            _frame++;
            ClientWorld? world = client.World;
            if (world == null || Done)
            {
                return InputFrame.None;
            }

            if (client.Digging)
            {
                return new InputFrame(Input8.None, InputButtons.Dig);
            }

            TileCoord me = Torus.TileOf(_shape, pos);
            if (_row == int.MinValue)
            {
                _row = me.Y;
                _lastPos = pos;
            }

            int dxSeam = Torus.DeltaX(_shape, 0, pos.X); // от шва (x = 0) до бота, единицы
            bool inBand = Math.Abs(dxSeam) <= HalfLength * U;
            if (!_entered)
            {
                if (inBand)
                {
                    _entered = true;
                }
                else
                {
                    _dir = dxSeam > 0 ? -1 : 1; // к шву по кратчайшему
                }
            }

            int yc = Torus.WrapY(_shape, _row * U + U / 2);
            int dy = Torus.DeltaY(_shape, pos.Y, yc);
            if (_shifting && Math.Abs(dy) <= RowTolerance)
            {
                _shifting = false;
            }

            if (!_shifting && _entered && (_dir > 0 ? dxSeam >= HalfLength * U : dxSeam <= -HalfLength * U))
            {
                NextRow();
                yc = Torus.WrapY(_shape, _row * U + U / 2);
                dy = Torus.DeltaY(_shape, pos.Y, yc);
            }

            // Что копать: сначала помеха по ходу, потом скалы в досягаемости (в полосе).
            if (TryDig(client, world, pos, me, inBand))
            {
                _stillFrames = 0;
                return new InputFrame(Input8.None, InputButtons.Dig);
            }

            Input8 move = _shifting
                ? new Input8(0, dy > 0 ? 1 : -1)
                : Math.Abs(dy) > RowTolerance * 2
                    ? new Input8(_dir, dy > 0 ? 1 : -1)
                    : new Input8(_dir, 0);

            if (pos == _lastPos)
            {
                if (++_stillFrames > StuckFrames)
                {
                    _stillFrames = 0;
                    NextRow(); // упёрлись в некопаемое — следующая строка
                }
            }
            else
            {
                _stillFrames = 0;
                _lastPos = pos;
            }

            return new InputFrame(move);
        }

        // Досягаемость с запасом 1/4 тайла: своя позиция — из снимка, сервер её уже обогнал.
        private bool InReach(WorldPos pos, TileCoord t)
        {
            const int reach = EditRules.DigReachUnits - U / 4;
            return Torus.Dist2(_shape, pos, EditRules.CenterOf(t)) <= (long)reach * reach;
        }

        private void NextRow()
        {
            _row += RowStep;
            _dir = -_dir;
            _shifting = true;
            Rows++;
        }

        private bool TryDig(NetClient client, ClientWorld world, WorldPos pos, TileCoord me, bool inBand)
        {
            int ax = _shifting ? 0 : _dir;
            int ay = _shifting ? 1 : 0;

            // Помехи: тайлы, которые круг персонажа задел бы, сдвинувшись на полтайла по ходу.
            var ahead = new WorldPos(pos.X + ax * U / 2, pos.Y + ay * U / 2);
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(me.X + ox, me.Y + oy));
                    if ((ox == 0 && oy == 0) || !Collision.CircleOverlapsTile(_shape, ahead, t))
                    {
                        continue;
                    }

                    ushort obj = world.World.GetObject(t);
                    if (ObjectId.IsSolid(obj) && Dig(client, world, pos, t, obj, inBand))
                    {
                        return true;
                    }
                }
            }

            if (!inBand || _shifting)
            {
                return false;
            }

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(me.X + ox, me.Y + oy));
                    ushort obj = world.World.GetObject(t);
                    if (ObjectId.IdOf(obj) == ObjectId.Rock && !ObjectId.IsPart(obj) && Dig(client, world, pos, t, obj, inBand))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool Dig(NetClient client, ClientWorld world, WorldPos pos, TileCoord t, ushort obj, bool inBand)
        {
            if (!EditRules.IsDiggable(obj) || world.IsPredicted(t) || !InReach(pos, t))
            {
                return false;
            }

            if (_skipUntil.TryGetValue(t, out int until) && _frame < until)
            {
                return false;
            }

            ushort id = client.StartDig(t);
            if (id == 0)
            {
                return false;
            }

            _digs[id] = inBand && ObjectId.IdOf(obj) == ObjectId.Rock;
            _digTile[id] = t;
            return true;
        }
    }
}
