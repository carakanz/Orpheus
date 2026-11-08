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
    /// и автосохранение там тоже раз в 2 минуты мира.
    /// </para>
    /// <para>
    /// Поводы (0-037; 6.4 «игрок вошёл или вышел — в течение 1 с», 2.3 «хозяин ушёл»): <see cref="Request"/> ставит
    /// «нужно сохранение», запись начинается на ближайшей границе такта (<see cref="AfterTick"/>) — несколько поводов
    /// одного такта дают одно сохранение. Идёт прошлая запись — повод ждёт её конца и пишется на первой границе такта
    /// после него, а не пропадает до автосохранения.
    /// </para>
    /// <para>
    /// Серия поводов: сохранение по поводу, начатое меньше чем через интервал автосохранения после начала серии, заменяет
    /// последнее поколение серии (<see cref="WorldFolder.WriteGeneration(ulong, ReadOnlySpan{byte}, ulong)"/>: новое
    /// поколение пишется тем же атомарным путём под новым номером, заменённое удаляется только после этого). Так частые
    /// входы и уходы дают не больше одного нового поколения за интервал и не вытесняют из трёх хранимых более старые.
    /// Автосохранение и остановка серию заканчивают.
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

        private string? _requested;
        private int _requests;
        private ulong _seriesNumber;
        private long _seriesStartTick;

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

        /// <summary>Из записанных — заменили последнее поколение серии поводов (0-037), а не добавили новое.</summary>
        public int Replaced { get; private set; }

        /// <summary>Повод ждёт записи: ближайшая граница такта или конец прошлой записи.</summary>
        public bool HasRequest => _requested != null;

        /// <summary>Идёт фоновая запись (итог ещё не забран).</summary>
        public bool Writing => _pending != null;

        /// <summary>После каждого такта (граница такта): забрать итог фоновой записи; пора или есть повод — начать новую.</summary>
        public void AfterTick(Simulation sim)
        {
            Poll(sim.Tick);
            bool due = sim.Tick - _lastSavedTick >= _intervalTicks;
            if (!due && _requested == null)
            {
                return;
            }

            if (due && _pending != null && WaitForPrevious)
            {
                _pending.Wait();
                Poll(sim.Tick);
            }

            if (_pending != null)
            {
                return; // повод (и автосохранение без ожидания) — на первой границе такта после конца прошлой записи
            }

            if (due)
            {
                Start(sim, "автосохранение", series: false); // снимок покрывает и повод, если он есть
            }
            else
            {
                Start(sim, _requested!, series: true);
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
        /// Повод сохранить, не дожидаясь интервала (6.4: игрок вошёл или вышел; 2.3: игра хоста упала и других игроков нет).
        /// Звать в потоке такта вне такта (вход и уход — из <c>NetServer.Poll</c>): запись начнётся на ближайшей границе
        /// такта, а идёт прошлая — на первой границе такта после её конца. Несколько поводов до начала записи — одно
        /// сохранение (причина — первого).
        /// </summary>
        public void Request(string reason)
        {
            _requested ??= reason;
            _requests++;
        }

        /// <summary>
        /// Остановка сервера: дождаться фоновой записи, затем сохранить текущий такт, если он новее записанного или после
        /// снимка был повод (уход в том же такте, что и остановка).
        /// </summary>
        public void SaveOnStop(Simulation sim)
        {
            if (_pending != null)
            {
                _pending.Wait();
                Poll(sim.Tick);
            }

            if (_requested == null && sim.Tick == _lastSavedTick && _lastTickSaved)
            {
                return;
            }

            Start(sim, "остановка", series: false);
            _pending!.Wait();
            Poll(sim.Tick);
        }

        private void Start(Simulation sim, string reason, bool series)
        {
            var watch = Stopwatch.StartNew();
            WorldSaveData data = WorldSave.Capture(sim, _baseSha, _name, Characters?.Invoke());
            double captureMs = watch.Elapsed.TotalMilliseconds;
            ulong number = _folder.NextNumber;
            long tick = sim.Tick;
            _lastSavedTick = tick;
            _lastTickSaved = false;
            int requests = _requests;
            _requested = null;
            _requests = 0;

            // Серия поводов (0-037): последнее записанное — поколение этой серии, и серия моложе интервала — заменить его.
            ulong replaces = series && _seriesNumber != 0 && _seriesNumber == LastNumber && tick - _seriesStartTick < _intervalTicks
                ? _seriesNumber
                : 0;
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
                    List<string> deleted = folder.WriteGeneration(number, bytes, replaces);
                    crash?.OnDone(number);
                    return new SaveOutcome(number, tick, reason, series, replaces, requests, bytes.Length, data.ChunkCoords.Length, captureMs, w.Elapsed.TotalMilliseconds, deleted, null);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    return new SaveOutcome(number, tick, reason, series, replaces, requests, 0, 0, captureMs, w.Elapsed.TotalMilliseconds, null, e.Message);
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
                // Последнее целое поколение осталось; игра идёт (6.8 «Диск переполнен»). Повтор — через интервал;
                // серия поводов не меняется: заменять по-прежнему можно только её последнее записанное поколение.
                _log.Error(tick, "save", "Мир не сохраняется: " + r.Error, ("number", r.Number), ("reason", r.Reason));
                return;
            }

            Written++;
            LastNumber = r.Number;
            _lastTickSaved = r.Tick == _lastSavedTick;
            if (r.Series)
            {
                if (r.Replaces == 0)
                {
                    _seriesStartTick = r.Tick;
                }
                else
                {
                    Replaced++;
                }

                _seriesNumber = r.Number;
            }
            else
            {
                _seriesNumber = 0;
            }

            _log.Info(tick, "save", "сохранено", ("number", r.Number), ("tick", r.Tick), ("reason", r.Reason),
                ("requests", r.Requests), ("replaces", r.Replaces),
                ("bytes", r.Bytes), ("chunks", r.Chunks), ("captureMs", r.CaptureMs), ("writeMs", r.WriteMs),
                ("deleted", r.Deleted != null && r.Deleted.Count > 0 ? string.Join(",", r.Deleted) : ""));
        }

        private sealed class SaveOutcome
        {
            public SaveOutcome(
                ulong number, long tick, string reason, bool series, ulong replaces, int requests, int bytes, int chunks, double captureMs, double writeMs,
                List<string>? deleted, string? error)
            {
                Number = number;
                Tick = tick;
                Reason = reason;
                Series = series;
                Replaces = replaces;
                Requests = requests;
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
            public bool Series { get; }
            public ulong Replaces { get; }
            public int Requests { get; }
            public int Bytes { get; }
            public int Chunks { get; }
            public double CaptureMs { get; }
            public double WriteMs { get; }
            public List<string>? Deleted { get; }
            public string? Error { get; }
        }
    }
}
