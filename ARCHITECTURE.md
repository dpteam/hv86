# hv86 — архитектура

Гипервизор «по смыслу как Hyper-V», но роль VMM-ядра играет **v86** (x86 → WASM эмулятор).
C# — управляющая плоскость (служба + UI), WebView2 — окна, v86 — сама ВМ.

```
┌──────────────────────────────────────────────────────────────┐
│ hv86.UI            (сессия пользователя, WinForms-шелл)       │
│   ManagerWindow (дерево/список/действия) + ConsoleWindow×N    │
│   страницы: web/ (ui.html, console.html) в WebView2           │
└───────────────▲──────────────────────────────┬───────────────┘
                │ JSON-RPC + push (named pipe) │
┌───────────────┴──────────────────────────────▼───────────────┐
│ hv86.Service       (Windows service, LocalSystem)            │
│   VmManager · CheckpointScheduler · Watchdog/FT · EventLog    │
│   hv86.json + vms/<id>/vm.json + snapshots/                   │
└───────────────┬──────────────────────────────────────────────┘
                │ spawn + supervise (Job Object, pipe)
┌───────────────▼──────────────────────────────────────────────┐
│ hv86.VmHost        1 процесс = 1 ВМ                          │
│  WebEngine      : скрытое окно + WebView2 → v86 в Chromium    │
│  HeadlessEngine : Node + v86 (npm) + canvas-shim → кадры/ввод │
└──────────────────────────────────────────────────────────────┘
```

## 0. Границы возможностей v86 (из docs/research/v86-api.md)

- 32-битный x86 ~Pentium 4 (SSE3), **одно ядро**, без 64-бит, без task gates.
- Быстро: Linux (i386), FreeDOS/DOS, KolibriOS, ReactOS, 9front, Haiku, Win 1.x/3.x/9x.
  Win 2000 «нормально», XP/Vista — «при условиях», современное — нет.
- Диски: raw-образы через IDE (+ CD/floppy hot-swap), 9p для файлообмена.
- Сеть: встроенный user-mode NAT (`net_device.relay_url: "fetch"`), свой relay — позже.
- Снапшот = `save_state()` (RAM + состояние устройств) + отдельно содержимое дисков.
- В браузере есть пиксели; **в Node — только VGA-текст** (см. risk R1).

## 1. Модель ВМ (`vms/<id>/vm.json`)

```jsonc
{
  "id": "b3f1…", "name": "alpine-dev", "generation": 1,
  "cpu": { "memory_mb": 128, "vga_memory_mb": 8, "cpuid_level": null },
  "boot": { "order": "CD_FLOPPY_HARDDISK", "bios": "seabios" },
  "media": { "hda": "disks/system.img", "hdb": null, "cdrom": "iso/alpine.iso", "fda": null },
  "net": { "type": "virtio", "mode": "nat", "mac": "52:54:00:…" },
  "engine": { "kind": "web|headless", "policy": "interactive|always" },
  "ft": { "mode": "off|checkpoint|replica", "interval_ms": 5000, "keep": 3,
          "restart": "always|on-failure|never", "max_attempts": 5 },
  "state": "stopped|starting|running|paused|saving|degraded|failed",
  "v86_version": "0.5.473", "created_utc": "…", "notes": ""
}
```

Настройки узла: `%ProgramData%\hv86\hv86.json` (пути, порт API, политика FT по умолчанию, i18n).

## 2. Диски и медиа

- Базовый образ — raw (immutable, с SHA-256 в каталоге).
- Рабочий диск — **overlay**: буфер base + dirty-битмап (4 КБ блоки) + журнал записей.
  Даёт: дешёвые снапшоты, revert, crash-consistency, клонирование ВМ за миллисекунды.
- CD/флоппи — hot-swap через `set_cdrom/eject_cdrom/set_fda` (меню «Insert ISO»).
- Каталог пресетов (`assets/presets.json`): Alpine, Buildroot, FreeDOS, KolibriOS, Win98 —
  скачивание с проверкой хэша, «Quick Create» как в Hyper-V.

#### Как изменения гостя попадают на диск

