/*
 * hv86 - headless-хост ВМ.
 *
 * Тот же самый протокол, что у `hv86.exe --vmhost`: named pipe
 * \\.\pipe\hv86-vm-<id>, построчный JSON-RPC + события. Поэтому служба,
 * снапшоты, чекпоинты и watchdog работают без изменений, а разница только
 * в том, кто исполняет v86 - WebView2 или Node.
 *
 * Почему Node: служба живёт в сессии 0, где WebView2 не работает вообще.
 * Здесь нет DOM, поэтому и графики нет: v86 в Node отдаёт VGA-текст
 * (`screen: {ansi:true}` → get_text_screen с ANSI-цветами). Этого хватает
 * консольным гостам (Linux tty, DOS) - им и нужен headless-режим.
 *
 * Запуск:
 *   node engine/vmhost.mjs --config <путь к json с конфигом ВМ>
 */
import fs from "node:fs";
import net from "node:net";
import { V86 } from "v86";
import { createShareHandler } from "./fs9p.mjs";
import { createNodeFs } from "./fs-node.mjs";

const STREAM_PREFIX = "\\\\.\\pipe\\hv86-vm-";
const SCREEN_POLL_MS = 120;
const METRICS_MS = 1000;

// ------------------------------------------------------------------ аргументы

function parseArgs(argv) {
    const args = {};

    for (let i = 0; i < argv.length; i++) {
        if (!argv[i].startsWith("--")) continue;
        const key = argv[i].slice(2);
        args[key] = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : "true";
    }

    return args;
}

const args = parseArgs(process.argv.slice(2));

if (!args.config) {
    console.error("usage: node vmhost.mjs --config <path>");
    process.exit(2);
}

const config = JSON.parse(fs.readFileSync(args.config, "utf8"));

const log = (text) => console.log(`[${new Date().toISOString().slice(11, 23)}] ${text}`);

// ------------------------------------------------------------------- состояние

let emulator = null;
let state = "starting";
let error = "";
let instructions = 0;
let mips = 0;
let lastInstructions = 0;
let lastTime = 0;
let lastScreen = null;
let shuttingDown = false;

const clients = new Set();

function broadcast(event, data) {
    const line = JSON.stringify({ event, data }) + "\n";

    for (const socket of clients) {
        try { socket.write(line); } catch { /* клиент отвалился */ }
    }
}

function setState(next, detail) {
    if (state === next) return;
    state = next;

    log("state: " + next + (detail ? " (" + detail + ")" : ""));
    broadcast("vm.state", { id: config.id, state, error });
}

// ---------------------------------------------------------------- эмулятор

function buildOptions(cfg) {
    const options = {
        memory_size: cfg.memory,
        vga_memory_size: cfg.vgaMemory,
        bios: { url: cfg.bios },
        vga_bios: { url: cfg.vgaBios },
        wasm_path: cfg.wasm,
        screen: { ansi: true },
        autostart: false,
        disable_speaker: true,
        disable_keyboard: true,
        disable_mouse: true,
        fastboot: true
    };

    if (cfg.netType) options.net_device = { type: cfg.netType, relay_url: "fetch" };
    if (cfg.bootOrder) options.boot_order = cfg.bootOrder;

    if (cfg.hda) options.hda = { url: cfg.hda };
    if (cfg.cdrom) options.cdrom = { url: cfg.cdrom };
    if (cfg.fda) options.fda = { url: cfg.fda };

    // общая папка: 9p-сервер поверх настоящего каталога хоста
    if (cfg.share && cfg.share.path) {
        if (!fs.existsSync(cfg.share.path)) {
            log("share: каталог не найден, пропускаю: " + cfg.share.path);
        } else {
            options.filesystem = {
                handle9p: createShareHandler({
                    fs: createNodeFs(cfg.share.path),
                    readonly: !!cfg.share.readonly,
                    log: (text) => log("9p: " + text)
                })
            };

            log("share: " + cfg.share.path + (cfg.share.readonly ? " (ro)" : " (rw)"));
        }
    }

    if (cfg.initialState) options.initial_state = { url: cfg.initialState };

    return options;
}

function boot(cfg) {
    log("boot: memory=" + (cfg.memory / 1024 / 1024) + "M");

    try {
        emulator = new V86(buildOptions(cfg));
    } catch (e) {
        error = String(e && e.message || e);
        log("не удалось создать эмулятор: " + error);
        setState("failed", error);
        return;
    }

    emulator.add_listener("emulator-ready", () => {
        log("emulator ready");
        try { emulator.run(); } catch (e) { log("run: " + e.message); }
    });

    emulator.add_listener("emulator-started", () => setState("running"));
    emulator.add_listener("emulator-stopped", () => setState("paused"));

    // страховка: если ready не пришёл, всё равно пробуем запустить
    setTimeout(() => {
        try {
            if (emulator && !emulator.is_running()) emulator.run();
        } catch { /* ниже metrics покажет нули */ }
    }, 4000);
}

