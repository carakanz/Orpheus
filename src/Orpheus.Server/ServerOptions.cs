using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Orpheus.Core.Bots;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;

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
    /// (11.6), <c>--port P</c> — слушать UDP (0-008; 0 — порт выдаст ОС), по умолчанию только на петле 127.0.0.1/::1
    /// (тесты, smoke, игра на одной машине — без окна брандмауэра), <c>--public</c> — на всех адресах (игроки с других машин) и <c>--max-players N</c>.
    /// Сохранения (0-009, docs/07 6.4): <c>--world</c> без <c>--create</c> загружает мир — снимок генерации и новейшее
    /// целое поколение; <c>--autosave &lt;сек&gt;</c> — интервал автосохранения в секундах мира (по умолчанию 120;
    /// игроку настройка даётся 1–30 мин, меньше минуты — для тестов); <c>--debug-save-crash &lt;зерно&gt;</c> —
    /// отладочный крючок приёмки 0.4. <c>--ticks</c> — сколько тактов выполнить в этом запуске.
    /// <para>
    /// Сервер игры (0-024, docs/07 2.3): <c>--mode single|host --host-token &lt;32 hex&gt;</c> — запущен игрой как дочерний
    /// процесс; слушает только петлю, вход с токеном — хозяин; <c>--open-address all|IPv4 --open-port P</c> — где слушать по
    /// «Открыть для друзей» (по умолчанию все адреса, 7777); <c>--host-grace &lt;сек&gt;</c> — сколько ждать упавшую игру хоста
    /// (60). Порт и токен — в файле-замке <c>host.lock</c> папки мира; строка готовности <c>orpheus-ready port=P</c> — в stdout;
    /// команда <c>stop</c> строкой в stdin — штатная остановка.
    /// </para>
    /// </summary>
    public sealed class ServerOptions
    {
        public const string Usage =
            "OrpheusServer --world <папка> [--create --seed N --size 1024|4096] [--sim] [--bots N:idle|wander|shuttle|run_seam[,N:...]]\n" +
            "              [--ticks N] [--hash] [--record <журнал>] [--replay <журнал>] [--logs <папка>] [--log кат=уровень]\n" +
            "              [--port P [--public] [--max-players N]] [--spawn-seam (net-smoke: появление у шва)]\n" +
            "              [--debug-run-lanes (run_seam: расчищенные полосы, появление в углу швов; мир в памяти)]\n" +
            "              [--mode single|host --host-token <32 hex> [--open-address all|<IPv4>] [--open-port P] [--host-grace <сек>]]\n" +
            "              [--autosave <сек>] [--debug-save-crash <зерно>] [--debug-save-plan <миг>[:%]] [--debug-save-pad <КБ>]\n" +
            "              [--debug-freeze <такт>:<мс>[,<такт>:<мс>...]] [--dummy [--dummy-wait <кадры>]]";

        /// <summary>Автосохранение по умолчанию: раз в 2 мин мира (docs/07, 6.4).</summary>
        public const int DefaultAutosaveSeconds = 120;

        /// <summary>Наибольший интервал автосохранения: 30 мин (6.4).</summary>
        public const int MaxAutosaveSeconds = 30 * 60;

        /// <summary>Предел отладочного наполнения сохранения: 512 КБ (приёмка 0.4).</summary>
        public const int MaxDebugSavePadKb = 512;

        /// <summary>Ожидание упавшей игры хоста по умолчанию: 60 с (docs/07, 2.3 и 7.6).</summary>
        public const int DefaultHostGraceSeconds = 60;

        /// <summary>Порт «Открыть для друзей» по умолчанию: UDP 7777 (docs/07, 0 и 10).</summary>
        public const int DefaultOpenPort = 7777;

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

        /// <summary>
        /// <c>--debug-freeze &lt;такт&gt;:&lt;мс&gt;</c> (0-025, приёмка 2.13 п. 5): после такта поток такта стоит столько мс —
        /// как сборка мусора или антивирус. Стоит только поток такта: транспорт LiteNetLib (свой поток) отвечает на пинги —
        /// заморозку всего процесса делает скрипт приёмки снаружи. Только с часами (не <c>--sim</c>).
        /// </summary>
        public List<(long Tick, int Ms)> DebugFreezes { get; } = new List<(long Tick, int Ms)>();

        /// <summary>
        /// Слушать только петлю 127.0.0.1/::1 — по умолчанию: брандмауэр Windows не спрашивает разрешения у каждой
        /// новой сборки (рабочие копии агентов). <c>--public</c> — слушать все адреса, для игроков с других машин.
        /// </summary>
        public bool LoopbackOnly { get; set; } = true;

        /// <summary>Мест для игроков по сети (по умолчанию 16).</summary>
        public int MaxPlayers { get; set; } = 16;

        /// <summary>
        /// <c>--spawn-seam</c> (0-017, net-smoke): боты и игроки появляются у шва x = 0 на чистой строке, а не в середине
        /// карты; вместе с ботами <c>shuttle</c> — чужие ходят через шов на глазах у клиента.
        /// </summary>
        public bool SpawnSeam { get; set; }

        /// <summary>
        /// <c>--debug-run-lanes</c> (0-027, приёмка 0.2): тестовый мир для <c>run_seam</c> — полосы бега расчищены на тайлах
        /// генерации (<see cref="Orpheus.Core.Bots.RunSeamLanes"/>), игроки появляются у угла (0, 0), где сходятся оба шва.
        /// Только мир в памяти (<c>--create</c> без <c>--world</c>): расчищенный мир не сохраняется.
        /// </summary>
        public bool DebugRunLanes { get; set; }

        /// <summary>
        /// Ключ отладки шипа 0-029: манекен в 1 т к востоку от первой точки появления бьёт ближним на запад раз в 1,5 с,
        /// удар по игрокам решается с ожиданием ввода (<see cref="DummyWaitFrames"/>).
        /// </summary>
        public bool Dummy { get; set; }

        /// <summary>Ожидание ввода W для решения удара манекена, кадров (по умолчанию 6 = 100 мс; 0 — без ожидания).</summary>
        public int DummyWaitFrames { get; set; } = 6;

        /// <summary>Режим сервера игры (<c>--mode</c>); null — выделенный сервер.</summary>
        public ServerMode? Mode { get; set; }

        /// <summary>Токен хозяина (<c>--host-token</c>).</summary>
        public HostToken HostToken { get; set; }

        /// <summary>Адрес «Открыть для друзей» (<c>--open-address</c>): null — все адреса.</summary>
        public IPAddress? OpenAddress { get; set; }

        /// <summary>Порт «Открыть для друзей» (<c>--open-port</c>); 0 — выдаст ОС.</summary>
        public int OpenPort { get; set; } = DefaultOpenPort;

        /// <summary>Сколько ждать упавшую игру хоста, с (<c>--host-grace</c>).</summary>
        public int HostGraceSeconds { get; set; } = DefaultHostGraceSeconds;

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
                    case "--public":
                        o.LoopbackOnly = false;
                        break;
                    case "--spawn-seam":
                        o.SpawnSeam = true;
                        break;
                    case "--debug-run-lanes":
                        o.DebugRunLanes = true;
                        break;
                    case "--dummy":
                        o.Dummy = true;
                        break;
                    case "--dummy-wait":
                        o.DummyWaitFrames = ParseInt(a, Value(args, ref i));
                        if (o.DummyWaitFrames < 0 || o.DummyWaitFrames > 30)
                        {
                            throw new ArgumentException($"--dummy-wait: {o.DummyWaitFrames} вне 0…30.");
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
                    case "--debug-freeze":
                        foreach (string part in Value(args, ref i).Split(','))
                        {
                            string[] tm = part.Split(':');
                            long ft = tm.Length == 2 ? ParseLong(a, tm[0]) : -1;
                            int ms = tm.Length == 2 ? ParseInt(a, tm[1]) : -1;
                            if (ft < 0 || ms < 1 || ms > 60_000)
                            {
                                throw new ArgumentException($"--debug-freeze: «такт:мс», мс 1…60000, а не «{part}».");
                            }

                            o.DebugFreezes.Add((ft, ms));
                        }

                        break;
                    case "--mode":
                        string mode = Value(args, ref i);
                        o.Mode = mode == "single" ? ServerMode.Single
                            : mode == "host" ? ServerMode.Host
                            : throw new ArgumentException($"--mode: single или host, а не «{mode}».");
                        break;
                    case "--host-token":
                        string token = Value(args, ref i);
                        if (!HostToken.TryParse(token, out HostToken t))
                        {
                            throw new ArgumentException($"--host-token: {HostToken.HexLength} шестнадцатеричных цифр, не нули.");
                        }

                        o.HostToken = t;
                        break;
                    case "--open-address":
                        string address = Value(args, ref i);
                        if (address == "all")
                        {
                            o.OpenAddress = null;
                        }
                        else if (IPAddress.TryParse(address, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            o.OpenAddress = ip;
                        }
                        else
                        {
                            throw new ArgumentException($"--open-address: all или адрес IPv4, а не «{address}».");
                        }

                        break;
                    case "--open-port":
                        o.OpenPort = ParseInt(a, Value(args, ref i));
                        if (o.OpenPort < 0 || o.OpenPort > 65535)
                        {
                            throw new ArgumentException($"--open-port: {o.OpenPort} вне 0…65535.");
                        }

                        break;
                    case "--host-grace":
                        o.HostGraceSeconds = ParseInt(a, Value(args, ref i));
                        if (o.HostGraceSeconds < 1 || o.HostGraceSeconds > 600)
                        {
                            throw new ArgumentException($"--host-grace: 1…600 с, а не {o.HostGraceSeconds}.");
                        }

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

            if (o.DebugRunLanes && (o.WorldDir != null || o.ReplayPath != null || o.RecordPath != null || o.SpawnSeam))
            {
                throw new ArgumentException("--debug-run-lanes — только мир в памяти (--create без --world), без --record/--replay и --spawn-seam.");
            }

            if (o.Mode.HasValue)
            {
                if (!o.Port.HasValue || o.WorldDir == null || o.HostToken.IsEmpty)
                {
                    throw new ArgumentException("--mode: нужны --port, --world и --host-token (сервер игры пишет файл-замок в папку мира).");
                }

                if (!o.LoopbackOnly)
                {
                    throw new ArgumentException("--mode и --public вместе нельзя: сервер игры открывается для друзей командой хозяина.");
                }
            }
            else if (!o.HostToken.IsEmpty)
            {
                throw new ArgumentException("--host-token — только с --mode single|host.");
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
