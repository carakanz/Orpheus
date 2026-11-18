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
    /// <see cref="InputButtons.Dig"/> (<see cref="SendFrame"/>) — тем же счётом, что у сервера, инструментом в руках
    /// (<see cref="Items.DigToolOf"/>, 1-010); докопали — предсказание «объект снят» (<see cref="ClientWorld.Predict"/>)
    /// и добычи в сумку (<see cref="DigLoot"/> по зерну, тайлу и объекту). <see cref="StopDig"/> до конца — «копать: конец».</item>
    /// <item><see cref="Place"/> — линия до 32 тайлов: тайлы, свободные по своему миру, предсказываются сразу.</item>
    /// <item>Ответ сервера — <see cref="ClientWorld.Resolve"/>: принятые остаются, остальные откатываются с вспышкой;
    /// отказ на скопированное копание откатывает и предсказанную добычу (1-010), подтверждение — сумка сервера (тип 26).</item>
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
        private byte _lastSlot; // ячейка быстрой панели последнего отправленного кадра (предмет в руках для предсказания)

        // Траты предсказаний постановки (E2-11): по предмету-постройке из ячейки панели на тайл линии. Отказ сервера по тайлу
        // (нет бита в маске) — предмет обратно в ячейку, если сводка сумки с тех пор не приходила (иначе в ней уже правда).
        private struct SpendEntry
        {
            public ushort Request;
            public byte LineIndex;
            public byte Item;
            public byte Cell;
            public ushort InvVersion;

            // Предметов снято за эту запись (E2-12: тайл стены — по предмету на ячейку); 0 — один (прежние записи).
            public byte Count;
        }

        private readonly System.Collections.Generic.List<SpendEntry> _spends = new System.Collections.Generic.List<SpendEntry>();

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

        /// <summary>Кадров копания насчитано своим вводом (0…<see cref="DigFrames"/>).</summary>
        public int DigProgress { get; private set; }

        /// <summary>
        /// Кадров нужно на текущее копание: время объекта по <see cref="DigTimes"/> инструментом в руках, взятое на своём
        /// кадре (0 — этим предметом не копается / неразрушим; 1-010).
        /// </summary>
        public int DigFrames { get; private set; }

        /// <summary>
        /// Таблица времени копания «база × модификатор × ярус» (0-034, 1-010) — должна совпадать с таблицей сервера
        /// (<see cref="NetServer.DigTimes"/>); по умолчанию такая же.
        /// </summary>
        public DigTimes DigTimes { get; } = new DigTimes();

        /// <summary>Предмет в руках по своему счёту: сумка сервера (тип 26) и ячейка последнего кадра (1-010).</summary>
        public byte HeldItem => Inv.SlotAt(_lastSlot);

        /// <summary>
        /// «Копать: начало» (канал 2): тайл <paramref name="tile"/>. Прежнее недокопанное копание сервер прервёт сам. Не в игре — 0.
        /// Дальше кадры ввода с <see cref="InputButtons.Dig"/> копят прогресс. Запрос уходит и с неподходящим предметом —
        /// отказ сервера («нужен топор», «нужна кирка: медная») показывается строкой (1-010).
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
            _digObj = DigKeyOf(t); // объект тайла; построенный пол и обломки — id пола (молот, 1-011)
            _digStrike = PickStrike.IsStrike(World.World, t, HeldItem); // C-028: кирка — взмах, не копание по времени
            DigFrames = NeededFrames();
            DigProgress = 0;
            SendRequest(new TileRequestMessage { RequestId = id, Op = TileOp.DigStart, Frame = _input.LastFrame + 1, From = t, To = t });
            return id;
        }

        /// <summary>«Копать: конец» — кнопку отпустили до конца копания; докопанное своим счётом не прерывается.</summary>
        public void StopDig()
        {
            if (!_digActive || _digPredicted || _digStrike) // C-028: начатый взмах доигрывается (как удар мечом)
            {
                return;
            }

            _digActive = false;
            SendRequest(new TileRequestMessage { RequestId = _digRequest, Op = TileOp.DigStop, Frame = _input.LastFrame + 1, From = _digTile, To = _digTile });
        }

        /// <summary>
        /// «Поставить» (E2-11): линия от <paramref name="from"/> до <paramref name="to"/> (до 32 тайлов), постройка
        /// <paramref name="obj"/> (ключ <see cref="BuildDefs"/>; пол — ключ пола) предметом из ячейки панели <paramref name="cell"/>.
        /// Тайлы, которые по своему миру и позиции <paramref name="self"/> проходят проверку, предсказываются сразу, по предмету
        /// из ячейки на тайл: где предметы кончились — предсказание останавливается, сервер ответит «нет ресурсов».
        /// Возвращает номер запроса; линия длиннее 32, ячейка вне панели или не в игре — 0.
        /// </summary>
        public ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell) =>
            Place(from, to, obj, self, cell, EditRules.IsFloorKey(obj) && World != null ? BuildCells.WholeTilesQuad(World.Shape, from, to) : (byte)0); // E2-15: пол — тайлы целиком

        /// <summary>
        /// «Поставить» с четвертями (E2-12, протокол 26): <paramref name="quad"/> — биты 0–1 ячейка 0,5 × 0,5 в тайле начала,
        /// биты 2–3 — в тайле конца (<see cref="TileRequestMessage.Quad"/>). Стена, забор, дверь — ячейками
        /// (<see cref="PlaceCells"/>), остальное — как <see cref="Place(TileCoord, TileCoord, ushort, WorldPos, byte)"/>.
        /// </summary>
        public ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell, byte quad) =>
            PlacePlan(from, to, obj, self, cell, (byte)(quad & 31)); // E2-16: бит 4 — из меню изготовления // E2-15: план заявки — общий с сервером (NetClient.Cells.cs)

        /// <summary>
        /// F (1-012, протокол 19): закрыть дверь на тайле <paramref name="tile"/>. Предсказание — record-only
        /// правка (биты открытости сняты, таймер обнулён) только по открытой двери; по закрытой — запрос
        /// уходит как есть: продление замка (1-028), предсказывать нечего. Радиус, замок и проём проверяет сервер
        /// (<see cref="Simulation.RequestCloseDoor"/>), отказ — откат по ответу (<see cref="ClientWorld.Resolve"/>).
        /// Возвращает номер запроса; не в игре или тайл не дверь — 0.
        /// </summary>
        public ushort CloseDoor(TileCoord tile)
        {
            if (State != NetClientState.InGame || World == null)
            {
                return 0;
            }

            TileCoord t = Torus.Wrap(World.Shape, tile);
            if (World.World.GetObject(t) != ObjectId.Door || !World.World.TryGetBuild(t, out BuildRecord rec))
            {
                return 0; // не дверь
            }

            ushort id = NextRequest();
            if (rec.IsDoorOpen)
            {
                var mine = new TileEdit(0, World.World.GetFloor(t), ObjectId.Door,
                    rec.WithoutFlags(BuildFlags.DoorOpen | BuildFlags.DoorKeepOpen).WithDebrisEndTick(0));
                World.Predict(id, 0, t, mine);
            }

            SendRequest(new TileRequestMessage
            {
                RequestId = id, Op = TileOp.CloseDoor, Frame = _input.LastFrame + 1, From = t, To = t,
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

        /// <summary>Кадр ввода ушёл (<see cref="SendFrame"/>): зажатое копание копит прогресс; докопали — предсказание мира и добычи.</summary>
        private void OnFrameSent(InputFrame frame)
        {
            _lastSlot = frame.Slot;
            if (Wear.Count > 0)
            {
                Wear.Expire(_nowUs); // C-028: трещины гаснут через 5 с без ударов (как у сервера)
            }

            if (!_digActive || _digPredicted || !InputButtons.DigHeld(frame) || World == null)
            {
                return;
            }

            DigFrames = NeededFrames(); // предмет в руках мог смениться кадром: не им — прогресс стоит (как у сервера, 1-010)
            if (DigFrames <= 0)
            {
                return; // неразрушим, тайл пуст или не тем инструментом — ответ сервера придёт «нельзя» / «пусто» / «нужен …»
            }

            if (++DigProgress < DigFrames)
            {
                return;
            }

            _digPredicted = true;
            if (_digStrike)
            {
                PredictStrike(); // C-028: удары по тайлам сектора — трещины сразу, снятие — пачкой сервера
                return;
            }

            if (DigKeyOf(_digTile) != _digObj)
            {
                return; // тайл уже не тот (пачка сервера) — предсказывать нечего, ответ придёт «пусто»
            }

            // Снос по слоям (E2-15, как у сервера): верх или обломки — пол остаётся; верха нет — построенный пол.
            BuildLayers.AfterDig(World.World, _digTile, out byte newFloor, out bool keep, out BuildRecord rest);
            World.Predict(_digRequest, 0, _digTile, keep ? new TileEdit(0, newFloor, ObjectId.Empty, rest) : new TileEdit(0, newFloor, ObjectId.Empty));

            // E2-1: возврат сноса и добыча копания падают кучами на землю (сервер пришлёт «кучи», тип 27) —
            // сумка от них не меняется, предсказывать нечего. Подбор не предсказывается: ждём сумку сервера (тип 26).
        }

        private void OnTileReply(in TileReplyMessage m)
        {
            EditRepliesReceived++;
            if (_digActive && m.RequestId == _digRequest)
            {
                _digActive = false; // отказ до конца копания или ответ на докопанное — копание кончилось
            }

            // Траты/возвраты предсказаний линии и сноса (1-011): тайл без бита в маске — предсказанное снимается;
            // сводка сумки сервера с момента предсказания не приходила (иначе в ней уже правда).
            for (int i = _spends.Count - 1; i >= 0; i--)
            {
                SpendEntry e = _spends[i];
                if (e.Request != m.RequestId)
                {
                    continue;
                }

                _spends.RemoveAt(i);
                if ((m.Mask & (1u << e.LineIndex)) != 0 || InvVersion != e.InvVersion)
                {
                    continue;
                }

                var inv = Inv;
                int units = e.Count == 0 ? 1 : e.Count;
                if (inv.CountIn(e.Cell) == 0 || inv.ItemAt(e.Cell) == e.Item)
                {
                    inv.SetCell(e.Cell, e.Item, inv.CountIn(e.Cell) + units);
                }
                else
                {
                    inv.Add(e.Item, units);
                }

                Inv = inv;
            }

            World?.Resolve(m.RequestId, m.Mask, m.Reply == EditReply.Accepted ? m.FirstRefusal : m.Reply);
            EditReplied?.Invoke(m);
        }

        /// <summary>
        /// Ключ времени копания и возврата по своему миру (1-011): объект; построенный пол — id пола, обломки —
        /// <see cref="FloorId.WoodFloor"/> (строка молота та же, 30 кадров).
        /// </summary>
        private ushort DigKeyOf(TileCoord t)
        {
            ushort obj = World!.World.GetObject(t);
            if (obj != ObjectId.Empty)
            {
                return obj;
            }

            byte floor = World.World.GetFloor(t);
            return FloorId.IsBuilt(floor) ? floor : FloorId.WoodFloor;
        }

        /// <summary>Кадров нужно на текущий объект предметом в руках (1-010); ≤ 0 — этим предметом не копается.</summary>
        private int NeededFrames() =>
            _digStrike ? (Items.DigToolOf(HeldItem) == ToolKind.Pick ? PickStrike.SwingFrames : 0)
            : DigTimes.FramesFor(_digObj, Items.DigToolOf(HeldItem), Items.TierOf(HeldItem));
    }
}
