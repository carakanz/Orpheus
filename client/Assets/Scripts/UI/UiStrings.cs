#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Строки интерфейса (E2-2) — все русские подписи окон в одном файле. Названия предметов — по ключу строки
    /// таблицы предметов (<see cref="ItemDef.Key"/>); нет подписи — показывается сам ключ (новая строка таблицы).
    /// </summary>
    public static class UiStrings
    {
        public const string BagTitle = "Сумка";
        public const string BagHint = "ЛКМ — взять / положить      ПКМ — взять 1 (ещё ПКМ — ещё 1)\nShift+ЛКМ — половину      Ctrl+ЛКМ — перекинуть      ЛКМ мимо окна — выбросить";
        public const string BagDead = "Вы погибли: сумка откроется для действий после возрождения";
        public const string BagKeys = "Q / I";

        // ---------- C-008: окна как в Terraria ----------

        /// <summary>Подсказка клавиш сумки — одной строкой мелко под сеткой (полная — <see cref="BagHint"/>, при наведении).</summary>
        public const string BagHintShort = "ПКМ — 1   Shift — половина   Ctrl — перекинуть";
        public const string BagTitleKeys = "Сумка  (Q)";
        public const string ChestTitleKeys = "Сундук  (F)";
        public const string ChestTakeAll = "Взять всё";
        public const string ChestPutAll = "Положить всё";
        /// <summary>C-008: почему «Взять / Положить всё» остановился (null — сказать нечего).</summary>
        public static string? MoveAllStopped(MoveAllStop why) => why switch
        {
            MoveAllStop.NoRoom => "Не всё влезло — нет места",
            MoveAllStop.NotAccepted => "Сервер не принял перенос",
            MoveAllStop.Dead => "Мёртвый не переносит",
            _ => null,
        };

        public const string ChestTakeAllTip = "Всё из сундука — в сумку";
        public const string ChestPutAllTip = "Сумка (без быстрой панели) — в сундук";
        public const string CraftTitleKeys = "Изготовление  (C)";
        public const string CraftMakeTip = "Щелчок по значку — выбрать, по ячейке сумки — сделать туда (Shift — 5, Ctrl — сколько влезет; Enter — в сумку)";
        public const string CraftPlaceTip = "Щелчок по значку — выбрать; по земле — сделать и поставить, по ячейке сумки — сделать туда (ПКМ / Esc — снять)";

        /// <summary>C-013: выбран рецепт не-постройки, курсор над миром — подсказка у курсора.</summary>
        public static string ArmedNote(byte item) => $"{ItemName(item)}: щёлкни по ячейке сумки   (ПКМ / Esc — снять)";
        public const string AbilityLocked = "Способность — откроется позже";
        public const string PassiveLocked = "Пассивная способность — откроется позже";
        public const string MapCollapsed = "Карта (Shift+M)"; // C-029: M — карта на весь экран
        public const string CraftEmptyShort = "Здесь пока нет рецептов";
        public const string CraftEmptyFilteredShort = "Сейчас ничего не сделать — Ins: показать все";

        public static string FilterTip(bool on) => on ? "Только доступные: вкл (Ins)" : "Только доступные: выкл (Ins)";

        public static string HealthTip(int cur, int max) => $"Здоровье {cur} / {max}";

        // ---------- E3-1: смерть и возрождение ----------

        public const string DeathTitle = "Вы погибли";

        /// <summary>Кто убил: враг по архетипу, другой игрок, неизвестно.</summary>
        public static string KilledBy(bool known, bool character, byte archetype) =>
            !known ? "Причина смерти неизвестна"
            : character ? "Вас убил другой игрок"
            : archetype == EnemyArchetype.Melee ? "Вас убил разбойник"
            : "Вас убил враг";

        /// <summary>Таймер возрождения: «Возрождение у Очага через 4 с»; 0 — «Возрождение…».</summary>
        public static string RespawnIn(int seconds, bool atBed) =>
            seconds > 0 ? $"Возрождение {(atBed ? "у кровати" : "у Очага")} через {seconds} с" : $"Возрождение {(atBed ? "у кровати" : "у Очага")}…";

        public static string Respawned(bool atBed) => atBed ? "Вы возродились у кровати" : "Вы возродились у Очага";

        /// <summary>Имя элитного врага над полоской (в срезе элитных нет — стенд снимков, E3-1).</summary>
        public const string EliteRogue = "Главарь разбойников";

        public static string StaminaTip(int cur, int max) => $"Выносливость {cur} / {max}";

        static readonly Dictionary<string, string> ItemNames = new Dictionary<string, string>
        {
            ["stone_pickaxe"] = "Каменная кирка",
            ["stone_axe"] = "Каменный топор",
            ["hammer"] = "Молот",
            ["wooden_sword"] = "Деревянный меч",
            ["simple_bow"] = "Простой лук",
            ["torch"] = "Факел",
            ["copper_pickaxe"] = "Медная кирка",
            ["wood"] = "Древесина",
            ["stone"] = "Камень",
            ["resin"] = "Смола",
            ["fiber"] = "Волокно",
            ["copper_ore"] = "Медная руда",
            ["arrows"] = "Стрелы",
            ["copper_ingot"] = "Медный слиток",
            ["rope"] = "Верёвка",
            ["copper_axe"] = "Медный топор",
            ["copper_sword"] = "Медный меч",
            ["copper_bow"] = "Медный лук",
            ["meat"] = "Мясо",
            ["roast_meat"] = "Жареное мясо",
            // E2-11: постройки — предметы.
            ["wood_floor"] = "Пол деревянный",
            ["stone_floor"] = "Пол каменный",
            ["platform"] = "Настил",
            ["wood_wall"] = "Стена деревянная",
            ["stone_wall"] = "Стена каменная",
            ["wood_fence"] = "Забор",
            ["door"] = "Дверь",
            ["campfire"] = "Костёр",
            ["workbench"] = "Верстак",
            ["chest"] = "Сундук",
            ["bed"] = "Кровать",
        };

        // ---------- окно крафта (E2-4) ----------

        public const string CraftTitle = "Изготовление";
        public const string CraftNeeds = "Нужно:";
        public const string CraftInChest = " (в сундуке)";
        public const string CraftQueue = "Очередь — щелчок по позиции отменяет её";
        public const string CraftQueueEmpty = "Очередь пуста";
        public const string CraftQueueStopped = "Стоит: подойдите к станку";
        public const string CraftDead = "Вы погибли: изготовление — после возрождения";
        public const string CraftMake1 = "Сделать 1";
        public const string CraftMake5 = "Сделать 5";
        public const string CraftMakeAll = "Сделать всё";

        // ---------- окно «сумка + изготовление» (E2-9) ----------

        public const string CraftKeyInv = "Q · C · Esc";
        public const string CraftOnlyAvailable = "Только доступные";

        /// <summary>C-029: карта на весь экран.</summary>
        public const string WorldMapTitle = "Карта";

        public const string WorldMapHelp = "M / Esc — закрыть · колесо — масштаб · ЛКМ — тянуть · ПКМ — к себе · золотая — вы, белое кольцо — Очаг, оранжевые — кровати";

        public static string WorldMapCoords(int x, int y, double tilesPerPixel) =>
            $"{x}, {y} · {(tilesPerPixel < 1 ? $"×{1 / tilesPerPixel:0}" : $"1:{tilesPerPixel:0}")}";

        /// <summary>
        /// C-029: подсказка ингредиента — название, описание; базовый — «Где взять:» и строки источников («?» — не открыто),
        /// небазовый — «ЛКМ — открыть рецепт».
        /// </summary>
        public static string IngredientTip(byte item, string description, bool isBase, List<string>? where)
        {
            var sb = new System.Text.StringBuilder(ItemName(item));
            if (description.Length > 0)
            {
                sb.Append('\n').Append(description);
            }

            if (!isBase)
            {
                sb.Append("\nЛКМ — открыть рецепт");
            }
            else
            {
                sb.Append("\nГде взять:");
                if (where == null || where.Count == 0)
                {
                    sb.Append(" ?");
                }
                else
                {
                    foreach (string w in where)
                    {
                        sb.Append("\n  ").Append(w);
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>C-029: остаток ингредиента после изготовления (красным у значка).</summary>
        public static string IngredientLeft(int left) => $"→{left}";

        /// <summary>C-029: пипетка не по постройке (трава, валун, дерево).</summary>
        public const string PipetteNothing = "Пипетка: здесь нет постройки";

        /// <summary>C-029: постройка уже есть в сумке — из меню ставится она, не изготавливается новая.</summary>
        public static string ReadyInBag(int n) => $"В сумке: {n} — ставится из сумки";
        public const string CraftFlat = "Все рецепты раздела";
        public const string CraftEmptySub = "Здесь пока нет рецептов";
        public const string CraftEmptyFiltered = "Сейчас здесь ничего не сделать\nснимите «Только доступные» (Ins)";
        public const string CraftPick = "Выберите рецепт в сетке слева";
        public const string CraftHintInv = "Enter — сделать 1   Shift+Enter — 5   Ctrl+Enter — всё   Del — отменить последнюю   Ins — только доступные\nPgUp / PgDn — рецепт   Home / End — раздел   Shift+Home / End — подраздел   колесо — прокрутка";

        static readonly Dictionary<string, string> ShelfNames = new Dictionary<string, string>
        {
            ["buildings"] = "Здания",
            ["furniture"] = "Мебель",
            ["tools"] = "Инструменты",
            ["weapons"] = "Оружие",
            ["armor"] = "Броня",
            ["materials"] = "Материалы",
            ["food"] = "Еда",
            ["buildings/wood"] = "Дерево",
            ["buildings/stone"] = "Камень",
            ["buildings/copper"] = "Медь",
            ["furniture/stations"] = "Станки",
            ["furniture/storage"] = "Хранение",
            ["furniture/sleep"] = "Сон",
            ["furniture/light"] = "Свет",
            ["furniture/misc"] = "Прочее",
            ["tools/picks"] = "Кирки",
            ["tools/axes"] = "Топоры",
            ["tools/hammers"] = "Молоты",
            ["weapons/swords"] = "Мечи",
            ["weapons/bows"] = "Луки",
            ["weapons/ammo"] = "Стрелы",
            ["armor/head"] = "Шлемы",
            ["armor/body"] = "Доспехи",
            ["armor/legs"] = "Поножи",
        };

        /// <summary>Название раздела по ключу (<c>CraftShelf.Sections</c>); нет подписи — сам ключ.</summary>
        public static string SectionName(string key) => ShelfNames.TryGetValue(key, out string? n) ? n : key;

        /// <summary>Название подраздела по ключам раздела и подраздела.</summary>
        public static string SubName(string section, string sub) => ShelfNames.TryGetValue(section + "/" + sub, out string? n) ? n : sub;

        /// <summary>
        /// Подсказка значка рецепта: название (× выход), ингредиенты «есть / нужно», станок и что сейчас мешает.
        /// <paramref name="have"/> — сколько каждого ингредиента есть (то же число, что в карточке).
        /// </summary>
        public static string RecipeTip(int recipe, Orpheus.Net.Session.CraftLine line, int have0, int have1, int have2)
        {
            ref readonly RecipeDef def = ref RecipeTable.Get(recipe);
            var sb = new System.Text.StringBuilder(RecipeName(recipe));
            if (def.ResultCount > 1)
            {
                sb.Append(" × ").Append(def.ResultCount);
            }

            for (int i = 0; i < def.IngredientCount; i++)
            {
                int have = i == 0 ? have0 : i == 1 ? have1 : have2;
                sb.Append('\n').Append(ItemName(def.IngredientItem(i))).Append("  ").Append(have).Append(" / ").Append(def.IngredientCountOf(i));
            }

            sb.Append('\n').Append(def.Station == CraftStation.None ? "Руками, где угодно" : "Станок: " + StationName(def.Station).ToLowerInvariant());
            sb.Append('\n').Append(CraftState(line.State, def.Station, line.Affordable));
            return sb.ToString();
        }

        /// <summary>Кнопка «всё», когда известно, на сколько хватает.</summary>
        public static string CraftMakeAllOf(int n) => $"Все {n}";

        /// <summary>Вкладка окна крафта: название станка.</summary>
        public static string StationName(CraftStation s) => s switch
        {
            CraftStation.Workbench => "Верстак",
            CraftStation.Campfire => "Костёр",
            _ => "Руками",
        };

        /// <summary>Пометка рецепта, станка которого рядом нет.</summary>
        public static string StationNeeded(CraftStation s) => s switch
        {
            CraftStation.Workbench => "нужен верстак",
            CraftStation.Campfire => "нужен костёр",
            _ => string.Empty,
        };

        /// <summary>Подсказка у быстрой панели: рядом станок, окно крафта — клавиша C.</summary>
        public static string StationHint(CraftStation s) => s == CraftStation.Campfire ? "C — костёр" : "C — верстак";

        /// <summary>Название рецепта = название того, что получается (в срезе у предмета один рецепт).</summary>
        public static string RecipeName(int recipe) => ItemName(RecipeTable.Get(recipe).Result);

        /// <summary>Строка под названием в карточке: сколько получится и за сколько.</summary>
        public static string RecipeYield(in RecipeDef def) =>
            $"Получится: {def.ResultCount}    Время: {def.Ticks / 30f:0.#} с    {StationName(def.Station)}";

        /// <summary>Состояние рецепта в карточке: «можно сделать N» или почему нельзя.</summary>
        public static string CraftState(CraftReply state, CraftStation station, int affordable) => state switch
        {
            CraftReply.Accepted => $"Хватает на {affordable}",
            CraftReply.NoStation => station == CraftStation.Campfire ? "Нужен костёр рядом" : "Нужен верстак рядом",
            CraftReply.StationFar => station == CraftStation.Campfire ? "Костёр слишком далеко" : "Верстак слишком далеко",
            CraftReply.NoIngredients => "Не хватает материалов",
            CraftReply.QueueFull => "Очередь полна",
            CraftReply.Dead => "Вы погибли",
            _ => CraftRefusal(state, station),
        };

        /// <summary>Отказ сервера (сообщение 31) — короткая строка внизу окна.</summary>
        public static string CraftRefusal(CraftReply reply, CraftStation station) => reply switch
        {
            CraftReply.NoStation => station == CraftStation.Campfire ? "Отказ: рядом нет костра" : "Отказ: рядом нет верстака",
            CraftReply.StationFar => "Отказ: слишком далеко от станка",
            CraftReply.NoIngredients => "Отказ: не хватает материалов",
            CraftReply.QueueFull => "Отказ: очередь полна",
            CraftReply.Dead => "Отказ: вы погибли",
            CraftReply.Paused => "Отказ: игра на паузе",
            CraftReply.Busy => "Отказ: слишком много заявок, повторите",
            CraftReply.NoEntry => "Отказ: такой позиции в очереди уже нет",
            CraftReply.NoRecipe => "Отказ: нет такого рецепта",
            CraftReply.BadCount => "Отказ: неверное количество",
            CraftReply.NoEntity => "Отказ: персонажа нет в мире",
            CraftReply.NoRoom => "Некуда: ячейка занята другим или стопка полна",
            _ => "Отказ сервера",
        };

        /// <summary>Подсказка позиции очереди.</summary>
        public static string QueueTip(int recipe, int left) => $"{RecipeName(recipe)}: осталось {left} — щелчок отменит";

        /// <summary>Название предмета для подсказки.</summary>
        public static string ItemName(byte item)
        {
            string key = ItemTable.Get(item).Key;
            return ItemNames.TryGetValue(key, out string? name) ? name : key;
        }

        /// <summary>Подсказка ячейки: название и количество в стопке.</summary>
        public static string ItemTip(byte item, int count) => count > 1 ? $"{ItemName(item)} × {count}" : ItemName(item);

        // ---------- режим стройки, сундук, F (E2-5) ----------

        static readonly Dictionary<ushort, string> BuildNames = new Dictionary<ushort, string>
        {
            [Orpheus.Core.World.BuildDefs.WoodFloorKey] = "Пол деревянный",
            [Orpheus.Core.World.BuildDefs.StoneFloorKey] = "Пол каменный",
            [Orpheus.Core.World.BuildDefs.PlatformKey] = "Настил",
            [Orpheus.Core.World.ObjectId.WoodWall] = "Стена деревянная",
            [Orpheus.Core.World.ObjectId.StoneWall] = "Стена каменная",
            [Orpheus.Core.World.ObjectId.WoodFence] = "Забор",
            [Orpheus.Core.World.ObjectId.Door] = "Дверь",
            [Orpheus.Core.World.ObjectId.Torch] = "Факел",
            [Orpheus.Core.World.ObjectId.Campfire] = "Костёр",
            [Orpheus.Core.World.ObjectId.Workbench] = "Верстак",
            [Orpheus.Core.World.ObjectId.Chest] = "Сундук",
            [Orpheus.Core.World.ObjectId.Bed] = "Кровать",
        };

        /// <summary>Название постройки по ключу (лента, призрак).</summary>
        public static string BuildName(ushort key)
        {
            if (key > ObjectId.IdMask && !ObjectId.IsPart(key))
            {
                key = ObjectId.IdOf(key); // E2-16: повёрнутая постройка — то же имя
            }

            return BuildNames.TryGetValue(key, out string? n) ? n : key.ToString();
        }

        public const string BuildDead = "Вы погибли: строить — после возрождения";

        /// <summary>Строка у курсора: «Сундук — нельзя: занято» / «Починить: 1 камень».</summary>
        public static string GhostNote(bool repair, bool ok, string reason, ushort key, string cost) =>
            repair ? (ok ? $"Починить: {cost}" : $"Починить нельзя: {reason}")
            : ok ? $"{BuildName(key)}: {cost}" : $"{BuildName(key)} — нельзя: {reason}";

        /// <summary>Подсказка у призрака постановки (E2-11): что жать и сколько в руке.</summary>
        public static string PlaceHint(int inHand) => $"ЛКМ — поставить, зажать — подряд (в руке {inHand})";

        /// <summary>E2-13: подпись режима постановки у панели.</summary>
        public static string PlaceModeLabel(bool ruler) => ruler ? "Линейка   (Tab — поштучно, G — пипетка)" : "Поштучно   (Tab — линейка, G — пипетка)"; // E2-16: R — поворот; C-029: G / средняя кнопка — пипетка

        /// <summary>C-015: подсказка значка режима у быстрой панели (линейка / шахматка).</summary>
        public static string PlaceModeTip(bool ruler) => ruler
            ? "Линейка: зажать ЛКМ и протянуть — стены и заборы линией, пол прямоугольником\nЩелчок или Tab — поштучно"
            : "Поштучно: щелчок — одна постройка, зажать — по ячейкам под курсором\nЩелчок или Tab — линейка";

        /// <summary>Подпись режима с поворотом и «из меню» (E2-16): «R — повернуть», «Esc / ПКМ — убрать».</summary>
        public static string PlaceModeLabel(bool ruler, bool rotatable, bool menu, int available) =>
            (rotatable ? "R — поворот   " : string.Empty)
            + (menu ? $"из меню: {available}  (Esc — убрать)" : PlaceModeLabel(ruler));

        /// <summary>E2-13: подсказка у призрака в режиме линейки.</summary>
        public static string RulerHint(Orpheus.Net.Session.RulerKind kind, int inHand) =>
            kind == Orpheus.Net.Session.RulerKind.Rect
                ? $"ЛКМ — тянуть прямоугольник, отпустить — поставить (в руке {inHand})"
                : $"ЛКМ — тянуть линию, Shift — по диагонали (в руке {inHand})";

        /// <summary>E2-13: строка у курсора при протягивании — длина или площадь, «нужно N, есть M», отмена.</summary>
        public static string RulerNote(Orpheus.Net.Session.RulerKind kind, int cells, int w, int h, int need, int have)
        {
            string size = kind == Orpheus.Net.Session.RulerKind.Rect
                ? $"{Half(w)} × {Half(h)} = {Quarter(w * h)} м²" // E2-15: стороны в ячейках 0,5
                : $"длина {cells / 2}{(cells % 2 == 1 ? ",5" : string.Empty)} м";
            return $"{size} · нужно {need}, есть {have} · ПКМ / Esc — отмена";
        }

        // Ячейки 0,5 → метры: 3 → «1,5».
        static string Half(int cells) => $"{cells / 2}{(cells % 2 == 1 ? ",5" : string.Empty)}";

        // Ячейки 0,5 × 0,5 → м²: 6 → «1,5», 1 → «0,25».
        static string Quarter(int cells) => (cells % 4) switch
        {
            0 => $"{cells / 4}",
            1 => $"{cells / 4},25",
            2 => $"{cells / 4},5",
            _ => $"{cells / 4},75",
        };

        /// <summary>Прочность постройки под курсором.</summary>
        public static string Integrity(int cur, int max) => $"Прочность {cur} / {max}";

        public const string ChestTitle = "Сундук";
        public const string ChestHint = "ЛКМ — взять / положить      ПКМ — взять 1      Shift+ЛКМ — половину\nCtrl+ЛКМ — в сумку / в сундук";
        public const string ChestKeys = "F, Esc";

        /// <summary>Подсказка над панелью: что сделает F.</summary>
        public static string UseHint(Orpheus.Net.Session.UseKind kind) => kind switch
        {
            Orpheus.Net.Session.UseKind.Door => "F — закрыть дверь",
            // C-013: «F — верстак не нужно» — у станков действия F нет (ObjectUse.KindOf), подсказки тоже.
            Orpheus.Net.Session.UseKind.Chest => "F / ПКМ — сундук", // C-030: ПКМ по сундуку и кровати — как F
            Orpheus.Net.Session.UseKind.Bed => "F / ПКМ — лечь в кровать", // C-030
            _ => string.Empty,
        };

        public const string BedSet = "Точка возрождения установлена"; // C-030 (было «Кровать: здесь вы возродитесь»)

        /// <summary>Отказ сервера на действие с объектом.</summary>
        public static string UseRefusal(string reason) => $"Нельзя: {reason}";
    }
}
