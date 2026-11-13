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
        /// Правки такта (3.7 «Рассылка»): по событиям такта — одна пачка на изменённый чанк, каждому, у кого чанк в окне.
        /// У клиента ревизия ровно предыдущая — пачка; уже эта — ничего; иначе (без ошибки не бывает) — чанк целиком.
        /// Пачка пишется из содержимого чанка по индексам принятых правок — <c>Simulation</c> только читается.
        /// </summary>
        private void BroadcastPatches()
        {
            ReadOnlySpan<SimEvent> events = _sim.Events;
            int n = 0;
            ChunkCoord current = default;
            for (int e = 0; e < events.Length; e++)
            {
                SimEvent ev = events[e];
                if (ev.Kind == SimEventKind.TileEdit)
                {
                    if (ev.Chunk != current)
                    {
                        n = 0; // пачка прошлого чанка без новой ревизии (все правки — отказ)
                        current = ev.Chunk;
                    }

                    if (ev.Result == EditResult.Applied)
                    {
                        _patchIdx[n++] = (ushort)ev.TileIndex;
                    }

                    continue;
                }

                if (ev.Kind != SimEventKind.ChunkRevised)
                {
                    continue;
                }

                if (n == 0 || ev.Chunk != current)
                {
                    throw new InvalidOperationException($"Такт {ev.Tick}: ревизия чанка {ev.Chunk} без принятых правок в событиях.");
                }

                int len = -1;
                Chunk chunk = _sim.World.FindChunk(ev.Chunk)!;
                for (int p = 0; p < _players.Count; p++)
                {
                    Player pl = _players[p];
                    HeldChunks? held = pl.Held;
                    int k = held == null || pl.State != PlayerState.InGame ? -1 : held.IndexOf(ev.Chunk);
                    if (k < 0 || held!.Revisions[k] == ev.Revision)
                    {
                        continue;
                    }

                    if (held.Revisions[k] != unchecked(ev.Revision - 1))
                    {
                        held.Revisions[k] = SendChunk(pl, ev.Chunk);
                        continue;
                    }

                    if (len < 0)
                    {
                        len = ChunkPatchMessage.Write(_worldBuf, ChunkWire.SurfaceMap, ev.Chunk, ev.Revision, chunk,
                            new ReadOnlySpan<ushort>(_patchIdx, 0, n));
                    }

                    _transport.Send(pl.Peer, NetChannel.World, new ReadOnlySpan<byte>(_worldBuf, 0, len));
                    held.Revisions[k] = ev.Revision;
                    PatchesSent++;
                    PatchBytes += len;
                }

                n = 0;
            }
        }

        /// <summary>Запрос чанка целиком (3.7 «Порядок»): чанк в окне клиента — свежая копия; нет — пропуск.</summary>
        private void OnChunkRequest(Player p, ChunkRequestMessage m)
        {
            ChunkRequests++;
            HeldChunks? held = p.Held;
            int k = held?.IndexOf(m.Chunk) ?? -1;
            if (k >= 0)
            {
                held!.Revisions[k] = SendChunk(p, m.Chunk);
            }
        }

        /// <summary>
        /// Чанк целиком — свежей копией (3.3, 3.6): копия сжимается, только если её ревизия устарела, — один раз на ревизию,
        /// сколько бы клиентов чанк ни получили. Возвращает отправленную ревизию.
        /// </summary>
        private uint SendChunk(Player pl, ChunkCoord c)
        {
            Chunk chunk = _sim.World.GetOrCreateChunk(c);
            if (!chunk.HasFreshCopy)
            {
                chunk.RebuildCompressed();
                ChunkCompressions++;
            }

            int len = ChunkMessage.Write(_worldBuf, ChunkWire.SurfaceMap, c, chunk.Revision, chunk.FreshCopy);
            _transport.Send(pl.Peer, NetChannel.World, new ReadOnlySpan<byte>(_worldBuf, 0, len));
            ChunksSent++;
            ChunkBytes += len;
            return chunk.Revision;
        }

        /// <summary>
        /// Чанки, которые сервер считает доставленными игроку с сущностью <paramref name="entityId"/> (набор окна, 3.5, 3.7;
        /// 0-026), — в <paramref name="into"/> (≥ <see cref="ChunkWindow.MaxChunks"/>). Возвращает число; нет игрока — −1.
        /// Для проверок «набор клиента = набор сервера».
        /// </summary>
        public int CopyHeldChunks(int entityId, Span<ChunkCoord> into)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.State == PlayerState.InGame && p.EntityId == entityId)
                {
                    HeldChunks? held = p.Held;
                    if (held == null)
                    {
                        return 0;
                    }

                    new ReadOnlySpan<ChunkCoord>(held.Coords, 0, held.Count).CopyTo(into);
                    return held.Count;
                }
            }

            return -1;
        }

        /// <summary>
        /// Окно сменилось (<see cref="BuildView"/>): чанки вне нового окна убираются из набора клиента — ему уходит центр
        /// окна (3.5: клиент выбрасывает чанки вне 8 × 6 вокруг него), затем вошедшие в окно чанки, которых у клиента нет, —
        /// свежей копией (3.7 «Вход чанка в окно»). Всё — канал 1, по порядку с пачками правок.
        /// </summary>
        private void SyncChunks(Player pl, ViewState v)
        {
            HeldChunks held = pl.Held ??= new HeldChunks();
            var window = new ReadOnlySpan<ChunkCoord>(v.Window, 0, v.WindowCount);
            if (held.RetainIn(window) > 0)
            {
                int len = new ChunkWindowMessage { Map = ChunkWire.SurfaceMap, Center = v.Center }.Write(_worldBuf);
                _transport.Send(pl.Peer, NetChannel.World, new ReadOnlySpan<byte>(_worldBuf, 0, len));
                WindowMessagesSent++;
            }

            for (int i = 0; i < window.Length; i++)
            {
                if (held.IndexOf(window[i]) < 0)
                {
                    held.Add(window[i], SendChunk(pl, window[i]));
                }
            }
        }

    }
}
