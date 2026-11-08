using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Core.Sim;
using Orpheus.Save;

namespace Orpheus.Server
{
    /// <summary>
    /// Сохранения сервера (docs/07, 6.4): автосохранение раз в <c>--autosave</c> секунд мира (по умолчанию 120)
    /// и при остановке. Состояние снимается на границе такта (<see cref="WorldSave.Capture"/>, основной поток,
    /// после <see cref="Simulation.Step"/>), сборка файла и атомарная запись — фоновым потоком. Следующее
    /// сохранение не начнётся, пока не закончилось предыдущее. Лог — только из основного потока
    /// (<see cref="IServerLog"/> не потокобезопасен): итог фоновой записи забирает <see cref="AfterTick"/>.
    /// <para>
    /// Интервал — в тактах (секунды × 30), а не по настенным часам: в режиме симуляции такты идут подряд,
    /// и автосохранение там тоже раз в 2 минуты мира. Поводы «босс, вход и выход игрока, смена настроек» — потом
    /// (карточка 0-009: только автосохранение и остановка).
    /// </para>
    /// </summary>
    public sealed class WorldSaver
    {
        private readonly WorldFolder _folder;
        private readonly byte[] _baseSha;
        private readonly string _name;
        private readonly IServerLog _log;
        private readonly long _intervalTicks;
        private readonly ISaveHook? _crash;
        private readonly int _padKb;

        private Task<SaveOutcome>? _pending;
        private long _lastSavedTick;
        private bool _lastTickSaved;

        /// <param name="startSaved">Состояние старта уже лежит в поколении (мир загружен из сохранения): при остановке
        /// без единого такта новое поколение не пишется.</param>
        /// <param name="padKb">Отладочное наполнение сохранения в КБ (приёмка 0.4, <c>--debug-save-pad</c>): 0 в обычной игре.</param>
        public WorldSaver(WorldFolder folder, byte[] baseSha256, string name, IServerLog log, int intervalSeconds, long startTick, bool startSaved, ISaveHook? crash, int padKb = 0)
        {
            _folder = folder;
            _baseSha = baseSha256;
            _name = name;
            _log = log;
            _intervalTicks = (long)intervalSeconds * Simulation.TickRate;
            _lastSavedTick = startTick;
            _lastTickSaved = startSaved;
            _crash = crash;
            _padKb = padKb;
        }

        /// <summary>Сохранений записано за запуск.</summary>
        public int Written { get; private set; }

        /// <summary>Номер последнего записанного поколения (0 — не было).</summary>
        public ulong LastNumber { get; private set; }

        /// <summary>После каждого такта (граница такта): забрать итог фоновой записи; пора — начать новую.</summary>
        public void AfterTick(Simulation sim)
        {
            Poll(sim.Tick);
            if (sim.Tick - _lastSavedTick < _intervalTicks)
            {
                return;
            }

            if (_pending != null && WaitForPrevious)
            {
                _pending.Wait();
                Poll(sim.Tick);
            }

            if (_pending == null)
            {
                Start(sim, "автосохранение");
            }
        }

        /// <summary>
        /// Режим симуляции: пора сохранять, а прошлая запись не кончилась — дождаться её, а не отложить. Такты там
        /// идут без часов, и без ожидания точки сохранения зависели бы от скорости диска; так они ровно через интервал.
        /// </summary>
        public bool WaitForPrevious { get; set; }

        /// <summary>Персонажи мира на границу такта (0-035, раздел ПЕРСОНАЖИ); null — нет.</summary>
        public Func<CharacterRecord[]>? Characters { get; set; }

        /// <summary>
        /// Сохранить сейчас, не дожидаясь интервала (0-024, docs/07 2.3: игра хоста упала и других игроков нет), — в фоне,
        /// как автосохранение; идёт прошлая запись или такт уже сохранён — ничего.
        /// </summary>
        public void SaveNow(Simulation sim, string reason)
        {
            Poll(sim.Tick);
            if (_pending != null || sim.Tick == _lastSavedTick && _lastTickSaved)
            {
                return;
            }

            Start(sim, reason);
        }

        /// <summary>Остановка сервера: дождаться фоновой записи, затем сохранить текущий такт (если он новее записанного).</summary>
        public void SaveOnStop(Simulation sim)
        {
            if (_pending != null)
            {
                _pending.Wait();
                Poll(sim.Tick);
            }

            if (sim.Tick == _lastSavedTick && _lastTickSaved)
            {
                return;
            }

            Start(sim, "остановка");
            _pending!.Wait();
            Poll(sim.Tick);
        }

        private void Start(Simulation sim, string reason)
        {
            var watch = Stopwatch.StartNew();
            WorldSaveData data = WorldSave.Capture(sim, _baseSha, _name, Characters?.Invoke());
            double captureMs = watch.Elapsed.TotalMilliseconds;
            ulong number = _folder.NextNumber;
            long tick = sim.Tick;
            _lastSavedTick = tick;
            _lastTickSaved = false;
            _crash?.OnBegin(number, tick, StateHash.Compute(sim).Total);

            long date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ISaveHook? crash = _crash;
            WorldFolder folder = _folder;
            _pending = Task.Run(() =>
            {
                var w = Stopwatch.StartNew();
                try
                {
                    byte[] bytes = WorldSave.Build(data, number, date, _padKb);
                    List<string> deleted = folder.WriteGeneration(number, bytes);
                    crash?.OnDone(number);
                    return new SaveOutcome(number, tick, reason, bytes.Length, data.ChunkCoords.Length, captureMs, w.Elapsed.TotalMilliseconds, deleted, null);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    return new SaveOutcome(number, tick, reason, 0, 0, captureMs, w.Elapsed.TotalMilliseconds, null, e.Message);
                }
            });
        }

        private void Poll(long tick)
        {
            if (_pending == null || !_pending.IsCompleted)
            {
                return;
            }

            SaveOutcome r = _pending.Result;
            _pending = null;
            if (r.Error != null)
            {
                // Последнее целое поколение осталось; игра идёт (6.8 «Диск переполнен»). Повтор — через интервал.
                _log.Error(tick, "save", "Мир не сохраняется: " + r.Error, ("number", r.Number), ("reason", r.Reason));
                return;
            }

            Written++;
            LastNumber = r.Number;
            _lastTickSaved = r.Tick == _lastSavedTick;
            _log.Info(tick, "save", "сохранено", ("number", r.Number), ("tick", r.Tick), ("reason", r.Reason),
                ("bytes", r.Bytes), ("chunks", r.Chunks), ("captureMs", r.CaptureMs), ("writeMs", r.WriteMs),
                ("deleted", r.Deleted != null && r.Deleted.Count > 0 ? string.Join(",", r.Deleted) : ""));
        }

        private sealed class SaveOutcome
        {
            public SaveOutcome(ulong number, long tick, string reason, int bytes, int chunks, double captureMs, double writeMs, List<string>? deleted, string? error)
            {
                Number = number;
                Tick = tick;
                Reason = reason;
                Bytes = bytes;
                Chunks = chunks;
                CaptureMs = captureMs;
                WriteMs = writeMs;
                Deleted = deleted;
                Error = error;
            }

            public ulong Number { get; }
            public long Tick { get; }
            public string Reason { get; }
            public int Bytes { get; }
            public int Chunks { get; }
            public double CaptureMs { get; }
            public double WriteMs { get; }
            public List<string>? Deleted { get; }
            public string? Error { get; }
        }
    }
}
