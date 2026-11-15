using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Восстановление повреждённой постройки (E2-14): запись списка повреждённых. Прочность в мире — на такт <see cref="Last"/>;
    /// прибавка с тех пор — накопитель <see cref="Acc"/> (в 1/3000 максимума) плюс такты после <see cref="Last"/>.
    /// </summary>
    public readonly struct BuildHeal : IEquatable<BuildHeal>
    {
        /// <summary>Тайл записи постройки (обёрнутый).</summary>
        public readonly TileCoord Tile;

        /// <summary>Такт последнего урона.</summary>
        public readonly long Hit;

        /// <summary>Такт, по который прибавка уже в мире (до начала восстановления — такт начала: урон + 3 с).</summary>
        public readonly long Last;

        /// <summary>Такт следующей записи в мир: через 1 с после <see cref="Last"/> или раньше — когда постройка станет целой.</summary>
        public readonly long Due;

        /// <summary>Остаток накопителя, [0; <see cref="BuildDefs.HealDenominator"/>): не теряется между записями.</summary>
        public readonly int Acc;

        public BuildHeal(TileCoord tile, long hit, long last, long due, int acc)
        {
            Tile = tile;
            Hit = hit;
            Last = last;
            Due = due;
            Acc = acc;
        }

        public bool Equals(BuildHeal o) => Tile == o.Tile && Hit == o.Hit && Last == o.Last && Due == o.Due && Acc == o.Acc;

        public override bool Equals(object? obj) => obj is BuildHeal o && Equals(o);

        public override int GetHashCode() => unchecked(Tile.X * 73856093 + Tile.Y * 19349663 + (int)Last * 17 + (int)Due * 7 + Acc);

        public override string ToString() => $"{Tile}: урон {Hit}, в мире по {Last}, запись {Due}, остаток {Acc}";
    }

    /// <summary>
    /// E2-14: постройки сами восстанавливают 3 % максимальной прочности в секунду через 3 с после последнего урона. Шаг — по
    /// списку повреждённых построек (не обход мира); запись в мир — правкой тайла раз в секунду на постройку (или на такте
    /// «стала целой»), значит и патч чанка клиентам — не чаще. Список входит в хэш состояния и в сохранение (формат 13).
    /// </summary>
    public sealed partial class Simulation
    {
        private BuildHeal[] _heals = new BuildHeal[64];
        private int _healCount;
        private readonly Dictionary<long, int> _healAt = new Dictionary<long, int>();
        private bool _healScanPending;

        /// <summary>Повреждённые постройки, которые восстанавливаются или ждут 3 с после урона (по порядку списка).</summary>
        public ReadOnlySpan<BuildHeal> Heals => new ReadOnlySpan<BuildHeal>(_heals, 0, _healCount);

        /// <summary>Повреждённые постройки старого мира будут найдены первым тактом (загрузка формата до 13, E2-14).</summary>
        public bool HealScanPending => _healScanPending;

        private static long HealKey(TileCoord t) => ((long)t.X << 32) | (uint)t.Y;

        /// <summary>
        /// Прочность записи <paramref name="rec"/> с прибавкой, набежавшей после последней записи в мир (урон по постройке
        /// посреди секунды восстановления не съедает уже набранное). Остаток накопителя — в <paramref name="acc"/>.
        /// </summary>
        private int HealedIntegrity(TileCoord t, ushort obj, in BuildRecord rec, out int acc)
        {
            acc = 0;
            if (!_healAt.TryGetValue(HealKey(t), out int i))
            {
                return rec.Integrity;
            }

            acc = _heals[i].Acc;
            if (Tick <= _heals[i].Last || !BuildDefs.TryGet(obj, out BuildDef def) || rec.Integrity >= def.MaxIntegrity)
            {
                return rec.Integrity;
            }

            int gain = Gain(Tick - _heals[i].Last, def.MaxIntegrity, ref acc);
            return Math.Min(def.MaxIntegrity, rec.Integrity + gain);
        }

        // Прибавка за ticks тактов: 3 % максимума в секунду, целочисленно; остаток — в acc.
        private static int Gain(long ticks, ushort max, ref int acc)
        {
            long num = ticks * BuildDefs.HealPercentPerSecond * max + acc;
            acc = (int)(num % BuildDefs.HealDenominator);
            return (int)Math.Min(int.MaxValue, num / BuildDefs.HealDenominator);
        }

        // Такт следующей записи: через 1 с или раньше — на такте, когда прибавка дотянет до максимума.
        private static long NextDue(long last, int integrity, ushort max, int acc)
        {
            long need = (long)(max - integrity) * BuildDefs.HealDenominator - acc;
            long perTick = (long)BuildDefs.HealPercentPerSecond * max;
            long ticks = need <= 0 || perTick == 0 ? 1 : (need + perTick - 1) / perTick;
            return last + Math.Max(1, Math.Min(BuildDefs.HealStepTicks, ticks));
        }

        /// <summary>Урон по постройке принят в очередь (<see cref="DamageBuilding"/>): отсчёт 3 с заново, остаток накопителя — тот же.</summary>
        private void NoteDamage(TileCoord t, int integrity, ushort obj, int acc)
        {
            long start = Tick + BuildDefs.HealDelayTicks;
            ushort max = BuildDefs.TryGet(obj, out BuildDef def) ? def.MaxIntegrity : (ushort)0;
            var e = new BuildHeal(t, Tick, start, NextDue(start, integrity, max, acc), acc);
            if (_healAt.TryGetValue(HealKey(t), out int i))
            {
                _heals[i] = e;
                return;
            }

            AddHeal(e);
        }

        private void AddHeal(in BuildHeal e)
        {
            if (_healCount == _heals.Length)
            {
                Array.Resize(ref _heals, _heals.Length * 2);
            }

            _healAt[HealKey(e.Tile)] = _healCount;
            _heals[_healCount++] = e;
        }

        private void RemoveHeal(int i)
        {
            _healAt.Remove(HealKey(_heals[i].Tile));
            if (--_healCount != i)
            {
                _heals[i] = _heals[_healCount];
                _healAt[HealKey(_heals[i].Tile)] = i;
            }
        }

        /// <summary>
        /// Загрузка (E2-14): список из сохранения формата 13 — как был. Старые форматы списка не пишут:
        /// <paramref name="scanWorld"/> — повреждённые постройки мира находятся первым тактом (обход один раз;
        /// хэш сразу после загрузки — прежний) и ждут 3 с, как после удара.
        /// </summary>
        public void RestoreHeals(ReadOnlySpan<BuildHeal> heals, bool scanWorld)
        {
            _healCount = 0;
            _healAt.Clear();
            for (int i = 0; i < heals.Length; i++)
            {
                if (_healAt.ContainsKey(HealKey(heals[i].Tile)))
                {
                    throw new ArgumentException($"Восстановление: тайл {heals[i].Tile} дважды.", nameof(heals));
                }

                AddHeal(heals[i]);
            }

            _healScanPending = scanWorld;
        }

        // Старый мир: все повреждённые постройки — в список, флаги урона и обломки — в очередь истечений (её старые файлы
        // не пишут; З2 проверки E2-14) — один раз, первым тактом после загрузки.
        private void ScanDamaged()
        {
            _healScanPending = false;
            Span<ushort> builds = stackalloc ushort[Chunk.TileCount];
            ReadOnlySpan<ChunkCoord> keys = _world.GetChunkCoords();
            for (int c = 0; c < keys.Length; c++)
            {
                Chunk chunk = _world.FindChunk(keys[c])!;
                int n = chunk.GetBuildIndices(builds);
                for (int k = 0; k < n; k++)
                {
                    TileCoord t = TileOf(keys[c], builds[k]);
                    chunk.TryGetBuild(builds[k], out BuildRecord rec);
                    ushort obj = _world.GetObject(t);
                    ExpireLegacy(keys[c], builds[k], rec);
                    if (obj != ObjectId.Empty && !rec.IsDebris && BuildDefs.TryGet(obj, out BuildDef def)
                        && rec.Integrity < def.MaxIntegrity && !_healAt.ContainsKey(HealKey(t)))
                    {
                        long start = Tick + BuildDefs.HealDelayTicks;
                        AddHeal(new BuildHeal(t, Tick, start, NextDue(start, rec.Integrity, def.MaxIntegrity, 0), 0));
                    }
                }
            }
        }

        /// <summary>
        /// Фаза 4.9 такта (E2-14, перед правками мира): у построек, которым пора, — прибавка в мир правкой тайла. Тайл, на
        /// который в этом такте уже есть правка (урон, дверь, снос, снятие флага), ждёт такт — прибавка не теряется.
        /// Постройка пропала, стала обломками или целой — из списка.
        /// </summary>
        private void StepHeal(long tick)
        {
            if (_healScanPending)
            {
                ScanDamaged();
            }

            int budget = BuildDefs.HealEditsPerTick;
            for (int i = 0; i < _healCount && budget > 0; i++)
            {
                BuildHeal e = _heals[i];
                if (e.Due > tick)
                {
                    continue;
                }

                TileCoord t = e.Tile;
                ChunkCoord chunk = Torus.ChunkOf(_shape, t);
                ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
                Chunk? chunkData = _world.FindChunk(chunk);
                ushort obj = _world.GetObject(t);
                if (chunkData is null || !chunkData.TryGetBuild(index, out BuildRecord rec) || rec.IsDebris || obj == ObjectId.Empty
                    || !BuildDefs.TryGet(obj, out BuildDef def) || rec.Integrity >= def.MaxIntegrity)
                {
                    RemoveHeal(i--);
                    continue;
                }

                // На тайл в этом такте уже есть правка: правка мира, не меняющая прочность (снятие флагов урона по истечении,
                // дверь), — прибавка вливается в неё (иначе на такте истечения флага ступень съезжала бы на такт); прочая
                // (игрок, починка, обломки, своя прочность) — ждём такт, прибавка не теряется.
                int j = FindEditAt(chunk, index);
                if (j >= 0 && (_edits[j].Requester > 0 || _edits[j].Repair || !_edits[j].Edit.HasRecord || _edits[j].Edit.Obj != obj
                    || _edits[j].Edit.Record.IsDebris || _edits[j].Edit.Record.Integrity != rec.Integrity))
                {
                    continue;
                }

                int acc = e.Acc;
                int healed = Math.Min(def.MaxIntegrity, rec.Integrity + Gain(tick - e.Last, def.MaxIntegrity, ref acc));
                if (healed != rec.Integrity)
                {
                    if (j >= 0)
                    {
                        EditRequest p = _edits[j];
                        BuildRecord merged = p.Edit.Record.WithIntegrity((ushort)healed);
                        _edits[j] = new EditRequest(p.Chunk, p.Arrival, new TileEdit(p.Edit.Index, p.Edit.Floor, p.Edit.Obj, merged),
                            p.Requester, p.Tag, p.CheckCreatures, p.Repair, p.Cell);
                    }
                    else if (!Enqueue(t, _world.GetFloor(t), obj, true, rec.WithIntegrity((ushort)healed), 0, 0, false))
                    {
                        return; // очередь правок полна — следующим тактом
                    }

                    budget--;
                }

                if (healed >= def.MaxIntegrity)
                {
                    RemoveHeal(i--);
                    continue;
                }

                _heals[i] = new BuildHeal(t, e.Hit, tick, NextDue(tick, healed, def.MaxIntegrity, acc), acc);
            }
        }

        // Первая правка такта на тайл (она и применится: на тайл в пачке проходит одна, 3.7); нет — −1.
        private int FindEditAt(ChunkCoord chunk, ushort index)
        {
            for (int j = 0; j < _editCount; j++)
            {
                if (_edits[j].Edit.Index == index && _edits[j].Chunk == chunk)
                {
                    return j;
                }
            }

            return -1;
        }
    }
}
