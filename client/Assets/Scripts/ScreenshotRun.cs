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
    /// <para>
    /// Лента (E1-11, <c>--shot-walk</c>): движение на одном снимке не видно, поэтому персонаж скриптом идёт в заданную
    /// сторону (или стоит), а кадры через равные промежутки вырезаются вокруг него и склеиваются в одну картинку
    /// (до 4 в ряд, слева направо, сверху вниз) — видно, как вздрагивает дерево, сыплются листья, ложится трава.
    /// </para>
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

        // Лента: кадры, склейка, такт следующего кадра.
        readonly int _walkFrames;
        Texture2D? _strip;
        int _walkTaken;
        int _walkStartFrame = -1;
        const int StripGap = 6;

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
            _walkFrames = netMode ? 0 : a.WalkFrames;
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

        // Кадр ленты: вырезка вокруг персонажа из кадра камеры — в клетку общей картинки.
        void CaptureStrip()
        {
            ClientArgs a = _g.Args;
            int w = a.ShotPixelsX, h = a.ShotPixelsY;
            int cw = Mathf.Min(a.CropX, w), ch = Mathf.Min(a.CropY, h);
            int cols = Mathf.Min(_walkFrames, 4), rows = (_walkFrames + cols - 1) / cols;
            if (_strip == null)
            {
                _strip = new Texture2D(cols * (cw + StripGap) - StripGap, rows * (ch + StripGap) - StripGap, TextureFormat.RGB24, false);
                var fill = new Color32[_strip.width * _strip.height];
                for (int i = 0; i < fill.Length; i++)
                {
                    fill[i] = new Color32(24, 24, 28, 255);
                }

                _strip.SetPixels32(fill);
            }

            Camera cam = _g.Camera;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture? prevTarget = cam.targetTexture;
            RenderTexture? prevActive = RenderTexture.active;
            try
            {
                WorldFx.BeforeManualRender(w, h);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(cw, ch, TextureFormat.RGB24, false);
                // Персонаж — в середине кадра (камера идёт за ним). ReadPixels: начало — левый нижний угол (D3D: верхний, перевёрнуто само).
                tex.ReadPixels(new Rect((w - cw) / 2, (h - ch) / 2, cw, ch), 0, 0);
                tex.Apply();
                int col = _walkTaken % cols, row = _walkTaken / cols;
                _strip.SetPixels32(col * (cw + StripGap), (rows - 1 - row) * (ch + StripGap), cw, ch, tex.GetPixels32());
                UnityEngine.Object.Destroy(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }

            _walkTaken++;
            if (_walkTaken == _walkFrames)
            {
                _strip.Apply();
                File.WriteAllBytes(_path, _strip.EncodeToPNG());
                UnityEngine.Object.Destroy(_strip);
                _strip = null;
                _g.ScriptedInput = Orpheus.Core.Sim.InputFrame.None;
                Debug.Log($"[screenshot] лента: кадров {_walkFrames} через {a.WalkInterval:0.00} с, вырезка {cw}x{ch}, толчков деревьев {LifeFx.Pushes}, листьев выпущено {LeafFx.Shedded}, точек следа {LifeFx.TrailTexels()}");
            }
        }

        // Лента: с кадра CaptureFrame персонаж идёт; кадры — после разбега через равные промежутки (время кадра 1/60 с).
        void WalkFrame()
        {
            ClientArgs a = _g.Args;
            if (_walkStartFrame < 0)
            {
                if (_frame < CaptureFrame)
                {
                    return;
                }

                _walkStartFrame = _frame;
                _g.ScriptedInput = GameBoot.ToFrame(new Vector2(a.WalkDirX, a.WalkDirY));
                TileCoord t = Torus.TileOf(_g.Map, _g.Player.Position);
                Debug.Log($"[screenshot] лента: старт с тайла {t.X},{t.Y}, направление {a.WalkDirX:0},{a.WalkDirY:0}");
            }

            float time = (_frame - _walkStartFrame) / 60f;
            if (_walkTaken < _walkFrames && time >= a.WalkLead + _walkTaken * a.WalkInterval - 1e-4f)
            {
                CaptureStrip();
                if (_walkTaken == _walkFrames)
                {
                    TileCoord t = Torus.TileOf(_g.Map, _g.Player.Position);
                    Debug.Log($"[screenshot] лента: конец на тайле {t.X},{t.Y}");
                    _requested = true;
                }
            }
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
                WorldFx.BeforeManualRender(w, h); // E1-7: тени и тон — под размер снимка, а не окна
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                BenchRender(cam, rt, _g.View);
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

        /// <summary>
        /// C-016: замер отрисовки сцены снимка — переменная окружения <c>ORPH_SHOT_BENCH</c> = число кадров: камера рисует
        /// в ту же текстуру столько раз, каждый кадр ждёт видеокарту (чтение точки); в лог — среднее и 1 % худших, мс.
        /// </summary>
        static void BenchRender(Camera cam, RenderTexture rt, WorldView view)
        {
            if (!int.TryParse(System.Environment.GetEnvironmentVariable("ORPH_SHOT_BENCH"), out int frames) || frames <= 0)
            {
                return;
            }

            var px = new Texture2D(1, 1, TextureFormat.RGB24, false);
            var ms = new double[frames];
            for (int i = -10; i < frames; i++) // 10 кадров прогрева
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                cam.Render();
                RenderTexture.active = rt;
                px.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                px.Apply();
                if (i >= 0)
                {
                    ms[i] = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                }
            }

            UnityEngine.Object.Destroy(px);
            System.Array.Sort(ms);
            double sum = 0;
            foreach (double v in ms)
            {
                sum += v;
            }

            double avg = sum / frames, p99 = ms[System.Math.Min(frames - 1, (int)(frames * 0.99))];
            Debug.Log($"[shotbench] кадров {frames}: средний {avg:0.000} мс ({1000.0 / avg:0} fps), медиана {ms[frames / 2]:0.000} мс, " +
                      $"1 % худших {p99:0.000} мс ({1000.0 / p99:0} fps), {rt.width}x{rt.height}; объекты чанка при входе — " +
                      $"{view.FreshObjectTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / System.Math.Max(view.FreshObjectFills, 1):0} мкс × {view.FreshObjectFills}");
        }

        public void AfterFrame()
        {
            _frame++;
            bool ready = _g.Net == null || _g.Net.InGameSeconds >= NetWaitSec;
            if (_walkFrames > 0 && !_requested)
            {
                WalkFrame();
                if (_frame > TimeoutFrames * 3)
                {
                    Debug.LogWarning($"[screenshot] лента не снята (кадр {_frame})");
                    Application.Quit(1);
                }

                return;
            }

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
