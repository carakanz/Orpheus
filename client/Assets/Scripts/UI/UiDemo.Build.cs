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
    /// <c>build</c> — молот в руках, лента построек (выбрана каменная стена), сетка, зелёный призрак на свободном тайле и
    /// красный — на дереве (строка причины у курсора); <c>chest</c> — открыт сундук рядом с персонажем, окна сундука и
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
        public static bool IsBuild(string? mode) => mode == "build" || mode == "chest" || mode == "house";

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

            if (_mode == "build")
            {
                _bag.Select(2); // молот
            }
        }

        // Заявки стройки стенда: мир сцены не меняется (снимок — про призрак и ленту).
        public ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self) => 1;

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
            if (_mode == "build")
            {
                // Каменная стена: лента по кругу с полов — четыре шага E.
                input.BuildStep = _frame >= 2 && _frame <= 5 ? 1 : 0;
                var good = new TileCoord(me.X + 3, me.Y - 1);
                var bad = new TileCoord(me.X - 3, me.Y + 1); // дерево сцены
                build.Hammer = true;
                build.HasGhost = true;
                build.Check = BuildHand.Check(_world, position, _bag, _bag.Held, true, bad, _ui.Build.Key);
                if (_origin != null && _ghost != null)
                {
                    BuildCheck ok = BuildHand.Check(_world, position, _bag, _bag.Held, true, good, _ui.Build.Key);
                    _ghost.Show(_origin, _world.Shape, position, 0, true, good, ok);
                    _ghost.Show(_origin, _world.Shape, position, 1, true, bad, build.Check);
                    // Курсор — над красным призраком: строка причины у курсора.
                    input.Pointer = _ui.WorldToCanvas(_origin.ToUnity(EditRules.CenterOf(bad)) + new Vector3(0.2f, -0.1f, 0f));
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
            _ui.Frame(input, _bag, selected: _mode == "build" ? 2 : 3, dead: false, health: 72, maxHealth: 100, stamina: 45, maxStamina: 100,
                new UiCraftState { Queue = _queue, World = _world, Position = position }, build);
        }
    }
}
