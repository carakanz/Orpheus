#nullable enable
using System;
using System.Globalization;

namespace Orpheus.Client
{
    /// <summary>
    /// Аргументы командной строки клиента (карточка 0-010, шаг 6).
    /// <list type="bullet">
    /// <item><c>--seed N</c> — зерно мира (десятичное или <c>0x…</c>); по умолчанию — как у <c>tools/gen_preview</c>.</item>
    /// <item><c>--size N</c> — сторона мира-тора в тайлах (степень двойки, 512…8192); по умолчанию 1024.</item>
    /// <item><c>--smoke</c> — дымовая проверка: прогон через шов по X и Y скриптовым вводом и выход с кодом 0/1.</item>
    /// <item><c>--screenshot &lt;путь&gt;</c> — кадр мира с персонажем в PNG и выход.</item>
    /// </list>
    /// </summary>
    public sealed class ClientArgs
    {
        /// <summary>Зерно по умолчанию — то же, что у <c>tools/gen_preview</c> (0x0E50007): картинки сверяются без ключей.</summary>
        public const ulong DefaultSeed = 0x0E5_0007UL;

        /// <summary>Мир для тестов среза — 1024² (docs/00, 5.1).</summary>
        public const int DefaultSize = 1024;

        /// <summary>
        /// Меньше 512 нельзя: окно выхода 8 × 6 чанков (256 тайлов) должно быть меньше половины мира,
        /// иначе ближайшая копия чанка по <c>Torus.Delta</c> неоднозначна (docs/07, 3.2 — «повторов нет»).
        /// </summary>
        public const int MinSize = 512;

        public const int MaxSize = 8192;

        public ulong Seed = DefaultSeed;
        public int Size = DefaultSize;
        public bool Smoke;
        public string? ScreenshotPath;

        /// <summary>Ошибка разбора (неизвестное значение) — текст; null — всё хорошо.</summary>
        public string? Error;

        public static ClientArgs Parse(string[] args)
        {
            var a = new ClientArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i];
                string? next = i + 1 < args.Length ? args[i + 1] : null;
                switch (s)
                {
                    case "--smoke":
                        a.Smoke = true;
                        break;
                    case "--seed":
                        i++;
                        if (!TryParseSeed(next, out a.Seed))
                        {
                            a.Error = $"--seed: не число '{next}'";
                            a.Seed = DefaultSeed;
                        }

                        break;
                    case "--size":
                        i++;
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
                            || size < MinSize || size > MaxSize || (size & (size - 1)) != 0)
                        {
                            a.Error = $"--size: нужна степень двойки {MinSize}…{MaxSize}, дано '{next}'";
                        }
                        else
                        {
                            a.Size = size;
                        }

                        break;
                    case "--screenshot":
                        i++;
                        if (string.IsNullOrEmpty(next))
                        {
                            a.Error = "--screenshot: нет пути";
                        }
                        else
                        {
                            a.ScreenshotPath = next;
                        }

                        break;
                }
            }

            return a;
        }

        static bool TryParseSeed(string? s, out ulong seed)
        {
            seed = 0;
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            if (s!.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out seed);
            }

            return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
        }
    }
}
