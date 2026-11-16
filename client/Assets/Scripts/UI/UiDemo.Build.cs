#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using UnityEngine;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Сцены стенда снимков режима стройки и сундука (E2-5; ключ <c>--ui-demo build|chest|house</c>), без сервера:
    /// <c>build</c> — каменная стена в руке (E2-11: предмет на панели, ленты у молота нет), сетка, зелёный призрак на
    /// свободном тайле и красный — на дереве (строка причины у курсора); <c>chest</c> — открыт сундук рядом с персонажем, окна сундука и
    /// сумки, скрипт перекладывает стопки; <c>house</c> — дом (каменные стены, дверь, деревянный пол, верстак, сундук,
    /// кровать, факел), окна закрыты. Мир сцены ставит <see cref="PaintDemo.House"/>.
    /// </summary>
    public sealed partial class UiDemo
    {
        FloatingOrigin? _origin;
        BuildGhost? _ghost;
        Inventory _chest;
        bool _chestOpen;

        /// <summary>Сцена режима стройки, сундука или дома (E2-5).</summary>
        public static bool IsBuild(string? mode) => mode == "build" || mode == "build-open" || mode == "chest" || mode == "house" || mode == "thin" || mode == "half" || mode == "turn" || mode == "cursor-place" || IsRuler(mode);

        /// <summary>E2-13: сцены линейки — протягивание стены (<c>ruler-wall</c>) и пола (<c>ruler-floor</c>).</summary>
        public static bool IsRuler(string? mode) => mode == "ruler-wall" || mode == "ruler-floor";

        /// <summary>Начало координат и родитель призрака для сцен E2-5 (звать сразу после конструктора).</summary>
        public void BindWorld(FloatingOrigin origin, Transform parent)
        {
            _origin = origin;
            _ghost = new BuildGhost(parent);
            if (_mode == "chest")
            {
                _chest.SetCell(0, Items.Wood, 64);
                _chest.SetCell(1, Items.Stone, 40);
                _chest.SetCell(2, Items.CopperOre, 17);
                _chest.SetCell(6, Items.Arrows, 120);
                _chest.SetCell(7, Items.Fiber, 9);
                _chest.SetCell(13, Items.CopperIngot, 6);
                _chestOpen = true;
            }

            if (_mode == "build" || _mode == "build-open")
            {
                _bag.SetCell(6, Items.StoneWall, 24); // E2-11: стена — предмет в ячейке панели 7
                _bag.Select(6);
                if (_mode == "build" && _ui.Ruler.Mode == PlaceMode.Ruler)
                {
                    _ui.ToggleRuler(); // C-015: сцена «поштучно» — у быстрой панели значок зигзага
                }
            }

            if (_mode == "cursor-place")
            {
                // C-013 п. 8: стена — в сумке (ячейка 20, не панель); в руке на панели — кирка. Скрипт берёт стену на курсор.
                _bag.SetCell(20, Items.StoneWall, 24);
            }

            if (_mode == "turn")
            {
                // E2-16: кровати в сумке нет, материалы есть — «из меню» она изготовится при постановке.
                _bag.SetCell(10, Items.Wood, 20);
                _bag.SetCell(11, Items.Fiber, 5);
            }

            if (IsRuler(_mode))
            {
                // E2-13: стен 12 на линию из 16 ячеек — хвост бледный («не хватит»); пол 20 на 4 × 3.
                _bag.SetCell(6, _mode == "ruler-wall" ? Items.StoneWall : Items.WoodFloor, _mode == "ruler-wall" ? 12 : 20);
                _bag.Select(6);
            }
        }

        readonly BuildRuler _demoRuler = new BuildRuler();

        // Заявки стройки стенда: мир сцены не меняется (снимок — про призрак и ленту).
        public ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell, byte quad) => 1;

        public ushort Repair(TileCoord tile) => 1;

        // Заявки на ячейки сундука (40…69) — над сумкой и сундуком стенда (в игре решает Simulation.ApplyChestOp).
        bool ChestMove(int from, int to)
        {
            bool fc = from >= BagHand.ChestBase, tc = to >= BagHand.ChestBase;
            int f = fc ? from - BagHand.ChestBase : from, t = tc ? to - BagHand.ChestBase : to;
            if (fc && tc)
            {
                return _chest.Move(f, t);
            }

            return fc ? Inventory.Move(ref _chest, f, ref _bag, t) : Inventory.Move(ref _bag, f, ref _chest, t);
        }

        bool ChestSplit(int from, int to, int count)
        {
            bool fc = from >= BagHand.ChestBase, tc = to >= BagHand.ChestBase;
            int f = fc ? from - BagHand.ChestBase : from, t = tc ? to - BagHand.ChestBase : to;
            if (fc && tc)
            {
                return _chest.Split(f, t, count);
            }

            return fc ? Inventory.Split(ref _chest, f, ref _bag, t, count) : Inventory.Split(ref _bag, f, ref _chest, t, count);
        }

        void BuildFrame(WorldPos position)
        {
            var input = new UiInput { HasPointer = true, Pointer = new Vector2(40f, 40f) };
            var build = new UiBuildState { ChestOpen = _chestOpen, Chest = _chest };
            TileCoord me = Torus.TileOf(_world!.Shape, position);
            if (_mode == "build-open")
            {
                input.Toggle = _frame == 2; // C-008: сумка и изготовление открыты, курсор над миром — призрак виден
            }

            if (_mode == "build" || _mode == "build-open")
            {
                var good = new TileCoord(me.X + 3, me.Y - 1);
                var bad = new TileCoord(me.X - 3, me.Y + 1); // дерево сцены
                build.Placing = true;
                build.HasGhost = true;
                build.Check = BuildHand.Check(_world, position, _bag, _bag.Selected, true, bad);
                if (_origin != null && _ghost != null)
                {
                    BuildCheck ok = BuildHand.Check(_world, position, _bag, _bag.Selected, true, good, 2); // E2-12: призрак — ячейка 0,5 (ЮЗ)
                    _ghost.Show(_origin, _world.Shape, position, 0, true, good, ok);
                    _ghost.Show(_origin, _world.Shape, position, 1, true, bad, build.Check);
                    // Курсор — над красным призраком: строка причины у курсора.
                    TileCoord at = _mode == "build-open" ? good : bad;
                    input.Pointer = _ui.WorldToCanvas(_origin.ToUnity(EditRules.CenterOf(at)) + new Vector3(0.2f, -0.1f, 0f));
                    if (_mode == "build-open")
                    {
                        build.Check = ok; // строка у курсора — про зелёный призрак
                    }
                }
            }
            else if (_mode == "cursor-place" && _origin != null && _ghost != null)
            {
                // C-013 п. 8: Q — окна открыты; ЛКМ по ячейке 20 — стена на курсоре; курсор над миром — зелёный призрак из
                // ячейки 20 (не выброс), строка «в руке 24».
                input.Toggle = _frame == 2;
                input.LeftDown = _frame == 5;
                input.LeftHeld = input.LeftDown;
                if (_frame <= 6)
                {
                    input.Pointer = _ui.CellCenter(20);
                }
                else
                {
                    var good = new TileCoord(me.X + 5, me.Y - 1); // справа от дома сцены, на траве
                    BuildCheck ok = BuildHand.Check(_world, position, _bag, 20, true, good, 2);
                    _ghost.Show(_origin, _world.Shape, position, 0, true, good, ok);
                    _ghost.Show(_origin, _world.Shape, position, 1, false, good, ok);
                    build.Placing = true;
                    build.HasGhost = true;
                    build.Check = ok;
                    build.FromCursor = true;
                    input.Pointer = _ui.WorldToCanvas(_origin.ToUnity(EditRules.CenterOf(good)) + new Vector3(0.2f, -0.1f, 0f));
                }
            }
            else if (_mode == "turn" && _origin != null && _ghost != null)
            {
                // E2-16: призрак кровати «из меню» с поворотом 90° (лёжа поперёк) — зелёный; подпись «R — повернуть, есть N».
                var at = new TileCoord(me.X - 2, me.Y + 2);
                int avail = MenuHand.Available(_world, position, _bag, null, Items.Bed);
                BuildCheck menu = MenuHand.Check(_world, position, _bag, 6, Items.Bed, avail, true, at, 0, 1);
                _ghost.Show(_origin, _world.Shape, position, 0, true, at, menu);
                _ghost.Show(_origin, _world.Shape, position, 1, false, at, menu); // второго призрака в сцене нет
                build.Placing = true;
                build.HasGhost = true;
                build.Check = menu;
                build.Rotatable = true;
                build.FromMenu = true;
                build.MenuAvailable = avail;
                input.Pointer = _ui.WorldToCanvas(_origin.ToUnity(EditRules.CenterOf(new TileCoord(at.X + 1, at.Y))) + new Vector3(0.2f, -0.1f, 0f));
            }
            else if (IsRuler(_mode))
            {
                // Протягивание: стена — ряд под персонажем через дерево сцены (красные ячейки пропускаются); пол — 4 × 3 слева
                // сверху, на тайле верстака — красный. Курсор — в точке B, у курсора «длина / площадь, нужно, есть».
                bool wall = _mode == "ruler-wall";
                var a = wall ? new TileCoord(me.X - 5, me.Y + 1) : new TileCoord(me.X - 5, me.Y - 3);
                var b = wall ? new TileCoord(me.X + 2, me.Y + 1) : new TileCoord(me.X - 2, me.Y - 1);
                _demoRuler.Begin(a, wall ? 2 : 0);
                _demoRuler.Update(_world, position, _bag, _bag.Selected, true, b, wall ? 3 : 0, false);
                build.Placing = true;
                build.RulerMode = true;
                build.Dragging = true;
                build.Plan = _demoRuler;
                if (_origin != null && _ghost != null)
                {
                    _ghost.ShowPlan(_origin, _world.Shape, position, _demoRuler.Spots);
                    input.Pointer = _ui.WorldToCanvas(_origin.ToUnity(EditRules.CenterOf(b)) + new Vector3(0.25f, -0.25f, 0f));
                }
            }
            else if (_mode == "chest")
            {
                // Скрипт: древесина из сундука → в сумку (Shift+ЛКМ), камень из сумки — половину в сундук.
                int f = _frame;
                input.Pointer = f < 5 ? _ui.ChestCellCenter(BagHand.ChestBase + 0) : f < 9 ? _ui.CellCenter(10) : f < 12 ? _ui.ChestCellCenter(BagHand.ChestBase + 4)
                    : _ui.ChestCellCenter(BagHand.ChestBase + 2) + new Vector2(10f, -6f);
                input.Shift = f == 4;
                input.LeftDown = f == 4 || f == 10;
                input.RightDown = f == 8;
                input.LeftHeld = input.LeftDown;
            }

            build.Chest = _chest;
            build.Use = _chestOpen ? UseKind.None : ObjectUse.Find(_world, position, null, out _, out UseKind use) ? use : UseKind.None;
            _ui.Frame(input, _bag, selected: _mode == "build" || _mode == "build-open" || IsRuler(_mode) ? 6 : 3, dead: false, health: 72, maxHealth: 100, stamina: 45, maxStamina: 100,
                new UiCraftState { Queue = _queue, World = _world, Position = position }, build);
        }
    }
}
