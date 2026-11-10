using System.Collections.Generic;

namespace Orpheus.Save
{
    /// <summary>
    /// Разделы контейнера (docs/07, 6.3) и номера полей их записей. Номер поля, однажды выпущенный,
    /// не меняет смысла и не переиспользуется (6.7: «новое поле с умолчанием миграции не требует»).
    /// В версиях формата 1–2 пишутся МИР, ЧАНКИ и СУЩЕСТВА; остальные id закреплены за будущими разделами.
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

            /// <summary>
            /// Поколение, которое это сохранение заменяет (0-037, серия поводов «вход/уход»); нет поля — 0. Пишется только
            /// у заменяющих: по нему старт узнаёт заменённое, не удалённое из-за обрыва, и убирает его, а не поколение
            /// автосохранения (<see cref="WorldFolder.Recover"/>).
            /// </summary>
            public const int Replaces = 9;
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

        /// <summary>Поля записи сущности (минимально: то, что есть в такте 0-006; уворот — с формата 2, 0-029).</summary>
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

            /// <summary>Уворот (<c>Mover.Dodge</c>, формат 2): кадров от начала рывка, 0…255. Нет поля (формат 1) — 0.</summary>
            public const int DodgeElapsed = 10;

            /// <summary>Уворот: кадров рывка осталось, 0…15.</summary>
            public const int DodgeDashLeft = 11;

            /// <summary>Уворот: кадров отката осталось, 0…24.</summary>
            public const int DodgeCooldown = 12;

            /// <summary>Уворот: направление рывка, код 0…8 (0 — уворота не было).</summary>
            public const int DodgeDirection = 13;

            /// <summary>Удар мечом (<c>Mover.Melee</c>, формат 5, 1-004): фаза 0…3. Нет поля (форматы 1–4) — 0, покой.</summary>
            public const int MeleePhase = 14;

            /// <summary>Удар: кадров до конца фазы, 0…31.</summary>
            public const int MeleeFramesLeft = 15;

            /// <summary>Удар: индекс в серии, 0…2.</summary>
            public const int MeleeIndex = 16;

            /// <summary>Удар: нажатие в буфере (0/1).</summary>
            public const int MeleeBuffered = 17;

            /// <summary>Удар: кадров покоя с конца отхода, 0…48+.</summary>
            public const int MeleeIdleFrames = 18;

            /// <summary>Удар решён (урон нанесён), 0/1.</summary>
            public const int MeleeResolved = 19;

            /// <summary>Удар: ось, шаги угла u16 (прицел начала замаха).</summary>
            public const int MeleeAimRaw = 20;

            /// <summary>Удар: начало удара, x в 1/256 тайла.</summary>
            public const int MeleeOriginX = 21;

