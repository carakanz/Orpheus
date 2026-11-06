using System;
using System.Collections.Generic;
using System.Globalization;
using Orpheus.Core.Bots;

namespace Orpheus.Server
{
    /// <summary>Группа ботов из <c>--bots N:сценарий</c>.</summary>
    public readonly struct BotGroup
    {
        public readonly int Count;
        public readonly string Script;

        public BotGroup(int count, string script)
        {
            Count = count;
            Script = script;
        }

        public override string ToString() => $"{Count}:{Script}";
    }

    /// <summary>
    /// Ключи сервера по docs/07, 11.2:
    /// <c>--world &lt;папка&gt; --create --seed N --size 1024|4096 --sim --bots 8:wander --ticks N --hash
    /// --record &lt;путь&gt; | --replay &lt;путь&gt;</c>; плюс <c>--logs &lt;папка&gt;</c>, <c>--log кат=уровень</c>
    /// (11.6), <c>--port P</c> — слушать UDP (0-008; 0 — порт выдаст ОС) и <c>--max-players N</c>.
    /// Сохранения (0-009, docs/07 6.4): <c>--world</c> без <c>--create</c> загружает мир — снимок генерации и новейшее
    /// целое поколение; <c>--autosave &lt;сек&gt;</c> — интервал автосохранения в секундах мира (по умолчанию 120;
    /// игроку настройка даётся 1–30 мин, меньше минуты — для тестов); <c>--debug-save-crash &lt;зерно&gt;</c> —
    /// отладочный крючок приёмки 0.4. <c>--ticks</c> — сколько тактов выполнить в этом запуске.
    /// </summary>
    public sealed class ServerOptions
    {
        public const string Usage =
            "OrpheusServer --world <папка> [--create --seed N --size 1024|4096] [--sim] [--bots N:idle|wander[,N:...]]\n" +
            "              [--ticks N] [--hash] [--record <журнал>] [--replay <журнал>] [--logs <папка>] [--log кат=уровень]\n" +
            "              [--port P [--max-players N]]\n" +
            "              [--autosave <сек>] [--debug-save-crash <зерно>] [--debug-save-plan <миг>[:%]] [--debug-save-pad <КБ>]";

        /// <summary>Автосохранение по умолчанию: раз в 2 мин мира (docs/07, 6.4).</summary>
        public const int DefaultAutosaveSeconds = 120;

        /// <summary>Наибольший интервал автосохранения: 30 мин (6.4).</summary>
        public const int MaxAutosaveSeconds = 30 * 60;

        /// <summary>Предел отладочного наполнения сохранения: 512 КБ (приёмка 0.4).</summary>
        public const int MaxDebugSavePadKb = 512;

        public string? WorldDir { get; set; }
        public bool Create { get; set; }
        public ulong Seed { get; set; }
        public bool SeedGiven { get; set; }
        public int Size { get; set; } = 1024;
        public bool SizeGiven { get; set; }
        public bool Sim { get; set; }
        public List<BotGroup> Bots { get; } = new List<BotGroup>();
        public long Ticks { get; set; } = long.MaxValue;
        public bool Hash { get; set; }
        public string? RecordPath { get; set; }
        public string? ReplayPath { get; set; }
        public string? LogsDir { get; set; }
        public List<string> LogLevels { get; } = new List<string>();
        public int? Port { get; set; }
        public int AutosaveSeconds { get; set; } = DefaultAutosaveSeconds;
        public ulong? DebugSaveCrashSeed { get; set; }

        /// <summary>Отладочное наполнение сохранения в КБ (приёмка 0.4, docs/08 3.0): 0 — обычной игрой не пишется.</summary>
        public int DebugSavePadKb { get; set; }

        /// <summary>Заданный миг обрыва для приёмки 0.4 (<c>--debug-save-plan</c>), синтаксис — <see cref="SaveCrashHook.Fixed"/>.</summary>
        public string? DebugSavePlan { get; set; }

        /// <summary>Мест для игроков по сети (по умолчанию 16).</summary>
        public int MaxPlayers { get; set; } = 16;

        /// <summary>Для тестов: зовётся в потоке такта после каждого такта (и рассылки снимков). Не ключ.</summary>
        public Action<Orpheus.Core.Sim.Simulation>? AfterTick { get; set; }

