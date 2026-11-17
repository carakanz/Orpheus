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

            _feed.OnDeath(m, Time.timeAsDouble);
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
