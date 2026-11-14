using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Куда уходят заявки стройки (E2-5): клиент сети (<see cref="NetClient"/>) или показ без сервера.</summary>
    public interface IBuildRequests
    {
        /// <summary>Поставить линию от тайла до тайла (с предсказанием, как у клиента сети); номер запроса, 0 — не ушло.</summary>
        ushort Place(TileCoord from, TileCoord to, ushort obj, WorldPos self);

        /// <summary>Починить постройку на тайле; номер запроса, 0 — не ушло.</summary>
        ushort Repair(TileCoord tile);
    }

    /// <summary>Куда уходят действия с объектами (E2-5, F): клиент сети или показ без сервера.</summary>
    public interface IUseRequests
    {
        /// <summary>Действие с объектом на тайле: сундук — открыть, кровать — точка возрождения.</summary>
        bool RequestUse(TileCoord tile);

        /// <summary>Закрыть открытый сундук.</summary>
        bool RequestCloseChest();
    }

    /// <summary>Что сделает ЛКМ молотом по тайлу (<see cref="BuildHand.Check"/>).</summary>
    public enum BuildAction : byte
    {
        /// <summary>Поставить выбранную постройку.</summary>
        Place = 0,

        /// <summary>Починить повреждённую постройку на тайле.</summary>
        Repair = 1,
    }

    /// <summary>Итог проверки тайла под курсором: что сделает щелчок, можно ли (иначе — причина) и цена.</summary>
    public readonly struct BuildCheck
    {
        public readonly BuildAction Action;
        public readonly EditReply Reply;

        /// <summary>Что ставится (ключ <see cref="BuildDefs"/>) или чинится (объект на тайле).</summary>
        public readonly ushort Key;

        /// <summary>Цена: материалы и количества (у починки — по недостающей доле прочности).</summary>
        public readonly Material Material1, Material2;
        public readonly int Count1, Count2;

        public BuildCheck(BuildAction action, EditReply reply, ushort key, Material m1, int c1, Material m2, int c2)
        {
            Action = action;
            Reply = reply;
            Key = key;
            Material1 = m1;
            Count1 = c1;
            Material2 = m2;
            Count2 = c2;
        }

        /// <summary>Можно: призрак зелёный, щелчок отправит заявку.</summary>
        public bool Ok => Reply == EditReply.Accepted;

        /// <summary>Причина отказа строкой (`05` 1.3); можно — пусто.</summary>
        public string Reason => Ok ? string.Empty : EditRules.Describe(Reply);
    }

    /// <summary>
    /// «Молот» режима стройки (E2-5, шаг 2.5): общий путь для игрока (лента построек, призрак, ЛКМ) и для ботов / тестов —
    /// как <see cref="BagHand"/> у сумки и <see cref="CraftDesk"/> у крафта. Правил игры здесь нет: «можно / нельзя с
    /// причиной» считают функции ядра, которыми проверяет и сервер (<see cref="EditRules.CheckPlaceTile"/>,
    /// <see cref="EditRules.CheckRepair"/>, <see cref="BuildDefs.Affordable"/>); решает всё равно сервер. Постановка
    /// предсказывается клиентом сети (<see cref="NetClient.Place"/>), починка — нет.
    /// </summary>
    public sealed class BuildHand
    {
        private readonly IBuildRequests _requests;

        public BuildHand(IBuildRequests requests)
        {
            _requests = requests;
        }

        /// <summary>Номер выбранной постройки в ленте (<see cref="BuildDefs.Palette"/>).</summary>
        public int Selected { get; private set; }

        /// <summary>Ключ выбранной постройки.</summary>
        public ushort Key => BuildDefs.Palette[Selected];

        /// <summary>Номер последнего отправленного запроса (постановка или починка); 0 — ещё не было.</summary>
        public ushort LastRequest { get; private set; }

        /// <summary>Заявок постановки и починки отправлено.</summary>
        public int PlaceRequests { get; private set; }
        public int RepairRequests { get; private set; }

        /// <summary>Листать ленту (Q / E): шаг по кругу.</summary>
        public void Step(int step)
        {
            int n = BuildDefs.Palette.Length;
            Selected = ((Selected + step) % n + n) % n;
        }

        /// <summary>Выбрать постройку по ключу; в ленте нет — false.</summary>
        public bool Select(ushort key)
        {
            int i = BuildDefs.Palette.IndexOf(key);
            if (i < 0)
            {
                return false;
            }

            Selected = i;
            return true;
        }

        /// <summary>Молот в руках (режим стройки открыт).</summary>
        public static bool IsHammer(byte item) => Items.DigToolOf(item) == ToolKind.Hammer;

        /// <summary>
        /// Что сделает ЛКМ по тайлу и можно ли (данные клиента: свой мир, место, сумка, предмет в руках по своим кадрам,
        /// жив ли). На тайле повреждённая постройка — починка (<see cref="EditRules.CheckRepair"/>, цена по сумке); иначе
        /// постановка выбранного (<see cref="EditRules.CheckPlaceTile"/>; непроходимое — не на себя; цена по сумке).
        /// </summary>
        public static BuildCheck Check(WorldModel world, WorldPos self, in Inventory bag, byte held, bool alive, TileCoord tile, ushort key)
        {
            EditReply repair = EditRules.CheckRepair(world, self, tile, out BuildDef rdef, out int r1, out int r2);
            if (repair != EditReply.Empty && repair != EditReply.Intact && repair != EditReply.OutOfReach)
            {
                if (repair == EditReply.Accepted && !EditRules.RepairAffordable(rdef, r1, r2, bag))
                {
                    repair = EditReply.NoResources;
                }

                return new BuildCheck(BuildAction.Repair, Gate(repair, alive, held), world.GetObject(Torus.Wrap(world.Shape, tile)),
                    rdef.Material1, r1, rdef.Material2, r2);
            }

            BuildDefs.TryGet(key, out BuildDef def);
            EditReply reply = EditRules.CheckPlaceTile(world, self, tile, key);
            if (reply == EditReply.Accepted && EditRules.BlocksCreature(key) && !EditRules.IsFloorKey(key) && EditRules.Overlaps(world.Shape, self, tile))
            {
                reply = EditReply.Occupied; // непроходимое — не на себя (`05` 1.3); чужих существ проверит сервер
            }

            if (reply == EditReply.Accepted && !BuildDefs.Affordable(def, bag))
            {
                reply = EditReply.NoResources;
            }

            return new BuildCheck(BuildAction.Place, Gate(reply, alive, held), key, def.Material1, def.Count1, def.Material2, def.Count2);
        }

        // Общие отказы раньше остальных: мёртвый не строит, без молота — «нужен молот».
        private static EditReply Gate(EditReply reply, bool alive, byte held) =>
            !alive ? EditReply.Dead : !IsHammer(held) ? EditReply.ToolRequired : reply;

        /// <summary>
        /// ЛКМ по тайлу: проверка <see cref="Check"/>; можно — заявка (постановка с предсказанием или починка), иначе заявки
        /// нет. Возвращает проверку (причину покажет призрак / строка у курсора).
        /// </summary>
        public BuildCheck Click(WorldModel world, WorldPos self, in Inventory bag, byte held, bool alive, TileCoord tile)
        {
            BuildCheck c = Check(world, self, bag, held, alive, tile, Key);
            if (!c.Ok)
            {
                return c;
            }

            if (c.Action == BuildAction.Repair)
            {
                ushort id = _requests.Repair(tile);
                if (id != 0)
                {
                    RepairRequests++;
                    LastRequest = id;
                }
            }
            else
            {
                ushort id = _requests.Place(tile, tile, c.Key, self);
                if (id != 0)
                {
                    PlaceRequests++;
                    LastRequest = id;
                }
            }

            return c;
        }

        /// <summary>
        /// Линия одной постройки (ведение с зажатой ЛКМ, ряд пола у бота): заявка уходит целиком — по тайлам решает сервер
        /// (`05` 1.4: линия до 32 тайлов, где ресурсы кончились — «нет ресурсов»). Возвращает номер запроса; 0 — не ушло.
        /// </summary>
        public ushort PlaceLine(TileCoord from, TileCoord to, WorldPos self)
        {
            ushort id = _requests.Place(from, to, Key, self);
            if (id != 0)
            {
                PlaceRequests++;
                LastRequest = id;
            }

            return id;
        }

        /// <summary>Сколько материала № <paramref name="i"/> (0 или 1) постройки есть в сумке и сколько нужно — подпись ленты.</summary>
        public static (Material Material, int Have, int Need) CostLine(ushort key, int i, in Inventory bag)
        {
            BuildDefs.TryGet(key, out BuildDef def);
            if (i == 1 && def.Material2 == def.Material1)
            {
                return (def.Material2, 0, 0); // смета одним материалом (настил над глубиной) — одна строка
            }

            Material m = i == 0 ? def.Material1 : def.Material2;
            int need = i == 0 ? def.Count1 + (def.Material2 == def.Material1 ? def.Count2 : 0) : def.Count2;
            return (m, need > 0 ? bag.CountAt(m) : 0, need);
        }
    }
}
