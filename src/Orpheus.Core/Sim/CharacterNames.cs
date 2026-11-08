using System;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Имя как ключ персонажа (0-035, допущение `[док]`: учётных записей нет — игрок, кроме хозяина, узнаётся по имени в мире).
    /// Одно имя — без учёта регистра и пробелов по краям: «Эвридика» и « эвридика» — один персонаж (docs/07 7.3: «два
    /// персонажа одного мира не носят одно имя»).
    /// </summary>
    public static class CharacterNames
    {
        /// <summary>Одно ли это имя для мира.</summary>
        public static bool Same(string? a, string? b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
