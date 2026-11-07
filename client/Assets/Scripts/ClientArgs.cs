#nullable enable
using System;
using System.Globalization;
using Orpheus.Net.Protocol;

namespace Orpheus.Client
{
    /// <summary>
    /// Аргументы командной строки клиента (карточка 0-010, шаг 6; сеть — карточка 0-012).
    /// <list type="bullet">
    /// <item><c>--seed N</c> — зерно мира (десятичное или <c>0x…</c>); по умолчанию — как у <c>tools/gen_preview</c>.
    /// В сетевом режиме зерно и размер присылает сервер.</item>
    /// <item><c>--size N</c> — сторона мира-тора в тайлах (степень двойки, 512…8192); по умолчанию 1024.</item>
    /// <item><c>--smoke</c> — дымовая проверка офлайна: прогон через шов по X и Y скриптовым вводом и выход с кодом 0/1.</item>
    /// <item><c>--connect хост:порт</c> — сетевой режим: вход на сервер, мир по его зерну, снимки с интерполяцией (0-012).</item>
    /// <item><c>--name N</c> — имя игрока для входа, 1…32 байта UTF-8 (как в протоколе; «Орфей» — 10); по умолчанию player-1.</item>
    /// <item><c>--net-smoke [сек]</c> — дымовая проверка сети: вход, играть сек секунд (по умолчанию 10), «[smoke] done», код 0/1.</item>
    /// <item><c>--net-smoke-seam</c> — net-smoke у шва (сервер с <c>--spawn-seam --bots 2:shuttle</c>): свой персонаж и чужие
    /// переходят шов x = 0, начало переезжает на шве (0-017).</item>
    /// <item><c>--hud</c> — текст состояния в окне (в сетевом режиме включено всегда).</item>
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

        /// <summary>Имя игрока по умолчанию для входа на сервер.</summary>
        public const string DefaultName = "player-1";

        public ulong Seed = DefaultSeed;
        public int Size = DefaultSize;
        public bool Smoke;
        public string? ScreenshotPath;

        /// <summary>«хост:порт» сервера; null — офлайн-режим (локальный такт).</summary>
        public string? Connect;
        public string Name = DefaultName;

        /// <summary>Секунд игры в net-smoke; 0 — режим выключен.</summary>
        public int NetSmokeSeconds;

        /// <summary>net-smoke требует перехода шва своим персонажем и чужими (0-017).</summary>
        public bool NetSmokeSeam;

        public bool Hud;

        /// <summary>
        /// Имитатор связи на клиенте (0-019): «RTT,σ,потери%[,зерно]» — например «100,20,1» или «200,30,1,7»; задержка
        /// RTT/2 в каждую сторону (<c>NetSimTransport</c> ядра). 0 — без имитатора. Зерно (0-021) — разные прогоны
        /// net-smoke на разных выборках пакетов; по умолчанию <see cref="DefaultNetSimSeed"/>.
        /// </summary>
        public int NetSimRttMs;
        public int NetSimJitterMs;
        public double NetSimLossPercent;
        public ulong NetSimSeed = DefaultNetSimSeed;

        /// <summary>Зерно имитатора без четвёртого числа в <c>--net-sim</c> (было зашито в 0-019).</summary>
        public const ulong DefaultNetSimSeed = 0x19;

        /// <summary>Ошибка разбора (неизвестное значение) — текст; null — всё хорошо.</summary>
        public string? Error;

        /// <summary>Текст подсказки для логов и подсказки при ошибке.</summary>
        public static string Usage =>
            "Orpheus [--seed N] [--size N] [--smoke] [--connect хост:порт] [--name N] " +
            "[--net-smoke [сек]] [--net-smoke-seam] [--net-sim RTT,σ,потери%[,зерно]] [--hud] [--screenshot путь]";

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
                    case "--connect":
                        i++;
                        if (!TryParseEndpoint(next, out string? endpoint))
                        {
                            a.Error = $"--connect: нужен хост:порт, дано '{next}'";
                        }
                        else
                        {
                            a.Connect = endpoint;
                        }

                        break;
                    case "--name":
                        i++;
                        string? nameError = LoginMessage.NameError(next); // байты UTF-8, как в протоколе (0-017)
                        if (nameError != null)
                        {
                            a.Error = "--name: " + nameError;
                        }
                        else
                        {
                            a.Name = next!;
                        }

                        break;
                    case "--net-smoke":
                        // Секунды необязательны: «--net-smoke 15» или «--net-smoke» (then другие ключи).
                        a.NetSmokeSeconds = 10;
                        if (int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs)
                            && secs >= 1 && secs <= 600)
                        {
                            a.NetSmokeSeconds = secs;
                            i++;
                        }

                        break;
                    case "--net-smoke-seam":
                        a.NetSmokeSeam = true;
                        break;
                    case "--hud":
                        a.Hud = true;
                        break;
                    case "--net-sim":
                        i++;
                        string[] parts = (next ?? string.Empty).Split(',');
                        if (parts.Length < 3 || parts.Length > 4
                            || parts.Length == 4 && !ulong.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.NetSimSeed)
                            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.NetSimRttMs)
                            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.NetSimJitterMs)
                            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out a.NetSimLossPercent)
                            || a.NetSimRttMs < 0 || a.NetSimRttMs > 2000 || a.NetSimJitterMs < 0 || a.NetSimJitterMs > 500
                            || a.NetSimLossPercent < 0 || a.NetSimLossPercent > 50)
                        {
                            a.Error = $"--net-sim: нужно RTT,σ,потери%[,зерно] (например 100,20,1 или 200,30,1,7), дано '{next}'";
                            a.NetSimRttMs = a.NetSimJitterMs = 0;
                            a.NetSimLossPercent = 0;
                            a.NetSimSeed = DefaultNetSimSeed;
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

            if (a.NetSmokeSeconds > 0 && a.Connect == null)
            {
                a.Error = "--net-smoke требует --connect";
            }

            return a;
        }

        /// <summary>«хост:порт» — порт 1…65535; хост — имя или адрес (IPv4).</summary>
        static bool TryParseEndpoint(string? s, out string? endpoint)
        {
            endpoint = null;
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            int colon = s!.LastIndexOf(':');
            if (colon <= 0 || colon == s.Length - 1)
            {
                return false;
            }

            string host = s.Substring(0, colon);
            if (!int.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                || port < 1 || port > 65_535)
            {
                return false;
            }

            endpoint = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            return true;
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
