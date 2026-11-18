using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Orpheus.Core.Sim;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>Откуда берётся базовый предмет: биом (заходил ли игрок) или враг (убил ли хоть одного).</summary>
    public enum SourceKind : byte
    {
        Biome = 0,
        Enemy = 1,
    }

    /// <summary>Одна строка «где взять»: биом / враг и чем добывать.</summary>
    public readonly struct ItemSource
    {
        public readonly SourceKind Kind;
        public readonly byte Id;
        public readonly string How;

        public ItemSource(SourceKind kind, byte id, string how)
        {
            Kind = kind;
            Id = id;
            How = how;
        }
    }

    /// <summary>
    /// Что открыл игрок (C-029, подсказки ингредиентов): биомы, в которые заходил, и враги, которых убил хоть одного. Ведёт
    /// клиент: биом — по полу тайла под персонажем (<see cref="BiomeOfFloor"/>; биома в мире клиента нет, пол его выдаёт:
    /// трава — луга, лесная подстилка — лес, камень — горы и пещеры вместе), враг — по сообщению о смерти от сервера. Хранится
    /// строкой (<see cref="Save"/> / <see cref="Load"/>) в файле клиента по зерну мира. <i>Допущение C-029:</i> открытия — у
    /// клиента, не в сохранении мира (в ядре учёта нет); другой компьютер — открывать заново.
    /// </summary>
    public sealed class Discovery
    {
        private readonly HashSet<byte> _biomes = new HashSet<byte>();
        private readonly HashSet<byte> _enemies = new HashSet<byte>();

        /// <summary>Изменилось с прошлого сохранения.</summary>
        public bool Dirty { get; private set; }

        public bool KnowsBiome(byte biome) => _biomes.Contains(biome);

        public bool KnowsEnemy(byte enemy) => _enemies.Contains(enemy);

        public bool Knows(in ItemSource s) => s.Kind == SourceKind.Biome ? KnowsBiome(s.Id) : KnowsEnemy(s.Id);

        /// <summary>Персонаж стоит на полу <paramref name="floor"/> — открыть его биом(ы).</summary>
        public void NoteFloor(byte floor)
        {
            switch (floor)
            {
                case FloorId.Grass:
                case FloorId.DryGrass:
                    NoteBiome(ItemSources.Meadow);
                    break;
                case FloorId.ForestFloor:
                    NoteBiome(ItemSources.Forest);
                    break;
                case FloorId.Stone:
                    NoteBiome(ItemSources.Mountain);
                    NoteBiome(ItemSources.Cave);
                    break;
            }
        }

        /// <summary>Биом по полу (для проверок): трава — луга, подстилка — лес, камень — горы; иначе 255.</summary>
        public static byte BiomeOfFloor(byte floor) => floor switch
        {
            FloorId.Grass or FloorId.DryGrass => ItemSources.Meadow,
            FloorId.ForestFloor => ItemSources.Forest,
            FloorId.Stone => ItemSources.Mountain,
            _ => 255,
        };

        public void NoteBiome(byte biome)
        {
            if (_biomes.Add(biome))
            {
                Dirty = true;
            }
        }

        public void NoteKill(byte enemy)
        {
            if (_enemies.Add(enemy))
            {
                Dirty = true;
            }
        }

        /// <summary>Строка для файла: «b:0,1;e:1».</summary>
        public string Save()
        {
            Dirty = false;
            var sb = new StringBuilder("b:");
            Join(sb, _biomes);
            sb.Append(";e:");
            Join(sb, _enemies);
            return sb.ToString();
        }

        /// <summary>Разобрать строку <see cref="Save"/>; мусор пропускается.</summary>
        public static Discovery Load(string? text)
        {
            var d = new Discovery();
            foreach (string part in (text ?? string.Empty).Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon < 1)
                {
                    continue;
                }

                HashSet<byte> into = part.Substring(0, colon).Trim() == "b" ? d._biomes : d._enemies;
                foreach (string v in part.Substring(colon + 1).Split(','))
                {
                    if (byte.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte id))
                    {
                        into.Add(id);
                    }
                }
            }

            return d;
        }

        private static void Join(StringBuilder sb, HashSet<byte> set)
        {
            var list = new List<byte>(set);
            list.Sort();
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append(i > 0 ? "," : string.Empty).Append(list[i].ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// Подсказки ингредиентов (C-029, слово Автора 17.11: «при наведении на ингредиенты в меню строительства должна выпадать
    /// подсказка что это. Если это не базовый предмет, то при нажатии левой кнопки мыши на нём открывать рецепт с ним. Если он
    /// базовый, то писать где его взять. Если биом или враг ещё не открыт (не зашёл, ни одного такого врага не убил) писать
    /// вопросик»). Описания и «где взять» — по вики (`wiki/catalog.json`: items, ores, enemies) и таблице добычи
    /// (<see cref="DigLoot"/>, <c>building.md</c>). Базовый — у предмета нет рецепта (<see cref="CraftRules.RecipeFor"/>).
    /// Номера биомов — как <c>Orpheus.WorldGen.Biome</c>; враги-звери (мясо) — свои номера, в срезе их ещё нет — всегда «?».
    /// </summary>
    public static class ItemSources
    {
        public const byte Meadow = 0, Forest = 1, Mountain = 4, Cave = 5;

        /// <summary>Враги: разбойник-мечник — архетип ближнего боя ядра; звери (вики, акт I) — 100+.</summary>
        public const byte Bandit = 1, Wolf = 100, Boar = 101, Deer = 102;

        /// <summary>Знак неоткрытого источника.</summary>
        public const string Unknown = "?";

        private static readonly Dictionary<byte, string> Descriptions = new Dictionary<byte, string>
        {
            [Items.Wood] = "Основа построек, рукоятей и луков.",
            [Items.Stone] = "Стены, полы, инструменты, наконечники стрел.",
            [Items.Resin] = "Горит: факелы и пропитка.",
            [Items.Fiber] = "Верёвки, тетивы, постель.",
            [Items.CopperOre] = "Переплавляется в медные слитки у костра.",
            [Items.CopperIngot] = "Медные инструменты и оружие.",
            [Items.Rope] = "Тетивы и вязка.",
            [Items.Meat] = "Жарится у костра.",
            [Items.RoastMeat] = "Восстанавливает здоровье.",
            [Items.Arrows] = "Для лука.",
            [Items.Hide] = "Медная броня: шлем, доспех, поножи.",
        };

        private static readonly Dictionary<byte, ItemSource[]> Sources = new Dictionary<byte, ItemSource[]>
        {
            [Items.Wood] = new[]
            {
                new ItemSource(SourceKind.Biome, Forest, "деревья, топором (больше всего)"),
                new ItemSource(SourceKind.Biome, Meadow, "рощицы и одиночные деревья, топором"),
            },
            [Items.Stone] = new[]
            {
                new ItemSource(SourceKind.Biome, Meadow, "валуны, киркой"),
                new ItemSource(SourceKind.Biome, Mountain, "скалы, киркой (больше всего)"),
                new ItemSource(SourceKind.Biome, Cave, "гряды и сталагмиты, киркой"),
            },
            [Items.Fiber] = new[]
            {
                new ItemSource(SourceKind.Biome, Meadow, "кусты — руками или топором"),
                new ItemSource(SourceKind.Biome, Forest, "кусты — руками или топором"),
            },
            [Items.CopperOre] = new[]
            {
                new ItemSource(SourceKind.Biome, Mountain, "медные жилы, киркой"),
                new ItemSource(SourceKind.Biome, Cave, "медные жилы, киркой"),
            },
            [Items.Resin] = new[]
            {
                new ItemSource(SourceKind.Biome, Forest, "деревья, топором"),
            },
            [Items.Meat] = new[]
            {
                new ItemSource(SourceKind.Enemy, Wolf, "волки"),
                new ItemSource(SourceKind.Enemy, Boar, "кабаны"),
                new ItemSource(SourceKind.Enemy, Deer, "олени"),
            },
            [Items.Hide] = new[] // E3-6: шкура — дроп волка [док] (код дропа пришёл в E3-3)
            {
                new ItemSource(SourceKind.Enemy, Wolf, "волки"),
            },
        };

        /// <summary>Базовый — без рецепта (добывается в мире).</summary>
        public static bool IsBase(byte item) => item != Items.None && CraftRules.RecipeFor(item) == 0;

        public static string Description(byte item) => Descriptions.TryGetValue(item, out string? d) ? d : string.Empty;

        public static IReadOnlyList<ItemSource> Of(byte item) =>
            Sources.TryGetValue(item, out ItemSource[]? s) ? s : System.Array.Empty<ItemSource>();

        public static string BiomeName(byte biome) => biome switch
        {
            Meadow => "Луга",
            Forest => "Лес",
            Mountain => "Горы",
            Cave => "Пещеры",
            _ => "?",
        };

        public static string EnemyName(byte enemy) => enemy switch
        {
            Bandit => "Разбойники",
            Wolf => "Волк",
            Boar => "Кабан",
            Deer => "Олень",
            _ => "?",
        };

        /// <summary>
        /// Строки «где взять» базового предмета: открытые источники — «Лес: деревья, топором»; неоткрытые — одной строкой
        /// «?» (сколько их и какие — не выдаём); источников нет — пусто.
        /// </summary>
        public static List<string> Where(byte item, Discovery known)
        {
            var lines = new List<string>();
            bool unknown = false;
            foreach (ItemSource s in Of(item))
            {
                if (known.Knows(s))
                {
                    lines.Add((s.Kind == SourceKind.Biome ? BiomeName(s.Id) : EnemyName(s.Id)) + ": " + s.How);
                }
                else
                {
                    unknown = true;
                }
            }

            if (unknown)
            {
                lines.Add(Unknown);
            }

            return lines;
        }
    }
}
