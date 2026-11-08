using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Orpheus.Save
{
    /// <summary>Повреждённый файл, перенесённый из папки мира (docs/07, 6.5 п. 5).</summary>
    public readonly struct DamagedFile
    {
        public readonly string Name;
        public readonly string MovedTo;
        public readonly string Reason;

        public DamagedFile(string name, string movedTo, string reason)
        {
            Name = name;
            MovedTo = movedTo;
            Reason = reason;
        }
    }

    /// <summary>Итог проверки папки мира при старте.</summary>
    public sealed class WorldRecovery
    {
        /// <summary>Новейшее целое поколение; null — поколений нет (свежий мир — только снимок генерации).</summary>
        public SaveFile? Latest { get; internal set; }

        /// <summary>Номер новейшего целого поколения по имени файла (0 — нет).</summary>
        public ulong LatestNumber { get; internal set; }

        /// <summary>Имя файла, из которого взято поколение (после продвижения <c>.tmp</c> — уже <c>.save</c>).</summary>
        public string? LatestName { get; internal set; }

        /// <summary>Новейшим оказался целый <c>.tmp</c>: данные были на носителе, переименование пропало — продвинут в <c>.save</c>.</summary>
        public bool PromotedTmp { get; internal set; }

        /// <summary>Битые файлы новее взятого — перенесены в <see cref="WorldFolder.DamagedDir"/>.</summary>
        public List<DamagedFile> Damaged { get; } = new List<DamagedFile>();

        /// <summary>Сколько файлов поколений проверено (SHA-256 и CRC).</summary>
        public int Checked { get; internal set; }

        /// <summary>
        /// Удалены при старте: поколения <c>.save</c> старше трёх последних (см. <see cref="WorldFolder.KeepGenerations"/>)
        /// и заменённое поколение серии, оставшееся после обрыва (<see cref="ReplacedLeftover"/>).
        /// </summary>
        public List<string> Pruned { get; } = new List<string>();

        /// <summary>
        /// Взятое поколение заменяло поколение серии (0-037), а то осталось в папке — обрыв между записью нового и удалением
        /// заменённого. Номер оставшегося (0 — нет): оно убирается первым и в «три последних» не считается.
        /// </summary>
        public ulong ReplacedLeftover { get; internal set; }
    }

    /// <summary>
    /// Папка мира (docs/07, 6.3 и 6.5): <c>world.base</c>, поколения <c>world.NNNNNN.save</c>, замок <c>world.lock</c>,
    /// битые — <c>backups/damaged/</c>. Всё — через <see cref="IPlatformStorage"/>.
    /// <list type="bullet">
    /// <item><b>Запись поколения</b> (<see cref="WriteGeneration"/>): новый файл со следующим номером атомарно
    /// (<see cref="IPlatformStorage.WriteAtomic"/>: <c>.tmp</c>, сброс, переименование со сбросом) — поверх
    /// последнего целого файла сервер не пишет никогда; только после этого удаляются поколения старше трёх последних.</item>
    /// <item><b>Старт</b> (<see cref="Recover"/>): <c>.tmp</c> не удаляется вслепую — все <c>world.*.save</c> и
    /// <c>world.*.save.tmp</c> перебираются от большего номера к меньшему, каждый проверяется (метка, CRC разделов,
    /// SHA-256); первый целый берётся, целый <c>.tmp</c> продвигается в <c>.save</c>; битые новее него переносятся
    /// в <c>backups/damaged/</c> (не удаляются). Все поколения битые — отказ без переносов: мир не должен молча
    /// откатиться к снимку генерации.</item>
    /// </list>
    /// Расхождение с карточкой 0-009 («битые — в <c>corrupt/</c>»): папка взята из документа (6.5 п. 5, 6.8, 6.10 п. 2) —
    /// <c>backups/damaged/</c>; записано в карточке.
    /// </summary>
    public sealed class WorldFolder
    {
        public const string BaseFileName = "world.base";
        public const string LockFileName = "world.lock";
        public const string BackupsDir = "backups";
        public const string DamagedDirName = "damaged";
        public const string GenerationPrefix = "world.";
        public const string GenerationSuffix = ".save";
        public const string TmpSuffix = ".tmp";

        /// <summary>Сколько последних поколений хранится (6.6).</summary>
        public const int KeepGenerations = 3;

        private readonly IPlatformStorage _storage;
        private ulong _maxNumber;

        /// <summary>
        /// Заменённые поколения серии (0-037), которые не удалось удалить (обрыв перед удалением, файл держит антивирус или
        /// копирование): <see cref="Prune"/> удаляет их первыми и не считает в «три последних» — иначе вместо лишнего
        /// поколения серии ушло бы самое старое поколение автосохранения.
        /// </summary>
        private readonly SortedSet<ulong> _leftovers = new SortedSet<ulong>();

        public WorldFolder(IPlatformStorage storage, string directory)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            Dir = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        /// <summary>Папка мира.</summary>
        public string Dir { get; }

        public IPlatformStorage Storage => _storage;

        /// <summary>Папка битых: <c>backups/damaged</c>.</summary>
        public string DamagedDir => Path.Combine(Dir, BackupsDir, DamagedDirName);

        public string BasePath => Path.Combine(Dir, BaseFileName);

        /// <summary>Номер следующего поколения: больше всех, что видели в папке.</summary>
        public ulong NextNumber => _maxNumber + 1;

        /// <summary>Имя поколения: <c>world.000127.save</c> (номер больше 999 999 — просто длиннее).</summary>
        public static string GenerationFileName(ulong number) =>
            GenerationPrefix + number.ToString("D6", CultureInfo.InvariantCulture) + GenerationSuffix;

        /// <summary>Разобрать имя поколения: <c>world.N.save</c> или <c>world.N.save.tmp</c>.</summary>
        public static bool TryParseGeneration(string name, out ulong number, out bool tmp)
        {
            number = 0;
            tmp = name.EndsWith(GenerationSuffix + TmpSuffix, StringComparison.Ordinal);
            string core = tmp ? name.Substring(0, name.Length - TmpSuffix.Length) : name;
            if (!core.StartsWith(GenerationPrefix, StringComparison.Ordinal) || !core.EndsWith(GenerationSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            string digits = core.Substring(GenerationPrefix.Length, core.Length - GenerationPrefix.Length - GenerationSuffix.Length);
            if (digits.Length == 0 || digits.Length > 19)
            {
                return false;
            }

            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < '0' || digits[i] > '9')
                {
                    return false;
                }
            }

            return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        // ---------- Замок ----------

        /// <summary>Взять замок мира; занят — <see cref="IOException"/> «мир уже открыт» (6.10 п. 7).</summary>
        public IDisposable Lock()
        {
            _storage.CreateDirectory(Dir);
            IDisposable? handle = _storage.TryLock(Path.Combine(Dir, LockFileName));
            if (handle == null)
            {
                throw new IOException($"Мир уже открыт: {Dir} держит другой сервер ({LockFileName}).");
            }

            return handle;
        }

        // ---------- Снимок генерации ----------

        public bool HasBase => _storage.FileExists(BasePath);

        /// <summary>Записать снимок генерации (один раз при создании, атомарно). Уже есть — отказ: мир не перезаписывается.</summary>
        public void CreateBase(ReadOnlySpan<byte> data)
        {
            _storage.CreateDirectory(Dir);
            if (HasBase)
            {
                throw new IOException($"Мир уже есть: {BasePath}. Создать заново — другая папка.");
            }

            _storage.WriteAtomic(BasePath, data);
        }

        public byte[] ReadBase()
        {
            if (!HasBase)
            {
                throw new IOException($"Нет мира: {BasePath}.");
            }

            return _storage.ReadAllBytes(BasePath);
        }

        // ---------- Поколения ----------

        /// <summary>Номера поколений <c>.save</c> в папке по возрастанию (без проверки содержимого).</summary>
        public ulong[] ListGenerations()
        {
            var numbers = new List<ulong>();
            string[] names = _storage.ListFiles(Dir);
            for (int i = 0; i < names.Length; i++)
            {
                if (TryParseGeneration(names[i], out ulong n, out bool tmp) && !tmp)
                {
                    numbers.Add(n);
                }
            }

            numbers.Sort();
            return numbers.ToArray();
        }

        /// <summary>Проверка папки при старте (6.5 п. 5). Описание — в заголовке класса.</summary>
        public WorldRecovery Recover()
        {
            var result = new WorldRecovery();
            var candidates = new List<(ulong Number, bool Tmp, string Name)>();
            string[] names = _storage.ListFiles(Dir);
            for (int i = 0; i < names.Length; i++)
            {
                if (TryParseGeneration(names[i], out ulong n, out bool tmp))
                {
                    candidates.Add((n, tmp, names[i]));
                    if (n > _maxNumber)
                    {
                        _maxNumber = n;
                    }
                }
            }

            // От большего номера к меньшему; при равном — сначала .save, потом .tmp.
            candidates.Sort((a, b) => a.Number != b.Number ? b.Number.CompareTo(a.Number) : a.Tmp.CompareTo(b.Tmp));

            var damaged = new List<(string Name, string Reason)>();
            int chosen = -1;
            SaveFile? file = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                string path = Path.Combine(Dir, candidates[i].Name);
                byte[] data = _storage.ReadAllBytes(path);
                SaveFile f = data.Length > SaveContainer.MaxFileBytes
                    ? DamagedBySize(data.Length)
                    : SaveContainer.Read(data);
                result.Checked++;
                if (f.Status == SaveCheckStatus.TooNew)
                {
                    throw new InvalidDataException($"{candidates[i].Name}: {f.Error}");
                }

                if (f.IsOk)
                {
                    chosen = i;
                    file = f;
                    break;
                }

                damaged.Add((candidates[i].Name, f.Error ?? "повреждён"));
            }

            if (chosen < 0)
            {
                if (candidates.Count > 0)
                {
                    throw new InvalidDataException(
                        $"Все поколения мира повреждены ({candidates.Count}): {Describe(damaged)}. " +
                        "Файлы не тронуты; восстановите мир из резервной копии (6.6).");
                }

                return result;
            }

            for (int i = 0; i < damaged.Count; i++)
            {
                string to = MoveToDamaged(damaged[i].Name);
                result.Damaged.Add(new DamagedFile(damaged[i].Name, to, damaged[i].Reason));
            }

            (ulong number, bool isTmp, string name) = candidates[chosen];
            if (isTmp)
            {
                string final = name.Substring(0, name.Length - TmpSuffix.Length);

                _storage.MoveDurable(Path.Combine(Dir, name), Path.Combine(Dir, final), replaceExisting: false);
                name = final;
                result.PromotedTmp = true;
            }

            result.Latest = file;
            result.LatestNumber = number;
            result.LatestName = name;

            // Взятое поколение заменяло поколение серии, а то осталось (обрыв до удаления) — оно лишнее: убрать его, а не
            // старейшее автосохранение. Верим полю, только если файл лежит под своим номером (не копия из резервных,
            // положенная руками, 6.6) и заменённое — ближайшее поколение ниже взятого.
            ulong replaced = ReplacedBy(file!);
            if (replaced != 0 && file!.Header.Number == number && replaced < number && NearestBelow(number) == replaced)
            {
                _leftovers.Add(replaced);
                result.ReplacedLeftover = replaced;
            }

            // «Три последних» считаем по папке, а не только по удачной записи: после обрыва prune не
            // успевает, и при повторных обрывах папка растёт (продвинутые .tmp, ручные правки).
            result.Pruned.AddRange(Prune());
            return result;
        }

        /// <summary>
        /// Записать поколение <paramref name="number"/> атомарно, затем удалить поколения старше трёх последних.
        /// Возвращает имена удалённых. Номер должен быть больше всех в папке (<see cref="NextNumber"/>).
        /// </summary>
        public List<string> WriteGeneration(ulong number, ReadOnlySpan<byte> data) => WriteGeneration(number, data, 0);

        /// <summary>
        /// То же, но поколение <paramref name="replaces"/> (0 — нет) заменяется новым (0-037: частые сохранения по поводу
        /// «вход/уход» не плодят поколений): новое пишется атомарно под новым номером, как всегда, и только после этого
        /// — оно уже целое на диске — удаляется заменённое, затем поколения старше трёх последних. Поверх целого файла
        /// не пишется ничего (6.5). Пропал свет до удаления — в папке останется и заменённое: новое помнит его номер
        /// (поле МИР <see cref="SaveSections.WorldField.Replaces"/>, пишет вызывающий), и старт уберёт именно его
        /// (<see cref="Recover"/>). Удаление не вышло (файл держит антивирус) — его уберёт следующая очистка, первым.
        /// <para>Порядок «сначала новое, потом удаление» держат тесты обрыва внутри заменяющего сохранения
        /// (<c>SaveEventTests.…CrashInsideReplacingSave…</c>, <c>SaveKillTests.…ReplacingSave…</c>).</para>
        /// </summary>
        public List<string> WriteGeneration(ulong number, ReadOnlySpan<byte> data, ulong replaces)
        {
            if (number <= _maxNumber)
            {
                throw new ArgumentOutOfRangeException(nameof(number), number, $"Номер поколения должен быть больше {_maxNumber}: поверх прежних не пишем.");
            }

            if (replaces >= number)
            {
                throw new ArgumentOutOfRangeException(nameof(replaces), replaces, $"Заменить можно только поколение старше {number}.");
            }

            _storage.CreateDirectory(Dir);
            _storage.WriteAtomic(Path.Combine(Dir, GenerationFileName(number)), data);
            _maxNumber = number;
            if (replaces != 0)
            {
                // Новое целое на диске — заменённое лишнее. Удаление не вышло — Prune уберёт его первым (_leftovers).
                _leftovers.Add(replaces);
            }

            return Prune();
        }

        /// <summary>
        /// Удалить лишнее: сначала заменённые поколения серии, оставшиеся в папке, затем поколения <c>.save</c> старше
        /// <see cref="KeepGenerations"/> последних (заменённые в их счёт не входят). Ошибка удаления — не беда: пропускаем.
        /// </summary>
        public List<string> Prune()
        {
            var deleted = new List<string>();
            foreach (ulong n in new List<ulong>(_leftovers))
            {
                string name = GenerationFileName(n);
                if (!_storage.FileExists(Path.Combine(Dir, name)))
                {
                    _leftovers.Remove(n);
                }
                else if (TryDelete(name))
                {
                    _leftovers.Remove(n);
                    deleted.Add(name);
                }
            }

            var numbers = new List<ulong>(ListGenerations());
            numbers.RemoveAll(n => _leftovers.Contains(n));
            for (int i = 0; i < numbers.Count - KeepGenerations; i++)
            {
                string name = GenerationFileName(numbers[i]);
                if (TryDelete(name))
                {
                    deleted.Add(name);
                }
            }

            return deleted;
        }

        /// <summary>Заменённое поколение из поля целого файла (0 — нет; поле не читается — тоже 0: лишнее уберётся как обычно).</summary>
        private static ulong ReplacedBy(SaveFile file)
        {
            try
            {
                return WorldSave.ReadReplaces(file);
            }
            catch (InvalidDataException)
            {
                return 0;
            }
        }

        /// <summary>Ближайшее поколение <c>.save</c> ниже <paramref name="number"/> (0 — нет).</summary>
        private ulong NearestBelow(ulong number)
        {
            ulong best = 0;
            foreach (ulong n in ListGenerations())
            {
                if (n < number && n > best)
                {
                    best = n;
                }
            }

            return best;
        }

        /// <summary>Удалить поколение <paramref name="name"/>; нет такого файла или удалить не вышло — false.</summary>
        private bool TryDelete(string name)
        {
            string path = Path.Combine(Dir, name);
            if (!_storage.FileExists(path))
            {
                return false; // имя без ведущих нулей, положенное руками, — не трогаем
            }

            try
            {
                _storage.Delete(path);
                return true;
            }
            catch (IOException)
            {
                // файл держит копирование — удалим в следующий раз
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        private string MoveToDamaged(string name)
        {
            string dir = DamagedDir;
            _storage.CreateDirectory(dir);
            string target = Path.Combine(dir, name);
            for (int k = 1; _storage.FileExists(target); k++)
            {
                target = Path.Combine(dir, name + "." + k.ToString(CultureInfo.InvariantCulture));
            }

            _storage.MoveDurable(Path.Combine(Dir, name), target, replaceExisting: false);
            return target;
        }

        private static SaveFile DamagedBySize(int length)
        {
            SaveFile f = SaveContainer.Read(Array.Empty<byte>());
            f.Error = $"Файл {length} байт — больше предела {SaveContainer.MaxFileBytes}.";
            return f;
        }

        private static string Describe(List<(string Name, string Reason)> damaged)
        {
            var parts = new string[damaged.Count];
            for (int i = 0; i < damaged.Count; i++)
            {
                parts[i] = damaged[i].Name + " — " + damaged[i].Reason;
            }

            return string.Join("; ", parts);
        }
    }
}
