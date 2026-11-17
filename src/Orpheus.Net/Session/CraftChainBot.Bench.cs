using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>Бот <c>craft_chain</c> (E2-3): постановка верстака, заявки крафта (через <see cref="CraftDesk"/>), кирка на панель.</summary>
    public sealed partial class CraftChainBot
    {
        // ---------- Верстак, крафт, панель ----------

        private static readonly sbyte[] BenchDx = { 2, -2, 0, 0, 2, 2, -2, -2, 1, -1, 1, -1, 2, 2, -2, -2, 1, -1, 0, 0, 1, -1, 1, -1 };
        private static readonly sbyte[] BenchDy = { 0, 0, 2, -2, 1, -1, 1, -1, 2, 2, -2, -2, 2, -2, 2, -2, 0, 0, 1, -1, 1, 1, -1, -1 };

        private InputFrame PlaceBench(NetClient client, ClientWorld world, WorldPos pos)
        {
            // E2-11: верстак — предмет. Нет его — сделать руками (10 древесины + 6 камня); в сумке — переложить на панель
            // (общий BuildHand.StockMove); на панели — взять в руку и поставить им.
            int benchSlot = BuildHand.SlotWith(client.Inv, Items.Workbench);
            if (benchSlot < 0 && _placeReq == 0)
            {
                if (client.Inv.Count(Items.Workbench) == 0)
                {
                    int before = CraftRequests;
                    InputFrame f = Craft(client, world, pos, RecipeTable.Workbench, 1);
                    if (CraftRequests > before)
                    {
                        _benchOrderFrame = _frame;
                    }

                    return f;
                }

                if (_frame - _moveFrame > 30 && BuildHand.StockMove(client.Inv, Items.Workbench, out int from, out int to) && client.RequestBagMove(from, to))
                {
                    _moveFrame = _frame;
                }

                return Stand();
            }

            InputFrame hammer = new InputFrame(Input8.None, 0, 0, (byte)Math.Max(0, benchSlot));
            if (_placeReq != 0)
            {
                if (_frame - _placeFrame > 180)
                {
                    _placeReq = 0; // ответ не пришёл — пробуем снова
                }

                return hammer;
            }

            TileCoord me = Torus.TileOf(_shape, pos);
            int open = Flood(world, me, me, false);
            for (int k = 0; k < BenchDx.Length; k++)
            {
                var t = Torus.Wrap(_shape, new TileCoord(me.X + BenchDx[k], me.Y + BenchDy[k]));
                if (_benchRefused.Contains(t) || world.World.GetObject(t) != ObjectId.Empty || !Walkable(world, t))
                {
                    continue;
                }

                // Верстак не должен запереть бота (он твёрдый): с ним на этом тайле доступно почти столько же, сколько без него.
                if (!(_armed && _armTile.X == t.X && _armTile.Y == t.Y) && Flood(world, me, t, true) < open - open / 8 - 1)
                {
                    continue;
                }

                // «Взвод»: несколько кадров с верстаком в руке до заявки (рука сервера догоняет кадры ввода).
                if (!_armed || _armTile.X != t.X || _armTile.Y != t.Y)
                {
                    _armed = true;
                    _armTile = t;
                    _armFrame = _frame;
                    return hammer;
                }

                if (_frame - _armFrame < 6)
                {
                    return hammer;
                }

                ushort id = benchSlot < 0 ? (ushort)0 : client.Place(t, t, ObjectId.Workbench, pos, (byte)benchSlot);
                if (id != 0)
                {
                    _placeReq = id;
                    _placeTile = t;
                    _placeFrame = _frame;
                }

                return hammer;
            }

            _benchRefused.Clear(); // все места вокруг перебрали — заново (мир мог измениться)
            return hammer;
        }

        private InputFrame Craft(NetClient client, ClientWorld world, WorldPos pos, byte recipe, int count)
        {
            // Тот же путь, что у окна крафта (CraftDesk): строка рецепта по данным клиента → кнопка «сделать».
            // Очередь пуста и ответа давно нет — заявка (первая или повтор); пока очередь идёт — стоим у верстака.
            _desk ??= new CraftDesk(client);

            // C-028: верстак (2 тайла шириной) поставлен в 2 тайлах — его середина бывает дальше радиуса крафта (3 т), и бот вечно
            // стоял с «станок далеко» (всплыло на зерне 2 после взмахов кирки: бот пришёл к месту постановки иначе). Подойти к нему.
            if (_hasBench && CraftRules.StationCheck(world.World, pos, CraftStation.Workbench) == CraftReply.StationFar)
            {
                Int2 d = Torus.Delta(_shape, pos, BuildLayers.CenterAt(world.World, _bench));
                return new InputFrame(new Input8(Math.Sign(d.X), Math.Sign(d.Y)), 0, 0, _slot);
            }

            if (count > 0 && client.Craft.IsEmpty && _frame - _craftFrame > 90)
            {
                CraftLine line = CraftDesk.LineOf(recipe, client.Inv, client.Craft, world.World, pos, stock: client.NearbyStock);
                if (_desk.Make(line, count))
                {
                    _craftFrame = _frame;
                    CraftRequests++;
                }
                else
                {
                    LastCraftRefusal = line.State; // своя проверка не пустила (станок далеко, не хватает) — видно в итоге
                }
            }

            return Stand();
        }

        private InputFrame Stand() => new InputFrame(Input8.None, 0, 0, _slot);

        private CraftDesk? _desk;
        private int _benchOrderFrame = -1000; // E2-11: заявка «сделать верстак» ушла — пока предмет не пришёл, этап «верстак»

        private InputFrame Equip(NetClient client, in Inventory inv)
        {
            if (_frame - _moveFrame <= 60)
            {
                return InputFrame.None;
            }

            int cell = -1, slot = Inventory.Slots - 1;
            for (int c = Inventory.Cells - 1; c >= 0; c--)
            {
                cell = inv.ItemAt(c) == Items.CopperPickaxe ? c : cell;
            }

            for (int s = Inventory.Slots - 1; s >= 0; s--)
            {
                slot = inv.SlotAt(s) == Items.None ? s : slot;
            }

            if (cell >= 0 && client.RequestBagMove(cell, slot))
            {
                _moveFrame = _frame;
            }

            return InputFrame.None;
        }
    }
}
