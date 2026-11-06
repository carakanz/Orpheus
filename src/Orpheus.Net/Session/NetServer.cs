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
        }

        private readonly ITransport _transport;
        private readonly Simulation _sim;
        private readonly Func<int, WorldPos> _spawn;
        private readonly int _maxPlayers;
        private readonly NetVersion _version;
        private readonly SnapshotLayout _layout;
        private readonly List<Player> _players = new List<Player>();
        private readonly byte[] _buf = new byte[NetSettings.MaxUnreliablePayload];
        private int _spawned;

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

        /// <summary>Снимков отправлено.</summary>
        public long SnapshotsSent { get; private set; }

        /// <summary>Байт снимков отправлено.</summary>
        public long SnapshotBytes { get; private set; }

        /// <summary>Наибольший снимок, байт.</summary>
        public int MaxSnapshotBytes { get; private set; }

        /// <summary>Снимков, обрезанных по бюджету 1200 байт (4.3: ошибка в лог).</summary>
        public long SnapshotsTruncated { get; private set; }

        /// <summary>Пакетов ввода принято / отброшено (старые, испорченные).</summary>
        public long InputsAccepted { get; private set; }

        /// <inheritdoc cref="InputsAccepted"/>
        public long InputsDropped { get; private set; }

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

        /// <summary>До такта: события транспорта, вход, ввод.</summary>
        public void Poll(long nowMicros) => _transport.Poll(nowMicros, this);

        /// <summary>После такта: полный снимок каждому игроку в игре.</summary>
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

                var w = new SnapshotWriter(_buf, _layout, tick, pl.LastFrame);
                WorldPos self = entities[selfSlot].Mover.Position;
                bool truncated = !Add(ref w, entities[selfSlot]);
                for (int i = 0; i < entities.Length && !truncated; i++)
                {
                    if (i == selfSlot)
                    {
                        continue;
                    }

                    Int2 d = Torus.Delta(_sim.Shape, self, entities[i].Mover.Position);
                    if (Math.Abs(d.X) <= InterestHalfWidthUnits && Math.Abs(d.Y) <= InterestHalfHeightUnits)
                    {
                        truncated = !Add(ref w, entities[i]);
                    }
                }

                int len = w.Finish();
                if (truncated)
                {
                    SnapshotsTruncated++;
                    if (SnapshotsTruncated == 1 || SnapshotsTruncated % 300 == 0)
                    {
                        Log?.Invoke(NetLogLevel.Error, $"снимок для #{pl.EntityId} больше {NetSettings.SnapshotBudget} байт: обрезан до {w.Count} сущностей (всего обрезано {SnapshotsTruncated})");
                    }
                }

                _transport.Send(pl.Peer, NetChannel.Unreliable, new ReadOnlySpan<byte>(_buf, 0, len));
                SnapshotsSent++;
                SnapshotBytes += len;
                if (len > MaxSnapshotBytes)
                {
                    MaxSnapshotBytes = len;
                }
            }
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
