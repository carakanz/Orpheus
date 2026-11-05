using System;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Orpheus.Client
{
    /// <summary>
    /// Дымовая проверка сборки: при запуске с аргументом <c>--smoke</c> проверяет сцену Boot,
    /// ждёт <c>--smoke-seconds=N</c> секунд (по умолчанию 5) и выходит.
    /// Код выхода 0 — ошибок в логе не было; 1 — были ошибки, исключения или сцена не та.
    /// Без аргумента компонент ничего не делает.
    /// </summary>
    public sealed class SmokeExit : MonoBehaviour
    {
        public const string Flag = "--smoke";
        const string SecondsFlag = "--smoke-seconds=";
        const int ExpectedTiles = 9;

        float _seconds = 5f;
        float _start;
        int _errors;
        bool _quitting;

        void Awake()
        {
            if (!ParseArgs(Environment.GetCommandLineArgs()))
            {
                enabled = false;
                return;
            }
            Application.logMessageReceived += OnLog;
            Application.targetFrameRate = 60; // без окна кадры не ждут vsync — не крутить ядро впустую
            _start = Time.realtimeSinceStartup;
            Debug.Log($"[smoke] start: Unity {Application.unityVersion}, backend {Backend()}, wait {_seconds:0.#} s");
            CheckScene();
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        bool ParseArgs(string[] args)
        {
            bool smoke = false;
            foreach (string a in args)
            {
                if (a == Flag) smoke = true;
                else if (a.StartsWith(SecondsFlag, StringComparison.Ordinal) &&
                         float.TryParse(a.Substring(SecondsFlag.Length),
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out float s) && s > 0f)
                    _seconds = s;
            }
            return smoke;
        }

        static string Backend()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return "Mono";
#endif
        }

        void CheckScene()
        {
            Tilemap tilemap = FindAnyObjectByType<Tilemap>();
            if (tilemap == null)
            {
                Debug.LogError("[smoke] в сцене нет Tilemap");
                return;
            }
            int tiles = 0;
            foreach (Vector3Int cell in tilemap.cellBounds.allPositionsWithin)
                if (tilemap.HasTile(cell)) tiles++;
            if (tiles != ExpectedTiles)
                Debug.LogError($"[smoke] тайлов {tiles}, ожидается {ExpectedTiles}");

            if (FindAnyObjectByType<SpriteRenderer>() == null)
                Debug.LogError("[smoke] в сцене нет спрайта персонажа");
            if (FindAnyObjectByType<Camera>() == null)
                Debug.LogError("[smoke] в сцене нет камеры");

            Debug.Log($"[smoke] scene ok: tiles={tiles}");
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _errors++;
        }

        void Update()
        {
            if (_quitting || Time.realtimeSinceStartup - _start < _seconds) return;
            _quitting = true;
            int code = _errors == 0 ? 0 : 1;
            Debug.Log($"[smoke] done: frames={Time.frameCount} errors={_errors} exit={code}");
            Application.Quit(code);
        }
    }
}
