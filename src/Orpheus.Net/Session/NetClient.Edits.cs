using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Правки тайлов со стороны клиента (docs/07, 3.7; 0-028) — без Unity: этим же пользуются боты и тесты.
    /// <list type="bullet">
    /// <item><see cref="StartDig"/> — «копать: начало»; прогресс копится по своим кадрам ввода с зажатым
    /// <see cref="InputButtons.Dig"/> (<see cref="SendFrame"/>) — тем же счётом, что у сервера; докопали — предсказание
    /// «объект снят» (<see cref="ClientWorld.Predict"/>). <see cref="StopDig"/> до конца — «копать: конец».</item>
    /// <item><see cref="Place"/> — линия до 32 тайлов: тайлы, свободные по своему миру, предсказываются сразу.</item>
    /// <item>Ответ сервера — <see cref="ClientWorld.Resolve"/>: принятые остаются, остальные откатываются с вспышкой.</item>
    /// </list>
    /// </summary>
    public sealed partial class NetClient
    {
        private ushort _nextRequest = 1;
        private bool _digActive;
        private bool _digPredicted;
        private ushort _digRequest;
        private TileCoord _digTile;
        private ushort _digObj;
        private readonly TileCoord[] _line = new TileCoord[EditRules.MaxLineTiles];

        /// <summary>Каждый ответ сервера на правку (после <see cref="ClientWorld.Resolve"/>).</summary>
        public Action<TileReplyMessage>? EditReplied { get; set; }

        /// <summary>Запросов правки отправлено / ответов принято.</summary>
        public long EditRequestsSent { get; private set; }

        /// <inheritdoc cref="EditRequestsSent"/>
        public long EditRepliesReceived { get; private set; }

        /// <summary>
        /// Копание идёт: начато и ответа ещё нет. Докопанное своим счётом (<see cref="DigPredicted"/>) — тоже: кнопку держат до
        /// ответа, сервер копит время по своим кадрам и мог недосчитать (перескок, выброс кадров) — новое начало его бы прервало.
        /// </summary>
        public bool Digging => _digActive;

        /// <summary>Докопано своим счётом — «объект снят» предсказан, ждём ответа.</summary>
        public bool DigPredicted => _digActive && _digPredicted;

        /// <summary>Тайл текущего копания.</summary>
        public TileCoord DigTile => _digTile;

        /// <summary>Кадров копания насчитано своим вводом (0…<see cref="EditRules.DigFrames"/>).</summary>
        public int DigProgress { get; private set; }

        /// <summary>
        /// «Копать: начало» (канал 2): тайл <paramref name="tile"/>. Прежнее недокопанное копание сервер прервёт сам. Не в игре — 0.
        /// Дальше кадры ввода с <see cref="InputButtons.Dig"/> копят прогресс.
        /// </summary>
        public ushort StartDig(TileCoord tile)
        {
            if (State != NetClientState.InGame || World == null)
            {
                return 0;
            }

            TileCoord t = Torus.Wrap(World.Shape, tile);
            ushort id = NextRequest();
            _digActive = true;
            _digPredicted = false;
            _digRequest = id;
            _digTile = t;
            _digObj = World.World.GetObject(t);
            DigProgress = 0;
            SendRequest(new TileRequestMessage { RequestId = id, Op = TileOp.DigStart, Frame = _input.LastFrame + 1, From = t, To = t });
            return id;
        }

        /// <summary>«Копать: конец» — кнопку отпустили до конца копания; докопанное своим счётом не прерывается.</summary>
        public void StopDig()
        {
            if (!_digActive || _digPredicted)
            {
                return;
            }

            _digActive = false;
            SendRequest(new TileRequestMessage { RequestId = _digRequest, Op = TileOp.DigStop, Frame = _input.LastFrame + 1, From = _digTile, To = _digTile });
        }

        /// <summary>
        /// «Поставить»: линия от <paramref name="from"/> до <paramref name="to"/> (до 32 тайлов), объект <paramref name="obj"/>.
        /// Тайлы, которые по своему миру и позиции <paramref name="self"/> проходят проверку, предсказываются сразу. Возвращает
        /// номер запроса; линия длиннее 32 или не в игре — 0.
        /// </summary>
        public ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self)
        {
            if (State != NetClientState.InGame || World == null)
            {
                return 0;
            }

            int n = EditRules.Line(World.Shape, from, to, _line);
            if (n < 0)
            {
                return 0;
            }

            ushort id = NextRequest();
            for (int i = 0; i < n; i++)
            {
                TileCoord t = _line[i];
                if (EditRules.CheckPlaceTile(World.World, self, t, obj) != EditReply.Accepted)
                {
                    continue;
                }

                byte floor = World.World.GetFloor(t);
                World.Predict(id, i, t, new TileEdit(0, floor, obj, BuildRecord.OfPlayer((ushort)EntityId, floor)));
            }

            SendRequest(new TileRequestMessage
            {
                RequestId = id, Op = TileOp.Place, Frame = _input.LastFrame + 1, From = Torus.Wrap(World.Shape, from), To = Torus.Wrap(World.Shape, to), Obj = obj,
            });
            return id;
        }

        private ushort NextRequest()
        {
            ushort id = _nextRequest++;
            if (_nextRequest == 0)
            {
                _nextRequest = 1;
            }

            return id;
        }

        private void SendRequest(in TileRequestMessage m)
        {
            int len = m.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            _transport.Flush();
            EditRequestsSent++;
        }

        /// <summary>Кадр ввода ушёл (<see cref="SendFrame"/>): зажатое копание копит прогресс; докопали — предсказание.</summary>
        private void OnFrameSent(InputFrame frame)
        {
            if (!_digActive || _digPredicted || !InputButtons.DigHeld(frame) || World == null)
            {
                return;
            }

            if (++DigProgress < EditRules.DigFrames)
            {
                return;
            }

            _digPredicted = true;
            if (World.World.GetObject(_digTile) != _digObj || _digObj == ObjectId.Empty)
            {
                return; // тайл уже не тот (пачка сервера) — предсказывать нечего, ответ придёт «пусто»
            }

            World.Predict(_digRequest, 0, _digTile, new TileEdit(0, World.World.GetFloor(_digTile), ObjectId.Empty));
        }

        private void OnTileReply(in TileReplyMessage m)
        {
            EditRepliesReceived++;
            if (_digActive && m.RequestId == _digRequest)
            {
                _digActive = false; // отказ до конца копания или ответ на докопанное — копание кончилось
            }

            World?.Resolve(m.RequestId, m.Mask, m.Reply == EditReply.Accepted ? m.FirstRefusal : m.Reply);
            EditReplied?.Invoke(m);
        }
    }
}
