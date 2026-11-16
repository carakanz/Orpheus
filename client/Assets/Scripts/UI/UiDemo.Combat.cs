#nullable enable
using System.Collections.Generic;
using Orpheus.Client.Anim;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using Pose = Orpheus.Client.Anim.Pose;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Сцены стенда снимков боя на экране (E3-1; ключ <c>--ui-demo combat-hit|combat-hurt|combat-kill|combat-death|combat-respawn</c>),
    /// без сервера: враги-болванки вокруг персонажа, скрипт кормит <see cref="CombatFeed"/> теми же сообщениями, что шлёт
    /// сервер (урон 37, смерть 24, возрождение 25), рисует тот же <see cref="CombatHud"/>. Время сцены — кадры по 1/60 с
    /// (снимок — на кадре 30, т. е. 0,5 с).
    /// <list type="bullet">
    /// <item><c>combat-hit</c> — свои удары по разбойнику (жёлтые 10, 10 и крупная 13 — добивающий серии), чужой удар по
    /// второму (белая), полоски у обоих; элитный — полоска с именем без попаданий;</item>
    /// <item><c>combat-hurt</c> — два разбойника бьют персонажа (красные цифры), сфера здоровья 80;</item>
    /// <item><c>combat-kill</c> — разбойник убит на 0,5 с: падает и растворяется (лента <c>--shot-walk stay</c>);</item>
    /// <item><c>combat-death</c> — персонаж погиб 1,2 с назад: экран темнеет, «Вы погибли», убийца, таймер;</item>
    /// <item><c>combat-respawn</c> — возродился у кровати 0,2 с назад: затемнение уходит, «Вы возродились у кровати».</item>
    /// </list>
    /// </summary>
    public sealed partial class UiDemo
    {
        const int DemoSelf = 1, DemoOther = 77;

        /// <summary>Сцены боя (E3-1).</summary>
        public static bool IsCombat(string? mode) =>
            mode == "combat-hit" || mode == "combat-hurt" || mode == "combat-kill" || mode == "combat-death" || mode == "combat-respawn";

        sealed class Foe
        {
            public int Id;
            public ActorRig Rig = null!;
            public Int2 Offset; // от персонажа, 1/256 тайла (ось Y ядра — вниз)
            public int Hp = Simulation.MaxHealthMeleeEnemy;
            public bool Elite;
            public float DeadAge = -1f;
        }

        readonly CombatFeed _feed = new CombatFeed { LocalId = DemoSelf };
        readonly List<Foe> _foes = new List<Foe>();
        readonly List<EnemyMark> _marks = new List<EnemyMark>();
        ActorRig? _heroRig;
        float _heroDeadAge = -1f;
        int _health = Simulation.MaxHealthCharacter;

        /// <summary>Враги сцены и свой персонаж (звать сразу после конструктора, вместе с <see cref="BindWorld"/>).</summary>
        public void BindCombat(FloatingOrigin origin, Transform parent, Sprite sprite, ActorRig hero)
        {
            _origin = origin;
            _heroRig = hero;
            const int T = Torus.SubTilesPerTile;
            switch (_mode)
            {
                case "combat-hit":
                    AddFoe(parent, sprite, 101, new Int2(T * 5 / 2, 0), false);
                    AddFoe(parent, sprite, 102, new Int2(T * 4, -T * 2), false);
                    AddFoe(parent, sprite, 103, new Int2(-T * 4, -T), true);
                    break;
                case "combat-hurt":
                    _health = 100;
                    AddFoe(parent, sprite, 101, new Int2(T * 3 / 2, 0), false);
                    AddFoe(parent, sprite, 102, new Int2(-T * 3 / 2, -T / 2), false);
                    break;
                case "combat-kill":
                    AddFoe(parent, sprite, 101, new Int2(T * 2, 0), false);
                    break;
                default:
                    AddFoe(parent, sprite, 101, new Int2(T * 3 / 2, 0), false);
                    break;
            }
        }

        void AddFoe(Transform parent, Sprite sprite, int id, Int2 offset, bool elite)
        {
            Transform root = ActorFactory.Create(parent, $"Foe {id}", sprite, ActorFactory.EnemyTint);
            WorldFx.AddActorShadow(root, 0.8f);
            _foes.Add(new Foe { Id = id, Rig = new ActorRig(root), Offset = offset, Elite = elite, Hp = elite ? 30 : Simulation.MaxHealthMeleeEnemy });
        }

        void CombatDemoFrame(WorldPos position)
        {
            const float dt = 1f / 60f;
            double t = _frame / 60.0;
            _lastPosition = position;
            CombatScript(t);

            var shape = _world!.Shape;
            _marks.Clear();
            foreach (Foe f in _foes)
            {
                WorldPos at = Torus.Offset(shape, position, f.Offset);
                Vector3 u = _origin!.ToUnity(at);
                f.Rig.Root.position = u;
                Pose p = f.Rig.Animator.Step(dt, new ActorInput());
                p.FlipX = (f.Offset.X > 0 ? false : true) != AnimParams.SpriteFacesRight; // лицом к персонажу
                if (f.DeadAge >= 0f)
                {
                    f.DeadAge += dt;
                    p = DeathAnim.Apply(p, f.DeadAge, f.Offset.X > 0, dissolve: true);
                    f.Rig.Root.gameObject.SetActive(!DeathAnim.Gone(f.DeadAge, true));
                }

                f.Rig.Apply(p);
                _marks.Add(new EnemyMark
                {
                    Id = f.Id, World = u, HealthPct = f.Hp * 100 / Simulation.MaxHealthMeleeEnemy, Always = f.Elite,
                    Name = f.Elite ? UiStrings.EliteRogue : null,
                });
            }

            if (_heroRig != null && (_feed.Dead || _mode == "combat-death"))
            {
                _heroDeadAge = _heroDeadAge < 0f ? 1.2f : _heroDeadAge + dt;
                _heroRig.Apply(DeathAnim.Apply(Pose.Identity, _heroDeadAge, false, dissolve: false));
            }

            _feed.Expire(t);
            var input = new UiInput { HasPointer = true, Pointer = new Vector2(-100f, -100f) };
            _ui.Frame(input, _bag, selected: 3, dead: _feed.Dead, health: _feed.Dead ? 0 : _health, maxHealth: 100, stamina: 45, maxStamina: 100,
                new UiCraftState { Queue = _queue, World = _world, Position = position });
            FloatingOrigin origin = _origin!;
            _ui.Combat.Frame(_feed, t, _marks, w => origin.ToUnity(w), UiStrings.KilledBy(true, false, EnemyArchetype.Melee));
        }

        WorldPos _lastPosition;

        /// <summary>Скрипт сцены: в кадр события — сообщение в ленту, как от сервера.</summary>
        void CombatScript(double t)
        {
            int f = _frame;
            switch (_mode)
            {
                case "combat-hit":
                    if (f == 6) Hit(101, DemoSelf, 10, false, t);
                    if (f == 15) Hit(101, DemoSelf, 10, false, t);
                    if (f == 18) Hit(102, DemoOther, 10, false, t);
                    if (f == 24) Hit(101, DemoSelf, 13, true, t);
                    break;
                case "combat-hurt":
                    if (f == 3) Hit(101, DemoSelf, 10, false, t);
                    if (f == 9) Hit(DemoSelf, 101, 10, false, t);
                    if (f == 21) Hit(DemoSelf, 102, 10, false, t);
                    break;
                case "combat-kill":
                    if (f == 3) Hit(101, DemoSelf, 10, false, t);
                    if (f == 12) Hit(101, DemoSelf, 10, false, t);
                    if (f == 21) Hit(101, DemoSelf, 13, true, t);
                    if (f == 30)
                    {
                        Hit(101, DemoSelf, 10, false, t);
                        Die(101, DemoSelf, t);
                    }

                    break;
                case "combat-death":
                    if (f == 1)
                    {
                        _feed.OnDeath(new DeathMessage { Victim = DemoSelf, Attacker = 101, Damage = 10, RespawnTicks = Simulation.RespawnDelayTicks }, t - 0.7);
                    }

                    break;
                case "combat-respawn":
                    if (f == 1)
                    {
                        _feed.OnDeath(new DeathMessage { Victim = DemoSelf, Attacker = 101, Damage = 10, RespawnTicks = Simulation.RespawnDelayTicks, Flags = DeathMessage.FlagAtBed }, t - 5.0);
                    }

                    if (f == 18)
                    {
                        _feed.OnRespawn(new RespawnMessage { Victim = DemoSelf }, t);
                    }

                    break;
            }
        }

        void Hit(int victim, int attacker, int amount, bool strong, double t)
        {
            var m = new DamageMessage { Victim = victim, Attacker = attacker, Amount = (ushort)amount, DamageType = 1, Flags = strong ? DamageMessage.FlagStrong : (byte)0 };
            WorldPos at = _lastPosition;
            Foe? foe = _foes.Find(x => x.Id == victim);
            if (foe != null)
            {
                at = Torus.Offset(_world!.Shape, _lastPosition, foe.Offset);
                foe.Hp = System.Math.Max(0, foe.Hp - amount);
                foe.Rig.Animator.OnHit(foe.Offset.X > 0 ? 1f : -1f, 0f);
            }
            else if (victim == DemoSelf)
            {
                _health -= amount;
                _heroRig?.Animator.OnHit(1f, 0f);
            }

            _feed.OnDamage(m, at, victim == DemoSelf, t);
        }

        void Die(int victim, int attacker, double t)
        {
            _feed.OnDeath(new DeathMessage { Victim = victim, Attacker = attacker, Damage = 10 }, t);
            Foe? foe = _foes.Find(x => x.Id == victim);
            if (foe != null)
            {
                foe.Hp = 0;
                foe.DeadAge = 0f;
            }
        }
    }
}
