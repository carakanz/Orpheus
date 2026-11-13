using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// P-002: partial NetServer - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>
        /// Место вернувшегося (docs/07 7.4): место выхода, если там можно стоять; иначе центр ближайшего свободного тайла
        /// в <see cref="PlaceSearchTiles"/> вокруг (по расстоянию, при равенстве — первый в обходе кольца); иначе точка появления.
        /// Данжей пока нет — «внутри недоступного данжа» не проверяется.
        /// </summary>
        private WorldPos PlaceFor(WorldPos saved, out string where)
        {
            MapShape shape = _sim.Shape;
            TileSolid solid = _sim.Solid;
            if (!Collision.TouchesSolid(shape, saved, solid))
            {
                where = "вернулся на место";
                return saved;
            }

            TileCoord t0 = Torus.TileOf(shape, saved);
            for (int r = 1; r <= PlaceSearchTiles; r++)
            {
                bool found = false;
                WorldPos best = default;
                long bestD2 = long.MaxValue;
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }

                        TileCoord t = Torus.Wrap(shape, new TileCoord(t0.X + dx, t0.Y + dy));
                        var c = new WorldPos((t.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2, (t.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);
                        if (Collision.TouchesSolid(shape, c, solid))
                        {
                            continue;
                        }

                        long d2 = Torus.Dist2(shape, saved, c);
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            best = c;
                            found = true;
                        }
                    }
                }

                if (found)
                {
                    CharactersMoved++;
                    where = $"вернулся: место занято, рядом в {r} тайл.";
                    return best;
                }
            }

            CharactersMoved++;
            where = "вернулся: рядом занято, у точки появления";
            WorldPos spawn = _spawn(_spawned);
            _spawned++;
            return spawn;
        }

        /// <summary>Сущность игрока уходит из мира (вне такта): движение — за его персонажем (0-035), сущность — из такта.</summary>
        private void LeaveWorld(int entityId)
        {
            int character = Characters.FindEntity(entityId);
            if (character >= 0)
            {
                Characters.Park(character, _sim);
            }
            else
            {
                _sim.RemoveEntity(entityId);
            }
        }

        private void OnLogin(Player p, LoginMessage m)
        {
            if (m.Version != _version)
            {
                Reject(p, LoginRejectReason.VersionMismatch, $"версия клиента {m.Version}, сервера {_version}");
                return;
            }

            bool isHost = !m.Token.IsEmpty;
            if (isHost && (HostToken.IsEmpty || m.Token != HostToken))
            {
                Reject(p, LoginRejectReason.BadToken, HostToken.IsEmpty ? "у сервера нет хозяина" : "токен хозяина не тот");
                return;
            }

            // Все отказы хозяину — до вытеснения его прежнего соединения (0-037, з. 4 проверки): отказ не должен выбивать из
            // мира его игру.
            string? nameError = LoginMessage.NameError(m.Name);
            if (nameError != null)
            {
                Reject(p, LoginRejectReason.BadName, nameError);
                return;
            }

            if (isHost)
            {
                // Имя чужого персонажа хозяину не даётся (0-037, docs/07 7.3: два персонажа мира не носят одно имя).
                int taken = Characters.FindPlayer(m.Name);
                if (taken >= 0)
                {
                    Reject(p, LoginRejectReason.NameTaken, $"имя «{m.Name}» — у персонажа игрока «{Characters[taken].Name}»");
                    return;
                }
            }

            // Место прежнего соединения хозяина освободится при вытеснении — оно в счёт не идёт.
            int replacing = isHost ? CountHostsInGame(p) : 0;
            if (PlayersInGame - replacing >= _maxPlayers)
            {
                Reject(p, LoginRejectReason.ServerFull, $"мест {_maxPlayers}");
                return;
            }

            if (isHost)
            {
                // Один токен с двух мест (7.7): новый вход вытесняет прежний — у упавшей игры хоста соединение
                // висит до тайм-аута транспорта, а вернувшийся хост входит сразу.
                for (int i = _players.Count - 1; i >= 0; i--)
                {
                    Player old = _players[i];
                    if (old != p && old.IsHost && old.State == PlayerState.InGame)
                    {
                        _players.RemoveAt(i);
                        LeaveWorld(old.EntityId);
                        int n = new ClosingMessage { Reason = ClosingReason.Replaced }.Write(_buf);
                        _transport.Disconnect(old.Peer, new ReadOnlySpan<byte>(_buf, 0, n));
                        Log?.Invoke(NetLogLevel.Warn, $"хозяин вошёл заново — прежнее соединение {old.Peer} (#{old.EntityId}) вытеснено");
                    }
                }
            }

            // Персонаж по ключу (0-035): хозяин — по токену, остальные — по имени в мире. Имя живого игрока и имя
            // персонажа хозяина другому не даются (7.3: два персонажа мира не носят одно имя).
            int character;
            if (isHost)
            {
                character = Characters.FindHost();
            }
            else
            {
                int hostAt = Characters.FindHost();
                if (hostAt >= 0 && CharacterNames.Same(Characters[hostAt].Name, m.Name))
                {
                    Reject(p, LoginRejectReason.NameTaken, $"имя «{m.Name}» — у персонажа хозяина");
                    return;
                }

                character = Characters.FindPlayer(m.Name);
                if (character >= 0 && Characters[character].EntityId > 0)
                {
                    Reject(p, LoginRejectReason.NameTaken, $"«{m.Name}» уже в мире (#{Characters[character].EntityId})");
                    return;
                }
            }

            int id;
            string where;
            try
            {
                if (character >= 0)
                {
                    Mover mover = Characters[character].Mover;
                    mover.Position = PlaceFor(mover.Position, out where);
                    id = _sim.AddEntity(mover);
                }
                else
                {
                    id = _sim.AddEntity(_spawn(_spawned));
                    _spawned++;
                    where = "новый";
                }
            }
            catch (InvalidOperationException)
            {
                Reject(p, LoginRejectReason.ServerFull, "нет места для сущности");
                return;
            }

            if (character >= 0)
            {
                _sim.SetInventory(id, Characters[character].Inv); // сумка вернувшегося — записана при уходе (1-002)
                Characters.SetInWorld(character, id, m.Name);
                CharactersReturned++;
            }
            else
            {
                _sim.SetInventory(id, Inventory.StartingKit()); // стартовый набор нового персонажа (06 0 + карточка 1-002)
                Characters.AddInWorld(isHost, m.Name, id);
            }

            p.State = PlayerState.InGame;
            p.EntityId = id;
            p.Name = m.Name;
            p.IsHost = isHost;
            p.LastInputTick = _sim.Tick;
            p.Credits = FramesPerTickLimit;
            p.ThreatsFrom = ServerFrame;
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
                GenerationHash = GenerationHash,
            }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            Log?.Invoke(NetLogLevel.Info, $"вход{(p.IsHost ? " хозяина" : "")}: «{m.Name}» → #{id} ({where}), соединение {p.Peer}");
            SendPauseTo(p); // 0-036: вошёл в паузу — сразу узнаёт, что пауза и кто поставил
            SendInventoryTo(p, _sim.GetEntity(id).Inv); // 1-002: вошёл — сразу знает свою сумку
            if (p.IsHost)
            {
                HostJoined?.Invoke();
            }

            if (character < 0)
            {
                CharacterCreated?.Invoke(m.Name);
            }
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

        private void Reject(Player p, LoginRejectReason reason, string why)
        {
            LoginsRejected++;
            _players.Remove(p);
            int len = new LoginRejectMessage { Reason = reason, ServerVersion = _version }.Write(_buf);
            Log?.Invoke(NetLogLevel.Warn, $"вход отклонён ({reason}): {why}; соединение {p.Peer}");
            _transport.Disconnect(p.Peer, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        /// <summary>«Открыть для друзей» (2.13 п. 9): только хозяин; уже открыт — тот же порт; итог — хозяину.</summary>
        private void OnOpenForFriends(Player p)
        {
            if (!p.IsHost || p.State != PlayerState.InGame)
            {
                Log?.Invoke(NetLogLevel.Warn, $"«Открыть для друзей» не от хозяина (соединение {p.Peer}) — мимо");
                return;
            }

            if (OpenedPort < 0 && OpenForFriends != null)
            {
                try
                {
                    OpenedPort = OpenForFriends();
                    Log?.Invoke(NetLogLevel.Info, $"открыто для друзей: порт {OpenedPort}");
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException || e is InvalidOperationException || e is ArgumentException)
                {
                    Log?.Invoke(NetLogLevel.Error, $"«Открыть для друзей» не вышло: {e.Message}");
                }
            }

            int len = new OpenResultMessage { Opened = OpenedPort >= 0, Port = (ushort)Math.Max(0, OpenedPort) }.Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        /// <summary>
        /// Последний шагнутый кадр ввода игрока с сущностью <paramref name="entityId"/> (идёт в снимок как
        /// <c>AckFrame</c>); нет такого игрока или кадров ещё не было — false.
        /// </summary>
        public bool TryGetLastFrame(int entityId, out uint frame)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId && p.HasApplied)
                {
                    frame = p.LastApplied;
                    return true;
                }
            }

            frame = 0;
            return false;
        }

        /// <summary>Номер сущности игрока на соединении <paramref name="peer"/>; не в игре — −1.</summary>
        public int EntityOf(int peer)
        {
            Player? p = Find(peer);
            return p != null && p.State == PlayerState.InGame ? p.EntityId : -1;
        }

        /// <summary>
        /// Закрыть мир (7.6): всем соединениям — отключение с <see cref="ClosingMessage"/> (клиент покажет «Хост вышел…»
        /// вместо «связь потеряна»). Сущности игроков остаются как есть — сервер после этого завершается.
        /// </summary>
        public void CloseAll(ClosingReason reason)
        {
            int len = new ClosingMessage { Reason = reason }.Write(_buf);
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                _transport.Disconnect(_players[i].Peer, new ReadOnlySpan<byte>(_buf, 0, len));
            }

            _players.Clear();
            _transport.Flush();
        }

        /// <summary>Соединения хозяина в мире, кроме <paramref name="except"/>, — их вытеснит новый вход хозяина.</summary>
        private int CountHostsInGame(Player except)
        {
            int n = 0;
            foreach (Player old in _players)
            {
                if (old != except && old.IsHost && old.State == PlayerState.InGame)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Кадров ввода игрока с сущностью <paramref name="entityId"/> пришло и ждёт шага (очередь); нет игрока — −1.</summary>
        public int BacklogOf(int entityId)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId)
                {
                    return p.HasNext && p.HasFrame ? Math.Max(0, (int)(p.Newest - p.NextFrame) + 1) : 0;
                }
            }

            return -1;
        }

        /// <summary>
        /// Запас кадров игрока с сущностью <paramref name="entityId"/> (0-019: +2 за такт, не больше <see cref="CreditCap"/>);
        /// нет игрока — −1. Для проверок «в паузе запас не копится» (0-036).
        /// </summary>
        public int CreditsOf(int entityId)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId)
                {
                    return p.Credits;
                }
            }

            return -1;
        }

    }
}
