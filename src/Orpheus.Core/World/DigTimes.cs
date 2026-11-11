using System;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Вид инструмента копания (<c>06</c> 2.3, 1-010). Руки — « ничем»: копается только то, где руки разрешены строкой таблицы
    /// (<see cref="DigTimes.SetDig"/> с <see cref="Hands"/>).
    /// </summary>
    public enum ToolKind : byte
    {
        /// <summary>Руки (предмет в руках — <see cref="Sim.Items.None"/>).</summary>
        Hands = 0,

        /// <summary>Топор: дерево, пень, куст (<c>06</c> 2.3).</summary>
        Axe = 1,

        /// <summary>Кирка: скала, гранит, залежь (<c>06</c> 2.3).</summary>
        Pick = 2,

        /// <summary>Молот: снос построек (<c>05</c> 1.3).</summary>
        Hammer = 3,

        /// <summary>Число видов (размер таблицы инструментов).</summary>
        Count = 4,
    }

    /// <summary>
    /// Время копания — «база × модификатор предмета» (слово Автора 08.11, <c>BRIEF.md</c> п. 11b; 0-034) и ярус инструмента (1-010,
    /// <c>01</c> 5 «Разрушаемость рельефа», <c>06</c> 2.3—2.4). Одно место, данные, без рефлексии (IL2CPP): база — кадров ввода 60 Гц
    /// с зажатым копанием; на объект — строки «инструмент + минимальный ярус + модификатор» (<see cref="SetDig"/>).
    /// Ярус выше нужного копать вдвое быстрее за ярус, потолок ÷4 (<c>01</c> 5, «06» 2.3):
    /// кадры = база × модификатор ÷ (100 &lt;&lt; min(2, ярус − нужный)).
    /// <para>
    /// <see cref="FramesFor"/>: &gt; 0 — кадров; 0 — «нельзя» (<see cref="Cannot"/>); −1 — «нужен инструмент» (<see cref="NeedTool"/>);
    /// −2 — «нужен ярус выше» (<see cref="NeedTier"/>, «нужна кирка: медная»).
    /// </para>
    /// <para>
    /// Числа — <i>(прикидка 0-034 + карточка 1-010)</i>: база 15 кадров = 0,25 с; скала киркой 1 ×1, дерево топором 1 ×1,2,
    /// залежь меди киркой 1 ×2, пень топором 1 ×0,6, куст топором 1 ×0,5 (руками ×1), гранит киркой 2 ×1,5;
    /// постройки — кирка ×8 (2 с, <c>05</c> 1.3), молот ×2 (0,5 с).
    /// </para>
    /// Таблица — у каждого, кто считает копание (сервер, клиент, бот): они должны совпадать, по умолчанию — одна и та же.
    /// </summary>
    public sealed class DigTimes
    {
        /// <summary>База по умолчанию: 15 кадров = 0,25 с.</summary>
        public const int DefaultBaseFrames = 15;

        /// <summary>Потолок ускорения ярусом: ÷4 (двух ярусов выше нужного), «06» 2.3.</summary>
        public const int TierSpeedupCap = 2;

        /// <summary>«Нельзя разрушить»: нет ни одной подходящей строки инструмента.</summary>
        public const int Cannot = 0;

        /// <summary>«Нужен инструмент»: у объекта есть строки, но не этим инструментом («нужен топор», «нужен инструмент: кирка»).</summary>
        public const int NeedTool = -1;

        /// <summary>«Нужен ярус выше»: инструмент тот, ярус ниже минимального («нужна кирка: медная»).</summary>
        public const int NeedTier = -2;

        private readonly int[] _modifiers = new int[ObjectId.MaxId * (int)ToolKind.Count];

        private readonly byte[] _reqTier = new byte[ObjectId.MaxId * (int)ToolKind.Count];

        private int _base = DefaultBaseFrames;

        /// <summary>Кадров базы на тайл объекта при модификаторе 100 (15 = 0,25 с); 0 — всё неразрушимо; отрицательное — исключение.</summary>
        public int BaseFrames
        {
            get => _base;
            set => _base = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(BaseFrames), value, "База копания не может быть отрицательной.");
        }

        public DigTimes()
        {
            // Природные объекты (карточка 1-010, `01` 5, `06` 2.4): инструмент своего яруса.
            SetDig(ObjectId.Rock, ToolKind.Pick, 1, 100);
            SetDig(ObjectId.Granite, ToolKind.Pick, 2, 150);
            SetDig(ObjectId.Ore, ToolKind.Pick, 1, 200);
            SetDig(ObjectId.Tree, ToolKind.Axe, 1, 120);
            SetDig(ObjectId.Stump, ToolKind.Axe, 1, 60);
            SetDig(ObjectId.Bush, ToolKind.Axe, 1, 50);
            SetDig(ObjectId.Bush, ToolKind.Hands, 1, 100); // карточка: «руками, если разрешено»

            // Постройки (1-010, `05` 1.3): кирка 2 с (×8), молот 0,5 с (×2). Пень — не постройка, Natural-флага у него нет.
            for (int id = 1; id <= ObjectId.MaxKnownId; id++)
            {
                if (!ObjectId.IsNatural((ushort)id) && id != ObjectId.Stump)
                {
                    SetDig((ushort)id, ToolKind.Pick, 1, 800);
                    SetDig((ushort)id, ToolKind.Hammer, 1, 200);
                }
            }
        }

        /// <summary>Модификатор в процентах инструмента <paramref name="tool"/> для id (<see cref="ObjectId.IdOf"/>); 0 — этим инструментом нельзя.</summary>
        public int ModifierOf(ushort id, ToolKind tool)
        {
            int slot = Slot(id, tool);
            return _reqTier[slot] == 0 ? 0 : _modifiers[slot];
        }

        /// <summary>Минимальный ярус инструмента <paramref name="tool"/> для id; 0 — этим инструментом нельзя.</summary>
        public byte RequiredTierOf(ushort id, ToolKind tool) => _reqTier[Slot(id, tool)];

        /// <summary>
        /// Строка таблицы: id копается инструментом <paramref name="tool"/> начиная с яруса <paramref name="reqTier"/>
        /// с модификатором <paramref name="modifier"/> процентов (100 = ×1; ≤ 0 — строки нет, копаться не будет).
        /// </summary>
        public void SetDig(ushort id, ToolKind tool, byte reqTier, int modifier)
        {
            if (id == 0 || id >= ObjectId.MaxId)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "SetDig: пустой или неизвестный id объекта.");
            }

            int slot = Slot(id, tool);
            _reqTier[slot] = modifier > 0 && reqTier >= 1 && reqTier <= byte.MaxValue ? reqTier : (byte)0;
            _modifiers[slot] = Math.Max(0, modifier);
        }

        /// <summary>«Время по своей силе» (калибровка 0-034): модификатор первой строки id; 0 — нельзя; ≤ 0 — снимаются все строки.</summary>
        public void SetModifier(ushort id, int value)
        {
            if (id == 0 || id >= ObjectId.MaxId)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "SetModifier: пустой или неизвестный id объекта.");
            }

            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "SetModifier: модификатор не может быть отрицательным.");
            }

            if (value == 0)
            {
                for (var tool = 0; tool < (int)ToolKind.Count; tool++)
                {
                    _reqTier[id * (int)ToolKind.Count + tool] = 0;
                    _modifiers[id * (int)ToolKind.Count + tool] = 0;
                }

                return;
            }

            for (var tool = 0; tool < (int)ToolKind.Count; tool++)
            {
                int slot = id * (int)ToolKind.Count + tool;
                if (_reqTier[slot] != 0)
                {
                    _modifiers[slot] = value;
                }
            }
        }

        /// <summary>Модификатор первой строки id (калибровка 0-034); 0 — объект нельзя разрушить.</summary>
        public int ModifierOf(ushort id)
        {
            for (var tool = 0; tool < (int)ToolKind.Count; tool++)
            {
                int slot = id * (int)ToolKind.Count + tool;
                if (_reqTier[slot] != 0)
                {
                    return _modifiers[slot];
                }
            }

            return 0;
        }

        /// <summary>
        /// Кадров копания объекта инструментом <paramref name="tool"/> яруса <paramref name="tier"/>:
        /// &gt; 0 — кадров до снятия; <see cref="Cannot"/> — нельзя; <see cref="NeedTool"/> — нужен другой инструмент
        /// (имя — <see cref="ToolNameOf"/> у первой строки); <see cref="NeedTier"/> — нужен ярус выше
        /// (имя — <see cref="RequiredTierOf"/>).
        /// </summary>
        public int FramesFor(ushort raw, ToolKind tool, byte tier)
        {
            ushort id = ObjectId.IdOf(raw);
            if (id == 0 || ObjectId.IsPart(raw))
            {
                return Cannot; // пустого тайла в запросе быть не может; часть многотайлового объекта — за строителем (1-011)
            }

            if (_base == 0)
            {
                return Cannot; // база 0 — всё неразрушимо (0-034)
            }

            int slot = Slot(id, tool);
            byte req = _reqTier[slot];
            if (req == 0)
            {
                return HasAnyRow(id) ? NeedTool : Cannot;
            }

            if (tier < req && !(tool == ToolKind.Hands && req == 1))
            {
                return NeedTier; // руки роют там, где разрешено строкой (карточка: «руками, если разрешено»)
            }

            int speed = 100 << Math.Min(TierSpeedupCap, Math.Max(0, tier - req));
            long num = (long)_base * _modifiers[slot];
            return Math.Max(1, (int)((num + speed / 2) / speed)); // округление к ближайшему, не меньше кадра (0-034)
        }

        /// <summary>
        /// Кадров «своим инструментом своего яруса» (калибровка, тесты, боты): первым инструментом по порядку
        /// <see cref="ToolKind"/> с подходящей строкой. 0 — нельзя (как в 0-034).
        /// </summary>
        public int FramesOf(ushort raw)
        {
            ushort id = ObjectId.IdOf(raw);
            if (id == 0 || ObjectId.IsPart(raw) || _base == 0)
            {
                return 0; // пустого, части объекта и базы 0 — времени нет (0-034)
            }

            for (var tool = 0; tool < (int)ToolKind.Count; tool++)
            {
                int slot = Slot(id, (ToolKind)tool);
                if (_reqTier[slot] != 0)
                {
                    return Math.Max(1, (int)(((long)_base * _modifiers[slot] + 50) / 100)); // к ближайшему, не меньше кадра
                }
            }

            return 0;
        }

        /// <summary>Копается ли объект вообще хоть чем (0-028: «нельзя» при старте копания).</summary>
        public bool CanDigAtAll(ushort raw) => ModifierOf(ObjectId.IdOf(raw)) > 0;

        /// <summary>
        /// Инструмент для строки отказа «нужен …»: первый подходящий id по порядку <see cref="ToolKind"/>;
        /// <see cref="ToolKind.Hands"/> — у объекта нет строк (копать нельзя).
        /// </summary>
        public ToolKind ToolKindFor(ushort raw)
        {
            ushort id = ObjectId.IdOf(raw);
            for (var tool = 1; tool < (int)ToolKind.Count; tool++) // руки — не «нужный инструмент»
            {
                if (_reqTier[Slot(id, (ToolKind)tool)] != 0)
                {
                    return (ToolKind)tool;
                }
            }

            return ToolKind.Hands;
        }

        /// <summary>Название инструмента для строк отказа (<c>05</c> 1.3): «топор», «кирка», «молот»; руки — «инструмент».</summary>
        public static string ToolName(ToolKind tool) => tool switch
        {
            ToolKind.Axe => "топор",
            ToolKind.Pick => "кирка",
            ToolKind.Hammer => "молот",
            _ => "инструмент",
        };

        /// <summary>Название яруса инструмента для строк отказа («медная», «железная»; 1 — «каменная»): род зависит от инструмента (<c>06</c> 2.3).</summary>
        public static string TierName(ToolKind tool, byte tier) => (tool, tier) switch
        {
            (ToolKind.Pick, 2) => "кирка: медная",
            (ToolKind.Pick, 3) => "кирка: железная",
            (ToolKind.Axe, 2) => "топор: медный",
            (ToolKind.Axe, 3) => "топор: железный",
            (ToolKind.Pick, 1) => "кирка: каменная",
            (ToolKind.Axe, 1) => "топор: каменный",
            _ => ToolName(tool),
        };

        private static int Slot(ushort id, ToolKind tool) => id * (int)ToolKind.Count + (int)tool;

        private bool HasAnyRow(ushort id)
        {
            for (var tool = 0; tool < (int)ToolKind.Count; tool++)
            {
                if (_reqTier[Slot(id, (ToolKind)tool)] != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