v86 не имеет API «сохрани диск»: записи гостя живут в кэше блоков внутри эмулятора.
Но `save_state()` сериализует этот кэш — только **изменённые** блоки по 256 байт.
Поэтому при штатной остановке `vmhost`:

1. замораживает эмулятор и снимает state во временный файл;
2. разбирает state (`Vm/V86State.cs`: заголовок → JSON `buffer_infos` → буферы)
   и находит в нём массив пар «номер блока + 256 байт»;
3. записывает эти блоки в образ диска по смещению `номер × 256`.

Итог: диск сохраняется без патчей v86 и без копирования всего образа. При падении
`vmhost` шаг 2–3 не выполняется — образ остаётся нетронутым, а ВМ поднимается из
чекпоинта, в котором эти блоки уже есть. Оба пути дают согласованное состояние.

#### Сеть: два режима

v86 даёт два разных сетевых backend'а, и они несовместимы по возможностям:

| режим (`net.mode`) | backend | что может | чего не может |
|---|---|---|---|
| `nat` (по умолчанию) | `fetch` | HTTP(S) наружу; DHCP/DNS/ICMP внутри; **проброс портов внутрь** (`tcp_probe/connect`) | произвольный TCP/UDP наружу (apt, git, ssh) |
| `relay` | `wisp` | **любой TCP** наружу через наш WISP-сервер | входящий проброс (нет NAT-стека в JS) |

Режим хранится в `vm.json` (`net.mode`) и меняется в настройках ВМ; применяется
после перезапуска, потому что backend выбирается при старте эмулятора.

