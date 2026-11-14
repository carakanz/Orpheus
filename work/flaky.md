# Хрупкие тесты (краснеют в общем прогоне, зелёные поодиночке)

Правило (`AGENTS.md` п. 4): сливать можно, если в `-All` упали только тесты отсюда и каждый
прошёл поодиночке. Тест, упавший так 3 раза, чинится раньше новых карточек (карточка P-003).
Счёт ведётся с 12.11.2026 (падения до этой даты — из журналов `work/status.md`).

| Тест | Класс | Случаи | Последнее | Примечание |
| --- | --- | --- | --- | --- |
| `D00_Net_Udp_BotsLogIn_Snapshots30Hz_VersionMismatchRejected` | NetTests | 9 | 13.11 (E1-1 трижды, P-002), 14.11 (E1-8 ci) | боты не входят за 6 с под нагрузкой; >3-го случая → чинить в P-003 |
| `D07_S7_4_ServerRun_HostCrashed_ServerRestarted_HostAndFriendsSamePlace` | ReturnTests | 6 | 13.11 (1-026), 14.11 (E1-8 ci) | в журнале — `HostCrashed_SamePlace`; «уход игрока не сохранён» под нагрузкой |
| `D00_Net_Udp_Big_TwoBots60s_Rtt100_Loss1_NoDisconnects` | NetTests | 3 | 13.11 (1-026) | UDP под нагрузкой; боты 0 с в игре |
| `D08_S3_0_2_RunSeam_Udp_Big_Rtt100_Sigma10_Loss1_LapPerAxisExact_ObserverNoSeamJump` | RunSeamTests | 4 | 13.11 (1-026) | ~9 мин, UDP Big; в этот раз — NullReference (BotRun.WatchText :659) при пустом прогоне |
| `D07_S1_9_4_Big_Server1024_ReadyWithin5s_Under128Mb` | MetricsTests | 3 | 13.11 (1-028) | ReadyWithin5s по времени; 3-й случай → чинить в P-003 |
| `D02_S9_2_Big_Bench1024_1000Ticks40Searches` | PathTests | 3 | 13.11 (1-026) | бенчмарк тактов под нагрузкой; 3-й случай → чинить в P-003 |
| `D07_S2_13_9_Launcher_SingleThenOpen_FriendJoins_NoRestart` | HostProcessTests | 5 | 13.11 (E1-1), 14.11 (E1-8 П) | старт > 5 с под нагрузкой; 3-й случай → чинить в P-003 |
| `D07_S6_5_Saver_CrashInsideReplacingSave_EveryMoment_LoadsNewestIntact` | SaveEventTests | 3 | 13.11 (1-028), 14.11 (E1-8 ci) | обрыв записи |
| `D07_S2_3_ServerRun_HostLeftAlone_SavedAtOnce` | HostTests | 4 | 13.11 (P-004, при сборке Unity), 14.11 (E1-8 П) | в журнале была опечатка `HostLeegAlone`, класс — HostTests (не SaveTests) |
| `PlayerLeavesAndReturns_RestartBetween_SamePlace` | ReturnTests | 1 | 11.11 (1-011) | фактический тест — `D07_S7_4_ServerThread_PlayerLeavesAndReturns_RestartBetween_SamePlace` |
| `D07_S6_4_Saver_LeaveDuringWrite_SavedRightAfter_Coalesced` | SaveEventTests | 1 | 11.11 | в журнале — `LeaveDuringWrite`; замер «записан ≤ 1 с» |
| `D07_S11_6_Clock_FreezeIsStall_NotTickTime` | MetricsTests | 1 | 08.11 (передача) | в журнале — `FreezeIsStall`; время хрупко под нагрузкой |
| `D07_S1_9_5_ServerThread_LoopbackClient_TickHolds_SaveLoadThroughStorage` | ServerThreadTests | 5 | 13.11 (E1-1), 14.11 (E1-8 ci) | таймаут треда под нагрузкой (763 тактов при 240–600, ×2,00); 3-й случай → чинить в P-003 |
| `D08_3_1_BuildRoom_Udp_Rtt100_Night_Under_Walls` | NightBotsTests | 7 | 13.11 (1-026), 14.11 ×6 (C-001 NightBots; P-003 r1 `-All`; соло) | 1-й: «Disconnected» на входе за 6 с под нагрузкой; 2-й: «урон за дверью 4 кадров» — монстр бил сквозь закрытую дверь (дефект правила — починен C-001); 3-й (11:03): «урон за дверью 5 кадров» — остаточный вектор: замах начат при открытой двери, приземлился после закрытия → новая карточка C-002; 4-й (11:14, r1 `-All`) и 5-й (13:10, соло в tc001, уже с фиксом C-001): «закрыта да» не найдено — бот достраивает (стены 9/9, двери 15/15, урон за дверью 0, смертей 0), но дверь не закрывает: цели блокируются EnemyNear x66/x33, ToolRequired x16/x18. Соло воспроизводится → не флейк, регулярный дефект бота на UDP RTT100; чинить в P-003 раньше новых карточек (п. 4). 6-й (13:29, соло в tp003 с трассой двери): «закрыта да», рассвет есть, этап Done, но «урон за дверью 8 кадров» — трасса `[т3852:закр]` единственная: дверь закрылась один раз и не открывалась; удары от врагов в d1–d3 через закрытую дверь = остаточный вектор (замахи начаты до закрытия) → **починено C-002 (`b716349`)**, ждать соло-подтверждения в tp003 после вливания main; 7-й (14:16, соло в tp003 без C-002, с трассой): тот же рисунок — трасса `[т3935:закр]`, Done/рассвет, «урон за дверью 6 кадров», попадания от k2 в d2–d3 в состоянии замаха (e12) = остаточный вектор; «закрыта нет» (4-й/5-й) — отдельный симптом, трасса его не воспроизвела, FailReason починен (печатает настоящий этап) |
| `Udp_ServerRun_OneBot_Pause15s_TicksStand_NoCorrectionsAfter` | PauseUdpTests | 1 | 14.11 (E1-2 ci в te12, 5-е падение прогона) | зелёное соло 5/5; новый случай — не дефект E1-2 (движок паузы не трогался), под нагрузкой |
| `D07_S2_13_1_Udp_Big_TwoBots60s_Rtt100_Loss1_Freeze2s_NoDisconnects_Within05Tile` | ClockUdpTests | 1 | 13.11 (1-026) | ≤ 0,5 тайла — 99,882 % (порог 99,9) под нагрузкой |
