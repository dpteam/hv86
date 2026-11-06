# v86 — выжимка API (проверено по npm 0.5.473 + master)

Источники: `https://github.com/copy/v86` (BSD-2-Clause), `v86.d.ts`, `examples/nodejs.js`,
содержимое npm-пакета `v86@0.5.473` (проверено локально), README (список эмуляции).

## Пакет npm `v86`

```
build/libv86.js     351 KB   (UMD, для WebView2)
build/libv86.mjs    351 KB   (ESM, для Node)
build/v86.wasm     1.91 MB   (CPU-ядро, Rust→wasm)
build/v86-debug.wasm 12 MB   (отладочное, не шипим)
```
BIOS в npm-пакет **не входит**. Брать из репозитория:
`bios/seabios.bin` (131 072 B), `bios/vgabios.bin` (36 352 B) — обе отдаются с raw.githubusercontent.com.

## Конструктор

```js
const emu = new V86({  // раньше назывался V86Starter
  memory_size: 128 * 1024 * 1024,   // степень двойки
  vga_memory_size: 8 * 1024 * 1024,
  bios: { url: "v86/seabios.bin" } | { buffer: ArrayBuffer },
  vga_bios: { url: "v86/vgabios.bin" },
  hda: { url } | { buffer } | null, hdb, fda, fdb, cdrom,
  bzimage, initrd, cmdline,          // прямая загрузка Linux-ядра
  initial_state: { url } | { buffer }, // снапшот из save_state()
  boot_order: 0x123,                 // CD_HARDDISK_FLOPPY и т.п.
  screen: { container: el, scaling: 1, use_graphical_text: false },
  serial_console: { type: "textarea"|"xtermjs"|"none", container },
  net_device: { type: "ne2k"|"virtio", relay_url: "fetch", id: 0 },
  filesystem: { basefs, baseurl, handle9p(req, reply), proxy_url },
  acpi: false, virtio_balloon: false, cpuid_level: number|null,
  disable_jit: false, autostart: false, fastboot: false, bootmenu: false
});
```

## Методы

| Метод | Зачем нам |
|---|---|
| `run() / stop() / restart() / destroy()` | жизненный цикл ВМ |
| `save_state() → ArrayBuffer`, `restore_state(buf)` | **снапшоты и FT** |
| `is_running()`, `get_instruction_counter()`, `get_instruction_stats()` | heartbeat/метрики |
| `set_cdrom(img) / eject_cdrom()`, `set_fda/fdb`, `eject_*` | hot-swap медиа |
| `keyboard_send_scancodes/keys/text()` | ввод без DOM (консоль, автоматизация) |
| `serial0_send(str)`, `serial_send_bytes(n, u8)`, `serial_set_*` | serial-консоль, агент гостя |
| `read_memory(off, len) / write_memory(u8, off)` | диагностика, fallback-скриншот |
| `screen_make_screenshot()` | **только браузер** (возвращает `<img>`) |
| `wait_until_vga_screen_contains(str/RegExp)` | health-проверки, selftest |
| `screen_set_scale(sx, sy)`, `screen_go_fullscreen()`, `lock_mouse()` | UX консоли |
| `create_file/read_file` (9p) | обмен файлами с гостем |

### Клавиатура: три разных метода

| метод | что делает | нужен адаптер |
|---|---|---|
| `keyboard_send_scancodes([...], delay)` | сырые PS/2-сканкоды в шину (`bus.send("keyboard-code")`) | **нет** |
| `keyboard_send_keys([13], delay)` | «нажать клавишу» по *keyCode* (Enter = 13) | да |
| `keyboard_send_text(str, delay)` | текст → скан-коды по US-раскладке | да |

`keyboard_send_text/keys` дергают `keyboard_adapter`, поэтому при
`disable_keyboard: true` падают с `Cannot read properties of undefined
(reading 'simulate_char')` — проверено на Node. Сам адаптер DOM не требует
(DOM-часть под `typeof window`), так что в Node его можно включать.

