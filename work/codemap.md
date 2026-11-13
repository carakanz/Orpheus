# Карта кода (обновлять при правке файла)
Мега-файлы: метод:строка. Строки дрейфуют: сначала grep, здесь границы ±5.

## Simulation.cs - src/Orpheus.Core/Sim (сердце: шаги, правила, двери, бой)
Здесь: сущности, вход, шаг, правки клеток (двери), день, бой. Снимы строит NetServer, не здесь.
Сущности: AddEntity :281 · AddEntity(in Mover) :296 · AddEnemy :311 · AddEntityWithId :326 · RemoveEntity :363 · Restore :393/:402 · SlotOf :494 · RefillPools :219
Вход: SetInput :538 · SetInputAt :551 · SetFramesAt :578 · SetInventory :607 · CopyPendingInputs :623
Шаг: Step :689 - полный такт симуляции; движение, двери, спавн идут отсюда.
Правки/двери: RequestEdit :643/:647 · RequestPlayerEdit :657 · DoorLockUntil :1169 (read-only, 1-028) · RequestCloseDoor :1286 · BeginDoorClose :1330 (ветка продления замка до проверки — 1-028) · DamageBuilding :1051 · автозакрытие дверей :1470-1517 (TouchDoorDeadline :1500, закрытие :1517)
День: WorldTime :234 · SetWorldTime :246 · DayScale :243 · DayScaleDefault :237 · рост часов в Step :808
Бой: ApplyStrike :2607 · StartAttack :2705 · EnableAttackCycle :2734 · AddMeleeRogue :1862
Прочее: IsBlocked :2861 · класс partial: спавн в Spawner.cs
Остальное в Simulation.cs (поля, приватные, структуры): grep по имени метода.

## Spawner.cs - partial Simulation (спавн)
Волны, точки появления, очередь спавна: методы по grep (класс Simulation :34).

## NetServer.cs - src/Orpheus.Net/Session (сессия сервера)
Здесь: логин, вход игроков, рассылка снимов, чанки, бой.
Вход: Poll :754 · PlanFrames :773 · TrimTo :896 · UpdateLeadStats :936 · Advance :949 · DropSilentConnections :958 · StopStaleInputs :980 · OnInput :2048
Снимы: SendSnapshots :1010 · SendSnapshotTo :1056 · TryWriteDelta :1137 · WriteFull :1272 · BuildView :1300 · Quantize :1394 · IndexIn :1406 · Add :1646
Чанки: SyncChunks :1426 · SendChunk :1450 · BroadcastPatches :1471 · OnChunkRequest :1613 · CopyHeldChunks :699
Бой: BroadcastDeath :1544 · BroadcastCombatEvents :1570 · SendInventoryTo :1045 · BroadcastClock :1629
Логин: OnLogin :1792 · Reject :2170 · LeaveWorld :1947 · OnOpenForFriends :2023 · CountHostsInGame :1778 · CloseAll :655
Транспорт (ITransportHandler): OnConnected :1652 · OnDisconnected :1660 · OnReceive :1684
Доступ: EntityOf :668 · TryGetLastFrame :678 · CreditsOf :724 · BacklogOf :739 · WorldTimeTicks :542 · ClockEpoch :536
Счётчики статистики: строки :458-626.

## NetServer* (парциальные)
- Edits (.Edits.cs): OnTileRequest :86 · DropDig :142 · ApplyPendingDig :158 · DigKeyOf :197 · OnPlace :209 · OnCloseDoor :286 · AdvanceEdits :302 · CompleteDig :329 · SendEditReplies :359 · Reply :416 · FindByEntity :431
- Pause (.Pause.cs): Paused :24 · PauseTick :27 · PausedBy :30 · SetPaused :53 · OnPauseRequest :55 · ChangePause :66 · KeepLagAfterPause :107 · StateMessage :118 · SendPauseTo :127 · ClearPauseIfEmpty :139 · SendPausedSnapshots :154
- Threats (.Threats.cs): ServerFrame :45 · InputWaitFrames :51 · Threats :60 · SetDummy :87 · StepDummy :95 · ResolveThreats :124 · HasPendingThreat :187 · StepEnemyThreats :207 · IsEnemyAiThreat :237 · Concerns :247 · SkipUnresolved :252 · Decide :260 · AttackRawOf :315 · SendSwings :330

## NetClient.cs - src/Orpheus.Net/Session (сессия клиента)
Здесь: состояние, кадры, приём снимов, часы.
Ядро: ctor :122 · State :135 · World :141 · Accept :156 · Latest :183 · HasSnapshot :185 · ServerTick :188 · Frame :191 · Clock :194 · WorldTimeClock :200
Отправка: OwnFrameAt :117 · RequestOpenForFriends :312 · Poll :325 · FramesDue :349 · SendFrame :368 · Disconnect :408
Транспорт (ITransportHandler): OnConnected :412 · OnDisconnected :424 · OnReceive :462
Приём: OnAccept :562 · OnWorld :593 · RequestChunk :620 · OnSnapshot :626 · OnDelta :645 · CommitSnapshot :724
Опережение: OnLeadReport :678 · SetLeadTarget :695 · StepLead :706 · LeadFrames :99 · LeadTarget :102 · DelayFrames :111 · HighLatency :114
Счётчики: строки :203-292.

