using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Истечение на тайле (1-011, C-005) — для сохранения (E2-14, формат 13): когда снять обломки или флаг урона
    /// (<see cref="BuildFlags.DamagedRecent"/> — 10 с, <see cref="BuildFlags.RepairBlocked"/> — 3 с).
    /// </summary>
    public readonly struct BuildExpiry : IEquatable<BuildExpiry>
    {
        public readonly TileCoord Tile;

        /// <summary>Такт истечения.</summary>
        public readonly long EndTick;

        /// <summary>Снять обломки (иначе — флаг <see cref="Clear"/>).</summary>
        public readonly bool Debris;

        /// <summary>Какой флаг урона снять (не обломки).</summary>
        public readonly BuildFlags Clear;

        public BuildExpiry(TileCoord tile, long endTick, bool debris, BuildFlags clear)
        {
            Tile = tile;
            EndTick = endTick;
            Debris = debris;
            Clear = debris ? BuildFlags.None : clear;
        }

        public bool Equals(BuildExpiry o) => Tile == o.Tile && EndTick == o.EndTick && Debris == o.Debris && Clear == o.Clear;

        public override bool Equals(object? obj) => obj is BuildExpiry o && Equals(o);

        public override int GetHashCode() => unchecked(Tile.X * 73856093 + Tile.Y * 19349663 + (int)EndTick * 7 + (int)Clear);

        public override string ToString() => $"{Tile}: такт {EndTick}, {(Debris ? "обломки" : Clear.ToString())}";
    }

    /// <summary>
    /// E2-14 (замечание З2 проверки): очередь истечений обломков и флагов урона едет в сохранении (формат 13). Раньше она
    /// терялась — после загрузки флаг «урон 10 с» застревал навсегда (снос целой постройки давал обломки без возврата),
    /// обломки не исчезали. Старые файлы (до 13) очереди не пишут — флаги и обломки находит поиск первым тактом.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Очередь истечений по порядку (для сохранения; порядок задаёт порядок правок такта).</summary>
        public BuildExpiry[] CopyExpiries()
        {
            var a = new BuildExpiry[_expireCount];
            for (int i = 0; i < _expireCount; i++)
            {
                ExpireEntry e = _expire[i];
                a[i] = new BuildExpiry(TileOf(e.Chunk, e.Index), e.EndTick, e.Debris, e.Clear);
            }

            return a;
        }

        /// <summary>Загрузка (формат 13): очередь истечений — как была. Сверх ёмкости — как при переполнении в игре.</summary>
        public void RestoreExpiries(ReadOnlySpan<BuildExpiry> expiries)
        {
            _expireCount = 0;
            for (int i = 0; i < expiries.Length; i++)
            {
                TileCoord t = Torus.Wrap(_shape, expiries[i].Tile);
                AddExpire(expiries[i].EndTick, Torus.ChunkOf(_shape, t), (ushort)Chunk.IndexOf(t.X, t.Y), expiries[i].Debris,
                    expiries[i].Debris ? BuildFlags.DamagedRecent : expiries[i].Clear);
            }
        }

        // Старый мир (поиск первым тактом): флаги урона и обломки без очереди — снять, как будто ударили / снесли сейчас.
        private void ExpireLegacy(ChunkCoord chunk, ushort index, in BuildRecord rec)
        {
            if (rec.IsDebris)
            {
                AddExpire(Tick + BuildDefs.DebrisTicks, chunk, index, debris: true);
                return;
            }

            if ((rec.Flags & (byte)BuildFlags.DamagedRecent) != 0)
            {
                AddExpire(Tick + BuildDefs.DamageFlagTicks, chunk, index, debris: false);
            }

            if ((rec.Flags & (byte)BuildFlags.RepairBlocked) != 0)
            {
                AddExpire(Tick + BuildDefs.RepairBlockTicks, chunk, index, debris: false, BuildFlags.RepairBlocked);
            }
        }
    }
}
