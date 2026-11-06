# AGENTS.md — правила проекта hv86

**hv86** — гипервизор на C#, где ВМ исполняет v86 (x86→WASM). Один exe: WinForms-шелл +
WebView2 UI + служба. Дизайн — [`ARCHITECTURE.md`](ARCHITECTURE.md), план — [`ROADMAP.md`](ROADMAP.md),
факты по v86 — [`docs/research/v86-api.md`](docs/research/v86-api.md). Задача — [`PROMPT.md`](PROMPT.md).

## Стек и жёсткие рамки
1. **.NET 9 (`net9.0-windows`), x64, WinForms-шелл.** UI — только WebView2, без дизайнера.
   `Nullable`/`ImplicitUsings` выключены, `LangVersion latest` — как в AutoZapret.
2. **Без NuGet, кроме WebView2.** Managed DLL — в `lib/webview2` (см. `tools/fetch-webview2.ps1`),
   `<Reference>` + `WebView2Loader.dll` в output. Всё остальное — BCL.
3. **UI в `web/`** (html/js/css), встраивается как `EmbeddedResource`, отдаётся через
   `WebResourceRequested` — работает офлайн, из одного exe.
4. **Мост UI↔C# — `chrome.webview.postMessage`** (`send`/`onMessage`), JSON-строки.
   `window.external` не используем.
5. **Данные — JSON + атомарная запись** (temp → `File.Replace`). Никаких БД без надобности.
6. **IPC служба↔UI/vmhost — named pipe** `\\.\pipe\hv86`, JSON-RPC 2.0, построчно.
7. **v86 пиннится по версии.** Ассеты в `assets/v86/<version>/`, SHA-256 в `assets/v86/manifest.json`.
   Версия пишется в `vm.json` и в `meta.json` снапшота: state несовместим между версиями.
8. **Сборка:** `tools/build.cmd` (dotnet build), `tools/publish.cmd` (self-contained single-file).
   Иконка — `tools/make-icon.ps1` (рисуется кодом, `assets/hv86.ico` коммитится).
   Проверки — `tools/selftest.ps1` (см. ниже). Вёрстка без сборки — `tools/preview-manager.ps1`.
9. **Кодировка исходников — UTF-8 (без BOM).** Кириллица в UI-строках только через `i18n/*.json`
   и `web/`; в C# — через ключи, не литералами. `.ps1`-скрипты — **только ASCII**:
   Windows PowerShell 5.1 читает их в OEM-кодировке и ломается на кириллице.

## Правила кода
- Пространства: `Hv86.App`, `Hv86.Core`, `Hv86.Vm`, `Hv86.VmHost`.
- Один файл = одна тема (`DiskOverlay.cs`, `Watchdog.cs`), без «God object».
- Ошибки службы не валят процесс: лог + состояние `failed` у конкретной ВМ.
- Все фоновые операции — `async` + `CancellationToken`, никаких `Thread.Sleep` в UI-потоке.
- Любой процесс-потомок — в Job Object (убивается вместе с vmhost).
- Публичные протоколы (pipe, кадры, `vm.json`) описываются в `docs/` до реализации.

## Дисциплина данных
- `%ProgramData%\hv86\` — единственное место записи; пути только через `Hv86.Core.Paths`.
  Для тестов и портативного режима `HV86_ROOT` переопределяет корень (наследуется vmhost'ами).
- Удаление ВМ — только через корзину `%ProgramData%\hv86\trash\<id>-<utc>\` (не `Directory.Delete`).
- Снапшот без `meta.json`/битого `state.bin` считается невалидным и не показывается.
  Снапшот = `state.bin` + `disk.img` (копия образа), см. `Vm/CheckpointStore.cs`.
- Никогда не трогаем `hv86.json` руками из кода ВМ-хоста: только служба-владелец.

## Документация
- Каждая нетривиальная подсистема (FT, движок, протокол кадров) — файл в `docs/`.
- Числа (бюджеты паузы, интервалы, лимиты) — в одном месте (`Ft/HealthModel` или
  `Core/Settings`), в документации только ссылки на дефолты.

## Проверки
- `tools\selftest.ps1` — сборка, затем `--unit` → `--selftest` → `--smoke` → `--itest`.
- `--unit`: формат state-файла v86, применение блоков диска, JSON, пути. WebView2 не нужен.
- `--smoke <образ>`: реальная загрузка гостя, `save_state`, перезапуск из state.
- `--itest`: движок + vmhost: создать → загрузить → снапшот → убить vmhost → автоподъём → откат.
- UI без сборки: `tools\preview-manager.ps1`; страница сама включает демо-данные, когда
  `chrome.webview` отсутствует (см. `preview()` в `web/manager.js`).

## Что не делаем
- Не форкаем и не патчим v86 без причины: сначала ищем штатный API.
- Не патчим v86 и ради сохранения диска: изменения берём из `save_state()`
  (разбор в `src/Vm/V86State.cs`), а не из внутренностей эмулятора.
- Не обещаем в UI того, чего v86 не умеет (64-бит, многопроцессорность, GPU).
- Не запускаем WebView2 внутри службы (Session 0) — это не поддерживается.