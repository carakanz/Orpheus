using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Занятые в такте ячейки верха (E2-15, замечание проверки 1). Планы постановки строятся по миру на момент заявки, а пачка
    /// чанка ловит только две правки одного тайла; мебель со сдвигом задевает соседние тайлы (и чанки). Поэтому pre-pass такта
    /// (по порядку прихода) отмечает ячейки 0,5, которые занимает каждая постановка верха: стена и забор — добавленные ячейки,
    /// дверь — её пара, мебель — 2 × 2 ячейки в задетых тайлах, прочее — тайл. Пересечение с уже отмеченными в этом такте —
    /// «занято» до списания предмета (пришедший первым побеждает, `07` 3.7). Пол — нижний слой, ячеек верха не занимает.
    /// </summary>
    public sealed partial class Simulation
    {
        // Ячейки, отмеченные в такте: ключ — тайл (Y·W + X), маска 4 бита. Сброс — в начале каждой пачки правок.
        private long[] _claimTile = new long[64];
        private byte[] _claimMask = new byte[64];
        private int _claimCount;

        private void ClearClaims() => _claimCount = 0;

        /// <summary>
        /// Отметить ячейки верха постановки № <paramref name="i"/> (до пачки). Не постановка верха — true без отметок. Ячейки уже
        /// заняты другой постановкой этого такта — false (ничего не отмечено).
        /// </summary>
        private bool ClaimCells(int i)
        {
            TileEdit edit = _edits[i].Edit;
            if (edit.Obj == ObjectId.Empty || ObjectId.IsPart(edit.Obj))
            {
                return true; // снос, копание, пол без верха
            }

            ushort obj = edit.Obj;
            ushort pre = _preObj[i];
            TileCoord tile = TileOf(_edits[i].Chunk, edit.Index);
            Span<TileCoord> tiles = stackalloc TileCoord[4];
            Span<byte> masks = stackalloc byte[4];
            int n = 1;
            tiles[0] = tile;
            if (BuildCells.IsCellBuilding(obj))
            {
                byte after = BuildCells.MaskOf(obj, edit.HasRecord, edit.Record);
                byte before = pre == obj ? BuildCells.MaskOf(pre, _preHasBuild[i], _preBuild[i]) : (byte)0;
                masks[0] = (byte)(after & ~before & BuildCells.Full);
            }
            else if (pre == obj)
            {
                return true; // верх тот же (починка, урон, пол под мебель): ячейки не меняются
            }
            else if (BuildLayers.IsFurniture(obj) && edit.HasRecord && edit.Record.HasOffset)
            {
                n = BuildLayers.Footprint(_shape, tile, edit.Record, tiles, masks);
            }
            else
            {
                masks[0] = BuildCells.Full;
            }

            for (int k = 0; k < n; k++)
            {
                if (masks[k] != 0 && (ClaimedAt(tiles[k]) & masks[k]) != 0)
                {
                    return false;
                }
            }

            for (int k = 0; k < n; k++)
            {
                if (masks[k] != 0)
                {
                    AddClaim(tiles[k], masks[k]);
                }
            }

            return true;
        }

        private long ClaimKey(TileCoord t)
        {
            TileCoord w = Torus.Wrap(_shape, t);
            return (long)w.Y * _shape.Width + w.X;
        }

        private byte ClaimedAt(TileCoord t)
        {
            long key = ClaimKey(t);
            byte m = 0;
            for (int j = 0; j < _claimCount; j++)
            {
                if (_claimTile[j] == key)
                {
                    m |= _claimMask[j];
                }
            }

            return m;
        }

        private void AddClaim(TileCoord t, byte mask)
        {
            if (_claimCount == _claimTile.Length)
            {
                Array.Resize(ref _claimTile, _claimTile.Length * 2);
                Array.Resize(ref _claimMask, _claimMask.Length * 2);
            }

            _claimTile[_claimCount] = ClaimKey(t);
            _claimMask[_claimCount++] = mask;
        }
    }
}
