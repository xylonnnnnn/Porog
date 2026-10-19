# Протокол проверки Порог

Дата: 19 октября 2026 года. Версия: 1.0.0, кандидат MVP. Основание: `Porog_MVP.docx`, раздел 9; «Чек-лист — Порог.docx».

## Выполненные проверки

- macOS arm64, .NET SDK 10.0.402: полная Release-сборка ядра, службы и WinForms UI — **0 ошибок, 0 предупреждений**.
- `Porog.Core.Tests`: **64 пройдено, 0 ошибок, 0 пропусков**. Время тестов около 139 мс в первом локальном запуске; это не измерение скорости UI или службы.
- Тесты работают с временными файлами и управляемым временем; системный hosts компьютера разработчика не изменялся.
- Windows smoke-сценарий и GitHub Actions подготовлены для проверки реальной установки, IPC, hosts и завершения собственного тестового процесса. Результат удалённого запуска фиксируется ниже после выполнения.

В репозитории нет заявления о проведённой ручной приёмке на Windows 10/11. Сборка Windows-кода на macOS подтверждает компиляцию, но не работу GUI и системных API на клиентском Windows.

## Сценарии ТЗ

| ID | Проверка | Автоматическое доказательство | Граница проверки |
|---|---|---|---|
| T-01 | Сохранение и перезапуск | ConfigPersistsAndReloadsAfterRestart | Хранилище и контроллер; Windows smoke дополнительно читает IPC |
| T-02 | Неверное время | RejectsEndBeforeStartAndEmptyTargets | Валидация до записи |
| T-03 | Пересечения | RejectsOverlapButAllowsAdjacentAndDisabled | Включённые, выключенные, соседние |
| T-04 | Предупреждение | WarnsOnlyWithinWindowAndOnlyOncePerScheduleVersion; WarningIsDurableAcrossServiceRestart | Решение планировщика; показ уведомления зависит от ОС |
| T-05 | Старт | FullWorkLifecycleReappliesProcessesAndRemovesDomains | Время контролируется тестом; реальные адаптеры проверяются Windows smoke |
| T-06 | Нет отмены | ActiveLockIsEnforcedThroughIpc; ActiveScheduleCannotBeDisabledDeletedShortenedOrRetargeted | Серверный запрет; UI не содержит отмены |
| T-07 | Конец и чужой hosts | HostsPreservesExternalBytesAndEditsWhileRemovingOnlyOwnedBlock; RealHostsAdapterOnlyTouchesSandboxFileAndCreatesBackup | Включая изменения после активации |
| T-08 | 3 и 12 минут | BreakPersistsAbsoluteDeadlineAndCompletesAtBoundary | Два края и восстановление; реальные 3/12 минут вручную ещё не измерены |
| T-09 | 2 и 13 минут | RejectsBreakOutsideEndpoints; InvalidBreakDoesNotCreateRuntimeFile | Нет побочных записей |
| T-10 | Остановка службы | Обработка SERVICE_UNAVAILABLE в PipeClient/MainForm | Ручная проверка видимого статуса на Windows ещё нужна |
| T-11 | Secure DNS | Ограничение в UI, README и руководстве | Экспериментальный набор браузеров ещё не пройден |
| T-12 | Повреждение JSON | CorruptConfigIsPreservedAndNotSilentlyReplacedByBackup; CorruptRuntimeIsPreserved | Оригинал не изменяется |
| T-13 | Нет прав hosts | PermissionErrorsReportPartialProtectionAndRecoverOnNextTick | Имитация отказа адаптера; реальные ACL отдельно |
| T-14 | Перезапуск службы | RestartDuringWorkRestoresSessionAndAttemptCount | Контроллер; Windows smoke перезапускает службу |

## Прослеживаемость функциональных требований

| Требование | Реализация |
|---|---|
| FR-01 | MainForm.Poll, диагностический экран, Install.ps1 |
| FR-02 | ScheduleEditor, Schedule, Controller.Save |
| FR-03 | Validation.Config, Domain, ProcessName, BreakDuration |
| FR-04 | ScheduleEngine.Evaluate, RuntimeState.Warnings, MainForm.Notify |
| FR-05 | EngineWorker, Controller.Tick |
| FR-06 | HostsBlocker.Render и Apply |
| FR-07 | ProcessBlocker.Apply, проверка SID токена |
| FR-08 | MainForm.UpdateStatus, Controller.GuardActive |
| FR-09 | Полуоткрытые интервалы, Apply([]) после конца |
| FR-10 | StartBreak, BreakForm, Screen.AllScreens |
| FR-11 | AtomicJsonStore.Load, Controller.Tick при старте |
| FR-12 | ProgramData и LocalAppData; нет HTTP-сервера и аналитики |
| FR-13 | ProcessResult.Terminated, Session.Attempts |
| FR-14 | Диагностика, руководство, ограничения README |

## Матрица оставшейся ручной приёмки

| Среда или условие | Проверка | Статус |
|---|---|---|
| Windows 10 22H2 x64 | Установка, трей, расписание, удаление | Требует отдельной машины |
| Windows 11 x64 | Тот же поток и несколько мониторов | Требует отдельной машины |
| Edge, Chrome, Firefox | DoH включён/выключен; кэш и открытые соединения | Требует ручного эксперимента |
| Обычный пользователь + другой администратор | OwnerSid, ACL IPC, запрет записи файлов | Требует отдельного профиля |
| Уведомления Windows | T−60, подавленные уведомления, перезапуск UI | Требует интерактивного сеанса |
| «Отход» | 3/12 минут, Alt+F4, Ctrl+Alt+Del, перезапуск UI, монитор | Требует интерактивного сеанса |
| Сон и перезагрузка | Абсолютные сроки, пропущенный интервал | Требует ручного испытания |
| SmartScreen и антивирус | Установка неподписанного архива | Не проверено; подписи нет |
| NFR-01 и NFR-02 | Открытие ≤2 с, сохранение ≤1 с, точность ≤2 с | Не измерено на референсном ПК |

## Решение по готовности

Реализованы три функции MVP и средства установки, диагностики и восстановления. Подтверждены компиляция и автоматические проверки ядра. Для выпуска конечным пользователям нужны целевые испытания Windows и проверка распространения неподписанного ПО. Числа бизнес-гипотезы о потерях времени и будущая цена не проверялись экспериментом.