        /// <summary>Разобрать ключи; ошибка — <see cref="ArgumentException"/> с понятным текстом.</summary>
        public static ServerOptions Parse(string[] args)
        {
            var o = new ServerOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--world":
                        o.WorldDir = Value(args, ref i);
                        break;
                    case "--create":
                        o.Create = true;
                        break;
                    case "--seed":
                        o.Seed = ParseSeed(Value(args, ref i));
                        o.SeedGiven = true;
                        break;
                    case "--size":
                        o.Size = ParseInt(a, Value(args, ref i));
                        o.SizeGiven = true;
                        if (o.Size != 1024 && o.Size != 4096)
                        {
                            throw new ArgumentException($"--size: размер мира 1024 или 4096 (docs/00, 5.1), а не {o.Size}.");
                        }

                        break;
                    case "--sim":
                        o.Sim = true;
                        break;
                    case "--bots":
                        ParseBots(Value(args, ref i), o.Bots);
                        break;
                    case "--ticks":
                        o.Ticks = ParseLong(a, Value(args, ref i));
                        if (o.Ticks < 0)
                        {
                            throw new ArgumentException("--ticks: число тактов не отрицательное.");
                        }

                        break;
                    case "--hash":
                        o.Hash = true;
                        break;
                    case "--record":
                        o.RecordPath = Value(args, ref i);
                        break;
                    case "--replay":
                        o.ReplayPath = Value(args, ref i);
                        break;
                    case "--logs":
                        o.LogsDir = Value(args, ref i);
                        break;
                    case "--log":
                        o.LogLevels.Add(Value(args, ref i));
                        break;
                    case "--port":
                        o.Port = ParseInt(a, Value(args, ref i));
                        if (o.Port < 0 || o.Port > 65535)
                        {
                            throw new ArgumentException($"--port: {o.Port} вне 0…65535.");
                        }

                        break;
                    case "--max-players":
                        o.MaxPlayers = ParseInt(a, Value(args, ref i));
                        if (o.MaxPlayers <= 0 || o.MaxPlayers > 255)
                        {
                            throw new ArgumentException($"--max-players: {o.MaxPlayers} вне 1…255.");
                        }

                        break;
                    case "--autosave":
                        o.AutosaveSeconds = ParseInt(a, Value(args, ref i));
                        if (o.AutosaveSeconds < 1 || o.AutosaveSeconds > MaxAutosaveSeconds)
                        {
                            throw new ArgumentException($"--autosave: 1…{MaxAutosaveSeconds} с, а не {o.AutosaveSeconds}.");
                        }

                        break;
                    case "--debug-save-crash":
                        o.DebugSaveCrashSeed = ParseSeed(Value(args, ref i));
                        break;
                    case "--debug-save-pad":
                        o.DebugSavePadKb = ParseInt(a, Value(args, ref i));
                        if (o.DebugSavePadKb < 0 || o.DebugSavePadKb > MaxDebugSavePadKb)
                        {
                            throw new ArgumentException($"--debug-save-pad: 0…{MaxDebugSavePadKb} КБ, а не {o.DebugSavePadKb}.");
                        }

                        break;
                    case "--debug-save-plan":
                        o.DebugSavePlan = Value(args, ref i);
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный ключ: {a}.");
                }
            }

            if (o.ReplayPath != null && o.RecordPath != null)
            {
                throw new ArgumentException("--record и --replay вместе нельзя.");
            }

            if (o.ReplayPath != null && o.Bots.Count > 0)
            {
                throw new ArgumentException("--replay берёт ввод из журнала: --bots не нужен.");
            }

            if (o.ReplayPath != null && !o.Sim)
            {
                throw new ArgumentException("--replay — только в режиме симуляции (--sim).");
            }

            if (o.Port.HasValue && (o.Sim || o.RecordPath != null || o.ReplayPath != null))
            {
                throw new ArgumentException("--port — только в обычном режиме: игроки по сети с --sim, --record и --replay — следующими задачами.");
            }

            if (o.WorldDir == null && !o.Create && o.ReplayPath == null)
            {
                throw new ArgumentException("Нужен --world <папка> (или --create для мира только в памяти).");
            }

            return o;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{args[i]}: нет значения.");
            }

            return args[++i];
        }

        private static int ParseInt(string key, string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : throw new ArgumentException($"{key}: «{s}» — не целое число.");

        private static long ParseLong(string key, string s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
                ? v
                : throw new ArgumentException($"{key}: «{s}» — не целое число.");

        private static ulong ParseSeed(string s)
        {
            if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u))
            {
                return u;
            }

            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            {
                return unchecked((ulong)l);
            }

            throw new ArgumentException($"--seed: «{s}» — не целое число.");
        }

        private static void ParseBots(string s, List<BotGroup> into)
        {
            foreach (string part in s.Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0)
                {
                    throw new ArgumentException($"--bots: «{part}» — нужно N:сценарий, например 8:wander.");
                }

                if (Array.IndexOf(SimBot.Scripts, kv[1]) < 0)
                {
                    throw new ArgumentException($"--bots: сценария «{kv[1]}» нет; есть: {string.Join(", ", SimBot.Scripts)}.");
                }

                into.Add(new BotGroup(n, kv[1]));
            }
        }
    }
}
