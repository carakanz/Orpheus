using System;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Мир клиента — только из чанков от сервера (docs/07, 3.4: «клиент генератора не запускает и зерна не знает»;
    /// 3.5–3.7; 0-026). Без Unity: этим же пользуются боты и тесты.
    /// <list type="bullet">
    /// <item><b>Чанк</b> — копия целиком: заменяет чанк, снимает ожидание запроса.</item>
    /// <item><b>Пачка</b> — применяется, только если ревизия чанка у клиента ровно предыдущая (3.7 «Порядок»); иначе —
    /// запрос чанка целиком, и до его прихода пачки этого чанка пропускаются. Старая пачка (ревизия не новее) — пропуск.</item>
    /// <item><b>Центр окна</b> — выбросить чанки вне 8 × 6 вокруг него (<see cref="ChunkWindow.InKeep"/>, 3.5).</item>
    /// <item><b>Пачка к чанку, которого нет</b> (честный сервер такого не шлёт: значит, чанк потерян — например, сообщение
    /// «чанк» отвергнуто как испорченное) — запрос чанка целиком, а не тихий пропуск: иначе сервер считает чанк
    /// доставленным и дыра остаётся навсегда (П по 0-026, Н1).</item>
    /// <item><b>Не больше <see cref="ChunkWindow.MaxChunks"/> чанков</b>: лишний новый чанк отвергается
    /// (<see cref="ProtocolException"/>, счётчик <see cref="ChunksRejected"/>), а не доходит до экрана (П по 0-026, Н4).</item>
    /// </list>
    /// </summary>
    public sealed partial class ClientWorld
    {
        private readonly Action<ChunkCoord> _request;
        private readonly HashSet<ChunkCoord> _awaiting = new HashSet<ChunkCoord>();
        private readonly List<ChunkCoord> _drop = new List<ChunkCoord>(ChunkWindow.MaxChunks);

        /// <param name="shape">Форма карты из «вход принят».</param>
        /// <param name="request">Отправить запрос «чанк целиком» (канал 1).</param>
        public ClientWorld(MapShape shape, Action<ChunkCoord> request)
        {
            World = new WorldModel(shape);
            _request = request ?? throw new ArgumentNullException(nameof(request));
        }

        /// <summary>Чанки окна; тайлов вне них нет (пусто, как у несозданного чанка).</summary>
        public WorldModel World { get; }

        public MapShape Shape => World.Shape;

        /// <summary>Чанков сейчас.</summary>
        public int ChunkCount => World.ChunkCount;

        /// <summary>Наибольшее число чанков одновременно (3.13 п. 9: ≤ 48).</summary>
        public int MaxChunkCount { get; private set; }

        public long ChunksReceived { get; private set; }
        public long PatchesApplied { get; private set; }

        /// <summary>Пачек не к той ревизии — запрошен чанк целиком (3.7 «Порядок»).</summary>
        public long PatchGaps { get; private set; }

        /// <summary>Пачек пропущено: ждём копию по запросу или пачка старая.</summary>
        public long PatchesSkipped { get; private set; }

        /// <summary>Пачек к чанку, которого у клиента нет, — запрошен чанк целиком (Н1).</summary>
        public long PatchesToMissing { get; private set; }

        /// <summary>Чанков отвергнуто: новый чанк сверх <see cref="ChunkWindow.MaxChunks"/> (Н4).</summary>
        public long ChunksRejected { get; private set; }

        /// <summary>Запросов «чанк целиком» отправлено.</summary>
        public long Requests { get; private set; }

        /// <summary>Чанков выброшено по центру окна.</summary>
        public long ChunksDropped { get; private set; }

        /// <summary>Чанк пришёл целиком или к нему применилась пачка — перерисовать этот чанк.</summary>
        public Action<ChunkCoord>? ChunkChanged { get; set; }

        /// <summary>Чанк выброшен из окна — убрать с экрана.</summary>
        public Action<ChunkCoord>? ChunkRemoved { get; set; }

        /// <summary>
        /// Разобрать сообщение мира (канал 1). false — тип не мировой. Испорченное — <see cref="ProtocolException"/>.
        /// </summary>
        public bool Handle(MessageType type, ReadOnlySpan<byte> data)
        {
            switch (type)
            {
                case MessageType.Chunk:
                    OnChunk(ChunkMessage.Read(data, Shape));
                    return true;
                case MessageType.ChunkPatch:
                    OnPatch(ChunkPatchMessage.Read(data, Shape));
                    return true;
                case MessageType.ChunkWindow:
                    OnWindow(ChunkWindowMessage.Read(data, Shape).Center);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Тот же чанк, что у <paramref name="server"/>, побайтно (слои, записи, ревизия; 3.13 п. 6–7).</summary>
        public bool SameAs(WorldModel server, ChunkCoord c)
        {
            Chunk? mine = World.FindChunk(c);
            Chunk? theirs = server.FindChunk(c);
            return mine != null && theirs != null && mine.ContentEquals(theirs);
        }

        private void OnChunk(ChunkMessage m)
        {
            if (World.ChunkCount >= ChunkWindow.MaxChunks && World.FindChunk(m.Chunk) == null)
            {
                ChunksRejected++;
                throw new ProtocolException(
                    $"Чанк ({m.Chunk.X}, {m.Chunk.Y}): у клиента уже {World.ChunkCount} чанков, больше {ChunkWindow.MaxChunks} нельзя.");
            }

            byte[] compressed = m.Compressed.ToArray();
            byte[] blob;
            try
            {
                blob = Chunk.Decompress(compressed);
                World.GetOrCreateChunk(m.Chunk).Load(blob, compressed, m.Revision, changedSinceBase: false);
            }
            catch (InvalidDataException e)
            {
                throw new ProtocolException($"Чанк ({m.Chunk.X}, {m.Chunk.Y}): {e.Message}");
            }

            _awaiting.Remove(m.Chunk);
            OnChunkReplaced(m.Chunk); // 0-028: предсказания поверх новой копии
            ChunksReceived++;
            if (World.ChunkCount > MaxChunkCount)
            {
                MaxChunkCount = World.ChunkCount;
            }

            ChunkChanged?.Invoke(m.Chunk);
        }

        private void OnPatch(in TilePatch patch)
        {
            Chunk? chunk = World.FindChunk(patch.Chunk);
            if (chunk == null && !_awaiting.Contains(patch.Chunk) && _awaiting.Count < ChunkWindow.MaxChunks)
            {
                // Сервер считает чанк нашим (шлёт пачку), а его нет: без запроса пачки так и пропускались бы (Н1).
                PatchesToMissing++;
                _awaiting.Add(patch.Chunk);
                Requests++;
                _request(patch.Chunk);
                return;
            }

            if (chunk == null || _awaiting.Contains(patch.Chunk) || unchecked((int)(patch.Revision - chunk.Revision)) <= 0)
            {
                PatchesSkipped++;
                return;
            }

            bool applied;
            try
            {
                applied = chunk.ApplyPatch(patch);
            }
            catch (InvalidDataException e)
            {
                throw new ProtocolException(e.Message);
            }

            if (!applied)
            {
                PatchGaps++;
                _awaiting.Add(patch.Chunk);
                Requests++;
                _request(patch.Chunk);
                return;
            }

            PatchesApplied++;
            OnPatchApplied(patch); // 0-028: сервер главнее предсказания
            ChunkChanged?.Invoke(patch.Chunk);
        }

        private void OnWindow(ChunkCoord center)
        {
            _drop.Clear();
            ReadOnlySpan<ChunkCoord> held = World.GetChunkCoords();
            for (int i = 0; i < held.Length; i++)
            {
                if (!ChunkWindow.InKeep(Shape, center, held[i]))
                {
                    _drop.Add(held[i]);
                }
            }

            for (int i = 0; i < _drop.Count; i++)
            {
                World.RemoveChunk(_drop[i]);
                _awaiting.Remove(_drop[i]);
                DropPredictions(_drop[i]);
                ChunksDropped++;
                ChunkRemoved?.Invoke(_drop[i]);
            }

            // Ожидания вне 8 × 6 (запрос чанка, которого нет, — сервер его уже не пришлёт) снимаются тоже.
            // Порядок обхода не важен: снимаются все такие сразу.
            MapShape shape = Shape;
            _awaiting.RemoveWhere(c => !ChunkWindow.InKeep(shape, center, c));
        }
    }
}
