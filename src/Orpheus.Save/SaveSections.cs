using System.Collections.Generic;

namespace Orpheus.Save
{
    /// <summary>
    /// Разделы контейнера (docs/07, 6.3) и номера полей их записей. Номер поля, однажды выпущенный,
    /// не меняет смысла и не переиспользуется (6.7: «новое поле с умолчанием миграции не требует»).
    /// В версии формата 1 пишутся МИР, ЧАНКИ и СУЩЕСТВА; остальные id закреплены за будущими разделами.
    /// </summary>
    public static class SaveSections
    {
        public const uint World = 1;
        public const uint Chunks = 2;
        public const uint Macromap = 3;
        public const uint Exploration = 4;
        public const uint Areas = 5;
        public const uint Creatures = 6;
        public const uint Dungeons = 7;
        public const uint Contents = 8;
        public const uint Characters = 9;
        public const uint Marks = 10;
        public const uint BuildLog = 11;

        /// <summary>
        /// Отладочное наполнение (ключ сервера <c>--debug-save-pad</c>, приёмка 0.4 — docs/08 3.0): раздел из
        /// байтов, который грузчик пропускает как незнакомый. Нужен, чтобы файл поколения был длиннее куска
        /// атомарной записи (хранилище «ПК» пишет кусками по 512 байта): иначе обрыв процесса физически не
        /// попадает в середину файла и приёмка 0.4 ничего не проверяет. В обычной игре не пишется.
        /// </summary>
        public const uint DebugPad = 90;

        /// <summary>Имя раздела для лога и <c>orpheus-save dump</c>.</summary>
        public static string Name(uint id) => id switch
        {
            World => "МИР",
            Chunks => "ЧАНКИ",
            Macromap => "МАКРОКАРТА",
            Exploration => "РАЗВЕДКА",
            Areas => "ОБЛАСТИ",
            Creatures => "СУЩЕСТВА",
            Dungeons => "ДАНЖИ",
            Contents => "СОДЕРЖИМОЕ",
            Characters => "ПЕРСОНАЖИ",
            Marks => "МЕТКИ",
            BuildLog => "ЖУРНАЛ ПОСТРОЕК",
            DebugPad => "ОТЛАДКА",
            _ => "раздел " + id,
        };

        /// <summary>Поля раздела МИР.</summary>
        public static class WorldField
        {
            public const int Name = 1;
            public const int Seed = 2;
            public const int Width = 3;
            public const int Height = 4;
            public const int Wraps = 5;
            public const int GeneratorVersion = 6;
            public const int BaseSha256 = 7;
            public const int NextEntityId = 8;
        }

        /// <summary>Раздел ЧАНКИ: повторяющееся поле <see cref="Chunk"/> — вложенная запись чанка.</summary>
        public static class ChunksField
        {
            public const int Chunk = 1;
        }

        /// <summary>Поля записи чанка.</summary>
        public static class ChunkField
        {
            /// <summary>Карта: 0 — поверхность (отдельные карты данжей — потом).</summary>
            public const int Map = 1;
            public const int X = 2;
            public const int Y = 3;
            public const int Revision = 4;

            /// <summary>Сжатый блок чанка (Deflate-6, docs/07 3.3).</summary>
            public const int Data = 5;
        }

        /// <summary>Раздел СУЩЕСТВА: повторяющееся поле <see cref="Entity"/>.</summary>
        public static class CreaturesField
        {
            public const int Entity = 1;
        }

        /// <summary>Поля записи сущности (минимально: то, что есть в такте 0-006).</summary>
        public static class EntityField
        {
            public const int Id = 1;
            public const int Kind = 2;
            public const int X = 3;
            public const int Y = 4;
            public const int CarryX = 5;
            public const int CarryY = 6;
            public const int SpeedX10 = 7;
            public const int Direction = 8;
            public const int Buttons = 9;
        }

        /// <summary>Схема записей раздела для <c>orpheus-save dump</c>: имена полей и вложенные схемы. null — схемы нет.</summary>
        public static RecordSchema? Schema(uint id) => id switch
        {
            World => WorldSchema,
            Chunks => ChunksSchema,
            Creatures => CreaturesSchema,
            _ => null,
        };

        private static readonly RecordSchema WorldSchema = new RecordSchema()
            .Field(WorldField.Name, "имя")
            .Field(WorldField.Seed, "зерно")
            .Field(WorldField.Width, "ширина")
            .Field(WorldField.Height, "высота")
            .Field(WorldField.Wraps, "тор")
            .Field(WorldField.GeneratorVersion, "версия генератора")
            .Field(WorldField.BaseSha256, "SHA-256 снимка генерации")
            .Field(WorldField.NextEntityId, "следующий номер сущности");

        private static readonly RecordSchema ChunksSchema = new RecordSchema()
            .Field(ChunksField.Chunk, "чанк", new RecordSchema()
                .Field(ChunkField.Map, "карта")
                .Field(ChunkField.X, "x")
                .Field(ChunkField.Y, "y")
                .Field(ChunkField.Revision, "ревизия")
                .Field(ChunkField.Data, "сжатый блок"));

        private static readonly RecordSchema CreaturesSchema = new RecordSchema()
            .Field(CreaturesField.Entity, "сущность", new RecordSchema()
                .Field(EntityField.Id, "номер")
                .Field(EntityField.Kind, "вид")
                .Field(EntityField.X, "x, 1/256 тайла")
                .Field(EntityField.Y, "y, 1/256 тайла")
                .Field(EntityField.CarryX, "остаток x")
                .Field(EntityField.CarryY, "остаток y")
                .Field(EntityField.SpeedX10, "скорость ×10")
                .Field(EntityField.Direction, "ввод: направление")
                .Field(EntityField.Buttons, "ввод: кнопки"));
    }

    /// <summary>Имена полей записи и схемы вложенных записей (только для текстового вывода).</summary>
    public sealed class RecordSchema
    {
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly Dictionary<int, RecordSchema> _nested = new Dictionary<int, RecordSchema>();

        public RecordSchema Field(int field, string name, RecordSchema? nested = null)
        {
            _names[field] = name;
            if (nested != null)
            {
                _nested[field] = nested;
            }

            return this;
        }

        /// <summary>Имя поля; незнакомое — null.</summary>
        public string? NameOf(int field) => _names.TryGetValue(field, out string? n) ? n : null;

        /// <summary>Схема вложенной записи поля; нет — null.</summary>
        public RecordSchema? NestedOf(int field) => _nested.TryGetValue(field, out RecordSchema? s) ? s : null;
    }
}
