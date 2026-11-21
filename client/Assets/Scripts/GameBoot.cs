#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Hosting;
using Orpheus.WorldGen;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Tilemaps;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace Orpheus.Client
{
    /// <summary>
    /// Сцена Boot (карточка 0-010): мир из ядра по зерну, окно чанков, свой персонаж в локальном такте 30 Гц,
    /// камера с зумом, «плавающее начало». Один компонент ведёт кадр в явном порядке, без зависимости от порядка
    /// <c>Update</c> разных объектов:
    /// <list type="number">
    /// <item>кадр ввода (клавиатура / геймпад или скрипт smoke);</item>
    /// <item>такты ядра, созревшие за кадр;</item>
    /// <item>переезд начала, если персонаж ушёл от якоря дальше чанка или перешёл шов;</item>
    /// <item>окно чанков по позиции ядра;</item>
    /// <item>персонаж — интерполяция между тактами; камера — за ним; зум;</item>
    /// <item>проверки smoke / снимок экрана.</item>
    /// </list>
    /// Поля с тайлами и объектами сцены заполняет <c>Orpheus.Editor.ProjectSetup</c>.
    /// </summary>
    public sealed class GameBoot : MonoBehaviour
    {
        public TileBase TileMeadow = null!;
        public TileBase TileStone = null!;
        public TileBase TileTree = null!;
        public TileBase TileRock = null!;
        public Transform Grid = null!;
        public Transform Character = null!;
        public Camera Camera = null!;
        public Text? DebugText;

        /// <summary>Обзор по умолчанию, тайлов по ширине на экране 16:9 (план, шаг 1.1): 64 × 36, на 1080p — 30 px на тайл.</summary>
        public const float DefaultViewTiles = 64f;

        /// <summary>Самый ближний обзор колесом: 40 × 22,5 тайла (прежний эталон docs/00, 5.1).</summary>
        public const float NearViewTiles = 40f;

        /// <summary>Самый дальний обзор колесом: 96 × 54 тайла. Окно чанков его покрывает: от персонажа до края блока входа
        /// 6 × 4 чанка — не меньше 80 тайлов по X и 48 по Y (<c>ChunkWindow</c>), нужно 48 и 27 + сдвиг камеры 0,35.</summary>
        public const float FarViewTiles = 96f;

        /// <summary>Эталон экрана: 64 × 36 тайлов при зуме ×1,0 — половина высоты 18.</summary>
        public const float ReferenceHalfHeight = DefaultViewTiles * 9f / 16f / 2f;

        /// <summary>Зум — во сколько раз ближе эталона: ×0,667 (96 тайлов) … ×1,6 (40 тайлов).</summary>
        public const float MinZoom = DefaultViewTiles / FarViewTiles;
        public const float MaxZoom = DefaultViewTiles / NearViewTiles;

        /// <summary>Шаг зума на щелчок колеса.</summary>
        public const float ZoomStep = 1.1f;

        /// <summary>Камера смотрит чуть выше опоры персонажа — в середину спрайта 1 × 1,5.</summary>
        public static readonly Vector3 CameraOffset = new Vector3(0f, 0.35f, -10f);

        public ClientArgs Args { get; private set; } = new ClientArgs();
        public WorldModel World { get; private set; } = null!;
        public MapShape Map { get; private set; }
        public Simulation Rules { get; private set; } = null!;
        public FloatingOrigin Origin { get; private set; } = null!;
        public WorldView View { get; private set; } = null!;
        public LocalCharacter Player { get; private set; } = null!;
        public float Zoom { get; private set; } = 1f;
        public double GenerationMs { get; private set; }

        /// <summary>Сетевой режим (0-012), в том числе одиночная и хост (0-024); null — офлайн (локальный такт).</summary>
        public NetGame? Net { get; private set; }

        // Действия ввода — для сетевого режима (тот же клавишно-геймпадный ввод).
        public InputAction Move => _move;
        public InputAction ZoomAxis => _zoom;
        public InputAction ZoomIn => _zoomIn;
        public InputAction ZoomOut => _zoomOut;
        public InputAction Dodge => _dodge;

        /// <summary>Ввод скриптом (smoke): если задан, клавиатура и геймпад не читаются.</summary>
        public InputFrame? ScriptedInput { get; set; }

        /// <summary>Тактов за последний кадр.</summary>
        public int TicksThisFrame { get; private set; }

        InputAction _move = null!;
        InputAction _zoom = null!;
        InputAction _zoomIn = null!;
        InputAction _zoomOut = null!;
        InputAction _dodge = null!;
        SmokeRun? _smoke;
        ScreenshotRun? _screenshot;
        Anim.ActorRig? _heroRig;        // K-001: ходьба и дыхание своего персонажа офлайн
        WadeDemoEnemy? _wadeEnemy;      // C-018: враг-болванка в воде (--wade-demo enemy)
        TileCoord _bedDemoAnchor;       // C-030: кровать сцены --bed-demo
        float _bedDemoAge;
        SpriteRenderer? _bedDemoShade;  // C-030: затемнение «лежит» в сцене снимков
        Anim.AnimShowcase? _showcase;   // K-001: показ анимации и замер (--anim-shots, --anim-bench)
        TileBenchRun? _tileBench;       // 1-022: стенд «48 Tilemap» (--tile-bench): бег через шов + поток правок, замеры
        DayTintView? _dayTint;          // E1-8: тон суток (ночь, сумерки, рассвет; свет факела и костра)
        int _hudFrame;
        PileView? _piles;               // E2-1: кучи на земле без сервера (--demo-piles, сцена снимков)
        HearthMarkerView? _hearth;      // E3-6b: маркер Очага на тайле центра (спавн) — офлайн-мир и снимки
        float _hearthClock;             // E3-6b: часы пульса маркера (в снимках кадр фиксированный — детерминированно)
        CampfireSparkFx? _fireSparks;   // C-049: искры у поставленных костров (часы те же, что у Очага)
        UI.UiDemo? _uiDemo;             // E2-2: интерфейс без сервера (--ui-demo, сцены снимков)

        void Awake()
        {
            Args = ClientArgs.Parse(Environment.GetCommandLineArgs());
            if (_startInMenu)
            {
                // E4-1: сцена перезагружена возвратом «в меню»: аргументы прошлого захода сброшены,
                // стартуем с чистого листа — и ниже открываем меню.
                // E4-1b: «--auto-menu-click» — настройка запуска процесса, не подключения: автоклик по
                // «В меню» проверяем как раз после такого возврата, поэтому флаг переживает сброс.
                bool autoMenuClick = Args.AutoMenuClick;
                _startInMenu = false;
                Args = new ClientArgs();
                Args.AutoMenuClick = autoMenuClick;
            }
            bool netMode = Args.Connect != null || Args.Mode.HasValue;
            if (Args.Error != null && !netMode)
            {
                Debug.LogWarning("[boot] аргументы: " + Args.Error + " — взято значение по умолчанию");
            }

            CreateInput();
            WorldFx.Init(Camera, Character); // E1-7: слой теней, персонаж за кроной (до первого WorldView)

            // E4-1: главный экран. Если это не скриптовый прогон (снимки, тесты, стенды) и не
            // сетевой запуск с аргументами, игра стартует в меню: «Новый мир», «Продолжить»,
            // «Открыть для друзей», «Войти по адресу». Смерть и обрыв связи тоже ведут сюда.
            if (!netMode && !ScriptedRun())
            {
                OpenMenu();
                return;
            }

            // Сетевой режим (0-012): мир присылает сервер, поэтому здесь только связь; окно — сразу с HUD-текстом.
            // Одиночная и хост (0-024) — тот же сетевой режим: сервер — дочерний процесс, вход через 127.0.0.1 (07, 2.3).
            if (netMode)
            {
                if (Args.Error != null)
                {
                    // Сетевой режим с ошибкой ключа (например, имя длиннее 32 байт UTF-8) — не входим вовсе (0-017).
                    Debug.LogError("[boot] аргументы: " + Args.Error + " Ключи: " + ClientArgs.Usage);
                    Application.Quit(1);
                    return;
                }

                ShowDebugLabels(Args.Hud); // надпись «TEMP ASSETS» и строка отладки — только с --hud, и в сетевом/одиночном режиме тоже
                Net = new NetGame(this, Args);
                Net.StartConnect();
                return;
            }

            ShowDebugLabels(Args.Hud);
            Map = MapShape.World(Args.Size);
            var sw = Stopwatch.StartNew();
            // Тот же генератор, что у сервера по умолчанию (план, шаг 0.5): снимок без сервера показывает тот же мир.
            // Заглушка 0-007 — только явным ключом --gen stub (--smoke и --tile-bench сверяются с ней).
            World = WorldGenModule.ForKey(Args.Gen).Generate(Map, Args.Seed);
            GenerationMs = sw.Elapsed.TotalMilliseconds;
            Debug.Log($"[boot] мир {Args.Size}² зерно 0x{Args.Seed:X} генератор {Args.Gen} (v{World.GeneratorVersion}): {GenerationMs:0} мс");

            if (Args.PaintDemo)
            {
                PaintDemo.Apply(World, Map); // E1-4: тестовый узор у старта — только для снимков
            }

            if (Args.PaintForest)
            {
                PaintDemo.ApplyBenchForest(World, Map); // E1-7: плотный лес для замера
            }

            // Правила проходимости — те же, что в такте сервера (Simulation.IsBlocked): пол и объекты, `05` 1.3.
            Rules = new Simulation(World, maxEntities: 1, maxEditsPerTick: 1, reserveBuildMaps: 0);

            WorldPos spawn = TileCenter(new TileCoord(Args.Size / 2, Args.Size / 2)); // стартовая зона генератора
            Player = new LocalCharacter(Map, Rules.Solid, spawn);
            Origin = new FloatingOrigin(Map, Torus.ChunkOf(Map, spawn));
            View = new WorldView(World, Origin, Grid, TileMeadow, TileStone, TileTree, TileRock);
            View.UpdateWindow(Player.Position);
            PlaceCharacterAndCamera();
            _dayTint = new DayTintView(Camera, World, Origin); // E1-8: тон суток (офлайн-часы — Simulation.WorldTime + --day-tick)
            _hearth = new HearthMarkerView(new GameObject("Hearth").transform, Map, Args.Size / 2, Args.Size / 2); // E3-6b: Очаг = центр, как спавн у ядра
            _fireSparks = new CampfireSparkFx(new GameObject("FireSparks").transform, World, Map); // C-049: искры у костров

            if (Args.Smoke)
            {
                _smoke = new SmokeRun(this);
            }
            else if (Args.ScreenshotPath != null)
            {
                _screenshot = new ScreenshotRun(this, Args.ScreenshotPath);
            }
            else if (Args.AnimShotsDir != null || Args.AnimBenchSeconds > 0)
            {
                _showcase = new Anim.AnimShowcase(this, Args.AnimShotsDir, Args.AnimBenchSeconds);
            }
            else if (Args.TileBenchSeconds > 0)
            {
                _tileBench = new TileBenchRun(this);
                Debug.Log($"[boot] стенд tile-bench: зум x{MinZoom:0.00} ({FarViewTiles:0} тайлов), бег через шов + правки, {Args.TileBenchSeconds} с (1-022)");
            }

            _heroRig = new Anim.ActorRig(Character);
            if (Args.DemoPiles)
            {
                // E2-1: кучи на земле для снимка без сервера (после телепорта снимка — вокруг точки сцены).
                _piles = new PileView(new GameObject("Piles").transform);
                _piles.AddDemo(Map, Player.Position, t => Rules.IsBlocked(Map, t));
            }

            if (Args.HomeDemo != null)
            {
                // E2-17: дом «с высотой» для снимков — постройки вокруг персонажа, персонаж в нужном месте сцены.
                TileCoord at = Torus.TileOf(Map, Player.Position);
                PaintDemo.Home(World, Map, at.X, at.Y, Args.HomeDemo!);
                View.Clear();
                View.UpdateWindow(Player.Position);
            }

            if (Args.BedDemoRot >= 0)
            {
                // C-030: спальня с двуспальной кроватью; с «l» — персонаж лежит в ней.
                TileCoord at = Torus.TileOf(Map, Player.Position);
                PaintDemo.Bed(World, Map, at.X, at.Y, Args.BedDemoRot);
                _bedDemoAnchor = Torus.Wrap(Map, PaintDemo.BedDemoAnchor(at.X, at.Y));
                View.Clear();
                View.UpdateWindow(Player.Position);
                if (Args.BedDemoLie)
                {
                    var tex = Texture2D.whiteTexture;
                    var go = new GameObject("RestShade");
                    go.transform.SetParent(Camera.transform, false);
                    go.transform.localPosition = new Vector3(0f, 0f, 5f); // перед камерой, как DayTintView
                    go.transform.localScale = new Vector3(600f, 600f, 1f);
                    _bedDemoShade = go.AddComponent<SpriteRenderer>();
                    _bedDemoShade.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
                    _bedDemoShade.sortingOrder = WorldView.TintOrder + 5;
                }
            }

            if (Args.WadeDemo != null)
            {
                // C-018: пруд и кусты вокруг персонажа; в сцене enemy — враг-болванка в воде.
                TileCoord at = Torus.TileOf(Map, Player.Position);
                PaintDemo.Wade(World, Map, at.X, at.Y, Args.WadeDemo!);
                View.Clear();
                View.UpdateWindow(Player.Position);
                if (PaintDemo.WadeDemoEnemyTile(Args.WadeDemo!, at.X, at.Y, out TileCoord et))
                {
                    Sprite heroSprite = Character.GetChild(0).GetComponent<SpriteRenderer>().sprite;
                    _wadeEnemy = new WadeDemoEnemy(new GameObject("WadeDemo").transform, heroSprite, Origin, TileCenter(et));
                }
            }

            if (Args.FireDemo != null)
            {
                // C-049: костёр в пяти тайлах севернее старта, персонаж — на заданной стороне огня
                // (in — в кольце камней, на огне). Снимки проверяют «низкую полосу» строки (LowRowOrderAt):
                // стоящий у костра — перед огнём, севернее середины — за ним. Факел восточнее — проверка sway.
                TileCoord at = Torus.TileOf(Map, Player.Position);
                int fx = at.X, fy = at.Y - 5; // Y мира вниз: меньше Y — севернее (выше на экране)
                PaintDemo.Put(World, Map, fx, fy, 0);
                PaintDemo.Put(World, Map, fx, fy - 1, 0);
                PaintDemo.Put(World, Map, fx, fy + 1, 0);
                PaintDemo.Put(World, Map, fx - 1, fy, 0);
                PaintDemo.Put(World, Map, fx + 1, fy, 0);
                PaintDemo.Put(World, Map, fx, fy, ObjectId.Campfire);
                PaintDemo.Put(World, Map, fx + 2, fy, ObjectId.Torch);
                TileCoord hero = Args.FireDemo! switch
                {
                    "south" => new TileCoord(fx, fy + 1),
                    "north" => new TileCoord(fx, fy - 1),
                    "east" => new TileCoord(fx + 1, fy),
                    "west" => new TileCoord(fx - 1, fy),
                    _ => new TileCoord(fx, fy), // "in"
                };
                Teleport(TileCenter(Torus.Wrap(Map, hero)));
                View.Clear();
                View.UpdateWindow(Player.Position);
            }

            if (Args.UiDemo != null)
            {
                if (Args.ScreenshotPath != null)
                {
                    UI.UiRoot.FixedFrame = new Vector2Int(Args.ShotPixelsX, Args.ShotPixelsY); // холст — сразу в размере снимка
                }

                if (UI.MenuUi.IsMenuDemo(Args.UiDemo))
                {
                    // E4-1: снимки меню: мир построен как обычно (кадр снимка идёт по своему сценарию),
                    // сверху — витрина меню; обычный UiDemo для этих режимов не создаётся.
                    // E4-1b: onMenu передаём и здесь — витрина menu-notice-click жмёт «В меню» настоящей мышью.
                    _menu = new UI.MenuUi(Camera, OnMenuStart, OnMenuNoticeBack);
                    _menu.ShowDemo(Args.UiDemo!);
                    return;
                }

                if (UI.UiDemo.IsCraft(Args.UiDemo))
                {
                    // E2-4: окно крафта показывается у верстака — ставим его в двух тайлах восточнее персонажа, костёр — западнее.
                    TileCoord at = Torus.TileOf(Map, Player.Position);
                    PaintDemo.Put(World, Map, at.X + 2, at.Y, ObjectId.Workbench);
                    PaintDemo.Put(World, Map, at.X - 6, at.Y + 1, ObjectId.Campfire);
                    PaintDemo.Put(World, Map, at.X + 4, at.Y - 1, ObjectId.Torch);
                    View.Clear();
                    View.UpdateWindow(Player.Position);
                }

                if (UI.UiDemo.IsBuild(Args.UiDemo))
                {
                    // E2-5: сцены режима стройки, сундука и дома — постройки вокруг персонажа (PaintDemo.House).
                    TileCoord at = Torus.TileOf(Map, Player.Position);
                    PaintDemo.House(World, Map, at.X, at.Y, Args.UiDemo!);
                    View.Clear();
                    View.UpdateWindow(Player.Position);
                }

                _uiDemo = new UI.UiDemo(Camera, Args.UiDemo, World);
                if (UI.UiDemo.IsBuild(Args.UiDemo))
                {
                    _uiDemo.BindWorld(Origin, transform);
                }

                if (UI.UiDemo.IsCombat(Args.UiDemo))
                {
                    // E3-1: враги-болванки вокруг персонажа, цифры урона, полоски, экран смерти.
                    Sprite heroSprite = Character.GetChild(0).GetComponent<SpriteRenderer>().sprite;
                    _uiDemo.BindCombat(Origin, new GameObject("CombatDemo").transform, heroSprite, _heroRig);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  E4-1: главное меню — вход в игру из меню и возврат в меню без рестарта процесса
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>Меню живо, пока открыт этот экран; кадры игры тогда не идут.</summary>
        UI.MenuUi? _menu;

        /// <summary>Стартовать в меню после перезагрузки сцены (static: перезагрузка сцены убивает этот компонент).</summary>
        static bool _startInMenu;

        /// <summary>Экран «Уведомление» с текстом причины для кадра после перезагрузки сцены.</summary>
        static string? _pendingNotice;

        /// <summary>
        /// Скриптовый прогон: снимки, тесты, стенды и демо рисуются без игрока в меню,
        /// поэтому они стартуют сразу, как раньше.
        /// </summary>
        bool ScriptedRun() =>
            Args.Smoke || Args.ScreenshotPath != null || Args.UiDemo != null
            || Args.AnimShotsDir != null || Args.AnimBenchSeconds > 0 || Args.TileBenchSeconds > 0
            || Args.PaintDemo || Args.PaintForest || Args.DemoPiles || Args.HomeDemo != null
            || Args.BedDemoRot >= 0 || Args.WadeDemo != null || Args.FireDemo != null || Args.WalkFrames != 0
            || Args.NetSmokeSeconds > 0; // E4-1b: net-smoke: обрыв остаётся быстрым «[smoke] FAILED»+выход (NetSmokeCheck), не меню

        /// <summary>Открыть меню (при старте и после каждой перезагрузки сцены).</summary>
        void OpenMenu()
        {
            ShowDebugLabels(false);
            _startInMenu = false;
            _menu = new UI.MenuUi(Camera, OnMenuStart, OnMenuNoticeBack);
            string? notice = _pendingNotice;
            _pendingNotice = null;
            if (notice != null)
            {
                _menu.ShowNotice(notice);
                // E4-1b: --auto-menu-click — живая проверка «обрыв → уведомление → «В меню» → меню» без рук:
                // авто-клик мышью (тот же путь, что витрина menu-notice-click).
                if (Args.AutoMenuClick)
                {
                    // На скрытом столе окно меню не в фокусе (после перезагрузки сцены D3D-контекст теряется),
                    // и Unity ставит плеер на паузу — кадры меню не идут и авто-клик не досчитывается.
                    // Под этим тестовым флагом играем в фоне всегда; для обычной игры флаг не ставится.
                    Application.runInBackground = true;
                    _menu.ScheduleNoticeClick();
                }

                Debug.Log($"[boot] E4-1b: меню с уведомлением, авто-клик = {Args.AutoMenuClick}");
            }

            Debug.Log("[boot] E4-1: главное меню открыто");
        }

        /// <summary>
        /// E4-1b: «В меню» на экране «Уведомление» — возврат на главный экран меню без перезагрузки сцены
        /// (меню уже открыт, обрыв за ним). Тот же путь, что приводит со смерти.
        /// </summary>
        void OnMenuNoticeBack()
        {
            Debug.Log("[boot] E4-1b: «В меню» на уведомлении нажато — главный экран меню");
            _menu?.ShowMainFromNotice();
        }

        /// <summary>
        /// Клик по кнопкам меню: собрать аргументы захода (тот же путь, что ключи) и войти в игру.
        /// «Новый мир» и «для друзей» — свежая папка Worlds/world-N и посев с экрана; «Продолжить» —
        /// самая свежая папка с сохранением; «войти по адресу» — только клиент, сервера здесь нет.
        /// </summary>
        void OnMenuStart(UI.MenuUi.Start s)
        {
            string? worldDir = null, connect = null;
            bool friends = false;
            ulong seed = 0;
            switch (s.Kind)
            {
                case UI.MenuUi.Kind.NewWorld:
                    worldDir = Path.Combine(UI.MenuUi.WorldsRoot, UI.MenuUi.NextWorldName());
                    seed = (ulong)s.Seed;
                    break;
                case UI.MenuUi.Kind.Continue:
                    worldDir = Path.Combine(UI.MenuUi.WorldsRoot, UI.MenuUi.LatestWorldName() ?? UI.MenuUi.NextWorldName());
                    break;
                case UI.MenuUi.Kind.FriendsNew:
                    worldDir = Path.Combine(UI.MenuUi.WorldsRoot, UI.MenuUi.NextWorldName());
                    seed = (ulong)s.Seed;
                    friends = true;
                    break;
                case UI.MenuUi.Kind.FriendsContinue:
                    worldDir = Path.Combine(UI.MenuUi.WorldsRoot, UI.MenuUi.LatestWorldName() ?? UI.MenuUi.NextWorldName());
                    friends = true;
                    break;
                case UI.MenuUi.Kind.Join:
                    connect = s.Address;
                    break;
            }

            Debug.Log($"[boot] E4-1: меню начинает заход {s.Kind}: мир {worldDir ?? "-"} адрес {connect ?? "-"}");
            StartNet(worldDir: worldDir, hostForFriends: friends, connect: connect, seed: seed);
        }

        /// <summary>
        /// Запуск игры из меню — тот же путь, что ключи: одиночный мир (LocalServer-поток),
        /// «для друзей» (LocalServer + UDP-подстёжка, как --single --open-for-friends), вход по адресу.
        /// </summary>
        void StartNet(string? worldDir = null, bool hostForFriends = false, string? connect = null, ulong seed = 0)
        {
            _menu?.Dispose();
            _menu = null;
            Args.FromMenu = true;
            Args.Mode = connect == null ? ServerMode.Single : (ServerMode?)null;
            Args.WorldDir = worldDir;
            if (seed != 0)
            {
                Args.Seed = seed;
            }

            Args.OpenForFriends = hostForFriends;
            Args.OpenAddress = hostForFriends ? UI.MenuUi.FriendsAddress : null;
            Args.OpenPort = hostForFriends ? UI.MenuUi.FriendsPort : Args.OpenPort;
            Args.Connect = connect;
            ShowDebugLabels(Args.Hud);
            Net = new NetGame(this, Args);
            Net.StartConnect();
        }

        /// <summary>
        /// Возврат в меню без рестарта процесса: экраны в буфере, сервер пишется по Stop
        /// (OnDestroy → Net.Shutdown), сцена перезагружается — и Awake открывает меню.
        /// </summary>
        public void GoToMenu(string? notice)
        {
            if (_startInMenu)
            {
                return; // перезагрузка сцены уже идёт
            }
            Debug.Log("[boot] E4-1: возврат в меню" + (notice != null ? ": " + notice : ""));
            _startInMenu = true;
            _pendingNotice = notice;
            UnityEngine.SceneManagement.SceneManager.LoadScene(gameObject.scene.name);
        }

        void OnDestroy()
        {
            _move?.Dispose();
            _zoom?.Dispose();
            _zoomIn?.Dispose();
            _dodge?.Dispose();
            _zoomOut?.Dispose();
            _smoke?.Dispose();
            _tileBench?.Dispose();
            Net?.Shutdown();
        }

        /// <summary>Центр тайла как позиция существа.</summary>
        public WorldPos TileCenter(TileCoord t) =>
            Torus.Offset(Map, Torus.CornerOf(t), new Int2(Torus.SubTilesPerTile / 2, Torus.SubTilesPerTile / 2));

        /// <summary>Поставить персонажа в точку без пути: окно и якорь — заново (smoke, снимок).</summary>
        public void Teleport(WorldPos p)
        {
            Player.Teleport(p);
            View.Clear();
            Origin.Rebase(Torus.ChunkOf(Map, Player.Position), seam: false);
            View.UpdateWindow(Player.Position);
            PlaceCharacterAndCamera();
        }

        public void SetZoom(float zoom)
        {
            Zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            Camera.orthographicSize = ReferenceHalfHeight / Zoom;
        }

        /// <summary>Обзор в тайлах по ширине экрана 16:9: 40…96.</summary>
        public void SetViewTiles(float tiles) => SetZoom(DefaultViewTiles / tiles);

        /// <summary>Обзор сейчас, тайлов по ширине экрана 16:9.</summary>
        public float ViewTiles => DefaultViewTiles / Zoom;

        /// <summary>
        /// Отладочные надписи (план, шаг 1.1): «Orpheus — TEMP ASSETS» и строка состояния — только с ключом <c>--hud</c>.
        /// Пометка заглушек на самих картинках (пурпурный уголок) остаётся всегда.
        /// </summary>
        void ShowDebugLabels(bool on)
        {
            if (DebugText == null)
            {
                return;
            }

            Transform? temp = DebugText.transform.parent != null ? DebugText.transform.parent.Find("TempLabel") : null;
            if (temp != null)
            {
                temp.gameObject.SetActive(on);
            }

            ShowDebugText(on);
        }

        /// <summary>Показать или скрыть строку состояния (сеть без <c>--hud</c>: видна только до входа в игру).</summary>
        public void ShowDebugText(bool on)
        {
            if (DebugText != null && DebugText.enabled != on)
            {
                DebugText.enabled = on;
            }
        }

        void CreateInput()
        {
            // Схема клавиатура + геймпад (docs/07, 1.2: Input System). Переназначение клавиш — отдельная задача.
            _move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            _move.AddBinding("<Gamepad>/leftStick");
            _move.AddBinding("<Gamepad>/dpad");
            _zoom = new InputAction("Zoom", InputActionType.PassThrough, "<Mouse>/scroll/y");
            _zoomIn = new InputAction("ZoomIn", InputActionType.Button, "<Gamepad>/rightShoulder");
            _zoomOut = new InputAction("ZoomOut", InputActionType.Button, "<Gamepad>/leftShoulder");

            // Уворот (слово Автора 08.11, 0-034): «что-то вроде Shift или X + стрелка» — Shift (любой) или X вместе с направлением
            // движения (WASD, стрелки, стик); на геймпаде — правая кнопка ромба (B / ○). Без направления — ничего (NetGame).
            // Все привязки — здесь, одним местом; переназначение — потом.
            _dodge = new InputAction("Dodge", InputActionType.Button);
            _dodge.AddBinding("<Keyboard>/leftShift");
            _dodge.AddBinding("<Keyboard>/rightShift");
            _dodge.AddBinding("<Keyboard>/x");
            _dodge.AddBinding("<Gamepad>/buttonEast");
            _move.Enable();
            _zoom.Enable();
            _zoomIn.Enable();
            _zoomOut.Enable();
            _dodge.Enable();
            SetZoom(1f); // обзор по умолчанию — 64 × 36 тайлов
        }

        /// <summary>Вектор ввода Unity (Y вверх) → направление ядра (Y вниз): ближайшее из 8 (docs/00, 5.1).</summary>
        public static InputFrame ToFrame(Vector2 v)
        {
            const float deadZone = 0.2f;
            if (v.sqrMagnitude < deadZone * deadZone)
            {
                return InputFrame.None;
            }

            return new InputFrame(Input8.FromStick(Mathf.RoundToInt(v.x * 1024f), Mathf.RoundToInt(-v.y * 1024f)));
        }

        void Update()
        {
            // E4-1: в меню весь кадр принадлежит меню; фатальная сетевая авария — возврат в меню.
            if (_menu != null)
            {
                _menu.Frame();
                _screenshot?.AfterFrame(); // снимки меню (--ui-demo menu*) снимаются на своём кадре
                return;
            }
            // E4-1b: гейт по скриптовому режиму (замечание 2 П E4-1): в скриптовых прогонах обрыв ведёт себя
            // как раньше — NetSmokeCheck даёт быстрый «[smoke] FAILED» и выход (код 1), а не меню до таймаута гейта.
            if (Net != null && Net.Fatal && !ScriptedRun())
            {
                GoToMenu(Net.StatusText);
                return;
            }

            if (Net != null)
            {
                Net.Frame();
                return;
            }

            _smoke?.BeforeFrame();
            _tileBench?.BeforeFrame();

            // 1. Кадр ввода.
            Player.Input = ScriptedInput ?? ToFrame(_move.ReadValue<Vector2>());
            if (ScriptedInput == null)
            {
                float scroll = _zoom.ReadValue<float>();
                if (scroll > 0.01f) SetZoom(Zoom * ZoomStep);
                else if (scroll < -0.01f) SetZoom(Zoom / ZoomStep);
                if (_zoomIn.WasPressedThisFrame()) SetZoom(Zoom * ZoomStep);
                if (_zoomOut.WasPressedThisFrame()) SetZoom(Zoom / ZoomStep);
            }

            // 2. Такты ядра.
            TicksThisFrame = Player.Advance(Time.deltaTime);

            // 3. Переезд начала: всё, что в координатах Unity, пересчитывается от нового якоря в этом же кадре.
            if (Origin.NeedsRebase(Torus.ChunkOf(Map, Player.Position), out bool seam))
            {
                Vector3 shift = Origin.Rebase(Torus.ChunkOf(Map, Player.Position), seam);
                Camera.transform.position += shift;
            }

            // 4. Окно чанков (расставляет узлы от текущего якоря).
            View.UpdateWindow(Player.Position);

            // 5. Персонаж и камера.
            PlaceCharacterAndCamera();
            if (Args.BedDemoLie && _heroRig != null)
            {
                // C-030: лежит в кровати сцены — поза RestAnim (как у NetAnimation), затемнение — RestAnim.Dim.
                _bedDemoAge += Time.deltaTime;
                Vector3 center = Origin.ToUnity(BuildLayers.CenterAt(World, _bedDemoAnchor));
                Vector3 root = Character.position;
                float sortY = Origin.ToUnity(TileCenter(_bedDemoAnchor)).y - 0.5f + ObjectTable.BaseLift - 0.25f;
                _heroRig.AnimateRest(Time.deltaTime, Math.Max(_bedDemoAge, Anim.RestAnim.LieSec), ObjectId.RotationOf(World.GetObject(_bedDemoAnchor)),
                    center.x - root.x, center.y - root.y, sleeper: 0, sortY);
                if (_bedDemoShade != null)
                {
                    _bedDemoShade.color = new Color(0.02f, 0.03f, 0.09f, Anim.RestAnim.DimMax);
                }
            }
            else
            {
                _heroRig?.AnimateWalk(Time.deltaTime);
            }

            _wadeEnemy?.Frame(Time.deltaTime);
            _piles?.Frame(Origin, Time.deltaTime);
            _hearthClock += Time.deltaTime; // E3-6b
            _hearth?.Frame(View, Origin, (long)(_hearthClock * 1_000_000.0)); // E3-6b: маркер Очага, пока его чанк в окне
            _fireSparks?.Frame(View, Origin, _hearthClock); // C-049: искры у поставленных костров в окне
            _dayTint?.Update(DayClock.Wrap(Rules.WorldTime + Args.DayTick)); // E1-8: фон тона — часы мира (Simulation) + сдвиг ключа

            _uiDemo?.Frame(Player.Position);
            UpdateHud();
            _smoke?.AfterFrame();
            _screenshot?.AfterFrame();
            _showcase?.AfterFrame();
            _tileBench?.AfterFrame();
        }

        /// <summary>Персонаж — в интерполированной позиции от якоря; камера — над ним.</summary>
        public void PlaceCharacterAndCamera()
        {
            Player.Interpolated(out WorldPos from, out float dx, out float dy);
            Vector3 p = Origin.ToUnity(from) + new Vector3(dx / FloatingOrigin.UnitsPerTile, -dy / FloatingOrigin.UnitsPerTile, 0f);
            Character.position = p;
            Camera.transform.position = p + CameraOffset;
            WorldFx.SetHero(p);
        }

        void UpdateHud()
        {
            if (DebugText == null || !Args.Hud || ++_hudFrame % 10 != 0)
            {
                return;
            }

            TileCoord t = Torus.TileOf(Map, Player.Position);
            ChunkCoord c = Torus.ChunkOf(Map, t);
            DebugText.text = $"seed 0x{Args.Seed:X}  {Map.Width}x{Map.Height}  tile {t.X},{t.Y}  chunk {c.X},{c.Y}  " +
                             $"window {View.Count}/{ChunkWindow.MaxChunks}  anchor {Origin.Anchor.X},{Origin.Anchor.Y}  " +
                             $"zoom x{Zoom:0.00} ({ViewTiles:0} т)  {1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f):0} fps";
        }
    }
}
