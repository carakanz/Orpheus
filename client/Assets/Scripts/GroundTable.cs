#nullable enable
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>Как рисуется пол тайла (E1-4, план 1.4–1.5).</summary>
    public enum GroundKind : byte
    {
        /// <summary>Земля: слой меша земли.</summary>
        Ground,

        /// <summary>Вода: слой «отмель» у берега и «глубина» дальше — выбирает сборщик меша по расстоянию до суши.</summary>
        Water,

        /// <summary>Построенный пол: плитка Tilemap поверх меша; под ней меш рисует слой строки.</summary>
        Built,
    }

    /// <summary>Слой меша земли: картинка 4 × 4 тайла. Номер слоя = приоритет на границе (больший ложится поверх).</summary>
    public readonly struct GroundLayer
    {
        public readonly string Name;      // имя файла без «TEMP_» и расширения
        public readonly Color32 Fallback; // цвет, если картинки нет (новый набор арта без неё)

        public GroundLayer(string name, Color32 fallback)
        {
            Name = name;
            Fallback = fallback;
        }
    }

    /// <summary>Строка таблицы «номер пола → вид».</summary>
    public readonly struct GroundRow
    {
        public readonly byte Layer;
        public readonly GroundKind Kind;

        public GroundRow(byte layer, GroundKind kind)
        {
            Layer = layer;
            Kind = kind;
        }
    }

    /// <summary>
    /// Таблица «FloorId → вид земли» (work/modules/worldview.md: вид тайла задаётся таблицей, не <c>if</c> по коду).
    /// Чтобы добавить вид: картинку <c>TEMP_ground_имя.png</c> в <c>Assets/Art/_TEMP</c>, строку в <see cref="Layers"/>
    /// (место в списке = приоритет) и строку в <see cref="Floors"/>.
    /// </summary>
    public static class GroundTable
    {
        // Слои по возрастанию приоритета: вода < песок < земля < трава/лес < камень (на границе больший лежит поверх).
        public const byte WaterDeep = 0;
        public const byte WaterShallow = 1;
        public const byte Sand = 2;
        public const byte Dirt = 3;
        public const byte DryGrass = 4;
        public const byte Grass = 5;
        public const byte Forest = 6;
        public const byte Rocky = 7;

        /// <summary>Слоёв воды (первые в списке): у них движение и светлая линия берега.</summary>
        public const int WaterLayers = 2;

        public static readonly GroundLayer[] Layers =
        {
            new GroundLayer("water_deep", new Color32(16, 92, 110, 255)),
            new GroundLayer("water_shallow", new Color32(72, 176, 190, 255)),
            new GroundLayer("ground_sand", new Color32(232, 208, 144, 255)),
            new GroundLayer("ground_dirt", new Color32(150, 96, 64, 255)),
            new GroundLayer("ground_drygrass", new Color32(200, 172, 80, 255)),
            new GroundLayer("ground_grass", new Color32(112, 170, 44, 255)),
            new GroundLayer("ground_forest", new Color32(84, 92, 44, 255)),
            new GroundLayer("ground_rocky", new Color32(176, 168, 150, 255)),
        };

        /// <summary>
        /// Номер пола «сухая трава». В <c>FloorId</c> ядра его пока нет (ждём шаг 1.2); 12 — первый свободный номер.
        /// Если ядро даст другой номер — поправить эту строку и строку в <see cref="Floors"/>.
        /// </summary>
        public const byte DryGrassFloor = 12;

        /// <summary>Размер таблицы — с запасом на новые полы ядра.</summary>
        public const int TableSize = 32;

        // Таблица данными: (номер пола, слой меша, как рисовать). Не названные номера — луг.
        static readonly (byte Floor, byte Layer, GroundKind Kind)[] Floors =
        {
            (FloorId.Empty, Dirt, GroundKind.Ground),
            (FloorId.Grass, Grass, GroundKind.Ground),
            (FloorId.Dirt, Dirt, GroundKind.Ground),
            (FloorId.Stone, Rocky, GroundKind.Ground),
            (FloorId.Sand, Sand, GroundKind.Ground),
            (FloorId.Snow, Rocky, GroundKind.Ground),           // своей картинки снега нет
            (FloorId.Water, WaterShallow, GroundKind.Water),
            (FloorId.Lava, Dirt, GroundKind.Ground),            // своей картинки лавы нет
            (FloorId.WoodFloor, Dirt, GroundKind.Built),        // постройки — плиткой поверх, под ними земля
            (FloorId.StoneFloor, Dirt, GroundKind.Built),
            (FloorId.Platform, WaterShallow, GroundKind.Built), // настил стоит над водой
            (FloorId.ForestFloor, Forest, GroundKind.Ground),
            (DryGrassFloor, DryGrass, GroundKind.Ground),       // FloorId.DryGrass — когда появится в ядре (шаг 1.2)
        };

        static readonly GroundRow[] Rows = BuildRows();

        static GroundRow[] BuildRows()
        {
            var rows = new GroundRow[TableSize];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new GroundRow(Grass, GroundKind.Ground);
            }

            foreach ((byte floor, byte layer, GroundKind kind) in Floors)
            {
                rows[floor] = new GroundRow(layer, kind);
            }

            return rows;
        }

        /// <summary>Вид пола; номер за пределами таблицы — луг.</summary>
        public static GroundRow Of(byte floor) => floor < TableSize ? Rows[floor] : Rows[FloorId.Grass];
    }
}
