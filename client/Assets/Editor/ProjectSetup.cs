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

        /// <summary>Камера, Tilemap 3 × 3, персонаж-заглушка, надпись «Orpheus — TEMP ASSETS».</summary>
        public static void CreateBootScene()
        {
            Tile meadow = MakeTile("Meadow", LoadSprite("TEMP_tile_meadow.png"));
            Tile rock = MakeTile("Rock", LoadSprite("TEMP_tile_rock.png"));
            Sprite orpheus = LoadSprite("TEMP_char_orpheus.png");
            AssetDatabase.SaveAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.position = new Vector3(0f, 0.25f, -10f);
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 2.5f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.07f, 0.10f);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 100f;

            var gridGo = new GameObject("Grid");
            gridGo.AddComponent<Grid>().cellSize = Vector3.one;
            var groundGo = new GameObject("Ground");
            groundGo.transform.SetParent(gridGo.transform, false);
            var tilemap = groundGo.AddComponent<Tilemap>();
            var tilemapRenderer = groundGo.AddComponent<TilemapRenderer>();
            tilemapRenderer.sortingOrder = 0;
            // 3 × 3: скалы по углам, луг крестом; заливка одним вызовом, как в 07, 1.2
            var cells = new TileBase[9];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    cells[y * 3 + x] = (x != 1 && y != 1) ? rock : meadow;
            tilemap.SetTilesBlock(new BoundsInt(-1, -1, 0, 3, 3, 1), cells);

            var heroGo = new GameObject("Orpheus_Placeholder");
            heroGo.transform.position = new Vector3(0f, -0.4f, 0f);
            var sr = heroGo.AddComponent<SpriteRenderer>();
            sr.sprite = orpheus;
            sr.sortingOrder = 10;

            var canvasGo = new GameObject("HUD");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            var labelGo = new GameObject("TempLabel");
            labelGo.transform.SetParent(canvasGo.transform, false);
            var label = labelGo.AddComponent<Text>();
            label.text = "Orpheus — TEMP ASSETS";
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 40;
            label.color = new Color(1f, 0f, 1f); // #FF00FF — цвет пометки заглушек
            label.alignment = TextAnchor.UpperLeft;
            label.raycastTarget = false;
            var rt = label.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, -24f);
            rt.sizeDelta = new Vector2(900f, 60f);

            new GameObject("Boot").AddComponent<SmokeExit>();

            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            if (!EditorSceneManager.SaveScene(scene, BootScenePath))
                throw new InvalidOperationException("не сохранилась сцена " + BootScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BootScenePath, true) };
            Debug.Log("[setup] сцена " + BootScenePath + " сохранена и стоит первой в сборке");
        }
    }
}