            /// <summary>Удар: начало удара, y в 1/256 тайла.</summary>
            public const int MeleeOriginY = 22;
        }

        /// <summary>Раздел ПЕРСОНАЖИ (формат 3, 0-035): повторяющееся поле <see cref="Character"/> — мировая часть персонажа.</summary>
        public static class CharactersField
        {
            public const int Character = 1;
        }

        /// <summary>
        /// Поля записи персонажа (формат 3–5, 0-035, 1-002, 1-004; docs/07 6.2 «Персонажи — мировая часть», 7.4 «Вернулся»): ключ
        /// (хозяин или имя), номер сущности, если персонаж в мире на такте сохранения (его движение — в разделе СУЩЕСТВА),
        /// иначе — движение на миг ухода; сумка (формат 4); удар мечом (формат 5).
        /// </summary>
        public static class CharacterField
        {
            /// <summary>Персонаж хозяина мира (ключ — токен хозяина, а не имя).</summary>
            public const int Host = 1;

            /// <summary>Имя (UTF-8, 1–32 байта).</summary>
            public const int Name = 2;

            /// <summary>Номер сущности в разделе СУЩЕСТВА; нет поля или 0 — персонаж вне мира, движение — в полях ниже.</summary>
            public const int EntityId = 3;

            public const int X = 4;
            public const int Y = 5;
            public const int CarryX = 6;
            public const int CarryY = 7;
            public const int SpeedX10 = 8;
            public const int DodgeElapsed = 9;
            public const int DodgeDashLeft = 10;
            public const int DodgeCooldown = 11;
            public const int DodgeDirection = 12;

            /// <summary>Сумка, payload 23 байта (1-002, формат 4): выбранная ячейка, 8 ячеек, счётчики материалов.
            /// Нет поля (формат 3) — стартовый набор.</summary>
            public const int Inv = 13;

            /// <summary>Удар мечом (<c>Mover.Melee</c>, формат 5, 1-004): фаза 0…3. Нет поля — 0, покой.</summary>
            public const int MeleePhase = 14;

            /// <summary>Удар: кадров до конца фазы.</summary>
            public const int MeleeFramesLeft = 15;

            /// <summary>Удар: индекс в серии, 0…2.</summary>
            public const int MeleeIndex = 16;

            /// <summary>Удар: нажатие в буфере (0/1).</summary>
            public const int MeleeBuffered = 17;

            /// <summary>Удар: кадров покоя с конца отхода.</summary>
            public const int MeleeIdleFrames = 18;

            /// <summary>Удар решён (урон нанесён), 0/1.</summary>
            public const int MeleeResolved = 19;

            /// <summary>Удар: ось, шаги угла u16.</summary>
            public const int MeleeAimRaw = 20;

            /// <summary>Удар: начало удара, x в 1/256 тайла.</summary>
            public const int MeleeOriginX = 21;

            /// <summary>Удар: начало удара, y в 1/256 тайла.</summary>
            public const int MeleeOriginY = 22;
        }

        /// <summary>Схема записей раздела для <c>orpheus-save dump</c>: имена полей и вложенные схемы. null — схемы нет.</summary>
        public static RecordSchema? Schema(uint id) => id switch
        {
            World => WorldSchema,
            Chunks => ChunksSchema,
            Creatures => CreaturesSchema,
            Characters => CharactersSchema,
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
            .Field(WorldField.NextEntityId, "следующий номер сущности")
            .Field(WorldField.Replaces, "заменяет поколение");

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
                .Field(EntityField.Buttons, "ввод: кнопки")
                .Field(EntityField.DodgeElapsed, "уворот: кадров с начала")
                .Field(EntityField.DodgeDashLeft, "уворот: рывка осталось")
                .Field(EntityField.DodgeCooldown, "уворот: отката осталось")
                .Field(EntityField.DodgeDirection, "уворот: направление")
                .Field(EntityField.MeleePhase, "удар: фаза")
                .Field(EntityField.MeleeFramesLeft, "удар: кадров до конца фазы")
                .Field(EntityField.MeleeIndex, "удар: индекс в серии")
                .Field(EntityField.MeleeBuffered, "удар: нажатие в буфере")
                .Field(EntityField.MeleeIdleFrames, "удар: кадров покоя")
                .Field(EntityField.MeleeResolved, "удар решён")
                .Field(EntityField.MeleeAimRaw, "удар: ось")
                .Field(EntityField.MeleeOriginX, "удар: x начала")
                .Field(EntityField.MeleeOriginY, "удар: y начала"));

        private static readonly RecordSchema CharactersSchema = new RecordSchema()
            .Field(CharactersField.Character, "персонаж", new RecordSchema()
                .Field(CharacterField.Host, "хозяин")
                .Field(CharacterField.Name, "имя")
                .Field(CharacterField.EntityId, "сущность (0 — вне мира)")
                .Field(CharacterField.X, "x, 1/256 тайла")
                .Field(CharacterField.Y, "y, 1/256 тайла")
                .Field(CharacterField.CarryX, "остаток x")
                .Field(CharacterField.CarryY, "остаток y")
                .Field(CharacterField.SpeedX10, "скорость ×10")
                .Field(CharacterField.DodgeElapsed, "уворот: кадров с начала")
                .Field(CharacterField.DodgeDashLeft, "уворот: рывка осталось")
                .Field(CharacterField.DodgeCooldown, "уворот: отката осталось")
                .Field(CharacterField.DodgeDirection, "уворот: направление")
                .Field(CharacterField.Inv, "сумка")
                .Field(CharacterField.MeleePhase, "удар: фаза")
                .Field(CharacterField.MeleeFramesLeft, "удар: кадров до конца фазы")
                .Field(CharacterField.MeleeIndex, "удар: индекс в серии")
                .Field(CharacterField.MeleeBuffered, "удар: нажатие в буфере")
                .Field(CharacterField.MeleeIdleFrames, "удар: кадров покоя")
                .Field(CharacterField.MeleeResolved, "удар решён")
                .Field(CharacterField.MeleeAimRaw, "удар: ось")
                .Field(CharacterField.MeleeOriginX, "удар: x начала")
                .Field(CharacterField.MeleeOriginY, "удар: y начала"));
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
