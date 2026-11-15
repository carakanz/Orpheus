# Карта кода (обновлять при правке файла)
Мега-файлы: метод:строка. Строки дрейфуют: сначала grep, здесь границы ±5.

## Simulation*.cs - src/Orpheus.Core/Sim (P-002: partial по подсистемам, ядро + 10 файлов; строки сверены 13.11)
Ядро Simulation.cs (538): EntityInput :12, поля/константы :63-156, ctor :157, RefillPools :219, DayScale :243, SetWorldTime :246, Step :292 (полный такт, порядок фаз), ExpireEntry :431, константы подсистем :420-528, AddEvent :530.
Simulation.Entities.cs: GetEntity :18 · SlotOf :30 · NearestCharacterWithin :58 · Respawn :81 · NearestCharacterSlot :95 · AddEntity :124/:138 · AddEnemy :153 · RemoveEntity :168 · AddEntityWithId :196
Simulation.Input.cs: ApplySlot :18 · CopyPendingInputs :30 · DesiredDirection :45 · SetInput :58 · SetInputAt :71 · SetFramesAt :95
Simulation.Inventory.cs: SetInventory :14
Simulation.Edits.cs: AddExpire :17 · StepExpiry :44 · GiveBuildRefund :81 · DamageBuilding :112 · CheckCreatures :156 · GiveDigLoot :189 · TileAndNeighborsFree :207 · TileOf :227 · RequestPlayerEdit :238 · Enqueue :244 · RequestEdit :271/:275 · CostOf :282 · RefundCost :294 · ApplyEditBatches :310 · PreEdit :418
Simulation.EditQueue.cs: SortByChunkThenArrival :19 · Compare :59 · структура EditRequest :62
Simulation.Doors.cs: BeginDoorOpen :18 · BeginDoorClose :43 · IndexOfDoor :109 · StepDoors :128 · PushEnemyOffTile :223 · TouchDoorDeadline :264 · AddDoor :283 · IndexDoors :322 · DoorLockUntil :345 · RequestCloseDoor :353 · CloseDoorNearest :376 · RemoveDoor :407
Simulation.Combat.cs: StartAttack :21 · ApplyStrike :57 · BeginWindup :151 · EnableAttackCycle :163 · ResolveMeleeStrike :189 · StepCombat :255 · StrikeLineClear приземления :333 · StrikeTileBlocked :395 · StrikeLineClear :420
Simulation.Enemies.cs: Chase :24 · AddMeleeRogue :240 · StepAI :273 · UpdateTarget :393
Simulation.Movement.cs: ISqrt :18 · StepSeparation :50 · MoveToward :98 · NewPathScheduler :112 · ResetPathCaches :119 · IsBlocked :138
Simulation.Restore.cs: Restore :23/:32 · NextEntityId :~75 · RestoreDefaults :83
Simulation.Piles.cs (E2-1): константы :18-42 · Piles :58 · AddPile :67 · RestorePiles :71 · RequestBagOp :101 · StepItems :119 (фаза 5.2 такта) · NearestPicker :166 · ApplyBagOp :188 · DropOnTile :222 · SpawnPile :238 · FreeGroundNear :296

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
- BuildRoomBot.cs :30 (build_room): Next :218 (главный цикл, этапы Phase), OnReply :155 (ответы постановки), Stage :86, метрики GatherTicks/BuildTicks/DamageThroughDoor :95–131 (метрика урона :242); TryDigObstacle :385, PlaceArmed :945. Обходы пина (13.11): WarmupTicks 120, пин Close >45 т → диагональ, Enter >45 т + cheb 1 → F снаружи. Самооборона (13.11): ThreatFrame :795 (меч, слот 3, сектор Reach.SwordStrikeUnits) в Enter :677 / Close :716 / WaitDawn :761; F в Close каждый кадр; AttacksIssued :128. C-004: смерть — `Health == 0`, самооборона до Enter, `Inside` (закрыта только изнутри); трасса попаданий сервера — `tests/Orpheus.Tests/StrikeTrace.cs`. E2-11: CraftItems (этап Craft: стены / полы / дверь через CraftDesk) · HammerSlot (предмет этапа в руке, StockMove).
- NightCampBot.cs :15 (night_camp): Next :57, TicksToDeath :29, KillerId :32.
- DigLineBot.cs :23 (dig_line, образец), DodgeBot.cs :25 (dodge).

