/*
 * hv86 - страница консоли ВМ.
 *
 * Держит один экземпляр v86 и разговаривает с хостом (vmhost) через
 * chrome.webview.postMessage. Никакой бизнес-логики: файлы, снапшоты,
 * показ/скрытие окна и политики - на стороне C#.
 *
 * Протокол (страница -> хост):
 *   {action:"ready"}                              страница загрузилась
 *   {action:"log", text}                          строка в лог
 *   {action:"state", state}                       running/paused/failed/…
 *   {action:"metrics", instructions, mips}        раз в секунду
 *   {action:"ctrl", cmd}                          run/pause/reset/snapshot/iso/eject/hide/screenshot
 *   {action:"stateChunk", token, seq, data}       кусок state-файла (base64)
 *   {action:"stateDone", token, size, ms}
 *   {action:"stateError", token, message}
 *   {action:"screenshot", data}                   data:image/png;base64,…
 *   {action:"error", message}
 *
 * Хост -> страница:
 *   {action:"boot", config}                       создать эмулятор
 *   {action:"ack", token, seq}                    подтверждение куска state
 *   {action:"attach", kind, url, size}            вставить образ
 *   {action:"detach", kind}                       извлечь образ
 *   {action:"toast", text, kind}
 */
(function () {
    "use strict";

    var post = function (message) {
        try { chrome.webview.postMessage(message); } catch (e) { /* страница закрывается */ }
    };

    var byId = function (id) { return document.getElementById(id); };

    var emu = null;
    var config = null;
    var state = "starting";
    var lastInstructions = 0;
    var lastTime = 0;
    var acks = new Map();
    var tcpSessions = new Map();

    // ------------------------------------------------------------- утилиты

    function setState(next, detail) {
        state = next;
        var dot = byId("dot");
        dot.className = "dot " + (next === "running" ? "running" : next === "failed" ? "failed" : "paused");
        byId("stat").textContent = detail || next;
        post({ action: "state", state: next });
    }

    function toBase64(bytes) {
        var chunk = 0x8000;
        var parts = "";
        for (var i = 0; i < bytes.length; i += chunk) {
            parts += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + chunk, bytes.length)));
        }
        return btoa(parts);
    }

    function fromBase64(text) {
        var binary = atob(text);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }

    function waitAck(token, seq) {
        return new Promise(function (resolve) {
            acks.set(token + ":" + seq, resolve);
        });
    }

    window.__hv86Ack = function (token, seq) {
        var key = token + ":" + seq;
        var resolve = acks.get(key);
        if (resolve) { acks.delete(key); resolve(); }
    };

    // --------------------------------------------------------------- boot

    function buildOptions(c) {
        var options = {
            memory_size: c.memory,
            vga_memory_size: c.vgaMemory,
            bios: { url: "/v86/seabios.bin" },
            vga_bios: { url: "/v86/vgabios.bin" },
            wasm_path: "/v86/v86.wasm",
            screen_container: byId("screen_container"),
            autostart: false,
            disable_speaker: false,
            fastboot: true
        };

        if (c.net) options.net_device = { type: c.net.type || "virtio", relay_url: c.net.relayUrl || "fetch" };
        if (c.bootOrder) options.boot_order = c.bootOrder;

        if (c.hda) options.hda = { url: "/media/hda", size: c.hda.size };
        if (c.cdrom) options.cdrom = { url: "/media/cdrom", size: c.cdrom.size };
        if (c.fda) options.fda = { url: "/media/fda", size: c.fda.size };

        if (c.initialState) options.initial_state = { url: c.initialState };

        return options;
    }

    async function boot(c) {
        config = c;

        byId("vm-name").textContent = c.name || c.id || "VM";
        byId("vm-id").textContent = c.id || "";

        if (emu) {
            try { await emu.destroy(); } catch (e) { /* уже мёртв */ }
            emu = null;
        }

        acks.clear();

        if (typeof V86 === "undefined") {
            post({ action: "error", message: "libv86.js не загрузился" });
            setState("failed", "no v86");
            return;
        }

        setState(c.initialState ? "restoring" : "starting", c.initialState ? "restoring" : "booting");

        try {
            emu = new V86(buildOptions(c));
        } catch (e) {
            post({ action: "error", message: String(e && e.message || e) });
            setState("failed", String(e && e.message || e));
            return;
        }

        emu.add_listener("emulator-ready", function () {
            post({ action: "log", text: "emulator ready" });
            try { emu.run(); } catch (e) { post({ action: "error", message: String(e) }); }
        });

        emu.add_listener("emulator-started", function () { setState("running", "running"); });
        emu.add_listener("emulator-stopped", function () { setState("paused", "paused"); });

        emu.add_listener("screen-set-size", function (size) {
            post({ action: "log", text: "screen " + size[0] + "x" + size[1] + "x" + size[2] });
        });

        emu.add_listener("mouse-enable", function (enabled) {
            byId("foot-right").textContent = enabled ? "mouse captured" : "";
        });

        // страховка: если ready не пришёл (например, образ грузится долго),
        // всё равно пытаемся запустить через 4 секунды
        setTimeout(function () {
            if (emu && !emu.is_running()) {
                try { emu.run(); } catch (e) { /* ниже будет metrics с нулями */ }
            }
        }, 4000);
    }

    // ------------------------------------------------------------- метрики

    setInterval(function () {
        if (!emu) return;

        var instructions = 0;
        var running = false;

        try { instructions = emu.get_instruction_counter(); } catch (e) { return; }
        try { running = emu.is_running(); } catch (e) { }

        var now = Date.now();
        var mips = 0;

        if (lastTime > 0 && now > lastTime && instructions >= lastInstructions) {
            mips = Math.round((instructions - lastInstructions) / (now - lastTime) / 1000);
        }

        lastInstructions = instructions;
        lastTime = now;

        post({ action: "metrics", instructions: instructions, mips: mips, running: running });

        var total = instructions / 1e9;
        byId("stat").textContent = (running ? "running" : "paused") +
            " · " + mips + " MIPS · " + (total >= 1 ? total.toFixed(2) + "G" : Math.round(instructions / 1e6) + "M") + " instr";
    }, 1000);

    // --------------------------------------------------------------- state

    async function saveState(message) {
        var token = message.token || "t";
        var started = performance.now();

        try {
            if (!emu) throw new Error("эмулятор не создан");

            var buffer = await emu.save_state();
            var bytes = new Uint8Array(buffer);

            var CHUNK = 512 * 1024;
            var seq = 0;

            for (var offset = 0; offset < bytes.length; offset += CHUNK, seq++) {
                var slice = bytes.subarray(offset, Math.min(offset + CHUNK, bytes.length));
                post({ action: "stateChunk", token: token, seq: seq, data: toBase64(slice) });
                await waitAck(token, seq);
            }

            post({
                action: "stateDone",
                token: token,
                size: bytes.length,
                ms: Math.round(performance.now() - started),
                instructions: emu.get_instruction_counter()
            });
        } catch (e) {
            post({ action: "stateError", token: token, message: String(e && e.message || e) });
        }
    }

    // ------------------------------------------------------------ уничтожение

    async function destroy() {
        tcpCloseAll();

        try {
            if (emu) await emu.destroy();
        } catch (e) {
            // уже уничтожен
        }

        emu = null;
        acks.clear();
        setState("stopped", "stopped");
    }

    // ------------------------------------------------------------ скриншот

    function screenshot() {
        try {
            var canvas = document.querySelector("#screen_container canvas");
            if (!canvas || !canvas.width || !canvas.height || canvas.style.display === "none") {
                post({ action: "toast", text: "Скриншот доступен только в графическом режиме", kind: "bad" });
                return;
            }

            post({ action: "screenshot", data: canvas.toDataURL("image/png") });
        } catch (e) {
            post({ action: "error", message: String(e && e.message || e) });
        }
    }

    // --------------------------------------------------------- проброс портов

    async function tcpOpen(message) {
        var session = message.session;

        try {
            if (!emu || !emu.network_adapter) throw new Error("сеть в этой ВМ выключена");

            var open = await emu.network_adapter.tcp_probe(message.port);

            if (!open) {
                post({ action: "tcpClosed", session: session, error: "refused" });
                return;
            }

            var connection = emu.network_adapter.connect(message.port);
            tcpSessions.set(session, connection);

            connection.on("connect", function () {
                post({ action: "tcpOpened", session: session });
            });

            connection.on("data", function (data) {
                post({ action: "tcpData", session: session, data: toBase64(data) });
            });

            connection.on("close", function () {
                tcpSessions.delete(session);
                post({ action: "tcpClosed", session: session, error: "closed" });
            });

            connection.on("shutdown", function () {
                tcpSessions.delete(session);
                post({ action: "tcpClosed", session: session, error: "shutdown" });
            });
        } catch (e) {
            tcpSessions.delete(session);
            post({ action: "tcpClosed", session: session, error: String(e && e.message || e) });
        }
    }

    function tcpWrite(message) {
        var connection = tcpSessions.get(message.session);
        if (!connection) return;

        try {
            connection.write(fromBase64(message.data));
        } catch (e) {
            tcpSessions.delete(message.session);
            post({ action: "tcpClosed", session: message.session, error: String(e && e.message || e) });
        }
    }

    function tcpClose(message) {
        var connection = tcpSessions.get(message.session);
        tcpSessions.delete(message.session);

        if (!connection) return;
        try { connection.close(); } catch (e) { /* уже закрыт */ }
    }

    function tcpCloseAll() {
        tcpSessions.forEach(function (connection) {
            try { connection.close(); } catch (e) { /* уже закрыт */ }
        });

        tcpSessions.clear();
    }

    // ------------------------------------------------------------- команды

    function command(cmd) {
        if (!emu) return;

        try {
            switch (cmd) {
                case "run":
                    emu.run();
                    setState("running", "running");
                    break;

                case "pause":
                    emu.stop();
                    setState("paused", "paused");
                    break;

                case "reset":
                    emu.restart();
                    setState("running", "rebooting");
                    break;

                case "eject":
                    post({ action: "ctrl", cmd: "eject" });
                    break;

                case "snapshot":
                case "iso":
                case "hide":
                    post({ action: "ctrl", cmd: cmd });
                    break;

                case "screenshot":
                    screenshot();
                    break;
            }
        } catch (e) {
            post({ action: "error", message: String(e && e.message || e) });
        }
    }

    // ------------------------------------------------------------- приём

    if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener("message", function (event) {
        var message = event.data;
        if (!message || typeof message !== "object") return;

        switch (message.action) {
            case "boot":
                boot(message.config || {});
                break;

            case "ack":
                window.__hv86Ack(message.token, message.seq);
                break;

            case "saveState":
                saveState(message);
                break;

            case "freeze":
                try {
                    if (emu) { emu.stop(); setState("paused", "paused"); }
                } catch (e) { post({ action: "error", message: String(e && e.message || e) }); }
                break;

            case "thaw":
                try {
                    if (emu) { emu.run(); setState("running", "running"); }
                } catch (e) { post({ action: "error", message: String(e && e.message || e) }); }
                break;

            case "reset":
                try {
                    if (emu) { emu.restart(); setState("running", "rebooting"); }
                } catch (e) { post({ action: "error", message: String(e && e.message || e) }); }
                break;

            case "destroy":
                destroy();
                break;

            case "tcpConnect":
                tcpOpen(message);
                break;

            case "tcpWrite":
                tcpWrite(message);
                break;

            case "tcpClose":
                tcpClose(message);
                break;

            case "attach":
                try {
                    var image = { url: message.url, size: message.size };
                    if (message.kind === "fda") emu.set_fda(image);
                    else emu.set_cdrom(image);
                } catch (e) {
                    post({ action: "error", message: String(e && e.message || e) });
                }
                break;

            case "detach":
                try {
                    if (message.kind === "fda") emu.eject_fda();
                    else emu.eject_cdrom();
                } catch (e) {
                    post({ action: "error", message: String(e && e.message || e) });
                }
                break;

            case "toast":
                post({ action: "toast", text: message.text, kind: message.kind });
                break;
        }
    });

    // ------------------------------------------------------- кнопки и старт

    var buttons = document.querySelectorAll(".cbtn");
    for (var i = 0; i < buttons.length; i++) {
        buttons[i].addEventListener("click", function () {
            command(this.getAttribute("data-cmd"));
        });
    }

    post({ action: "ready" });
})();