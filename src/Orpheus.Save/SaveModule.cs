namespace Orpheus.Save
{
    /// <summary>
    /// Модуль сохранений (docs/07, раздел 6): двоичный контейнер (<see cref="SaveContainer"/>), записи
    /// «номер поля, тип, длина, значение» (<see cref="RecordWriter"/>), мир в сохранение и обратно
    /// (<see cref="WorldSave"/>), папка мира с поколениями и проверкой <c>.tmp</c> (<see cref="WorldFolder"/>)
    /// поверх <see cref="IPlatformStorage"/>. P/Invoke здесь нет: атомарная запись на ПК — <c>Orpheus.Platform.Pc</c>.
    /// </summary>
    public static class SaveModule
    {
        /// <summary>
        /// Версия формата сохранения (6.7): 1 — первая; 2 — у сущности поля уворота (0-029, <c>EntityField.Dodge*</c>);
        /// 3 — раздел ПЕРСОНАЖИ (0-035, <c>CharacterField</c>: ключ персонажа и где он стоял);
        /// 4 — сумка персонажа (<c>CharacterField.Inv</c>, 1-002); 5 — поля удара мечом у сущности и персонажа
        /// (<c>EntityField.Melee*</c>, <c>CharacterField.Melee*</c>, 1-004): у форматов 1–4 удар нулевой (покой);
        /// 6 — мозг ИИ у сущности (<c>EntityField.Brain*</c>, 1-007): у форматов 1–5 мозга нет (враг стоит заглушкой).
        /// 7 — сумка ячейками (payload 121 байт вместо 23) и кучи на земле (<c>CreaturesField.Pile</c>, <c>WorldField.NextPileId</c>;
        /// E2-1): у форматов 4–6 сумка-счётчики читается и раскладывается по ячейкам (<c>Inventory.ReadLegacyPayload</c>), куч нет.
        /// 8 — очередь изготовления у сущности (<c>EntityField.Craft</c>, E2-3): у форматов 1–7 поля нет — очередь пустая.
        /// 9 — сундуки с содержимым (<c>CreaturesField.Chest</c>) и точка возрождения у кровати (<c>EntityField.BedX/BedY</c>,
        /// <c>CharacterField.BedX/BedY</c>; E2-5): у форматов 1–8 сундуки пустые, кровати нет — возрождение у старта.
        /// Миграций ещё нет: формат 1 читается тем же кодом, у сущностей без полей уворота он нулевой; формат 1–2 — без
        /// персонажей; формат 3 — персонажи без сумки (стартовый набор) (6.7: «новое поле с умолчанием миграции не требует»).
        /// </summary>
        public const uint FormatVersion = 9;

        /// <summary>Версия сборки в заголовке сохранения: растёт с выпусками.</summary>
        public const uint BuildVersion = 1;
    }
}