## Остальное в src/Orpheus.Core/Sim/ (по одной строке)
- StateHash.cs: хэш состояния (контрольные суммы сверки реплеев).
- Spawner.cs: partial Simulation: спавн, волны, точки появления.
- SimEvent.cs: события симуляции (звук, эффекты, удары).
- SimEntity.cs: сущность: статус, мувер, инвентарь.
- Items.cs: имена id предметов и видов; свойства — в `Items/ItemTable.cs`.
- Inventory.cs: сумка — 40 ячеек со стопками: Add / Remove / Move / Split / TakeFrom, payload 121 байт, чтение счётчиков старых сохранений.
- InputJournal.cs: журнал ввода (реплей, сверка сервера).
- InputFrame.cs: кадр ввода (направление, кнопки, прицел, слот).
- EntityChunkGrid.cs: сетка чанков сущностей (быстрый поиск по тайлам).
- EnemyBrain.cs: ИИ врагов (цель, преследование, атака).
- DigLoot.cs: добыча при копании (что из чего падает).
- CharacterRecord.cs: запись персонажа (сквозной сейв).
- CharacterNames.cs: имена персонажей.

## src/Orpheus.Core/Items/ (E2-1; пространство имён Orpheus.Core.Sim)
- ItemTable.cs: `ItemCategory`, `ItemDef`, `ItemTable` — таблица предметов данными (строки добавлять в конец).
- GroundPile.cs: `GroundPile` (куча на земле), `BagOp` (операция с ячейками).
- RecipeTable.cs (E2-3): `CraftStation`, `RecipeDef`, `RecipeTable` — рецепты данными (строки — в конец). CraftQueue.cs: `CraftReply`, `CraftQueue` (очередь изготовления в `SimEntity.Craft`, payload 12 байт). CraftRules.cs: `Affordable` (E2-10: и со сводкой `ChestStock`), `StationCheck`, `SourceCenter` (откуда считать сундуки: ближайший станок / персонаж), `ChestRadiusTiles` (6) — общие для сервера и клиента. ChestStock.cs (E2-10): «сундуки рядом» по видам станка.
- Sim/Simulation.Craft.cs (E2-3): CheckCraft · RequestCraft · RequestCraftCancel · RefundCraftQueue · ApplyCraft · ApplyCraftCancel · StepCraft (фаза 5.2 такта, после заявок на сумку). Sim/Simulation.CraftChests.cs (E2-10): CollectNearChests (порядок снятия) · AffordableNear · TakeIngredients (сумка → сундуки) · FillChestStock.

## Сеть и клиент предметов (E2-1)
- NetServer.Items.cs: OnBagRequest · SendPilesTo (разница куч игроку) · TakerOf. NetClient.Items.cs: Piles · RequestBagMove/Split/Drop · OnPiles · OnPileGone.
- Protocol/Messages.cs: InventoryMessage (ячейки), NetPile, PilesMessage (27), PileGoneMessage (28), BagRequestMessage (29).
- BotPiles.cs: бот идёт к куче и ждёт подбора (dig_line, build_room).
- Крафт (E2-3): Protocol/CraftMessages.cs — CraftRequestMessage (30), CraftReplyMessage (31), CraftStateMessage (32), NearbyStockMessage (36, E2-10). NetServer.Items.cs: OnCraftRequest · SendCraftTo · SendStockTo (E2-10, раз в 5 тактов и после своего изготовления). NetClient.Items.cs: Craft · CraftRunning · CraftProgressAt · RequestCraft · RequestCraftCancel. CraftDesk.cs: `ICraftRequests`, `CraftLine`, `ICraftCancelChecked`, `CraftDesk` (LineOf · Missing · Have · InChests · Make · Cancel; E2-10 — со сводкой `NetClient.NearbyStock`) — путь окна крафта и ботов.
- Полки окна изготовления (E2-9): `src/Orpheus.Net/Session/CraftShelf.cs` — Sections (разделы и подразделы), Rows (ключ результата → раздел / подраздел), Place · Recipes · CountIn · Shown (фильтр «только доступные») · Available · Badge (число на значке); тесты `CraftShelfTests`.
- CraftChainBot*.cs (craft_chain, E2-3): `.cs` — Next / Decide (этапы Stage), OnReply · `.Harvest.cs` — Harvest (ходьба рывками, копание, подбор), Dig, ToolSlotFor · `.Path.cs` — Plan (поиск в ширину, разведка по клеткам), Flood, BuildPath · `.Bench.cs` — PlaceBench, Craft, Equip.
- CharacterRoster.cs: Restore (загрузка: очередь изготовления → в сумку записи) · Park (уход из мира: очередь → в сумку).
- client/Assets/Scripts/PileView.cs: значки куч; NetGame.Edits.cs: HookPiles, BagHud (ячейки). tools/make_item_icons.py — заглушки значков.

