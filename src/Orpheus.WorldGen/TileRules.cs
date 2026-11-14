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

    /// <summary>Строка групп: объект ставится кучками — центров на 100 000 тайлов биома, в кучке от Min до Max объектов в квадрате 5 × 5.</summary>
    public readonly struct GroupRow
    {
        public GroupRow(ushort obj, int centersPer100k, int min, int max)
        {
            Object = obj;
            CentersPer100k = centersPer100k;
            Min = min;
            Max = max;
        }

        public ushort Object { get; }

        public int CentersPer100k { get; }

        public int Min { get; }

        public int Max { get; }
    }

    /// <summary>
    /// Правила тайлов биома для этапа 11 (`01` 3, карточки биомов акта I; `01` 5 — какие объекты чем копаются; E1-2):
    /// природный пол, скальная масса (доля площади, ядро — гранит; мелкая — кучки валунов, крупная — гряды пещер),
    /// рассыпанные объекты, залежи и кучки.
    /// </summary>
    public sealed class BiomeTileRule
    {
        public BiomeTileRule(byte biome, byte floor, int rockSharePercent, int graniteOfRockPercent, ScatterRow[] scatter, ScatterRow[] deposits,
            GroupRow[]? groups = null, bool fineMass = false)
        {
            Biome = biome;
            Floor = floor;
            RockSharePercent = rockSharePercent;
            GraniteOfRockPercent = graniteOfRockPercent;
            Scatter = Array.AsReadOnly((ScatterRow[])scatter.Clone());
            Deposits = Array.AsReadOnly((ScatterRow[])deposits.Clone());
            Groups = Array.AsReadOnly((GroupRow[])(groups ?? Array.Empty<GroupRow>()).Clone());
            FineMass = fineMass;
        }

        public byte Biome { get; }

        /// <summary>Природный пол (<see cref="FloorId"/>): его же возвращает снос постройки (`01` 5 «Пол»).</summary>
        public byte Floor { get; }

        /// <summary>Скальная масса — доля площади биома, % (горы 8 — кучки валунов, пещеры 20 — гряды; у лугов и леса — 0).</summary>
        public int RockSharePercent { get; }

        /// <summary>Гранит — доля скальной массы, %: самые высокие значения шума массы; остальное — скала яруса 1.</summary>
        public int GraniteOfRockPercent { get; }

        /// <summary>Шум массы мелкий (ячейки 16, 8): кучки валунов по 3–8 тайлов вместо гряд.</summary>
        public bool FineMass { get; }

        /// <summary>Рассыпанные объекты: ставятся только вне скальной массы, по порядку строк.</summary>
        public IReadOnlyList<ScatterRow> Scatter { get; }

        /// <summary>Залежи: ставятся на любой тайл биома, в том числе в скальную массу (жила в склоне). Идут раньше рассыпанных.</summary>
        public IReadOnlyList<ScatterRow> Deposits { get; }

        /// <summary>Кучки (деревья лугов по 2–6, кусты, валуны): ставятся последними на свободные тайлы.</summary>
        public IReadOnlyList<GroupRow> Groups { get; }
    }

    /// <summary>
    /// Таблица этапа 11 для набора «срез» (E1-2, план 1.2–1.3; раньше — 1-017). Объекты — дерево, куст, скала, гранит,
    /// залежь меди. Лес ставится не строкой плотности, а полем плотности (<see cref="FloraStage"/>). Сплошной скальной
    /// массы гор больше нет (слово Автора 13.11: горы — обрывы-границы, а не «непонятные скалы»): в горах — каменистая
    /// земля, кучки валунов и руда; гряды остались только в пещерах (20 %).
    /// </summary>
    public static class TileRules
    {
        /// <summary>Луга: трава; деревья кучками по 2–6 (≈ 1,5 % тайлов), кусты по 2–4, валуны по 1–3.</summary>
        public static readonly BiomeTileRule Meadow = new BiomeTileRule(Biome.Meadow, FloorId.Grass, 0, 0,
            Array.Empty<ScatterRow>(), Array.Empty<ScatterRow>(),
            new[] { new GroupRow(ObjectId.Tree, 400, 2, 6), new GroupRow(ObjectId.Bush, 300, 2, 4), new GroupRow(ObjectId.Rock, 250, 1, 3) });

        /// <summary>Лес: подстилка; деревья — полем плотности; кусты по 2–4, валуны по 1–3.</summary>
        public static readonly BiomeTileRule Forest = new BiomeTileRule(Biome.Forest, FloorId.ForestFloor, 0, 0,
            Array.Empty<ScatterRow>(), Array.Empty<ScatterRow>(),
            new[] { new GroupRow(ObjectId.Bush, 400, 2, 4), new GroupRow(ObjectId.Rock, 150, 1, 3) });

        /// <summary>Горы: камень; валуны кучками 8 % площади (гранит — 30 % из них) и по 1–3; медь 12 на 1000; редкие деревья.</summary>
        public static readonly BiomeTileRule Mountain = new BiomeTileRule(Biome.Mountain, FloorId.Stone, 8, 30,
            Array.Empty<ScatterRow>(),
            new[] { ScatterRow.PerThousand(ObjectId.Ore, 12) },
            new[] { new GroupRow(ObjectId.Rock, 400, 1, 3), new GroupRow(ObjectId.Tree, 120, 2, 4) },
            fineMass: true);

        /// <summary>Пещеры: камень; гряды 20 % (гранит 40 % из них); сталагмит 30, медь 10 на 1000.</summary>
        public static readonly BiomeTileRule Cave = new BiomeTileRule(Biome.Cave, FloorId.Stone, 20, 40,
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
