using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Предсказанные правки клиента (docs/07, 3.7 «Ответ»; `05` 1.5; 0-028): своя правка ложится на тайл чанка сразу, до ответа
    /// сервера, а копия сервера этого тайла хранится рядом.
    /// <list type="bullet">
    /// <item><b>«Принято»</b> — предсказание остаётся: пачка сервера с тем же тайлом придёт (или уже пришла) и совпадёт.</item>
    /// <item><b>«Отказ»</b> — откат: тайл = копия сервера; <see cref="RolledBack"/> — красная вспышка с причиной.</item>
    /// <item><b>Пачка сервера с предсказанным тайлом</b> — сервер главнее: тайл = пачка, предсказание «улажено» (копия сервера уже
    /// в тайле; отказ потом только покажет вспышку). Пачка без этого тайла — предсказание остаётся поверх чанка.</item>
    /// <item><b>Чанк целиком</b> — копия сервера тайла берётся из нового чанка, неулаженное предсказание кладётся снова.</item>
    /// <item><b>Чанк выброшен из окна</b> — его предсказания снимаются.</item>
    /// </list>
    /// Ревизия чанка под предсказанием — серверная: пачки ложатся по обычному правилу «ровно предыдущая».
    /// </summary>
    public sealed partial class ClientWorld
    {
        private struct Prediction
        {
            public ushort Request;
            public byte LineIndex;
            public TileCoord Tile;
            public ChunkCoord Chunk;
            public TileEdit Server;
            public TileEdit Mine;
            public bool Settled;
        }

        private readonly List<Prediction> _predictions = new List<Prediction>();

        /// <summary>Откат предсказанной правки: тайл и причина (красная вспышка, `05` 1.5).</summary>
        public Action<TileCoord, EditReply>? RolledBack { get; set; }

        /// <summary>Предсказаний, ждущих ответа.</summary>
        public int PendingPredictions => _predictions.Count;

        /// <summary>Предсказано тайлов / подтверждено / откачено / улажено пачкой сервера раньше ответа.</summary>
        public long Predicted { get; private set; }

        /// <inheritdoc cref="Predicted"/>
        public long PredictionsConfirmed { get; private set; }

        /// <inheritdoc cref="Predicted"/>
        public long PredictionsRolledBack { get; private set; }

        /// <inheritdoc cref="Predicted"/>
        public long PredictionsSettledByPatch { get; private set; }

        /// <summary>
        /// Предсказать свою правку тайла (запрос <paramref name="request"/>, место в линии <paramref name="lineIndex"/>): тайл чанка
        /// становится <paramref name="mine"/> (индекс — любой, ставится по тайлу). Чанка нет — false (не предсказано).
        /// </summary>
        public bool Predict(ushort request, int lineIndex, TileCoord tile, TileEdit mine)
        {
            TileCoord t = Torus.Wrap(Shape, tile);
            ChunkCoord c = Torus.ChunkOf(Shape, t);
            Chunk? chunk = World.FindChunk(c);
            if (chunk == null || _awaiting.Contains(c))
            {
                return false;
            }

            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            mine = mine.HasRecord ? new TileEdit(index, mine.Floor, mine.Obj, mine.Record) : new TileEdit(index, mine.Floor, mine.Obj);
            TileEdit server = chunk.GetTileEdit(index);
            for (int i = 0; i < _predictions.Count; i++)
            {
                if (_predictions[i].Tile == t && !_predictions[i].Settled)
                {
                    server = _predictions[i].Server; // под старым предсказанием — та же копия сервера
                    break;
                }
            }

            _predictions.Add(new Prediction
            {
                Request = request,
                LineIndex = (byte)lineIndex,
                Tile = t,
                Chunk = c,
                Server = server,
                Mine = mine,
            });
            chunk.SetLocalTile(mine);
            Predicted++;
            ChunkChanged?.Invoke(c);
            return true;
        }

        /// <summary>
        /// Ответ сервера на запрос <paramref name="request"/>: тайлы линии с битом в <paramref name="mask"/> — приняты (предсказание
        /// остаётся), остальные — откат к копии сервера с причиной <paramref name="reason"/>.
        /// </summary>
        public void Resolve(ushort request, uint mask, EditReply reason)
        {
            for (int i = 0; i < _predictions.Count; i++)
            {
                Prediction p = _predictions[i];
                if (p.Request != request)
                {
                    continue;
                }

                _predictions.RemoveAt(i--);
                if ((mask & (1u << p.LineIndex)) != 0)
                {
                    PredictionsConfirmed++;
                    continue;
                }

                PredictionsRolledBack++;
                if (!p.Settled)
                {
                    Chunk? chunk = World.FindChunk(p.Chunk);
                    if (chunk != null)
                    {
                        chunk.SetLocalTile(LatestFor(p.Tile, p.Server));
                        ChunkChanged?.Invoke(p.Chunk);
                    }
                }

                RolledBack?.Invoke(p.Tile, reason);
            }
        }

        // Под откаченным может лежать более позднее предсказание того же тайла — тогда тайл = оно, иначе копия сервера.
        private TileEdit LatestFor(TileCoord tile, TileEdit server)
        {
            for (int i = _predictions.Count - 1; i >= 0; i--)
            {
                if (_predictions[i].Tile == tile && !_predictions[i].Settled)
                {
                    return _predictions[i].Mine;
                }
            }

            return server;
        }

        /// <summary>После пачки сервера по чанку: предсказанные тайлы из пачки — улажены (сервер главнее).</summary>
        private void OnPatchApplied(in TilePatch patch)
        {
            if (_predictions.Count == 0)
            {
                return;
            }

            TileEdit[] edits = patch.Edits;
            for (int i = 0; i < _predictions.Count; i++)
            {
                Prediction p = _predictions[i];
                if (p.Chunk != patch.Chunk || p.Settled)
                {
                    continue;
                }

                int index = Chunk.IndexOf(p.Tile.X, p.Tile.Y);
                for (int k = 0; k < edits.Length; k++)
                {
                    if (edits[k].Index == index)
                    {
                        p.Server = edits[k];
                        p.Settled = true;
                        _predictions[i] = p;
                        PredictionsSettledByPatch++;
                        break;
                    }
                }
            }
        }

        /// <summary>После чанка целиком: копия сервера — из нового чанка, неулаженные предсказания — снова поверх.</summary>
        private void OnChunkReplaced(ChunkCoord c)
        {
            if (_predictions.Count == 0)
            {
                return;
            }

            Chunk chunk = World.FindChunk(c)!;
            for (int i = 0; i < _predictions.Count; i++)
            {
                Prediction p = _predictions[i];
                if (p.Chunk != c || p.Settled)
                {
                    continue;
                }

                p.Server = chunk.GetTileEdit(p.Mine.Index);
                _predictions[i] = p;
            }

            for (int i = 0; i < _predictions.Count; i++)
            {
                if (_predictions[i].Chunk == c && !_predictions[i].Settled)
                {
                    chunk.SetLocalTile(_predictions[i].Mine);
                }
            }
        }

        private void DropPredictions(ChunkCoord c)
        {
            for (int i = _predictions.Count - 1; i >= 0; i--)
            {
                if (_predictions[i].Chunk == c)
                {
                    _predictions.RemoveAt(i);
                }
            }
        }

        /// <summary>Предсказанный и ещё не подтверждённый тайл (для проверок «тайл клиента = сервер»).</summary>
        public bool IsPredicted(TileCoord tile)
        {
            TileCoord t = Torus.Wrap(Shape, tile);
            for (int i = 0; i < _predictions.Count; i++)
            {
                if (_predictions[i].Tile == t)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