// ---------------------------------------------------------------- экран/метрики

function readScreen() {
    if (!emulator || !emulator.screen_adapter || !emulator.screen_adapter.get_text_screen) return null;

    try { return emulator.screen_adapter.get_text_screen(); }
    catch { return null; }
}

function pollScreen() {
    const rows = readScreen();
    if (!rows) return;

    const joined = rows.join("\n");
    if (joined === lastScreen) return;

    lastScreen = joined;

    broadcast("vm.screen", {
        id: config.id,
        cols: rows.length ? rows[0].length : 0,
        rows: rows.length,
        lines: rows
    });
}

setInterval(pollScreen, SCREEN_POLL_MS);

setInterval(() => {
    if (!emulator) return;

    try { instructions = emulator.get_instruction_counter(); } catch { return; }

    const now = Date.now();

    if (lastTime > 0 && now > lastTime && instructions >= lastInstructions) {
        mips = Math.round((instructions - lastInstructions) / (now - lastTime) / 1000);
    }

    lastInstructions = instructions;
    lastTime = now;

    broadcast("vm.metrics", {
        id: config.id,
        state,
        instructions,
        mips,
        engine: "headless",
        cols: lastScreen ? 80 : 0
    });
}, METRICS_MS);

// ------------------------------------------------------ сохранение диска

/*
 * Состояние v86 устроено так:
 *   0..16   заголовок (magic, версия, общая длина, длина JSON)
 *   16..    JSON {buffer_infos, state}
 *   далее   буферы, выровненные по 4
 *
 * В state попадает кэш записанных блоков диска: массив пар
 * [номер блока, Uint8Array(256)]. Именно их и надо положить в образ,
 * иначе изменения гостя потеряются при остановке.
 */
function applyDiskBlocks(stateBuffer, diskPath) {
    if (!diskPath || !fs.existsSync(diskPath)) return 0;

    const view = new DataView(stateBuffer);
    const magic = view.getInt32(0, true);

    if (magic !== 0x86768676) throw new Error("это не state-файл v86");

    const infoLength = view.getInt32(12, true);
    const info = JSON.parse(new TextDecoder().decode(new Uint8Array(stateBuffer, 16, infoLength)));

    const offsets = info.buffer_infos.map((b) => b.offset);
    const lengths = info.buffer_infos.map((b) => b.length);
    const buffersStart = (16 + infoLength + 3) & ~3;

    const blocks = [];

    const walk = (node) => {
        if (!Array.isArray(node)) return;

        if (node.length === 2 && typeof node[0] === "number" &&
            node[1] && typeof node[1] === "object" &&
            node[1].__state_type__ === "Uint8Array") {
            const id = node[1].buffer_id;
            if (lengths[id] === 256) { blocks.push([id, node[0]]); return; }
        }

        for (const item of node) walk(item);
    };

    walk(info.state);

    if (blocks.length === 0) return 0;

    const fd = fs.openSync(diskPath, "r+");
    const size = fs.fstatSync(fd).size;
    const block = Buffer.alloc(256);
    let applied = 0;

    try {
        for (const [bufferId, index] of blocks) {
            const target = index * 256;
            if (target < 0 || target + 256 > size) continue;

            Buffer.from(stateBuffer, buffersStart + offsets[bufferId], 256).copy(block);
            fs.writeSync(fd, block, 0, 256, target);
            applied++;
        }
    } finally {
        fs.closeSync(fd);
    }

    return applied;
}

// ---------------------------------------------------------------- RPC

async function saveState(path) {
    if (!emulator) throw new Error("эмулятор не создан");

    const started = Date.now();

    const buffer = await emulator.save_state();
    const bytes = Buffer.from(buffer);

    fs.mkdirSync(path.substring(0, path.lastIndexOf("\\")) || ".", { recursive: true });
    fs.writeFileSync(path, bytes);

    return {
        path,
        size: bytes.length,
        ms: Date.now() - started,
        instructions: emulator.get_instruction_counter()
    };
}