## Стройка, сундук, кровать (E2-5)
- Sim/Simulation.Build.cs: RequestRepair · IsRepairEdit · PreRepair · RefundRepair · RequestSetBed · SetBed · RespawnPointOf · сундуки: ChestCells (30) · ChestCellBase · ChestTiles/ChestContents · TryGetChest · SetChest · RestoreChests · RequestChestOp · ApplyChestOp · SpillChest · StoreChest.
- World/BuildDefs.cs: FloorKeyBase / WoodFloorKey… · FloorKey · FloorOf · Palette · ItemOf · TryKeyOf (E2-11) · Affordable · RepairCostOf · TryGet; BuildDef.Item.
- Sim/Simulation.BuildItems.cs (E2-11): AnyCell · CostOf · PayPlacement (pre-pass: предмет из ячейки) · CellWith · RefundPlacement · GiveBuildRefund (снос → один предмет). Simulation.EditQueue.cs: EditRequest.Cell. World/EditRules.cs: Intact/UnderAttack/Paused (16–18) · MaxReply · UseRadiusTiles · InUseRadius · CheckRepair · RepairAffordable.
- Net/Protocol/UseMessages.cs: UseOp · UseRequestMessage (33) · UseReplyMessage (34) · ChestStateMessage (35). EditMessages: TileOp.Repair (5). Messages: BagRequestMessage.MaxCell (70).
- Net/Session/NetServer.Use.cs: OnRepair · OnUseRequest · OnChestBagRequest · SendChestTo · CloseChest. NetClient.Use.cs: Repair · RequestUse · RequestCloseChest · Chest/ChestOpen/ChestTile · UseReplied · ChestChanged.
- Net/Session/BuildHand.cs: IBuildRequests (Place с ячейкой) · IUseRequests · BuildAction · BuildCheck · BuildHand (E2-11: IsHammer, IsBuildItem, Check/Click/PlaceLine по ячейке, SlotWith, StockMove, LastRequest). ObjectUse.cs: UseKind · KindOf · At · Find (правило F). BagHand: SyncChest · ChestBase · ItemAt/CountIn · FitTarget.
- Save: SaveSections CreaturesField.Chest · ChestField · EntityField.BedX/BedY · CharacterField.BedX/BedY; WorldSave ReadChest. CharacterRecord.HasBed/Bed; CharacterRoster.Park (кровать за ключом).
- client: UI/GameUi.Build.cs (UiBuildState.Placing, UiNote, ShowBuild, SyncChestWindow, OpenCraft, Notify) · UI/BuildBar.cs (ChestWindow; лента BuildBar убрана в E2-11) · UI/UiDemo.Build.cs (--ui-demo build|chest|house) · BuildGhost.cs · BuildLook.cs · NetGame.Edits.cs (BuildState, UseObject, HammerEdits) · PaintDemo.House.
- tests/BuildTests.cs (E25_*); Golden/format8_craft.*.

## Стена в полтайла (E2-12)
- World/BuildCells.cs: CellGroup · BuildCells (Full, Bit, IsCellBuilding, MaskOf, MaskAt, Count, IsDoorMask, CellShape/CellOf/CellAt/QuadOf, Line — линия ячеек по тайлам, DoorMaskFor, CheckPlace). BuildRecord: FloorCells / NaturalFloor / Cells / WithCells.
- Movement/Collision.cs: ITileCells · SolidCellsOf · CircleOverlapsCells · AxisPush/OverlapsTile по квадратам ячеек. Simulation.Movement.cs: SolidCells. Simulation.Combat.cs: StrikeCellSolid. Simulation.BuildItems.cs: PlacementUnits · CostPaidRepair.
- E2-13 линейка: Net/Session/BuildRuler.cs — PlaceMode (Ruler по умолчанию / Single) · RulerKind (Line стена/забор, Rect пол/настил, Single прочее) · RulerSpot · BuildRuler: KindOf/KindOfItem · Begin · Update (план: PlanLine — ось по большему смещению, Shift — Брезенхэм, отрезки по 32 ячейки; PlanRect — ряды тайлов) · Release (пачка BuildHand.PlaceLine, хвост обрезан по «есть» — LastOk) · AddRun (единая развёртка в отрезки, стык — по границе тайла) · RectOnCells (E2-15) · AnchorItem · Cancel · Toggle. Клиент: NetGame.Edits.cs RulerEdits / RulerTookEscape, NetGame.cs RulerMayTakeShift / _shiftDodgeHold (Shift ждёт ЛКМ 6 кадров), BuildState (план при протягивании), GameUi.Build.cs (Ruler, ToggleRuler, SessionMode, подпись _mode), BuildGhost.ShowPlan/HidePlan, UiDemo.Build.cs (ruler-wall|ruler-floor). Тесты RulerTests (E213_*).
- Net: NetServer.Cells.cs (PlaceCells, TagCountShift) · NetClient.Cells.cs (PlaceCells) · TileRequestMessage.Quad · BuildHand Check/Click(quad), PlaceLine(fromQuad, toQuad), BuildCheck.Cells. BuildRoomBot: WallCellsOf · WallDone · PlaceWallCells.
- Save/LegacyWalls.cs (счёт старых стен, формат < 10). Client: ThinWallLook.cs · WorldView.RefreshSeams · BuildGhost.CellBox · PaintDemo.ThinHouse (--ui-demo thin) · NetGame.Edits _cursorQuad.
- tests/HalfWallTests.cs; Golden/format9_walls.*.

