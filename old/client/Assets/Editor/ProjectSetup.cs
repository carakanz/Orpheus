using System;
using Orpheus.Client;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using UnityEngine.UI;

namespace Orpheus.Editor
{
    /// <summary>
    /// Настройка проекта и сцены Boot командой, без кликов (docs/07, 11.1a — запасной путь
    /// <c>-batchmode -executeMethod</c>). Повторный запуск пересоздаёт сцену и тайлы с теми же именами.
    /// <code>Unity -batchmode -nographics -quit -projectPath client -executeMethod Orpheus.Editor.ProjectSetup.Run -logFile setup.log</code>
    /// </summary>
    public static class ProjectSetup
    {
        public const string BootScenePath = "Assets/Scenes/Boot.unity";
        const string TempArt = "Assets/Art/_TEMP/";
        const string TilesDir = "Assets/Tiles";
        const string Renderer2DPath = "Assets/Settings/Renderer2D.asset";

        [MenuItem("Orpheus/Настроить проект и пересоздать Boot")]
        public static void Run()
        {
            try
            {
                ApplyPlayerSettings();
                ApplyRendererSettings();
                CreateBootScene();
                AssetDatabase.SaveAssets();
                Debug.Log("[setup] готово");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }

        /// <summary>IL2CPP для Windows, API .NET Standard 2.1, только Input System (08, 3.0; 07, 1.3).</summary>
        public static void ApplyPlayerSettings()
        {
            PlayerSettings.companyName = "Orpheus";
            PlayerSettings.productName = "Orpheus";
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.usePlayerLog = true;

            // activeInputHandler: 0 — старый Input Manager, 1 — Input System, 2 — оба. Публичного API нет.
            var so = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            SerializedProperty input = so.FindProperty("activeInputHandler");
            if (input == null) throw new InvalidOperationException("нет поля activeInputHandler в ProjectSettings");
            input.intValue = 1;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(so.targetObject);

            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            ApiCompatibilityLevel api = PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.Standalone);
            Debug.Log($"[setup] PlayerSettings: backend={backend}, api={(int)api} (6 = .NET Standard 2.1), activeInputHandler={input.intValue}");
            if (backend != ScriptingImplementation.IL2CPP)
                throw new InvalidOperationException($"backend для Standalone не переключился: {backend}");
        }

        /// <summary>Сортировка спрайтов по оси Y — перспектива «сверху с наклоном» (00, 5.1; 07, 1.2).</summary>
        public static void ApplyRendererSettings()
        {
            UnityEngine.Object renderer = AssetDatabase.LoadMainAssetAtPath(Renderer2DPath);
            if (renderer == null) throw new InvalidOperationException("нет " + Renderer2DPath);
            var so = new SerializedObject(renderer);
            SerializedProperty mode = so.FindProperty("m_TransparencySortMode");
            SerializedProperty axis = so.FindProperty("m_TransparencySortAxis");
            if (mode == null || axis == null) throw new InvalidOperationException("в Renderer2D нет полей сортировки");
            mode.intValue = (int)TransparencySortMode.CustomAxis;
            axis.vector3Value = new Vector3(0f, 1f, 0f);
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[setup] Renderer2D: сортировка по оси Y");
        }

        static Text AddLabel(Transform parent, string name, string text, int size, Color color, float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<Text>();
            label.text = text;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.color = color;
            label.alignment = TextAnchor.UpperLeft;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = label.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, y);
            rt.sizeDelta = new Vector2(1800f, size * 1.5f);
            return label;
        }

        static Sprite LoadSprite(string file)
        {
            string path = TempArt + file;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
                throw new InvalidOperationException($"нет спрайта {path} — сначала python tools/make_temp_assets.py");
            return sprite;
        }

        static Tile MakeTile(string name, Sprite sprite)
        {
            if (!AssetDatabase.IsValidFolder(TilesDir)) AssetDatabase.CreateFolder("Assets", "Tiles");
            string path = $"{TilesDir}/{name}.asset";
            Tile tile = AssetDatabase.LoadAssetAtPath<Tile>(path);
            if (tile == null)
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                AssetDatabase.CreateAsset(tile, path);
            }
            tile.sprite = sprite;
            tile.colliderType = Tile.ColliderType.None; // физику Unity не используем (07, 1.2)
            EditorUtility.SetDirty(tile);
            return tile;
        }

        /// <summary>
        /// Сцена Boot (карточка 0-010): камера, общий Grid для узлов чанков, персонаж-заглушка, надписи
        /// «Orpheus — TEMP ASSETS» и строка отладки, объект Game с <see cref="GameBoot"/> — он строит мир при запуске.
        /// </summary>
        public static void CreateBootScene()
        {
            Tile meadow = MakeTile("FloorMeadow", LoadSprite("TEMP_tile_meadow.png"));
            Tile stone = MakeTile("FloorStone", LoadSprite("TEMP_tile_stone.png"));
            Tile tree = MakeTile("ObjTree", LoadSprite("TEMP_obj_tree.png"));
            Tile rock = MakeTile("ObjRock", LoadSprite("TEMP_obj_rock.png"));
            Sprite orpheus = LoadSprite("TEMP_char_orpheus.png");
            AssetDatabase.SaveAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.position = GameBoot.CameraOffset;
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = GameBoot.ReferenceHalfHeight;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.07f, 0.10f);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 100f;

            // Общий Grid; узлы чанков (по два Tilemap) создаёт WorldView при запуске (07, 1.2).
            var gridGo = new GameObject("Grid");
            gridGo.AddComponent<Grid>().cellSize = Vector3.one;

            // Персонаж: опора спрайта (низ по центру) — у ног, на 0,4 тайла ниже центра круга коллизии (00 5.1).
            var heroGo = new GameObject("Orpheus_Placeholder");
            var spriteGo = new GameObject("Sprite");
            spriteGo.transform.SetParent(heroGo.transform, false);
            spriteGo.transform.localPosition = new Vector3(0f, -0.4f, 0f);
            var sr = spriteGo.AddComponent<SpriteRenderer>();
            sr.sprite = orpheus;
            sr.sortingOrder = WorldView.ObjectOrder; // сортируется по Y вместе с объектами
            sr.spriteSortPoint = SpriteSortPoint.Pivot;

            var canvasGo = new GameObject("HUD");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            AddLabel(canvasGo.transform, "TempLabel", "Orpheus — TEMP ASSETS", 40, new Color(1f, 0f, 1f), -24f); // #FF00FF — цвет пометки заглушек
            Text debug = AddLabel(canvasGo.transform, "DebugText", "", 24, new Color(1f, 1f, 1f, 0.85f), -76f);

            var game = new GameObject("Game").AddComponent<GameBoot>();
            game.TileMeadow = meadow;
            game.TileStone = stone;
            game.TileTree = tree;
            game.TileRock = rock;
            game.Grid = gridGo.transform;
            game.Character = heroGo.transform;
            game.Camera = cam;
            game.DebugText = debug;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            if (!EditorSceneManager.SaveScene(scene, BootScenePath))
                throw new InvalidOperationException("не сохранилась сцена " + BootScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BootScenePath, true) };
            Debug.Log("[setup] сцена " + BootScenePath + " сохранена и стоит первой в сборке");
        }
    }
}
