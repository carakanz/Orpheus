#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.InputSystem;

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

        // C-012: ось своего удара в фазе «удар» — отход доигрывает её, даже если нажатие буфера уже записало в ядро ось
        // следующего удара (MeleeAction.AimRaw).
        Vector2 _strikeAxis = Vector2.right;

        sealed class Enemy
        {
            public SwingActor Actor = null!;
            public WorldPos Position;
        }

        // E2-7: такт копания своего — по счёту кадров ядра (NetClient.DigProgress) и доле кадра с последнего счёта.
        const float FrameSec = 1f / ActionAnim.FramesPerSecond;
        readonly DigTrack _dig = new DigTrack(); // объект тайла с начала копания и часы удара по счёту кадров
        long _requestsSeen = -1;

        // E2-7: удар мечом чужих — фаза из снимка (Melee), доля — своим таймером от смены фазы.
        readonly Dictionary<int, RemoteMelee> _remoteMelee = new Dictionary<int, RemoteMelee>();

        struct RemoteMelee
        {
            public byte Phase;
            public byte Index;
            public int SeenFrame; // кадр Unity, в котором сущность была в снимке
            public float Age;
        }

        /// <summary>E2-7: ударов инструментом своего показано (отклик: дерево — встряска и щепки, камень — искры и крошка).</summary>
        public long ImpactsShown { get; private set; }

        /// <summary>E2-7: жестов стройки и наклонов подбора показано.</summary>
        public long BuildGestures { get; private set; }
        public long PickupGestures { get; private set; }

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

        /// <summary>
        /// Чужой персонаж поставлен на интерполированное место: ходьба, рывок — по скорости; E2-7: предмет в руке и удар мечом —
        /// по снимку (<see cref="SnapshotEntity.HeldItem"/>, <see cref="SnapshotEntity.Melee"/>). Копания чужих в снимке нет —
        /// рубки у них не видно (протокол не менялся, см. карточку E2-7).
        /// </summary>
        public void Remote(int id, Transform root, in SnapshotEntity e)
        {
            if (!_remotes.TryGetValue(id, out ActorRig? rig))
            {
                rig = new ActorRig(root);
                _remotes.Add(id, rig);
            }

            float dt = Time.deltaTime;
            Vector2 v = rig.MeasureVelocity(dt);
            rig.SetHeld(e.HeldItem);
            var s = new ActorInput { VelX = v.x, VelY = v.y, Held = rig.Held };
            RemoteSwing(id, e.Melee, dt, rig, ref s);
            if (v.magnitude > AnimParams.RemoteDashSpeed)
            {
                Vector2 d = v.normalized;
                s.Dashing = true;
                s.DashProgress = 0.5f;
                s.DashDirX = d.x;
                s.DashDirY = d.y;
            }

            Pose pose = rig.Animator.Step(dt, s);
            if (e.HealthPct == 0)
            {
                // E3-1: труп (сервер держит его 10,5 с) — падает от своего персонажа и растворяется; не мгновенно.
                _deadAge.TryGetValue(id, out float age);
                age = _deadAge.ContainsKey(id) ? age + dt : 0f;
                _deadAge[id] = age;
                bool right = root.position.x >= _g.Character.position.x;
                pose = DeathAnim.Apply(pose, age, right, dissolve: e.Kind == Orpheus.Core.Sim.SimEntity.KindEnemy);
            }
            else
            {
                _deadAge.Remove(id);
            }

            rig.Apply(pose);
        }

        // E3-1: сколько секунд показа сущность мертва (снимок: доля здоровья 0); свой — отдельно (−1 — жив).
        readonly Dictionary<int, float> _deadAge = new Dictionary<int, float>();
        float _selfDeadAge = -1f;

        /// <summary>E3-1: труп врага растворился — не рисовать (и тень).</summary>
        public bool Gone(int id, byte kind) => _deadAge.TryGetValue(id, out float age) && DeathAnim.Gone(age, kind == Orpheus.Core.Sim.SimEntity.KindEnemy);

        /// <summary>
        /// E3-1: урон по чужому существу (сообщение 37) — красная вспышка и отдача от атакующего (своё попадание по себе — как
        /// раньше, «удар решён»).
        /// </summary>
        public void OnRemoteHit(int victim, Vector3 victimAt, Vector3 attackerAt)
        {
            if (_remotes.TryGetValue(victim, out ActorRig? rig))
            {
                Vector2 dir = ((Vector2)(victimAt - attackerAt)).normalized;
                if (dir.sqrMagnitude < 0.01f)
                {
                    dir = Vector2.right;
                }

                rig.Animator.OnHit(dir.x, dir.y);
            }
        }

        /// <summary>
        /// E2-7: удар мечом чужого по снимку — фаза (2 бита) и номер в серии; доля фазы считается от её смены по длительностям
        /// <see cref="MeleeAction"/> (снимок идёт реже кадров — доля своим таймером). Ось — куда смотрит.
        /// </summary>
        void RemoteSwing(int id, byte melee, float dt, ActorRig rig, ref ActorInput s)
        {
            byte phase = (byte)(melee & 3);
            byte index = (byte)(melee >> 2);
            _remoteMelee.TryGetValue(id, out RemoteMelee m);
            if (m.Phase != phase || m.Index != index)
            {
                m = new RemoteMelee { Phase = phase, Index = index, Age = 0f };
            }
            else
            {
                m.Age += dt;
            }

            m.SeenFrame = Time.frameCount;
            _remoteMelee[id] = m;
            if (phase == MeleeAction.PhaseIdle)
            {
                return;
            }

            int frames = phase == MeleeAction.PhaseWindup ? MeleeAction.WindupFrames
                : phase == MeleeAction.PhaseStrike ? MeleeAction.StrikeFrames
                : index == MeleeAction.SeriesSize - 1 ? MeleeAction.LastRecoverFrames : MeleeAction.RecoverFrames;
            s.Swing = (SwingPhase)phase;
            s.SwingProgress = Mathf.Clamp01(m.Age / (frames * FrameSec));
            s.SwingDirX = rig.Animator.FacingRight ? 1f : -1f;
            s.SwingDirY = 0f;
        }

        readonly List<int> _gone = new List<int>();

        /// <summary>E2-7: сущность ушла из снимка (не появлялась больше 60 кадров) — её фаза удара забывается.</summary>
        void ForgetGoneMelee()
        {
            int now = Time.frameCount;
            _gone.Clear();
            foreach (KeyValuePair<int, RemoteMelee> kv in _remoteMelee)
            {
                if (now - kv.Value.SeenFrame > 60) // больше секунды без снимка с ней — ушла из обзора
                {
                    _gone.Add(kv.Key);
                }
            }

            for (int i = 0; i < _gone.Count; i++)
            {
                _remoteMelee.Remove(_gone[i]);
            }
        }

        /// <summary>Фаз удара чужих в памяти (для проверки очистки).</summary>
        public int RemoteMeleeTracked => _remoteMelee.Count;

        /// <summary>
        /// E2-7: кучу подобрали (сообщение «кучи нет» с поднявшим): свой (<paramref name="self"/>) или чужой
        /// (<paramref name="takerId"/>) делает короткий наклон к ней.
        /// </summary>
        public void OnPickup(bool self, int takerId, Vector3 pileAt)
        {
            ActorRig? rig = self ? _self : _remotes.TryGetValue(takerId, out ActorRig? r) ? r : null;
            if (rig == null)
            {
                return;
            }

            Vector2 d = (Vector2)(pileAt - rig.Root.position);
            if (d.sqrMagnitude > 9f)
            {
                d = Vector2.zero; // куча неизвестна (позиция по умолчанию) — наклон туда, куда смотрит
            }

            rig.Animator.OnPickup(d.x, d.y);
            PickupGestures++;
        }

        /// <summary>Точка под курсором мыши в координатах Unity (нет мыши — null).</summary>
        Vector3? CursorWorld()
        {
            Mouse? mouse = Mouse.current;
            Camera? cam = _g.Camera;
            if (mouse == null || cam == null)
            {
                return null;
            }

            Vector2 screen = mouse.position.ReadValue();
            return cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -GameBoot.CameraOffset.z));
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

            // Свой удар (1-004): фаза из предсказания — MeleeAction шагает в каждом отправленном кадре; ось — прицел (AimRaw:
            // в замахе с удержанием идёт за мышью, C-012). Сектор своего удара не рисуется (слово Автора 15.11, C-012).
            SwingPhase selfPhase = SwingPhase.None;
            float selfQ = 0f;
            var selfAxis = Vector2.zero;
            MeleeAction mel = pred.Melee;
            if (mel.Active)
            {
                AimMath.AxisOf(mel.AimRaw, out float ax, out float ay);
                if (mel.Phase == MeleeAction.PhaseRecover)
                {
                    selfAxis = _strikeAxis;
                }
                else
                {
                    selfAxis = new Vector2(ax, ay);
                    _strikeAxis = selfAxis;
                }
                int total = mel.Phase == MeleeAction.PhaseWindup ? MeleeAction.WindupFrames
                    : mel.Phase == MeleeAction.PhaseStrike ? MeleeAction.StrikeFrames
                    : mel.Index == MeleeAction.SeriesSize - 1 ? MeleeAction.LastRecoverFrames : MeleeAction.RecoverFrames;
                selfPhase = (SwingPhase)mel.Phase; // значения SwingPhase совпадают с фазами MeleeAction (1…3)
                selfQ = 1f - mel.FramesLeft / (float)total;
                s.Swing = selfPhase;
                s.SwingProgress = selfQ;
                s.SwingDirX = selfAxis.x;
                s.SwingDirY = selfAxis.y;
            }

            // E2-7: предмет в руке — по своему счёту (ячейка последнего кадра).
            byte held = pred.HeldItem;
            _self.SetHeld(held);
            s.Held = _self.Held;

            Vector3 digAt = Vector3.zero;
            float[] dust = AnimParams.DustLight;
            ushort digObj = ObjectId.Empty;
            bool digStarted = false;
            if (_client.Digging)
            {
                TileCoord t = _client.DigTile;
                digAt = _origin.ToUnity(TileCenter(t));
                Vector2 d = ((Vector2)(digAt - _g.Character.position)).normalized;
                s.Digging = true;
                s.DigDirX = d.x;
                s.DigDirY = d.y;
                s.DigPeriod = ProceduralAnimator.DigPeriodFor(_client.DigFrames);
                // E2-7: такт удара — по кадрам копания ядра (DigProgress из DigFrames): удар — в кадр, когда счёт дошёл до конца
                // замаха, последний — в кадр снятия объекта (DigTrack.ClockSec; между счётами — доля кадра, но не за границу).
                // Объект тайла — запомненный в начале копания: к последнему удару предсказание его уже сняло.
                s.ActionSec = _dig.Update(t.X, t.Y, _world.GetObject(t), _client.DigProgress, _client.DigFrames, _client.DigPredicted, dt,
                    out digStarted);
                dust = DustOf(_dig.Object);
                s.Action = ActionAnim.DigActionOf(held);
                s.ActionFrames = _client.DigFrames;
            }
            else
            {
                _dig.Stop();
            }

            // E2-7: постановка постройки — запрос правки ушёл с постройкой в руке (не начало копания): жест к курсору.
            long req = _client.EditRequestsSent;
            if (_requestsSeen >= 0 && req > _requestsSeen && !digStarted && BuildHand.IsBuildItem(held))
            {
                Vector3? cur = CursorWorld();
                Vector2 to = cur.HasValue ? (Vector2)(cur.Value - _g.Character.position) : new Vector2(_self.Animator.FacingRight ? 1f : -1f, 0f);
                _self.Animator.OnBuild(to.x, to.y);
                BuildGestures++;
            }

            _requestsSeen = req;

            Pose p = _self.Animator.Step(dt, s);
            if (pred.Dead)
            {
                // E3-1: свой персонаж падает и лежит до возрождения (не растворяется).
                _selfDeadAge = _selfDeadAge < 0f ? 0f : _selfDeadAge + dt;
                p = DeathAnim.Apply(p, _selfDeadAge, !_self.Animator.FacingRight, dissolve: false);
            }
            else
            {
                _selfDeadAge = -1f;
            }

            _self.Apply(p);
            if (p.DigPokeNow)
            {
                if (s.Action == ActionKind.Chop || s.Action == ActionKind.Mine || s.Action == ActionKind.Hammer)
                {
                    // Вид отклика — по запомненному объекту (дерево/камень), даже если его уже сняло предсказание.
                    Emit(_sparks, digAt, new Vector2(-s.DigDirX, -s.DigDirY), _dig.Look);
                    ImpactsShown++;
                }
                else
                {
                    EmitDust(digAt, new Vector2(-s.DigDirX, -s.DigDirY), dust);
                }

                // E1-11: удар по дереву — встряска и листья. На последнем ударе дерева уже нет (предсказание сняло его в этот же
                // кадр): облачко листьев срубленного даёт WorldView.Fill → LifeFx.TreeGone по тому же изменению тайла — не дублируем.
                if (_client.Digging && _world.GetObject(_client.DigTile) != ObjectId.Empty)
                {
                    LifeFx.HitTile(_client.DigTile, new Vector2(s.DigDirX, s.DigDirY));
                }
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
            ForgetGoneMelee();
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

        /// <summary>
        /// E2-7: отклик удара инструментом по тайлу. Дерево — щепки (листья и встряску даёт <c>LifeFx.HitTile</c>); камень,
        /// руда, стена — искры (короткие, тормозят) и крошка камня (падает); пустой тайл — пыль. Общий пул <see cref="Sparks"/>.
        /// </summary>
        public static void Emit(Sparks sparks, Vector3 tileCenter, Vector2 towardPlayer, ImpactLook look)
        {
            Vector2 at = (Vector2)tileCenter + towardPlayer * 0.42f;
            if (look == ImpactLook.Dust)
            {
                sparks.Emit(at, towardPlayer + Vector2.up * 0.8f, AnimParams.DigDust, AnimParams.DigDustSpeed, AnimParams.DigDustLife, 55f, true,
                    AnimParams.DustLight);
                return;
            }

            if (look == ImpactLook.Wood)
            {
                sparks.Emit(at + Vector2.up * 0.25f, towardPlayer + Vector2.up * 1.1f, AnimParams.WoodChips, AnimParams.DigDustSpeed * 1.2f,
                    AnimParams.DigDustLife, 50f, true, AnimParams.DustWood, AnimParams.DustWood, AnimParams.DustLight);
                return;
            }

            sparks.Emit(at, towardPlayer + Vector2.up * 0.4f, AnimParams.StoneSparks, AnimParams.StoneSparkSpeed, AnimParams.StoneSparkLife, 70f, false,
                AnimParams.StrikeFlash, AnimParams.SparkLight);
            sparks.Emit(at, towardPlayer + Vector2.up * 0.9f, AnimParams.StoneCrumbs, AnimParams.DigDustSpeed, AnimParams.DigDustLife, 45f, true,
                AnimParams.DustStone, AnimParams.DustStone, AnimParams.DustLight);
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
