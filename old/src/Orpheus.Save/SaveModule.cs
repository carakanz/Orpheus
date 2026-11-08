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
        /// Версия формата сохранения (6.7): 1 — первая; 2 — у сущности поля уворота (0-029, <c>EntityField.Dodge*</c>).
        /// Миграций ещё нет: формат 1 читается тем же кодом, у сущностей без полей уворота он нулевой (6.7: «новое поле
        /// с умолчанием миграции не требует»).
        /// </summary>
        public const uint FormatVersion = 2;

        /// <summary>Версия сборки в заголовке сохранения: растёт с выпусками.</summary>
        public const uint BuildVersion = 1;
    }
}
