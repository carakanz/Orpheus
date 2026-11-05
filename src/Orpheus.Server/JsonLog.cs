using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Orpheus.Server
{
    /// <summary>Уровень записи лога (docs/07, 11.6).</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    /// <summary>
    /// Лог JSON-строками (docs/07, 11.6): одна запись — одна строка
    /// <c>{"time":…,"tick":…,"level":…,"cat":…,"msg":…,"data":{…}}</c>.
    /// Файл на сутки — <c>&lt;папка&gt;/server-ГГГГ-ММ-ДД.jsonl</c> (дата UTC), копия строки — в консоль.
    /// Уровень по умолчанию — <c>info</c>, по категориям — <c>--log net=debug</c>.
    /// Категории: <c>net</c>, <c>world</c>, <c>save</c>, <c>ai</c>, <c>combat</c>, <c>story</c>, <c>perf</c>, <c>admin</c>.
    /// Лог — вне симуляции: время здесь настенное, в хэш ничего не идёт.
    /// </summary>
    public sealed class JsonLog : IDisposable
    {
        public static readonly string[] Categories = { "net", "world", "save", "ai", "combat", "story", "perf", "admin" };

        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            // Кириллица в логе — как есть, а не \uXXXX: лог читают люди и агенты.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly string? _dir;
        private readonly TextWriter? _console;
        private readonly Dictionary<string, LogLevel> _levels = new Dictionary<string, LogLevel>(StringComparer.Ordinal);
        private readonly MemoryStream _line = new MemoryStream(512);
        private StreamWriter? _file;
        private string? _fileDate;

        /// <param name="dir">Папка логов; null — только консоль.</param>
        /// <param name="console">Куда копировать строки; null — никуда.</param>
        /// <param name="levels">Уровни по категориям: «net=debug».</param>
        public JsonLog(string? dir, TextWriter? console, IEnumerable<string>? levels = null)
        {
            _dir = dir;
            _console = console;
            if (levels != null)
            {
                foreach (string spec in levels)
                {
                    string[] kv = spec.Split('=');
                    if (kv.Length != 2 || Array.IndexOf(Categories, kv[0]) < 0 || !TryParseLevel(kv[1], out LogLevel level))
                    {
                        throw new ArgumentException($"--log: «{spec}» — нужно категория=уровень ({string.Join("|", Categories)} = debug|info|warn|error).");
                    }

                    _levels[kv[0]] = level;
                }
            }
        }

        /// <summary>Путь текущего файла лога (null — файла нет).</summary>
        public string? FilePath { get; private set; }

        /// <summary>Пишется ли уровень <paramref name="level"/> в категории <paramref name="cat"/>.</summary>
        public bool Enabled(string cat, LogLevel level) =>
            level >= (_levels.TryGetValue(cat, out LogLevel min) ? min : LogLevel.Info);

        public void Info(long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            Write(LogLevel.Info, tick, cat, msg, data);

        public void Warn(long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            Write(LogLevel.Warn, tick, cat, msg, data);

        public void Error(long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            Write(LogLevel.Error, tick, cat, msg, data);

        public void Debug(long tick, string cat, string msg, params (string Key, object? Value)[] data) =>
            Write(LogLevel.Debug, tick, cat, msg, data);

        /// <summary>Записать строку лога.</summary>
        public void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
        {
            if (!Enabled(cat, level))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            _line.SetLength(0);
            using (var w = new Utf8JsonWriter(_line, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("time", now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteNumber("tick", tick);
                w.WriteString("level", LevelName(level));
                w.WriteString("cat", cat);
                w.WriteString("msg", msg);
                if (data != null && data.Length > 0)
                {
                    w.WriteStartObject("data");
                    foreach ((string key, object? value) in data)
                    {
                        WriteValue(w, key, value);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            string text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
            _console?.WriteLine(text);
            StreamWriter? file = FileFor(now);
            if (file != null)
            {
                file.WriteLine(text);
                file.Flush();
            }
        }

        public void Dispose()
        {
            _file?.Dispose();
            _file = null;
        }

        private StreamWriter? FileFor(DateTime now)
        {
            if (_dir == null)
            {
                return null;
            }

            string date = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (_file == null || _fileDate != date)
            {
                _file?.Dispose();
                Directory.CreateDirectory(_dir);
                FilePath = Path.Combine(_dir, $"server-{date}.jsonl");
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                _fileDate = date;
            }

            return _file;
        }

        private static void WriteValue(Utf8JsonWriter w, string key, object? value)
        {
            switch (value)
            {
                case null:
                    w.WriteNull(key);
                    break;
                case bool b:
                    w.WriteBoolean(key, b);
                    break;
                case int i:
                    w.WriteNumber(key, i);
                    break;
                case long l:
                    w.WriteNumber(key, l);
                    break;
                case ulong u:
                    w.WriteNumber(key, u);
                    break;
                case double d:
                    w.WriteNumber(key, Math.Round(d, 3));
                    break;
                default:
                    w.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Debug => "debug",
            LogLevel.Info => "info",
            LogLevel.Warn => "warn",
            _ => "error",
        };

        private static bool TryParseLevel(string s, out LogLevel level)
        {
            switch (s)
            {
                case "debug":
                    level = LogLevel.Debug;
                    return true;
                case "info":
                    level = LogLevel.Info;
                    return true;
                case "warn":
                    level = LogLevel.Warn;
                    return true;
                case "error":
                    level = LogLevel.Error;
                    return true;
                default:
                    level = LogLevel.Info;
                    return false;
            }
        }
    }
}