`delay` — пауза **на символ** (мс). Без неё гость теряет нажатия: FreeDOS
при `keyboard_send_text("echo ...")` без задержки пропускает символы.
`\n` в тексте сам по себе Enter не нажимает — Enter отправляется
`keyboard_send_keys([13])`.

### Звук

- `disable_speaker: false` создаёт `AudioContext` + mixer + pc-speaker + DAC (SB16).
- Устройство `sb16` создаётся **всегда** (`devices.sb16 = new D(...)`), не по опции,
  и сериализуется в `save_state()` — то есть звук переживает снапшот.
- В Node звука нет (`window.AudioContext` отсутствует), поэтому там `disable_speaker: true`.
- Громкость/мьют снаружи не выведены: пришлось бы лезть в `speaker_adapter.audio_context`,
  а это внутренности v86.

## События (шина)

`emulator-ready`, `emulator-started`, `emulator-stopped`, `screen-set-size [w,h,bpp]`,
`serial0-output-byte`, `virtio-console0-output-bytes`, `net0-send`,
`eth-transmit-end/eth-receive-end`, `ide-read-end [ch,bytes,sectors]`,
`ide-write-end`, `download-progress`, `mouse-enable`, `vmware-absolute-mouse`,
`9p-attach/9p-read-start/9p-write-end`.

## Headless (Node) — важное ограничение

`examples/nodejs.js` работает: `bios/vga_bios/cdrom` по путям, serial через события, сеть
`net_device: { type: "virtio", relay_url: "fetch" }`. Но при отсутствии `screen.container`
v86 подставляет **текстовый адаптер** (`put_char`, `get_text_screen`, `get_text_row`, ANSI),
никаких `put_pixels`. То есть в Node доступен только VGA-**текст** (80×25 и т.п.),
графические режимы снять нечем, `screen_make_screenshot()` вне браузера не работает.

Обходные пути (по возрастанию надёжности):
1. `read_memory(0xA0000…)` + декодирование режима — костыль, но бесплатно для DOS/BIOS-экранов.
2. Shim DOM/Canvas (`document.createElement("canvas")`, `ImageData`, `OffscreenCanvas`,
   `requestAnimationFrame`) поверх `@napi-rs/canvas` → настоящий `ScreenAdapter` и кадры.
3. Держать ВМ в WebView2 (Chromium) — полная совместимость, но только в сессии пользователя.

## Сеть

- `relay_url: "fetch"` — встроенный user-mode NAT: DHCP выдаёт `192.168.86.100`,
  шлюз/DNS `192.168.86.1`, DNS `static` или `doh`. Наружу ходит через `fetch` хоста.
- `wss://…` — внешний relay (нужен свой сервер, зато возможен порт-форвард на гостя).
- v86 «network id»: несколько ВМ с одинаковым id — в одной виртуальной сети.

## Состояние/снапшоты

- `save_state()` отдаёт RAM + регистры CPU + состояние устройств; **диски не входят**.
- `restore_state()` требует те же опции (`memory_size`, те же образы) и **ту же версию v86**.
- Есть поддержка `.zst` для state-файлов (встроенный zstd-декодер); мы для контроля
  используем свой Brotli на стороне C# и грузим через `{ buffer }`.

## Ограничения гостей (для визарда и ожиданий)

- Linux до i386-эпохи (Alpine, Buildroot, Debian ≤ 18.04, Arch32) — хорошо.
- ReactOS, FreeDOS/MS-DOS, Win 1.01/3.x, KolibriOS, Haiku, 9front, QNX, FreeBSD — ок.
- Win 95/98/ME/NT/2000 — «reasonably well», Win2000 требует Standard PC.
- Win XP/Vista/8 — «при условиях» (issue #86, #208); 64-бит нет вообще.

## Лицензии

v86 — BSD-2-Clause (можно шипить в закрытом продукте, сохранив копирайт);
SoftFloat/zstd/qemu-части — свои лицензии в `licenses/`. Наш код — MIT.