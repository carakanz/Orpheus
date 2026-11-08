using System;
using System.Collections.Generic;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Персонажи мира на сервере (0-035; docs/07 6.2 «Персонажи — мировая часть», 7.4 «Вернулся», 7.5 «Уход»): кто бывал
    /// в мире и где стоял. Вошёл — сущность в такте; ушёл — сущность убрана, движение (позиция, остаток, скорость, уворот)
    /// остаётся за ключом; вошёл снова — тот же персонаж там же. Ключ — <see cref="CharacterRecord"/>: хозяин по токену,
    /// остальные по имени (<see cref="CharacterNames"/>, допущение `[док]`).
    /// <para>Порядок — по первому входу (и по файлу сохранения): сохранение одного состояния даёт один файл.</para>
    /// Только поток такта; вне такта (вход, уход — из <see cref="NetServer.Poll"/>).
    /// </summary>
    public sealed class CharacterRoster
    {
        private readonly List<CharacterRecord> _list = new List<CharacterRecord>();

        /// <summary>Персонажей мира (в мире и вне).</summary>
        public int Count => _list.Count;

        /// <summary>Персонаж № <paramref name="index"/> (копия).</summary>
        public CharacterRecord this[int index] => _list[index];

        /// <summary>Сейчас в мире (с сущностью).</summary>
        public int InWorld
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _list.Count; i++)
                {
                    if (_list[i].EntityId > 0)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// Персонажи из сохранения в мир, только что загруженный в <paramref name="sim"/>: кто был в мире на такте
        /// сохранения, тот теперь вне мира (после перезапуска сервера соединений нет) — его сущность убирается из такта,
        /// движение остаётся за ключом. Звать до появления ботов: они берут загруженные сущности по порядку.
        /// </summary>
        public static CharacterRoster Restore(IReadOnlyList<CharacterRecord>? saved, Simulation sim)
        {
            if (sim == null)
            {
                throw new ArgumentNullException(nameof(sim));
            }

            var roster = new CharacterRoster();
            if (saved == null)
            {
                return roster;
            }

            for (int i = 0; i < saved.Count; i++)
            {
                roster._list.Add(saved[i]);
            }

            for (int i = 0; i < roster._list.Count; i++)
            {
                if (roster._list[i].EntityId > 0)
                {
                    roster.Park(i, sim);
                }
            }

            return roster;
        }

        /// <summary>Персонаж хозяина; нет — −1.</summary>
        public int FindHost()
        {
            for (int i = 0; i < _list.Count; i++)
            {
                if (_list[i].IsHost)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Персонаж игрока (не хозяина) с этим именем; нет — −1.</summary>
        public int FindPlayer(string name)
        {
            for (int i = 0; i < _list.Count; i++)
            {
                if (!_list[i].IsHost && CharacterNames.Same(_list[i].Name, name))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Персонаж сущности <paramref name="entityId"/>; нет — −1.</summary>
        public int FindEntity(int entityId)
        {
            for (int i = 0; entityId > 0 && i < _list.Count; i++)
            {
                if (_list[i].EntityId == entityId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Новый персонаж в мире (первый вход). Возвращает его номер в списке.</summary>
        public int AddInWorld(bool isHost, string name, int entityId)
        {
            _list.Add(new CharacterRecord { IsHost = isHost, Name = name ?? string.Empty, EntityId = entityId });
            return _list.Count - 1;
        }

        /// <summary>Персонаж вернулся: в мире сущностью <paramref name="entityId"/>; имя — как при этом входе.</summary>
        public void SetInWorld(int index, int entityId, string name)
        {
            CharacterRecord r = _list[index];
            r.EntityId = entityId;
            r.Name = name ?? r.Name;
            _list[index] = r;
        }

        /// <summary>
        /// Персонаж уходит из мира: движение сущности — за ключом, сущность — из такта (вне такта). Возвращает,
        /// была ли сущность.
        /// </summary>
        public bool Park(int index, Simulation sim)
        {
            CharacterRecord r = _list[index];
            int slot = sim.SlotOf(r.EntityId);
            bool had = slot >= 0;
            if (had)
            {
                Mover m = sim.Entities[slot].Mover;
                r.Mover = m;
                sim.RemoveEntity(r.EntityId);
            }

            r.EntityId = 0;
            _list[index] = r;
            return had;
        }

        /// <summary>
        /// Копия списка на границу такта — для сохранения (у персонажей в мире движение берётся из сущности, в файле — её номер).
        /// </summary>
        public CharacterRecord[] Capture() => _list.ToArray();
    }
}
