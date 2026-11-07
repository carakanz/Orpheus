using System;
using System.Globalization;
using Orpheus.Core.Bots;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Bots
{
    /// <summary>
    /// Ключи ботов по сети (docs/07, 11.3; карточка 0-008):
    /// <c>--connect host:port --count N --script wander --rtt мс --jitter мс [--burst] --loss % [--dup %] [--reorder %]
    /// [--seed N] --minutes M [--seconds S] --report &lt;json&gt; [--name префикс] [--version x.y.z]</c>.
    /// </summary>
    public sealed class BotOptions
    {
        public const string Usage =
            "Orpheus.Bots --connect host:port [--count N] [--script idle|wander] [--rtt мс] [--jitter мс] [--burst]\n" +
            "             [--loss %] [--dup %] [--reorder %] [--seed N] [--minutes M | --seconds S] [--report <json>]\n" +
            "             [--name префикс] [--version x.y.z]";

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
                    default:
                        throw new ArgumentException($"Неизвестный ключ: {a}.");
                }
            }

            if (!connect)
            {
                throw new ArgumentException("Нужен --connect host:port.");
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
