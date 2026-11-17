#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Бой на экране в сетевой игре (E3-1, шаг 3.1): цифры урона, полоски здоровья врагов, экран смерти, вспышка попадания
    /// по чужим. Источник — только сообщения сервера (урон 37, смерть 24, возрождение 25) через <see cref="CombatFeed"/>;
    /// предсказание цифр не рисует. Рисует <see cref="UI.CombatHud"/> после расстановки сущностей кадра (<see cref="Render"/>).
    /// </summary>
    public sealed partial class NetGame
    {
        readonly CombatFeed _feed = new CombatFeed();
        readonly List<UI.EnemyMark> _enemyMarks = new List<UI.EnemyMark>();
        string _killer = "";

        /// <summary>Подписаться на события боя клиента (после входа: свой номер известен).</summary>
        void HookCombat(NetClient c)
        {
            _feed.LocalId = _localId;
            System.Action<DamageMessage>? prevDamage = c.DamageSeen;
            c.DamageSeen = m => { prevDamage?.Invoke(m); OnDamage(m); };
            System.Action<DeathMessage>? prevDeath = c.DeathSeen;
            c.DeathSeen = m => { prevDeath?.Invoke(m); OnDeath(m); };
            System.Action<RespawnMessage>? prevRespawn = c.RespawnSeen;
            c.RespawnSeen = m => { prevRespawn?.Invoke(m); _feed.OnRespawn(m, Time.timeAsDouble); };
        }

        void OnDamage(DamageMessage m)
        {
            if (!TryPositionOf(m.Victim, out WorldPos at, out bool character))
            {
                return; // жертва вне обзора — цифру негде показать
            }

            _feed.OnDamage(m, at, character, Time.timeAsDouble);
            if (m.Victim != _localId && _origin != null && TryPositionOf(m.Attacker, out WorldPos from, out _))
            {
                _anim?.OnRemoteHit(m.Victim, _origin.ToUnity(at), _origin.ToUnity(from)); // E3-1: вспышка и отдача у чужих
            }
        }

        void OnDeath(DeathMessage m)
        {
            if (m.Victim == _localId)
            {
                SnapshotEntity killer = default;
                bool known = m.Attacker > 0 && _client != null && _client.HasSnapshot && _client.Latest.TryFind(m.Attacker, out killer);

                _killer = UI.UiStrings.KilledBy(known, killer.Kind == SimEntity.KindCharacter,
                    (byte)(killer.Enemy >> SnapshotLayout.EnemyArchetypeShift));
            }

            // C-029: убит враг — открыт (подсказка «где взять» перестаёт писать «?» у его добычи).
            if (m.Victim != _localId && _client != null && _client.HasSnapshot && _client.Latest.TryFind(m.Victim, out SnapshotEntity victim)
                && victim.Kind == SimEntity.KindEnemy)
            {
                byte arch = (byte)(victim.Enemy >> SnapshotLayout.EnemyArchetypeShift);
                _known?.NoteKill(arch == EnemyArchetype.Melee ? ItemSources.Bandit : arch);
            }

            _feed.OnDeath(m, Time.timeAsDouble);
        }

        // ---------- C-029: открытия игрока (биомы, враги) для подсказок ингредиентов ----------

        Discovery? _known;
        ulong _knownSeed;
        float _knownNextSave;

        string KnownPath(ulong seed) => System.IO.Path.Combine(Application.persistentDataPath, "Discovery", $"{seed:x16}.txt");

        /// <summary>Кадр открытий: загрузить по зерну мира, отметить биом под персонажем, раз в 5 с сохранить изменения.</summary>
        void DiscoveryFrame()
        {
            if (_world == null || _ui == null)
            {
                return;
            }

            if (_known == null || _knownSeed != _world.Seed)
            {
                _knownSeed = _world.Seed;
                string? text = null;
                try
                {
                    string path = KnownPath(_knownSeed);
                    text = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[known] не прочитать открытия: {e.Message}");
                }

                _known = Discovery.Load(text);
                _ui.Known = _known;
            }

            _known.NoteFloor(_world.GetFloor(Torus.TileOf(_map, _localPos)));
            if (_known.Dirty && Time.unscaledTime >= _knownNextSave)
            {
                _knownNextSave = Time.unscaledTime + 5f;
                try
                {
                    string path = KnownPath(_knownSeed);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    System.IO.File.WriteAllText(path, _known.Save());
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[known] не сохранить открытия: {e.Message}");
                }
            }
        }

        /// <summary>Где сущность сейчас на экране (свой — предсказание; чужие — показ интерполяции, иначе последний снимок).</summary>
        bool TryPositionOf(int id, out WorldPos at, out bool character)
        {
            at = default;
            character = false;
            if (id == _localId)
            {
                at = _localPos;
                character = true;
                return true;
            }

            if (_client == null || !_client.HasSnapshot || !_client.Latest.TryFind(id, out SnapshotEntity e))
            {
                return false;
            }

            character = e.Kind == SimEntity.KindCharacter;
            at = _remote != null && _remote.TryGet(id, out WorldPos from, out _, out _) ? from : e.Position;
            return true;
        }

        /// <summary>Кадр боя на экране: после расстановки чужих (их места — нарисованные корни).</summary>
        void CombatFrame()
        {
            if (_ui == null || _origin == null || _remote == null)
            {
                return;
            }

            DiscoveryFrame(); // C-029
            double now = Time.timeAsDouble;
            _feed.Expire(now);
            _enemyMarks.Clear();
            Snapshot snap = _remote.Latest;
            for (int i = 0; i < snap.Count; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Kind != SimEntity.KindEnemy || !_seen.Contains(e.Id) || !_ents.TryGetValue(e.Id, out GameObject? go))
                {
                    continue;
                }

                _enemyMarks.Add(new UI.EnemyMark { Id = e.Id, World = go.transform.position, HealthPct = e.HealthPct, Always = false });
            }

            FloatingOrigin origin = _origin;
            _ui.Combat.Frame(_feed, now, _enemyMarks, p => origin.ToUnity(p), _killer);
        }
    }
}
