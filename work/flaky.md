# Хрупкие тесты (краснеют в общем прогоне, зелёные поодиночке)

Правило (`AGENTS.md` п. 4): сливать можно, если в `-All` упали только тесты отсюда и каждый
прошёл поодиночке. Тест, упавший так 3 раза, чинится раньше новых карточек (карточка P-003).
Счёт ведётся с 12.11.2026 (падения до этой даты — из журналов `work/status.md`).

| Тест | Класс | Случаи | Последнее | Примечание |
| --- | --- | --- | --- | --- |
| `D00_Net_Udp_BotsLogIn_Snapshots30Hz_VersionMismatchRejected` | NetTests | 4 | 13.11 (1-026) | боты не входят за 6 с под нагрузкой; >3-го случая → чинить в P-003 |
| `D07_S7_4_ServerRun_HostCrashed_ServerRestarted_HostAndFriendsSamePlace` | ReturnTests | 5 | 13.11 (1-026) | в журнале — `HostCrashed_SamePlace`; «уход игрока не сохранён» под нагрузкой |
| `D00_Net_Udp_Big_TwoBots60s_Rtt100_Loss1_NoDisconnects` | NetTests | 3 | 13.11 (1-026) | UDP под нагрузкой; боты 0 с в игре |
| `D08_S3_0_2_RunSeam_Udp_Big_Rtt100_Sigma10_Loss1_LapPerAxisExact_ObserverNoSeamJump` | RunSeamTests | 4 | 13.11 (1-026) | ~9 мин, UDP Big; в этот раз — NullReference (BotRun.WatchText :659) при пустом прогоне |
| `D07_S1_9_4_Big_Server1024_ReadyWithin5s_Under128Mb` | MetricsTests | 3 | 13.11 (1-028) | ReadyWithin5s по времени; 3-й случай → чинить в P-003 |
| `D02_S9_2_Big_Bench1024_1000Ticks40Searches` | PathTests | 3 | 13.11 (1-026) | бенчмарк тактов под нагрузкой; 3-й случай → чинить в P-003 |
| `D07_S2_13_9_Launcher_SingleThenOpen_FriendJoins_NoRestart` | HostProcessTests | 3 | 13.11 (1-028) | старт > 5 с под нагрузкой; 3-й случай → чинить в P-003 |
| `D07_S6_5_Saver_CrashInsideReplacingSave_EveryMoment_LoadsNewestIntact` | SaveEventTests | 2 | 13.11 (1-028) | обрыв записи |
| `D07_S2_3_ServerRun_HostLeftAlone_SavedAtOnce` | HostTests | 2 | 13.11 (1-026) | в журнале была опечатка `HostLeegAlone`, класс — HostTests (не SaveTests) |
| `PlayerLeavesAndReturns_RestartBetween_SamePlace` | ReturnTests | 1 | 11.11 (1-011) | фактический тест — `D07_S7_4_ServerThread_PlayerLeavesAndReturns_RestartBetween_SamePlace` |
| `D07_S6_4_Saver_LeaveDuringWrite_SavedRightAfter_Coalesced` | SaveEventTests | 1 | 11.11 | в журнале — `LeaveDuringWrite`; замер «записан ≤ 1 с» |
| `D07_S11_6_Clock_FreezeIsStall_NotTickTime` | MetricsTests | 1 | 08.11 (передача) | в журнале — `FreezeIsStall`; время хрупко под нагрузкой |
| `D07_S1_9_5_ServerThread_LoopbackClient_TickHolds_SaveLoadThroughStorage` | ServerThreadTests | 3 | 13.11 (1-026) | таймаут треда под нагрузкой (875 тактов при 240–600); 3-й случай → чинить в P-003 |
| `D08_3_1_BuildRoom_Udp_Rtt100_Night_Under_Walls` | NightBotsTests | 1 | 13.11 (1-026) | «Disconnected» на входе за 6 с под нагрузкой (~13 мин тест, зелёный поодиночке) |
| `D07_S2_13_1_Udp_Big_TwoBots60s_Rtt100_Loss1_Freeze2s_NoDisconnects_Within05Tile` | ClockUdpTests | 1 | 13.11 (1-026) | ≤ 0,5 тайла — 99,882 % (порог 99,9) под нагрузкой |
