using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот <c>build_room</c> (docs/07 11.3; приёмка `08` 3.1 1.5; карточка 1-026): добывает камень и древесину,
    /// строит комнату 5 × 5 — пол внутри 3 × 3, стены по периметру, дверь в центре южной стены — входит и закрывает
    /// дверь, ждёт рассвета. Тем же протоколом, что игрок: «поставить» линией, «копать: начало» с кнопкой в кадрах
    /// ввода, предсказание мира и сумки делает клиент (<see cref="NetClient.Edits"/>), бот только решает.
    /// <para>
    /// Площадка — центр на тайле появления (зона появления 1-009 чистая: диск 4 тайла, деревья кольцом 6–8): периметр
    /// стены стоят прямо на землю (<c>CheckPlaceTile</c> разрешает стену на сушу без пола). Добыча — ближайшие скалы
    /// (кирка) и деревья (топор) в пределах <see cref="GatherRadius"/> тайлов от площадки; ход — жадный по осям,
    /// помеха на курсе — скалывается (как <see cref="DigLineBot"/>; пути клиента не строит: поиск пути на сервере).
    /// </para>
    /// <para>
    /// E2-11: постройки — предметы. После добычи и расчистки бот делает руками (рецепты, <see cref="CraftDesk"/>) 15 каменных
    /// стен, деревянный пол (E2-15: 5 × 5 под стенами, 100 ячеек) и дверь, кладёт их на панель (<see cref="BuildHand.StockMove"/>) и ставит из руки через
    /// <see cref="BuildHand"/>; сервер снимает по предмету на тайл (клиент предсказывает). Счёт целей — по рецептам:
    /// 15 стен × 2 камня = 30 камня, E2-15: пол 25 древесины + дверь 6 = 31; после двери — факел из стартового набора у западной стены со сдвигом 0,5; добыча ведётся до <see cref="TargetStone"/>/<see cref="TargetWood"/>
    /// (перелёт допустим: добыча идёт объектами, не разовыми порциями).
    /// </para>
    /// </summary>
    public sealed class BuildRoomBot
    {
        public const string Name = "build_room";

        /// <summary>
        /// Добыть до перехода к стройке: 34 камня (E2-12: 17 рецептов по 2 камня = 34 ячейки тонкой стены) и 34 древесины
        /// (E2-15: пол 5 × 5 под стенами — 25 рецептов по 4 ячейки + дверь 6 + 3 припаса; деревьев в радиусе 32 у старта — 13, 39 древесины).
        /// </summary>
        public const int TargetStone = 34;
        public const int TargetWood = 34;

        /// <summary>Сторона комнаты (тайлов): периметр стен, внутри пол 3 × 3.</summary>
        public const int RoomSize = 5;

        /// <summary>
        /// Здоровье своего персонажа целое, максимум уровня 0 — 100 (`02` 8.1, [док]: карточка говорит «1-го уровня»,
        /// точное число ждёт ответа Автора; до ответа считаем 100, как в <c>SelfState.HealthBits</c>).</summary>
        public const int MaxHealth = 100;

        /// <summary>Радиус поиска скал и деревьев от площадки, тайлов.</summary>
        public const int GatherRadius = 32;

        /// <summary>Лимит тиков сценария (~14 мин): после — <see cref="Phase"/> в <see cref="Phase.Fail"/> (журнал).
        /// В мире генератора v1 добыча на радиусе 32 т занимает 6–7 мин реального времени, ночь — 780-я секунда.</summary>
        public const int MaxTicks = 25_000;

        /// <summary>Тактов ожидания клиентских чанков перед «нет целей добычи» (снапшоты 30 Гц, RTT).</summary>
        public const int WarmupTicks = 120;

        /// <summary>Этапы: добыча → расчистка площадки → изготовление построек (E2-11) → пол → стены → дверь → вход → закрытие → рассвет.</summary>
        public enum Phase
        {
            Gather, Clear, Craft, Floor, Walls, Door, Furnish, Enter, Close, WaitDawn, Done, Fail
        }

        private readonly MapShape _shape;
        private readonly TileCoord _center; // центр комнаты = тайл появления

        // Запас 1/4 тайла, как у копания DigLineBot: позиция из снимка сервером уже обогнана.
        private static int PlaceReach => EditRules.PlaceRadiusTiles * Torus.SubTilesPerTile;

        private readonly Dictionary<ushort, ushort> _reqObj = new(); // запрос правки → объект для счётчиков
        private readonly Dictionary<TileCoord, int> _skipUntil = new(); // тайл → кадр, до которого не пробуем (отказ)

        private TileCoord _digTile;
        private int _digSlot = byte.MaxValue;
        private WorldPos _lastPos;
        private int _stuckFrames;
        private int _frame;
        private int _prevHealth = -1; // здоровье прошлого кадра — детект нового попадания
        private bool _dead; // свой персонаж мёртв (здоровье 0 или нет в снимке) — смерть считается один раз
        private Phase _prevStage = Phase.Gather;
        private int _closeOpenTicks; // Close: тактов дверь открыта (F отвергается — враг на тайле, выталкивание блокировано)
        private int _enterStuckTicks; // Enter: тактов позиция не меняется (враг в проёме не пускает)
        private int _wallsFrom; // индекс следующей стены в обходе периметра
        private int _floorRow;  // индекс следующей ряда пола

        /// <summary>Текущий этап.</summary>
        public Phase Stage { get; private set; } = Phase.Gather;

        /// <summary>Причина <see cref="Phase.Fail"/> (журнал приёмки), null пока идёт сценарий.</summary>
        public string? FailReason { get; private set; }

        /// <summary>Кадров сценария (вход → текущий).</summary>
        public int Ticks { get; private set; }

        /// <summary>Кадров заняла добыча (на переходе к стройке).</summary>
        public int GatherTicks { get; private set; }

        /// <summary>Кадров заняла стройка (с момента закрытия двери).</summary>
        public int BuildTicks { get; private set; }

        /// <summary>Скал скопано (ответ «принято»), деревьев срублено.</summary>
        public int RocksDug { get; private set; }
        public int TreesChopped { get; private set; }

        /// <summary>Отказов правки всего (сумка, досягаемость, «нельзя»).</summary>
        public int Refused { get; private set; }

        /// <summary>Камень и древесина в сумке на момент конца добычи.</summary>
        public int StoneAtGatherEnd { get; private set; }
        public int WoodAtGatherEnd { get; private set; }

        /// <summary>Полов, стен и дверей видно в мире клиента (подтверждённые).</summary>
        public int FloorsPlaced { get; private set; }
        public int WallsPlaced { get; private set; }
        public bool DoorPlaced { get; private set; }

        /// <summary>Дверь закрыта (запись тайла без бита открытости) и бот внутри.</summary>
        public bool DoorClosed { get; private set; }

        /// <summary>Кадров, в которых здоровье своё падало при закрытой двери и боте внутри (новые попадания
        /// за закрытой дверью; удары, полученные снаружи до закрытия, — не «урон за дверью»).</summary>
        public int DamageThroughDoor { get; private set; }

        /// <summary>Диагностика попаданий за дверью: на каждое — такт, здоровье, состояние двери и сущности
        /// в радиусе 3 тайла (UDP Big 13.11: 6 кадров — ищем источник).</summary>
        public string DamageDetail { get; private set; } = "";

        /// <summary>Кадров удара мечом, отданных ботом (самооборона в Close/Enter/WaitDawn, 13.11).</summary>
        public int AttacksIssued { get; private set; }

        /// <summary>Ячейка стартового деревянного меча (<see cref="Inventory.StartingKit"/> — 3).</summary>
        public const int SwordSlot = 3;

        /// <summary>Смертей своё (здоровье 0 или пропажа <see cref="SelfState.Present"/>; каждая — один раз).</summary>
        public int Deaths { get; private set; }

        /// <summary>Дожило до рассвета при закрытой двери.</summary>
        public bool DawnReached { get; private set; }

        /// <summary>Тайл двери (центр южной стены).</summary>
        public TileCoord DoorTile => new TileCoord(_center.X, _center.Y + RoomSize / 2);

        public BuildRoomBot(MapShape shape, WorldPos spawn)
        {
            _shape = shape;
            _center = Torus.TileOf(shape, spawn);
            _lastPos = spawn;
        }

        /// <summary>Ответ сервера на правку: счётчики добычи и стройки, cooldowns по отказам (образец <see cref="DigLineBot"/>).</summary>
        /// <remarks>
        /// Счётчики стройки (<see cref="_floorRow"/>, <see cref="_wallsFrom"/>) идут ТОЛЬКО по «принято»: на отправке
        /// индекс уползал за пределы плана при отказах (ряд 21, стена 300 — 1-026). По одному незакрытому запросу
        /// на фазу: <see cref="_floorReq"/>, <see cref="_wallReq"/>.
        /// </remarks>
        public void OnReply(TileReplyMessage m)
        {
            if (!_reqObj.Remove(m.RequestId, out ushort obj))
            {
                return; // не наша правка (кот-и-мышь клиента)
            }

            _reqTile.Remove(m.RequestId);
            if (m.Reply == EditReply.Accepted && (m.Mask & StrikeMessage.ReplySwingBit) != 0)
            {
                return; // C-028: взмах кирки — снятые скалы приходят «взмахом» (CountBroken в Next)
            }

            if (m.Reply != EditReply.Accepted)
            {
                Refused++;
                _refused[m.Reply] = _refused.TryGetValue(m.Reply, out int n) ? n + 1 : 1;
                _armObj = 0; // отказ: следующий запрос той же цели заново «взводится» кадром с молотом
                if (obj == BuildDefs.WoodFloorKey)
                {
                    _floorReq = 0;
                    _skipUntil[_center] = _frame + RefusalCooldownFrames;
                }
                else if (obj == ObjectId.StoneWall)
                {
                    _wallReq = 0;
                }

                return;
            }

            switch (obj)
            {
                case ObjectId.Rock:
                case ObjectId.Granite:
                    RocksDug++;
                    break;
                case ObjectId.Tree:
                    TreesChopped++;
                    break;
                case ObjectId.StoneWall:
                    _wallReq = 0; // E2-12: тайл стены — 2–3 ячейки, к следующему тайлу — когда этот полон (Walls)
                    break;
                case ObjectId.Door:
                    DoorPlaced = true;
                    break;
                case BuildDefs.WoodFloorKey:
                    _floorReq = 0;
                    _floorRow++;
                    break;
            }
        }

        /// <summary>Кадров ждём после отказа стройки, пока та же цель в cooldown (повторных запросов не шлём).</summary>
        public const int RefusalCooldownFrames = 90;

        private readonly Dictionary<ushort, TileCoord> _reqTile = new(); // запрос → тайл (стена) для cooldown отказа
        private ushort _floorReq;  // незакрытый запрос ряда пола
        private ushort _wallReq;   // незакрытый запрос стены

        private readonly Dictionary<EditReply, int> _refused = new();

        /// <summary>Отказы правки по кодам (<see cref="EditReply"/>) — диагноз приёмки.</summary>
        public string RefusalsText => string.Join(", ", _refused.Select(kv => kv.Key + " ×" + kv.Value));

        private bool? _doorSeen; // состояние двери в мире клиента прошлым тиком (диагностика P-003: D08_3_1 «закрыта нет»)
        private string _doorTrace = "";

        /// <summary>Переходы состояния двери (наблюдение клиента): [тN:откр/закр/нет этап] — диагноз, закрывается ли
        /// дверь вовсе, или открыта весь прогон, или хлопает (автооткрытие/отбрасывание).</summary>
        public string DoorTrace => _doorTrace.Length == 0 ? "переходов нет" : _doorTrace;

        /// <summary>Кадр ввода (30 Гц): копать, идти, ставить или стоять. <paramref name="pos"/> — позиция своя
        /// (предсказание NetBot или снимок), <paramref name="nowUs"/> — часы NetBot (для часов мира, 1-014).</summary>
        public InputFrame Next(NetClient client, WorldPos pos, long nowUs)
        {
            Ticks++;
            _frame++;
            _broken.Clear();
            client.DrainBrokenByMe(_broken); // C-028: скалы, снятые своими взмахами
            foreach ((TileCoord _, ushort raw) in _broken)
            {
                if (ObjectId.IdOf(raw) == ObjectId.Rock || ObjectId.IdOf(raw) == ObjectId.Granite)
                {
                    RocksDug++;
                }
            }

            ClientWorld? world = client.World;
            if (world == null || !client.HasSnapshot || Stage is Phase.Done or Phase.Fail)
            {
                return InputFrame.None;
            }

            if (Ticks > MaxTicks)
            {
                // Этап пишется ДО перехода в Fail: иначе «этап Fail» — самоссылка, фаза терялась (P-003).
                FailReason = $"лимит {MaxTicks} тиков: этап {Stage}, полов {FloorsPlaced}/{FloorTiles}, стен {WallsPlaced}/15, дверь {(DoorPlaced ? "поставлена" : "нет")}";
                Stage = Phase.Fail;
                return InputFrame.None;
            }

            SelfState self = client.Latest.Self;
            if (!self.Present || self.Health == 0)
            {
                // C-004: мёртвый персонаж в снимке ЕСТЬ (Present), здоровье 0 — раньше смерть не замечалась
                // («смертей 0» при убитом строителе), а сценарий продолжал строить от имени трупа.
                if (!_dead)
                {
                    _dead = true;
                    Deaths++;
                }

                _prevHealth = -1;
                return InputFrame.None; // возрождение — на стороне сервера (1-009)
            }

            if (_dead)
            {
                // Возродился у точки появления мира, а не в комнате: «закрыта и бот внутри» больше не верно —
                // возвращаемся ко входу (этапы до двери продолжаются как шли: цели считаются от позиции).
                _dead = false;
                if (Stage is Phase.Enter or Phase.Close or Phase.WaitDawn)
                {
                    Stage = Phase.Enter;
                    DoorClosed = false;
                }
            }

            if (Stage >= Phase.WaitDawn && DoorClosed && _prevHealth >= 0 && self.Health < _prevHealth)
            {
                DamageThroughDoor++; // новое попадание за закрытой дверью — провал приёмки 1.5
                if (DamageDetail.Length < 900)
                {
                    DamageDetail += $"[т{Ticks} hp{_prevHealth}->{self.Health} {(world.World.TryGetBuild(DoorTile, out BuildRecord drg) && drg.IsDoorOpen ? "дверьОткр" : "дверьЗакр")} {NearbyText(client, pos)}] ";
                }
            }
            _prevHealth = self.Health;

            // Диагностика P-003 (D08_3_1 «закрыта нет»): каждое состояние двери в мире клиента — в трассу.
            bool? doorOpen = world.World.TryGetBuild(DoorTile, out BuildRecord drec) ? drec.IsDoorOpen : (bool?)null;
            if (doorOpen != _doorSeen)
            {
                if (_doorTrace.Length < 900)
                {
                    _doorTrace += $"[т{Ticks}:{(doorOpen == null ? "нет" : doorOpen.Value ? "откр" : "закр")} {Stage}] ";
                }

                _doorSeen = doorOpen;
            }

            // C-004: самооборона и на добыче/стройке. Сутки ×30 — ночь приходит посреди стройки; без меча
            // разбойник забивал строителя за 9 ударов (трасса сервера, такты 1028–1292), и «комнату» достраивал труп.
            // Удар важнее копания: на мече прогресс копания стоит (1-002) и продолжается после.
            if (Stage < Phase.Enter)
            {
                InputFrame? defend = ThreatFrame(client, pos);
                if (defend != null)
                {
                    return defend.Value;
                }
            }

            if (client.Digging)
            {
                return new InputFrame(Input8.None, InputButtons.Dig, 0, (byte)_digSlot);
            }

            if (Stage != _prevStage)
            {
                _prevStage = Stage;
                _closeOpenTicks = 0;
                _enterStuckTicks = 0;
            }

            InputFrame frame = Stage switch
            {
                Phase.Gather => Gather(client, world, pos),
                Phase.Clear => Clear(client, world, pos),
                Phase.Craft => CraftItems(client, world, pos),
                Phase.Floor => Floor(client, world, pos),
                Phase.Walls => Walls(client, world, pos),
                Phase.Door => Door(client, world, pos),
                Phase.Furnish => Furnish(client, world, pos),
                Phase.Enter => Enter(client, world, pos),
                Phase.Close => Close(client, world, pos),
                Phase.WaitDawn => WaitDawn(client, pos, nowUs),
                _ => InputFrame.None,
            };
            return frame;
        }

        // ------------------------------------------------------------------ добыча

        private BotPiles? _piles;

        /// <summary>Куч добычи подобрано походом к ним (E2-1).</summary>
        public int PilesCollected => _piles?.Collected ?? 0;

        private InputFrame Gather(NetClient client, ClientWorld world, WorldPos pos)
        {
            int stone = client.Inv.CountAt(Material.Stone);
            int wood = client.Inv.CountAt(Material.Wood);
            if (stone >= TargetStone && wood >= TargetWood)
            {
                GatherTicks = Ticks;
                StoneAtGatherEnd = stone;
                WoodAtGatherEnd = wood;
                Stage = Phase.Clear;
                return InputFrame.None;
            }

            // E2-1: добыча лежит кучей на тайле снятого объекта — подойти и подобрать, прежде чем искать следующую цель.
            _piles ??= new BotPiles(_shape);
            if (_piles.TrySeek(client, pos, item => item == Items.Stone || item == Items.Wood, out Input8 toPile))
            {
                if (toPile != Input8.None && TryDigObstacle(client, world, pos, toPile))
                {
                    return new InputFrame(Input8.None, InputButtons.Dig, 0, (byte)_digSlot);
                }

                return WalkFrames(pos, toPile);
            }

            TileCoord? target = FindGatherTarget(client, world, pos, stone < TargetStone, wood < TargetWood);
            if (target == null)
            {
                if (Ticks < WarmupTicks)
                {
                    return InputFrame.None; // клиентские чанки ещё не доехали (снапшоты с 30 Гц) — ждём, не проваливаем сценарий
                }

                Stage = Phase.Fail;
                FailReason = $"нет целей добычи радиуса {GatherRadius} камня {stone}, древесины {wood}";
                return InputFrame.None;
            }

            return DigOrWalk(client, world, pos, target.Value);
        }

        /// <summary>Ближайшая скала (нужен камень) или дерево (нужна древесина) в радиусе площадки; null — цель кончилась.</summary>
        private TileCoord? FindGatherTarget(NetClient client, ClientWorld world, WorldPos pos, bool needStone, bool needWood)
        {
            TileCoord best = default;
            int bestCost = int.MaxValue;
            int half = GatherRadius;
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy));
                    ushort obj = world.World.GetObject(t);
                    Material mat;
                    switch (obj)
                    {
                        case ObjectId.Rock:
                        case ObjectId.Granite:
                            mat = Material.Stone;
                            break;
                        case ObjectId.Tree:
                            mat = Material.Wood;
                            break;
                        default:
                            continue; // не добыча
                    }

                    if ((mat == Material.Stone && !needStone) || (mat == Material.Wood && !needWood)
                        || _skipUntil.TryGetValue(t, out int until) && _frame < until
                        || ToolSlotFor(client, obj) == byte.MaxValue)
                    {
                        continue;
                    }

                    int cost = Cheb(Torus.TileOf(_shape, pos), t);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = t;
                    }
                }
            }

            return bestCost == int.MaxValue ? null : best;
        }

        /// <summary>Копать цель, если в досягаемости; иначе идти к ней, скалывая помеху на курсе.</summary>
        private InputFrame DigOrWalk(NetClient client, ClientWorld world, WorldPos pos, TileCoord t)
        {
            if (!world.IsPredicted(t) && EditRules.InReach(_shape, pos, t, EditRules.DigReachUnits - Torus.SubTilesPerTile / 4))
            {
                ushort obj = world.World.GetObject(t);
                byte slot = ToolSlotFor(client, obj);
                if (slot != byte.MaxValue && !Armed(slot))
                {
                    return new InputFrame(Input8.None, 0, 0, slot); // C-028: сначала инструмент в руки (сервер решает по нему)
                }

                if (slot != byte.MaxValue)
                {
                    ushort id = client.StartDig(t);
                    if (id != 0)
                    {
                        _digTile = t;
                        _digSlot = slot;
                        _reqObj[id] = obj;
                        return new InputFrame(Input8.None, InputButtons.Dig, 0, slot);
                    }
                }
            }

            Input8 dir = StepDir(pos, TileCenter(t));
            if (TryDigObstacle(client, world, pos, dir))
            {
                return new InputFrame(Input8.None, InputButtons.Dig, 0, (byte)_digSlot);
            }

            return WalkFrames(pos, dir);
        }

        private readonly List<(TileCoord Tile, ushort Obj)> _broken = new List<(TileCoord Tile, ushort Obj)>();
        private int _digToolSlot = -1;
        private int _digToolFrame;

        /// <summary>
        /// C-028: инструмент ячейки <paramref name="slot"/> в руках не меньше 6 кадров — запрос копания пойдёт с ним (сервер решает
        /// «взмах или копание» и «нужен инструмент» по предмету в руках такта).
        /// </summary>
        private bool Armed(byte slot)
        {
            if (_digToolSlot != slot)
            {
                _digToolSlot = slot;
                _digToolFrame = _frame;
                return false;
            }

            return _frame - _digToolFrame >= 6;
        }

        /// <summary>Скалывает природное, что стоит на курсе (круг персонажа задел тайл на полтайла вперёд).</summary>
        private bool TryDigObstacle(NetClient client, ClientWorld world, WorldPos pos, Input8 dir)
        {
            var ahead = new WorldPos(pos.X + dir.DX * Torus.SubTilesPerTile / 2, pos.Y + dir.DY * Torus.SubTilesPerTile / 2);
            TileCoord me = Torus.TileOf(_shape, pos);
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(me.X + ox, me.Y + oy));
                    if ((ox == 0 && oy == 0) || !Collision.CircleOverlapsTile(_shape, ahead, t)
                        || world.IsPredicted(t)
                        || !EditRules.InReach(_shape, pos, t, EditRules.DigReachUnits - Torus.SubTilesPerTile / 4))
                    {
                        continue;
                    }

                    ushort obj = world.World.GetObject(t);
                    if (!ObjectId.IsSolid(obj) && !ObjectId.IsNatural(obj))
                    {
                        continue;
                    }

                    byte slot = ToolSlotFor(client, obj);
                    if (slot == byte.MaxValue)
                    {
                        continue;
                    }

                    if (!Armed(slot))
                    {
                        _digSlot = slot;
                        return true; // C-028: кадры с инструментом — до запроса
                    }

                    ushort id = client.StartDig(t);
                    if (id == 0)
                    {
                        continue;
                    }

                    _digTile = t;
                    _digSlot = slot;
                    _reqObj[id] = obj;
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ расчистка площадки

        private InputFrame Clear(NetClient client, ClientWorld world, WorldPos pos)
        {
            TileCoord? blocker = FindNaturalOnSite(world);
            if (blocker == null)
            {
                Stage = Phase.Craft; // E2-11: сначала сделать постройки-предметы рецептами, потом ставить
                return InputFrame.None;
            }

            return DigOrWalk(client, world, pos, blocker.Value);
        }

        /// <summary>Природный объект на площадке (периметр 5 × 5 и внутри): стена там не встанет, пол тоже.</summary>
        private TileCoord? FindNaturalOnSite(ClientWorld world)
        {
            int h = RoomSize / 2;
            for (int dy = -h; dy <= h; dy++)
            {
                for (int dx = -h; dx <= h; dx++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy));
                    ushort obj = world.World.GetObject(t);
                    if (obj != ObjectId.Empty && ObjectId.IsNatural(obj))
                    {
                        return t;
                    }
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ изготовление построек (E2-11)

        /// <summary>
        /// Сколько предметов-построек нужно комнате: пол 5 × 5 — под стенами тоже (E2-15: пол — нижний слой, предмет на ячейку
        /// 0,5, 4 на тайл), стены периметра без двери, дверь.
        /// </summary>
        public const int NeedFloors = 4 * RoomSize * RoomSize;

        /// <summary>Тайлов пола: вся площадка 5 × 5 (E2-15: стены ставятся поверх пола).</summary>
        public const int FloorTiles = RoomSize * RoomSize;
        /// <summary>
        /// Стен-ячеек на периметр (E2-12): тонкая стена — внешние половины тайлов периметра, по 2 ячейки на тайл стороны
        /// и 3 на угол; тайл двери — дверь (пара ячеек снизу).
        /// </summary>
        public const int NeedWalls = 4 * 3 + 2 * (4 * (RoomSize - 2) - 1);
        public const int NeedDoors = 1;

        private CraftDesk? _desk;
        private int _craftFrame = -1000;

        /// <summary>Заявок изготовления построек отправлено (E2-11).</summary>
        public int CraftRequests { get; private set; }

        /// <summary>Кадров заняло изготовление построек.</summary>
        public int CraftTicks { get; private set; }

        /// <summary>
        /// E2-11: постройки — предметы. Бот делает пол, стены и дверь руками (рецепты <see cref="RecipeTable"/>, тот же путь, что
        /// у окна изготовления, — <see cref="CraftDesk"/>), пока в сумке нет полного набора, потом ставит их из руки.
        /// </summary>
        private InputFrame CraftItems(NetClient client, ClientWorld world, WorldPos pos)
        {
            int floors = client.Inv.Count(Items.WoodFloor), walls = client.Inv.Count(Items.StoneWall), doors = client.Inv.Count(Items.Door);
            if (floors >= NeedFloors && walls >= NeedWalls && doors >= NeedDoors)
            {
                CraftTicks = Ticks - GatherTicks;
                Stage = Phase.Floor;
                return InputFrame.None;
            }

            InputFrame? fight = ThreatFrame(client, pos);
            if (fight != null)
            {
                return fight.Value;
            }

            // Очередь пуста и ответа давно нет — следующая заявка (первая или повтор после отказа).
            _desk ??= new CraftDesk(client);
            if (client.Craft.IsEmpty && _frame - _craftFrame > 45)
            {
                (byte recipe, int count) = walls < NeedWalls ? (RecipeTable.StoneWall, NeedWalls - walls)
                    : floors < NeedFloors ? (RecipeTable.WoodFloor, NeedFloors - floors)
                    : (RecipeTable.Door, NeedDoors - doors);
                int output = RecipeTable.Get(recipe).ResultCount; // E2-12: стена — 2 ячейки за рецепт
                count = (count + output - 1) / output;
                CraftLine line = CraftDesk.LineOf(recipe, client.Inv, client.Craft, world.World, pos, stock: client.NearbyStock);
                if (_desk.Make(line, count))
                {
                    _craftFrame = _frame;
                    CraftRequests++;
                }
                else if (_frame - _craftFrame > 900)
                {
                    Stage = Phase.Fail;
                    FailReason = $"изготовление: рецепт {recipe} × {count} — {line.State} (камень {client.Inv.Count(Items.Stone)}, древесина {client.Inv.Count(Items.Wood)})";
                }
            }

            return HammerFrame(client);
        }

        // ------------------------------------------------------------------ пол

        private InputFrame Floor(NetClient client, ClientWorld world, WorldPos pos)
        {
            CountFloors(world);
            if (FloorsPlaced >= FloorTiles)
            {
                Stage = Phase.Walls;
                return InputFrame.None;
            }

            // Стоим ровно в центре площадки (не «в радиусе 6 т»: с края ряд из трёх тайлов не влезает в радиус
            // постановки целиком — средние тайлы ряда сервер отвергает как OutOfReach).
            if (Cheb(Torus.TileOf(_shape, pos), _center) > 1)
            {
                return DigOrWalk(client, world, pos, _center);
            }

            if (_floorReq != 0)
            {
                return HammerFrame(client); // ждём ответ на ряд
            }

            if (_frame < _skipUntil.GetValueOrDefault(_center))
            {
                return HammerFrame(client); // ряд только что отвергнут
            }

            // E2-15: ряд пола — вся ширина площадки (под будущими стенами тоже), заявка — прямоугольник ячеек ряда тайлов.
            int h = RoomSize / 2;
            int y = _center.Y - h + FirstOpenFloorRow(world);
            var from = Torus.Wrap(_shape, new TileCoord(_center.X - h, y));
            var to = Torus.Wrap(_shape, new TileCoord(_center.X + h, y));
            if (!PlaceArmed(BuildDefs.WoodFloorKey, from))
            {
                return HammerFrame(client);
            }

            // E2-11: тот же путь, что у игрока, — BuildHand, пол из руки (ряд пола — линией, как ведение с зажатой ЛКМ).
            int floorSlot = BuildHand.SlotWith(client.Inv, Items.WoodFloor);
            if (floorSlot < 0)
            {
                return HammerFrame(client); // пол ещё не на панели — HandSlot переложит
            }

            ushort id = Build(client).PlaceLine(from, to, pos, client.Inv, floorSlot);
            if (id != 0)
            {
                _reqObj[id] = BuildDefs.WoodFloorKey;
                _floorReq = id;
            }

            return HammerFrame(client);
        }

        // Первый ряд площадки (0…RoomSize − 1), где пол лежит не целиком (E2-15: ряд с готовыми тайлами отвечал бы «занято»).
        private int FirstOpenFloorRow(ClientWorld world)
        {
            int h = RoomSize / 2;
            for (int dy = -h; dy <= h; dy++)
            {
                for (int dx = -h; dx <= h; dx++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy));
                    if (world.World.GetFloor(t) != FloorId.WoodFloor || BuildLayers.FloorCellsAt(world.World, t) != BuildCells.Full)
                    {
                        return dy + h;
                    }
                }
            }

            return _floorRow % RoomSize;
        }

        private void CountFloors(ClientWorld world)
        {
            int h = RoomSize / 2, n = 0;
            for (int dy = -h; dy <= h; dy++)
            {
                for (int dx = -h; dx <= h; dx++)
                {
                    var t = Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy));
                    if (world.World.GetFloor(t) == FloorId.WoodFloor && BuildLayers.FloorCellsAt(world.World, t) == BuildCells.Full)
                    {
                        n++;
                    }
                }
            }

            FloorsPlaced = n;
        }

        // ------------------------------------------------------------------ стены

        private InputFrame Walls(NetClient client, ClientWorld world, WorldPos pos)
        {
            CountWalls(world);
            if (WallsPlaced >= 4 * RoomSize - 5) // периметр 5 × 5 минус тайл двери
            {
                Stage = Phase.Door;
                return InputFrame.None;
            }

            if (_wallReq != 0)
            {
                return HammerFrame(client); // ждём ответ на стену
            }

            if (_wallsFrom >= 4 * RoomSize - 5)
            {
                _wallsFrom = 0; // полный обход сделан: идём второй круг по оставшимся
            }

            TileCoord wall = WallAt(_wallsFrom);
            if (WallDone(world, wall))
            {
                _wallsFrom++; // уже стоит
                return HammerFrame(client);
            }

            if (_frame < _skipUntil.GetValueOrDefault(wall))
            {
                _wallsFrom++; // отказ по этой стене: до cooldown пропускаем её, обход продолжится
                return HammerFrame(client);
            }

            if (InPlaceReach(pos, wall) && !EditRules.Overlaps(_shape, pos, wall))
            {
                if (PlaceArmed(ObjectId.StoneWall, wall))
                {
                    ushort id = PlaceWallCells(client, world, pos, wall);
                    if (id != 0)
                    {
                        _reqObj[id] = ObjectId.StoneWall;
                        _reqTile[id] = wall;
                        _wallReq = id;
                    }
                }

                return HammerFrame(client);
            }

            // Встаём в 1 тайле ОТ стены со стороны центра: стена в радиусе постановки (cheb 1…2), тело на неё
            // не заходит («занято существом»), путь внутри площадки свободен. Наружу не идём: обход по внешнему
            // контуру жадными шагами упирался в уже поставленные стены (1-026).
            return WalkHammer(client, pos, AnchorInside(wall));
        }

        /// <summary>Тайл в 1 тайле от стены к центру площадки — позиция постановки стены изнутри.</summary>
        private TileCoord AnchorInside(TileCoord wall)
        {
            int dx = Math.Sign(Torus.DeltaX(_shape, _center.X, wall.X)), dy = Math.Sign(Torus.DeltaY(_shape, _center.Y, wall.Y));
            return Torus.Wrap(_shape, new TileCoord(wall.X - dx, wall.Y - dy));
        }

        /// <summary>Обход периметра по порядку (без тайла двери), индекс → тайл стены.</summary>
        private TileCoord WallAt(int index)
        {
            int h = RoomSize / 2;
            for (int dy = -h; dy <= h; dy++)
            {
                for (int dx = -h; dx <= h; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != h)
                    {
                        continue; // не периметр
                    }

                    var t = Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy));
                    if (t == DoorTile)
                    {
                        continue;
                    }

                    if (index-- == 0)
                    {
                        return t;
                    }
                }
            }

            return DoorTile; // индекс за концом: к двери
        }

        private void CountWalls(ClientWorld world)
        {
            int h = RoomSize / 2, n = 0;
            for (int dy = -h; dy <= h; dy++)
            {
                for (int dx = -h; dx <= h; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != h)
                    {
                        continue;
                    }

                    var t = Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy));
                    if (t != DoorTile && WallDone(world, t))
                    {
                        n++;
                    }
                }
            }

            WallsPlaced = n;
        }

        /// <summary>Тайл в 1 тайле от стены наружу (от центра площадки) — позиция постановки стены.</summary>
        private TileCoord AnchorOutside(TileCoord wall)
        {
            int dx = Math.Sign(Torus.DeltaX(_shape, _center.X, wall.X)), dy = Math.Sign(Torus.DeltaY(_shape, _center.Y, wall.Y));
            return Torus.Wrap(_shape, new TileCoord(wall.X + dx, wall.Y + dy));
        }

        // ------------------------------------------------------------------ дверь, вход, закрытие

        /// <summary>Факел у стены поставлен (E2-15: мебель с шагом 0,5 — в свободной половине тайла стены).</summary>
        public bool TorchPlaced { get; private set; }

        /// <summary>Тайл и четверть СЗ ячейки факела у западной стены: восточная половина тайла стены и западная — соседнего.</summary>
        public TileCoord TorchTile => Torus.Wrap(_shape, new TileCoord(_center.X - RoomSize / 2, _center.Y));

        private int _furnishFrom = -1;

        /// <summary>
        /// E2-15: факел (стартовый набор) у западной стены изнутри — со сдвигом на полтайла: СЗ ячейка — восточная половина тайла
        /// стены (стена стоит в западной), якорь — соседний тайл. Факела нет или не встал за 3 с — дальше без него (комната и
        /// ночь важнее).
        /// </summary>
        private InputFrame Furnish(NetClient client, ClientWorld world, WorldPos pos)
        {
            if (_furnishFrom < 0)
            {
                _furnishFrom = _frame;
            }

            TileCoord anchor = Torus.Wrap(_shape, new TileCoord(TorchTile.X + 1, TorchTile.Y));
            if (ObjectId.IdOf(world.World.GetObject(anchor)) == ObjectId.Torch)
            {
                TorchPlaced = true;
            }

            if (TorchPlaced || client.Inv.Count(Items.Torch) == 0 || _frame - _furnishFrom > 180)
            {
                Stage = Phase.Enter;
                return InputFrame.None;
            }

            if (InPlaceReach(pos, TorchTile) && PlaceArmed(ObjectId.Torch, TorchTile) && !_reqObj.ContainsValue(ObjectId.Torch))
            {
                ushort id = ClickBuild(client, world, pos, ObjectId.Torch, TorchTile, 1);
                if (id != 0)
                {
                    _reqObj[id] = ObjectId.Torch;
                }
            }

            return HammerFrame(client);
        }

        private InputFrame Door(NetClient client, ClientWorld world, WorldPos pos)
        {
            TileCoord door = DoorTile;
            if (world.World.GetObject(door) == ObjectId.Door)
            {
                Stage = Phase.Furnish;
                return InputFrame.None;
            }

            if (InPlaceReach(pos, door))
            {
                if (PlaceArmed(ObjectId.Door, door))
                {
                    ushort id = ClickBuild(client, world, pos, ObjectId.Door, door, 2); // E2-12: ячейка снизу — дверь вдоль нижней стены
                    if (id != 0)
                    {
                        _reqObj[id] = ObjectId.Door;
                    }
                }

                return HammerFrame(client);
            }

            return DigOrWalk(client, world, pos, AnchorOutside(door));
        }

        private InputFrame Enter(NetClient client, ClientWorld world, WorldPos pos)
        {
            TileCoord me = Torus.TileOf(_shape, pos);
            if (Cheb(me, _center) <= 1)
            {
                Stage = Phase.Close;
                return InputFrame.None;
            }

            // Снаружи периметра и не перед дверью (C-004: возродились у точки появления мира) — обход стен:
            // над/под комнатой сначала вбок, потом по своему столбцу до ряда перед дверью, по нему — к двери.
            // Жадный шаг «прямо к центру» упирался бы в свою же стену.
            TileCoord door = DoorTile;
            TileCoord anchor = AnchorOutside(door);
            Int2 off = Torus.Delta(_shape, _center, me);
            int h = RoomSize / 2;
            if (Math.Max(Math.Abs(off.X), Math.Abs(off.Y)) > h && me != anchor)
            {
                TileCoord wp = anchor;
                if (off.Y <= h)
                {
                    wp = Math.Abs(off.X) <= h
                        ? Torus.Wrap(_shape, new TileCoord(_center.X + (off.X >= 0 ? h + 1 : -(h + 1)), me.Y))
                        : Torus.Wrap(_shape, new TileCoord(me.X, _center.Y + h + 1));
                }

                InputFrame? fight = ThreatFrame(client, pos);
                if (fight != null)
                {
                    return fight.Value;
                }

                // Копаем помехи (скалы, деревья) только вдали от стен: рядом с комнатой молот снёс бы свою же стену.
                return Math.Max(Math.Abs(off.X), Math.Abs(off.Y)) > h + 1
                    ? DigOrWalk(client, world, pos, wp)
                    : WalkFrames(pos, StepDir(pos, TileCenter(wp)));
            }

            // Враг на тайле проёма: ходьба упирается (выталкивание блокировано). F снаружи (chebyshev 1)
            // закрывает дверь и выталкивает врага; замок истёк — автооткрытие открывает проём, входим
            // (UDP Big, 1-026).
            if (pos == _lastPos && world.World.TryGetBuild(door, out BuildRecord rec) && rec.IsDoorOpen)
            {
                if (++_enterStuckTicks > 45 && Cheb(me, door) == 1)
                {
                    client.CloseDoor(door);
                    return HammerFrame(client);
                }
            }
            else
            {
                _enterStuckTicks = 0;
            }

            // Дверь ставится открытой (1-012); шаг на тайл двери её распахивает (автооткрытие шагом, `05` 2.1).
            // Идём в центр комнаты через проём, без копки: копать дверь — снести свою же дверь.
            // Враг сел на хвост в проёме — бьём мечом, пока не убрался (иначе в комнате драка при закрытой двери).
            InputFrame? hit = ThreatFrame(client, pos);
            if (hit != null)
            {
                return hit.Value;
            }

            Input8 dir = StepDir(pos, TileCenter(_center));
            return WalkFrames(pos, dir);
        }

        private InputFrame Close(NetClient client, ClientWorld world, WorldPos pos)
        {
            TileCoord door = DoorTile;
            if (!world.World.TryGetBuild(door, out BuildRecord rec))
            {
                Stage = Phase.Fail;
                FailReason = "нет записи двери на тайле проёма";
                return InputFrame.None;
            }

            if (!rec.IsDoorOpen)
            {
                if (!Inside(pos))
                {
                    Stage = Phase.Enter; // дверь закрылась, а мы снаружи (C-004): «закрыта» считается только изнутри
                    return InputFrame.None;
                }

                DoorClosed = true; // закрыли мы или автозакрытие (30 тактов свободы тайла, `05` 2.1)
                Stage = Phase.WaitDawn;
                BuildTicks = Ticks - GatherTicks;
                return InputFrame.None;
            }

            // F — радиус chebyshev ≤ 1 от двери, тайл под нажавшим — отказ: встаём на тайл комнаты у проёма.
            TileCoord near = Torus.Wrap(_shape, new TileCoord(door.X, door.Y - 1)); // C-004: было +1 — тайл СНАРУЖИ
            if (++_closeOpenTicks > 45)
            {
                // F отвергается: враг на тайле двери, выталкивание (E,S,W,N) блокировано тайлом под нами.
                // Уход в угол по диагонали — тайл у двери освобождается, врага выталкивает (UDP Big, 1-026).
                near = Torus.Wrap(_shape, new TileCoord(door.X + 1, door.Y - 1));
            }

            client.CloseDoor(door); // каждый кадр: закрытие + замок; запрос не занимает руки (1-028)

            // Враг в досягаемости удара (сел на проём, зашёл следом) — бьём мечом: удары за закрытой дверью
            // при живой атаке outside ломают приёмку 1.5 («урон за дверью 0 кадров»).
            InputFrame? hit = ThreatFrame(client, pos);
            if (hit != null)
            {
                return hit.Value;
            }

            if (Torus.TileOf(_shape, pos) != near)
            {
                return DigOrWalk(client, world, pos, near);
            }

            return HammerFrame(client);
        }

        /// <summary>Свой тайл — внутри комнаты (пол 3 × 3 вокруг центра).</summary>
        private bool Inside(WorldPos pos) => Cheb(Torus.TileOf(_shape, pos), _center) <= 1;

        private InputFrame WaitDawn(NetClient client, WorldPos pos, long nowUs)
        {
            if (!Inside(pos))
            {
                // Ждать рассвета можно только в комнате (C-004): оказались снаружи — ко входу, дверь «не закрыта».
                DoorClosed = false;
                Stage = Phase.Enter;
                return InputFrame.None;
            }

            WorldClock clock = client.WorldTimeClock;
            if (clock.Valid && clock.TicksAt(nowUs) >= DayClock.DawnStartTick)
            {
                DawnReached = true;
                Stage = Phase.Done;
                return InputFrame.None;
            }

            // Ночь у закрытой двери: F каждый кадр. Замок F (30 тактов, 1-012) продлевается непрерывно —
            // автооткрытие молчит, враг на тайл двери не встаёт и в хижину не входит (приёмка 1.5);
            // вставшего на тайл закрытие выталкивает (E,S,W,N).
            client.CloseDoor(DoorTile);

            // Просочился в комнату до закрытия — самооборона мечом (UDP Big 13.11: 7 попаданий за ночь).
            return ThreatFrame(client, pos) ?? InputFrame.None;
        }

        /// <summary>Расстояние Чебышёва тайлов по тору (максимум из осевых разностей).</summary>
        private int Cheb(TileCoord a, TileCoord b)
        {
            Int2 d = Torus.Delta(_shape, a, b);
            return Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        }

        /// <summary>
        /// Кадр удара мечом по ближайшему врагу в досягаемости удара (сектор <see cref="Reach.SwordStrikeUnits"/>
        /// + тело цели): самооборона в Enter/Close/WaitDawn. Враги лезут в проём во время пина (дверь открыта),
        /// и удары приземляются уже за закрытой дверью — приёмка 1.5 («урон за дверью 0») без боя недостижима;
        /// меч — из стартового набора (<c>06</c> 0), правило ядра не меняется. F (запрос «закрыть дверь»)
        /// идёт отдельным сообщением и руки не занимает (<see cref="Simulation.RequestCloseDoor" />) — замок
        /// продлевается и с мечом в руках.
        /// </summary>
        private InputFrame? ThreatFrame(NetClient client, WorldPos pos)
        {
            Snapshot snap = client.Latest;
            const int reach = Reach.SwordStrikeUnits + Collision.RadiusUnits; // сектор + круг цели (`02` 3.2)
            long bestD2 = long.MaxValue;
            Int2 bestD = default;
            for (int i = 0; i < snap.Count; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Id == client.EntityId || e.Enemy >> SnapshotLayout.EnemyArchetypeShift == 0 || e.HealthPct == 0)
                {
                    continue; // не враг (архетип 0 — мозг выключен) или мёртв
                }

                Int2 d = Torus.Delta(_shape, pos, e.Position);
                long d2 = (long)d.X * d.X + (long)d.Y * d.Y;
                if (d2 <= (long)reach * reach && d2 < bestD2)
                {
                    bestD2 = d2;
                    bestD = d;
                }
            }

            if (bestD2 == long.MaxValue)
            {
                return null;
            }

            AttacksIssued++;
            ushort aim = Angle16.FromDirection(bestD).Raw; // сектор 120° от прицела кадра (1-004)
            return new InputFrame(Input8.None, InputButtons.Attack, aim, SwordSlot);
        }

        /// <summary>Сущности снимка в радиусе 3 тайла от своей (для журнала попаданий, UDP Big 13.11).</summary>
        private string NearbyText(NetClient client, WorldPos pos)        {
            TileCoord me = Torus.TileOf(_shape, pos);
            Snapshot snap = client.Latest;
            var sb = new System.Text.StringBuilder("вокруг:");
            for (int i = 0; i < snap.Count; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Id == client.EntityId)
                {
                    continue;
                }

                int d = Cheb(Torus.TileOf(_shape, e.Position), me);
                if (d <= 3)
                {
                    sb.Append($" #{e.Id} k{e.Kind} d{d} hp{e.HealthPct} e{e.Enemy} m{e.Melee}");
                }
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ ходьба и инструменты

        private bool InPlaceReach(WorldPos pos, TileCoord t) =>
            EditRules.InReach(_shape, pos, t, PlaceReach - Torus.SubTilesPerTile / 4);

        /// <summary>Направление к цели: крупная ось доходит первой, мелкая — до нуля; запас 2 единицы — «у цели».</summary>
        private Input8 StepDir(WorldPos pos, WorldPos target)
        {
            int dx = Torus.DeltaX(_shape, pos.X, target.X);
            int dy = Torus.DeltaY(_shape, pos.Y, target.Y);
            int sx = Math.Abs(dx) > 2 ? Math.Sign(dx) : 0;
            int sy = Math.Abs(dy) > 2 ? Math.Sign(dy) : 0;
            if (sx == 0 && sy == 0)
            {
                sx = Math.Sign(dx);
                sy = Math.Sign(dy);
            }

            return new Input8(sx, sy);
        }

        /// <summary>Кадр ходьбы с детектором «упёрся»: стоит на месте — счётчик; цель он не меняет (копает вызывающий).</summary>
        private InputFrame WalkFrames(WorldPos pos, Input8 dir)
        {
            if (dir != Input8.None && pos == _lastPos)
            {
                if (++_stuckFrames > 40)
                {
                    _stuckFrames = 0;
                    var t = Torus.TileOf(_shape, new WorldPos(pos.X + dir.DX * Torus.SubTilesPerTile / 2, pos.Y + dir.DY * Torus.SubTilesPerTile / 2));
                    _skipUntil[t] = _frame + 90; // помеха, которую нечем копать: цель обходим сменой цели
                }
            }
            else
            {
                _stuckFrames = 0;
            }

            _lastPos = pos;
            return new InputFrame(dir);
        }

        private WorldPos TileCenter(TileCoord t) =>
            new WorldPos((t.X << Torus.SubTileBits) + Torus.SubTilesPerTile / 2, (t.Y << Torus.SubTileBits) + Torus.SubTilesPerTile / 2);

        /// <summary>Ячейка сумки с инструментом, который копаёт объект (время &gt; 0 по таблице клиента); нет — <see cref="byte.MaxValue"/> (1-010).</summary>
        private static byte ToolSlotFor(NetClient client, ushort obj)
        {
            for (byte s = 0; s < Inventory.Slots; s++)
            {
                byte item = client.Inv.SlotAt(s);
                if (client.DigTimes.FramesFor(obj, Items.DigToolOf(item), Items.TierOf(item)) > 0)
                {
                    return s;
                }
            }

            return byte.MaxValue;
        }

        /// <summary>
        /// Ячейка панели «в руке» в фазах стройки (E2-11): предмет-постройка этапа (пол, стена, дверь). Его нет на панели, а в
        /// сумке есть — заявка «переложить на панель» (раз в 30 кадров, общий <see cref="BuildHand.StockMove"/>); пока — молот.
        /// </summary>
        private byte HammerSlot(NetClient client)
        {
            byte item = Stage switch
            {
                Phase.Floor => Items.WoodFloor,
                Phase.Walls => Items.StoneWall,
                Phase.Door => Items.Door,
                Phase.Furnish => Items.Torch,
                _ => Items.None,
            };
            int hand = BuildHand.SlotWith(client.Inv, item);
            if (hand >= 0)
            {
                return (byte)hand;
            }

            if (item != Items.None && _frame - _stockFrame > 30 && BuildHand.StockMove(client.Inv, item, out int from, out int to)
                && client.RequestBagMove(from, to))
            {
                _stockFrame = _frame;
                StockMoves++;
            }

            return ToolHammerSlot(client);
        }

        private int _stockFrame = -1000;

        /// <summary>Заявок «переложить постройку на панель» (E2-11).</summary>
        public int StockMoves { get; private set; }

        private static byte ToolHammerSlot(NetClient client)
        {
            for (byte s = 0; s < Inventory.Slots; s++)
            {
                if (Items.DigToolOf(client.Inv.SlotAt(s)) == ToolKind.Hammer)
                {
                    return s;
                }
            }

            return 0;
        }

        /// <summary>
        /// Кадр фазы стройки: ходьба/стояние с молотом в руках. Кадр ходьбы по умолчанию несёт слот 0 (кирка),
        /// и серверный <c>Inv.Held</c> соскакивает с молота между запросами — постановка ловит «нужен молот»
        /// (1-026). В фазах Floor/Walls/Door все кадры, в том числе шаги, идут с молотом.
        /// </summary>
        private InputFrame WalkHammer(NetClient client, WorldPos pos, TileCoord target)
        {
            InputFrame f = WalkFrames(pos, StepDir(pos, TileCenter(target)));
            return new InputFrame(f.Direction, f.Buttons, f.Angle16, HammerSlot(client));
        }

        private ushort _armObj;
        private TileCoord _armTile;
        private int _armFrame;

        /// <summary>
        /// Кадр-«взвод» перед постановкой: запрос правки летит на Facts-канале сразу (быстрее кадра ввода), сервер
        /// проверяет молот по <c>Inv.Held</c> кадра, который пришёл ПОСЛЕ запроса. Первый кадр на цели бот просто
        /// стоит с молотом — на следующем кадре <c>Held</c> сервера уже молот, и запрос проходит.
        /// </summary>
        private BuildHand? _build;

        /// <summary>«Молот» бота (E2-5) — тот же <see cref="BuildHand"/>, что у окна игрока.</summary>
        private BuildHand Build(NetClient client) => _build ??= new BuildHand(client);

        /// <summary>Хватает ли сумки на стену, дверь и пол (E2-5, после «рукой» BuildHand): метрика для журнала.</summary>
        public int LocalRefusals { get; private set; }

        /// <summary>
        /// ЛКМ «молотом» по тайлу (E2-5): выбор постройки в ленте и щелчок — проверка теми же функциями ядра, что у сервера
        /// и призрака игрока; можно — заявка (номер запроса), нельзя — 0 и тайл в cooldown, как после отказа сервера.
        /// </summary>
        /// <summary>
        /// Ячейки тонкой стены тайла периметра (E2-12): внешняя половина — верх / низ / лево / право, угол — две стороны (3 ячейки).
        /// </summary>
        private byte WallCellsOf(TileCoord t)
        {
            int h = RoomSize / 2;
            int dx = Torus.DeltaX(_shape, _center.X, t.X), dy = Torus.DeltaY(_shape, _center.Y, t.Y);
            byte mask = 0;
            mask |= dy == -h ? BuildCells.DoorNorth : (byte)0;
            mask |= dy == h ? BuildCells.DoorSouth : (byte)0;
            mask |= dx == -h ? BuildCells.DoorWest : (byte)0;
            mask |= dx == h ? BuildCells.DoorEast : (byte)0;
            return mask;
        }

        /// <summary>Тонкая стена тайла периметра стоит целиком (все её ячейки).</summary>
        private bool WallDone(ClientWorld world, TileCoord t)
        {
            byte want = WallCellsOf(t);
            return world.World.GetObject(t) == ObjectId.StoneWall && (BuildCells.MaskAt(world.World, t) & want) == want;
        }

        /// <summary>
        /// Заявка на недостающие ячейки стены тайла (E2-12): пара ячеек вдоль стороны — одной линией (<see cref="BuildHand.PlaceLine(TileCoord, TileCoord, WorldPos, in Inventory, int, int, int)"/>),
        /// иначе одна ячейка (<see cref="BuildHand.Click(WorldModel, WorldPos, in Inventory, int, bool, TileCoord, int)"/>). Проверка — та же, что у призрака.
        /// </summary>
        private ushort PlaceWallCells(NetClient client, ClientWorld world, WorldPos pos, TileCoord t)
        {
            byte have = world.World.GetObject(t) == ObjectId.StoneWall ? BuildCells.MaskAt(world.World, t) : (byte)0;
            int missing = WallCellsOf(t) & ~have;
            (int a, int b) = (missing & 3) == 3 ? (0, 1) : (missing & 12) == 12 ? (2, 3) : (missing & 5) == 5 ? (0, 2) : (missing & 10) == 10 ? (1, 3) : (-1, -1);
            if (a < 0)
            {
                int q = 0;
                while (q < 4 && (missing & (1 << q)) == 0)
                {
                    q++;
                }

                return ClickBuild(client, world, pos, ObjectId.StoneWall, t, q & 3);
            }

            BuildHand build = Build(client);
            int slot = BuildHand.SlotWith(client.Inv, Items.StoneWall);
            if (slot < 0)
            {
                return 0;
            }

            if (!BuildHand.Check(world.World, pos, client.Inv, slot, true, t, a).Ok || !BuildHand.Check(world.World, pos, client.Inv, slot, true, t, b).Ok
                || client.Inv.CountIn(slot) < 2)
            {
                LocalRefusals++;
                _armObj = 0;
                _skipUntil[t] = _frame + RefusalCooldownFrames / 3;
                return 0;
            }

            return build.PlaceLine(t, t, pos, client.Inv, slot, a, b);
        }

        private ushort ClickBuild(NetClient client, ClientWorld world, WorldPos pos, ushort key, TileCoord t, int quad = 0)
        {
            BuildHand build = Build(client);
            int slot = BuildHand.SlotWith(client.Inv, BuildDefs.ItemOf(key));
            if (slot < 0)
            {
                return 0; // предмет ещё не на панели (HandSlot переложит) — не отказ
            }

            int before = build.PlaceRequests;
            BuildCheck chk = build.Click(world.World, pos, client.Inv, slot, true, t, quad);
            if (chk.Ok && build.PlaceRequests > before)
            {
                return build.LastRequest;
            }

            LocalRefusals++;
            _armObj = 0;
            _skipUntil[t] = _frame + RefusalCooldownFrames / 3; // своя проверка — короткий cooldown: сумка/рука догоняют сервер
            return 0;
        }

        private bool PlaceArmed(ushort obj, TileCoord t)
        {
            if (_armObj == obj && _armTile == t && _frame > _armFrame)
            {
                return true;
            }

            _armObj = obj;
            _armTile = t;
            _armFrame = _frame;
            return false;
        }

        /// <summary>
        /// Кадр постановки: молот в руках (1-011, <c>`05` 1.3</c> — сервер требует молот и для постановки,
        /// отказ «нужен молот»).
        /// </summary>
        private InputFrame HammerFrame(NetClient client)
        {
            byte slot = HammerSlot(client);
            return new InputFrame(Input8.None, 0, 0, slot);
        }
    }
}