async function shutdown() {
    if (shuttingDown) return;
    shuttingDown = true;

    log("shutdown");

    try {
        const tempDir = config.tempDir;
        const statePath = tempDir + "\\shutdown-" + Date.now() + ".bin";

        fs.mkdirSync(tempDir, { recursive: true });

        try { emulator.stop(); } catch { /* уже стоит */ }

        let buffer = null;

        try {
            buffer = Buffer.from(await emulator.save_state());
            fs.writeFileSync(statePath, buffer);
            log("state при остановке: " + Math.round(buffer.length / 1024 / 1024) + " МБ");
        } catch (e) {
            log("state не снят: " + e.message);
        }

        if (buffer && config.hda) {
            try {
                const applied = applyDiskBlocks(buffer, config.hda);
                log("применено блоков диска: " + applied);
            } catch (e) {
                log("диск: " + e.message);
            }
        }

        try { fs.unlinkSync(statePath); } catch { }

        try { await emulator.destroy(); } catch { }
    } catch (e) {
        log("shutdown: " + e.message);
    }

    setState("stopped");

    setTimeout(() => process.exit(0), 100);
}

function keyInput(params) {
    if (!emulator) return;

    if (params.scancodes && params.scancodes.length) {
        emulator.keyboard_send_scancodes(params.scancodes);
        return;
    }

    if (typeof params.text === "string" && params.text.length) {
        emulator.keyboard_send_text(params.text);
    }
}

async function handle(method, params) {
    switch (method) {
        case "vm.status":
            return {
                id: config.id, state, error, instructions, mips,
                engine: "headless", cols: 80, rows: 25, relay_port: 0, relay_clients: 0
            };

        case "vm.pause":
            try { emulator.stop(); } catch { /* уже */ }
            setState("paused");
            return { state };

        case "vm.resume":
            try { emulator.run(); } catch { /* уже */ }
            setState("running");
            return { state };

        case "vm.reset":
            try { emulator.restart(); } catch { /* уже */ }
            setState("running");
            return { state };

        case "vm.saveState":
            return await saveState(params.path);

        case "vm.mount":
            if (params.kind === "fda") emulator.set_fda({ url: params.path });
            else emulator.set_cdrom({ url: params.path });
            return { ok: true };

        case "vm.eject":
            if (params.kind === "fda") emulator.eject_fda();
            else emulator.eject_cdrom();
            return { ok: true };

        case "vm.key":
            keyInput(params || {});
            return { ok: true };

        case "vm.screen": {
            const rows = readScreen() || [];
            return { cols: rows.length ? rows[0].length : 0, rows: rows.length, lines: rows };
        }

        case "vm.forwards":
            // в headless v1 проброс портов не поддержан: релей/fetch-стек
            // живёт в странице, а её здесь нет
            return { count: 0, dropped: 0 };

        case "vm.show":
        case "vm.hide":
            return { ok: true };

        case "vm.shutdown":
            setTimeout(shutdown, 10);
            return { ok: true };
    }

    throw new Error("неизвестный метод: " + method);
}

// ---------------------------------------------------------------- сервер

const pipePath = STREAM_PREFIX + config.id;

const server = net.createServer((socket) => {
    clients.add(socket);
    log("клиент подключился (" + clients.size + ")");

    socket.setEncoding("utf8");

    let buffer = "";

    socket.on("data", async (chunk) => {
        buffer += chunk;

        let index;

        while ((index = buffer.indexOf("\n")) >= 0) {
            const line = buffer.slice(0, index).trim();
            buffer = buffer.slice(index + 1);

            if (!line) continue;

            let request;

            try { request = JSON.parse(line); }
            catch { socket.write(JSON.stringify({ id: 0, ok: false, error: "bad-json" }) + "\n"); continue; }

            const id = request.id ?? 0;

            try {
                const result = await handle(request.method, request.params || {});
                socket.write(JSON.stringify({ id, ok: true, result }) + "\n");
            } catch (e) {
                socket.write(JSON.stringify({ id, ok: false, error: String(e && e.message || e) }) + "\n");
            }
        }
    });

    socket.on("close", () => {
        clients.delete(socket);
        log("клиент отключился (" + clients.size + ")");
    });

    socket.on("error", () => {
        clients.delete(socket);
    });

    // новый клиент сразу получает экран, иначе окно будет пустым до изменения
    const rows = readScreen();
    if (rows) {
        socket.write(JSON.stringify({
            event: "vm.screen",
            data: { id: config.id, cols: rows.length ? rows[0].length : 0, rows: rows.length, lines: rows }
        }) + "\n");
    }
});

server.listen(pipePath, () => {
    log("pipe слушает " + pipePath);
    boot(config);
});

process.on("SIGTERM", () => shutdown());
process.on("SIGINT", () => shutdown());
process.on("uncaughtException", (e) => {
    error = String(e && e.message || e);
    log("необработанная ошибка: " + error);
    broadcast("vm.error", { id: config.id, message: error });
});