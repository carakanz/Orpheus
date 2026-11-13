#nullable enable
using System.IO;
using Orpheus.Core.Geometry;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Режим <c>--screenshot &lt;путь&gt;</c> (карточка 0-010, шаг 8; сеть — 0-012; сцены стенда — E1-1): в офлайне персонаж
    /// стоит на старте или на тайле <c>--shot-tile X,Y</c> (ближайший свободный), в сети — создаётся после входа и снимает
    /// там, где поселил сервер; обзор — <c>--shot-width</c> тайлов по ширине (по умолчанию 64);
    /// на 30-м кадре камера рисует в текстуру <c>--shot-size</c> (по умолчанию 1920 × 1080), та пишется в PNG, игра выходит.
    /// Офлайн время кадра фиксировано (1/60 с): повтор той же сцены даёт тот же кадр (стенд <c>tools/shots.ps1</c>).
    /// Окно не нужно (слово Автора 13.11: запуски не перехватывают фокус): <c>Orpheus.exe -batchmode --screenshot …</c>;
    /// <c>-nographics</c> нельзя — нужно графическое устройство. Надписи экранного слоя (HUD) в кадр не попадают.
    /// </summary>
    public sealed class ScreenshotRun
    {
        const int CaptureFrame = 30;
        const int TimeoutFrames = 600;
        const double NetWaitSec = 2.6; // время скрипта движения: 0,5 с стой + 2,1 с восток (~13 тайлов)
        const double NetTimeoutSec = 15;

        readonly GameBoot _g;
        readonly string _path;
        readonly bool _netMode;
        readonly double _startT = Time.realtimeSinceStartupAsDouble;
        int _frame;
        bool _requested;

        public ScreenshotRun(GameBoot g, string path, bool netMode = false)
        {
            _g = g;
            _netMode = netMode;
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

            ClientArgs a = g.Args;
            _g.SetViewTiles(a.ShotWidth > 0 ? a.ShotWidth : GameBoot.DefaultViewTiles);
            if (netMode)
            {
                Debug.Log($"[screenshot] сеть: снимок → {_path} (персонаж там, где поселил сервер), обзор {_g.ViewTiles:0} тайлов");
                return;
            }

            Time.captureDeltaTime = 1f / 60f; // кадры не зависят от скорости машины: повтор сцены — тот же PNG
            if (a.HasShotTile)
            {
                TileCoord at = FindFree(Torus.Wrap(g.Map, new TileCoord(a.ShotTileX, a.ShotTileY)));
                _g.Teleport(_g.TileCenter(at));
            }

            TileCoord t = Torus.TileOf(g.Map, _g.Player.Position);
            Debug.Log($"[screenshot] персонаж на тайле {t.X},{t.Y}, обзор {_g.ViewTiles:0} тайлов, снимок → {_path}");
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

        // Кадр камеры — в текстуру заданного размера и в PNG: без окна и без ScreenCapture (в -batchmode тот не работает).
        void Capture()
        {
            int w = _g.Args.ShotPixelsX, h = _g.Args.ShotPixelsY;
            Camera cam = _g.Camera;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture? prevTarget = cam.targetTexture;
            RenderTexture? prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(_path, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        public void AfterFrame()
        {
            _frame++;
            bool ready = _g.Net == null || _g.Net.InGameSeconds >= NetWaitSec;
            if (!_requested && _frame >= CaptureFrame && ready)
            {
                // Сеть: кадр ждём входа — персонаж к этому моменту уже проходит скрипт движения (0,5 с стой + восток).
                Debug.Log($"[screenshot] захват на кадре {_frame}, в игре {_g.Net?.InGameSeconds:0.0} с");
                Capture();
                _requested = true;
                return;
            }

            if (_requested && File.Exists(_path) && new FileInfo(_path).Length > 0)
            {
                Debug.Log($"[screenshot] сохранён {_path} ({_g.Args.ShotPixelsX}x{_g.Args.ShotPixelsY})");
                Application.Quit(0);
                _requested = false;
            }
            else if (_netMode ? Time.realtimeSinceStartupAsDouble - _startT > NetTimeoutSec : _frame > TimeoutFrames)
            {
                Debug.LogWarning($"[screenshot] файл не появился (кадр {_frame}, в игре {_g.Net?.InGameSeconds:0.0} с)");
                Application.Quit(1);
            }
        }
    }
}