**NAT + проброс внутрь.** `fetch`-backend проксирует только HTTP(S). Зато у него
есть JS-API `network_adapter.tcp_probe/connect` (PR #1233), которым можно открыть
TCP-соединение к порту гостя. Отсюда проброс:

```
клиент → 127.0.0.1:2222 (TcpListener в vmhost)
        → postMessage {tcpConnect, port:22}
        → v86.connect(22) → TCP-машина fetch-backend → гость
        ↔ {tcpData} в обе стороны, base64, куски по 32 КБ
```

Правила: `net.forwards[{host_port, guest_port, bind}]`, слушаем `127.0.0.1`,
на установку соединения 30 с, всё видно в журнале как `vm.forward`.

**Relay наружу.** `src/VmHost/WispRelay.cs` — WISP-сервер (protocol.md v1.2):
WebSocket на 127.0.0.1 со случайным портом, пакеты `CONNECT/DATA/CONTINUE/CLOSE`,
соединение к `host:port` открывает уже наш процесс, поэтому гостю доступен любой TCP.

- Свой WebSocket-сервер вместо `HttpListener`: тот идёт через http.sys и требует
  URL ACL, а vmhost живёт под обычным пользователем. Хендшейк и кадры разбираются
  вручную (~150 строк), зависимостей ноль.
- Страница консоли отдаётся по `http://hv86.local`, а не `https://`: Chromium
  запрещает `ws://` из https-документа (mixed content), а релей локальный.
- Ограничения релея: 128 потоков на соединение, буфер 4 МБ на поток,
  таймаут установки 12 с, ответы об ошибках по кодам протокола (0x41–0x49).

## 2a. Движки: web и headless

Одна и та же ВМ может исполняться двумя разными процессами, и для службы
разницы нет — протокол канала один (`vm.status/start/stop/saveState/...`).

| | `engine=web` | `engine=headless` |
|---|---|---|
| процесс | `hv86.exe --vmhost` | `node engine\vmhost.mjs --config …` |
| сессия | пользовательская | **службы (0)** |
| экран | canvas v86 целиком | VGA-текст 80×25 с ANSI-цветами |
| мышь/звук | да | нет |
| окно | своё окно v86 | окно интерфейса рисует текст |

- Headless не умеет пиксели в принципе: в Node нет DOM/canvas, а шим браузера
  внутри эмулятора — хрупкий костыль (разбор в `docs/research/headless.md`).
- `NodeRuntime` ищет Node 20+ (`HV86_NODE` → PATH → Program Files → `engine\node`),
  `Vm/V86Assets` выкладывает вшитые BIOS/wasm в кэш, пакет `v86` пиннится
  в `engine\package.json` (`tools\fetch-engine.ps1`).
- Сохранение диска в headless делает сам Node: state и так у него в руках,
  повторяем тот же разбор блоков, что и `Vm/V86State.cs`.

## 3. Снапшоты (checkpoint-дерево)

```
snapshots/0001-Initial/
    meta.json      # родитель, время, инструкции, размеры, v86_version
    state.bin.br   # save_state() → Brotli
    disk.bin       # overlay-дельта относительно родителя (или полный при full)
```
- Снятие: pause → `save_state()` → commit overlay → resume (пауза в бюджете, см. §4).
- Операции: revert, clone (новая ВМ из снапшота), export/import `.hv86x` (zip), delete tree.
- Версия v86 пишется в meta: state несовместим между версиями → отказ с понятной ошибкой.

## 4. Динамическая отказоустойчивость

Уровни (`ft.mode`), включаются на лету:

| Уровень | Что делает | RTO |
|---|---|---|
| `off` | только рестарт процесса с холодной загрузкой | минуты |
| `checkpoint` | кольцо последних K state-снапшотов + журнал диска | ~2–5 с |
| `replica` | второй vmhost в горячем ожидании, получает state | ~1 с |
| `replica-remote` | то же на второй машине по сети (позже) | ~1–3 с |

Механика:
- **Heartbeat** vmhost → служба каждые 500 мс: состояние, `get_instruction_counter()`,
  MIPS, размер последнего state, время снятия, I/O-счётчики (events `ide-*`, `eth-*`).
- **Watchdog**: 3 с тишины → `degraded`, 10 с → kill + restore из свежего чекпоинта,
  инкремент `restarts`, событие в UI/EventLog.
- **Адаптивный интервал**: планировщик держит бюджет паузы (по умолчанию ≤250 мс и ≤3 %
  wall-time). Гость простаивает → интервал растёт; много записи на диск → чаще.
- **Деградация**: при нехватке RAM/CPU — сначала реже чекпоинты, затем ниже fps кадров,
  затем пауза ВМ с низким приоритетом; всё видно в UI как «FT: High/Med/Low».
- **Replica**: standby грузит state в остановленный эмулятор; при смерти primary —
  `run()`. Конфликт диска решается журналом: standby применяет дельту с последнего commit.

## 5. Протокол UI ↔ служба

- Транспорт: `\\.\pipe\hv86` (JSON-RPC 2.0, построчный JSON), ACL: Administrators + Interactive.
- Клиенты: окно менеджера, `hv86 --cli` (консоль/скрипты) — оба ходят одним и тем же путём,
  поэтому состояние всегда одно и «кто прав» не бывает.
- Методы: `vm.list/create/start/stop/pause/resume/reset/delete`, `vm.snapshot.*`,
  `vm.media.mount/eject`, `vm.console.attach`, `host.stats`, `log.tail`, `service.status`.
- Push-события: `vm.state`, `vm.metrics`, `vm.log`, `ft.event`, `checkpoint.created`.
- Консоль: UI-окно получает кадры/отдаёт ввод через тот же pipe (для HeadlessEngine),
  либо WebView2 самой ВМ (для WebEngine) — интерфейс одинаковый для UI.

## 6. Пути и данные

```
%ProgramData%\hv86\
    hv86.json  hv86.db(?)  logs\hv86-YYYYMMDD.log
    vms\<id>\ vm.json  disks\  iso\  snapshots\  checkpoints\ (ring, temp)
    cache\presets\  cache\v86\<version>\
```
Всё пишется атомарно (temp + `File.Replace`), как в AutoZapret (`AtomicFile`).

## 7. Безопасность

- Служба — LocalSystem (нужны права на создание процессов и Job Objects).
- vmhost запускается с ограниченным токеном + Job Object (kill-on-close, лимит RAM).
- Pipe доступен только локально; удалённое управление — опционально, HTTPS на 127.0.0.1 + токен.
- Импорт ISO/образов: проверка размера/хэша, запрет путей вне каталога ВМ.
- Гость в v86 — не песочница: сеть наружу идёт через NAT, но гость может сканировать LAN;
  в v1 предупреждаем, в v2 — изоляция сетей per-VM.

## 8. Риски и решения

| # | Риск | Решение |
|---|---|---|
| R1 | WebView2 не работает в Session 0 (служба) — подтверждено | движок абстрагирован: `WebEngine` живёт в сессии пользователя, `HeadlessEngine` (Node + canvas-shim) — в службе |
| R2 | В Node у v86 нет пикселей (только VGA-текст) | shim DOM/Canvas (`@napi-rs/canvas`) → `screen_make_screenshot`-подобный поток кадров; fallback — чтение VGA-памяти через `read_memory` |
| R3 | `save_state` = копия RAM (128 МБ → ~128 МБ) | Brotli + бюджет паузы + адаптивный интервал; опция «чекпоинт только по событию» |
| R4 | Производительность (нет 64-бит, 1 ядро) | честные пресеты и ожидания в UI; «MIPS» в статусе; предупреждение для тяжёлых гостей |
| R5 | Несовместимость state между версиями v86 | пиннинг версии в `vm.json`/`meta.json`, версия в имени кэша |
| R6 | WebView2 Runtime отсутствует | проверка при старте + ссылка на Evergreen Bootstrapper (как в AutoZapret) |

## 9. Структура репозитория

```
hv86.csproj          net9.0-windows, WinForms-шелл + WebView2, single-file publish
app.manifest         requireAdministrator
AGENTS.md ROADMAP.md ARCHITECTURE.md README.md PROMPT.md
src/App/             Program, ServiceHost, ServiceManager, ServiceRunner, MainForm,
                     WebBridge, Cli, SelfTest, UnitTest, SmokeTest, IntegrationTest,
                     HeadlessTest, Diagnostics, HeadlessConsoleWindow
src/Core/            Paths, Json, AtomicFile, Log, Settings, SingleInstance, PipeServer,
                     PipeClient, ProcessJob, SessionLauncher, SelfHost, NodeRuntime,
                     Brand, Resources
src/Vm/              VmDefinition, VmStore, VmManager, VmProcess, CheckpointStore,
                     PresetCatalog, V86State, V86Assets, VmArchive, ForwardRule
src/VmHost/          VmHostForm, VmHostRunner, VmResourceServer, PortForwarder, WispRelay
web/                 manager.html, manager.js, console.html, console.js, theme.css, i18n/
engine/              vmhost.mjs (Node), package.json (v86 pinned), node_modules (npm install)
assets/              hv86.ico, presets.json, v86/<version>/{libv86.js,v86.wasm,seabios.bin,vgabios.bin}
docs/research/       v86-api.md, webview2-service.md, ft-design.md, wisp.md, headless.md
tools/               fetch-webview2.ps1, fetch-v86.ps1, fetch-engine.ps1, make-icon.ps1,
                     build.cmd, publish.cmd, selftest.ps1, preview-manager.ps1
```

## 10. Что считается «готово» для v1

Служба ставится/удаляется, переживает перезагрузку; в UI создаётся ВМ из ISO/пресета,
грузится Linux, работает клавиатура/мышь, есть снапшот с revert, а при убийстве vmhost
ВМ сама поднимается с последнего чекпоинта и это видно в журнале.

## 11. Как это проверяется

Четыре уровня, все без ручных кликов (`tools/selftest.ps1`):

| команда | проверяет |
|---|---|
| `--unit` | формат state v86, применение блоков диска, JSON, пути |
| `--selftest` | вшитые ресурсы и их размеры, WebView2 Runtime, каталог данных, пресеты, служба |
| `--smoke <образ>` | настоящая загрузка гостя → save_state → перезапуск из state → рост счётчика инструкций |
| `--itest` | движок + vmhost: create → boot → snapshot → kill vmhost → автоподъём → revert → delete |
| `--htest` | headless: ВМ в Node, текст FreeDOS на экране, снапшот, остановка (нужен Node 20+) |

Дополнительно `tools\preview-manager.ps1` собирает страницу менеджера в обычный HTML
с демо-данными: вёрстку можно проверять в браузере, не собирая exe и не поднимая службу.