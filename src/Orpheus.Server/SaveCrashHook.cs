using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Orpheus.Platform.Pc;
using Orpheus.Save;

namespace Orpheus.Server
{
    /// <summary>
    /// Отладочный крючок приёмки 0.4 (<c>--debug-save-crash &lt;зерно&gt;</c>; docs/07 6.10 п. 1): по зерну выбирается
    /// сохранение этого запуска (1-е…3-е) и миг записи — случайное смещение в файле (60 %), «записано, не сброшено»,
    /// «сброшено, не переименовано», «переименовано, старые не удалены», «старые удалены». В этот миг процесс
    /// пишет в stderr <c>ORPHEUS-DEBUG crash-point …</c> и замирает — тест убивает его (TerminateProcess).
    /// Строки <c>save-begin</c> (номер, такт, хэш состояния) и <c>save-done</c> говорят тесту, что должно загрузиться.
    /// В обычной игре крючка нет.
    /// </summary>
    public sealed class SaveCrashHook : ISaveHook
    {
        public const string Marker = "ORPHEUS-DEBUG";

        private readonly TextWriter _out;
        private int _saveIndex;
        private PcWriteStage _stage;
        private bool _afterPrune;
        private double _offsetFraction;
        private int _begun;
        private ulong _target;

        public SaveCrashHook(ulong seed, TextWriter output)
        {
            _out = output;
            var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
            _saveIndex = 1 + rng.Next(3);
            double p = rng.NextDouble();
            _stage = p < 0.6 ? PcWriteStage.Writing : p < 0.7 ? PcWriteStage.Written : p < 0.8 ? PcWriteStage.Flushed : PcWriteStage.Moved;
            _afterPrune = p >= 0.9;
            _offsetFraction = rng.NextDouble();
        }

        private SaveCrashHook(TextWriter output) => _out = output;

        /// <summary>
        /// План по ключу сервера <c>--debug-save-plan &lt;миг&gt;[:&lt;процент файла&gt;][@&lt;номер сохранения&gt;]</c> — миг
        /// обрыва задаёт тест явно, а не жребием: приёмка 0.4 обязана проверить каждый из них. Миги:
        /// <c>writing</c> (писали файл, процент — до какого смещения успели), <c>written</c> (отдан ОС, не сброшен),
        /// <c>flushed</c> (сброшен, не переименован), <c>moved</c> (переименован, старые поколения не удалены),
        /// <c>pruned</c> (старые удалены).
        /// </summary>
        public static SaveCrashHook Fixed(string spec, TextWriter output)
        {
            string s = (spec ?? string.Empty).Trim().ToLowerInvariant();
            int saveIndex = 1;
            int at = s.IndexOf('@');
            if (at >= 0)
            {
                if (!int.TryParse(s.Substring(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out saveIndex) || saveIndex < 1)
                {
                    throw new ArgumentException($"--debug-save-plan: сохранение «{s.Substring(at + 1)}» — целое от 1.");
                }

                s = s.Substring(0, at);
            }

            int pctAt = s.IndexOf(':');
            string stage = pctAt < 0 ? s : s.Substring(0, pctAt);
            double fraction = 0.5;
            if (pctAt >= 0)
            {
                if (!int.TryParse(s.Substring(pctAt + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pct) || pct < 0 || pct > 100)
                {
                    throw new ArgumentException($"--debug-save-plan: смещение «{s.Substring(pctAt + 1)}» — целое 0…100.");
                }

                fraction = pct / 100d;
            }

            var hook = new SaveCrashHook(output);
            hook._saveIndex = saveIndex;
            hook._afterPrune = stage == "pruned";
            hook._stage = stage switch
            {
                "writing" => PcWriteStage.Writing,
                "written" => PcWriteStage.Written,
                "flushed" => PcWriteStage.Flushed,
                "moved" => PcWriteStage.Moved,
                "pruned" => PcWriteStage.Moved,
                _ => throw new ArgumentException($"--debug-save-plan: мига «{stage}» нет; есть writing, written, flushed, moved, pruned."),
            };
            hook._offsetFraction = fraction;
            return hook;
        }

        /// <summary>Описание плана для лога.</summary>
        public string Plan => $"сохранение {_saveIndex}, {(_afterPrune ? "после удаления старых" : _stage.ToString())}" +
                              (_stage == PcWriteStage.Writing ? $", смещение {_offsetFraction:P0}" : "");

        /// <summary>Повесить на хранилище.</summary>
        public void Attach(PcStorage storage) => storage.Probe = OnProbe;

        /// <summary>Повесить на хранилище сервера: крючок умеет только <see cref="PcStorage"/> (миги записи ПК).</summary>
        public void Attach(IPlatformStorage storage) =>
            Attach(storage as PcStorage ?? throw new ArgumentException("Отладочный крючок сохранений — только для PcStorage."));

        /// <summary>Мир загружен: строка <c>loaded номер такт хэш</c> — тест знает, с чего начали.</summary>
        public void OnLoaded(ulong number, long tick, ulong hash) => Line($"loaded {number} {tick} {hash:x16}");

        public void Line(string text)
        {
            lock (_out)
            {
                _out.WriteLine(Marker + " " + text);
                _out.Flush();
            }
        }

        public void OnBegin(ulong number, long tick, ulong hash)
        {
            _begun++;
            if (_begun == _saveIndex)
            {
                _target = number;
            }

            Line($"save-begin {number} {tick} {hash:x16}");
        }

        public void OnDone(ulong number)
        {
            if (number == _target && _afterPrune)
            {
                Hang("pruned", 0, 0);
            }

            Line($"save-done {number}");
        }

        private void OnProbe(string path, PcWriteStage stage, long offset, long total)
        {
            if (_target == 0 || _afterPrune || !path.EndsWith(WorldFolder.GenerationFileName(_target), StringComparison.Ordinal))
            {
                return;
            }

            if (stage != _stage)
            {
                return;
            }

            if (stage == PcWriteStage.Writing && offset < (long)(_offsetFraction * total))
            {
                return;
            }

            Hang(stage.ToString(), offset, total);
        }

        private void Hang(string stage, long offset, long total)
        {
            Line($"crash-point {stage} {offset} {total}");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
