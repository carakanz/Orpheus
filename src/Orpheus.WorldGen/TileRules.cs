using System;
using System.Collections.Generic;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Строка плотности: объект и число на 1000 тайлов биома (`01` 3 «Пол и преграды»: «плотность — число объектов
    /// на 1000 тайлов пола биома»). Хранится в сотых долях промилле — на 100 000 тайлов, чтобы влезали дробные плотности
    /// вроде «серебро 1,5».
    /// </summary>
    public readonly struct ScatterRow
    {
        public ScatterRow(ushort obj, int per100k)
        {
            Object = obj;
            Per100k = per100k;
        }

        /// <summary>Id объекта (<see cref="ObjectId"/>).</summary>
        public ushort Object { get; }

        /// <summary>Объектов на 100 000 тайлов биома (8 на 1000 = 800).</summary>
        public int Per100k { get; }

        /// <summary>Строка «n на 1000 тайлов».</summary>
        public static ScatterRow PerThousand(ushort obj, int perThousand) => new ScatterRow(obj, perThousand * 100);
    }

    /// <summary>
    /// Правила тайлов биома для этапа 11 (`01` 3, карточки биомов акта I; `01` 5 — какие объекты чем копаются):
    /// природный пол, скальная масса (доля площади, ядро — гранит), рассыпанные объекты и залежи.
    /// </summary>
    public sealed class BiomeTileRule
    {
        public BiomeTileRule(byte biome, byte floor, int rockSharePercent, int graniteOfRockPercent, ScatterRow[] scatter, ScatterRow[] deposits)
        {
            Biome = biome;
            Floor = floor;
            RockSharePercent = rockSharePercent;
            GraniteOfRockPercent = graniteOfRockPercent;
            Scatter = Array.AsReadOnly((ScatterRow[])scatter.Clone());
            Deposits = Array.AsReadOnly((ScatterRow[])deposits.Clone());
        }

        public byte Biome { get; }

        /// <summary>Природный пол (<see cref="FloorId"/>): его же возвращает снос постройки (`01` 5 «Пол»).</summary>
        public byte Floor { get; }

        /// <summary>Скальная масса — доля площади биома, % (горы 45, пещеры 35; у лугов и леса — 0).</summary>
        public int RockSharePercent { get; }

        /// <summary>Гранит — доля скальной массы, %: самые высокие значения шума массы (ядро хребта); остальное — скала яруса 1.</summary>
        public int GraniteOfRockPercent { get; }

        /// <summary>Рассыпанные объекты: ставятся только вне скальной массы, по порядку строк.</summary>
        public IReadOnlyList<ScatterRow> Scatter { get; }

        /// <summary>Залежи: ставятся на любой тайл биома, в том числе в скальную массу (жила в склоне). Идут раньше рассыпанных.</summary>
        public IReadOnlyList<ScatterRow> Deposits { get; }
    }

    /// <summary>
    /// Таблица этапа 11 для набора «срез» (`01` 3; веха 1, карточка 1-017). Объекты вехи 1 — дерево, куст, скала, гранит,
    /// залежь меди. Чего нет в вехе 1 (и почему) — в «Решениях» карточки 1-017:
    /// <list type="bullet">
    /// <item>валун 2 × 2 — одиночной скалой (многотайловое природное копание не умеет `EditRules`), плотность валунов — к скалам;</item>
    /// <item>базальт (кирка 3) — гранитом: железной кирки в вехе 1 нет;</item>
    /// <item>улей, трава-волокно, лунные цветы, поваленный ствол, грибы, кристаллы, железо, уголь, серебро — не в вехе 1.</item>
    /// </list>
    /// </summary>
    public static class TileRules
    {
        /// <summary>Луга: трава; дерево 8, скала 10 + валун 6, куст ягод 12 на 1000.</summary>
        public static readonly BiomeTileRule Meadow = new BiomeTileRule(Biome.Meadow, FloorId.Grass, 0, 0,
            new[] { ScatterRow.PerThousand(ObjectId.Tree, 8), ScatterRow.PerThousand(ObjectId.Rock, 16), ScatterRow.PerThousand(ObjectId.Bush, 12) },
            Array.Empty<ScatterRow>());

        /// <summary>Лес: подстилка; дерево 120, валун 8, куст 15 на 1000.</summary>
        public static readonly BiomeTileRule Forest = new BiomeTileRule(Biome.Forest, FloorId.ForestFloor, 0, 0,
            new[] { ScatterRow.PerThousand(ObjectId.Tree, 120), ScatterRow.PerThousand(ObjectId.Rock, 8), ScatterRow.PerThousand(ObjectId.Bush, 15) },
            Array.Empty<ScatterRow>());

        /// <summary>Горы: камень; скалы 45 % площади (скала 70 %, гранит 25 % + базальт 5 % → гранит 30 %); медь 12 на 1000.</summary>
        public static readonly BiomeTileRule Mountain = new BiomeTileRule(Biome.Mountain, FloorId.Stone, 45, 30,
            Array.Empty<ScatterRow>(),
            new[] { ScatterRow.PerThousand(ObjectId.Ore, 12) });

        /// <summary>Пещеры: камень; скалы 35 % (ярус 1 — 60 %, гранит 30 % + базальт 10 % → гранит 40 %); сталагмит 30, медь 10 на 1000.</summary>
        public static readonly BiomeTileRule Cave = new BiomeTileRule(Biome.Cave, FloorId.Stone, 35, 40,
            new[] { ScatterRow.PerThousand(ObjectId.Rock, 30) },
            new[] { ScatterRow.PerThousand(ObjectId.Ore, 10) });

        private static readonly BiomeTileRule?[] ByBiome = Build();

        /// <summary>Правило биома; биома нет в таблице (не размечен, не из среза) — правило лугов.</summary>
        public static BiomeTileRule For(byte biome) => biome < ByBiome.Length && ByBiome[biome] != null ? ByBiome[biome]! : Meadow;

        private static BiomeTileRule?[] Build()
        {
            var t = new BiomeTileRule?[Biome.Count];
            foreach (BiomeTileRule r in new[] { Meadow, Forest, Mountain, Cave })
            {
                t[r.Biome] = r;
            }

            return t;
        }
    }
}
