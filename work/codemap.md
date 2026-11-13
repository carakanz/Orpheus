# Карта кода (обновлять при правке файла)
Мега-файлы: метод:строка. Строки дрейфуют: сначала grep, здесь границы ±5.

## Simulation*.cs - src/Orpheus.Core/Sim (P-002: partial по подсистемам, ядро + 10 файлов; строки сверены 13.11)
Ядро Simulation.cs (542): EntityInput :12, поля/константы :63-156, ctor :157, RefillPools :219, DayScale :243, SetWorldTime :246, Step :292 (полный такт, порядок фаз), ExpireEntry :431, константы подсистем :420-528, AddEvent :530.
Simulation.Entities.cs: GetEntity :18 · SlotOf :30 · NearestCharacterWithin :58 · Respawn :81 · NearestCharacterSlot :95 · AddEntity :124/:138 · AddEnemy :153 · RemoveEntity :168 · AddEntityWithId :196
Simulation.Input.cs: ApplySlot :18 · CopyPendingInputs :30 · DesiredDirection :45 · SetInput :58 · SetInputAt :71 · SetFramesAt :95
Simulation.Inventory.cs: SetInventory :14
Simulation.Edits.cs: AddExpire :17 · StepExpiry :44 · GiveBuildRefund :81 · DamageBuilding :112 · CheckCreatures :156 · GiveDigLoot :189 · TileAndNeighborsFree :207 · TileOf :227 · RequestPlayerEdit :238 · Enqueue :244 · RequestEdit :271/:275 · CostOf :282 · RefundCost :294 · ApplyEditBatches :310 · PreEdit :418
Simulation.EditQueue.cs: SortByChunkThenArrival :19 · Compare :59 · структура EditRequest :62
Simulation.Doors.cs: BeginDoorOpen :18 · BeginDoorClose :43 · IndexOfDoor :109 · StepDoors :128 · PushEnemyOffTile :223 · TouchDoorDeadline :264 · AddDoor :283 · IndexDoors :322 · DoorLockUntil :345 · RequestCloseDoor :353 · CloseDoorNearest :376 · RemoveDoor :407
Simulation.Combat.cs: StartAttack :21 · ApplyStrike :57 · BeginWindup :151 · EnableAttackCycle :163 · ResolveMeleeStrike :189 · StepCombat :255 · StrikeTileBlocked :389 · StrikeLineClear :412
Simulation.Enemies.cs: Chase :24 · AddMeleeRogue :240 · StepAI :273 · UpdateTarget :393
Simulation.Movement.cs: ISqrt :18 · StepSeparation :50 · MoveToward :98 · NewPathScheduler :112 · ResetPathCaches :119 · IsBlocked :138
Simulation.Restore.cs: Restore :23/:32 · NextEntityId :~75 · RestoreDefaults :83

## Spawner.cs - partial Simulation (спавн)
Волны, точки появления, очередь спавна: методы по grep (класс Simulation :34).

## NetServer*.cs - src/Orpheus.Net/Session (P-002: partial по подсистемам, ядро + 6 файлов + Edits/Pause/Threats)
Ядро NetServer.cs (530): поля/константы :45-140, ctor :142, метрики-свойства :~185-380, ClockEpoch :265, WorldTimeTicks :271, Poll :382, OnConnected :403 / OnDisconnected :411 / OnReceive :435 (ITransportHandler). Остальное — grep по имени метода.
NetServer.Types.cs: PlayerState :18 · Player :24 · ViewState :166 · SentRing :195 · FrameWindow :238
NetServer.Input.cs: PlanFrames :15 · TrimTo :145 · TakeSticky :169 · UpdateLeadStats :187 · Advance :201 · DropSilentConnections :211 · StopStaleInputs :234 · OnInput :343
NetServer.Snapshots.cs: SendSnapshots :15 · SendInventoryTo :51 · SendSnapshotTo :62 · TryWriteDelta :148 · WriteFull :290 · BuildView :323 · Quantize :422 (IndexIn в ядре)
NetServer.Chunks.cs: CopyHeldChunks :15 · SyncChunks :43 · SendChunk :68 · BroadcastPatches :90 · OnChunkRequest :233
NetServer.Events.cs: BroadcastDeath :15 · BroadcastCombatEvents :42 · BroadcastClock :103
NetServer.Login.cs: CloseAll :15 · EntityOf :29 · TryGetLastFrame :40 · CreditsOf :87 · BacklogOf :103 · CountHostsInGame :142 · OnLogin :157 · LeaveWorld :319 · PlaceFor :338 · OnOpenForFriends :397 · Reject (grep)

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
- Предсказание: LocalPrediction.cs: ctor :89 · Sent :190 · Teleport :238 · Reconcile :255 (состояние для переигровки :366-386: с сервера — позиция, уворот, нокбэк; предсказанное для ack — удар, таймеры выносливости) · RenderDelta :416 · ErrorUnits :440 · счётчики :98-131
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

## Боты - src/Orpheus.Net/Session (клиентские сценарии, 1-026)
- NetBot.cs :28: маршрутизация сценариев по `--script` :122–157 (dodge/dig_line/build_room/night_camp), driving каждого кадра :201–217.
- BuildRoomBot.cs :30 (build_room): Next :218 (главный цикл, этапы Phase), OnReply :155 (ответы постановки), Stage :86, метрики GatherTicks/BuildTicks/DamageThroughDoor :95–131 (метрика урона :242); TryDigObstacle :385, PlaceArmed :945. Обходы пина (13.11): WarmupTicks 120, пин Close >45 т → диагональ, Enter >45 т + cheb 1 → F снаружи. Самооборона (13.11): ThreatFrame :795 (меч, слот 3, сектор Reach.SwordStrikeUnits) в Enter :677 / Close :716 / WaitDawn :761; F в Close каждый кадр; AttacksIssued :128.
- NightCampBot.cs :15 (night_camp): Next :57, TicksToDeath :29, KillerId :32.
- DigLineBot.cs :23 (dig_line, образец), DodgeBot.cs :25 (dodge).

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
