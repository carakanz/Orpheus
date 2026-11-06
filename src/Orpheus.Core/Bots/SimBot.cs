using System;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;

namespace Orpheus.Core.Bots
{
    /// <summary>
    /// Бот без сети — источник ввода в режиме симуляции (docs/07, 11.3): перед каждым тактом даёт кадр
    /// ввода своей сущности. Решение — только от номера такта, своей сущности и своего генератора от зерна,
    /// поэтому один и тот же запуск даёт один и тот же журнал ввода.
    /// </summary>
    public abstract class SimBot
    {
        protected SimBot(int entityId)
        {
            EntityId = entityId;
        }

        /// <summary>Номер сущности, которой бот управляет.</summary>
        public int EntityId { get; }

        /// <summary>Сценарий: <c>idle</c>, <c>wander</c>.</summary>
        public abstract string Script { get; }

        /// <summary>Кадр ввода перед тактом <paramref name="tick"/> (номер такта, который сейчас выполнится).</summary>
        public abstract InputFrame Think(long tick, in SimEntity self);

        /// <summary>Сценарии, которые есть: <c>idle</c>, <c>wander</c>.</summary>
        public static readonly string[] Scripts = { IdleBot.Name, WanderBot.Name };

        /// <summary>
        /// Бот сценария <paramref name="script"/> для сущности <paramref name="entityId"/>; <paramref name="botIndex"/> —
        /// номер бота по порядку (зерно потока бота). Неизвестный сценарий — исключение.
        /// </summary>
        public static SimBot Create(string script, int entityId, int botIndex, ulong worldSeed)
        {
            switch (script)
            {
                case IdleBot.Name:
                    return new IdleBot(entityId);
                case WanderBot.Name:
                    return new WanderBot(entityId, botIndex, worldSeed);
                default:
                    throw new ArgumentException($"Неизвестный сценарий бота «{script}»; есть: {string.Join(", ", Scripts)}.", nameof(script));
            }
        }
    }

    /// <summary>Сценарий <c>idle</c>: стоит, ничего не нажимает.</summary>
    public sealed class IdleBot : SimBot
    {
        public const string Name = "idle";

        public IdleBot(int entityId)
            : base(entityId)
        {
        }

        public override string Script => Name;

        public override InputFrame Think(long tick, in SimEntity self) => InputFrame.None;
    }

    /// <summary>
    /// Сценарий <c>wander</c>: бродит — выбирает одно из 8 направлений (или стойку с шансом 1 из 9)
    /// и держит его от 0,5 до 3 с, потом выбирает снова. Генератор — <see cref="DetRandom"/> от
    /// SplitMix64(номер бота, <see cref="SystemId"/>, зерно мира): у каждого бота свой поток.
    /// </summary>
    public sealed class WanderBot : SimBot
    {
        public const string Name = "wander";

        /// <summary>Номер системы для зерна потока ботов (11.5: «поток на систему»).</summary>
        public const int SystemId = 0x0B07;

        /// <summary>Наименьшая выдержка направления в тактах: 0,5 с.</summary>
        public const int MinHoldTicks = Simulation.TickRate / 2;

        /// <summary>Наибольшая выдержка направления в тактах: 3 с.</summary>
        public const int MaxHoldTicks = Simulation.TickRate * 3;

        private DetRandom _rng;
        private InputFrame _current;
        private int _ticksLeft;

        public WanderBot(int entityId, int botIndex, ulong worldSeed)
            : base(entityId)
        {
            _rng = new DetRandom(SplitMix64.Hash(botIndex, SystemId, worldSeed));
        }

        public override string Script => Name;

        public override InputFrame Think(long tick, in SimEntity self)
        {
            if (_ticksLeft <= 0)
            {
                int pick = _rng.NextInt(Input8.Count + 1); // 0…7 — направление, 8 — стойка
                _current = new InputFrame(pick == Input8.Count ? Input8.None : Input8.FromIndex(pick));
                _ticksLeft = _rng.NextInt(MinHoldTicks, MaxHoldTicks + 1);
            }

            _ticksLeft--;
            return _current;
        }
    }
}
