#nullable enable
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Orpheus.Client
{
    /// <summary>
    /// C-048: net-smoke на ЖИВОМ пути ввода (лук: «при зажатии должен нонстопом стрелять», слово Автора 21.11).
    /// В -batchmode -nographics мыши нет (<c>Mouse.current == null</c>) — <c>UpdateEdits</c> возвращает 0, и живой путь
    /// (ветка лука в NetGame.Edits.cs) smoke-прогонами не покрыт вовсе: скриптованные кадры идут мимо мыши.
    /// Здесь на прогоне <c>--smoke-bow</c> заводятся synthetic-мышь и клавиатура (<see cref="InputSystem.AddDevice{T}"/>)
    /// и каждый кадр их состояние queue'ится (<see cref="InputSystem.QueueStateEvent{TState}"/>), состояние приходит
    /// на следующем кадре (1 кадр лага на 5-секундном окне незаметен).
    /// Сценарий: с 0,5 с бег на восток (D — гейт сдвига ≥ 2 тайла), на 1,2 с нажатие «5» (лук — ячейка 4 стартового
    /// набора, клавиша 5), зажатие ЛКМ 5,0–10,0 с (скриптовая пауза 2,8–4,0 с заканчивается до окна), отпускание на 10 с —
    /// выстрел при отпускании. Проверка — в <c>NetSmokeCheck</c> по подтверждённым сервером выстрелам
    /// (<see cref="Orpheus.Net.Session.ClientProjectiles.OwnConfirmed"/>).
    /// </summary>
    public sealed partial class NetGame
    {
        const double BowRunStartSec = 0.5;  // бег на восток (D) — тот же сдвиг, что в скриптовом прогоне
        const double BowSlotPressSec = 1.2; // один кадр «5» — лук в руках (ячейка 4 стартового набора)
        const double BowHoldStartSec = 5.0; // окно зажатия ЛКМ (после скриптовой паузы 2,8–4,0 с)
        const double BowHoldEndSec = 10.0;  // отпускание — выстрел при отпускании

        Mouse? _smokeMouse;
        Keyboard? _smokeKeyboard;
        MouseState _smokeMouseState;
        KeyboardState _smokeKbState;

        /// <summary>Точка мыши для лучника (экранные пиксели, низ слева): центр экрана или низ над панелью.</summary>
        Vector2 _smokePointerPos;
        bool _smokePointerPlaced;

        /// <summary>0 — «5» не отправлена, 1 — нажата (кадр), 2 — отпущена.</summary>
        int _smokeDigitPhase;

        /// <summary>Заводит synthetic-устройства (StartConnect, до первого кадра ввода).</summary>
        void SetupSmokeInput()
        {
            _smokeMouse = InputSystem.AddDevice<Mouse>();
            _smokeKeyboard = InputSystem.AddDevice<Keyboard>();
            _smokeMouseState = new MouseState();
            _smokeKbState = new KeyboardState();
        }

        /// <summary>
        /// Точка мыши: по умолчанию центр экрана (панель не мешает). C-048 (<c>--smoke-bow-bar</c>): середина сферы
        /// здоровья нижней панели — заведомо нарисованная зона интерфейса. HudLayout — холст 1080 (низ слева),
        /// экран дыма — 1280×720 (масштаб 2/3): центр левой сферы x = (Screen.width−BarWidth·s)/2 + Orb·s/2,
        /// y = (BarBottom + BarHeight/2)·s. В <c>-batchmode -nographics</c> Screen.width/height могут быть 0 —
        /// печатаем их в строку лука, проверка «панель блокирует» на нулевом экране невозможна, это видно по данным.
        /// </summary>
        void PlaceSmokePointer()
        {
            float h = Screen.height, w = Screen.width;
            float s = h > 0f ? h / 1080f : 0f;
            float x = w > 0f ? (w - HudLayout.BarWidth * s) * 0.5f + HudLayout.Orb * s * 0.5f : 536f;
            float y = h > 0f ? (HudLayout.BarBottom + HudLayout.BarHeight * 0.5f) * s : 49f;
            _smokePointerPos = _args.NetSmokeBowBar
                ? new Vector2(x, y)
                : new Vector2(w > 0f ? w * 0.5f : 640f, h > 0f ? h * 0.5f : 360f);
            _smokePointerPlaced = true;
        }

        /// <summary>Каждый кадр (начало <c>Frame</c>): очередь состояния по сценарию.</summary>
        void InjectSmokeInput()
        {
            if (_smokeMouse == null || _smokeKeyboard == null)
            {
                return;
            }

            if (!_smokePointerPlaced)
            {
                PlaceSmokePointer();
            }

            double t = InGameSeconds;

            MouseState m = _smokeMouseState;
            m.position = _smokePointerPos;
            if (t >= BowHoldStartSec && t < BowHoldEndSec)
            {
                m.buttons |= 1; // Mouse.leftButton — бит 0 в MouseState.buttons
            }

            KeyboardState k = _smokeKbState;
            if (t >= BowRunStartSec)
            {
                k.Set(Key.D, true);
            }

            if (_smokeDigitPhase == 0 && t >= BowSlotPressSec)
            {
                k.Set(Key.Digit5, true);
                _smokeDigitPhase = 1;
            }
            else if (_smokeDigitPhase == 1)
            {
                _smokeDigitPhase = 2; // один кадр нажатия — DigitPressed берёт wasPressedThisFrame
            }

            InputSystem.QueueStateEvent(_smokeMouse, m);
            InputSystem.QueueStateEvent(_smokeKeyboard, k);
        }
    }
}
