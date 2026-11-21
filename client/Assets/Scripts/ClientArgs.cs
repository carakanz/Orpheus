#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Orpheus.Net.Hosting;
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
    /// <item><c>--hud</c> — отладочные надписи в окне: «Orpheus — TEMP ASSETS» и строка состояния (E1-1; без ключа в сети
    /// видна только строка входа, пока игрок не в игре).</item>
    /// <item><c>--screenshot &lt;путь&gt;</c> — кадр мира с персонажем в PNG и выход. Сцена кадра (стенд <c>tools/shots.ps1</c>, E1-1):
    /// <c>--shot-tile X,Y</c> — тайл, куда ставится персонаж (офлайн; по умолчанию — старт), <c>--shot-width N</c> — обзор
    /// в тайлах по ширине, 40…96 (по умолчанию 64), <c>--shot-size ШxВ</c> — размер кадра в пикселях (по умолчанию 1920x1080).
    /// Кадр рисуется в текстуру, окно не нужно: запускать <c>Orpheus.exe -batchmode --screenshot …</c> (без <c>-nographics</c>).</item>
    /// <item><c>--gen v1|stub</c> — генератор мира офлайна (E1-1): по умолчанию тот же, что у сервера (<c>v1</c>);
    /// <c>stub</c> — заглушка 0-007 для <c>--smoke</c> и <c>--tile-bench</c> (их проверки сверяются с ней).</item>
    /// <item><c>--single</c> / <c>--host</c> — одиночная игра / хост (0-024, docs/07 2.3; меню — потом): игра запускает
    /// <c>OrpheusServer</c> дочерним процессом (<c>PcServerLauncher</c>) и входит хозяином через 127.0.0.1. Сервер уже
    /// работает с этим миром (игра упала, 7.6) — подключается к нему по <c>host.lock</c>.</item>
    /// <item><c>--world &lt;папка&gt;</c> — папка мира; нет <c>world.base</c> — мир создаётся по <c>--seed</c>/<c>--size</c>.
    /// По умолчанию — <c>Worlds/world-1</c> в <c>Application.persistentDataPath</c>.</item>
    /// <item><c>--server &lt;путь&gt;</c> — <c>OrpheusServer.exe</c>/<c>.dll</c>; по умолчанию — рядом с <c>Orpheus.exe</c>.</item>
    /// <item><c>--server-arg A</c> — ещё ключ серверу (повторяемый): <c>--server-arg --bots --server-arg 2:wander</c>.</item>
    /// <item><c>--open-for-friends</c> — сразу после входа «Открыть для друзей» (иначе клавиша F2).</item>
    /// <item><c>--open-address IPv4</c>, <c>--open-port P</c> — где открыть для друзей; по умолчанию все адреса и 7777.
    /// Прогоны и тесты — только 127.0.0.2: бинд на все адреса вызывает окно брандмауэра.</item>
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

        /// <summary>Сцена снимка (E1-1): тайл персонажа; нет ключа — старт.</summary>
        public bool HasShotTile;
        public int ShotTileX;
        public int ShotTileY;

        /// <summary>Сцена снимка (E1-1): обзор по ширине, тайлов; 0 — по умолчанию (64).</summary>
        public int ShotWidth;

        /// <summary>Сцена снимка (E1-1): размер кадра в пикселях.</summary>
        public int ShotPixelsX = 1920;
        public int ShotPixelsY = 1080;

        /// <summary>
        /// E1-11, лента кадров <c>--shot-walk НАПРАВЛЕНИЕ:КАДРОВ:ИНТЕРВАЛ[:РАЗБЕГ]</c> (вместе с <c>--screenshot</c>): персонаж
        /// скриптом идёт в сторону (e, w, n, s, ne, nw, se, sw; stay — стоит), кадры каждые ИНТЕРВАЛ секунд после РАЗБЕГА
        /// вырезаются вокруг персонажа (<c>--shot-crop ШxВ</c>) и склеиваются в одну картинку. 0 кадров — обычный снимок.
        /// </summary>
        public int WalkFrames;
        public float WalkDirX, WalkDirY, WalkInterval = 0.15f, WalkLead;
        public int CropX = 640, CropY = 400;

        /// <summary>Ключ генератора офлайна (<c>WorldGenModule.ForKey</c>): «v1» (как у сервера) или «stub».</summary>
        public string Gen = "v1";

        /// <summary>«хост:порт» сервера; null — офлайн-режим (локальный такт).</summary>
        public string? Connect;
        public string Name = DefaultName;

        /// <summary>Секунд игры в net-smoke; 0 — режим выключен.</summary>
        public int NetSmokeSeconds;

        /// <summary>net-smoke требует перехода шва своим персонажем и чужими (0-017).</summary>
        public bool NetSmokeSeam;

        /// <summary>
        /// net-smoke проходит дверь (1-012): сервер получает <c>--debug-door</c> (закрытая дверь в 4 тайлах
        /// к востоку от появления); прогон бежит на восток — автооткрытие шагом, проход, автозакрытие через 30 тактов.
        /// </summary>
        public bool NetSmokeDoor;

        /// <summary>
        /// C-030: net-smoke ложится в кровать — сервер получает <c>--debug-bed</c> (ряд кроватей под точками появления);
        /// в 0,15 с — F тем же путём, что клавиша (<c>ObjectUse.Find</c> → заявка 33), ответ «принято» кладёт персонажа,
        /// скриптовый бег с 0,5 с поднимает. Проверка — в <c>NetSmokeCheck</c>.
        /// </summary>
        public bool NetSmokeBed;

        /// <summary>
        /// C-048: net-smoke на ЖИВОМ пути ввода (не скриптованные кадры): synthetic-мышь и клавиатура
        /// (<c>NetGame.SmokeBow</c>) держат ЛКМ 5,0–10,0 с с луком в руках (ячейка 5) — проверка, что
        /// удержание даёт серию выстрелов («лук при зажатии стреляет нонстопом», слово Автора 21.11).
        /// </summary>
        public bool NetSmokeBow;

        /// <summary>
        /// C-048: тот же лук, но курсор — над нижней панелью (низ экрана, где сферы и способности: Автор целится
        /// в моба у ног, курсор оказывается над панелью, и E2-2 «мир не отвечает» глушит выстрелы). Проверка, что
        /// с боевым оружием в руках ЛКМ уходит в мир даже над панелью.
        /// </summary>
        public bool NetSmokeBowBar;

        public bool Hud;

        /// <summary>Одиночная / хост: сервер дочерним процессом (0-024); null — офлайн или <see cref="Connect"/>.</summary>
        public ServerMode? Mode;

        /// <summary>
        /// «Сервер в потоке» (0-031, docs/07 1.6, 2.3 «На консоли»): одиночная игра, <c>ServerHost</c> в потоке игры, клиент — через
        /// локальную связь; сохранения — через <c>PcStorage</c>. Ключ <c>--server-thread</c> (сам включает <c>--single</c>). На ПК по
        /// умолчанию остаётся дочерний процесс (0-024).
        /// </summary>
        public bool ServerThread;

        /// <summary>Папка мира одиночной/хоста; null — по умолчанию (<c>persistentDataPath/Worlds/world-1</c>).</summary>
        public string? WorldDir;

        /// <summary>Путь к <c>OrpheusServer</c>; null — рядом со сборкой клиента.</summary>
        public string? ServerPath;

        /// <summary>Ключи серверу сверх тех, что ставит лаунчер.</summary>
        public readonly List<string> ServerArgs = new List<string>();

        public bool OpenForFriends;

        /// <summary>
        /// Игра запущена из главного меню (E4-1): после смерти героя или обрыва
        /// связи игрок возвращается в меню, а не остаётся на чёрном экране.
        /// </summary>
        public bool FromMenu;

        /// <summary>
        /// E4-1b: <c>--auto-menu-click</c> — автоклик по кнопке «В меню» на экране «Уведомление»
        /// (живая проверка «хост убит → уведомление → возврат в меню» без рук; тот же путь, что мышь).
        /// </summary>
        public bool AutoMenuClick;

        /// <summary>
        /// K-001, офлайн: <c>--anim-shots &lt;папка&gt;</c> — показ процедурной анимации (ходьба, уворот, замах с сектором, попадание,
        /// копание) и серии кадров в PNG (<c>K-001_*.png</c>); <c>--anim-bench &lt;сек&gt;</c> — замер fps с 8 персонажами и
        /// 40 врагами-болванками. Нужен вывод на экран — без <c>-batchmode -nographics</c>.
        /// </summary>
        public string? AnimShotsDir;
        public int AnimBenchSeconds;

        /// <summary>
        /// 1-022: <c>--tile-bench &lt;сек&gt;</c> — стенд «48 Tilemap»: бег через шов на самом дальнем обзоре (96 тайлов) и поток правок
        /// (20 тайлов/с + пачки 32 тайла), замеры fps и стоимости правок (шаг 1–2; приёмка 1.11).
        /// </summary>
        public int TileBenchSeconds;

        /// <summary>
        /// E1-4: вместе с <c>--tile-bench</c> — каждый кадр камера рисует в текстуру 1920 × 1080 и ждёт видеокарту
        /// (чтение одного пикселя): время кадра включает отрисовку. Без окна: <c>-batchmode</c> без <c>-nographics</c>.
        /// </summary>
        public bool BenchRender;

        /// <summary>
        /// E1-4, только для снимков: офлайн после генерации область у старта перекрашивается в тестовый узор
        /// (озеро с песчаным берегом, пятна всех видов земли, стык видов) — <see cref="PaintDemo"/>.
        /// </summary>
        public bool PaintDemo;

        /// <summary>E2-1, только для снимков без сервера: вокруг персонажа лежат кучи предметов всех значков (<see cref="PileView.AddDemo"/>).</summary>
        public bool DemoPiles;

        /// <summary>E2-2, только для снимков без сервера: показ интерфейса — <c>bag</c> (окно сумки открыто, предмет «в руке») или <c>hotbar</c> (панель и подсказка).</summary>
        public string? UiDemo;

        /// <summary>
        /// E2-17, только для снимков без сервера: дом из тонких стен с мебелью, дверями и забором вокруг персонажа
        /// (<see cref="global::Orpheus.Client.PaintDemo.Home"/>); слово — где стоит персонаж: <c>overview</c>, <c>south</c>, <c>north</c>,
        /// <c>behind</c>, <c>door</c>, <c>camp</c>, <c>fence</c>, <c>torches</c> (C-017: обзор и 10 факелов), <c>room</c> (C-017: персонаж в комнате), <c>wall</c> (C-017: факел и валун у стены).
        /// </summary>
        public string? HomeDemo;

        /// <summary>
        /// C-030, только для снимков без сервера: спальня с двуспальной кроватью (<see cref="global::Orpheus.Client.PaintDemo.Bed"/>);
        /// слово — поворот кровати 0…3, с буквой <c>l</c> — персонаж лежит в ней (поза и затемнение), напр. <c>0</c>, <c>1l</c>.
        /// </summary>
        public int BedDemoRot = -1;
        public bool BedDemoLie;

        /// <summary>
        /// C-018, только для снимков без сервера: пруд и кусты вокруг персонажа (<see cref="global::Orpheus.Client.PaintDemo.Wade"/>);
        /// слово — <c>water</c> (персонаж в воде), <c>bush</c> (в кустах), <c>enemy</c> (враг-болванка в воде).
        /// </summary>
        public string? WadeDemo;

        /// <summary>
        /// C-049, только для снимков без сервера: костёр в пяти тайлах севернее старта, персонаж на заданной
        /// стороне огня — <c>in</c> (в кольце камней, на огне), <c>south</c>, <c>north</c>, <c>east</c>, <c>west</c>;
        /// проверяет порядок отрисовки «низкой полосы» (LowRowOrderAt) и живое пламя.
        /// </summary>
        public string? FireDemo;

        /// <summary>E1-7, только для замера <c>--tile-bench</c>: плотный лес вдоль полос бега стенда (<c>PaintDemo.ApplyBenchForest</c>).</summary>
        public bool PaintForest;

        /// <summary>
        /// E1-8 (тон суток): такт суток мира (0…35 999), задающий время офлайн-снимков — у офлайна часы мира
        /// стартуют с 0 (Simulation.WorldTime), ключ задаёт сдвиг. В сети время присылает сервер, ключ игнорируется.
        /// </summary>
        public int DayTick;

        /// <summary>Адрес «Открыть для друзей»; null — все адреса (игра).</summary>
        public string? OpenAddress;

        /// <summary>Порт «Открыть для друзей»: по умолчанию 7777 (docs/07, 0 и 10); 0 — выдаст ОС.</summary>
        public int OpenPort = 7777;

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
            "[--net-smoke [сек]] [--net-smoke-seam] [--net-sim RTT,σ,потери%[,зерно]] [--hud] [--auto-menu-click] [--screenshot путь [--shot-tile X,Y] [--shot-width 40…96] [--shot-size ШxВ]] [--gen v1|stub] " +
            "[--single|--host] [--server-thread] [--world папка] [--server путь] [--server-arg A]… [--open-for-friends] [--open-address IPv4] [--open-port P] " +
            "[--anim-shots папка] [--anim-bench сек] [--tile-bench сек [--bench-render]] [--paint-demo] [--demo-piles] [--ui-demo bag|hotbar|craft|craft+bag|craft-hint|craft-chest|inv|inv-weapons|inv-buildings|inv-stone|build|build-open|chest|house|thin|half|turn|oldbeds|rmb-open|rmb-disarm|cursor-place|ruler-wall|ruler-floor] [--home-demo overview|south|north|behind|door|camp|fence|torches|room|wall] [--day-tick 0…35999]";

        static bool TryParseWalk(string? text, ClientArgs a)
        {
            string[] p = (text ?? string.Empty).ToLowerInvariant().Split(':');
            if (p.Length < 3 || p.Length > 4)
            {
                return false;
            }

            switch (p[0])
            {
                case "e": a.WalkDirX = 1f; a.WalkDirY = 0f; break;
                case "w": a.WalkDirX = -1f; a.WalkDirY = 0f; break;
                case "n": a.WalkDirX = 0f; a.WalkDirY = 1f; break;
                case "s": a.WalkDirX = 0f; a.WalkDirY = -1f; break;
                case "ne": a.WalkDirX = 1f; a.WalkDirY = 1f; break;
                case "nw": a.WalkDirX = -1f; a.WalkDirY = 1f; break;
                case "se": a.WalkDirX = 1f; a.WalkDirY = -1f; break;
                case "sw": a.WalkDirX = -1f; a.WalkDirY = -1f; break;
                case "stay": a.WalkDirX = 0f; a.WalkDirY = 0f; break;
                default: return false;
            }

            a.WalkLead = 0f;
            return int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.WalkFrames)
                && a.WalkFrames >= 1 && a.WalkFrames <= 16
                && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out a.WalkInterval)
                && a.WalkInterval > 0f && a.WalkInterval <= 5f
                && (p.Length < 4 || (float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a.WalkLead) && a.WalkLead >= 0f && a.WalkLead <= 30f));
        }

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
                    case "--smoke-door":
                        a.NetSmokeDoor = true;
                        a.ServerArgs.Add("--debug-door"); // дверь ставит сервер у точки появления (1-012)
                        break;
                    case "--smoke-bed":
                        a.NetSmokeBed = true;
                        a.ServerArgs.Add("--debug-bed"); // C-030: кровать ставит сервер у точки появления
                        break;
                    case "--smoke-bow":
                        a.NetSmokeBow = true;
                        break;
                    case "--smoke-bow-bar": // C-048: лучник целится в моба у ног — курсор над нижней панелью
                        a.NetSmokeBow = true;
                        a.NetSmokeBowBar = true;
                        break;
                    case "--auto-menu-click": // E4-1b: автоклик «В меню» на «Уведомлении» (живая проверка возврата в меню)
                        a.AutoMenuClick = true;
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
                    case "--single":
                    case "--host":
                        ServerMode mode = s == "--single" ? ServerMode.Single : ServerMode.Host;
                        if (a.Mode.HasValue && a.Mode != mode)
                        {
                            a.Error = "--single и --host вместе нельзя";
                        }

                        a.Mode = mode;
                        break;
                    case "--server-thread":
                        a.ServerThread = true;
                        break;
                    case "--world":
                    case "--server":
                    case "--server-arg":
                        i++;
                        if (string.IsNullOrEmpty(next))
                        {
                            a.Error = s + ": нет значения";
                        }
                        else if (s == "--world")
                        {
                            a.WorldDir = next;
                        }
                        else if (s == "--server")
                        {
                            a.ServerPath = next;
                        }
                        else
                        {
                            a.ServerArgs.Add(next!);
                        }

                        break;
                    case "--open-for-friends":
                        a.OpenForFriends = true;
                        break;
                    case "--open-address":
                        i++;
                        // Только IPv4-адрес: «все адреса» — умолчание игры, ключом его не просят (окно брандмауэра в прогонах).
                        if (!IPAddress.TryParse(next ?? string.Empty, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                        {
                            a.Error = $"--open-address: нужен адрес IPv4 (например 127.0.0.2), дано '{next}'";
                        }
                        else
                        {
                            a.OpenAddress = ip.ToString();
                        }

                        break;
                    case "--open-port":
                        i++;
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out a.OpenPort)
                            || a.OpenPort < 0 || a.OpenPort > 65_535)
                        {
                            a.Error = $"--open-port: нужен порт 0…65535, дано '{next}'";
                            a.OpenPort = 7777;
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
                    case "--shot-tile":
                        i++;
                        string[] xy = (next ?? string.Empty).Split(',');
                        if (xy.Length != 2
                            || !int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.ShotTileX)
                            || !int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.ShotTileY)
                            || a.ShotTileX < 0 || a.ShotTileY < 0)
                        {
                            a.Error = $"--shot-tile: нужно X,Y (тайлы ≥ 0), дано '{next}'";
                        }
                        else
                        {
                            a.HasShotTile = true;
                        }

                        break;
                    case "--shot-width":
                        i++;
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out a.ShotWidth)
                            || a.ShotWidth < 40 || a.ShotWidth > 96)
                        {
                            a.Error = $"--shot-width: нужно тайлов 40…96, дано '{next}'";
                            a.ShotWidth = 0;
                        }

                        break;
                    case "--shot-size":
                        i++;
                        string[] wh = (next ?? string.Empty).ToLowerInvariant().Split('x');
                        if (wh.Length != 2
                            || !int.TryParse(wh[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shotW)
                            || !int.TryParse(wh[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shotH)
                            || shotW < 64 || shotW > 7680 || shotH < 64 || shotH > 4320)
                        {
                            a.Error = $"--shot-size: нужно ШxВ в пикселях (например 1920x1080), дано '{next}'";
                        }
                        else
                        {
                            a.ShotPixelsX = shotW;
                            a.ShotPixelsY = shotH;
                        }

                        break;
                    case "--shot-walk":
                        i++;
                        if (!TryParseWalk(next, a))
                        {
                            a.Error = $"--shot-walk: нужно НАПРАВЛЕНИЕ:КАДРОВ:ИНТЕРВАЛ[:РАЗБЕГ] (e|w|n|s|ne|nw|se|sw|stay : 1…16 : секунды), дано '{next}'";
                            a.WalkFrames = 0;
                        }

                        break;
                    case "--shot-crop":
                        i++;
                        string[] crop = (next ?? string.Empty).ToLowerInvariant().Split('x');
                        if (crop.Length != 2
                            || !int.TryParse(crop[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.CropX)
                            || !int.TryParse(crop[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.CropY)
                            || a.CropX < 64 || a.CropY < 64)
                        {
                            a.Error = $"--shot-crop: нужно ШxВ в пикселях кадра (например 640x400), дано '{next}'";
                            a.CropX = 640;
                            a.CropY = 400;
                        }

                        break;
                    case "--paint-demo":
                        a.PaintDemo = true;
                        break;
                    case "--demo-piles":
                        a.DemoPiles = true;
                        break;
                    case "--ui-demo":
                        i++;
                        if (!UI.UiDemo.IsMode(next) && !UI.MenuUi.IsMenuDemo(next))
                        {
                            a.Error = $"--ui-demo: нужно bag, hotbar, craft, craft+bag, craft-hint, craft-chest, inv, inv-weapons, inv-buildings, inv-stone, build, build-open, chest, house, thin, half, turn, oldbeds, rmb-open, rmb-disarm, cursor-place, ruler-wall, ruler-floor, menu|menu-new|menu-host|menu-join|menu-notice или combat-hit|hurt|kill|death|respawn, дано '{next}'";
                        }
                        else
                        {
                            a.UiDemo = next;
                        }

                        break;
                    case "--home-demo":
                        i++;
                        if (!global::Orpheus.Client.PaintDemo.IsHomeMode(next))
                        {
                            a.Error = $"--home-demo: нужно overview, south, north, behind, door, camp, fence, torches, room или wall, дано '{next}'";
                        }
                        else
                        {
                            a.HomeDemo = next;
                        }

                        break;
                    case "--bed-demo":
                        i++;
                        if (next == null || next.Length < 1 || next[0] < '0' || next[0] > '3' || (next.Length > 1 && next.Substring(1) != "l"))
                        {
                            a.Error = $"--bed-demo: нужно 0…3 или 0l…3l, дано '{next}'";
                        }
                        else
                        {
                            a.BedDemoRot = next[0] - '0';
                            a.BedDemoLie = next.Length > 1;
                        }

                        break;
                    case "--wade-demo":
                        i++;
                        if (!global::Orpheus.Client.PaintDemo.IsWadeMode(next))
                        {
                            a.Error = $"--wade-demo: нужно water, bush или enemy, дано '{next}'";
                        }
                        else
                        {
                            a.WadeDemo = next;
                        }

                        break;
                    case "--fire-demo":
                        i++;
                        if (next != "in" && next != "south" && next != "north" && next != "east" && next != "west")
                        {
                            a.Error = $"--fire-demo: нужно in, south, north, east или west, дано '{next}'";
                        }
                        else
                        {
                            a.FireDemo = next;
                        }

                        break;
                    case "--paint-forest":
                        a.PaintForest = true;
                        break;
                    case "--day-tick":
                        i++;
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out a.DayTick)
                            || a.DayTick < 0 || a.DayTick >= 36_000)
                        {
                            a.Error = $"--day-tick: нужен такт суток 0…35999, дано '{next}'";
                            a.DayTick = 0;
                        }

                        break;
                    case "--shot-time":
                        // E1-7: время суток словом (день, сумерки, ночь, рассвет) или долей суток 0…1 — то же, что --day-tick.
                        i++;
                        if (next == null || !DayTintView.TryParseTime(next, out a.DayTick))
                        {
                            a.Error = $"--shot-time: нужно день|сумерки|ночь|рассвет (day|dusk|night|dawn) или доля суток 0…1, дано '{next}'";
                            a.DayTick = 0;
                        }

                        break;
                    case "--bench-render":
                        a.BenchRender = true;
                        break;
                    case "--gen":
                        i++;
                        if (next != "v1" && next != "stub")
                        {
                            a.Error = $"--gen: нужно v1 или stub, дано '{next}'";
                        }
                        else
                        {
                            a.Gen = next!;
                        }

                        break;
                    case "--anim-shots":
                        i++;
                        if (string.IsNullOrEmpty(next))
                        {
                            a.Error = "--anim-shots: нет папки";
                        }
                        else
                        {
                            a.AnimShotsDir = next;
                        }

                        break;
                    case "--anim-bench":
                        i++;
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out a.AnimBenchSeconds)
                            || a.AnimBenchSeconds < 1 || a.AnimBenchSeconds > 600)
                        {
                            a.Error = $"--anim-bench: нужно секунд 1…600, дано '{next}'";
                            a.AnimBenchSeconds = 0;
                        }

                        break;
                    case "--tile-bench":
                        i++;
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out a.TileBenchSeconds)
                            || a.TileBenchSeconds < 1 || a.TileBenchSeconds > 600)
                        {
                            a.Error = $"--tile-bench: нужно секунд 1…600, дано '{next}'";
                            a.TileBenchSeconds = 0;
                        }

                        break;
                }
            }

            if (a.ServerThread)
            {
                if (a.Mode == ServerMode.Host || a.OpenForFriends)
                {
                    a.Error = "--server-thread — только одиночная игра (без --host и --open-for-friends)";
                }

                a.Mode = ServerMode.Single; // сервер в потоке — та же одиночная игра, только без дочернего процесса
            }

            if (a.Mode.HasValue && a.Connect != null)
            {
                a.Error = "--connect и --single/--host вместе нельзя";
            }

            if (a.NetSmokeSeconds > 0 && a.Connect == null && !a.Mode.HasValue)
            {
                a.Error = "--net-smoke требует --connect, --single или --host";
            }

            if (a.OpenForFriends && !a.Mode.HasValue)
            {
                a.Error = "--open-for-friends — только с --single или --host";
            }

            // P-004: net-smoke меряет «правок 0 на чистом движении», а чистое — без чужого влияния (docs/07 2.6 п. 5).
            // Спавнер (1-009) на такте 1 ставит разбойника в 36–46 т от бота, тот идёт навстречу скриптовому бегу и
            // выталкивает персонажа (`02` 2.1, 68 unit за такт) — этого клиент предсказать не может. Свой сервер
            // net-smoke поэтому без спавнера; касание врага проверяют тесты ядра (PredictionTests, «EnemyTouch»).
            if (a.NetSmokeSeconds > 0 && a.Mode.HasValue && !a.ServerArgs.Contains(NoSpawnServerArg))
            {
                a.ServerArgs.Add(NoSpawnServerArg);
            }

            return a;
        }

        /// <summary>Ключ сервера «без спавнера и исчезновения» (1-009) — его получает свой сервер net-smoke (P-004).</summary>
        public const string NoSpawnServerArg = "--no-spawn";

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
