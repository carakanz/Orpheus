# Хрупкие тесты (краснеют в общем прогоне, зелёные поодиночке)

Правило (`AGENTS.md` п. 4): сливать можно, если в `-All` упали только тесты отсюда и каждый
прошёл поодиночке. Тест, упавший так 3 раза, чинится раньше новых карточек (карточка P-003).
Счёт ведётся с 12.11.2026 (падения до этой даты — из журналов `work/status.md`).

| Тест | Класс | Случаи | Последнее | Примечание |
| --- | --- | --- | --- | --- |
| `D00_Net_Udp_BotsLogIn_Snapshots30Hz_VersionMismatchRejected` | NetTests | 3 | 13.11 (1-028) | боты не входят за 6 с под нагрузкой; 3-й случай → чинить в P-003 |
| `D07_S7_4_ServerRun_HostCrashed_ServerRestarted_HostAndFriendsSamePlace` | ReturnTests | 3 | 13.11 (1-028) | в журнале — `HostCrashed_SamePlace`; 3-й случай → P-003 |
| `D00_Net_Udp_Big_TwoBots60s_Rtt100_Loss1_NoDisconnects` | NetTests | 2 | 13.11 (1-028) | UDP под нагрузкой |
| `D08_S3_0_2_RunSeam_Udp_Big_Rtt100_Sigma10_Loss1_LapPerAxisExact_ObserverNoSeamJump` | RunSeamTests | 2 | 13.11 (1-028) | ~9 мин, UDP Big |
| `D07_S1_9_4_Big_Server1024_ReadyWithin5s_Under128Mb` | MetricsTests | 2 | 13.11 (1-028) | ReadyWithin5s по времени |
| `D02_S9_2_Big_Bench1024_1000Ticks40Searches` | PathTests | 1 | 13.11 (1-028) | бенчмарк тактов под нагрузкой |
| `D07_S2_13_9_Launcher_SingleThenOpen_FriendJoins_NoRestart` | HostProcessTests | 3 | 13.11 (1-028) | старт > 5 с под нагрузкой; 3-й случай → чинить в P-003 |
| `CrashInsideReplacingSave` | SaveTests | 1 | 12.11 (1-014) | обрыв записи |
| `HostLeftAlone_SavedAtOnce` | SaveTests | 1 | 12.11 (1-014) | в журнале — опечатка `HostLeegAlone` |
| `PlayerLeavesAndReturns_RestartBetween_SamePlace` | SaveTests | 1 | 11.11 (1-011) | |
| `LeaveDuringWrite` | SaveTests | 1 | 11.11 | |
| `FreezeIsStall` | ClockTests | 1 | 08.11 (передача) | время хрупко под нагрузкой |
| ServerThread (таймаут треда) | NetTests | 1 | 11.11 | |