## NetClient* (парциальные)
- Edits (.Edits.cs): Digging :68 · DigPredicted :71 · DigProgress :77 · StartDig :99 · StopDig :120 · Place :138 · CloseDoor :211 · NextRequest :236 · SendRequest :247 · OnFrameSent :256 · OnTileReply :337 · DigKeyOf :403 · NeededFrames :416
- Pause (.Pause.cs): Paused :22 · PauseTick :25 · PausedBy :28 · RequestPause :52 · OnPauseState :64 · ResumeIfSnapshotNewer :104 · Resume :113
- Предсказание: LocalPrediction.cs: ctor :89 · Sent :190 · Teleport :238 · Reconcile :255 · RenderDelta :407 · ErrorUnits :431 · счётчики :98-131
- Часы: WorldClock.cs: класс :21 · EstimateX256 :67 · TicksAt :86 · PhaseAt :89 (это WorldTimeClock клиента). ClientClock.cs: класс :21 · Estimate :82 · Hold :97 · OwdUs :58 · Rate :64.

## Snapshot.cs - src/Orpheus.Net/Protocol (формат снимов)
Здесь: битовая упаковка сущностей и себя, полный сним, дельты.
Сущности: SnapshotEntity :11 · SelfState :49 (Write :151, биты :70-119)
Раскладка: SnapshotLayout :253 (QuantShift :253, EntityBits :307, SelfBits :315, MaxEntitiesInBudget :318, SizeFor :321)
Полный сним: Snapshot :346 (HeaderBytes :349, MaxEntities :352, EpochOffset :357, WorldTimeOffset :360, TryFind :388, CopyTo :404, Write :417, Read :435)
Writer: SetClockEpoch :534 · SetWorldTime :537 · TryAdd :547 · Finish :568
Дельта: SnapshotDeltaMessage :608 (HeaderBytes :611, Read :671, ApplyTo :788)
DeltaWriter: TryAddRemoved :965 · TryAddUpdated :979 · TryAddAdded :1030 · Finish :1052

## WorldSave.cs - src/Orpheus.Save (сейв мира)
Здесь: снятие состояния мира, загрузка, восстановление в симуляцию.
Сейв: WorldSaveData :19 (поля :40-65) · Capture :141
Загрузка: LoadedWorldState :71 (Number :85, FormatVersion :106, WorldTime :109) · ReadReplaces :370 · Load :410
Восстановление: RestoreInto :112 · ReadCharacter :613 · ReadEntity :756
Контейнер и записи: SaveContainer.cs, Records.cs.

## NetGame* - client/Assets/Scripts (клей Unity)
- NetGame.cs: ctor :130 · StartConnect :154 · Connect :180 · PollLocalServer :212 · OpenForFriendsFrame :250 · Frame :282 · Shutdown :375 · BuildWorld :399 · OnSnapshot :447 · SendInput :474 · ComputeAim :539 · Render :633 · UpdateZoom :774 · UpdateHud :825 · Fail :1034 · Quit :1042
- Edits (.Edits.cs): OnInventory :41 · HeldItem :44 · BagHud :53 · UpdateEdits :115 · CloseNearestDoor :172 · SmokeEdit :257 · TryFindFreePlaceTile :325 · RemoteNear :367 · OnRolledBack :408 · RenderFlashes :441 · EditHud :462
- Pause (.Pause.cs): PauseFrame :49 · ShowPause :110 · SmokePause :169 · SmokePauseDone :259
- Thread (.Thread.cs): StartThreadServer :20 · PollThreadServer :34 · ShutdownThreadServer :75

## ServerHost.cs - src/Orpheus.ServerHost (headless-сервер)
Бег: Run :183 · RunCore :200 · SaveAfterError :440 · SetUpHost :474 · Ready :540 · ReadyMetrics :528 · CloseWorld :553 · CreateNetServer :593 · Loop :628 · FinishMetrics :812 · Verify :820 · GeneratorFor :171
Мир: OpenWorld :891 · LoadLatest :971 · SpawnPoint :1012/:1018 · DefaultSpawnCenter :1025 · SeamSpawnCenter :1033 · SpawnCenter :1063 · SpawnBots :1088 · CountBots :1105 · BotsText :1116

## Остальное в src/Orpheus.Core/Sim/ (по одной строке)
- StateHash.cs: хэш состояния (контрольные суммы сверки реплеев).
- Spawner.cs: partial Simulation: спавн, волны, точки появления.
- SimEvent.cs: события симуляции (звук, эффекты, удары).
- SimEntity.cs: сущность: статус, мувер, инвентарь.
- Items.cs: виды предметов и их параметры.
- Inventory.cs: инвентарь: слоты, выбор, передача.
- InputJournal.cs: журнал ввода (реплей, сверка сервера).
- InputFrame.cs: кадр ввода (направление, кнопки, прицел, слот).
- EntityChunkGrid.cs: сетка чанков сущностей (быстрый поиск по тайлам).
- EnemyBrain.cs: ИИ врагов (цель, преследование, атака).
- DigLoot.cs: добыча при копании (что из чего падает).
- CharacterRecord.cs: запись персонажа (сквозной сейв).
- CharacterNames.cs: имена персонажей.

## Остальное в src/Orpheus.Save/
- WorldFolder.cs: папка мира: набор файлов и структура на диске.
- SaveSections.cs: секции сейва: виды, границы.
- SaveModule.cs: модуль сейва: регистрация версионных данных.
- SaveContainer.cs: контейнер: заголовок, CRC, порядок секций.
- Records.cs: записи: персонажи, сущности, мета.
- IPlatformStorage.cs: интерфейс платформенного хранилища (пути, потоки).
- HostLockFile.cs: лок-файл хоста (чтобы два сервера не писали в один мир).
- Crc32.cs: CRC32 для целостности контейнера.
