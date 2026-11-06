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
        /// <summary>Версия формата сохранения (6.7): 1 — первая, миграций ещё нет.</summary>
        public const uint FormatVersion = 1;

        /// <summary>Версия сборки в заголовке сохранения: растёт с выпусками.</summary>
        public const uint BuildVersion = 1;
    }
}
