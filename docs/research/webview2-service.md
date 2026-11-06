# WebView2 и служба Windows

Короткая выжимка того, почему hv86 устроен именно так, с проверяемыми фактами.

## Проблема

`WebView2` — это Chromium, который инициализируется через `CoreWebView2Controller`,
а тот требует окна и рабочего стола. В сессии 0 (сессия служб) рабочего стола
пользователя нет. Официальная позиция Microsoft: WebView2 в службах не
поддерживается; практика подтверждает — инициализация из службы зависает или
падает (см. WebView2Feedback issue #3458 «WebView2 initialization in headless mode
not work when invoked by a Windows Service»).

## Как это обходит hv86

Три уровня, каждый со своей сессией:

| процесс | сессия | что делает |
|---|---|---|
| `hv86.exe --service` | 0 (LocalSystem) | реестр ВМ, снапшоты, watchdog, pipe-сервер |
| `hv86.exe --vmhost <id>` | сессия пользователя | WebView2 + v86, окно консоли |
| `hv86.exe` (UI) | сессия пользователя | менеджер, общается со службой по pipe |

Служба запускает `vmhost` в активной консольной сессии:

1. `WTSGetActiveConsoleSessionId()` — номер сессии;
2. `WTSQueryUserToken(session)` — токен вошедшего пользователя (LocalSystem имеет
   `SeTcbPrivilege`, поэтому вызов разрешён);
3. `DuplicateTokenEx(..., TokenPrimary)` — первичный токен;
4. `CreateEnvironmentBlock` — окружение пользователя;
5. `CreateProcessAsUser` с `lpDesktop = "winsta0\\default"`.

Если `CreateProcessAsUser` не хватает привилегий (код 1314), используется резервный
`CreateProcessWithTokenW`. Реализация — `src/Core/SessionLauncher.cs`.

## Следствия, которые надо помнить

- **ВМ живут только при вошедшем пользователе.** Завершение сеанса = завершение
  `vmhost`. Это осознанное ограничение текущего этапа; headless-движок (M5)
  снимет его, потому что там нет ни окна, ни WebView2.
- **Служба не создаёт окон вообще** — даже скрытых. Любая попытка инициализировать
  WinForms в сессии 0 вешает процесс.
- **Процессы-потомки живут в Job Object** с `KILL_ON_JOB_CLOSE`: падение службы
  не оставляет висящих `vmhost` с занятыми файлами дисков.
- **`Paths.ExePath` и `SelfHost`** различают «запущено как hv86.exe» и «запущено
  как `dotnet hv86.dll`»: во втором случае дочерний процесс запускается через
  `dotnet <dll>`, иначе манифест `requireAdministrator` не даст его создать без UAC.

## Что проверено локально

- `--selftest` видит WebView2 Runtime (`GetAvailableBrowserVersionString`).
- `--smoke` и `--itest` реально поднимают `vmhost` из `dotnet hv86.dll` (без прав
  администратора) и проходят полный цикл загрузки гостя.
- Установка службы (`sc create ... binPath= "...hv86.exe" --service`) требует
  прав администратора — это единственная часть, которую нельзя проверить
  в непривилегированном окружении.