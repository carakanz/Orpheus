using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Спавнер и исчезновение врагов (карточка 1-009; docs/02 8.5, 8.6, 8.8; docs/01 6 «Свет и появление
    /// врагов»; docs/07 3.5, 4.5). Часть <see cref="Simulation"/> — серверное правило: <see cref="SpawnEnabled"/>
    /// включает сервер (<see cref="ServerHost"/>), клиент и предсказание молчат — появления приходят снимками.
    /// <para>
    /// 8.5 «Появление»: сервер обходит игроков по кругу; у игрока, чья окрестность 40 т ниже лимита
    /// (день 5 / ночь 8 на игрока, группа с пересекающимися окрестностями ×(1 + 0,5(n − 1)) вверх до целого),
    /// попытка раз в 8 с днём / 4 с ночью (линейно в сумерки): до 10 случайных тайлов в кольце 36–46 т по тору —
    /// проходимый, не построенный пол игрока, не в замкнутой стенами области (заливка ≤ 256), свет построенных
    /// источников &lt; 0,5, не ближе 16 т к точке старта/Очагу, не ближе 36 т ни к одному игроку.
    /// Появившийся враг — «Интерес» 20 с: идёт шагом в сторону своего игрока (8.3).
    /// </para>
    /// <para>
    /// 8.6 «Сутки»: множитель от фона — здоровье на спавне и удар ×1,25 ночью (линейно в сумерки), радиус
    /// обнаружения ×1,5; днём ровно ×1 (фон 1,0). Ночное здоровье даётся на спавне и с рассветом не теряется.
    /// </para>
    /// <para>
    /// 8.8 «Исчезновение»: враг с мозгом без цели исчезает, если 30 с подряд дальше 80 т от всех игроков
    /// (цель — серия прерывается: «идёт бой») или его чанк вышел из активной зоны сервера (9 × 9 у всех).
    /// Без дропа и анимации; удаление — вне фаз, от хвоста списка (как трупы).
    /// </para>
    /// <para>
    /// Детерминизм (11.5): выбор тайлов — <see cref="EnemyRng"/> от (номер игрока, такт, зерно мира); счётчики
    /// попыток — состояние сессии сервера (после загрузки сервер продолжает спавн по своим часам — так же,
    /// как кэш путей не переносится).
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        // ---- 8.5 «Появление»: числа правил ----

        /// <summary>Окрестность игрока для подсчёта врагов: 40 тайлов.</summary>
        public const int SpawnNeighborhoodTiles = 40;

        /// <summary>Кольцо появления: 36…46 тайлов от игрока (по тору).</summary>
        public const int SpawnRingMinTiles = 36;
        public const int SpawnRingMaxTiles = 46;

        /// <summary>Лимит врагов в окрестности: день 5, ночь 8 (на игрока; группа — формулой).</summary>
        public const int SpawnDayLimit = 5;
        public const int SpawnNightLimit = 8;

        /// <summary>Ритм попыток: день 8 с = 240 тактов, ночь 4 с = 120, линейно в сумерки/рассвет.</summary>
        public const int SpawnAttemptDayTicks = 240;
        public const int SpawnAttemptNightTicks = 120;

        /// <summary>Тайлов-кандидатов за попытку — не больше 10.</summary>
        public const int SpawnMaxTilesPerAttempt = 10;

        /// <summary>Минимальная дистанция до любого игрока: 36 тайлов (кольцо по тору).</summary>
        public const int SpawnMinPlayerTiles = 36;

        /// <summary>Минимальная дистанция до точки старта / Очага (<see cref="RespawnCenter"/>): 16 тайлов.</summary>
        public const int SpawnMinCenterTiles = 16;

        /// <summary>Запрет появления: освещённость построенными источниками ≥ 0,5 (512/1024, docs/01 6).</summary>
        public const int SpawnLightLimitX1024 = 512;

        /// <summary>«Интерес» после появления: 20 с = 600 тактов (8.3).</summary>
        public const int SpawnInterestTicks = 600;

        /// <summary>Заливка «замкнутости» (как <see cref="Pathing.PathFinder"/>): лимит 256 тайлов; меньше — область замкнута.</summary>
        public const int SpawnEnclosureFillLimit = 256;

        // ---- 8.6 «Сутки»: множители от фона ----

        /// <summary>Здоровье (на спавне) и удар: ×1,25 ночью, линейно в сумерки; фон 1024 → ровно ×1.</summary>
        public int NightMultX1024 => 1024 + (1024 - DayClock.BackgroundX1024(WorldTime)) * 256 / 717;

        /// <summary>Радиус обнаружения: ×1,5 ночью, линейно в сумерки; фон 1024 → ровно ×1.</summary>
        public int NightSightMultX1024 => 1024 + (1024 - DayClock.BackgroundX1024(WorldTime)) * 512 / 717;

        /// <summary>Здоровье/удар с ночным множителем, округление к ближайшему: (v × m + 512) / 1024 (35→44, 10→13).</summary>
        public static int ScaledByNight(int value, int multX1024) => (value * multX1024 + 512) / 1024;

        /// <summary>Радиус обнаружения с множителем: квадрат дистанции × (m/1024)², два шага без переполнения.</summary>
        public static long ScaledSight2(long sight2, int multX1024) => sight2 * multX1024 / 1024 * multX1024 / 1024;

        // ---- 8.8 «Исчезновение» ----

        /// <summary>Дальше скольких тайлов от всех игроков идёт отсчёт: 80.</summary>
        public const int DespawnFarTiles = 80;

        /// <summary>Сколько тактов подряд «дальше 80 т» до исчезновения: 30 с = 900.</summary>
        public const int DespawnFarTicks = 900;

        // ---- Состояние сессии сервера ----

        /// <summary>
        /// Включает серверные правила 8.5/8.8 (появление и исчезновение). По умолчанию выключено: сервер
        /// (<c>ServerHost</c>) включает, клиент и предсказание не спавнят сами — враги приходят снимками.
        /// Множители суток (8.6) работают всегда: днём они ровно ×1.
        /// </summary>
        public bool SpawnEnabled { get; set; }

        private int _spawnCursor;                 // обход игроков по кругу (8.5)
        private long _spawnNextTry = 1;           // такт следующей попытки

        /// <summary>Позиция в обходе игроков (8.5) — состояние сессии сервера, в сейв не пишется; тесты
        /// переносят его при перестроении <see cref="Simulation"/> (Restore), как и такт следующей попытки.</summary>
        public int SpawnCursor { get => _spawnCursor; set => _spawnCursor = value; }

        /// <summary>Такт следующей попытки появления — состояние сессии сервера, в сейв не пишется.</summary>
        public long SpawnNextTry { get => _spawnNextTry; set => _spawnNextTry = value; }
        private WorldPos[] _spawnPos = new WorldPos[64];
        private int[] _spawnIds = new int[64];
        private int _spawnPlayers;

        private ChunkCoord[] _zoneA = Array.Empty<ChunkCoord>(); // активная зона (07, 3.5): набор чанков
        private ChunkCoord[] _zoneB = Array.Empty<ChunkCoord>();
        private int _zoneCount;

        private int[] _fillId = Array.Empty<int>(); // заливка «замкнутости»: открытая адресация со штампом
        private int[] _fillGen = Array.Empty<int>();
        private int _fillStamp;

        /// <summary>Чанки активной зоны сервера (для снимков правил и тестов; отсортированы).</summary>
        public ReadOnlySpan<ChunkCoord> ActiveChunks => _zoneA.AsSpan(0, _zoneCount);

        /// <summary>
        /// Фаза 8 такта: активная зона, исчезновение (8.8), попытка появления (8.5). Вызывается в конце такта,
        /// вне фаз: <see cref="RemoveEntity"/> сдвигает список — как трупы, от хвоста; появления — обычными
        /// add-сущностями (протокол не растёт).
        /// </summary>
        private void StepSpawnAndDespawn(long tick)
        {
            if (!SpawnEnabled)
            {
                return;
            }

            CollectPlayers();
            UpdateZone();
            StepDespawn(tick);
            StepSpawnAttempt(tick);
        }

        /// <summary>Игроки такта (живые персонажи) в буфер по номерам сущностей — детерминированный порядок.</summary>
        private void CollectPlayers()
        {
            int n = 0;
            for (int i = 0; i < _entityCount; i++)
            {
                if (!_entities[i].IsCharacter || !_entities[i].Alive)
                {
                    continue;
                }

                if (n == _spawnPos.Length)
                {
                    Array.Resize(ref _spawnPos, n * 2);
                    Array.Resize(ref _spawnIds, n * 2);
                }

                _spawnPos[n] = _entities[i].Mover.Position;
                _spawnIds[n] = _entities[i].Id;
                n++;
            }

            _spawnPlayers = n;
        }

        /// <summary>Активная зона (07, 3.5): вход 7 × 7 вокруг чанка игрока, выход за 9 × 9 у всех.</summary>
        private void UpdateZone()
        {
            int cap = ActiveZone.MaxChunks(_shape, Math.Max(1, _spawnPlayers));
            if (_zoneA.Length < cap)
            {
                _zoneA = new ChunkCoord[cap];
                _zoneB = new ChunkCoord[cap];
            }

            if (_spawnPos.Length < _spawnPlayers)
            {
                _spawnPos = new WorldPos[_spawnPlayers];
                _spawnIds = new int[_spawnPlayers];
                CollectPlayers(); // переполнение между сбором и зоной — пересобрать в новый буфер
            }

            _zoneCount = ActiveZone.Update(_shape, _spawnPos.AsSpan(0, _spawnPlayers),
                _zoneA.AsSpan(0, _zoneCount), _zoneB);
            (_zoneA, _zoneB) = (_zoneB, _zoneA);
        }

        /// <summary>
        /// Исчезновение (8.8): враг с мозгом, живой, без цели — чанк вне активной зоны сразу; серия
        /// «дальше 80 т от всех игроков» 900 тактов подряд. Цель («идёт бой») прерывает серию; с целью — не
        /// исчезает. Порядок от хвоста: <see cref="RemoveEntity"/> сдвигает список.
        /// </summary>
        private void StepDespawn(long tick)
        {
            const long Far2 = (long)DespawnFarTiles * Torus.SubTilesPerTile * DespawnFarTiles * Torus.SubTilesPerTile;

            for (int i = _entityCount - 1; i >= 0; i--)
            {
                ref SimEntity e = ref _entities[i];
                if (e.Kind != SimEntity.KindEnemy || e.Brain.Archetype == EnemyArchetype.None || e.Health <= 0)
                {
                    continue; // заглушки/манекены (без мозга) и трупы — не трогаем; расставленные/неподвижные — 8.7
                }

                ref EnemyBrain b = ref e.Brain;
                ChunkCoord chunk = Torus.ChunkOf(_shape, e.Mover.Position);
                if (_zoneCount > 0 && !ChunkSets.Contains(_zoneA.AsSpan(0, _zoneCount), chunk))
                {
                    RemoveEntity(e.Id);
                    continue;
                }

                if (_spawnPlayers == 0)
                {
                    continue; // сервер без игроков: «далеко от всех» бессмысленно — враги ждут (зона уже пуста)
                }

                if (b.TargetId >= 0)
                {
                    b.FarSince = 0; // идёт бой — серия «дальше 80 т» прерывается
                    continue;
                }

                bool far = true;
                for (int p = 0; p < _spawnPlayers; p++)
                {
                    if (Torus.Dist2(_shape, e.Mover.Position, _spawnPos[p]) <= Far2)
                    {
                        far = false;
                        break;
                    }
                }

                if (!far)
                {
                    b.FarSince = 0;
                    continue;
                }

                if (b.FarSince == 0)
                {
                    b.FarSince = (int)tick;
                }
                else if (tick - b.FarSince + 1 >= DespawnFarTicks)
                {
                    RemoveEntity(e.Id);
                }
            }
        }

        /// <summary>
        /// Попытка появления (8.5): раз в 240 тактов днём / 120 ночью (линейно в сумерки) — следующий игрок
        /// по кругу; если его окрестность 40 т ниже лимита группы — до 10 тайлов кольца 36–46 т, первый годный
        /// тайл → «Ближний» с «Интересом» на своего игрока. Архетип: «Стрелок» (1-008) не реализован —
        /// таблица 2:1 вырождается в 100 % «Ближний» (записано в решениях карточки).
        /// </summary>
        private void StepSpawnAttempt(long tick)
        {
            int bg = DayClock.BackgroundX1024(WorldTime);
            int interval = SpawnAttemptDayTicks - (SpawnAttemptDayTicks - SpawnAttemptNightTicks) * (1024 - bg) / 717;
            if (tick < _spawnNextTry)
            {
                return;
            }

            _spawnNextTry = tick + interval;
            if (_spawnPlayers == 0)
            {
                return;
            }

            int pi = _spawnCursor % _spawnPlayers;
            _spawnCursor++;
            WorldPos at = _spawnPos[pi];
            int pid = _spawnIds[pi];

            int limit = SpawnLimitFor(pi);
            int live = CountEnemiesNear(at);
            if (live >= limit || _entityCount >= Capacity) // ёмкость симы (64) — спавн ждёт, исключений не поднимаем
            {
                return;
            }

            TileCoord pt = Torus.TileOf(_shape, at);
            ulong seed = _world.Seed;
            for (int k = 0; k < SpawnMaxTilesPerAttempt; k++)
            {
                ulong h = EnemyRng.Hash(unchecked(pid + k * 1021), tick, seed);
                int dx = (int)(h % (2 * (long)SpawnRingMaxTiles + 1)) - SpawnRingMaxTiles;
                int dy = (int)((h >> 7) % (2 * (long)SpawnRingMaxTiles + 1)) - SpawnRingMaxTiles;
                int r2t = dx * dx + dy * dy;
                if (r2t < SpawnRingMinTiles * SpawnRingMinTiles || r2t > SpawnRingMaxTiles * SpawnRingMaxTiles)
                {
                    continue; // мимо кольца
                }

                TileCoord tile = Torus.Wrap(_shape, new TileCoord(pt.X + dx, pt.Y + dy));
                if (!IsSpawnTileOk(tile))
                {
                    continue;
                }

                int id = AddMeleeRogue(EditRules.CenterOf(tile), NightMultX1024);
                int slot = SlotOf(id);
                ref EnemyBrain b = ref _entities[slot].Brain;
                b.CauseId = pid;
                b.State = EnemyState.Interest;
                b.StateTicks = 0;
                b.InterestUntil = tick + SpawnInterestTicks;
                return; // одна сущность за попытку
            }
        }

        /// <summary>
        /// Лимит окрестности игрока <paramref name="pi"/>: база (фаза суток) × (1 + 0,5 × (n − 1)) для группы
        /// с пересекающимися окрестностями (дистанция &lt; 80 т), вверх до целого (8.5). Плотность биома — 1,0
        /// до генератора.
        /// </summary>
        private int SpawnLimitFor(int pi)
        {
            int baseLimit = DayClock.PhaseOf(WorldTime) == DayPhase.Night ? SpawnNightLimit : SpawnDayLimit;
            const long Group2 = (long)SpawnNeighborhoodTiles * 2 * Torus.SubTilesPerTile * SpawnNeighborhoodTiles * 2 * Torus.SubTilesPerTile;

            int n = 1;
            for (int p = 0; p < _spawnPlayers; p++)
            {
                if (p != pi && Torus.Dist2(_shape, _spawnPos[pi], _spawnPos[p]) < Group2)
                {
                    n++;
                }
            }

            // ceil(base × (1024 + 512(n − 1)) / 1024) — целочисленно, без плавающей точки.
            return (baseLimit * (1024 + 512 * (n - 1)) + 1023) / 1024;
        }

        /// <summary>
        /// Живые враги с мозгом в окрестности игрока (8.5 «окрестность 40 т ниже лимита»): считаем до 46 т —
        /// внешний край кольца появления, чтобы каждый новый враг попадал в счёт сразу (иначе лимит
        /// перелетается спавнами на 41–46 т).
        /// </summary>
        private int CountEnemiesNear(in WorldPos at)
        {
            const long Near2 = (long)SpawnRingMaxTiles * Torus.SubTilesPerTile * SpawnRingMaxTiles * Torus.SubTilesPerTile;
            int n = 0;
            for (int i = 0; i < _entityCount; i++)
            {
                if (_entities[i].Kind == SimEntity.KindEnemy && _entities[i].Brain.Archetype != EnemyArchetype.None &&
                    _entities[i].Health > 0 &&
                    Torus.Dist2(_shape, at, _entities[i].Mover.Position) <= Near2)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// Годный тайл появления (8.5, docs/01 6): проходимый; не построенный пол (пол игрока); свет
        /// построенных источников &lt; 0,5; ≥ 16 т от точки старта/Очага; ≥ 36 т от каждого игрока; не внутри
        /// области, замкнутой стенами (заливка проходимых 4-соседей, лимит 256).
        /// </summary>
        public bool IsSpawnTileOk(TileCoord tile)
        {
            if (IsBlocked(_shape, tile))
            {
                return false;
            }

            if (FloorId.IsBuilt(_world.GetFloor(tile)))
            {
                return false; // пол, построенный игроком, — «дом», не место появления
            }

            if (_world.Lights.SourceLightX1024(tile) >= SpawnLightLimitX1024)
            {
                return false;
            }

            WorldPos center = EditRules.CenterOf(tile);
            const long MinCenter2 = (long)SpawnMinCenterTiles * Torus.SubTilesPerTile * SpawnMinCenterTiles * Torus.SubTilesPerTile;
            if (Torus.Dist2(_shape, center, RespawnCenter) < MinCenter2)
            {
                return false;
            }

            const long MinPlayer2 = (long)SpawnMinPlayerTiles * Torus.SubTilesPerTile * SpawnMinPlayerTiles * Torus.SubTilesPerTile;
            for (int p = 0; p < _spawnPlayers; p++)
            {
                if (Torus.Dist2(_shape, center, _spawnPos[p]) < MinPlayer2)
                {
                    return false;
                }
            }

            return !IsEnclosedArea(tile);
        }

        /// <summary>
        /// Замкнутость тайла (8.5 «не внутри замкнутой стенами области», вопрос карточки — лимит как в
        /// <see cref="Pathing.PathFinder"/>): заливка проходимых тайлов (4-соседи, тороидально) с лимитом 256.
        /// Заливка закончилась раньше лимита — область замкнута; достигла лимита — открытая.
        /// </summary>
        private bool IsEnclosedArea(TileCoord start)
        {
            if (_fillId.Length == 0)
            {
                _fillId = new int[1024];
                _fillGen = new int[1024];
            }

            int w = _shape.WidthUnits >> Torus.SubTileBits;
            int startId = start.Y * w + start.X;
            _fillStamp++;
            Span<int> queue = stackalloc int[SpawnEnclosureFillLimit];
            int n = 0;
            queue[n++] = startId;
            ProbeAdd(startId);

            int head = 0;
            while (head < n)
            {
                int id = queue[head++];
                int x = id % w, y = id / w;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    TileCoord nb = Torus.Wrap(_shape, new TileCoord(nx, ny));
                    int nid = nb.Y * w + nb.X;
                    if (ProbeHas(nid) || IsBlocked(_shape, nb))
                    {
                        continue;
                    }

                    ProbeAdd(nid);
                    if (n == SpawnEnclosureFillLimit)
                    {
                        return false; // открытая область — спавн разрешён
                    }

                    queue[n++] = nid;
                }
            }

            return true; // замкнуто
        }

        /// <summary>Открытая адресация заливки: линия с проверкой «штамп такта» (очистки нет).</summary>
        private bool ProbeHas(int id)
        {
            int s = id & 1023;
            while (_fillGen[s] == _fillStamp)
            {
                if (_fillId[s] == id)
                {
                    return true;
                }

                s = (s + 1) & 1023;
            }

            return false;
        }

        private void ProbeAdd(int id)
        {
            int s = id & 1023;
            while (_fillGen[s] == _fillStamp)
            {
                s = (s + 1) & 1023;
            }

            _fillGen[s] = _fillStamp;
            _fillId[s] = id;
        }
    }
}
