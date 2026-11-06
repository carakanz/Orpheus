#nullable enable
using System.IO;
using Orpheus.Core.Geometry;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Режим <c>--screenshot &lt;путь&gt;</c> (карточка 0-010, шаг 8): персонаж ставится у края стартовой зоны
    /// (видны луга, скалы, деревья), через полсекунды кадр уходит в PNG через <see cref="ScreenCapture"/>, игра выходит.
    /// Нужен вывод на экран — запускать без <c>-batchmode -nographics</c>; окно открыто ~1 с.
    /// </summary>
    public sealed class ScreenshotRun
    {
        const int CaptureFrame = 30;
        const int TimeoutFrames = 600;

        readonly GameBoot _g;
        readonly string _path;
        int _frame;
        bool _requested;

        public ScreenshotRun(GameBoot g, string path)
        {
            _g = g;
            _path = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            // Стартовая зона — 32 × 32 чистого луга в центре; на 20 тайлов восточнее и 8 южнее в кадре и она, и мир.
            int c = g.Map.Width / 2;
            _g.Teleport(_g.TileCenter(FindFree(new TileCoord(c + 20, c + 8))));
            Debug.Log($"[screenshot] персонаж в {_g.Player.Position}, снимок → {_path}");
        }

        // Ближайший проходимый тайл по спирали квадратов.
        TileCoord FindFree(TileCoord t)
        {
            for (int r = 0; r < 16; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var c = Torus.Wrap(_g.Map, new TileCoord(t.X + dx, t.Y + dy));
                        if (!_g.Rules.IsBlocked(_g.Map, c))
                        {
                            return c;
                        }
                    }
                }
            }

            return t;
        }

        public void AfterFrame()
        {
            _frame++;
            if (_frame == CaptureFrame)
            {
                ScreenCapture.CaptureScreenshot(_path);
                _requested = true;
                return;
            }

            if (_requested && File.Exists(_path) && new FileInfo(_path).Length > 0)
            {
                Debug.Log($"[screenshot] сохранён {_path} ({Screen.width}x{Screen.height})");
                Application.Quit(0);
                _requested = false;
            }
            else if (_frame > TimeoutFrames)
            {
                Debug.LogWarning("[screenshot] файл не появился");
                Application.Quit(1);
            }
        }
    }
}
