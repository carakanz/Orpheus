#nullable enable
using System;
using System.Diagnostics;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
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
        Anim.AnimShowcase? _showcase;   // K-001: показ анимации и замер (--anim-shots, --anim-bench)
        TileBenchRun? _tileBench;       // 1-022: стенд «48 Tilemap» (--tile-bench): бег через шов + поток правок, замеры
        int _hudFrame;

        void Awake()
        {
            Args = ClientArgs.Parse(Environment.GetCommandLineArgs());
            bool netMode = Args.Connect != null || Args.Mode.HasValue;
            if (Args.Error != null && !netMode)
            {
                Debug.LogWarning("[boot] аргументы: " + Args.Error + " — взято значение по умолчанию");
            }

            CreateInput();

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

                ShowDebugLabels(Args.Hud);
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

            // Правила проходимости — те же, что в такте сервера (Simulation.IsBlocked): пол и объекты, `05` 1.3.
            Rules = new Simulation(World, maxEntities: 1, maxEditsPerTick: 1, reserveBuildMaps: 0);

            WorldPos spawn = TileCenter(new TileCoord(Args.Size / 2, Args.Size / 2)); // стартовая зона генератора
            Player = new LocalCharacter(Map, Rules.Solid, spawn);
            Origin = new FloatingOrigin(Map, Torus.ChunkOf(Map, spawn));
            View = new WorldView(World, Origin, Grid, TileMeadow, TileStone, TileTree, TileRock);
            View.UpdateWindow(Player.Position);
            PlaceCharacterAndCamera();

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
            _heroRig?.AnimateWalk(Time.deltaTime);

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
