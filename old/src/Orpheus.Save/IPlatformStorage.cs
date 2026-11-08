using System;

namespace Orpheus.Save
{
    /// <summary>
    /// Хранилище платформы (docs/07, 1.6): всё, что сохранения делают с файлами. Ядро не зовёт ОС напрямую —
    /// на ПК реализация <c>Orpheus.Platform.Pc.PcStorage</c> (атомарная запись по 6.5, P/Invoke только там),
    /// на PS5 — API сохранений платформы (потом). Формат файла один; поколения и контрольная сумма — внутри формата.
    /// <para>Пути — строки ОС; имена файлов — без папки.</para>
    /// </summary>
    public interface IPlatformStorage
    {
        bool FileExists(string path);

        bool DirectoryExists(string path);

        void CreateDirectory(string path);

        /// <summary>Имена файлов папки (без папки), по возрастанию (ordinal). Нет папки — пусто.</summary>
        string[] ListFiles(string directory);

        byte[] ReadAllBytes(string path);

        /// <summary>
        /// Атомарно записать файл целиком (6.5): сначала <c>&lt;path&gt;.tmp</c>, сброс на носитель, затем
        /// переименование в <paramref name="path"/> с гарантией, что и переименование на носителе. После
        /// возврата файл целиком на носителе; при обрыве в любой миг — либо прежнее состояние, либо целый
        /// <c>.tmp</c>, либо целый новый файл. Существующий <paramref name="path"/> заменяется.
        /// </summary>
        void WriteAtomic(string path, ReadOnlySpan<byte> data);

        /// <summary>Переименовать с гарантией на носителе (продвижение целого <c>.tmp</c>, перенос битого в <c>backups/damaged</c>).</summary>
        void MoveDurable(string from, string to, bool replaceExisting);

        void Delete(string path);

        /// <summary>
        /// Замок мира (6.3, <c>world.lock</c>): держать файл открытым без совместного доступа, пока жив
        /// возвращённый объект. Занят другим процессом — null.
        /// </summary>
        IDisposable? TryLock(string path);
    }
}
