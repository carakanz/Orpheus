using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>Удары по тайлу (C-028): тайл, объект, по которому били, ударов набрано из нужных, время последнего удара.</summary>
    public struct WearEntry
    {
        public TileCoord Tile;
        public ushort Obj;
        public byte Hits;
        public byte Needed;
        public long LastHit;
    }

    /// <summary>
    /// C-028: прочность тайла — общий механизм ударов по природным объектам (сейчас — кирка; топор и деревья — позже той же
    /// таблицей). Удары копятся по тайлу (у всех игроков общие), сбрасываются через <see cref="ResetAfter"/> без ударов и при
    /// смене объекта тайла. Время — в единицах владельца (сервер — такты 30 Гц, клиент — микросекунды): часы и порог задаёт он.
    /// Порядок записей — порядок первого удара (детерминирован), записей не больше <see cref="Capacity"/> (сверх — вытесняется
    /// самая старая по удару). Не сохраняется: через несколько секунд без ударов всё равно сброс.
    /// </summary>
    public sealed class TileWear
    {
        /// <summary>Сброс на сервере: 5 с без ударов = 150 тактов (<i>прикидка</i>).</summary>
        public const int ServerResetTicks = 150;

        /// <summary>Сброс у клиента (показ трещин): те же 5 с, мкс.</summary>
        public const long ClientResetUs = 5_000_000;

        /// <summary>Записей не больше.</summary>
        public const int Capacity = 512;

        private readonly List<WearEntry> _entries = new List<WearEntry>();

        public TileWear(long resetAfter)
        {
            ResetAfter = resetAfter > 0 ? resetAfter : throw new ArgumentOutOfRangeException(nameof(resetAfter));
        }

        /// <summary>Без ударов дольше этого — сброс.</summary>
        public long ResetAfter { get; }

        /// <summary>Записей (тайлов с набранными ударами).</summary>
        public int Count => _entries.Count;

        /// <summary>Запись по номеру (показ трещин).</summary>
        public WearEntry this[int i] => _entries[i];

        /// <summary>
        /// Удар по тайлу <paramref name="tile"/> с объектом <paramref name="obj"/> (нужно <paramref name="needed"/>) в момент
        /// <paramref name="now"/>: удары +1 (объект сменился или запись истекла — с нуля). Возвращает набранное.
        /// </summary>
        public int Hit(TileCoord tile, ushort obj, int needed, long now)
        {
            int i = IndexOf(tile);
            int hits = 1;
            if (i >= 0 && _entries[i].Obj == obj && now - _entries[i].LastHit <= ResetAfter)
            {
                hits = Math.Min(byte.MaxValue, _entries[i].Hits + 1);
            }

            Set(tile, obj, hits, needed, now);
            return hits;
        }

        /// <summary>Записать удары тайла как есть (сводка сервера у клиента); 0 ударов — запись снимается.</summary>
        public void Set(TileCoord tile, ushort obj, int hits, int needed, long now)
        {
            int i = IndexOf(tile);
            if (hits <= 0)
            {
                if (i >= 0)
                {
                    _entries.RemoveAt(i);
                }

                return;
            }

            var e = new WearEntry
            {
                Tile = tile,
                Obj = obj,
                Hits = (byte)Math.Min(byte.MaxValue, hits),
                Needed = (byte)Math.Clamp(needed, 1, byte.MaxValue),
                LastHit = now,
            };
            if (i >= 0)
            {
                _entries[i] = e;
                return;
            }

            if (_entries.Count >= Capacity)
            {
                int old = 0;
                for (int k = 1; k < _entries.Count; k++)
                {
                    if (_entries[k].LastHit < _entries[old].LastHit)
                    {
                        old = k;
                    }
                }

                _entries.RemoveAt(old);
            }

            _entries.Add(e);
        }

        /// <summary>Ударов на тайле с объектом <paramref name="obj"/> (другой объект или нет записи — 0).</summary>
        public int HitsOn(TileCoord tile, ushort obj)
        {
            int i = IndexOf(tile);
            return i >= 0 && _entries[i].Obj == obj ? _entries[i].Hits : 0;
        }

        /// <summary>Снять записи без ударов дольше <see cref="ResetAfter"/> к моменту <paramref name="now"/>.</summary>
        public void Expire(long now)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (now - _entries[i].LastHit > ResetAfter)
                {
                    _entries.RemoveAt(i);
                }
            }
        }

        /// <summary>Снять все записи (выход из мира).</summary>
        public void Clear() => _entries.Clear();

        private int IndexOf(TileCoord tile)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Tile == tile)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
