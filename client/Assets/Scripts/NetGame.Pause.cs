#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Orpheus.Client
{
    /// <summary>
    /// Пауза в клиенте (карточка 0-036; BRIEF п. 11b — у любого игрока есть возможность поставить паузу). Esc или Start на
    /// геймпаде — «поставить паузу», в паузе — «продолжить» (снять может любой, допущение <c>[док]</c>); Q в паузе — выйти
    /// (раньше выходом был Esc). Пауза ставится только сервером: экран и ввод меняются по его «пауза / продолжить» —
    /// надпись «Пауза — поставил &lt;имя&gt;», часы клиента стоят (кадры ввода не уходят, предсказание не шагает), время показа
    /// чужих стоит (<see cref="RemoteInterpolation.OnPause"/>).
    /// <para>
    /// net-smoke: на 2,8 с в игре — пауза на 1 с посреди бега на север; в паузе свой персонаж (предсказание и снимок) стоит
    /// и кадры не уходят, надпись видна; после — дальше по скрипту (правки и рывки проверяет общий net-smoke).
    /// </para>
    /// </summary>
    public sealed partial class NetGame
    {
        const double SmokePauseAtSec = 2.8;
        const double SmokePauseForSec = 1.0;

        /// <summary>Секунд от входа в паузу, пока позиция на входе уточняется (снимок в пути мог прийти после «пауза»).</summary>
        const double SmokePauseSettleSec = 0.3;

        InputAction? _pauseKey;
        GameObject? _pauseOverlay;
        Text? _pauseText;
        bool _pauseHooked;

        // net-smoke паузы
        int _smokePauseStage; // 0 — ждёт, 1 — запрошена, 2 — стоит, 3 — снятие запрошено, 4 — готово
        double _smokePauseAt;
        int _smokePauseFrames;
        WorldPos _smokePausePred;
        WorldPos _smokePauseSelf;
        long _smokePauseInputs;
        int _smokePauseMoved;
        bool _smokePauseShown;
        bool _smokePauseShot;

        /// <summary>
        /// Кадр паузы: в игре Esc / Start — пауза, Q в паузе — выход; вне игры (подключение, «Не удалось подключиться»,
        /// «Хост вышел» и другие окончательные статусы) Esc и Q, как до 0-036, закрывают игру (проверка 0-036, з. 1).
        /// </summary>
        void PauseFrame()
        {
            NetClient? c = _client;
            if (c == null || !InGame || _fatal)
            {
                ShowPause(null);
                Keyboard? kb = Keyboard.current;
                if (_args.NetSmokeSeconds == 0 && kb != null && (kb.escapeKey.wasPressedThisFrame || kb.qKey.wasPressedThisFrame))
                {
                    Quit(0);
                }

                return;
            }

            if (!_pauseHooked && _remote != null)
            {
                _pauseHooked = true;
                RemoteInterpolation r = _remote;
                c.PauseChanged = paused =>
                {
                    r.OnPause(paused, NowUs());
                    Debug.Log(paused
                        ? $"[net] пауза — поставил «{c.PausedBy}» (такт {c.PauseTick})"
                        : $"[net] продолжить — снял «{(c.PausedBy.Length > 0 ? c.PausedBy : "сервер")}», стояли {c.PausedTotalUs / 1e6:0.00} с всего");
                };
                if (c.Paused)
                {
                    r.OnPause(true, NowUs()); // вошли в паузу: «пауза» пришла вместе со входом
                }
            }

            if (_args.NetSmokeSeconds > 0)
            {
                SmokePause(c);
            }
            else
            {
                if (_pauseKey == null)
                {
                    _pauseKey = new InputAction("Pause", InputActionType.Button, "<Keyboard>/escape");
                    _pauseKey.AddBinding("<Gamepad>/start");
                    _pauseKey.Enable();
                }

                if (_pauseKey.WasPressedThisFrame())
                {
                    c.RequestPause(!c.Paused);
                    Debug.Log(c.Paused ? "[net] «продолжить» отправлено" : "[net] «пауза» отправлена");
                }

                if (c.Paused && Keyboard.current?.qKey.wasPressedThisFrame == true)
                {
                    Quit(0);
                }
            }

            ShowPause(c.Paused ? $"Пауза — поставил {(c.PausedBy.Length > 0 ? c.PausedBy : "сервер")}" : null);
        }

        /// <summary>Надпись паузы по центру экрана поверх затемнения; null — убрать.</summary>
        void ShowPause(string? text)
        {
            if (text == null)
            {
                if (_pauseOverlay != null && _pauseOverlay.activeSelf)
                {
                    _pauseOverlay.SetActive(false);
                }

                return;
            }

            if (_pauseOverlay == null)
            {
                Canvas? canvas = _g.DebugText != null ? _g.DebugText.canvas : null;
                if (canvas == null)
                {
                    return; // сцена без HUD — показывать негде (пауза всё равно работает)
                }

                _pauseOverlay = new GameObject("Pause");
                _pauseOverlay.transform.SetParent(canvas.transform, false);
                var shade = _pauseOverlay.AddComponent<Image>();
                shade.color = new Color(0f, 0f, 0f, 0.6f); // линейное пространство: 0,6 — экран темнее на ≈ 40 %
                shade.raycastTarget = false;
                RectTransform srt = shade.rectTransform;
                srt.anchorMin = Vector2.zero;
                srt.anchorMax = Vector2.one;
                srt.offsetMin = srt.offsetMax = Vector2.zero;

                var textGo = new GameObject("PauseText");
                textGo.transform.SetParent(_pauseOverlay.transform, false);
                _pauseText = textGo.AddComponent<Text>();
                _pauseText.font = _g.DebugText!.font;
                _pauseText.fontSize = 64;
                _pauseText.color = Color.white;
                _pauseText.alignment = TextAnchor.MiddleCenter;
                _pauseText.raycastTarget = false;
                _pauseText.horizontalOverflow = HorizontalWrapMode.Overflow;
                _pauseText.verticalOverflow = VerticalWrapMode.Overflow;
                RectTransform trt = _pauseText.rectTransform;
                trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 0.5f);
                trt.sizeDelta = new Vector2(1600f, 240f);
            }

            string full = text + "\n<size=32>Esc / Start — продолжить, Q — выйти</size>";
            if (_pauseText != null && _pauseText.text != full)
            {
                _pauseText.supportRichText = true;
                _pauseText.text = full;
            }

            if (!_pauseOverlay.activeSelf)
            {
                _pauseOverlay.SetActive(true);
            }
        }

        /// <summary>net-smoke: пауза по скрипту и проверка, что в паузе всё стоит.</summary>
        void SmokePause(NetClient c)
        {
            double t = InGameSeconds;
            switch (_smokePauseStage)
            {
                case 0:
                    if (t >= SmokePauseAtSec)
                    {
                        c.RequestPause(true);
                        _smokePauseStage = 1;
                    }

                    break;
                case 1:
                    if (c.Paused)
                    {
                        _smokePauseStage = 2;
                        _smokePauseAt = t;
                    }
                    else if (t >= SmokePauseAtSec + 1.5)
                    {
                        Fail("[smoke] FAILED: пауза — сервер не поставил её за 1,5 с");
                    }

                    break;
                case 2:
                    WorldPos pred = _pred!.Position;
                    WorldPos self = c.HasSnapshot ? c.Latest.Self.Position : default;
                    _smokePauseFrames++;
                    if (t < _smokePauseAt + SmokePauseSettleSec)
                    {
                        _smokePausePred = pred;
                        _smokePauseSelf = self;
                        _smokePauseInputs = c.InputsSent;
                    }
                    else if (pred != _smokePausePred || self != _smokePauseSelf)
                    {
                        _smokePauseMoved++;
                    }

                    if (!_smokePauseShot && t >= _smokePauseAt + SmokePauseForSec / 2)
                    {
                        // Кадр надписи паузы для глаз (не в batchmode: там не рисуется): путь — в переменной ORPHEUS_PAUSE_SHOT.
                        _smokePauseShot = true;
                        string? shot = System.Environment.GetEnvironmentVariable("ORPHEUS_PAUSE_SHOT");
                        if (!string.IsNullOrEmpty(shot) && !Application.isBatchMode)
                        {
                            ScreenCapture.CaptureScreenshot(shot);
                            Debug.Log($"[smoke] пауза: кадр → {shot}");
                        }
                    }

                    _smokePauseShown |= _pauseOverlay != null && _pauseOverlay.activeSelf && _pauseText != null && _pauseText.text.StartsWith("Пауза — поставил ");
                    if (!c.Paused)
                    {
                        Fail("[smoke] FAILED: пауза снялась сама");
                    }
                    else if (t >= _smokePauseAt + SmokePauseForSec)
                    {
                        long inputs = c.InputsSent - _smokePauseInputs;
                        Debug.Log($"[smoke] пауза: стояли {t - _smokePauseAt:0.00} с, кадров рисования {_smokePauseFrames}, сдвигов {_smokePauseMoved}, " +
                                  $"кадров ввода в паузе {inputs}, надпись «{(_pauseText != null ? _pauseText.text.Split('\n')[0] : "—")}» {(_smokePauseShown ? "видна" : "НЕ видна")}, " +
                                  $"образцов часов в паузе {c.Clock.SamplesWhileHeld}, кадров показа чужих в паузе {_remote?.PausedFrames ?? 0}");
                        if (_smokePauseMoved > 0 || inputs != 0 || !_smokePauseShown)
                        {
                            Fail("[smoke] FAILED: пауза — в паузе персонаж сдвинулся, ушли кадры ввода или нет надписи");
                            return;
                        }

                        c.RequestPause(false);
                        _smokePauseStage = 3;
                    }

                    break;
                case 3:
                    if (!c.Paused)
                    {
                        _smokePauseStage = 4;
                        Debug.Log($"[smoke] пауза снята: «{c.PausedBy}», правок до сих пор {_pred!.Resynced}, перенумераций {c.FrameRenumbers}");
                    }
                    else if (t >= _smokePauseAt + SmokePauseForSec + 1.5)
                    {
                        Fail("[smoke] FAILED: пауза — сервер не снял её за 1,5 с");
                    }

                    break;
            }
        }

        /// <summary>net-smoke не кончается, пока пауза не прошла (проверка — до «[smoke] done»).</summary>
        bool SmokePauseDone => _args.NetSmokeSeconds <= 0 || _smokePauseStage == 4;
    }
}
