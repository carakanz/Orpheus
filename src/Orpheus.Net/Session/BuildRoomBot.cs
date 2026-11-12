using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
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
    /// Стоимость постановки списывает сервер (клиент предсказывает); молот для постановки не нужен — <c>PreEdit</c>
    /// проверяет предмет в руках только для копания. Счёт целей — по рецептам <c>BuildDefs</c>: 15 стен × 2 камня =
    /// 30 камня, 9 полов × 1 + дверь × 6 = 15 дерева; добыча ведётся до <see cref="TargetStone"/>/<see cref="TargetWood"/>
    /// (перелёт допустим: добыча идёт объектами, не разовыми порциями).
    /// </para>
    /// </summary>
    public sealed class BuildRoomBot
    {
        public const string Name = "build_room";

        /// <summary>Добыть до перехода к стройке: 30 камня (15 стен) и 26 древесины (9 полов + дверь 6 + 11 припас).</summary>
        public const int TargetStone = 30;
        public const int TargetWood = 26;

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

        /// <summary>Этапы: добыча → расчистка площадки → пол → стены → дверь → вход → закрытие → рассвет.</summary>
        public enum Phase
        {
            Gather, Clear, Floor, Walls, Door, Enter, Close, WaitDawn, Done, Fail
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

        /// <summary>Кадров с закрытой дверью, в которых здоровье своё было &lt; 100 % (урон за закрытой дверью).</summary>
        public int DamageThroughDoor { get; private set; }

        /// <summary>Смертей своё (пропажа <see cref="SnapshotSelf.Present"/>).</summary>
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
            if (m.Reply != EditReply.Accepted)
            {
                Refused++;
                _refused[m.Reply] = _refused.TryGetValue(m.Reply, out int n) ? n + 1 : 1;
                _armObj = 0; // отказ: следующий запрос той же цели заново «взводится» кадром с молотом
                if (obj == FloorId.WoodFloor)
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
                    _wallReq = 0;
                    _wallsFrom++;
                    break;
                case ObjectId.Door:
                    DoorPlaced = true;
                    break;
                case FloorId.WoodFloor:
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

        /// <summary>Кадр ввода (30 Гц): копать, идти, ставить или стоять. <paramref name="pos"/> — позиция своя
        /// (предсказание NetBot или снимок), <paramref name="nowUs"/> — часы NetBot (для часов мира, 1-014).</summary>
        public InputFrame Next(NetClient client, WorldPos pos, long nowUs)
        {
            Ticks++;
            _frame++;
            ClientWorld? world = client.World;
            if (world == null || !client.HasSnapshot || Stage is Phase.Done or Phase.Fail)
            {
                return InputFrame.None;
            }

            if (Ticks > MaxTicks)
            {
                Stage = Phase.Fail;
                FailReason = $"лимит {MaxTicks} тиков: этап {Stage}, полов {FloorsPlaced}/9, стен {WallsPlaced}/15, дверь {(DoorPlaced ? "поставлена" : "нет")}";
                return InputFrame.None;
            }

            SelfState self = client.Latest.Self;
            if (!self.Present)
            {
                Deaths++;
                return InputFrame.None; // возрождение — на стороне сервера (1-009)
            }

            if (Stage >= Phase.WaitDawn && DoorClosed && self.Health < MaxHealth)
            {
                DamageThroughDoor++; // урон за закрытой дверью — провал приёмки 1.5
            }

            if (client.Digging)
            {
                return new InputFrame(Input8.None, InputButtons.Dig, 0, (byte)_digSlot);
            }

            InputFrame frame = Stage switch
            {
                Phase.Gather => Gather(client, world, pos),
                Phase.Clear => Clear(client, world, pos),
                Phase.Floor => Floor(client, world, pos),
                Phase.Walls => Walls(client, world, pos),
                Phase.Door => Door(client, world, pos),
                Phase.Enter => Enter(client, world, pos),
                Phase.Close => Close(client, world, pos),
                Phase.WaitDawn => WaitDawn(client, nowUs),
                _ => InputFrame.None,
            };
            return frame;
        }

        // ------------------------------------------------------------------ добыча

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

            TileCoord? target = FindGatherTarget(client, world, pos, stone < TargetStone, wood < TargetWood);
            if (target == null)
            {
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
                Stage = Phase.Floor;
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

        // ------------------------------------------------------------------ пол

        private InputFrame Floor(NetClient client, ClientWorld world, WorldPos pos)
        {
            CountFloors(world);
            if (FloorsPlaced >= 9)
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

            int h = RoomSize / 2;
            int y = _center.Y - h + 1 + _floorRow % 3;
            var from = Torus.Wrap(_shape, new TileCoord(_center.X - h + 1, y));
            var to = Torus.Wrap(_shape, new TileCoord(_center.X + h - 1, y));
            if (!PlaceArmed(FloorId.WoodFloor, from))
            {
                return HammerFrame(client);
            }

            ushort id = client.Place(from, to, FloorId.WoodFloor, pos);
            if (id != 0)
            {
                _reqObj[id] = FloorId.WoodFloor;
                _floorReq = id;
            }

            return HammerFrame(client);
        }

        private void CountFloors(ClientWorld world)
        {
            int h = RoomSize / 2, n = 0;
            for (int dy = -h + 1; dy <= h - 1; dy++)
            {
                for (int dx = -h + 1; dx <= h - 1; dx++)
                {
                    if (world.World.GetFloor(Torus.Wrap(_shape, new TileCoord(_center.X + dx, _center.Y + dy))) == FloorId.WoodFloor)
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
            if (world.World.GetObject(wall) == ObjectId.StoneWall)
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
                    ushort id = client.Place(wall, wall, ObjectId.StoneWall, pos);
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
                    if (t != DoorTile && world.World.GetObject(t) == ObjectId.StoneWall)
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

        private InputFrame Door(NetClient client, ClientWorld world, WorldPos pos)
        {
            TileCoord door = DoorTile;
            if (world.World.GetObject(door) == ObjectId.Door)
            {
                Stage = Phase.Enter;
                return InputFrame.None;
            }

            if (InPlaceReach(pos, door))
            {
                if (PlaceArmed(ObjectId.Door, door))
                {
                    ushort id = client.Place(door, door, ObjectId.Door, pos);
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

            // Дверь ставится открытой (1-012); шаг на тайл двери её распахивает (автооткрытие шагом, `05` 2.1).
            // Идём в центр комнаты через проём, без копки: копать дверь — снести свою же дверь.
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
                DoorClosed = true; // закрыли мы или автозакрытие (30 тактов свободы тайла, `05` 2.1)
                Stage = Phase.WaitDawn;
                BuildTicks = Ticks - GatherTicks;
                return InputFrame.None;
            }

            // F — радиус chebyshev ≤ 1 от двери, тайл под нажавшим — отказ: встаём на тайл комнаты у проёма.
            TileCoord near = Torus.Wrap(_shape, new TileCoord(door.X, door.Y + 1));
            if (Torus.TileOf(_shape, pos) != near)
            {
                return DigOrWalk(client, world, pos, near);
            }

            client.CloseDoor(door);
            return HammerFrame(client);
        }

        private InputFrame WaitDawn(NetClient client, long nowUs)
        {
            WorldClock clock = client.WorldTimeClock;
            if (clock.Valid && clock.TicksAt(nowUs) >= DayClock.DawnStartTick)
            {
                DawnReached = true;
                Stage = Phase.Done;
            }

            return InputFrame.None;
        }

        /// <summary>Расстояние Чебышёва тайлов по тору (максимум из осевых разностей).</summary>
        private int Cheb(TileCoord a, TileCoord b)
        {
            Int2 d = Torus.Delta(_shape, a, b);
            return Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
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

        /// <summary>Ячейка сумки с молотом (постановка требует молот в руках кадра, 1-011); нет — 0.</summary>
        private static byte HammerSlot(NetClient client)
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
        private static InputFrame HammerFrame(NetClient client)
        {
            byte slot = 0;
            for (byte s = 0; s < Inventory.Slots; s++)
            {
                if (Items.DigToolOf(client.Inv.SlotAt(s)) == ToolKind.Hammer)
                {
                    slot = s;
                    break;
                }
            }

            return new InputFrame(Input8.None, 0, 0, slot);
        }
    }
}
