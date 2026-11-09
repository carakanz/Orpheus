using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Правки тайлов от клиентов (docs/07, 3.7; 0-028): запросы «копать: начало / конец» и «поставить линию» по каналу 2,
    /// проверки сервера вехи 0, прогресс копания по кадрам ввода, ответ «принято» / «отказ: причина» каждому запросу.
    /// <list type="bullet">
    /// <item><b>Копать.</b> Начало — предмет в руках: кирка (1-002, без неё запрос молча игнорируется), затем проверка
    /// «что копать» (время по <see cref="DigTimes"/>, 0 — «нельзя») и досягаемости
    /// «персонаж + инструмент» (<see cref="EditRules.CheckDig"/>); прогресс копится по кадрам ввода, шагнутым сервером, с зажатым
    /// <see cref="InputButtons.Dig"/> (время сервера, а не клиента). Докопали (<see cref="DigTimes.FramesOf"/> объекта, взятого в
    /// начале) — ещё раз досягаемость из позиции перед тактом и «тайл тот же, что в начале»
    /// (иначе «пусто»), затем правка в такт (<c>Simulation.RequestPlayerEdit</c>). Новое начало до конца — прежнему «прервано»;
    /// конец (кнопку отпустили) — «прервано».</item>
    /// <item><b>Поставить.</b> Линия до 32 тайлов (<see cref="EditRules.Line"/>), каждый тайл — радиус 6, тайл свободен, не жидкость,
    /// враг-манекен; предела «тайлов в секунду» нет (слово Автора 08.11, 0-034). Существа и враги-сущности проверяет такт
    /// после движения. Тайлы по порядку; часть — отказ, остальные встают (маска ответа). Полна очередь такта или ждущих ответа
    /// линий игрока (<see cref="MaxPendingPlaces"/>) — «очередь полна».</item>
    /// <item><b>Конфликт.</b> Два запроса на тайл в один такт — побеждает пришедший первым (порядок очереди такта), второй —
    /// «занято» (3.13 п. 8).</item>
    /// <item><b>Ответ</b> — после такта, когда исход известен; отказ до такта — сразу. Ответ есть на каждый запрос: персонажа нет —
    /// «нельзя», идущее копание — «прервано».</item>
    /// </list>
    /// Инвентаря и ресурсов в вехе 0 нет — правки без них (допущение 0-028, <c>[док]</c>).
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Ждущих ответа линий у игрока не больше (за такт) — сверх отказ «очередь полна».</summary>
        public const int MaxPendingPlaces = 16;

        private const int TagDig = 1 << 21;
        private const int TagLineBits = 5;

        private sealed class EditState
        {
            public bool Digging;
            public ushort DigRequest;
            public TileCoord DigTile;
            public ushort DigObj;
            public int DigProgress;
            public int DigFrames;

            // Ждущий запрос копания (начало или конец) — действует с кадра PendingFrame (кадр запроса клиента).
            public bool HasPending;
            public TileOp PendingOp;
            public ushort PendingRequest;
            public TileCoord PendingTile;
            public uint PendingFrame;

            public readonly ushort[] PendingId = new ushort[MaxPendingPlaces];
            public readonly uint[] PendingMask = new uint[MaxPendingPlaces];
            public readonly EditReply[] PendingFirst = new EditReply[MaxPendingPlaces];
            public readonly int[] PendingLeft = new int[MaxPendingPlaces];
            public int PendingCount;
        }

        private readonly TileCoord[] _line = new TileCoord[EditRules.MaxLineTiles];

        /// <summary>Запросов правки принято к разбору (копать: начало и конец, поставить).</summary>
        public long EditRequests { get; private set; }

        /// <summary>Ответов «принято» / «отказ» отправлено.</summary>
        public long EditsAccepted { get; private set; }

        /// <inheritdoc cref="EditsAccepted"/>
        public long EditsRefused { get; private set; }

        /// <summary>Докопано (правка копания ушла в такт).</summary>
        public long DigsCompleted { get; private set; }

        /// <summary>
        /// Таблица времени копания «база × модификатор» (0-034). Правится на ходу (база, модификатор объекта); у клиентов — своя
        /// (<see cref="NetClient.DigTimes"/>), по умолчанию такая же. Идущее копание дописывает время, взятое в начале.
        /// </summary>
        public DigTimes DigTimes { get; } = new DigTimes();

        /// <summary>Байт ответов на правки.</summary>
        public long ReplyBytes { get; private set; }

        private void OnTileRequest(Player p, in TileRequestMessage m)
        {
            EditRequests++;
            EditState s = p.Edits ??= new EditState();
            int slot = _sim.SlotOf(p.EntityId);
            if (slot < 0)
            {
                // Персонажа нет (в будущем — смерть до возрождения): ответ всё равно, иначе предсказание клиента осталось бы навсегда.
                DropDig(p, s);
                if (m.Op != TileOp.DigStop)
                {
                    Reply(p, m.RequestId, EditReply.NotAllowed, EditReply.NotAllowed, 0);
                }

                return;
            }

            WorldPos pos = _sim.Entities[slot].Mover.Position;
            if (m.Op == TileOp.Place)
            {
                OnPlace(p, s, pos, m);
                return;
            }

            byte held = _sim.Entities[slot].Inv.Held;

            // Копать умеет только кирка (1-002, `06` 2.3): клиент без кирки запроса не шлёт, а чужой — сервер
            // молча игнорирует: правки нет и отказа нет («основное действие» у меча — удар, не копание).
            // DigStop проходит всегда — он и без кирки снимает ждущее начало.
            if (m.Op == TileOp.DigStart && !Items.IsDiggingTool(held))
            {
                return;
            }

            // Копание — с кадра запроса (AdvanceEdits): кадры раньше него докапывают прежнее. Ждёт один запрос; новый вытесняет
            // ждущий: вытесненное начало — «прервано», не начавшись.
            if (s.HasPending && s.PendingOp == TileOp.DigStart)
            {
                Reply(p, s.PendingRequest, EditReply.Interrupted, EditReply.Interrupted, 0);
            }

            s.HasPending = true;
            s.PendingOp = m.Op;
            s.PendingRequest = m.RequestId;
            s.PendingTile = m.From;
            s.PendingFrame = m.Frame;
            if (!p.HasApplied || (int)(m.Frame - (p.LastApplied + 1)) <= 0)
            {
                ApplyPendingDig(p, s, pos, held); // кадр запроса уже наступил — сразу
            }
        }

        /// <summary>
        /// Персонажа нет: идущее копание и ждущее начало — «прервано» (кадров для них не будет, без ответа предсказание клиента
        /// осталось бы навсегда).
        /// </summary>
        private void DropDig(Player p, EditState s)
        {
            if (s.HasPending && s.PendingOp == TileOp.DigStart)
            {
                Reply(p, s.PendingRequest, EditReply.Interrupted, EditReply.Interrupted, 0);
            }

            s.HasPending = false;
            if (s.Digging)
            {
                Reply(p, s.DigRequest, EditReply.Interrupted, EditReply.Interrupted, 0);
                s.Digging = false;
            }
        }

        /// <summary>Ждущий запрос копания вступает: конец — «прервано»; начало — прежнему «прервано», проверка, старт.</summary>
        private void ApplyPendingDig(Player p, EditState s, WorldPos pos, byte held)
        {
            s.HasPending = false;
            if (s.PendingOp == TileOp.DigStop)
            {
                if (s.Digging && s.DigRequest == s.PendingRequest)
                {
                    Reply(p, s.DigRequest, EditReply.Interrupted, EditReply.Interrupted, 0);
                    s.Digging = false;
                }

                return;
            }

            if (s.Digging)
            {
                Reply(p, s.DigRequest, EditReply.Interrupted, EditReply.Interrupted, 0);
                s.Digging = false;
            }

            EditReply check = EditRules.CheckDig(_sim.World, pos, s.PendingTile, DigTimes, held);
            if (check != EditReply.Accepted)
            {
                Reply(p, s.PendingRequest, check, check, 0);
                return;
            }

            s.Digging = true;
            s.DigRequest = s.PendingRequest;
            s.DigTile = s.PendingTile;
            s.DigObj = _sim.World.GetObject(s.PendingTile);
            s.DigFrames = DigTimes.FramesOf(s.DigObj);
            s.DigProgress = 0;
        }

        private void OnPlace(Player p, EditState s, WorldPos pos, in TileRequestMessage m)
        {
            int n = EditRules.Line(_sim.Shape, m.From, m.To, _line);
            if (n < 0 || !EditRules.IsPlaceable(m.Obj))
            {
                Reply(p, m.RequestId, EditReply.NotAllowed, EditReply.NotAllowed, 0);
                return;
            }

            if (s.PendingCount == MaxPendingPlaces)
            {
                Reply(p, m.RequestId, EditReply.QueueFull, EditReply.QueueFull, 0);
                return;
            }

            EditReply first = EditReply.Accepted;
            int queued = 0;
            for (int i = 0; i < n; i++)
            {
                TileCoord t = _line[i];
                EditReply check = EditRules.CheckPlaceTile(_sim.World, pos, t, m.Obj);
                if (check == EditReply.Accepted && _hasDummy && EditRules.NeedsClearance(m.Obj) && EditRules.EnemyTooClose(_sim.Shape, _dummy.Position, t))
                {
                    check = EditReply.EnemyNear;
                }

                int tag = (m.RequestId << TagLineBits) | i;
                byte floor = _sim.World.GetFloor(t);
                if (check == EditReply.Accepted &&
                    !_sim.RequestPlayerEdit(t, floor, m.Obj, true, BuildRecord.OfPlayer((ushort)p.EntityId, floor), p.EntityId, tag))
                {
                    check = EditReply.QueueFull; // очередь такта полна
                }

                if (check != EditReply.Accepted)
                {
                    if (first == EditReply.Accepted)
                    {
                        first = check;
                    }

                    continue;
                }

                queued++;
            }

            if (queued == 0)
            {
                Reply(p, m.RequestId, first, first, 0);
                return;
            }

            int k = s.PendingCount++;
            s.PendingId[k] = m.RequestId;
            s.PendingMask[k] = 0;
            s.PendingFirst[k] = first;
            s.PendingLeft[k] = queued;
        }

        /// <summary>
        /// План такта (из <see cref="PlanFrames"/>): прогресс копания по кадрам такта с зажатым копанием, пока в руках кирка
        /// (1-002: на мече прогресс стоит); докопали — проверки и правка в такт.
        /// </summary>
        private void AdvanceEdits(Player p, int slot, int frames)
        {
            EditState? s = p.Edits;
            if (s == null)
            {
                return;
            }

            WorldPos pos = _sim.Entities[slot].Mover.Position;
            byte held = _sim.Entities[slot].Inv.Held; // предмет в руках такта — им и копаем
            bool canDig = Items.IsDiggingTool(held);
            for (int i = 0; i < frames; i++)
            {
                if (s.HasPending && (int)(_frameNum[i] - s.PendingFrame) >= 0)
                {
                    ApplyPendingDig(p, s, pos, held);
                }

                if (canDig && s.Digging && InputButtons.DigHeld(_frameBuf[i]) && ++s.DigProgress >= s.DigFrames)
                {
                    CompleteDig(p, s, pos, held);
                }
            }
        }

        /// <summary>Докопали (по кадрам сервера): досягаемость предметом в руках, «тайл тот же», правка в такт; отказ — ответ сразу.</summary>
        private void CompleteDig(Player p, EditState s, WorldPos pos, byte held)
        {
            s.Digging = false;
            EditReply check = EditRules.CheckDig(_sim.World, pos, s.DigTile, DigTimes, held);
            if (check == EditReply.Accepted && _sim.World.GetObject(s.DigTile) != s.DigObj)
            {
                check = EditReply.Empty; // тайл не тот, что в начале копания (3.7)
            }

            if (check == EditReply.Accepted &&
                !_sim.RequestPlayerEdit(s.DigTile, _sim.World.GetFloor(s.DigTile), ObjectId.Empty, false, default, p.EntityId, TagDig | (s.DigRequest << TagLineBits)))
            {
                check = EditReply.QueueFull;
            }

            if (check != EditReply.Accepted)
            {
                Reply(p, s.DigRequest, check, check, 0);
                return;
            }

            DigsCompleted++;
        }

        /// <summary>После такта: исходы правок игроков → ответы (копание — сразу; линия — когда известны все её тайлы).</summary>
        private void SendEditReplies()
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            for (int e = 0; e < events.Length; e++)
            {
                SimEvent ev = events[e];
                if (ev.Kind != SimEventKind.TileEdit || ev.Requester == 0)
                {
                    continue;
                }

                Player? p = FindByEntity(ev.Requester);
                if (p == null)
                {
                    continue; // ушёл до конца такта
                }

                EditReply reply = EditRules.FromResult(ev.Result);
                ushort id = (ushort)((ev.Tag >> TagLineBits) & 0xFFFF);
                if ((ev.Tag & TagDig) != 0)
                {
                    Reply(p, id, reply, reply, reply == EditReply.Accepted ? 1u : 0u);
                    continue;
                }

                EditState? s = p.Edits;
                int k = s == null ? -1 : Array.IndexOf(s.PendingId, id, 0, s.PendingCount);
                if (k < 0)
                {
                    continue;
                }

                if (reply == EditReply.Accepted)
                {
                    s!.PendingMask[k] |= 1u << (ev.Tag & ((1 << TagLineBits) - 1));
                }
                else if (s!.PendingFirst[k] == EditReply.Accepted)
                {
                    s.PendingFirst[k] = reply;
                }

                if (--s.PendingLeft[k] > 0)
                {
                    continue;
                }

                uint mask = s.PendingMask[k];
                EditReply firstRefusal = s.PendingFirst[k];
                Reply(p, id, mask != 0 ? EditReply.Accepted : firstRefusal, firstRefusal, mask);
                int last = --s.PendingCount;
                s.PendingId[k] = s.PendingId[last];
                s.PendingMask[k] = s.PendingMask[last];
                s.PendingFirst[k] = s.PendingFirst[last];
                s.PendingLeft[k] = s.PendingLeft[last];
            }
        }

        private void Reply(Player p, ushort id, EditReply reply, EditReply first, uint mask)
        {
            int len = new TileReplyMessage { RequestId = id, Reply = reply, FirstRefusal = first, Mask = mask }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            ReplyBytes += len;
            if (reply == EditReply.Accepted)
            {
                EditsAccepted++;
            }
            else
            {
                EditsRefused++;
            }
        }

        private Player? FindByEntity(int entityId)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].State == PlayerState.InGame && _players[i].EntityId == entityId)
                {
                    return _players[i];
                }
            }

            return null;
        }
    }
}
