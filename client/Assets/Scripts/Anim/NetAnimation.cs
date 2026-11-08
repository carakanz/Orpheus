#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Процедурная анимация в сетевой игре (K-001): только отображение — ядро, сеть и предсказание не меняются, состояние берётся
    /// из того, что клиент уже знает:
    /// <list type="bullet">
    /// <item>свой персонаж — скорость по нарисованной позиции (предсказание с гашением), уворот — <see cref="LocalPrediction.Dodge"/>,
    /// копание — <see cref="NetClient.Digging"/> и тайл (0-028);</item>
    /// <item>чужие — скорость по интерполированной позиции; уворота в снимке нет — рывок узнаётся по скорости;</item>
    /// <item>враги — «замах начат» (<see cref="NetClient.SwingStarted"/>, 0-029/0-033): замах до кадра решения удара по этому
    /// игроку, удар, отход; сектор на земле; «попал» (<see cref="NetClient.MeleeResolved"/>) — красная вспышка, отдача, искры.</item>
    /// </list>
    /// <c>NetGame</c> зовёт <see cref="Remote"/> для каждого чужого и <see cref="Frame"/> раз в кадр после расстановки.
    /// </summary>
    public sealed class NetAnimation
    {
        readonly GameBoot _g;
        readonly NetClient _client;
        readonly FloatingOrigin _origin;
        readonly WorldModel _world;
        readonly Transform _root;
        readonly Sprite _sprite;
        readonly Material _sectorMaterial;
        readonly ActorRig _self;
        readonly Dictionary<int, ActorRig> _remotes = new Dictionary<int, ActorRig>();
        readonly Dictionary<int, Enemy> _enemies = new Dictionary<int, Enemy>();
        readonly SwingTrack _swings = new SwingTrack();
        readonly Sparks _sparks;

        sealed class Enemy
        {
            public SwingActor Actor = null!;
            public WorldPos Position;
        }

        /// <summary>Замахов показано (без повторов сообщений), попаданий показано.</summary>
        public long SwingsShown => _swings.Accepted;
        public long HitsShown { get; private set; }

        public NetAnimation(GameBoot g, NetClient client, FloatingOrigin origin, WorldModel world, Transform entityRoot)
        {
            _g = g;
            _client = client;
            _origin = origin;
            _world = world;
            _root = entityRoot;
            _self = new ActorRig(g.Character);
            _sprite = _self.Renderer.sprite;
            _sectorMaterial = AnimAssets.SpriteMaterial(_self.Renderer);
            _sparks = new Sparks(entityRoot);
            client.SwingStarted = OnSwing;
            client.MeleeResolved = OnResult;
        }

        /// <summary>Чужой персонаж поставлен на интерполированное место: ходьба, рывок — по скорости.</summary>
        public void Remote(int id, Transform root)
        {
            if (!_remotes.TryGetValue(id, out ActorRig? rig))
            {
                rig = new ActorRig(root);
                _remotes.Add(id, rig);
            }

            float dt = Time.deltaTime;
            Vector2 v = rig.MeasureVelocity(dt);
            var s = new ActorInput { VelX = v.x, VelY = v.y };
            if (v.magnitude > AnimParams.RemoteDashSpeed)
            {
                Vector2 d = v.normalized;
                s.Dashing = true;
                s.DashProgress = 0.5f;
                s.DashDirX = d.x;
                s.DashDirY = d.y;
            }

            rig.Apply(rig.Animator.Step(dt, s));
        }

        /// <summary>Кадр: свой персонаж, враги, частицы. Позиции корней уже расставлены от якоря.</summary>
        public void Frame(long nowUs, LocalPrediction pred)
        {
            float dt = Time.deltaTime;

            // Свой.
            Vector2 v = _self.MeasureVelocity(dt);
            var s = new ActorInput { VelX = v.x, VelY = v.y };
            Dodge dodge = pred.Dodge;
            if (dodge.Dashing)
            {
                Input8 dir = dodge.DashDirection;
                var d = new Vector2(dir.DX, -dir.DY).normalized; // ядро: Y вниз; экран: Y вверх
                s.Dashing = true;
                s.DashProgress = 1f - dodge.DashLeft / (float)Dodge.DashFrames;
                s.DashDirX = d.x;
                s.DashDirY = d.y;
            }

            s.Invulnerable = dodge.Invulnerable;
            Vector3 digAt = Vector3.zero;
            float[] dust = AnimParams.DustLight;
            if (_client.Digging)
            {
                TileCoord t = _client.DigTile;
                digAt = _origin.ToUnity(TileCenter(t));
                Vector2 d = ((Vector2)(digAt - _g.Character.position)).normalized;
                s.Digging = true;
                s.DigDirX = d.x;
                s.DigDirY = d.y;
                s.DigPeriod = ProceduralAnimator.DigPeriodFor(_client.DigFrames);
                dust = DustOf(_world.GetObject(t));
            }

            Pose p = _self.Animator.Step(dt, s);
            _self.Apply(p);
            if (p.DigPokeNow)
            {
                EmitDust(digAt, new Vector2(-s.DigDirX, -s.DigDirY), dust);
            }

            // Враги по «замах начат».
            foreach (KeyValuePair<int, Enemy> kv in _enemies)
            {
                Enemy e = kv.Value;
                e.Actor.Rig.Root.position = _origin.ToUnity(e.Position);
                SwingPhase phase = _swings.Phase(kv.Key, nowUs, out float q);
                e.Actor.Animate(dt, phase, q);
            }

            _swings.Expire(nowUs);
            _sparks.Update(dt);
        }

        void OnSwing(SwingStartMessage m)
        {
            long now = NetGame.NowUs();
            if (!_swings.OnSwing(m.AttackerId, m.StartFrame, m.ResolveFrame, _client.OwnFrameAt(now), now))
            {
                return; // повтор — сервер шлёт «замах начат» каждый такт замаха
            }

            if (!_enemies.TryGetValue(m.AttackerId, out Enemy? e))
            {
                // Манекен 0-029 — не сущность снимка: виден только по сообщениям замаха.
                Transform root = ActorFactory.Create(_root, $"Enemy {m.AttackerId}", _sprite, ActorFactory.EnemyTint);
                e = new Enemy { Actor = new SwingActor(root, _sectorMaterial) };
                _enemies.Add(m.AttackerId, e);
            }

            e.Position = m.Origin;
            float a = m.Axis / 65536f * Mathf.PI * 2f;
            e.Actor.Axis = new Vector2(Mathf.Cos(a), -Mathf.Sin(a)); // угол ядра: 0 — восток, рост к югу (Y вниз)
            e.Actor.Reach = m.ReachUnits / (float)Torus.SubTilesPerTile;
            e.Actor.HalfArc = m.HalfArcSteps / 65536f * Mathf.PI * 2f;
            if (_swings.Accepted <= 3)
            {
                _swings.TryGet(m.AttackerId, out SwingTrack.Swing sw);
                Debug.Log($"[anim] замах #{m.AttackerId}: показ {(sw.ContactUs - now) / 1000} мс (d {m.WindupFrames} кадров, D_p {m.DelayFrames})");
            }
        }

        void OnResult(MeleeResultMessage m)
        {
            if (!m.Hit)
            {
                return;
            }

            HitsShown++;
            Vector2 me = _g.Character.position;
            Vector2 from = _enemies.TryGetValue(m.AttackerId, out Enemy? e) ? (Vector2)_origin.ToUnity(e.Position) : me + Vector2.right;
            Vector2 dir = (me - from).normalized;
            _self.Animator.OnHit(dir.x, dir.y);
            EmitHit(me, dir);
            if (HitsShown <= 3)
            {
                Debug.Log($"[anim] попадание от #{m.AttackerId}: вспышка и отдача");
            }
        }

        void EmitDust(Vector3 tileCenter, Vector2 towardPlayer, float[] main)
        {
            Vector2 at = (Vector2)tileCenter + towardPlayer * 0.45f;
            _sparks.Emit(at, towardPlayer + Vector2.up * 0.8f, AnimParams.DigDust, AnimParams.DigDustSpeed, AnimParams.DigDustLife, 55f, true,
                main, main, AnimParams.DustLight);
        }

        void EmitHit(Vector2 at, Vector2 dir)
        {
            _sparks.Emit(at + new Vector2(0f, 0.35f), dir, AnimParams.HitSparks, AnimParams.HitSparkSpeed, AnimParams.HitSparkLife, 80f, false,
                AnimParams.StrikeFlash, AnimParams.SparkLight);
        }

        /// <summary>Пыль по объекту тайла: дерево — щепки, скала и стена — каменная крошка.</summary>
        public static float[] DustOf(ushort obj) =>
            ObjectId.IdOf(obj) == ObjectId.Tree ? AnimParams.DustWood : obj == ObjectId.Empty ? AnimParams.DustLight : AnimParams.DustStone;

        static WorldPos TileCenter(TileCoord t) =>
            new WorldPos(t.X * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2, t.Y * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2);
    }
}
