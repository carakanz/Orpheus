using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Trig;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// P-002: partial Simulation - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class Simulation
    {
        // Сортировка слиянием снизу вверх: устойчивая (порядок прихода внутри чанка сохраняется),
        // O(n log n), без выделений — второй массив заранее. Array.Sort с IComparer выделяет делегат.
        private void SortByChunkThenArrival(int n)
        {
            EditRequest[] src = _edits;
            EditRequest[] dst = _editScratch;
            for (int width = 1; width < n; width <<= 1)
            {
                for (int lo = 0; lo < n; lo += width << 1)
                {
                    int mid = Math.Min(lo + width, n);
                    int hi = Math.Min(lo + (width << 1), n);
                    int a = lo;
                    int b = mid;
                    int k = lo;
                    while (a < mid && b < hi)
                    {
                        dst[k++] = Compare(src[b].Chunk, src[a].Chunk) < 0 ? src[b++] : src[a++];
                    }

                    while (a < mid)
                    {
                        dst[k++] = src[a++];
                    }

                    while (b < hi)
                    {
                        dst[k++] = src[b++];
                    }
                }

                EditRequest[] t = src;
                src = dst;
                dst = t;
            }

            if (!ReferenceEquals(src, _edits))
            {
                Array.Copy(src, _edits, n);
            }
        }

        private static int Compare(ChunkCoord a, ChunkCoord b) =>
            a.Y != b.Y ? (a.Y < b.Y ? -1 : 1) : a.X != b.X ? (a.X < b.X ? -1 : 1) : 0;

        private readonly struct EditRequest
        {
            public readonly ChunkCoord Chunk;
            public readonly int Arrival;
            public readonly TileEdit Edit;
            public readonly int Requester;
            public readonly int Tag;
            public readonly bool CheckCreatures;

            /// <summary>Починка (E2-5): цена — по недостающей доле прочности (<c>PreRepair</c>), не цена постановки.</summary>
            public readonly bool Repair;

            /// <summary>Ячейка сумки, из которой постановка берёт предмет-постройку (E2-11); <see cref="AnyCell"/> — любая с таким предметом.</summary>
            public readonly byte Cell;

            /// <summary>
            /// Перенос якоря мебели (C-011): вместе с этой правкой (стена в тайл-якорь) — правка <see cref="MoveEdit"/> тайла того же
            /// чанка (мебель на новом якоре). Применяются обе или ни одна (<c>ApplyEditBatches</c>).
            /// </summary>
            public readonly bool HasMove;
            public readonly TileEdit MoveEdit;

            public EditRequest(ChunkCoord chunk, int arrival, TileEdit edit, int requester, int tag, bool checkCreatures, bool repair, byte cell, in TileEdit moveEdit)
                : this(chunk, arrival, edit, requester, tag, checkCreatures, repair, cell)
            {
                HasMove = true;
                MoveEdit = moveEdit;
            }

            /// <summary>Та же заявка с другой правкой тайла (перенос якоря сохраняется).</summary>
            public EditRequest With(in TileEdit edit, byte cell) => HasMove
                ? new EditRequest(Chunk, Arrival, edit, Requester, Tag, CheckCreatures, Repair, cell, MoveEdit)
                : new EditRequest(Chunk, Arrival, edit, Requester, Tag, CheckCreatures, Repair, cell);

            public EditRequest(ChunkCoord chunk, int arrival, TileEdit edit, int requester, int tag, bool checkCreatures, bool repair = false, byte cell = AnyCell)
            {
                HasMove = false;
                MoveEdit = default;
                Cell = cell;
                Repair = repair;
                Chunk = chunk;
                Arrival = arrival;
                Edit = edit;
                Requester = requester;
                Tag = tag;
                CheckCreatures = checkCreatures;
            }
        }
    }
}
