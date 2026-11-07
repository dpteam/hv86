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
 *   {action:"ctrl", cmd}                          run/pause/reset/snapshot/iso/eject/hide/screenshot/floppy-prev/floppy-next
 *   {action:"stateChunk", token, seq, data}       кусок state-файла (base64)
 *   {action:"stateDone", token, size, ms}
 *   {action:"stateError", token, message}
 *   {action:"screenshot", data}                   data:image/png;base64,…
 *   {action:"error", message}
 *   {action:"paste"}                              Ctrl+V: дай текст из буфера хоста
 *   {action:"typed", ok, chars|error}             текст набран (ответ на type)
 *   {action:"xhrResult"} / {action:"restoreDone"} / {action:"fetchResult"}
 *
 * Хост -> страница:
 *   {action:"boot", config}                       создать эмулятор
 *   {action:"ack", token, seq}                    подтверждение куска state
 *   {action:"attach", kind, url, size, index, count}  вставить образ (для набора дискет - какая именно)
 *   {action:"detach", kind}                       извлечь образ
 *   {action:"type", text}                         набрать текст в гостя
 *   {action:"fetchProbe", url}                    диагностика egress
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

    var scaleMode = storageGet("hv86.scale", "1");   // запоминаем на эту ВМ (свой профиль WebView2)
    var appliedScale = 1;
    var nativeSize = { w: 0, h: 0 };
    var captured = false;

    // комплект дискет: сколько их и какая вставлена (листает служба)
    var floppyIndex = 0;
    var floppyCount = 0;

    function storageGet(key, fallback) {
        try { return localStorage.getItem(key) || fallback; } catch (e) { return fallback; }
    }

    function storageSet(key, value) {
        try { localStorage.setItem(key, value); } catch (e) { /* хранилище недоступно */ }
    }

    // --------------------------------------------------------------- шрифт текста

    // Текстовый режим v86 рисует <div> с построчным текстом: шрифт задаётся
    // CSS, поэтому кастомизация сводится к font-family/size на этом слое.
    // На гостя это не влияет - он про шрифт ничего не знает.
    //
    // ВАЖНО: размер и межстрочный интервал ставим ВСЕГДА, даже когда шрифт не
    // выбран. Иначе слой наследует line-height контейнера, а там 0 - и все
    // строки схлопываются в одну полосу (это был реальный баг).
    var textFontDefault = 14;
    var fontFacesReady = false;

    // Свои шрифты приходят в boot-конфиге как [{family, url}]: подключаем их
    // через FontFace API - так они сразу доступны и для CSS, и для canvas.
    async function ensureFontFaces(fonts) {
        if (fontFacesReady || !fonts || !fonts.length) return;
        if (!window.FontFace) return;

        fontFacesReady = true;

        for (var i = 0; i < fonts.length; i++) {
            try {
                var face = new FontFace(fonts[i].family, "url(" + fonts[i].url + ")");
                await face.load();
                document.fonts.add(face);

                post({ action: "log", text: "шрифт подключён: " + fonts[i].family });
            } catch (e) {
                post({ action: "log", text: "шрифт " + fonts[i].family + " не загрузился: " + (e && e.message || e) });
            }
        }
    }

    function applyTextFont(font, size) {
        var layer = document.querySelector("#screen_container > div");
        if (!layer) return;

        var family = String(font || "").replace(/"/g, "").trim();
        var px = Number(size) || 0;

        if (px <= 0) px = textFontDefault;

        layer.style.fontFamily = (family.length > 0 ? '"' + family + '", ' : "") + "monospace";
        layer.style.fontSize = px + "px";
        layer.style.lineHeight = px + "px";
        layer.style.whiteSpace = "pre";

        // размер ячейки изменился - пересчитываем масштаб
        requestAnimationFrame(function () { measureNative(); applyScale(); });
    }

    // --------------------------------------------------------------- дискеты

    // Комплект дискет (установочные диски DOS): их несколько, вставляется одна.
    // Само листание делает служба (она владеет vm.json), страница только просит
    // и показывает «2/5». Кнопки видны лишь при наборе больше одной дискеты.
    function setFloppy(index, count) {
        if (typeof count === "number" && count >= 0) floppyCount = count;
        if (typeof index === "number" && index >= 0) floppyIndex = index;

        var group = byId("floppy-group");
        var label = byId("floppy-count");

        if (group) group.classList.toggle("hidden", floppyCount <= 1);

        if (label) {
            label.textContent = (floppyIndex + 1) + "/" + Math.max(floppyCount, 1);

            label.classList.add("flash");
            setTimeout(function () { label.classList.remove("flash"); }, 600);
        }
    }

    // ---------------------------------------------------------------- масштаб

    // Масштабируем через CSS `zoom`, а не через screen_set_scale и не через
    // transform. Причина: v86 для текстового режима ставит transform, а
    // transform НЕ меняет layout - контейнер остаётся прежним, flex-центрирование
    // врёт, и увеличенный экран обрезается по краям. `zoom` меняет layout, так
    // что и центрирование, и прокрутка остаются честными.
    function applyScale() {
        var cont = byId("screen_container");
        var wrap = document.querySelector(".screen-wrap");

        if (!cont || !wrap) return;

        var k = 1;

        if (scaleMode === "fit") {
            if (nativeSize.w > 0 && nativeSize.h > 0) {
                var w = wrap.clientWidth - 12;
                var h = wrap.clientHeight - 12;

                if (w > 0 && h > 0) k = Math.min(w / nativeSize.w, h / nativeSize.h);
            }
        } else {
            k = Number(scaleMode) || 1;
        }

        k = Math.max(0.25, Math.min(6, k));

        cont.style.zoom = k;

        appliedScale = k;
        markScale();
    }

    // Снимаем собственный масштаб v86: он то ставит canvas'у удвоенный CSS-размер
    // (для мелких экранов), то transform текстовому слою. Нам нужен родной
    // размер, дальше масштабируем сами.
    function normalizeScreen() {
        try { if (emu) emu.screen_set_scale(1, 1); } catch (e) { }

        var canvas = document.querySelector("#screen_container canvas");

        if (canvas && canvas.width && canvas.height) {
            canvas.style.width = canvas.width + "px";
            canvas.style.height = canvas.height + "px";
        }

        var text = document.querySelector("#screen_container > div");
        if (text) text.style.transform = "";
    }

    function markScale() {
        var names = { "1": "scale-1", "2": "scale-2", fit: "scale-fit" };
        var active = names[scaleMode] || "scale-1";

        var buttons = document.querySelectorAll("[data-cmd^='scale-']");
        for (var i = 0; i < buttons.length; i++)
            buttons[i].classList.toggle("on", buttons[i].getAttribute("data-cmd") === active);
    }

    function measureNative() {
        var canvas = document.querySelector("#screen_container canvas");
        var text = document.querySelector("#screen_container > div");

        var w = 0, h = 0;

        // текстовый слой виден в текстовом режиме, canvas - в графическом;
        // пустой слой даёт нули, поэтому пробуем оба
        if (text && text.offsetWidth > 0 && text.offsetHeight > 0) {
            w = text.offsetWidth;
            h = text.offsetHeight;
        }

        if ((w <= 0 || h <= 0) && canvas && canvas.offsetWidth > 0 && canvas.offsetHeight > 0) {
            w = canvas.offsetWidth;
            h = canvas.offsetHeight;
        }

        if (w > 0 && h > 0) nativeSize = { w: w, h: h };
    }

    function setScaleMode(mode) {
        scaleMode = mode;
        storageSet("hv86.scale", mode);
        applyScale();
    }

    // ------------------------------------------------------------ захват мыши

    // v86 умеет захватывать указатель (pointer lock): только так гость
    // получает относительные движения, не упираясь в края окна. Захват -
    // по кнопке или кликом по экрану (если гость включил мышь), выход -
    // настраиваемый (по умолчанию F8+F12, как в 86box).
    var guestMouse = false;

    function captureMouse() {
        if (!emu) return;

        try {
            if (emu.lock_mouse) emu.lock_mouse();
            else document.body.requestPointerLock();
        } catch (e) {
            post({ action: "log", text: "захват мыши: " + (e && e.message || e) });
        }
    }

    function releaseMouse() {
        try { document.exitPointerLock(); } catch (e) { /* уже отпущено */ }
    }

    function captureHint() {
        var mode = (config && config.captureRelease) || "f8f12";

        if (mode === "middle") return "middle mouse button";
        if (mode === "esc") return "Esc";
        if (mode === "ctrlalt") return "Ctrl+Alt";

        return "F8+F12";
    }

    function setCaptured(next) {
        captured = next;

        var button = document.querySelector("[data-cmd='capture']");
        if (button) button.classList.toggle("on", next);

        byId("foot-right").textContent = next
            ? "mouse captured · release: " + captureHint()
            : "";
    }

    // Клик по экрану = захват. Это то, чего ждут от консоли: щёлкнул по
    // гостю - мышь «внутри», а не «есть кнопка где-то в панели».
    //
    // Захватываем только если гость действительно включил мышь (драйвер
    // PS/2 в гостевой ОС): иначе события всё равно никуда не идут, и
    // захват выглядел бы как «мышь пропала». Про это честно пишем внизу.
    function bindScreenCapture() {
        var screen = byId("screen_container");
        if (!screen) return;

        screen.addEventListener("mousedown", function (event) {
            if (captured) return;
            if (event.button !== 0) return;
            if (config && config.captureOnClick === false) return;

            if (!guestMouse) {
                byId("foot-right").textContent = "гость не включил мышь: в его ОС нет драйвера мыши";
                return;
            }

            captureMouse();
        }, true);
    }

    // какой способ выхода выбран - решает настройка хоста
    function isReleaseChord(event) {
        var mode = (config && config.captureRelease) || "f8f12";

        if (mode === "esc") return event.key === "Escape";
        if (mode === "ctrlalt") return event.ctrlKey && event.altKey;
        if (mode === "middle") return false;      // обрабатывается на mousedown

        return event.key === "F8" || event.key === "F12";
    }

    document.addEventListener("keydown", function (event) {
        if (!captured) return;
        if (!isReleaseChord(event)) return;

        event.preventDefault();
        releaseMouse();
    }, true);

    document.addEventListener("mousedown", function (event) {
        if (!captured) return;

        var mode = (config && config.captureRelease) || "f8f12";

        if (mode === "middle" && event.button === 1) {
            event.preventDefault();
            releaseMouse();
        }
    }, true);

    document.addEventListener("pointerlockchange", function () {
        setCaptured(!!document.pointerLockElement);
    });

    // --------------------------------------------------------------- утилиты

    // ------------------------------------------------------- egress (fetch)

    // В режиме nat сеть гостя идёт через браузерный fetch, поэтому без этой
    // обёртки гость достаёт localhost хоста и его LAN. Решение принимает C#
    // (`VmHost/EgressGate`): страница только спрашивает и слушается.
    var realFetch = window.fetch ? window.fetch.bind(window) : null;

    function gateDecision(url) {
        try {
            var xhr = new XMLHttpRequest();
            xhr.open("POST", "/egress", false);
            xhr.setRequestHeader("Content-Type", "application/json");
            xhr.send(JSON.stringify({ url: String(url) }));

            return JSON.parse(xhr.responseText || "{}") || {};
        } catch (e) {
            // хост не ответил - закрываемся, а не пропускаем
            return { allow: false, reason: "gate unavailable" };
        }
    }

    function needsGate(url) {
        try {
            var parsed = new URL(String(url), location.href);

            if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return false;

            return parsed.origin !== location.origin;    // свои ресурсы не трогаем
        } catch (e) {
            return false;
        }
    }

    if (realFetch) {
        window.fetch = function (input, init) {
            var url = typeof input === "string" ? input : (input && input.url) || "";

            if (!needsGate(url)) return realFetch(input, init);

            var decision = gateDecision(url);

            if (decision.allow === false) {
                post({ action: "log", text: "egress: запрещено " + url + " (" + (decision.reason || "denied") + ")" });
                return Promise.reject(new TypeError("hv86: egress blocked: " + (decision.reason || "denied")));
            }

            return realFetch(input, init).then(function (response) {
                // редирект мог увести на внутренний адрес: тело уже не отдаём,
                // но сам факт фиксируем в логе
                if (response && response.url && response.url !== url && needsGate(response.url)) {
                    var final = gateDecision(response.url);

                    if (final.allow === false) {
                        post({ action: "log", text: "egress: запрещён редирект на " + response.url });
                        throw new TypeError("hv86: egress blocked after redirect");
                    }
                }

                return response;
            });
        };
    }

    function setState(next, detail) {
        state = next;
        var dot = byId("dot");

        var kind = next === "running" ? "running" :
                   next === "failed" ? "failed" :
                   next === "stopped" ? "stopped" :
                   next === "paused" ? "paused" : "paused";

        dot.className = "dot " + kind;
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

        if (c._filesystem) options.filesystem = c._filesystem;

        return options;
    }

    async function boot(c) {
        config = c;

        hideIdle();

        // свои шрифты (загруженные пользователем) - до применения шрифта
        try { await ensureFontFaces(c.customFonts); } catch (e) { /* не критично */ }

        // шрифт текстового режима: свой у ВМ или хостовый (уже разрешён в C#)
        applyTextFont(c.textFont, c.textSize);

        byId("vm-name").textContent = c.name || c.id || "VM";
        byId("vm-id").textContent = c.id || "";

        setFloppy(c.floppyIndex, c.floppyCount);

        // общая папка web-движка: протокол 9p в JS, файлы - в C# через
        // синхронные POST (handle9p у v86 синхронный)
        if (c.share) {
            try {
                const modules = await Promise.all([
                    import("/engine/fs9p.mjs"),
                    import("/engine/fs-xhr.mjs")
                ]);

                c._filesystem = {
                    handle9p: modules[0].createShareHandler({
                        fs: modules[1].createXhrFs(c.share.endpoint),
                        readonly: !!c.share.readonly,
                        log: function (text) { post({ action: "log", text: "9p: " + text }); }
                    })
                };

                post({ action: "log", text: "9p: общая папка подключена" });
            } catch (e) {
                post({ action: "log", text: "9p: не загрузился — " + (e && e.message || e) });
            }
        }

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

            if (config && config.paused) {
                setState("paused", "replica ready");
                return;
            }

            try { emu.run(); } catch (e) { post({ action: "error", message: String(e) }); }
        });

        emu.add_listener("emulator-started", function () { setState("running", "running"); });
        emu.add_listener("emulator-stopped", function () { setState("paused", "paused"); });

        emu.add_listener("screen-set-size", function (size) {
            post({ action: "log", text: "screen " + size[0] + "x" + size[1] + "x" + size[2] });

            // v86 сам ставит 2x для мелких экранов и transform для текстового
            // режима: снимаем, меряем родной размер, применяем свой масштаб
            requestAnimationFrame(function () {
                var cont = byId("screen_container");
                if (cont) cont.style.zoom = 1;

                normalizeScreen();
                measureNative();
                applyScale();
            });
        });

        emu.add_listener("mouse-enable", function (enabled) {
            guestMouse = !!enabled;
            byId("foot-right").textContent = enabled
                ? (captured ? "mouse captured · release: " + captureHint() : "гость включил мышь: щёлкните по экрану")
                : "";
        });

        // страховка: если ready не пришёл (например, образ грузится долго),
        // всё равно пытаемся запустить через 4 секунды. Реплику не трогаем:
        // она обязана стоять на паузе до повышения.
        setTimeout(function () {
            if (config && config.paused) return;
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

    // Окно консоли можно открыть и для выключенной ВМ (как в Hyper-V):
    // движок присылает "idle", показываем экран "выключена" и кнопку Start
    function showIdle(message) {
        var idle = byId("idle");
        if (idle) idle.classList.remove("hidden");

        if (message) {
            byId("vm-name").textContent = message.name || message.id || "VM";
            byId("vm-id").textContent = message.id || "";
        }

        setState("stopped", "off");
    }

    function hideIdle() {
        var idle = byId("idle");
        if (idle) idle.classList.add("hidden");
    }

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

    // --------------------------------------------------------------- ввод

    // Текст в гость: v86 отправляет настоящие нажатия с паузой (без неё
    // гость теряет символы). "\n" - это Enter.
    async function typeText(message) {
        if (!emu || !message.text) {
            post({ action: "typed", ok: false, error: "эмулятор не создан" });
            return;
        }

        var lines = String(message.text).split("\n");

        try {
            for (var i = 0; i < lines.length; i++) {
                if (i > 0) await emu.keyboard_send_keys([13], 0);
                if (lines[i].length) await emu.keyboard_send_text(lines[i], 6);
            }

            post({ action: "typed", ok: true, chars: message.text.length });
        } catch (e) {
            post({ action: "typed", ok: false, error: String(e && e.message || e) });
            post({ action: "error", message: "type: " + (e && e.message || e) });
        }
    }

    // ------------------------------------------------------------ восстановление

    async function restoreState(message) {
        try {
            await emu.stop();

            setState("restoring", "restoring state");

            const response = await fetch(message.url, { cache: "no-store" });
            if (!response.ok) throw new Error("HTTP " + response.status);

            const buffer = await response.arrayBuffer();
            await emu.restore_state(buffer);

            lastInstructions = emu.get_instruction_counter();
            lastTime = 0;

            setState("paused", "state restored");
            post({ action: "restoreDone", ok: true, size: buffer.byteLength });
        } catch (e) {
            post({ action: "restoreDone", ok: false, error: String(e && e.message || e) });
            post({ action: "error", message: "restore: " + (e && e.message || e) });
            post({ action: "state", state: "failed" });
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
        // Start доступен и без эмулятора: окно может быть открыто для
        // выключенной ВМ, гостя в нём ещё нет
        if (cmd === "start") {
            post({ action: "ctrl", cmd: "start" });
            return;
        }

        // дискету можно сменить и у выключенной ВМ: это про vm.json, не про эмулятор
        if (cmd === "floppy-prev" || cmd === "floppy-next") {
            post({ action: "ctrl", cmd: cmd });
            return;
        }

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

                case "paste":
                    post({ action: "paste" });
                    break;

                case "scale-1":
                    setScaleMode("1");
                    break;

                case "scale-2":
                    setScaleMode("2");
                    break;

                case "scale-fit":
                    setScaleMode("fit");
                    break;

                case "capture":
                    if (captured) releaseMouse();
                    else captureMouse();
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

            case "idle":
                showIdle(message);
                break;

            case "font":
                applyTextFont(message.font, message.size);
                break;

            case "type":
                typeText(message);
                break;

            // диагностика: что сейчас на экране гостя (текстовый режим - как есть)
            case "screenProbe":
            {
                var textLayer = document.querySelector("#screen_container > div");
                var canvas = document.querySelector("#screen_container > canvas");

                post({
                    action: "screenResult",
                    text: textLayer ? (textLayer.textContent || "") : "",
                    graphics: !!(canvas && canvas.style.display !== "none")
                });
                break;
            }

            // диагностика масштаба: применяем режим и отдаём реальный размер экрана
            case "scaleProbe":
            {
                var mode = message.mode || "1";
                setScaleMode(mode);

                requestAnimationFrame(function () {
                    var cont = byId("screen_container");
                    var rect = cont ? cont.getBoundingClientRect() : { width: 0, height: 0 };
                    var layer = document.querySelector("#screen_container > div");
                    var style = layer ? getComputedStyle(layer) : null;

                    post({
                        action: "scaleResult",
                        mode: mode,
                        scale: appliedScale,
                        width: Math.round(rect.width),
                        height: Math.round(rect.height),
                        font: style ? style.fontFamily : "",
                        fontSize: style ? style.fontSize : "",
                        // диагностика текстового слоя: сколько строк, какая высота,
                        // не остались ли от v86 жёсткие width/height (docs/screen.md)
                        rows: layer ? layer.childNodes.length : 0,
                        layerH: layer ? layer.offsetHeight : 0,
                        layerStyleH: layer ? (layer.style.height || "") : "",
                        layerStyleW: layer ? (layer.style.width || "") : "",
                        layerLine: style ? style.lineHeight : "",
                        layerTransform: layer ? (layer.style.transform || "") : "",
                        zoom: cont ? (cont.style.zoom || "") : "",
                        capture: (config && config.captureRelease) || "",
                        floppyCount: floppyCount,
                        floppyIndex: floppyIndex,
                        floppyVisible: !!(byId("floppy-group") && !byId("floppy-group").classList.contains("hidden")),
                        wrap: (function () {
                            var wrap = document.querySelector(".screen-wrap");
                            return wrap ? wrap.clientWidth + "x" + wrap.clientHeight : "";
                        })()
                    });
                });
                break;
            }
            case "fetchProbe":
                fetch(message.url)
                    .then(function (response) {
                        post({
                            action: "fetchResult",
                            ok: true,
                            status: response.status,
                            url: response.url
                        });
                    })
                    .catch(function (e) {
                        post({ action: "fetchResult", ok: false, error: String(e && e.message || e) });
                    });
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

            case "restore":
                restoreState(message);
                break;

            case "xhrTest":
                try {
                    var xhr = new XMLHttpRequest();
                    xhr.open("POST", "/share", false);
                    xhr.setRequestHeader("Content-Type", "application/json");
                    xhr.send(JSON.stringify({ op: "stat", path: "hello.txt" }));

                    post({
                        action: "xhrResult",
                        ok: xhr.status === 200,
                        status: xhr.status,
                        body: String(xhr.responseText || "").slice(0, 160),
                        filesystem: !!(config && config._filesystem)
                    });
                } catch (e) {
                    post({ action: "xhrResult", ok: false, status: 0, body: String(e && e.message || e) });
                }
                break;

            case "attach":
                try {
                    // у выключенной ВМ эмулятора ещё нет - счётчик дискет всё
                    // равно обновляем: набор уже сменён в vm.json
                    if (emu) {
                        var image = { url: message.url, size: message.size };
                        if (message.kind === "fda") emu.set_fda(image);
                        else emu.set_cdrom(image);
                    }
                } catch (e) {
                    post({ action: "error", message: String(e && e.message || e) });
                }

                // смена дискеты набора: обновляем счётчик в панели
                if (message.kind === "fda" && typeof message.count === "number")
                    setFloppy(message.index, message.count);
                break;

            case "detach":
                try {
                    if (!emu) break;
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

    // Ctrl+V - это вставка из буфера хоста, а не Ctrl+V гостя. v86 ловит
    // клавиши на document, поэтому перехватываем раньше него (capture).
    window.addEventListener("keydown", function (e) {
        if (!e.ctrlKey || e.altKey || e.shiftKey) return;
        if (e.code !== "KeyV" && e.keyCode !== 86) return;

        e.preventDefault();
        e.stopPropagation();

        post({ action: "paste" });
    }, true);

    markScale();

    // «Fit» должен пересчитываться при изменении размера окна
    window.addEventListener("resize", function () {
        if (scaleMode === "fit") applyScale();
    });

    var buttons = document.querySelectorAll("[data-cmd]");
    for (var i = 0; i < buttons.length; i++) {
        buttons[i].addEventListener("click", function () {
            command(this.getAttribute("data-cmd"));
        });
    }

    bindScreenCapture();

    post({ action: "ready" });
})();