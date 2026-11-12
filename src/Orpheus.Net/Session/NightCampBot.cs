using System;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот <c>night_camp</c> (docs/07 11.3; приёмка `08` 3.1 1.6; карточка 1-026): стоит ночью в поле — без ввода,
    /// без удара, без убегания. Считает, когда погиб: <see cref="TicksToDeath"/> по пропаже
    /// <see cref="SelfState.Present"/> (снимок), убийцу — по «Смерти» (тип 24, канал 2): жертва = свой
    /// (<see cref="DeathMessage.Victim"/>), атакующий и тип урона — из сообщения. Это вторая половина критерия
    /// «минута» вехи 1: без укрытия разбойник убивает персонажа «первого уровня» за ≤ 2 мин (<c>02</c> 8.2).
    /// </summary>
    public sealed class NightCampBot
    {
        public const string Name = "night_camp";

        /// <summary>Здоровье своего целое, максимум уровня 0 — 100 (см. <see cref="BuildRoomBot.MaxHealth"/>, [док]).</summary>
        public const int MaxHealth = BuildRoomBot.MaxHealth;

        private int _selfId = -1;
        private int _lastHealth = MaxHealth;

        /// <summary>Кадров сценария (вход → текущий).</summary>
        public int Ticks { get; private set; }

        /// <summary>Кадров от входа до смерти; −1 — ещё жив.</summary>
        public int TicksToDeath { get; private set; } = -1;

        /// <summary>Кто убил (№ сущности из «Смерти»; −1 — не видели сообщения).</summary>
        public int KillerId { get; private set; } = -1;

        /// <summary>Тип урона добивающего удара (<see cref="DeathMessage.DamageType"/>).</summary>
        public byte KillerDamageType { get; private set; }

        /// <summary>Сколько раз полоска здоровья падала (число ударов, замеченных снимками).</summary>
        public int HitsTaken { get; private set; }

        /// <summary>Минимальная полоска здоровья, %.</summary>
        public int MinHealthPct { get; private set; } = 100;

        /// <summary>Жив (смерти не видели).</summary>
        public bool Alive => TicksToDeath < 0;

        /// <summary>«Смерть» сервера (тип 24): если жертва — свой, запоминаем убийцу и тип урона.</summary>
        public void OnDeath(DeathMessage m)
        {
            if (m.Victim == _selfId)
            {
                KillerId = m.Attacker;
                KillerDamageType = m.DamageType;
            }
        }

        /// <summary>Кадр ввода (30 Гц): всегда «стой» — бот не двигается и не бьёт (правило сценария).</summary>
        public InputFrame Next(NetClient client)
        {
            Ticks++;
            if (client.World == null || !client.HasSnapshot)
            {
                return InputFrame.None;
            }

            SelfState self = client.Latest.Self;
            if (_selfId < 0)
            {
                _selfId = client.EntityId;
            }

            if (Alive)
            {
                int hp = self.Health;
                if (hp < _lastHealth)
                {
                    HitsTaken++;
                }

                _lastHealth = hp;
                int pct = MaxHealth > 0 ? hp * 100 / MaxHealth : 0;
                if (pct < MinHealthPct)
                {
                    MinHealthPct = pct;
                }

                if (!self.Present || hp == 0)
                {
                    TicksToDeath = Ticks; // снимок: своего состояния нет или здоровье нулевое — персонаж мёртв (1-001)
                }
            }

            return InputFrame.None;
        }
    }
}
