using System;
using System.Globalization;
using Orpheus.Core.Bots;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Bots
{
    /// <summary>
    /// Ключи ботов по сети (docs/07, 11.3; карточка 0-008):
    /// <c>--connect host:port --count N --script wander --rtt мс --jitter мс [--burst] --loss % [--dup %] [--reorder %]
    /// [--seed N] --minutes M [--seconds S] --report &lt;json&gt; [--name префикс] [--version x.y.z]</c>.
    /// <para>
    /// «Игра хоста» без Unity (0-024, docs/07 2.3): <c>--launch &lt;OrpheusServer&gt; --world &lt;папка&gt; [--create-seed N]
    /// [--mode single|host] [--open-address A] [--open-port P] [--host-grace сек] [--server-arg ключ]…</c> — запустить сервер
    /// лаунчером и войти хозяином; <c>--find &lt;папка мира&gt;</c> — найти работающий сервер по файлу-замку и войти хозяином;
    /// <c>--host-token &lt;32 hex&gt;</c> — войти хозяином по известному токену; <c>--open-for-friends</c> — бот 0 после входа
    /// жмёт «Открыть для друзей».
    /// </para>
    /// </summary>
    public sealed class BotOptions
    {
        public const string Usage =
            "Orpheus.Bots --connect host:port [--count N] [--script idle|wander|shuttle|run_seam|seam_watch] [--rtt мс] [--jitter мс] [--burst]\n" +
            "             [--loss %] [--dup %] [--reorder %] [--seed N] [--minutes M | --seconds S] [--report <json>]\n" +
            "             [--name префикс] [--version x.y.z] [--host-token <32 hex>] [--open-for-friends]\n" +
            "Orpheus.Bots --launch <OrpheusServer> --world <папка> [--create-seed N] [--mode single|host] [--open-address A]\n" +
            "             [--open-port P] [--host-grace сек] [--server-arg ключ]... [ключи ботов без --connect]\n" +
            "Orpheus.Bots --find <папка мира> [ключи ботов без --connect]";

        /// <summary>Токен хозяина: бот 0 входит хозяином (остальные — обычными игроками).</summary>
        public HostToken HostToken { get; set; }

        /// <summary>Бот 0 после входа жмёт «Открыть для друзей».</summary>
        public bool OpenForFriends { get; set; }

        /// <summary>Запустить сервер лаунчером «ПК» (путь к <c>OrpheusServer</c>); null — сервер уже есть.</summary>
        public string? LaunchServer { get; set; }

        /// <summary>Что запустить (<see cref="LaunchServer"/>).</summary>
        public ServerLaunch Launch { get; } = new ServerLaunch();

        /// <summary>Найти работающий сервер мира по файлу-замку (<c>--find</c>).</summary>
        public string? FindWorld { get; set; }

        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; }
        public int Count { get; set; } = 1;
        public string Script { get; set; } = "wander";
        public int RttMs { get; set; }
        public int JitterMs { get; set; }
        public bool Burst { get; set; }
        public double LossPercent { get; set; }
        public double DuplicatePercent { get; set; }
        public double ReorderPercent { get; set; }
        public ulong Seed { get; set; } = 1;
        public double Seconds { get; set; } = 60;
        public string? ReportPath { get; set; }
        public string Name { get; set; } = "bot";

        /// <summary>Версия, которой бот представляется (для проверки отказа входа).</summary>
        public NetVersion Version { get; set; } = NetModule.Version;

        /// <summary>Для тестов: каждый принятый снимок бота № i (в потоке ботов). Не ключ.</summary>
        public Action<int, Snapshot, int>? OnSnapshot { get; set; }

        /// <summary>Для тестов: начало кадра ботов (в потоке ботов) с его временем, мкс от старта ботов, — тем же, что уходит в опрос транспорта и имитатора; метка для замера задержки (0-032). Не ключ.</summary>
        public Action<long>? OnFrame { get; set; }

        /// <summary>Настройки имитатора бота № <paramref name="index"/>: своё зерно у каждого.</summary>
        public NetSimSettings SimFor(int index) => new NetSimSettings
        {
            DelayMs = RttMs / 2,
            JitterMs = JitterMs,
            BurstJitter = Burst,
            LossPpm = NetSimSettings.PercentToPpm(LossPercent),
            DuplicatePpm = NetSimSettings.PercentToPpm(DuplicatePercent),
            ReorderPpm = NetSimSettings.PercentToPpm(ReorderPercent),
            Seed = unchecked(Seed * 0x9E3779B97F4A7C15UL + (ulong)index),
        };

        public static BotOptions Parse(string[] args)
        {
            var o = new BotOptions();
            bool connect = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--connect":
                        ParseEndpoint(Value(args, ref i), o);
                        connect = true;
                        break;
                    case "--count":
                        o.Count = Int(a, Value(args, ref i), 1, 255);
                        break;
                    case "--script":
                        o.Script = Value(args, ref i);
                        if (Array.IndexOf(SimBot.Scripts, o.Script) < 0)
                        {
                            throw new ArgumentException($"--script: сценария «{o.Script}» нет; есть: {string.Join(", ", SimBot.Scripts)}.");
                        }

                        break;
                    case "--rtt":
                        o.RttMs = Int(a, Value(args, ref i), 0, 10_000);
                        break;
                    case "--jitter":
                        o.JitterMs = Int(a, Value(args, ref i), 0, 5_000);
                        break;
                    case "--burst":
                        o.Burst = true;
                        break;
                    case "--loss":
                        o.LossPercent = Percent(a, Value(args, ref i));
                        break;
                    case "--dup":
                        o.DuplicatePercent = Percent(a, Value(args, ref i));
                        break;
                    case "--reorder":
                        o.ReorderPercent = Percent(a, Value(args, ref i));
                        break;
                    case "--seed":
                        string s = Value(args, ref i);
                        o.Seed = ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed)
                            ? seed
                            : throw new ArgumentException($"--seed: «{s}» — не целое число.");
                        break;
                    case "--minutes":
                        o.Seconds = Number(a, Value(args, ref i)) * 60;
                        break;
                    case "--seconds":
                        o.Seconds = Number(a, Value(args, ref i));
                        break;
                    case "--report":
                        o.ReportPath = Value(args, ref i);
                        break;
                    case "--name":
                        o.Name = Value(args, ref i);
                        break;
                    case "--version":
                        o.Version = ParseVersion(Value(args, ref i));
                        break;
                    case "--host-token":
                        if (!HostToken.TryParse(Value(args, ref i), out HostToken token))
                        {
                            throw new ArgumentException($"--host-token: {HostToken.HexLength} шестнадцатеричных цифр, не нули.");
                        }

                        o.HostToken = token;
                        break;
                    case "--open-for-friends":
                        o.OpenForFriends = true;
                        break;
                    case "--launch":
                        o.LaunchServer = Value(args, ref i);
                        connect = true;
                        break;
                    case "--world":
                        o.Launch.WorldDir = Value(args, ref i);
                        break;
                    case "--create-seed":
                        o.Launch.Create = true;
                        o.Launch.Seed = ulong.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out ulong ws)
                            ? ws
                            : throw new ArgumentException("--create-seed: целое без знака.");
                        break;
                    case "--mode":
                        string mode = Value(args, ref i);
                        o.Launch.Mode = mode == "single" ? ServerMode.Single
                            : mode == "host" ? ServerMode.Host
                            : throw new ArgumentException($"--mode: single или host, а не «{mode}».");
                        break;
                    case "--open-address":
                        o.Launch.OpenAddress = Value(args, ref i);
                        break;
                    case "--open-port":
                        o.Launch.OpenPort = Int(a, Value(args, ref i), 0, 65535);
                        break;
                    case "--host-grace":
                        o.Launch.HostGraceSeconds = Int(a, Value(args, ref i), 1, 600);
                        break;
                    case "--server-arg":
                        o.Launch.ExtraArgs.Add(Value(args, ref i));
                        break;
                    case "--find":
                        o.FindWorld = Value(args, ref i);
                        connect = true;
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный ключ: {a}.");
                }
            }

            if (!connect)
            {
                throw new ArgumentException("Нужен --connect host:port (или --launch / --find).");
            }

            if (o.LaunchServer != null && (o.FindWorld != null || o.Launch.WorldDir.Length == 0))
            {
                throw new ArgumentException("--launch: нужен --world <папка>; вместе с --find нельзя.");
            }

            if (o.Seconds <= 0)
            {
                throw new ArgumentException("--minutes/--seconds: время больше нуля.");
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

        private static void ParseEndpoint(string s, BotOptions o)
        {
            int colon = s.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(s.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port <= 0 || port > 65535)
            {
                throw new ArgumentException($"--connect: «{s}» — нужно host:port, например 127.0.0.1:7777.");
            }

            o.Host = s.Substring(0, colon).Trim('[', ']');
            o.Port = port;
        }

        private static int Int(string key, string s, int min, int max)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min || v > max)
            {
                throw new ArgumentException($"{key}: «{s}» — целое от {min} до {max}.");
            }

            return v;
        }

        private static double Number(string key, string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : throw new ArgumentException($"{key}: «{s}» — не число.");

        private static double Percent(string key, string s)
        {
            double v = Number(key, s.TrimEnd('%'));
            if (v < 0 || v > 100)
            {
                throw new ArgumentException($"{key}: {v} % вне 0…100.");
            }

            return v;
        }

        private static NetVersion ParseVersion(string s)
        {
            string[] p = s.Split('.');
            if (p.Length != 3
                || !ushort.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort a)
                || !ushort.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort b)
                || !ushort.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out ushort c))
            {
                throw new ArgumentException($"--version: «{s}» — нужно x.y.z.");
            }

            return new NetVersion(a, b, c);
        }
    }
}
