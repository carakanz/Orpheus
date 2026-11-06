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

        /// <summary>Эталон экрана: 40 × 22,5 тайла при зуме ×1,0 (docs/00, 5.1) — половина высоты 11,25.</summary>
        public const float ReferenceHalfHeight = 11.25f;
        public const float MinZoom = 0.6f;
        public const float MaxZoom = 1.0f;

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

        /// <summary>Сетевой режим (0-012); null — офлайн (локальный такт).</summary>
        public NetGame? Net { get; private set; }

        // Действия ввода — для сетевого режима (тот же клавишно-геймпадный ввод).
        public InputAction Move => _move;
        public InputAction ZoomAxis => _zoom;
        public InputAction ZoomIn => _zoomIn;
        public InputAction ZoomOut => _zoomOut;

        /// <summary>Ввод скриптом (smoke): если задан, клавиатура и геймпад не читаются.</summary>
        public InputFrame? ScriptedInput { get; set; }

        /// <summary>Тактов за последний кадр.</summary>
        public int TicksThisFrame { get; private set; }

        InputAction _move = null!;
        InputAction _zoom = null!;
        InputAction _zoomIn = null!;
        InputAction _zoomOut = null!;
        SmokeRun? _smoke;
        ScreenshotRun? _screenshot;
        int _hudFrame;

        void Awake()
        {
            Args = ClientArgs.Parse(Environment.GetCommandLineArgs());
            if (Args.Error != null)
            {
                Debug.LogWarning("[boot] аргументы: " + Args.Error + " — взято значение по умолчанию");
            }

            CreateInput();

            // Сетевой режим (0-012): мир присылает сервер, поэтому здесь только связь; окно — сразу с HUD-текстом.
            if (Args.Connect != null)
            {
                if (Args.Error != null)
                {
                    Application.Quit(1);
                    return;
                }

                Net = new NetGame(this, Args);
                Net.StartConnect();
                return;
            }

            Map = MapShape.World(Args.Size);
            var sw = Stopwatch.StartNew();
            World = new StubGenerator().Generate(Map, Args.Seed, out GenStats stats);
            GenerationMs = sw.Elapsed.TotalMilliseconds;
            Debug.Log($"[boot] мир {Args.Size}² зерно 0x{Args.Seed:X} генератор v{World.GeneratorVersion}: {GenerationMs:0} мс; " +
                      $"деревьев {stats.Trees}, скал-объектов {stats.Rocks}");

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
        }

        void OnDestroy()
        {
            _move?.Dispose();
            _zoom?.Dispose();
            _zoomIn?.Dispose();
            _zoomOut?.Dispose();
            _smoke?.Dispose();
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
            _move.Enable();
            _zoom.Enable();
            _zoomIn.Enable();
            _zoomOut.Enable();
            SetZoom(MaxZoom);
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

            UpdateHud();
            _smoke?.AfterFrame();
            _screenshot?.AfterFrame();
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
            if (DebugText == null || ++_hudFrame % 10 != 0)
            {
                return;
            }

            TileCoord t = Torus.TileOf(Map, Player.Position);
            ChunkCoord c = Torus.ChunkOf(Map, t);
            DebugText.text = $"seed 0x{Args.Seed:X}  {Map.Width}x{Map.Height}  tile {t.X},{t.Y}  chunk {c.X},{c.Y}  " +
                             $"window {View.Count}/{ChunkWindow.MaxChunks}  anchor {Origin.Anchor.X},{Origin.Anchor.Y}  " +
                             $"zoom x{Zoom:0.00}  {1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f):0} fps";
        }
    }
}
