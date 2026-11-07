using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Orpheus.Save
{
    /// <summary>Содержимое файла-замка хоста: куда подключаться вернувшейся игре хоста (docs/07, 7.6).</summary>
    public sealed class HostLock
    {
        /// <summary>UDP-порт сервера на петле 127.0.0.1.</summary>
        public int Port { get; set; }

        /// <summary>Токен хозяина — 32 шестнадцатеричные цифры.</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>Номер процесса сервера.</summary>
        public int ProcessId { get; set; }

        /// <summary>Режим: <c>single</c> или <c>host</c>.</summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>Порт «Открыть для друзей»; не открыт — −1.</summary>
        public int OpenPort { get; set; } = -1;
    }

    /// <summary>
    /// Файл-замок хоста <c>host.lock</c> в папке мира (0-024, docs/07 7.6): сервер одиночной игры и хоста пишет порт и токен
    /// хозяина, когда начал слушать, и удаляет при штатном выходе; игра хоста после своего падения находит работающий
    /// сервер по нему. Запись — атомарная, через <see cref="IPlatformStorage"/> (6.5): обрыв не оставит половину файла.
    /// <para>
    /// Живость — не по файлу (после пропажи света он остаётся), а по замку мира <c>world.lock</c>: его держит работающий
    /// сервер (<see cref="WorldFolder.Lock"/>), и он отпускается вместе с процессом.
    /// </para>
    /// </summary>
    public static class HostLockFile
    {
        public const string FileName = "host.lock";

        private const string Header = "orpheus-host 1";

        public static string PathIn(string worldDir) => Path.Combine(worldDir, FileName);

        public static void Write(IPlatformStorage storage, string worldDir, HostLock l)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("port=").Append(l.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("token=").Append(l.Token).Append('\n');
            sb.Append("pid=").Append(l.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mode=").Append(l.Mode).Append('\n');
            sb.Append("open=").Append(l.OpenPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
            storage.WriteAtomic(PathIn(worldDir), Encoding.UTF8.GetBytes(sb.ToString()));
        }

        /// <summary>
        /// Прочитать; нет файла, он не того вида (битый после пропажи света: нули, обрывок, мусор) или занят — null,
        /// без исключения: поиск сервера такой замок пропускает.
        /// </summary>
        public static HostLock? TryRead(IPlatformStorage storage, string worldDir)
        {
            string path = PathIn(worldDir);
            if (!storage.FileExists(path))
            {
                return null;
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(storage.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null; // занят (сервер как раз переносит новый замок на место, антивирус) — спросят ещё раз
            }

            string[] lines = text.Split('\n');
            if (lines.Length < 1 || lines[0].TrimEnd('\r') != Header)
            {
                return null;
            }

            var l = new HostLock();
            bool port = false;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                switch (key)
                {
                    case "port":
                        port = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0 && p <= 65535;
                        l.Port = p;
                        break;
                    case "token":
                        l.Token = value;
                        break;
                    case "pid":
                        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid);
                        l.ProcessId = pid;
                        break;
                    case "mode":
                        l.Mode = value;
                        break;
                    case "open":
                        l.OpenPort = int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int o) ? o : -1;
                        break;
                }
            }

            return port && l.Token.Length > 0 ? l : null;
        }

        /// <summary>Удалить (штатный выход сервера); нет — ничего.</summary>
        public static void Delete(IPlatformStorage storage, string worldDir)
        {
            string path = PathIn(worldDir);
            if (storage.FileExists(path))
            {
                storage.Delete(path);
            }
        }

        /// <summary>
        /// Держит ли кто-то мир (замок <c>world.lock</c> занят). Проверка берёт замок на миг и сразу отпускает: сервер,
        /// стартующий ровно в этот миг, увидит «мир уже открыт» — вызывать, когда сервер не запускается этой же игрой.
        /// </summary>
        public static bool IsWorldHeld(IPlatformStorage storage, string worldDir)
        {
            if (!storage.DirectoryExists(worldDir))
            {
                return false;
            }

            IDisposable? probe = storage.TryLock(Path.Combine(worldDir, WorldFolder.LockFileName));
            if (probe == null)
            {
                return true;
            }

            probe.Dispose();
            return false;
        }
    }
}