## Интерфейс (E2-2) - client/Assets/Scripts/UI
- UiRoot.cs: `UiRoot` (холст перед камерой, Fit, FromScreen, Node/Picture/Label), `UiGroup` (привязка, Contains/Local).
- UiStyle.cs: цвета, `Frame` (рамка с 9 срезами кодом), `Font`.
- UiParts.cs: `UiItemIcon` (Set/Dim), `UiSlotGrid` (Set/Select/Hover/Hit), `UiWindow`, `UiButton` (E2-4: кнопка, вкладка, строка), `UiTooltip`, `UiCarry`, `UiBar`.
- CraftWindow.cs (E2-4 → E2-9): `CraftWindow` — разделы / подразделы / сетка рецептов (Refresh → CraftShelf.Shown, SelectSection · SelectSub · SelectCell · StepRecipe · Scroll · ToggleFilter, HitTest → `CraftHit` (Section, Sub, Cell, Filter, ScrollBar, Button, Queue), CenterOf, Show: ShowGrid · ShowCard · ShowQueue; Stock — сундуки рядом, E2-10); раскладка — константы в начале класса. Демо-скрипты `--ui-demo inv*`, `craft-chest` — `UiDemo.CraftScript` в GameUi.cs (таблицы шагов InvSteps, WeaponSteps, BuildingSteps, ChestSteps).
- GameUi.cs: `UiInput`, `UiCraftState`, `BagWindow`, `GameUi` (ReadInput, Frame, CraftReplied, CraftClick / CraftKeys / Make, FindStation, Layout, PointerBlocked, OverWindow, EscapeUsed, SlotClicked), `UiDemo` (--ui-demo bag|hotbar|craft|craft+bag|craft-hint|craft-chest; CraftScript). E2-10: `UiCraftState.Stock`, `CraftWindow.Stock` — «есть» и пометка «(в сундуке)» в карточке.
- UiStrings.cs: строки и названия предметов по ключу.
- src/Orpheus.Net/Session/BagHand.cs: `IBagRequests`, `BagHand` (Sync, Left, Right, Quick, Outside, ShownCount, QuickTarget); тесты `BagHandTests.cs`.
- NetGame.Edits.cs: UiFrame (перед SendInput), блокировка мыши в UpdateEdits.

## Остальное в src/Orpheus.Save/
- WorldFolder.cs: папка мира: набор файлов и структура на диске.
- SaveSections.cs: секции сейва: виды, границы.
- SaveModule.cs: модуль сейва: регистрация версионных данных.
- SaveContainer.cs: контейнер: заголовок, CRC, порядок секций.
- Records.cs: записи: персонажи, сущности, мета.
- IPlatformStorage.cs: интерфейс платформенного хранилища (пути, потоки).
- HostLockFile.cs: лок-файл хоста (чтобы два сервера не писали в один мир).
- Crc32.cs: CRC32 для целостности контейнера.

## Генератор - src/Orpheus.WorldGen
- DepositStage.cs (E2-6): Run :~120 (сетка ячеек 32, выбор вида) · Place :~205 (тайлы пятна) · DepositStats.Of / MaskOf (числа приёмки). Штамп меди — HearthStage.StampCopper.
- C-008: Net/Session/HudLayout.cs (раскладка панели и мини-карты, AddHud, SlotCenter, OrbCenter) · BagHand.Pump/MoveAllStop (пачки по 8) · Net/Session/UiPointer.cs (PointerRoute World/Ui/Drop, Add/Over/Route/Blocks) · Net/Session/MiniMap.cs (FloorColors, ObjectColors, ColorOf, Paint, TileOffset, Marker) · BagHand.TakeAll/PutAll · UI/BottomBar.cs (сферы, панель 1–8, рамки способностей; PartAt, AddZones, Set) · UI/MiniMapView.cs (Frame, Repaint по 32 строки, Toggle M, Zoom) · BagWindow (GameUi.cs, 8 × 4, AddZones) · ChestWindow (BuildBar.cs, кнопки ButtonAt) · CraftWindow (столбик GridRows 7, AddZones, TipFor) · UiCellLook / UiSlotGrid(cell) в UiParts.cs · UiStyle.Glass*, Round (сферы, замок) · UiGroup.Zone · тесты C008PointerTests, C008MiniMapTests.
