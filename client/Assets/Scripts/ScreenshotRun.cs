#nullable enable
using System.IO;
using Orpheus.Core.Geometry;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Режим <c>--screenshot &lt;путь&gt;</c> (карточка 0-010, шаг 8): персонаж ставится в 60 тайлах к востоку от центра
    /// (для зерна по умолчанию там в кадре и поле скал, и луга с деревьями), зум ×0,6 — самый широкий обзор (67 тайлов);
    /// через полсекунды кадр уходит в PNG через <see cref="ScreenCapture"/>, игра выходит.
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

            int c = g.Map.Width / 2;
            _g.Teleport(_g.TileCenter(FindFree(new TileCoord(c + 60, c))));
            _g.SetZoom(GameBoot.MinZoom);
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
