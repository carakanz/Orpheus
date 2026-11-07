using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>Уровень строки лога сети.</summary>
    public enum NetLogLevel : byte
    {
        Info,
        Warn,
        Error,
    }

    /// <summary>
    /// Сервер по сети над тактом (docs/07, 2.4, 2.10, 4.3): приём соединений, вход со сверкой версии, сущность игрока,
    /// пакеты ввода → ввод сущности такта, полный снимок каждый такт (30 Гц), часы.
    /// <para>
    /// Порядок в цикле сервера: <see cref="Poll"/> (до такта: события транспорта, вход, ввод) → <c>Simulation.Step</c>
    /// → <see cref="SendSnapshots"/> (после такта). Всё — в потоке такта; времени внутри нет.
    /// </para>
    /// <para>
    /// Ввод без опережения (время персонажа — 2.7 — отложено, `08` 3.0): в такте действует новейший пришедший кадр;
    /// пакет со старыми кадрами (переставленный или дубль) отбрасывается. Четыре кадра в пакете пока только
    /// подстраховка от потерь.
    /// </para>
    /// <para>
    /// Обрыв (2.13 п. 6, 0-015): нового кадра нет <see cref="InputStaleTicks"/> тактов (0,2 с) — сервер подставляет пустой
    /// ввод, персонаж стоит; соединение держится до обычного тайм-аута транспорта.
    /// </para>
    /// </summary>
    public sealed class NetServer : ITransportHandler
    {
        /// <summary>Окно интереса: ±половина окна чанков клиента 6 × 4 (3.5) — ±96 × ±64 тайла.</summary>
        public const int InterestHalfWidthUnits = ChunkWindow.Width * Torus.ChunkSize / 2 * Torus.SubTilesPerTile;

        /// <inheritdoc cref="InterestHalfWidthUnits"/>
        public const int InterestHalfHeightUnits = ChunkWindow.Height * Torus.ChunkSize / 2 * Torus.SubTilesPerTile;

        private enum PlayerState : byte
        {
            AwaitingLogin,
            InGame,
        }

        private sealed class Player
        {
            public int Peer;
            public PlayerState State;
            public int EntityId;
            public string Name = string.Empty;
            public uint LastFrame;
            public bool HasFrame;
            public uint LastSnapshotAck;

            /// <summary>Такт сервера, на котором пришёл последний новый кадр ввода (или вход).</summary>
            public long LastInputTick;

            /// <summary>Ввод подменён пустым по обрыву; снимается новым кадром.</summary>
            public bool Stopped;

            /// <summary>Кольцо отправленных снимков для дельт (0-014); создаётся при входе в игру.</summary>
            public SentRing? Ring;
        }

        /// <summary>
        /// Кольцо «что клиент имеет»: состояние (сущности), отправленное игроку на такте tick.
        /// Дельта строится от записи с тактом = подтверждение клиента (<c>LastSnapshot</c> ввода).
        /// </summary>
        private sealed class SentRing
        {
            public const int Depth = 32; // 1 с снимков 30 Гц: клиент, подтверждая, всегда внутри

            public readonly uint[] Tick = new uint[Depth];
            public readonly bool[] Valid = new bool[Depth];
            public readonly int[] Count = new int[Depth];
            public readonly SnapshotEntity[][] Ents = new SnapshotEntity[Depth][];
            public int Head;

            public SentRing()
            {
                for (int i = 0; i < Depth; i++)
                {
                    Ents[i] = new SnapshotEntity[Snapshot.MaxEntities];
                }
            }

            /// <summary>Индекс записи с тактом <paramref name="tick"/>; нет (клиент подтвердил старое) — −1.</summary>
            public int Find(uint tick)
            {
                for (int i = 0; i < Depth; i++)
                {
                    if (Valid[i] && Tick[i] == tick)
                    {
                        return i;
                    }
                }

                return -1;
            }

            public void Push(uint tick, SnapshotEntity[] src, int count)
            {
                Tick[Head] = tick;
                Valid[Head] = true;
                Count[Head] = count;
                Array.Copy(src, Ents[Head], count);
                Head = (Head + 1) % Depth;
            }
        }

        private readonly ITransport _transport;
        private readonly Simulation _sim;
        private readonly Func<int, WorldPos> _spawn;
        private readonly int _maxPlayers;
        private readonly NetVersion _version;
        private readonly SnapshotLayout _layout;
        private readonly List<Player> _players = new List<Player>();
        private readonly byte[] _buf = new byte[NetSettings.MaxUnreliablePayload];
        private readonly SnapshotEntity[] _cur = new SnapshotEntity[Snapshot.MaxEntities];
        private readonly SnapshotEntity[] _merged = new SnapshotEntity[Snapshot.MaxEntities];
        private readonly SnapshotDeltaMessage _delta = new SnapshotDeltaMessage();
        private readonly bool[] _matched = new bool[Snapshot.MaxEntities];
        private readonly int[] _removedIdx = new int[Snapshot.MaxEntities];
        private readonly int[] _updBase = new int[Snapshot.MaxEntities];
        private readonly int[] _updCur = new int[Snapshot.MaxEntities];
        private readonly DeltaFields[] _updFields = new DeltaFields[Snapshot.MaxEntities];
        private readonly int[] _addCur = new int[Snapshot.MaxEntities];
        private int _spawned;

        /// <summary>
        /// Нового кадра ввода нет столько тактов (6 = 0,2 с, 2.13 п. 6) — ввод игрока пустой: движение 0, кнопки 0.
        /// </summary>
        public const int InputStaleTicks = 6;

        /// <summary>
        /// Контрольный полный кадр — раз в столько тактов (30 = 1 с; 0-014, 4.3). 1 — всегда полный (эталон для тестов).
        /// </summary>
        public uint KeyframePeriod { get; set; } = 30;

        /// <param name="transport">Транспорт сервера (слушает).</param>
        /// <param name="sim">Такт; сущности игроков добавляются и убираются вне такта.</param>
        /// <param name="spawn">Точка появления игрока № n (по счёту входов).</param>
        /// <param name="maxPlayers">Мест.</param>
        /// <param name="version">Версия сборки сервера (тесты подставляют свою).</param>
        public NetServer(ITransport transport, Simulation sim, Func<int, WorldPos> spawn, int maxPlayers, NetVersion version)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
            _maxPlayers = maxPlayers;
            _version = version;
            _layout = new SnapshotLayout(sim.Shape);
        }

        /// <summary>Лог: уровень, категория (<c>net</c>), текст.</summary>
        public Action<NetLogLevel, string>? Log { get; set; }

        /// <summary>Игроков в игре.</summary>
        public int PlayersInGame
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _players.Count; i++)
                {
                    if (_players[i].State == PlayerState.InGame)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>Снимков отправлено (полных и дельт).</summary>
        public long SnapshotsSent { get; private set; }

        /// <summary>Байт снимков отправлено (полных и дельт).</summary>
        public long SnapshotBytes { get; private set; }

        /// <summary>Наибольший полный снимок, байт (дельты — <see cref="MaxDeltaBytes"/>).</summary>
        public int MaxSnapshotBytes { get; private set; }

        /// <summary>Снимков, обрезанных по бюджету 1200 байт (4.3: ошибка в лог).</summary>
        public long SnapshotsTruncated { get; private set; }

        /// <summary>Дельт отправлено / байт (0-014).</summary>
        public long DeltasSent { get; private set; }

        /// <inheritdoc cref="DeltasSent"/>
        public long DeltaBytes { get; private set; }

        /// <summary>Полных снимков отправлено (контрольные, первый, база потеряна, дельта не меньше полного).</summary>
        public long FullSent { get; private set; }

        /// <summary>Наибольшая дельта, байт.</summary>
        public int MaxDeltaBytes { get; private set; }

        /// <summary>Пакетов ввода принято / отброшено (старые, испорченные).</summary>
        public long InputsAccepted { get; private set; }

        /// <inheritdoc cref="InputsAccepted"/>
        public long InputsDropped { get; private set; }

        /// <summary>Сколько раз ввод игрока подменён пустым: нового кадра нет <see cref="InputStaleTicks"/> тактов.</summary>
        public long InputStops { get; private set; }

        /// <summary>Входов принято / отклонено.</summary>
        public int LoginsAccepted { get; private set; }

        /// <inheritdoc cref="LoginsAccepted"/>
        public int LoginsRejected { get; private set; }

        /// <summary>Отключений игроков в игре (любая причина).</summary>
        public int Disconnects { get; private set; }

        /// <summary>Номер сущности игрока на соединении <paramref name="peer"/>; не в игре — −1.</summary>
        public int EntityOf(int peer)
        {
            Player? p = Find(peer);
            return p != null && p.State == PlayerState.InGame ? p.EntityId : -1;
        }

        /// <summary>
        /// Последний принятый кадр ввода игрока с сущностью <paramref name="entityId"/> (идёт в снимок как
        /// <c>AckFrame</c>); нет такого игрока или кадров ещё не было — false.
        /// </summary>
        public bool TryGetLastFrame(int entityId, out uint frame)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId && p.HasFrame)
                {
                    frame = p.LastFrame;
                    return true;
                }
            }

            frame = 0;
            return false;
        }

        /// <summary>До такта: события транспорта, вход, ввод; затем стоп тем, от кого ввода нет 0,2 с.</summary>
        public void Poll(long nowMicros)
        {
            _transport.Poll(nowMicros, this);
            StopStaleInputs();
        }

        /// <summary>
        /// 2.13 п. 6: от игрока нет нового кадра <see cref="InputStaleTicks"/> тактов — пустой ввод на ближайший такт.
        /// Зовётся после приёма пакетов такта: если сервер сам стоял (заморозка, п. 5), пакеты, пришедшие за это
        /// время, уже приняты и стопа не будет.
        /// </summary>
        private void StopStaleInputs()
        {
            long tick = _sim.Tick;
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State != PlayerState.InGame || p.Stopped || tick - p.LastInputTick < InputStaleTicks)
                {
                    continue;
                }

                p.Stopped = true;
                InputStops++;
                _sim.SetInput(p.EntityId, InputFrame.None);
            }
        }

        /// <summary>
        /// После такта: снимок каждому игроку в игре (0-014, 4.3). База — снимок, который клиент подтвердил
        /// (<c>LastSnapshot</c> ввода) и который ещё лежит в кольце «что клиент имеет» (32 такта). Есть база —
        /// дельта к ней; нет (вход, потеря больше секунды) — полный. Плюс контрольный полный раз в
        /// <see cref="KeyframePeriod"/> тактов и полный вместо дельты, если дельта не меньше полного (все вокруг бегут).
        /// </summary>
        public void SendSnapshots()
        {
            ReadOnlySpan<SimEntity> entities = _sim.Entities;
            uint tick = (uint)_sim.Tick;
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State != PlayerState.InGame)
                {
                    continue;
                }

                int selfSlot = _sim.SlotOf(pl.EntityId);
                if (selfSlot < 0)
                {
                    continue;
                }

                int curCount = BuildView(entities, selfSlot, _cur);
                SentRing ring = pl.Ring ??= new SentRing();
                int bi = ring.Find(pl.LastSnapshotAck);
                bool keyframe = KeyframePeriod <= 1 || tick % KeyframePeriod == 0;
                uint age = tick - pl.LastSnapshotAck;

                int len = -1;
                bool truncated = false;
                if (bi >= 0 && !keyframe && age >= 1 && age <= SnapshotDeltaMessage.MaxBaselineAge)
                {
                    len = TryWriteDelta(tick, pl, ring.Ents[bi], ring.Count[bi], curCount, out truncated, out int mergedCount);
                    if (len > 0)
                    {
                        ring.Push(tick, _merged, mergedCount);
                        DeltasSent++;
                        DeltaBytes += len;
                        if (len > MaxDeltaBytes)
                        {
                            MaxDeltaBytes = len;
                        }
                    }
                }

                if (len < 0)
                {
                    len = WriteFull(tick, pl, curCount, out int written);
                    truncated = written < curCount;
                    ring.Push(tick, _cur, written); // у клиента будет ровно то, что влезло
                    FullSent++;
                    if (len > MaxSnapshotBytes)
                    {
                        MaxSnapshotBytes = len;
                    }
                }

                if (truncated)
                {
                    SnapshotsTruncated++;
                    if (SnapshotsTruncated == 1 || SnapshotsTruncated % 300 == 0)
                    {
                        Log?.Invoke(NetLogLevel.Error, $"снимок для #{pl.EntityId} больше {NetSettings.SnapshotBudget} байт: обрезан (всего обрезано {SnapshotsTruncated})");
                    }
                }

                _transport.Send(pl.Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                SnapshotsSent++;
                SnapshotBytes += len;
            }
        }

        /// <summary>
        /// Дельта к базе в <see cref="_buf"/>; состояние клиента после неё — в <see cref="_merged"/>. Дельта не меньше
        /// полного — −1 (вызывающий шлёт полный). Сервер читает свою же дельту и склеивает её той же
        /// <see cref="SnapshotDeltaMessage.ApplyTo"/>, что и клиент, — номера сущностей в базе совпадают по построению.
        /// </summary>
        private int TryWriteDelta(uint tick, Player pl, SnapshotEntity[] baseEnts, int baseCount, int curCount,
                                  out bool truncated, out int mergedCount)
        {
            truncated = false;
            mergedCount = 0;
            Array.Clear(_matched, 0, curCount);
            int removedN = 0;
            int updatedN = 0;
            int bits = SnapshotDeltaMessage.HeaderBytes * 8;
            for (int j = 0; j < baseCount; j++)
            {
                SnapshotEntity b = baseEnts[j];
                int ci = IndexIn(_cur, curCount, b.Id);
                if (ci < 0 || _cur[ci].Kind != b.Kind)
                {
                    _removedIdx[removedN++] = j; // ушла из обзора или сменила вид (тогда придёт новой)
                    bits += SnapshotDeltaMessage.IndexBits;
                    continue;
                }

                _matched[ci] = true;
                DeltaFields f = DeltaFields.None;
                if (_cur[ci].Position != b.Position)
                {
                    f |= DeltaFields.Position;
                }

                if (_cur[ci].Direction != b.Direction)
                {
                    f |= DeltaFields.Direction;
                }

                if (f != DeltaFields.None)
                {
                    _updBase[updatedN] = j;
                    _updCur[updatedN] = ci;
                    _updFields[updatedN] = f;
                    updatedN++;
                    bits += SnapshotDeltaMessage.UpdatedBits(f, _layout);
                }
            }

            int addedN = 0;
            for (int ci = 0; ci < curCount; ci++)
            {
                if (!_matched[ci])
                {
                    _addCur[addedN++] = ci;
                    bits += _layout.EntityBits;
                }
            }

            if ((bits + 7) / 8 >= _layout.SizeFor(curCount))
            {
                return -1;
            }

            // Бюджет: исчезнувшие (≤ 255 байт) влезают всегда; дальше — изменения (своя сущность — номер 0, первой),
            // затем новые. Не влезло — дельта обрезана, остальное дошлёт следующая.
            var w = new SnapshotDeltaWriter(_buf, _layout, tick, pl.LastFrame, pl.LastSnapshotAck);
            for (int i = 0; i < removedN; i++)
            {
                w.TryAddRemoved(_removedIdx[i]);
            }

            for (int i = 0; i < updatedN && !truncated; i++)
            {
                SnapshotEntity e = _cur[_updCur[i]];
                truncated = !w.TryAddUpdated(_updBase[i], _updFields[i], e.Position, e.Direction);
            }

            for (int i = 0; i < addedN && !truncated; i++)
            {
                SnapshotEntity e = _cur[_addCur[i]];
                truncated = !w.TryAddAdded(e.Id, e.Kind, e.Position, e.Direction);
            }

            int len = w.Finish();
            _delta.Read(new ReadOnlySpan<byte>(_buf, 0, len), _layout);
            mergedCount = _delta.ApplyTo(baseEnts, baseCount, _merged);
            return len;
        }

        /// <summary>Полный снимок из <see cref="_cur"/>; возвращает длину, <paramref name="written"/> — сколько влезло.</summary>
        private int WriteFull(uint tick, Player pl, int curCount, out int written)
        {
            var w = new SnapshotWriter(_buf, _layout, tick, pl.LastFrame);
            for (int i = 0; i < curCount; i++)
            {
                if (!w.TryAdd(_cur[i].Id, _cur[i].Kind, _cur[i].Position, _cur[i].Direction))
                {
                    break;
                }
            }

            written = w.Count;
            return w.Finish();
        }

        /// <summary>Интерес-окно вокруг себя (свой — первым), позиции квантованы; число сущностей.</summary>
        private int BuildView(ReadOnlySpan<SimEntity> entities, int selfSlot, SnapshotEntity[] cur)
        {
            WorldPos self = entities[selfSlot].Mover.Position;
            int n = 0;
            cur[n++] = Quantize(entities[selfSlot]);
            for (int i = 0; i < entities.Length && n < Snapshot.MaxEntities; i++)
            {
                if (i == selfSlot)
                {
                    continue;
                }

                Int2 d = Torus.Delta(_sim.Shape, self, entities[i].Mover.Position);
                if (Math.Abs(d.X) <= InterestHalfWidthUnits && Math.Abs(d.Y) <= InterestHalfHeightUnits)
                {
                    cur[n++] = Quantize(entities[i]);
                }
            }

            return n;
        }

        private static SnapshotEntity Quantize(in SimEntity e) => new SnapshotEntity
        {
            Id = e.Id,
            Kind = e.Kind,
            Position = SnapshotLayout.Quantize(e.Mover.Position),
            Direction = e.Input.DirectionCode,
        };

        private static int IndexIn(SnapshotEntity[] ents, int count, int id)
        {
            for (int i = 0; i < count; i++)
            {
                if (ents[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Часы всем в игре (2.4: после перегрузки, когда такты не догнаны).</summary>
        public void BroadcastClock()
        {
            int len = new ClockMessage { ServerTick = (uint)_sim.Tick }.Write(_buf);
            for (int p = 0; p < _players.Count; p++)
            {
                if (_players[p].State == PlayerState.InGame)
                {
                    _transport.Send(_players[p].Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                }
            }
        }

        private static bool Add(ref SnapshotWriter w, in SimEntity e) =>
            w.TryAdd(e.Id, e.Kind, e.Mover.Position, e.Input.DirectionCode);

        // ---------- События транспорта ----------

        void ITransportHandler.OnConnected(int peer)
        {
            if (Find(peer) == null)
            {
                _players.Add(new Player { Peer = peer, State = PlayerState.AwaitingLogin });
            }
        }

        void ITransportHandler.OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
        {
            Player? p = Find(peer);
            if (p == null)
            {
                return;
            }

            _players.Remove(p);
            if (p.State == PlayerState.InGame)
            {
                Disconnects++;
                _sim.RemoveEntity(p.EntityId);
                Log?.Invoke(reason == NetDisconnectReason.Timeout ? NetLogLevel.Warn : NetLogLevel.Info,
                    $"игрок «{p.Name}» (#{p.EntityId}) ушёл: {reason}");
            }
        }

        void ITransportHandler.OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            Player? p = Find(peer);
            if (p == null)
            {
                return;
            }

            try
            {
                MessageType type = Codec.TypeOf(data);
                if (p.State == PlayerState.AwaitingLogin)
                {
                    if (type != MessageType.Login || channel != NetChannel.Facts)
                    {
                        if (type == MessageType.Input)
                        {
                            return; // ввод мог обогнать вход по ненадёжному каналу
                        }

                        Reject(p, LoginRejectReason.BadLogin, $"первым пришло {type} по каналу {channel}");
                        return;
                    }

                    OnLogin(p, LoginMessage.Read(data));
                    return;
                }

                if (type == MessageType.Input && channel == NetChannel.Unreliable)
                {
                    OnInput(p, InputMessage.Read(data));
                    return;
                }

                InputsDropped++;
            }
            catch (ProtocolException e)
            {
                if (p.State == PlayerState.AwaitingLogin)
                {
                    Reject(p, LoginRejectReason.BadLogin, e.Message);
                }
                else
                {
                    InputsDropped++;
                }
            }
        }

        private void OnLogin(Player p, LoginMessage m)
        {
            if (m.Version != _version)
            {
                Reject(p, LoginRejectReason.VersionMismatch, $"версия клиента {m.Version}, сервера {_version}");
                return;
            }

            if (PlayersInGame >= _maxPlayers)
            {
                Reject(p, LoginRejectReason.ServerFull, $"мест {_maxPlayers}");
                return;
            }

            int id;
            try
            {
                id = _sim.AddEntity(_spawn(_spawned));
            }
            catch (InvalidOperationException)
            {
                Reject(p, LoginRejectReason.ServerFull, "нет места для сущности");
                return;
            }

            _spawned++;
            p.State = PlayerState.InGame;
            p.EntityId = id;
            p.Name = m.Name;
            p.LastInputTick = _sim.Tick;
            LoginsAccepted++;
            int len = new LoginAcceptMessage
            {
                EntityId = id,
                ServerTick = (uint)_sim.Tick,
                MapWidth = _sim.Shape.Width,
                MapHeight = _sim.Shape.Height,
                Wraps = _sim.Shape.Wraps,
                WorldSeed = _sim.World.Seed,
                TickRate = Simulation.TickRate,
            }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            Log?.Invoke(NetLogLevel.Info, $"вход: «{m.Name}» → #{id}, соединение {p.Peer}");
        }

        private void OnInput(Player p, in InputMessage m)
        {
            if (p.HasFrame && (int)(m.LastFrame - p.LastFrame) <= 0)
            {
                InputsDropped++; // старый, дубль или переставленный — уже есть новее
                return;
            }

            InputFrame frame = m.Frame3.ToInputFrame();
            p.LastFrame = m.LastFrame;
            p.HasFrame = true;
            p.LastSnapshotAck = m.LastSnapshot;
            p.LastInputTick = _sim.Tick;
            p.Stopped = false;
            _sim.SetInput(p.EntityId, frame);
            InputsAccepted++;
        }

        private void Reject(Player p, LoginRejectReason reason, string why)
        {
            LoginsRejected++;
            _players.Remove(p);
            int len = new LoginRejectMessage { Reason = reason, ServerVersion = _version }.Write(_buf);
            Log?.Invoke(NetLogLevel.Warn, $"вход отклонён ({reason}): {why}; соединение {p.Peer}");
            _transport.Disconnect(p.Peer, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        private Player? Find(int peer)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].Peer == peer)
                {
                    return _players[i];
                }
            }

            return null;
        }
    }
}
