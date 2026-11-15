using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Куда уходят заявки изготовления (E2-3): клиент сети (<see cref="NetClient"/>) или показ без сервера.</summary>
    public interface ICraftRequests
    {
        /// <summary>Изготовить рецепт × count (решает сервер; ответ — сообщение 31, очередь — 32, сумка — 26).</summary>
        bool RequestCraft(int recipe, int count = 1);

        /// <summary>Отменить позицию очереди изготовления (ингредиенты вернутся в сумку).</summary>
        bool RequestCraftCancel(int index);
    }

    /// <summary>
    /// Отмена со сверкой (E2-3, проверка П п. 4): в заявке едет рецепт, который окно видело в позиции; очередь на сервере
    /// сдвинулась, пока заявка шла, — отказ «нет позиции», соседняя не отменяется. <see cref="CraftDesk.Cancel"/> зовёт его,
    /// если получатель заявок его умеет; иначе — <see cref="ICraftRequests.RequestCraftCancel(int)"/>.
    /// </summary>
    public interface ICraftCancelChecked
    {
        /// <summary>Отменить позицию <paramref name="index"/>, в которой заявитель видел рецепт <paramref name="recipe"/>.</summary>
        bool RequestCraftCancel(int index, int recipe);
    }

    /// <summary>C-013: заявка «изготовить сразу в ячейку сумки» (выбранный рецепт и щелчок по ячейке).</summary>
    public interface ICraftIntoRequests
    {
        /// <summary>Изготовить рецепт × <paramref name="count"/> в ячейку <paramref name="cell"/> (0xFF — куда влезет).</summary>
        bool RequestCraftInto(int recipe, int cell, int count = 1);
    }

    public sealed partial class NetClient : ICraftRequests, ICraftCancelChecked, ICraftIntoRequests
    {
    }

    /// <summary>Строка списка рецептов для окна крафта (E2-3 → шаг 2.4).</summary>
    public readonly struct CraftLine
    {
        /// <summary>Номер рецепта.</summary>
        public readonly byte Recipe;

        /// <summary>Что скажет сервер на заявку «× 1» по своим данным клиента: «принято» или причина (нет станка, далеко,
        /// не хватает, очередь полна, мёртв).</summary>
        public readonly CraftReply State;

        /// <summary>На сколько изготовлений хватает ингредиентов в сумке и в сундуках рядом (E2-10, сводка сервера).</summary>
        public readonly int Affordable;

        public CraftLine(byte recipe, CraftReply state, int affordable)
        {
            Recipe = recipe;
            State = state;
            Affordable = affordable;
        }

        /// <summary>Можно делать прямо сейчас («белая» строка, <c>06</c> 3.3).</summary>
        public bool CanMake => State == CraftReply.Accepted;
    }

    /// <summary>
    /// «Стол» окна крафта (E2-3): общий путь для окна игрока (шаг 2.4) и для ботов / тестов — как <see cref="BagHand"/>
    /// у сумки. Правил игры здесь нет: строки списка считаются теми же функциями ядра (<see cref="CraftRules"/>), что
    /// проверяет сервер, а решает всё равно он. Заявки не предсказываются.
    /// </summary>
    public sealed class CraftDesk
    {
        private readonly ICraftRequests _requests;

        public CraftDesk(ICraftRequests requests)
        {
            _requests = requests;
        }

        /// <summary>Заявок «изготовить» / «отменить» отправлено.</summary>
        public int MakeRequests { get; private set; }
        public int CancelRequests { get; private set; }

        /// <summary>
        /// Строка рецепта по данным клиента: сумка (<paramref name="bag"/>), очередь (<paramref name="queue"/>), свой мир
        /// и позиция (станок рядом), жив ли персонаж; <paramref name="stock"/> — сводка сервера «сундуки рядом» (E2-10,
        /// <see cref="NetClient.NearbyStock"/>), её предметы считаются «есть» наравне с сумкой; null — только сумка.
        /// </summary>
        public static CraftLine LineOf(int recipe, in Inventory bag, in CraftQueue queue, WorldModel world, WorldPos pos, bool alive = true,
            ChestStock? stock = null)
        {
            if (!RecipeTable.Exists(recipe))
            {
                return new CraftLine(0, CraftReply.NoRecipe, 0);
            }

            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            int can = CraftRules.Affordable(in bag, in def, stock);
            CraftReply state = CraftReply.Accepted;
            if (!alive)
            {
                state = CraftReply.Dead;
            }
            else if (queue.RoomFor(def.Id) == 0)
            {
                state = CraftReply.QueueFull;
            }
            else
            {
                state = CraftRules.StationCheck(world, pos, def.Station);
                if (state == CraftReply.Accepted && can == 0)
                {
                    state = CraftReply.NoIngredients;
                }
            }

            return new CraftLine(def.Id, state, can);
        }

        /// <summary>
        /// Сколько не хватает ингредиента № <paramref name="i"/> на одно изготовление (0 — хватает) — подпись «красной»
        /// строки; с <paramref name="stock"/> — с учётом сундуков рядом (E2-10).
        /// </summary>
        public static int Missing(int recipe, int i, in Inventory bag, ChestStock? stock = null) =>
            Math.Max(0, Need(recipe, i) - Have(recipe, i, bag, stock));

        /// <summary>Сколько ингредиента № <paramref name="i"/> «есть»: сумка + сундуки рядом (E2-10) — число «есть / нужно» окна.</summary>
        public static int Have(int recipe, int i, in Inventory bag, ChestStock? stock = null)
        {
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            if (i >= def.IngredientCount)
            {
                return 0;
            }

            byte item = def.IngredientItem(i);
            return bag.Count(item) + (stock?.Count(def.Station, item) ?? 0);
        }

        /// <summary>Сколько ингредиента № <paramref name="i"/> лежит в сундуках рядом (E2-10) — пометка «в сундуке».</summary>
        public static int InChests(int recipe, int i, ChestStock? stock)
        {
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            return i < def.IngredientCount && stock != null ? stock.Count(def.Station, def.IngredientItem(i)) : 0;
        }

        private static int Need(int recipe, int i)
        {
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            return i < def.IngredientCount ? def.IngredientCountOf(i) : 0;
        }

        /// <summary>Кнопка «сделать» (× 1, × 10…): заявка уходит, только если строка «белая»; иначе — false без заявки.</summary>
        public bool Make(in CraftLine line, int count = 1)
        {
            if (!line.CanMake || count <= 0 || !_requests.RequestCraft(line.Recipe, count))
            {
                return false;
            }

            MakeRequests++;
            return true;
        }

        /// <summary>
        /// Отмена позиции очереди. <paramref name="queue"/> — очередь, которую видит окно: её рецепт в позиции едет в заявке
        /// для сверки (<see cref="ICraftCancelChecked"/>), чтобы при сдвинувшейся очереди не отменилась соседняя.
        /// </summary>
        /// <summary>Заявок «изготовить в ячейку» отправлено (C-013).</summary>
        public int MakeIntoRequests { get; private set; }

        /// <summary>
        /// C-013: изготовить рецепт строки × <paramref name="count"/> сразу в ячейку сумки <paramref name="cell"/> (0xFF — куда
        /// влезет). Очередь не нужна (полная очередь не мешает); нельзя по строке (нет станка, материалов, мёртв) или заявитель
        /// не умеет такую заявку — false, ничего не ушло. Место в ячейке решает сервер (отказ «некуда»).
        /// </summary>
        public bool MakeInto(in CraftLine line, int cell, int count = 1)
        {
            bool can = line.CanMake || (line.State == CraftReply.QueueFull && line.Affordable > 0);
            if (!can || count <= 0 || _requests is not ICraftIntoRequests into || !into.RequestCraftInto(line.Recipe, cell, count))
            {
                return false;
            }

            MakeIntoRequests++;
            return true;
        }

        public bool Cancel(in CraftQueue queue, int index)
        {
            if ((uint)index >= (uint)queue.Count)
            {
                return false;
            }

            bool sent = _requests is ICraftCancelChecked checkedCancel
                ? checkedCancel.RequestCraftCancel(index, queue.RecipeAt(index))
                : _requests.RequestCraftCancel(index);
            if (!sent)
            {
                return false;
            }

            CancelRequests++;
            return true;
        }
    }
}
